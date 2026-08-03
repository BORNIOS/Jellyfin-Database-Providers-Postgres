using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>Snapshot of an in-progress PostgreSQL → SQLite export.</summary>
public sealed record ExportToSqliteProgress(
    bool IsRunning,
    bool IsCompleted,
    bool HasError,
    string? ErrorMessage,
    int PercentComplete,
    string CurrentTable,
    long ExportedRows,
    long TotalRows,
    string? TargetSqlitePath,
    IReadOnlyList<string> LogLines);

/// <summary>
/// Exports all Jellyfin data from PostgreSQL directly into a SQLite <c>.db</c> file.
/// The target file can be an existing Jellyfin SQLite database or a new empty file;
/// in both cases tables are auto-created from the PostgreSQL schema when missing.
/// </summary>
public sealed class ExportToSqliteService : IDisposable
{
    private const int CommitEveryRows = 10_000;
    private const int MaxLogLines = 500;

    private static readonly HashSet<string> SkipTables = new(StringComparer.OrdinalIgnoreCase)
        { "__EFMigrationsHistory", "__EFMigrationsLock" };

    private readonly ILogger<ExportToSqliteService> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private volatile bool _isRunning;
    private volatile bool _isCompleted;
    private volatile bool _hasError;
    private string? _errorMessage;
    private int _percentComplete;
    private string _currentTable = string.Empty;
    private long _exportedRows;
    private long _totalRows;
    private string? _targetSqlitePath;
    private readonly List<string> _logBuffer = new(MaxLogLines);
    private readonly object _logLock = new();

    /// <summary>Initializes a new instance of <see cref="ExportToSqliteService"/>.</summary>
    public ExportToSqliteService(ILogger<ExportToSqliteService> logger) => _logger = logger;

    /// <inheritdoc />
    public void Dispose() => _semaphore.Dispose();

    /// <summary>Returns the conventional SQLite database path for the given Jellyfin data directory.</summary>
    public static string DetectDefaultSqlitePath(string dataPath)
        => Path.Combine(dataPath, "jellyfin.db");

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Gets a live snapshot of the export progress.</summary>
    public ExportToSqliteProgress GetProgress()
    {
        lock (_logLock)
        {
            return new ExportToSqliteProgress(
                IsRunning: _isRunning,
                IsCompleted: _isCompleted,
                HasError: _hasError,
                ErrorMessage: _errorMessage,
                PercentComplete: _percentComplete,
                CurrentTable: _currentTable,
                ExportedRows: _exportedRows,
                TotalRows: _totalRows,
                TargetSqlitePath: _targetSqlitePath,
                LogLines: _logBuffer.ToList());
        }
    }

    /// <summary>
    /// Starts the export in a background task.
    /// Returns <see langword="false"/> if an export is already running.
    /// </summary>
    public bool StartExport(string pgConnStr, string sqliteDbPath)
    {
        if (!_semaphore.Wait(0)) return false;

        _isRunning = true;
        _isCompleted = false;
        _hasError = false;
        _errorMessage = null;
        _percentComplete = 0;
        _currentTable = string.Empty;
        _exportedRows = 0;
        _totalRows = 0;
        _targetSqlitePath = sqliteDbPath;

        lock (_logLock) { _logBuffer.Clear(); }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunExportAsync(pgConnStr, sqliteDbPath, CancellationToken.None).ConfigureAwait(false);
                _isCompleted = true;
            }
            catch (Exception ex)
            {
                _hasError = true;
                _errorMessage = ex.Message;
                Log($"[FATAL] {ex.Message}");
                _logger.LogError(ex, "PostgreSQL → SQLite export failed.");
            }
            finally
            {
                _isRunning = false;
                _semaphore.Release();
            }
        });

        return true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Core export logic
    // ─────────────────────────────────────────────────────────────────────────

    private async Task RunExportAsync(string pgConnStr, string sqliteDbPath, CancellationToken ct)
    {
        Log($"Iniciando exportación PostgreSQL → SQLite");
        Log($"Destino: {sqliteDbPath}");

        var dir = Path.GetDirectoryName(sqliteDbPath);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

        // Non-pooled PG connection — read-only, no timeout
        var pgBuilder = new NpgsqlConnectionStringBuilder(pgConnStr)
        {
            MaxAutoPrepare = 0,
            Pooling = false,
            CommandTimeout = 0,
        };

        await using var pgConn = new NpgsqlConnection(pgBuilder.ConnectionString);
        await pgConn.OpenAsync(ct).ConfigureAwait(false);

        var tables = await GetUserTablesAsync(pgConn, ct).ConfigureAwait(false);
        tables = tables.Where(t => !SkipTables.Contains(t)).ToList();
        Log($"Tablas a exportar: {tables.Count}");

        _totalRows = await EstimateTotalRowsAsync(pgConn, tables, ct).ConfigureAwait(false);
        Log($"Filas estimadas: {_totalRows:N0}");
        _percentComplete = 2;

        await using var sqliteConn = new SqliteConnection($"Data Source={sqliteDbPath}");
        await sqliteConn.OpenAsync(ct).ConfigureAwait(false);

        // Disable FK checks so tables can be populated in any order
        await ExecSqliteAsync(sqliteConn, "PRAGMA foreign_keys = OFF;", ct).ConfigureAwait(false);
        await ExecSqliteAsync(sqliteConn, "PRAGMA journal_mode = WAL;", ct).ConfigureAwait(false);
        await ExecSqliteAsync(sqliteConn, "PRAGMA synchronous = NORMAL;", ct).ConfigureAwait(false);

        for (var i = 0; i < tables.Count; i++)
        {
            var table = tables[i];
            _currentTable = table;
            _percentComplete = 2 + (int)((i / (double)tables.Count) * 95);
            await ExportTableAsync(pgConn, sqliteConn, table, ct).ConfigureAwait(false);
        }

        await ExecSqliteAsync(sqliteConn, "PRAGMA foreign_keys = ON;", ct).ConfigureAwait(false);
        await ExecSqliteAsync(sqliteConn, "ANALYZE;", ct).ConfigureAwait(false);

        _percentComplete = 100;
        var fileSize = new FileInfo(sqliteDbPath).Length;
        Log($"Exportación completada. {_exportedRows:N0} filas escritas.");
        Log($"Archivo: {sqliteDbPath} ({FormatBytes(fileSize)})");
    }

    private async Task ExportTableAsync(
        NpgsqlConnection pgConn, SqliteConnection sqliteConn, string table, CancellationToken ct)
    {
        var schema = await GetColumnSchemaAsync(pgConn, table, ct).ConfigureAwait(false);
        if (schema.Count == 0) return;

        // Ensure table exists in SQLite (no-op if file was created by Jellyfin)
        await EnsureTableExistsAsync(pgConn, sqliteConn, table, schema, ct).ConfigureAwait(false);

        // Count exact rows (fast for small tables, acceptable for large ones)
        long rowCount = 0;
        await using (var countCmd = new NpgsqlCommand($"""SELECT COUNT(*) FROM "{table}";""", pgConn))
        {
            rowCount = Convert.ToInt64(
                await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }

        if (rowCount == 0) return;
        Log($"  → {table} ({rowCount:N0} filas)");

        // Wipe existing data before importing
        await ExecSqliteAsync(sqliteConn, $"""DELETE FROM "{table}";""", ct).ConfigureAwait(false);

        var quotedCols = string.Join(", ", schema.Select(c => $"\"{c.Name}\""));
        var paramNames = string.Join(", ", schema.Select((_, i) => $"@p{i}"));
        var insertSql  = $"""INSERT INTO "{table}" ({quotedCols}) VALUES ({paramNames});""";

        await using var pgCmd = new NpgsqlCommand(
            $"""SELECT {quotedCols} FROM "{table}";""", pgConn) { CommandTimeout = 0 };
        await using var reader = await pgCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        await using var insertCmd = sqliteConn.CreateCommand();
        insertCmd.CommandText = insertSql;

        var parameters = schema.Select((_, i) =>
        {
            var p = insertCmd.CreateParameter();
            p.ParameterName = $"@p{i}";
            insertCmd.Parameters.Add(p);
            return p;
        }).ToList();

        await using var tx = await sqliteConn.BeginTransactionAsync(ct).ConfigureAwait(false);
        insertCmd.Transaction = (SqliteTransaction)tx;
        long batchCount = 0;

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            for (var col = 0; col < schema.Count; col++)
            {
                parameters[col].Value = ConvertForSqlite(reader.GetValue(col));
            }

            await insertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            batchCount++;
            _exportedRows++;

            if (batchCount >= CommitEveryRows)
            {
                await tx.CommitAsync(ct).ConfigureAwait(false);
                // Start new transaction for next batch
                await using var newTx = await sqliteConn.BeginTransactionAsync(ct).ConfigureAwait(false);
                insertCmd.Transaction = (SqliteTransaction)newTx;
                batchCount = 0;
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Schema helpers
    // ─────────────────────────────────────────────────────────────────────────

    private sealed record ColumnInfo(string Name, string PgDataType, string PgUdtName);

    private static async Task<List<ColumnInfo>> GetColumnSchemaAsync(
        NpgsqlConnection conn, string table, CancellationToken ct)
    {
        var cols = new List<ColumnInfo>();
        await using var cmd = new NpgsqlCommand(@"
            SELECT column_name, data_type, udt_name
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @t
            ORDER BY ordinal_position;", conn);
        cmd.Parameters.AddWithValue("t", table);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            cols.Add(new ColumnInfo(r.GetString(0), r.GetString(1), r.GetString(2)));
        }

        return cols;
    }

    private static async Task<List<string>> GetPrimaryKeyColumnsAsync(
        NpgsqlConnection conn, string table, CancellationToken ct)
    {
        var pks = new List<string>();
        await using var cmd = new NpgsqlCommand(@"
            SELECT kcu.column_name
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
              ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema
            WHERE tc.table_schema = 'public' AND tc.table_name = @t AND tc.constraint_type = 'PRIMARY KEY'
            ORDER BY kcu.ordinal_position;", conn);
        cmd.Parameters.AddWithValue("t", table);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) pks.Add(r.GetString(0));
        return pks;
    }

    private static async Task EnsureTableExistsAsync(
        NpgsqlConnection pgConn, SqliteConnection sqliteConn,
        string table, List<ColumnInfo> schema, CancellationToken ct)
    {
        // Check if the table already exists in SQLite
        await using var checkCmd = sqliteConn.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@t;";
        checkCmd.Parameters.AddWithValue("@t", table);
        var exists = Convert.ToInt64(await checkCmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0;
        if (exists) return;

        var pks = await GetPrimaryKeyColumnsAsync(pgConn, table, ct).ConfigureAwait(false);
        var pkSet = new HashSet<string>(pks, StringComparer.OrdinalIgnoreCase);

        var colDefs = schema.Select(c =>
        {
            var affinity = MapToSqliteAffinity(c.PgDataType, c.PgUdtName);
            var pk = pkSet.Contains(c.Name) ? " PRIMARY KEY" : string.Empty;
            return $"""    "{c.Name}" {affinity}{pk}""";
        });

        var ddl = $"""CREATE TABLE IF NOT EXISTS "{table}" ({Environment.NewLine}{string.Join($",{Environment.NewLine}", colDefs)}{Environment.NewLine});""";
        await ExecSqliteAsync(sqliteConn, ddl, ct).ConfigureAwait(false);
    }

    private static string MapToSqliteAffinity(string pgDataType, string pgUdtName)
        => pgDataType.ToLowerInvariant() switch
        {
            "boolean" => "INTEGER",
            "integer" or "bigint" or "smallint" => "INTEGER",
            "real" or "double precision" or "numeric" or "decimal" => "REAL",
            "bytea" => "BLOB",
            "array" => "TEXT",  // serialized as JSON
            _ => "TEXT",        // uuid, text, varchar, timestamp, etc.
        };

    // ─────────────────────────────────────────────────────────────────────────
    // PostgreSQL helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<List<string>> GetUserTablesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var tables = new List<string>();
        await using var cmd = new NpgsqlCommand(
            "SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename;", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) tables.Add(r.GetString(0));
        return tables;
    }

    private static async Task<long> EstimateTotalRowsAsync(
        NpgsqlConnection conn, List<string> tables, CancellationToken ct)
    {
        long total = 0;
        await using var cmd = new NpgsqlCommand(
            "SELECT reltuples::bigint FROM pg_class WHERE relname = ANY(@tables) AND relkind = 'r';", conn);
        cmd.Parameters.AddWithValue("tables", tables.ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) total += Math.Max(0, r.GetInt64(0));
        return total;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Value conversion
    // ─────────────────────────────────────────────────────────────────────────

    private static object ConvertForSqlite(object? value) => value switch
    {
        null or DBNull => DBNull.Value,
        bool b         => b ? 1 : 0,
        Guid g         => g.ToString(),
        DateTime dt    => dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        float f when float.IsNaN(f) || float.IsInfinity(f)   => DBNull.Value,
        double d when double.IsNaN(d) || double.IsInfinity(d) => DBNull.Value,
        long[]  arr => JsonSerializer.Serialize(arr),
        int[]   arr => JsonSerializer.Serialize(arr),
        float[] arr => JsonSerializer.Serialize(arr),
        _ => value,
    };

    // ─────────────────────────────────────────────────────────────────────────
    // SQLite utility
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task ExecSqliteAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Logging
    // ─────────────────────────────────────────────────────────────────────────

    private void Log(string message)
    {
        if (!string.IsNullOrEmpty(message)) _logger.LogInformation("{ExportMessage}", message);
        lock (_logLock)
        {
            if (_logBuffer.Count >= MaxLogLines) _logBuffer.RemoveAt(0);
            _logBuffer.Add($"[{DateTime.UtcNow:HH:mm:ss}] {message}");
        }
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1_073_741_824 ? $"{bytes / 1_073_741_824.0:F1} GB" :
        bytes >= 1_048_576     ? $"{bytes / 1_048_576.0:F1} MB"     :
        bytes >= 1_024         ? $"{bytes / 1_024.0:F1} KB"          :
        $"{bytes} B";
}
