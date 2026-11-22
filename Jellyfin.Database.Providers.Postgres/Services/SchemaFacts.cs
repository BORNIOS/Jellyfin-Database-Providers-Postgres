using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// What the database actually contains (tables, columns, indexes) in the schemas on the search path, read from the
/// catalog. <see cref="SchemaDriftGuard"/> decides from these facts, never from its own records, whether a change is
/// in place: a record says what was once applied, not what the database looks like now.
/// </summary>
internal sealed partial class SchemaFacts
{
    // PostgreSQL truncates identifiers to NAMEDATALEN - 1 bytes; Jellyfin has index names longer than that.
    private const int MaxIdentifierBytes = 63;

    // Npgsql's annotation for the INCLUDE columns of an index (NpgsqlAnnotationNames.IndexInclude, internal API).
    private const string IncludeAnnotation = "Npgsql:IndexInclude";

    private SchemaFacts(HashSet<string> tables, HashSet<string> columns, Dictionary<string, IndexFact> indexes)
    {
        Tables = tables;
        Columns = columns;
        Indexes = indexes;
    }

    /// <summary>Gets the table names.</summary>
    internal HashSet<string> Tables { get; }

    /// <summary>Gets the columns, as <c>Table.Column</c>.</summary>
    internal HashSet<string> Columns { get; }

    /// <summary>Gets the indexes other than primary keys, by name.</summary>
    internal Dictionary<string, IndexFact> Indexes { get; }

    /// <summary>Reads the facts through the context's connection.</summary>
    /// <param name="context">Context pointing at the database.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The facts.</returns>
    internal static async Task<SchemaFacts> LoadAsync(DbContext context, CancellationToken cancellationToken)
    {
        var tables = new HashSet<string>(StringComparer.Ordinal);
        var columns = new HashSet<string>(StringComparer.Ordinal);
        var indexes = new Dictionary<string, IndexFact>(StringComparer.Ordinal);

        var connection = context.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened)
        {
            await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT c.relname, a.attname
                    FROM pg_class c
                    JOIN pg_namespace n ON n.oid = c.relnamespace
                    LEFT JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
                    WHERE c.relkind IN ('r', 'p') AND n.nspname = ANY (current_schemas(false));
                    """;
                using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var table = reader.GetString(0);
                    tables.Add(table);
                    if (!await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false))
                    {
                        columns.Add(string.Concat(table, ".", reader.GetString(1)));
                    }
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT i.relname, t.relname, x.indisunique, pg_get_expr(x.indpred, x.indrelid), x.indisvalid,
                           ARRAY(SELECT COALESCE(a.attname, '(expr)')
                                 FROM unnest(x.indkey) WITH ORDINALITY AS k(attnum, ord)
                                 LEFT JOIN pg_attribute a ON a.attrelid = x.indrelid AND a.attnum = k.attnum AND k.attnum > 0
                                 WHERE k.ord <= x.indnkeyatts
                                 ORDER BY k.ord)::text[],
                           ARRAY(SELECT a.attname
                                 FROM unnest(x.indkey) WITH ORDINALITY AS k(attnum, ord)
                                 JOIN pg_attribute a ON a.attrelid = x.indrelid AND a.attnum = k.attnum
                                 WHERE k.ord > x.indnkeyatts
                                 ORDER BY k.ord)::text[]
                    FROM pg_index x
                    JOIN pg_class i ON i.oid = x.indexrelid
                    JOIN pg_class t ON t.oid = x.indrelid
                    JOIN pg_namespace n ON n.oid = t.relnamespace
                    WHERE NOT x.indisprimary AND n.nspname = ANY (current_schemas(false));
                    """;
                using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var predicate = await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(3);
                    var keyColumns = await reader.GetFieldValueAsync<string[]>(5, cancellationToken).ConfigureAwait(false);
                    var included = await reader.GetFieldValueAsync<string[]>(6, cancellationToken).ConfigureAwait(false);
                    indexes[reader.GetString(0)] = new IndexFact(reader.GetString(1), reader.GetBoolean(2), keyColumns, predicate, reader.GetBoolean(4), included);
                }
            }
        }
        finally
        {
            if (opened)
            {
                await context.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }

        return new SchemaFacts(tables, columns, indexes);
    }

    /// <summary>Builds facts directly, for tests.</summary>
    /// <param name="tables">Tables.</param>
    /// <param name="columns">Columns as <c>Table.Column</c>.</param>
    /// <param name="indexes">Indexes by name.</param>
    /// <returns>The facts.</returns>
    internal static SchemaFacts Create(IEnumerable<string> tables, IEnumerable<string> columns, IReadOnlyDictionary<string, IndexFact> indexes)
        => new(
            new HashSet<string>(tables, StringComparer.Ordinal),
            new HashSet<string>(columns, StringComparer.Ordinal),
            new Dictionary<string, IndexFact>(indexes, StringComparer.Ordinal));

    /// <summary>The name PostgreSQL stores for an identifier: truncated to 63 bytes.</summary>
    /// <param name="name">Identifier.</param>
    /// <returns>The stored name.</returns>
    internal static string StoredName(string name)
    {
        if (Encoding.UTF8.GetByteCount(name) <= MaxIdentifierBytes)
        {
            return name;
        }

        var end = name.Length;
        while (Encoding.UTF8.GetByteCount(name.AsSpan(0, end)) > MaxIdentifierBytes)
        {
            end--;
        }

        return name[..end];
    }

    /// <summary>Normalizes an index predicate for comparison: PostgreSQL adds parentheses and casts to what it stores.</summary>
    /// <remarks>String literals are kept as they are: <c>'Movie'</c> and <c>'movie'</c> are different filters.</remarks>
    /// <param name="predicate">Predicate as written in the model or as returned by <c>pg_get_expr</c>.</param>
    /// <returns>The normalized text.</returns>
    internal static string NormalizePredicate(string? predicate)
    {
        if (string.IsNullOrWhiteSpace(predicate))
        {
            return string.Empty;
        }

        var result = new StringBuilder(predicate.Length);
        var position = 0;
        foreach (Match literal in StringLiteral().Matches(predicate))
        {
            result.Append(NormalizeOutsideLiterals(predicate[position..literal.Index])).Append(literal.Value);
            position = literal.Index + literal.Length;
        }

        return result.Append(NormalizeOutsideLiterals(predicate[position..])).ToString();
    }

    /// <summary>
    /// What the catalog shows for the object an operation targets. The guard records it after applying the operation,
    /// so it can tell "applied, and the database still looks as the guard left it" (not worth retrying) from "changed
    /// since", for example by a rollback to an older Jellyfin.
    /// </summary>
    /// <param name="operation">Operation.</param>
    /// <returns>A short description of the object's current state.</returns>
    internal string Fingerprint(MigrationOperation operation)
    {
        var indexName = operation switch
        {
            CreateIndexOperation c => c.Name,
            DropIndexOperation d => d.Name,
            _ => null,
        };
        if (indexName is not null)
        {
            return TryGetIndex(indexName, out var index)
                ? string.Join('|', index.Table, index.IsUnique, string.Join(',', index.Columns), string.Join(',', index.Included ?? []), NormalizePredicate(index.Predicate), index.IsValid)
                : "-";
        }

        return operation switch
        {
            CreateTableOperation t => Tables.Contains(t.Name) ? "+" : "-",
            AddColumnOperation a => Columns.Contains(string.Concat(a.Table, ".", a.Name)) ? "+" : "-",
            _ => string.Empty,
        };
    }

    /// <summary>Finds an index by its model name.</summary>
    /// <param name="name">Index name as in the model or a migration.</param>
    /// <param name="index">The index, if present.</param>
    /// <returns>Whether it exists.</returns>
    internal bool TryGetIndex(string name, out IndexFact index)
        => Indexes.TryGetValue(StoredName(name), out index!);

    /// <summary>Whether the database has an index with exactly this definition.</summary>
    /// <param name="operation">Index as the model defines it.</param>
    /// <returns><see langword="true"/> when name, table, uniqueness, columns and filter match and the index is valid.</returns>
    internal bool Matches(CreateIndexOperation operation)
        => TryGetIndex(operation.Name, out var index)
            && index.IsValid
            && string.Equals(index.Table, operation.Table, StringComparison.Ordinal)
            && index.IsUnique == operation.IsUnique
            && index.Columns.SequenceEqual(operation.Columns, StringComparer.Ordinal)
            && (index.Included ?? []).SequenceEqual(operation[IncludeAnnotation] as string[] ?? [], StringComparer.Ordinal)
            && string.Equals(NormalizePredicate(index.Predicate), NormalizePredicate(operation.Filter), StringComparison.Ordinal);

    private static string NormalizeOutsideLiterals(string text)
        => Noise().Replace(Cast().Replace(text, string.Empty), string.Empty).ToUpperInvariant();

    // Casts PostgreSQL adds when it stores an expression: 'x'::text, 0::bigint, NULL::character varying.
    [GeneratedRegex("""::(?:"[^"]+"|[a-z_][a-z0-9_]*(?: [a-z_][a-z0-9_]*)*)(?:\[\])?""", RegexOptions.CultureInvariant)]
    private static partial Regex Cast();

    [GeneratedRegex(@"[\s()]+", RegexOptions.CultureInvariant)]
    private static partial Regex Noise();

    [GeneratedRegex("'(?:[^']|'')*'", RegexOptions.CultureInvariant)]
    private static partial Regex StringLiteral();

    /// <summary>An index as the catalog describes it.</summary>
    /// <param name="Table">Table it belongs to.</param>
    /// <param name="IsUnique">Whether it is unique.</param>
    /// <param name="Columns">Key columns in order; an expression shows as <c>(expr)</c>, which no model column matches.</param>
    /// <param name="Predicate">Partial index predicate, or <see langword="null"/>.</param>
    /// <param name="IsValid">
    /// <see langword="false"/> for an index the planner ignores: what a failed or interrupted
    /// <c>CREATE INDEX CONCURRENTLY</c> leaves behind.
    /// </param>
    /// <param name="Included">Non-key <c>INCLUDE</c> columns, or <see langword="null"/> for none.</param>
    internal sealed record IndexFact(string Table, bool IsUnique, IReadOnlyList<string> Columns, string? Predicate, bool IsValid = true, IReadOnlyList<string>? Included = null);
}
