using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Providers.Postgres.Services.Models;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// PostgreSQL maintenance: connection tests, VACUUM, REINDEX, stats queries.
/// Backup/restore operations are in <see cref="MaintenanceBackupService"/>.
/// </summary>
public sealed class MaintenanceService
{
    private readonly ILogger<MaintenanceService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MaintenanceService"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public MaintenanceService(ILogger<MaintenanceService> logger) => _logger = logger;

    // ── Connection helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Tests the connection string. Returns <see langword="null"/> on success or an error message.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string to test.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="null"/> on success, or an error message string on failure.</returns>
    public static async Task<string?> TestConnectionAsync(string connectionString, CancellationToken ct = default)
    {
        try
        {
            using var pg = new NpgsqlConnection(connectionString);
            await pg.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = new NpgsqlCommand("SELECT version();", pg);
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Returns the PostgreSQL server version string.</summary>
    /// <inheritdoc cref="TestConnectionAsync"/>
    public static async Task<string> GetServerVersionAsync(string connectionString, CancellationToken ct = default)
    {
        using var pg = new NpgsqlConnection(connectionString);
        await pg.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = new NpgsqlCommand("SELECT version();", pg);
        return (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))?.ToString() ?? "unknown";
    }

    // ── VACUUM ANALYZE ────────────────────────────────────────────────────────

    /// <summary>Runs <c>VACUUM ANALYZE</c> on the database.</summary>
    /// <inheritdoc cref="TestConnectionAsync"/>
    public async Task VacuumAnalyzeAsync(string connectionString, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting VACUUM ANALYZE...");
        using var pg = new NpgsqlConnection(connectionString);
        await pg.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = new NpgsqlCommand("VACUUM ANALYZE;", pg) { CommandTimeout = 0 };
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("VACUUM ANALYZE completed.");
    }

    // ── REINDEX ───────────────────────────────────────────────────────────────

    /// <summary>Runs <c>REINDEX DATABASE CONCURRENTLY</c>.</summary>
    /// <inheritdoc cref="TestConnectionAsync"/>
    public async Task ReindexAsync(string connectionString, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting REINDEX DATABASE...");
        using var pg = new NpgsqlConnection(connectionString);
        await pg.OpenAsync(ct).ConfigureAwait(false);

        string dbName;
        using (var dbNameCmd = new NpgsqlCommand("SELECT current_database();", pg))
        {
            dbName = (await dbNameCmd.ExecuteScalarAsync(ct).ConfigureAwait(false))?.ToString() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(dbName))
        {
            throw new InvalidOperationException("Unable to resolve current PostgreSQL database name for REINDEX.");
        }

        // dbName comes from SELECT current_database() — server-controlled, not user input.
        // QuoteIdentifier escapes any internal double-quotes for safety.
        using var cmd = CreateReindexCommand(pg, dbName);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("REINDEX DATABASE completed.");
    }

    // ── Table statistics ──────────────────────────────────────────────────────

    /// <summary>Returns size and row-count statistics for all user tables in the given schema.</summary>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    /// <param name="schema">Schema to query. Defaults to <c>public</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of per-table size and row-count statistics.</returns>
    public static async Task<List<TableStats>> GetTableStatsAsync(
        string connectionString, string schema = "public", CancellationToken ct = default)
    {
        const string tableStatsSql = @"
            SELECT
                c.relname                                   AS table_name,
                c.reltuples::bigint                         AS row_count,
                pg_size_pretty(pg_total_relation_size(c.oid)) AS total_size,
                pg_size_pretty(pg_relation_size(c.oid))       AS table_size,
                pg_size_pretty(
                    pg_total_relation_size(c.oid)
                    - pg_relation_size(c.oid))              AS index_size
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_stat_user_tables t ON t.relname = c.relname AND t.schemaname = n.nspname
            WHERE n.nspname = @schema
              AND c.relkind = 'r'
            ORDER BY pg_total_relation_size(c.oid) DESC
            LIMIT 100;";

        var result = new List<TableStats>();
        using var pg = new NpgsqlConnection(connectionString);
        await pg.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = new NpgsqlCommand(tableStatsSql, pg);
        cmd.Parameters.AddWithValue("schema", schema);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new TableStats(
                TableName: reader.GetString(0),
                RowCount: reader.GetInt64(1),
                TotalSize: reader.GetString(2),
                TableSize: reader.GetString(3),
                IndexSize: reader.GetString(4)));
        }

        return result;
    }

    /// <summary>Returns the current number of active connections to the database.</summary>
    /// <inheritdoc cref="TestConnectionAsync"/>
    public static async Task<int> GetConnectionCountAsync(string connectionString, CancellationToken ct = default)
    {
        const string sql = "SELECT COUNT(*) FROM pg_stat_activity WHERE datname = current_database();";
        using var pg = new NpgsqlConnection(connectionString);
        await pg.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = new NpgsqlCommand(sql, pg);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    /// <summary>Returns the total size of the current PostgreSQL database as a human-readable string.</summary>
    /// <inheritdoc cref="TestConnectionAsync"/>
    public static async Task<string> GetDatabaseSizeAsync(string connectionString, CancellationToken ct = default)
    {
        const string sql = "SELECT pg_size_pretty(pg_database_size(current_database()));";
        using var pg = new NpgsqlConnection(connectionString);
        await pg.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = new NpgsqlCommand(sql, pg);
        return (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))?.ToString() ?? "unknown";
    }

    // ── GIN index status ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns the names and sizes of all GIN indexes in the public schema
    /// created by this plugin (names ending in <c>_gin_trgm</c>).
    /// </summary>
    /// <inheritdoc cref="TestConnectionAsync"/>
    public static async Task<List<GinIndexInfo>> GetGinIndexStatusAsync(
        string connectionString, CancellationToken ct = default)
    {
        const string ginSql = @"
            SELECT
                c.relname                                       AS index_name,
                t.relname                                       AS table_name,
                pg_size_pretty(pg_relation_size(c.oid))        AS index_size,
                ix.indisvalid                                   AS is_valid
            FROM pg_class c
            JOIN pg_index ix ON ix.indexrelid = c.oid
            JOIN pg_class t  ON t.oid = ix.indrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public'
              AND c.relkind = 'i'
              AND c.relname LIKE '%_gin_trgm'
            ORDER BY t.relname, c.relname;";

        var result = new List<GinIndexInfo>();
        using var pg = new NpgsqlConnection(connectionString);
        await pg.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = new NpgsqlCommand(ginSql, pg);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new GinIndexInfo(
                IndexName: reader.GetString(0),
                TableName: reader.GetString(1),
                IndexSize: reader.GetString(2),
                IsValid: reader.GetBoolean(3)));
        }

        return result;
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    internal static string QuoteIdentifier(string input)
        => $"\"{input.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    internal static string ValidateBackupFilePath(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException(
                $"Backup file path must be an absolute path. Received: {path}");
        }

        return Path.GetFullPath(path);
    }

    internal static string SanitizeFileName(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(c, '_');
        }

        return value;
    }

    // ── Backup / Restore (delegates to MaintenanceBackupService) ─────────────

    /// <summary>
    /// Creates a database backup using <c>pg_dump</c>.
    /// Delegates to <see cref="MaintenanceBackupService.CreateBackupAsync"/>.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    /// <param name="outputDirectory">Directory where the backup file will be written.</param>
    /// <param name="compress">When <see langword="true"/>, wraps the SQL dump in a ZIP file.</param>
    /// <param name="pgDumpPath">Optional path to the <c>pg_dump</c> executable.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Absolute path to the created backup file.</returns>
    public Task<string> CreateBackupAsync(
        string connectionString,
        string outputDirectory,
        bool compress,
        string? pgDumpPath,
        CancellationToken ct = default)
    {
        return new MaintenanceBackupService(_logger).CreateBackupAsync(connectionString, outputDirectory, compress, pgDumpPath, ct);
    }

    /// <summary>
    /// Restores a PostgreSQL backup using <c>psql</c>.
    /// Delegates to <see cref="MaintenanceBackupService.RestoreBackupAsync"/>.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    /// <param name="backupFilePath">Path to the <c>.sql</c> or <c>.zip</c> backup file.</param>
    /// <param name="pgRestorePath">Optional path to the <c>psql</c> executable.</param>
    /// <param name="replaceExistingObjects">When <see langword="true"/>, uses <c>--single-transaction</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Absolute path to the SQL file used for restoration.</returns>
    public Task<string> RestoreBackupAsync(
        string connectionString,
        string backupFilePath,
        string? pgRestorePath,
        bool replaceExistingObjects,
        CancellationToken ct = default)
    {
        return new MaintenanceBackupService(_logger).RestoreBackupAsync(connectionString, backupFilePath, pgRestorePath, replaceExistingObjects, ct);
    }

    // ── Command factory (CA2100) ──────────────────────────────────────────────

    // dbName comes from SELECT current_database() — server-controlled, not user input.
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "dbName comes from SELECT current_database(), server-controlled. QuoteIdentifier prevents injection.")]
    private static NpgsqlCommand CreateReindexCommand(NpgsqlConnection pg, string dbName)
    {
        var sql = string.Concat("REINDEX DATABASE CONCURRENTLY ", QuoteIdentifier(dbName), ";");
        return new NpgsqlCommand(sql, pg) { CommandTimeout = 0 };
    }
}
