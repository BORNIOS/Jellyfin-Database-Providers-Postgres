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
    // All junction / mapping tables where Jellyfin may attempt a duplicate INSERT
    // during a library refresh or plugin-triggered collection update.
    private static readonly string[] TargetTables =
    [
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

        if (!TargetTables.Any(t => sql.Contains(t, StringComparison.Ordinal)))
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
        command.CommandText = InsertStatementRegex().Replace(sql, static match =>
        {
            var tableName = match.Groups[1].Value;

            if (!TargetTables.Any(t => string.Equals(tableName, t, StringComparison.Ordinal)))
            {
                return match.Value;
            }

            // Already has ON CONFLICT — don't double-append.
            if (match.Value.Contains("ON CONFLICT", StringComparison.OrdinalIgnoreCase))
            {
                return match.Value;
            }

            var stmt = match.Value.TrimEnd();
            return stmt.EndsWith(';')
                ? stmt[..^1] + "\nON CONFLICT DO NOTHING;"
                : stmt + "\nON CONFLICT DO NOTHING";
        });
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
}
