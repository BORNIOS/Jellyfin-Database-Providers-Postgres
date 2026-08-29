using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Providers.Postgres.Logging;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// EF Core command interceptor that writes every database-level error to the plugin
/// log file so operators do not need to dig through Jellyfin''s main log to find
/// PostgreSQL problems (e.g. 23505 duplicate key, 26000 missing prepared statement,
/// XX001 page corruption, slow queries).
/// </summary>
/// <remarks>
/// Only error-level events are logged; successful commands are left untouched so
/// that the plugin log stays readable and not flooded with routine query noise.
/// </remarks>
public sealed class DbErrorLoggingInterceptor : DbCommandInterceptor
{
    // Severity thresholds: only log when the Npgsql/EF Core exception represents
    // an actual database error, not a normal "no rows" result.
    private static readonly string[] SilentSqlStates =
    [
        "02000", // no_data
        "02001", // no_additional_dynamic_result_sets_returned
    ];

    /// <inheritdoc />
    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
        => LogCommandError(command, eventData.Exception);

    /// <inheritdoc />
    public override Task CommandFailedAsync(
        DbCommand command,
        CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        LogCommandError(command, eventData.Exception);
        return Task.CompletedTask;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void LogCommandError(DbCommand command, Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        // Skip benign non-error states.
        if (exception is PostgresException pgEx)
        {
            if (SilentSqlStates.Any(state => string.Equals(pgEx.SqlState, state, StringComparison.Ordinal)))
            {
                return;
            }

            var severity = pgEx.Severity?.ToUpperInvariant();
            var label = severity switch
            {
                "FATAL" or "PANIC" => "FATAL",
                "ERROR" => "ERROR",
                _ => "WARN ",
            };

            // Truncate the SQL to 200 chars so the log line stays readable.
            var sqlPreview = Truncate(command.CommandText, 200);
            PostgresLog.Write(
                label,
                $"[DB] {pgEx.SqlState} {pgEx.MessageText} | SQL: {sqlPreview}");
        }
        else
        {
            var sqlPreview = Truncate(command.CommandText, 200);
            PostgresLog.Error(
                $"[DB] Error de base de datos: {exception.GetType().Name}: {exception.Message} | SQL: {sqlPreview}");
        }
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength
            ? value
            : string.Concat(value.AsSpan(0, maxLength), "\u2026");
    }
}
