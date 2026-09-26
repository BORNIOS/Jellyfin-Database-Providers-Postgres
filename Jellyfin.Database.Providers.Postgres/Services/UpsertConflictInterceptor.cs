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

    private static readonly string[] UserDataKeyColumns = ["ItemId", "UserId", "CustomDataKey"];

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

    // A library scan can remove an item after Jellyfin checked it exists and before it persists the
    // item's dependent rows.  PostgreSQL correctly rejects the child INSERT in that narrow window;
    // SQLite's old write path effectively treated it as a no-op.  Keep that no-op limited to the
    // dependent tables whose parent key is ItemId.  A newly inserted BaseItem is already present by
    // the time EF sends these statements, so normal saves are unaffected.
    private static readonly string[] BaseItemDependentTables =
    [
        "BaseItemImageInfos",
        "BaseItemProviders",
        "BaseItemTrailerTypes",
        "BaseItemMetadataFields",
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

        if (!sql.Contains("INSERT INTO", StringComparison.OrdinalIgnoreCase)
            && !sql.Contains("UPDATE \"BaseItems\"", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!TargetTables.Any(t => sql.Contains(t, StringComparison.Ordinal))
            && !sql.Contains("\"UserData\"", StringComparison.Ordinal)
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
        // A Person detail request can trigger an on-demand metadata refresh. Jellyfin first observes an
        // existing BaseItem, then later attaches it as Modified. A parallel library refresh may delete the
        // row in between; EF treats the resulting zero-row UPDATE as a concurrency error and returns 500.
        // The mapped entity contains the complete BaseItems row, so PostgreSQL's atomic upsert preserves
        // normal updates and recreates only that vanished item. This is intentionally limited to the exact
        // parameter-only UPDATE shape emitted by ItemPersistenceService.
        var rewritten = BaseItemUpdateRegex().Replace(sql, static match =>
        {
            var assignments = BaseItemAssignmentRegex().Matches(match.Groups["set"].Value);
            if (assignments.Count == 0)
            {
                return match.Value;
            }

            var columns = assignments.Select(static assignment => assignment.Groups["column"].Value).ToArray();
            var values = assignments.Select(static assignment => assignment.Groups["value"].Value).ToArray();
            // Only ItemPersistenceService's full entity update carries Type. EF also emits smaller updates
            // elsewhere; those cannot safely become INSERTs because required BaseItems columns are absent.
            if (!columns.Contains("Type", StringComparer.Ordinal))
            {
                return match.Value;
            }

            var id = match.Groups["id"].Value;

            return string.Concat(
                "INSERT INTO \"BaseItems\" (\"Id\", ",
                string.Join(", ", columns.Select(static column => $"\"{column}\"")),
                ") VALUES (",
                id,
                ", ",
                string.Join(", ", values),
                ") ON CONFLICT (\"Id\") DO UPDATE SET ",
                match.Groups["set"].Value.Trim(),
                ";");
        });

        rewritten = UserDataInsertRegex().Replace(rewritten, static match =>
        {
            var columns = ColumnNameRegex().Matches(match.Groups["columns"].Value)
                .Select(static column => column.Groups[1].Value)
                .ToArray();
            var updates = columns
                .Where(column => !UserDataKeyColumns.Contains(column, StringComparer.Ordinal))
                .Select(static column => $"\"{column}\" = EXCLUDED.\"{column}\"")
                .ToArray();

            // UserData always has non-key state columns. Keep the original statement untouched if an
            // unexpected SQL shape contains only the composite key.
            if (updates.Length == 0)
            {
                return match.Value;
            }

            var statement = match.Value.TrimEnd();
            var suffix = string.Concat(
                "\nON CONFLICT (\"ItemId\", \"UserId\", \"CustomDataKey\") DO UPDATE SET ",
                string.Join(", ", updates));
            return statement.EndsWith(';') ? statement[..^1] + suffix + ";" : statement + suffix;
        });

        rewritten = InsertStatementRegex().Replace(rewritten, static match =>
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

        // Do this after conflict rewriting so the generated INSERT ... SELECT retains the duplicate
        // protection above. Each matched value is an EF parameter; the table and column names are
        // fixed literals selected from BaseItemDependentTables.
        rewritten = BaseItemDependentInsertRegex().Replace(rewritten, static match =>
        {
            var table = match.Groups["table"].Value;
            if (!BaseItemDependentTables.Contains(table, StringComparer.Ordinal))
            {
                return match.Value;
            }

            var columns = ColumnNameRegex().Matches(match.Groups["columns"].Value)
                .Select(static column => column.Groups[1].Value)
                .ToArray();
            var itemIdIndex = Array.FindIndex(columns, static column => string.Equals(column, "ItemId", StringComparison.Ordinal));
            var values = match.Groups["values"].Value.Split(',', StringSplitOptions.TrimEntries);

            if (itemIdIndex < 0 || itemIdIndex >= values.Length || !values[itemIdIndex].StartsWith('@'))
            {
                return match.Value;
            }

            var suffix = match.Groups["conflict"].Success ? "\nON CONFLICT DO NOTHING" : string.Empty;
            return string.Concat(
                "INSERT INTO \"",
                table,
                "\" (",
                match.Groups["columns"].Value,
                ")\nSELECT ",
                match.Groups["values"].Value,
                " WHERE EXISTS (SELECT 1 FROM \"BaseItems\" WHERE \"Id\" = ",
                values[itemIdIndex],
                ")",
                suffix,
                ";");
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

    /// <summary>Matches the normal single-row INSERT shape EF Core emits for UserData.</summary>
    [GeneratedRegex(
        @"INSERT\s+INTO\s+""UserData""\s*\((?<columns>[^)]*)\)\s*VALUES\s*\([^;]*\)\s*;",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UserDataInsertRegex();

    /// <summary>Extracts a quoted identifier from an EF Core-generated column list.</summary>
    [GeneratedRegex(@"""([^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex ColumnNameRegex();

    /// <summary>
    /// Matches the single row INSERT EF Core generates for <c>ItemValuesMap</c>, capturing the column order
    /// and the two parameters so the statement can be turned into a conditional insert.
    /// </summary>
    [GeneratedRegex(
        @"INSERT\s+INTO\s+""ItemValuesMap""\s*\(\s*""(?<first>ItemValueId|ItemId)""\s*,\s*""(?:ItemValueId|ItemId)""\s*\)\s*VALUES\s*\(\s*(?<p1>@[A-Za-z0-9_]+)\s*,\s*(?<p2>@[A-Za-z0-9_]+)\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ItemValuesMapInsertRegex();

    /// <summary>
    /// Matches a conflict-normalised single-row child INSERT. The values are intentionally captured as
    /// EF parameters rather than parsed as SQL expressions.
    /// </summary>
    [GeneratedRegex(
        @"INSERT\s+INTO\s+""(?<table>[^""]+)""\s*\((?<columns>[^)]*)\)\s*VALUES\s*\((?<values>[^;]*)\)\s*(?<conflict>ON\s+CONFLICT\s+DO\s+NOTHING)?\s*;",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BaseItemDependentInsertRegex();

    /// <summary>
    /// Matches the full-column, parameter-only update that <c>ItemPersistenceService</c> emits for a
    /// tracked <c>BaseItems</c> entity. More complex UPDATE statements, including code migrations, are
    /// deliberately left untouched.
    /// </summary>
    [GeneratedRegex(
        @"UPDATE\s+""BaseItems""\s+SET\s+(?<set>(?:""[A-Za-z0-9_]+""\s*=\s*@[A-Za-z0-9_]+\s*,?\s*)+)WHERE\s+""Id""\s*=\s*(?<id>@[A-Za-z0-9_]+)\s*;",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BaseItemUpdateRegex();

    /// <summary>Extracts a parameter-only assignment from Jellyfin's generated BaseItems update.</summary>
    [GeneratedRegex(
        @"""(?<column>[A-Za-z0-9_]+)""\s*=\s*(?<value>@[A-Za-z0-9_]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex BaseItemAssignmentRegex();
}
