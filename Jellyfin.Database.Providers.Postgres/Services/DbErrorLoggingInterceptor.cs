using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Text;
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
    // Longest SQL preview written for a failed command (EF Core statements are long, and the head of
    // the statement is what identifies it).
    private const int MaxSqlPreviewLength = 500;

    // Longest value written for a single command parameter.
    private const int MaxParameterLength = 64;

    private const int MaxReportedStates = 200;

    // Severity thresholds: only log when the Npgsql/EF Core exception represents
    // an actual database error, not a normal "no rows" result.
    private static readonly string[] SilentSqlStates =
    [
        "02000", // no_data
        "02001", // no_additional_dynamic_result_sets_returned
    ];

    // Failures whose stack trace has already been written, so a query that breaks on every request
    // reports the trace once and a single line afterwards.
    private static readonly HashSet<string> ReportedStates = new(StringComparer.Ordinal);

    private static readonly object ReportLock = new();

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
            var isError = severity is "ERROR" or "FATAL" or "PANIC";
            var level = isError ? "ERROR" : "WARN ";
            var message = BuildMessage(pgEx, command);

            // The first occurrence carries the full detail (stack trace plus SqlState, table,
            // constraint, hint); later occurrences of the same failure stay on one line.
            if (ShouldReportFullDetail(pgEx))
            {
                if (isError)
                {
                    PostgresLog.Error(message, pgEx);
                }
                else
                {
                    PostgresLog.Warn(message, pgEx);
                }
            }
            else
            {
                PostgresLog.Write(level, string.Concat(message, " | traza completa ya reportada"));
            }
        }
        else
        {
            PostgresLog.Error(BuildMessage(exception, command), exception);
        }
    }

    /// <summary>
    /// Builds the context line of a failed command: where it came from, the statement and, when the
    /// administrator enabled it, the parameter values that actually caused the failure.
    /// </summary>
    /// <param name="exception">The failure (PostgreSQL server error or handled exception).</param>
    /// <param name="command">The command that failed.</param>
    /// <returns>The message to log.</returns>
    private static string BuildMessage(Exception exception, DbCommand command)
    {
        var text = new StringBuilder("[DB] Comando fallido: ");
        if (exception is PostgresException postgres)
        {
            text.Append(postgres.SqlState).Append(' ').Append(postgres.MessageText);
            if (!string.IsNullOrWhiteSpace(postgres.ConstraintName))
            {
                text.Append(" | constraint=").Append(postgres.ConstraintName);
            }

            if (!string.IsNullOrWhiteSpace(postgres.TableName))
            {
                text.Append(" | tabla=").Append(postgres.TableName);
            }
        }

        text.Append(" | SQL: ").Append(Truncate(command.CommandText, MaxSqlPreviewLength));

        // Parameter values contain user data, so they are only written when the administrator turns
        // the option on; Jellyfin's own sensitive-data logging stays untouched.
        if (PostgresPlugin.Instance?.Configuration?.LogCommandParameters == true && command.Parameters.Count > 0)
        {
            text.Append(" | params: ");
            for (var i = 0; i < command.Parameters.Count; i++)
            {
                var parameter = command.Parameters[i];
                if (i > 0)
                {
                    text.Append(", ");
                }

                text.Append(parameter.ParameterName)
                    .Append('=')
                    .Append(Truncate(FormatValue(parameter.Value), MaxParameterLength));
            }
        }

        return text.ToString();
    }

    private static bool ShouldReportFullDetail(PostgresException exception)
    {
        var key = string.Concat(exception.SqlState, "|", exception.ConstraintName, "|", exception.MessageText);
        lock (ReportLock)
        {
            if (!ReportedStates.Add(key))
            {
                return false;
            }

            if (ReportedStates.Count > MaxReportedStates)
            {
                ReportedStates.Clear();
            }
        }

        return true;
    }

    private static string FormatValue(object? value)
        => value switch
        {
            null or DBNull => "NULL",
            byte[] bytes => string.Create(
                CultureInfo.InvariantCulture,
                $"byte[{bytes.Length}]"),
            Array array => string.Create(
                CultureInfo.InvariantCulture,
                $"array[{array.Length}]"),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };

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
