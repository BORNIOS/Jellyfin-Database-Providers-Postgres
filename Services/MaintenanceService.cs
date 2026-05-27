using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>Table size statistics returned by the maintenance API.</summary>
public sealed record TableStats(
    string TableName,
    long RowCount,
    string TotalSize,
    string TableSize,
    string IndexSize);

/// <summary>
/// Provides PostgreSQL maintenance operations: VACUUM ANALYZE, REINDEX, and table statistics.
/// </summary>
public sealed class MaintenanceService
{
    private readonly ILogger<MaintenanceService> _logger;

    /// <summary>Initializes a new instance of <see cref="MaintenanceService"/>.</summary>
    public MaintenanceService(ILogger<MaintenanceService> logger)
    {
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Connection test
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tests the connection string and returns <see langword="null"/> on success,
    /// or an error message on failure.
    /// </summary>
    public static async Task<string?> TestConnectionAsync(string connectionString, CancellationToken ct = default)
    {
        try
        {
            await using var pg = new NpgsqlConnection(connectionString);
            await pg.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand("SELECT version();", pg);
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return null; // success — version is logged but not returned
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Tests the connection string and returns the PostgreSQL server version string.
    /// Throws if the connection fails.
    /// </summary>
    public static async Task<string> GetServerVersionAsync(string connectionString, CancellationToken ct = default)
    {
        await using var pg = new NpgsqlConnection(connectionString);
        await pg.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT version();", pg);
        return (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))?.ToString() ?? "unknown";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // VACUUM ANALYZE
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs <c>VACUUM ANALYZE</c> on the database. This reclaims dead tuple storage
    /// and refreshes query planner statistics.
    /// Note: must be run outside a transaction block (Npgsql handles this automatically
    /// when <c>CommandTimeout</c> is set to 0 and no explicit transaction is open).
    /// </summary>
    public async Task VacuumAnalyzeAsync(string connectionString, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting VACUUM ANALYZE...");
        await using var pg = new NpgsqlConnection(connectionString);
        await pg.OpenAsync(ct).ConfigureAwait(false);
        // VACUUM cannot run inside a transaction — use command timeout 0 for long-running ops
        await using var cmd = new NpgsqlCommand("VACUUM ANALYZE;", pg) { CommandTimeout = 0 };
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("VACUUM ANALYZE completed.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // REINDEX
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Runs <c>REINDEX DATABASE</c> to rebuild all indexes.</summary>
    public async Task ReindexAsync(string connectionString, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting REINDEX DATABASE...");
        await using var pg = new NpgsqlConnection(connectionString);
        await pg.OpenAsync(ct).ConfigureAwait(false);
        await using var dbNameCmd = new NpgsqlCommand("SELECT current_database();", pg);
        var dbNameObj = await dbNameCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        var dbName = dbNameObj?.ToString();
        if (string.IsNullOrWhiteSpace(dbName))
        {
            throw new InvalidOperationException("Unable to resolve current PostgreSQL database name for REINDEX.");
        }

        var sql = $"REINDEX DATABASE CONCURRENTLY {QuoteIdentifier(dbName)};";
        await using var cmd = new NpgsqlCommand(sql, pg) { CommandTimeout = 0 };
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("REINDEX DATABASE completed.");
    }

    private static string QuoteIdentifier(string input)
        => $"\"{input.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    // ─────────────────────────────────────────────────────────────────────────
    // Table stats
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns size and row-count statistics for all user tables in the given schema.
    /// </summary>
    public static async Task<List<TableStats>> GetTableStatsAsync(
        string connectionString, string schema = "public", CancellationToken ct = default)
    {
        await using var pg = new NpgsqlConnection(connectionString);
        await pg.OpenAsync(ct).ConfigureAwait(false);

        // reltuples is an estimate from pg_class; use it as-is (same as pgAdmin).
        await using var cmd = new NpgsqlCommand(@"
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
            LIMIT 100;", pg);
        cmd.Parameters.AddWithValue("schema", schema);

        var result = new List<TableStats>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
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

    // ─────────────────────────────────────────────────────────────────────────
    // Connection count
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Returns the current number of active connections to the database.</summary>
    public static async Task<int> GetConnectionCountAsync(string connectionString, CancellationToken ct = default)
    {
        await using var pg = new NpgsqlConnection(connectionString);
        await pg.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM pg_stat_activity WHERE datname = current_database();", pg);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Database size
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Returns the total size of the current PostgreSQL database as a human-readable string.</summary>
    public static async Task<string> GetDatabaseSizeAsync(string connectionString, CancellationToken ct = default)
    {
        await using var pg = new NpgsqlConnection(connectionString);
        await pg.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "SELECT pg_size_pretty(pg_database_size(current_database()));", pg);
        return (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))?.ToString() ?? "unknown";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Backup
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a database backup using <c>pg_dump</c> in plain SQL format.
    /// Optionally compresses the resulting SQL file into a ZIP archive.
    /// Returns the final backup file path.
    /// </summary>
    public async Task<string> CreateBackupAsync(
        string connectionString,
        string outputDirectory,
        bool compress,
        string? pgDumpPath,
        CancellationToken ct = default)
    {
        var cs = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(cs.Database))
        {
            throw new InvalidOperationException("Connection string must contain Database.");
        }

        Directory.CreateDirectory(outputDirectory);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var databaseName = cs.Database ?? string.Empty;
        var safeDbName = SanitizeFileName(databaseName);
        var sqlFilePath = Path.Combine(outputDirectory, $"{safeDbName}_{timestamp}.sql");

        var executable = ResolvePgDumpExecutable(pgDumpPath);
        var args = BuildPgDumpArguments(cs, sqlFilePath);

        _logger.LogInformation("Starting PostgreSQL backup with pg_dump to {Path}", sqlFilePath);

        var psi = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = args,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(cs.Password))
        {
            psi.Environment["PGPASSWORD"] = cs.Password;
        }

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start pg_dump process.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        var stdOut = await stdoutTask.ConfigureAwait(false);
        var stdErr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stdErr) ? stdOut : stdErr;
            throw new InvalidOperationException($"pg_dump failed with exit code {process.ExitCode}. {detail}".Trim());
        }

        if (!File.Exists(sqlFilePath))
        {
            throw new InvalidOperationException("Backup completed but SQL output file was not created.");
        }

        if (!compress)
        {
            _logger.LogInformation("PostgreSQL backup completed at {Path}", sqlFilePath);
            return sqlFilePath;
        }

        var zipPath = sqlFilePath + ".zip";
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(sqlFilePath, Path.GetFileName(sqlFilePath), CompressionLevel.Optimal);
        }

        File.Delete(sqlFilePath);
        _logger.LogInformation("PostgreSQL backup completed at {Path}", zipPath);
        return zipPath;
    }

    /// <summary>
    /// Restores a PostgreSQL backup file (.sql or .zip containing .sql) using <c>psql</c>.
    /// Returns the SQL file path used for restoration.
    /// </summary>
    public async Task<string> RestoreBackupAsync(
        string connectionString,
        string backupFilePath,
        string? pgRestorePath,
        bool replaceExistingObjects,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(backupFilePath))
        {
            throw new InvalidOperationException("Backup file path is required.");
        }

        if (!File.Exists(backupFilePath))
        {
            throw new FileNotFoundException("Backup file not found.", backupFilePath);
        }

        var cs = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(cs.Database))
        {
            throw new InvalidOperationException("Connection string must contain Database.");
        }

        string? tempDir = null;
        var sqlFilePath = backupFilePath;

        try
        {
            var ext = Path.GetExtension(backupFilePath);
            if (string.Equals(ext, ".zip", StringComparison.OrdinalIgnoreCase))
            {
                tempDir = Path.Combine(Path.GetTempPath(), "jellyfin-pg-restore-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(tempDir);

                using var archive = ZipFile.OpenRead(backupFilePath);
                ZipArchiveEntry? sqlEntry = null;
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                    {
                        sqlEntry = entry;
                        break;
                    }
                }

                if (sqlEntry is null)
                {
                    throw new InvalidOperationException("ZIP backup does not contain a .sql file.");
                }

                sqlFilePath = Path.Combine(tempDir, Path.GetFileName(sqlEntry.FullName));
                sqlEntry.ExtractToFile(sqlFilePath, overwrite: true);
            }

            if (!File.Exists(sqlFilePath) || !sqlFilePath.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Restore requires a .sql backup file.");
            }

            var executable = ResolvePsqlExecutable(pgRestorePath);
            var args = BuildPsqlRestoreArguments(cs, sqlFilePath, replaceExistingObjects);

            _logger.LogWarning(
                "Starting PostgreSQL restore from {Backup}. ReplaceExistingObjects={ReplaceExistingObjects}",
                backupFilePath,
                replaceExistingObjects);

            var psi = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = args,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (!string.IsNullOrWhiteSpace(cs.Password))
            {
                psi.Environment["PGPASSWORD"] = cs.Password;
            }

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start psql restore process.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdOut = await stdoutTask.ConfigureAwait(false);
            var stdErr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(stdErr) ? stdOut : stdErr;
                throw new InvalidOperationException($"psql restore failed with exit code {process.ExitCode}. {detail}".Trim());
            }

            _logger.LogWarning("PostgreSQL restore completed from {Backup}", backupFilePath);
            return sqlFilePath;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempDir) && Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch
                {
                    // Best-effort cleanup.
                }
            }
        }
    }

    private static string BuildPgDumpArguments(NpgsqlConnectionStringBuilder cs, string outputFile)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(cs.Host))
        {
            sb.Append(" --host ").Append(QuoteArg(cs.Host));
        }

        if (cs.Port > 0)
        {
            sb.Append(" --port ").Append(cs.Port.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(cs.Username))
        {
            sb.Append(" --username ").Append(QuoteArg(cs.Username));
        }

        sb.Append(" --format=plain --no-owner --no-privileges");
        sb.Append(" --file ").Append(QuoteArg(outputFile));
        sb.Append(' ').Append(QuoteArg(cs.Database ?? string.Empty));

        return sb.ToString().Trim();
    }

    private static string ResolvePgDumpExecutable(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        return OperatingSystem.IsWindows() ? "pg_dump.exe" : "pg_dump";
    }

    private static string ResolvePsqlExecutable(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        return OperatingSystem.IsWindows() ? "psql.exe" : "psql";
    }

    private static string SanitizeFileName(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(c, '_');
        }

        return value;
    }

    private static string QuoteArg(string value)
    {
        return '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"';
    }

    private static string BuildPsqlRestoreArguments(
        NpgsqlConnectionStringBuilder cs,
        string sqlFile,
        bool replaceExistingObjects)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(cs.Host))
        {
            sb.Append(" --host ").Append(QuoteArg(cs.Host));
        }

        if (cs.Port > 0)
        {
            sb.Append(" --port ").Append(cs.Port.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(cs.Username))
        {
            sb.Append(" --username ").Append(QuoteArg(cs.Username));
        }

        sb.Append(" --dbname ").Append(QuoteArg(cs.Database ?? string.Empty));
        sb.Append(" --set ON_ERROR_STOP=1");

        if (replaceExistingObjects)
        {
            // Ensure a consistent restore target even if schema objects already exist.
            sb.Append(" --command ").Append(QuoteArg("DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;"));
        }

        sb.Append(" --file ").Append(QuoteArg(sqlFile));

        return sb.ToString().Trim();
    }
}
