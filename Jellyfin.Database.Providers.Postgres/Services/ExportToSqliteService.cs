using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Providers.Postgres.Logging;
using Jellyfin.Database.Providers.Postgres.Services.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// Exports all Jellyfin data from PostgreSQL directly into a SQLite <c>.db</c> file.
/// </summary>
public sealed class ExportToSqliteService : IDisposable
{
    // ── Constants / static fields ─────────────────────────────────────────────
    private const int CommitEveryRows = 10_000;
    private const int MaxLogLines = 500;

    // SQLite primary result code for constraint violations (SQLITE_CONSTRAINT).
    private const int SqliteConstraintErrorCode = 19;

    private static readonly HashSet<string> SkipTables = new(StringComparer.OrdinalIgnoreCase)
        { "__EFMigrationsHistory", "__EFMigrationsLock" };

    // Jellyfin's native SQLite stores GUIDs in UPPERCASE. PostgreSQL uses lowercase.
    // This pattern detects UUID strings so we can normalise them on export.
    private static readonly System.Text.RegularExpressions.Regex GuidPattern =
        new System.Text.RegularExpressions.Regex(
            @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // ── Instance fields ───────────────────────────────────────────────────────
    private readonly ILogger<ExportToSqliteService> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly List<string> _logBuffer = new(MaxLogLines);
    private readonly object _logLock = new();

    private volatile bool _isRunning;
    private volatile bool _isCompleted;
    private volatile bool _hasError;
    private string? _errorMessage;
    private int _percentComplete;
    private string _currentTable = string.Empty;
    private long _exportedRows;
    private long _totalRows;
    private string? _targetSqlitePath;

    // ── Constructor ───────────────────────────────────────────────────────────

    /// <summary>
    /// Initializes a new instance of the <see cref="ExportToSqliteService"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public ExportToSqliteService(ILogger<ExportToSqliteService> logger) => _logger = logger;

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void Dispose() => _semaphore.Dispose();

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Returns the conventional SQLite database path for the given Jellyfin data directory.</summary>
    /// <param name="dataPath">Jellyfin data directory path.</param>
    /// <returns>Absolute path to the default <c>jellyfin.db</c> file.</returns>
    public static string DetectDefaultSqlitePath(string dataPath)
        => Path.Combine(dataPath, "jellyfin.db");

    /// <summary>Gets a live snapshot of the export progress.</summary>
    /// <returns>Current <see cref="ExportToSqliteProgress"/> snapshot.</returns>
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
    /// <param name="pgConnStr">PostgreSQL connection string to export from.</param>
    /// <param name="sqliteDbPath">Absolute path to the target SQLite file.</param>
    /// <returns><see langword="true"/> if the export started; <see langword="false"/> if one is already running.</returns>
    public bool StartExport(string pgConnStr, string sqliteDbPath)
    {
        if (!_semaphore.Wait(0))
        {
            return false;
        }

        _isRunning = true;
        _isCompleted = false;
        _hasError = false;
        _errorMessage = null;
        _percentComplete = 0;
        _currentTable = string.Empty;
        _exportedRows = 0;
        _totalRows = 0;
        _targetSqlitePath = sqliteDbPath;

        lock (_logLock)
        {
            _logBuffer.Clear();
        }

        PostgresLog.Info($"[Export] INICIO: PostgreSQL → {sqliteDbPath}");

        _ = Task.Run(async () =>
        {
            try
            {
                await RunExportAsync(pgConnStr, sqliteDbPath, CancellationToken.None).ConfigureAwait(false);
                _isCompleted = true;
                PostgresLog.Info($"[Export] COMPLETADA: {_exportedRows:N0} filas exportadas → {sqliteDbPath}");
            }
            catch (Exception ex)
            {
                _hasError = true;
                _errorMessage = ex.Message;
                Log($"[FATAL] {ex.Message}");
                _logger.LogError(ex, "PostgreSQL → SQLite export failed.");
                PostgresLog.Error("[Export] FALLIDA", ex);
            }
            finally
            {
                _isRunning = false;
                _semaphore.Release();
            }
        });

        return true;
    }

    // ── Core export logic ─────────────────────────────────────────────────────

    private async Task RunExportAsync(string pgConnStr, string sqliteDbPath, CancellationToken ct)
    {
        Log($"Iniciando exportación PostgreSQL → SQLite");
        Log($"Destino: {sqliteDbPath}");

        var dir = Path.GetDirectoryName(sqliteDbPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var pgBuilder = new NpgsqlConnectionStringBuilder(pgConnStr)
        {
            MaxAutoPrepare = 0,
            Pooling = false,
            CommandTimeout = 0,
        };

        using var pgConn = new NpgsqlConnection(pgBuilder.ConnectionString);
        await pgConn.OpenAsync(ct).ConfigureAwait(false);

        var tables = await ExportHelpers.GetUserTablesAsync(pgConn, ct).ConfigureAwait(false);
        tables = tables.Where(t => !SkipTables.Contains(t)).ToList();
        Log($"Tablas a exportar: {tables.Count}");
        PostgresLog.Info($"[Export] Tablas a exportar: {tables.Count}");

        _totalRows = await ExportHelpers.EstimateTotalRowsAsync(pgConn, tables, ct).ConfigureAwait(false);
        Log($"Filas estimadas: {_totalRows:N0}");
        PostgresLog.Info($"[Export] Filas estimadas: {_totalRows:N0}");
        _percentComplete = 2;

        // File existence and schema are guaranteed by the controller before StartExport is called.
        using var sqliteConn = new SqliteConnection($"Data Source={sqliteDbPath}");
        await sqliteConn.OpenAsync(ct).ConfigureAwait(false);

        await ExportHelpers.ExecSqliteAsync(sqliteConn, "PRAGMA foreign_keys = OFF;", ct).ConfigureAwait(false);
        await ExportHelpers.ExecSqliteAsync(sqliteConn, "PRAGMA journal_mode = WAL;", ct).ConfigureAwait(false);
        await ExportHelpers.ExecSqliteAsync(sqliteConn, "PRAGMA synchronous = NORMAL;", ct).ConfigureAwait(false);

        for (var i = 0; i < tables.Count; i++)
        {
            var table = tables[i];
            _currentTable = table;
            _percentComplete = 2 + (int)((i / (double)tables.Count) * 95);
            await ExportTableAsync(pgConn, sqliteConn, table, ct).ConfigureAwait(false);
        }

        await ExportHelpers.ExecSqliteAsync(sqliteConn, "PRAGMA foreign_keys = ON;", ct).ConfigureAwait(false);
        await ExportHelpers.ExecSqliteAsync(sqliteConn, "ANALYZE;", ct).ConfigureAwait(false);

        // Ensure library.db exists as a valid empty SQLite file.
        // Jellyfin requires it to back up before running legacy code migrations
        // (e.g. RemoveDuplicateExtras). Without it the migration service throws.
        EnsureEmptyLibraryDb(sqliteDbPath);

        // Remove orphaned rows that reference non-existent Users, to prevent
        // FOREIGN KEY constraint failures when Jellyfin operates in SQLite mode.
        await CleanOrphanedForeignKeysAsync(sqliteConn, ct).ConfigureAwait(false);

        await CopyCodeMigrationsAsync(pgConn, sqliteConn, ct).ConfigureAwait(false);

        _percentComplete = 100;
        var fileSize = new FileInfo(sqliteDbPath).Length;
        Log($"Exportación completada. {_exportedRows:N0} filas escritas.");
        Log($"Archivo: {sqliteDbPath} ({FormatBytes(fileSize)})");
    }

    private async Task ExportTableAsync(
        NpgsqlConnection pgConn, SqliteConnection sqliteConn, string table, CancellationToken ct)
    {
        // Only export columns that exist in BOTH PostgreSQL and the target SQLite.
        // This preserves the original SQLite schema (with all FK constraints) intact.
        var pgSchema = await ExportHelpers.GetColumnSchemaAsync(pgConn, table, ct).ConfigureAwait(false);
        if (pgSchema.Count == 0)
        {
            return;
        }

        // Get columns actually present in the SQLite table (if it exists)
        var sqliteColumns = await ExportHelpers.GetSqliteTableColumnsAsync(sqliteConn, table, ct).ConfigureAwait(false);

        List<ColumnInfo> schema;
        if (sqliteColumns.Count == 0)
        {
            // Table does not exist in SQLite — skip it to preserve referential integrity.
            Log($"  [SKIP] {table}: no existe en SQLite destino, omitida.");
            return;
        }
        else
        {
            // Use only columns present in both PG and SQLite
            var sqliteColSet = new HashSet<string>(sqliteColumns, StringComparer.OrdinalIgnoreCase);
            schema = pgSchema.Where(c => sqliteColSet.Contains(c.Name)).ToList();
        }

        // Primary-key columns are copied verbatim: text key columns are case-sensitive
        // in both engines, so normalising their case would merge distinct rows.
        var keyColumns = await ExportHelpers.GetPrimaryKeyColumnsAsync(pgConn, table, ct).ConfigureAwait(false);
        var isKeyColumn = schema
            .Select(c => keyColumns.Contains(c.Name, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        // table comes from pg_tables (system catalog), not user input (CA2100).
        long rowCount;
        using var countCmd = CreateCountCommand(pgConn, table);
        rowCount = Convert.ToInt64(
            await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false),
            CultureInfo.InvariantCulture);

        if (rowCount == 0)
        {
            return;
        }

        Log($"  → {table} ({rowCount:N0} filas)");
        PostgresLog.Info($"[Export]   → {table} ({rowCount:N0} filas)");

        await ExportHelpers.ExecSqliteAsync(sqliteConn, $"""DELETE FROM "{table}";""", ct).ConfigureAwait(false);

        var quotedCols = string.Join(", ", schema.Select(c => $"\"{c.Name}\""));
        var paramNames = string.Join(", ", schema.Select((_, i) => $"@p{i}"));

        // All identifiers from information_schema (system catalog), not user input (CA2100).
        using var pgCmd = CreateSelectCommand(pgConn, table, quotedCols);
        using var reader = await pgCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        using var insertCmd = CreateSqliteInsertCommand(sqliteConn, table, quotedCols, paramNames);

        var parameters = schema.Select((_, i) =>
        {
            var p = insertCmd.CreateParameter();
            p.ParameterName = $"@p{i}";
            insertCmd.Parameters.Add(p);
            return p;
        }).ToList();

        SqliteTransaction? activeTx = (SqliteTransaction)await sqliteConn.BeginTransactionAsync(ct).ConfigureAwait(false);
        insertCmd.Transaction = activeTx;
        long batchCount = 0;
        long rowsRead = 0;

        try
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                for (var col = 0; col < schema.Count; col++)
                {
                    parameters[col].Value = ConvertForSqlite(reader.GetValue(col), isKeyColumn[col]);
                }

                try
                {
                    await insertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraintErrorCode)
                {
                    throw new InvalidOperationException(
                        BuildConstraintError(table, schema, parameters, isKeyColumn, rowsRead + 1, ex), ex);
                }

                batchCount++;
                rowsRead++;
                _exportedRows++;

                if (batchCount >= CommitEveryRows)
                {
                    await activeTx.CommitAsync(ct).ConfigureAwait(false);
                    await activeTx.DisposeAsync().ConfigureAwait(false);
                    activeTx = (SqliteTransaction)await sqliteConn.BeginTransactionAsync(ct).ConfigureAwait(false);
                    insertCmd.Transaction = activeTx;
                    batchCount = 0;
                }
            }

            await activeTx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await activeTx.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds an actionable error for a SQLite constraint violation, including the
    /// primary-key values of the row that could not be inserted.
    /// </summary>
    /// <param name="table">Table being exported.</param>
    /// <param name="schema">Columns being exported, in insert order.</param>
    /// <param name="parameters">Insert parameters holding the offending row values.</param>
    /// <param name="isKeyColumn">Flags the primary-key columns within <paramref name="schema"/>.</param>
    /// <param name="rowNumber">1-based row number within the table.</param>
    /// <param name="inner">The SQLite error being reported.</param>
    /// <returns>Error message with the conflicting primary key.</returns>
    private static string BuildConstraintError(
        string table,
        List<ColumnInfo> schema,
        List<SqliteParameter> parameters,
        bool[] isKeyColumn,
        long rowNumber,
        SqliteException inner)
    {
        var key = new List<string>();
        for (var i = 0; i < schema.Count; i++)
        {
            if (isKeyColumn[i])
            {
                key.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}={1}",
                    schema[i].Name,
                    parameters[i].Value ?? "NULL"));
            }
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "Failed to export table '{0}' (row {1}): {2} Conflicting primary key: {3}. "
            + "Verify the source rows in PostgreSQL; two rows may differ only by letter case.",
            table,
            rowNumber,
            inner.Message,
            key.Count > 0 ? string.Join(", ", key) : "(unknown)");
    }

    // ── Value conversion ──────────────────────────────────────────────────────

    // GUID format note: Jellyfin's native SQLite stores GUIDs in UPPERCASE
    // (e.g. "DB139722-47D3-4C47-ADE9-F625715551CA"). PostgreSQL stores them
    // in lowercase. We normalise to UPPERCASE on export so that Jellyfin's
    // case-sensitive internal lookups (UserManager, DeviceManager, etc.) work
    // correctly when switching back to SQLite mode.
    //
    // Primary-key columns are never case-normalised: a text key column such as
    // UserData.CustomDataKey is case-sensitive, and rows like 'AE19...' / 'ae19...'
    // are legitimate distinct rows. Uppercasing them merges both rows and violates
    // the primary key, aborting the export.
    private static object ConvertForSqlite(object? value, bool preserveKeyColumn) => value switch
    {
        null or DBNull => DBNull.Value,
        bool b => b ? 1 : 0,
        // GUIDs must be UPPERCASE to match Jellyfin's native SQLite format
        Guid g => g.ToString().ToUpperInvariant(),
        DateTime dt => dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        float f when float.IsNaN(f) || float.IsInfinity(f) => DBNull.Value,
        double d when double.IsNaN(d) || double.IsInfinity(d) => DBNull.Value,
        long[] arr => JsonSerializer.Serialize(arr),
        int[] arr => JsonSerializer.Serialize(arr),
        float[] arr => JsonSerializer.Serialize(arr),
        // String GUIDs from PostgreSQL (uuid columns read as string) → UPPERCASE
        string s when !preserveKeyColumn && GuidPattern.IsMatch(s) => s.ToUpperInvariant(),
        _ => value,
    };

    // ── Logging ───────────────────────────────────────────────────────────────

    private void Log(string message)
    {
        if (!string.IsNullOrEmpty(message))
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("{Message}", message);
            }
        }

        lock (_logLock)
        {
            if (_logBuffer.Count >= MaxLogLines)
            {
                _logBuffer.RemoveAt(0);
            }

            _logBuffer.Add($"[{DateTime.UtcNow:HH:mm:ss}] {message}");
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:F1} GB", bytes / 1_073_741_824.0);
        }

        if (bytes >= 1_048_576)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:F1} MB", bytes / 1_048_576.0);
        }

        if (bytes >= 1_024)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:F1} KB", bytes / 1_024.0);
        }

        return string.Format(CultureInfo.InvariantCulture, "{0} B", bytes);
    }

    // ── Command factory methods ─────────────────────────────────────────────────────────
    // table/quotedCols come from pg_tables / information_schema (server catalog), not user input.

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "table comes from pg_tables system catalog, not user input. Identifier is quoted.")]
    private static NpgsqlCommand CreateCountCommand(NpgsqlConnection conn, string table)
    {
        var sql = string.Concat("SELECT COUNT(*) FROM \"", table.Replace("\"", "\"\"", StringComparison.Ordinal), "\";");
        return new NpgsqlCommand(sql, conn);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "table/quotedCols from information_schema catalog, not user input.")]
    private static NpgsqlCommand CreateSelectCommand(NpgsqlConnection conn, string table, string quotedCols)
    {
        var sql = string.Concat("SELECT ", quotedCols, " FROM \"", table.Replace("\"", "\"\"", StringComparison.Ordinal), "\";");
        return new NpgsqlCommand(sql, conn) { CommandTimeout = 0 };
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "table/quotedCols from information_schema catalog, not user input.")]
    private static SqliteCommand CreateSqliteInsertCommand(
        SqliteConnection conn, string table, string quotedCols, string paramNames)
    {
        var cmd = conn.CreateCommand();
        var safeName = table.Replace("\"", "\"\"", StringComparison.Ordinal);
        cmd.CommandText = string.Concat("INSERT INTO \"", safeName, "\" (", quotedCols, ") VALUES (", paramNames, ");");
        return cmd;
    }

    // ── Orphan cleanup ────────────────────────────────────────────────────────
    // After export, some rows may reference Users that were not exported
    // (e.g. system/service accounts). Deleting orphans prevents FK failures
    // when Jellyfin activates FK enforcement at runtime.

    [SuppressMessage("Security", "CA2100", Justification = "Hardcoded cleanup SQL, no user input.")]
    private async Task CleanOrphanedForeignKeysAsync(SqliteConnection conn, CancellationToken ct)
    {
        // Devices and ApiKeys hold session tokens tied to the PostgreSQL instance.
        // When switching to SQLite mode those tokens are invalid anyway, and their
        // UserId values may reference users that differ between the two engines.
        // Clearing them forces Jellyfin to create fresh sessions on first login.
        var cleanupSql = new[]
        {
            "DELETE FROM \"Devices\";",
            "DELETE FROM \"DeviceOptions\";",
            "DELETE FROM \"ApiKeys\" WHERE \"Name\" != 'Jellyfin';",
            "DELETE FROM \"Permissions\" WHERE \"UserId\" NOT IN (SELECT \"Id\" FROM \"Users\");",
            "DELETE FROM \"Preferences\" WHERE \"UserId\" NOT IN (SELECT \"Id\" FROM \"Users\");",
            "DELETE FROM \"AccessSchedules\" WHERE \"UserId\" NOT IN (SELECT \"Id\" FROM \"Users\");",
            "DELETE FROM \"DisplayPreferences\" WHERE \"UserId\" NOT IN (SELECT \"Id\" FROM \"Users\");",
            "DELETE FROM \"ItemDisplayPreferences\" WHERE \"UserId\" NOT IN (SELECT \"Id\" FROM \"Users\");",
            "DELETE FROM \"CustomItemDisplayPreferences\" WHERE \"UserId\" NOT IN (SELECT \"Id\" FROM \"Users\");",
            "DELETE FROM \"UserData\" WHERE \"UserId\" NOT IN (SELECT \"Id\" FROM \"Users\");",
            "DELETE FROM \"HomeSection\" WHERE \"DisplayPreferencesId\" NOT IN (SELECT \"Id\" FROM \"DisplayPreferences\");",
            "DELETE FROM \"ImageInfos\" WHERE \"UserId\" IS NOT NULL AND \"UserId\" NOT IN (SELECT \"Id\" FROM \"Users\");",
        };

        var totalDeleted = 0;
        foreach (var sql in cleanupSql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            try
            {
                var deleted = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                if (deleted > 0)
                {
                    totalDeleted += deleted;
                    Log($"  [Cleanup] {deleted} filas huérfanas eliminadas: {sql[..Math.Min(60, sql.Length)]}...");
                }
            }
            catch (Exception ex)
            {
                // Table may not exist in this schema version — skip silently
                Log($"  [Cleanup] Omitido (tabla no existe): {ex.Message[..Math.Min(80, ex.Message.Length)]}");
            }
        }

        if (totalDeleted > 0)
        {
            Log($"[PostExport] Limpieza FK: {totalDeleted} filas huérfanas eliminadas.");
            PostgresLog.Info($"[Export] FK cleanup: {totalDeleted} orphaned rows removed.");
        }
    }

    // ── library.db stub ──────────────────────────────────────────────────────
    // Jellyfin's migration service tries to back up library.db before running
    // legacy code migrations. If it doesn't exist (or is 0 bytes / malformed),
    // the backup fails and the whole startup aborts. We create a minimal valid
    // SQLite file so the backup succeeds and the legacy migrations can proceed
    // (they will find no TypedBaseItems and exit gracefully, or be pre-marked).

    private static void EnsureEmptyLibraryDb(string sqliteDbPath)
    {
        var dataDir = Path.GetDirectoryName(sqliteDbPath) ?? string.Empty;
        var libraryDbPath = Path.Combine(dataDir, "library.db");

        if (File.Exists(libraryDbPath) && new FileInfo(libraryDbPath).Length >= 4096)
        {
            return; // already a valid SQLite file
        }

        // Minimal valid SQLite database: one 4096-byte page with correct header.
        // Page size stored at offset 16 (big-endian uint16): 0x10 0x00 = 4096.
        var page = new byte[4096];
        var magic = System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0");
        Array.Copy(magic, page, magic.Length);
        page[16] = 0x10; // page size high byte (4096 = 0x1000)
        page[17] = 0x00; // page size low byte
        page[18] = 1;    // file format write version
        page[19] = 1;    // file format read version
        page[20] = 0;    // reserved bytes per page
        page[21] = 64;   // max embedded payload fraction
        page[22] = 32;   // min embedded payload fraction
        page[23] = 32;   // leaf payload fraction

        File.WriteAllBytes(libraryDbPath, page);
        PostgresLog.Info($"[Export] library.db stub creado: {libraryDbPath}");
    }

    // Preserve pending code migrations instead of claiming every discovered routine ran.
    private async Task CopyCodeMigrationsAsync(NpgsqlConnection pg, SqliteConnection conn, CancellationToken ct)
    {
        using var select = new NpgsqlCommand("SELECT \"MigrationId\", \"ProductVersion\" FROM \"__EFMigrationsHistory\";", pg);
        using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var count = 0;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            if (!MigrationCodeMigrations.IsCodeMigrationId(id))
            {
                continue;
            }

            using var insert = conn.CreateCommand();
            insert.CommandText = "INSERT OR IGNORE INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES (@id, @version);";
            insert.Parameters.AddWithValue("@id", id);
            insert.Parameters.AddWithValue("@version", reader.GetString(1));
            count += await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        Log($"[PostExport] {count} migraciones aplicadas copiadas desde PostgreSQL.");
    }
}
