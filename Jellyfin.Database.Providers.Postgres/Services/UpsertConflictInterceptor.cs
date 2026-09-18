using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// EF Core command interceptor that rewrites plain <c>INSERT</c> statements on tables
/// with known composite-primary-key constraints into
/// <c>INSERT … ON CONFLICT DO NOTHING</c>.
/// </summary>
/// <remarks>
/// <para>
/// Jellyfin's <c>BaseItemRepository.UpdateOrInsertItems</c> generates <c>INSERT</c>
/// statements for junction tables such as <c>BaseItemProviders</c> without checking
/// for pre-existing rows first. In SQLite this silently succeeded (implicit REPLACE
/// semantics), but PostgreSQL enforces PK uniqueness strictly and raises error 23505,
/// which propagates as an unhandled exception that crashes the server when triggered
/// from a plugin timer (e.g. TMDbBoxSets <c>AddMoviesToCollection</c>).
/// </para>
/// <para>
/// The rewrite is idempotent: if the row already exists the insert is skipped,
/// preserving the same effective behaviour as the original SQLite implementation.
/// </para>
/// </remarks>
public sealed partial class UpsertConflictInterceptor : DbCommandInterceptor
{
    // ItemValues is rewritten with an explicit conflict target: the pair that has to stay unique is
    // (Type, Value), while ItemValueId is a fresh random guid on every attempt, so a plain
    // ON CONFLICT DO NOTHING would never fire.
    private const string ItemValuesTable = "\"ItemValues\"";

    private const string ItemValueIdColumn = "ItemValueId";

    // All junction / mapping tables where Jellyfin may attempt a duplicate INSERT
    // during a library refresh or plugin-triggered collection update.
    //
    // BaseItems is here for the same reason and it is what breaks channel items and image conversions:
    // Jellyfin cree que el item es nuevo (no esta en su cache) cuando la fila ya existe, y el INSERT
    // duplicado hacia fallar todo el lote con 23505. Ignorarlo deja la fila que ya estaba, y las claves
    // foraneas de las tablas hijas siguen apuntando a ella.
    //
    // ItemValues is rewritten too. Ignoring a duplicate value row on its own would leave ItemValuesMap
    // pointing at a value that was never inserted, so the mapping insert is made conditional in the same
    // pass (see ItemValuesMapInsertRegex) and only runs when the value really exists.
    private static readonly string[] TargetTables =
    [
        "\"BaseItems\"",
        "\"BaseItemProviders\"",
        "\"BaseItemImageInfos\"",
        "\"AncestorIds\"",
        "\"ItemValuesMap\"",
        "\"PeopleBaseItemMap\"",
        "\"BaseItemTrailerTypes\"",
        "\"BaseItemMetadataFields\"",
    ];

    /// <inheritdoc />
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        RewriteCommand(command);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        RewriteCommand(command);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        RewriteCommand(command);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        RewriteCommand(command);
        return ValueTask.FromResult(result);
    }

    // ── Rewrite logic ─────────────────────────────────────────────────────────

    private static void RewriteCommand(DbCommand command)
    {
        var sql = command.CommandText;

        if (!sql.Contains("INSERT INTO", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!TargetTables.Any(t => sql.Contains(t, StringComparison.Ordinal))
            && !sql.Contains(ItemValuesTable, StringComparison.Ordinal))
        {
            return;
        }

        // Delegate the actual CommandText assignment to a separate method so that
        // the CA3001 suppression is scoped only to where the assignment occurs.
        ApplyRewrite(command, sql);
    }

    // CA2100 / CA3001: CommandText is built by EF Core's internal model rewriter.
    // The Regex only appends the literal string "ON CONFLICT DO NOTHING" to statements
    // that EF Core itself generated — no user-supplied HTTP input ever reaches this path.
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Rewritten text appends a hardcoded literal to EF Core-generated SQL. No user input reaches this path.")]
    [SuppressMessage("Security", "CA3001:Review code for SQL injection vulnerabilities", Justification = "Rewritten text appends a hardcoded literal to EF Core-generated SQL. No user input reaches this path.")]
    private static void ApplyRewrite(DbCommand command, string sql)
    {
        var rewritten = InsertStatementRegex().Replace(sql, static match =>
        {
            var tableName = match.Groups[1].Value;
            var isItemValue = string.Equals(tableName, ItemValuesTable, StringComparison.Ordinal);

            if (!isItemValue && !TargetTables.Any(t => string.Equals(tableName, t, StringComparison.Ordinal)))
            {
                return match.Value;
            }

            // Already has ON CONFLICT — don't double-append.
            if (match.Value.Contains("ON CONFLICT", StringComparison.OrdinalIgnoreCase))
            {
                return match.Value;
            }

            // ItemValues needs the column pair named: the fresh random ItemValueId never collides, so a
            // clause without a target would never fire and the duplicate (Type, Value) would still fail.
            var clause = isItemValue
                ? "\nON CONFLICT (\"Type\", \"Value\") DO NOTHING"
                : "\nON CONFLICT DO NOTHING";

            var stmt = match.Value.TrimEnd();
            return stmt.EndsWith(';')
                ? stmt[..^1] + clause + ";"
                : stmt + clause;
        });

        // Second pass, only for batches that insert values and their mapping together: if the value insert
        // was skipped because another connection stored the same pair first, the mapping must be skipped as
        // well instead of violating FK_ItemValuesMap_ItemValues_ItemValueId and taking the whole batch —
        // items included — down with it.
        if (rewritten.Contains(ItemValuesTable, StringComparison.Ordinal))
        {
            rewritten = ItemValuesMapInsertRegex().Replace(rewritten, static match =>
            {
                var first = match.Groups["first"].Value;
                var itemId = string.Equals(first, ItemValueIdColumn, StringComparison.Ordinal)
                    ? match.Groups["p2"].Value
                    : match.Groups["p1"].Value;
                var itemValueId = string.Equals(first, ItemValueIdColumn, StringComparison.Ordinal)
                    ? match.Groups["p1"].Value
                    : match.Groups["p2"].Value;

                return string.Concat(
                    "INSERT INTO \"ItemValuesMap\" (\"ItemId\", \"ItemValueId\")\nSELECT ",
                    itemId,
                    ", ",
                    itemValueId,
                    " WHERE EXISTS (SELECT 1 FROM \"ItemValues\" WHERE \"ItemValueId\" = ",
                    itemValueId,
                    ")");
            });
        }

        command.CommandText = rewritten;
    }

    /// <summary>
    /// Matches a single INSERT statement targeting a quoted table name,
    /// including its VALUES clause and trailing semicolon.
    /// Group 1 captures the quoted table name.
    /// </summary>
    [GeneratedRegex(
        @"INSERT\s+INTO\s+(""[^""]+"")[\s\S]*?VALUES\s*\([^;]*\)\s*;",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InsertStatementRegex();

    /// <summary>
    /// Matches the single row INSERT EF Core generates for <c>ItemValuesMap</c>, capturing the column order
    /// and the two parameters so the statement can be turned into a conditional insert.
    /// </summary>
    [GeneratedRegex(
        @"INSERT\s+INTO\s+""ItemValuesMap""\s*\(\s*""(?<first>ItemValueId|ItemId)""\s*,\s*""(?:ItemValueId|ItemId)""\s*\)\s*VALUES\s*\(\s*(?<p1>@[A-Za-z0-9_]+)\s*,\s*(?<p2>@[A-Za-z0-9_]+)\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ItemValuesMapInsertRegex();
}
