using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Providers.Postgres.Logging;
using Npgsql;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// Runs ad-hoc read-only statements against the configured database, for the cases where an administrator
/// has to inspect or operate on PostgreSQL directly.
/// </summary>
/// <remarks>
/// The console is deliberately restricted: only statements that cannot modify data or structure are
/// accepted, a single statement per request, a bounded number of rows and a short command timeout.
/// Everything that is executed is written to the plugin log, so a change made from the console can always
/// be traced back.
/// </remarks>
public sealed class QueryConsoleService
{
    /// <summary>Hard ceiling for the requested row limit.</summary>
    internal const int MaxRowsLimit = 5000;

    /// <summary>Statements are expected to be fast; a long one is almost always a mistake.</summary>
    internal const int DefaultTimeoutSeconds = 30;

    // Only read-only entry points are allowed.
    private static readonly string[] AllowedStarters = ["SELECT", "WITH", "TABLE", "VALUES", "EXPLAIN", "SHOW"];

    // Statements that would modify data, structure or server state. Checked as whole words.
    private static readonly string[] ForbiddenKeywords =
    [
        "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "DROP", "ALTER", "CREATE", "GRANT", "REVOKE",
        "COPY", "VACUUM", "ANALYZE", "REINDEX", "CLUSTER", "REFRESH", "CALL", "DO", "SET", "RESET", "LOCK",
        "COMMIT", "ROLLBACK", "BEGIN", "SAVEPOINT", "DISCARD", "LISTEN", "NOTIFY", "IMPORT", "REASSIGN",
        "CHECKPOINT", "ATTACH", "DETACH", "PREPARE", "EXECUTE", "DEALLOCATE", "INTO", "SECURITY", "REPLACE",
    ];

    /// <summary>
    /// Validates a statement and returns it ready to be executed.
    /// </summary>
    /// <param name="sql">Statement typed by the administrator.</param>
    /// <returns>The normalised statement.</returns>
    /// <exception cref="ArgumentException">The statement is empty, not read-only or contains several statements.</exception>
    internal static string Validate(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("La consulta está vacía.", nameof(sql));
        }

        // Two views of the statement: the one that will be executed (comments removed, literals and quoted
        // identifiers untouched) and the one used to check keywords (literals and identifiers emptied, so a
        // keyword inside a literal is not mistaken for a statement).
        var (cleaned, masked) = SplitCommentsAndLiterals(sql);
        var statement = cleaned.Trim();
        if (statement.Length == 0)
        {
            throw new ArgumentException("La consulta no contiene ninguna sentencia.", nameof(sql));
        }

        var analysis = masked.Trim();

        // One statement per request: a trailing semicolon is fine, an inner one is not.
        var withoutTrailingSemicolon = analysis.TrimEnd(';');
        if (withoutTrailingSemicolon.Contains(';', StringComparison.Ordinal))
        {
            throw new ArgumentException("Solo se permite una sentencia por consulta.", nameof(sql));
        }

        var candidate = withoutTrailingSemicolon.TrimStart('(').TrimStart();
        var starter = FirstWord(candidate);
        if (!AllowedStarters.Contains(starter, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Solo se permiten consultas de lectura ({string.Join(", ", AllowedStarters)}). Se recibió '{starter}'."),
                nameof(sql));
        }

        foreach (var keyword in ForbiddenKeywords)
        {
            if (ContainsKeyword(candidate, keyword))
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"La consulta contiene '{keyword}', que está bloqueado en la consola."),
                    nameof(sql));
            }
        }

        return statement;
    }

    /// <summary>
    /// Executes a previously validated statement and returns at most <paramref name="maxRows"/> rows.
    /// </summary>
    /// <param name="connectionString">Connection string of the active database.</param>
    /// <param name="sql">Statement to execute.</param>
    /// <param name="schema">Schema to expose as search path, so tables can be queried by name.</param>
    /// <param name="maxRows">Maximum number of rows to return.</param>
    /// <param name="explain">Whether to return the execution plan instead of the rows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result set.</returns>
    /// <exception cref="ArgumentException">The statement was rejected by <see cref="Validate(string)"/>.</exception>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Statement comes from the administrator using the in-dashboard console; it is restricted to read-only single statements by Validate before reaching this call, and the console requires the elevation policy.")]
    internal async Task<QueryResult> ExecuteAsync(
        string connectionString,
        string sql,
        string schema,
        int maxRows,
        bool explain,
        CancellationToken cancellationToken)
    {
        var statement = Validate(sql);
        var limit = Math.Clamp(maxRows, 1, MaxRowsLimit);

        if (explain)
        {
            // Without ANALYZE the statement is planned, never executed.
            statement = string.Concat("EXPLAIN (VERBOSE, COSTS) ", statement);
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            // Short timeout: a console query that takes longer than this is a mistake, not a job.
            CommandTimeout = DefaultTimeoutSeconds,
            SearchPath = string.Concat("\"", schema.Replace("\"", "\"\"", StringComparison.Ordinal), "\""),
        };

        var stopwatch = Stopwatch.StartNew();
        var columns = new List<string>();
        var rows = new List<string?[]>();
        var truncated = false;

        // Synchronous disposal, as elsewhere in the plugin: the pooled Npgsql objects are cheap to dispose
        // and this keeps CA2007 (ConfigureAwait on every awaited operation) satisfied.
        using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new NpgsqlCommand(statement, connection);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        for (var field = 0; field < reader.FieldCount; field++)
        {
            columns.Add(reader.GetName(field));
        }

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (rows.Count >= limit)
            {
                truncated = true;
                break;
            }

            var row = new string?[reader.FieldCount];
            for (var field = 0; field < reader.FieldCount; field++)
            {
                row[field] = await reader.IsDBNullAsync(field, cancellationToken).ConfigureAwait(false)
                    ? null
                    : FormatValue(reader.GetValue(field));
            }

            rows.Add(row);
        }

        stopwatch.Stop();
        var durationMs = stopwatch.ElapsedMilliseconds;

        // Audit trail: whoever looks at the plugin log can see what was executed from the console.
        PostgresLog.Info(
            string.Create(
                CultureInfo.InvariantCulture,
                $"[QueryConsole] {(explain ? "PLAN" : "SELECT")} {rows.Count} fila(s) en {durationMs} ms{(truncated ? " (truncado)" : string.Empty)}: {Preview(statement)}"));

        return new QueryResult(columns, rows, rows.Count, truncated, durationMs);
    }

    /// <summary>
    /// Splits a statement in two: the cleaned text that is actually executed (comments removed) and a masked
    /// copy where literals and quoted identifiers are emptied, used to look for forbidden keywords without
    /// matching the contents of a string.
    /// </summary>
    /// <param name="sql">Statement to clean.</param>
    /// <returns>The text to execute and the text to analyse.</returns>
    private static (string Cleaned, string Masked) SplitCommentsAndLiterals(string sql)
    {
        var cleaned = new StringBuilder(sql.Length);
        var masked = new StringBuilder(sql.Length);
        var index = 0;

        while (index < sql.Length)
        {
            var current = sql[index];

            if (current == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                while (index < sql.Length && sql[index] != '\n')
                {
                    index++;
                }

                cleaned.Append('\n');
                masked.Append('\n');
                continue;
            }

            if (current == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < sql.Length && !(sql[index] == '*' && sql[index + 1] == '/'))
                {
                    index++;
                }

                index = Math.Min(index + 2, sql.Length);
                cleaned.Append(' ');
                masked.Append(' ');
                continue;
            }

            if (current is '\'' or '"')
            {
                var start = index;
                var quote = current;
                index++;
                while (index < sql.Length)
                {
                    if (sql[index] == quote)
                    {
                        // A doubled quote is an escaped quote inside the literal.
                        if (index + 1 < sql.Length && sql[index + 1] == quote)
                        {
                            index += 2;
                            continue;
                        }

                        index++;
                        break;
                    }

                    index++;
                }

                cleaned.Append(sql, start, index - start);
                masked.Append(quote).Append(quote);
                continue;
            }

            cleaned.Append(current);
            masked.Append(current);
            index++;
        }

        return (cleaned.ToString(), masked.ToString());
    }

    private static string FirstWord(string sql)
    {
        var end = 0;
        while (end < sql.Length && (char.IsLetter(sql[end]) || sql[end] == '_'))
        {
            end++;
        }

        return sql[..end].ToUpperInvariant();
    }

    private static string FormatValue(object value)
        => value switch
        {
            byte[] bytes => string.Create(CultureInfo.InvariantCulture, $"byte[{bytes.Length}]"),
            DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            DateTimeOffset offset => offset.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture),
            Array array => string.Create(CultureInfo.InvariantCulture, $"[{array.Length} elementos]"),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };

    private static string Preview(string sql)
        => sql.Length <= 400 ? sql : string.Concat(sql.AsSpan(0, 400), "…");

    /// <summary>
    /// Looks for a whole word in a statement. Written by hand instead of with a regular expression so the
    /// check does not depend on culture or on compiling a pattern per keyword.
    /// </summary>
    /// <param name="sql">Statement to inspect.</param>
    /// <param name="keyword">Word to look for.</param>
    /// <returns><see langword="true"/> when the word appears on its own.</returns>
    private static bool ContainsKeyword(string sql, string keyword)
    {
        var index = 0;
        while (index < sql.Length)
        {
            if (!IsWordCharacter(sql[index]))
            {
                index++;
                continue;
            }

            var start = index;
            while (index < sql.Length && IsWordCharacter(sql[index]))
            {
                index++;
            }

            if (string.Equals(sql[start..index], keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWordCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';

    /// <summary>Result of an ad-hoc statement.</summary>
    /// <param name="Columns">Column names of the result set.</param>
    /// <param name="Rows">Rows, as text values (null for SQL NULL).</param>
    /// <param name="RowCount">Number of returned rows.</param>
    /// <param name="Truncated">Whether more rows were available than the requested limit.</param>
    /// <param name="DurationMs">Server round trip time in milliseconds.</param>
    internal sealed record QueryResult(IReadOnlyList<string> Columns, IReadOnlyList<string?[]> Rows, int RowCount, bool Truncated, long DurationMs);
}
