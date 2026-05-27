using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>Immutable snapshot of migration progress returned to the API layer.</summary>
public sealed record MigrationProgress(
    bool IsRunning,
    bool IsCompleted,
    bool HasError,
    string? ErrorMessage,
    int PercentComplete,
    string CurrentTable,
    long MigratedRows,
    long TotalRows,
    IReadOnlyList<string> LogLines);

/// <summary>
/// Runs the SQLite → PostgreSQL data migration in-process.
/// Mirrors the logic of the standalone migrator exe but:
///   - Reports progress for the plugin UI.
///   - Discovers code migrations from the running AppDomain instead of via --jellyfin-dll.
/// Thread-safe: only one migration can run at a time (enforced by <see cref="_semaphore"/>).
/// </summary>
public sealed class MigrationService : IDisposable
{
    private const int DefaultBatch = 1000;
    private const int MaxLogLines = 500;

    private static readonly HashSet<string> SkipTables = new(StringComparer.OrdinalIgnoreCase)
        { "__EFMigrationsHistory", "__EFMigrationsLock" };

    private readonly ILogger<MigrationService> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <inheritdoc />
    public void Dispose() => _semaphore.Dispose();

    // Live state — written by migration task, read by status endpoint
    private volatile bool _isRunning;
    private volatile bool _isCompleted;
    private volatile bool _hasError;
    private string? _errorMessage;
    private int _percentComplete;
    private string _currentTable = string.Empty;
    private long _migratedRows;
    private long _totalRows;
    private readonly List<string> _logBuffer = new(MaxLogLines);
    private readonly object _logLock = new();

    /// <summary>
    /// Initializes a new instance of <see cref="MigrationService"/>.
    /// </summary>
    public MigrationService(ILogger<MigrationService> logger)
    {
        _logger = logger;
    }

    /// <summary>Gets a snapshot of the current migration progress.</summary>
    public MigrationProgress GetProgress()
    {
        lock (_logLock)
        {
            return new MigrationProgress(
                IsRunning: _isRunning,
                IsCompleted: _isCompleted,
                HasError: _hasError,
                ErrorMessage: _errorMessage,
                PercentComplete: _percentComplete,
                CurrentTable: _currentTable,
                MigratedRows: _migratedRows,
                TotalRows: _totalRows,
                LogLines: _logBuffer.ToList());
        }
    }

    /// <summary>
    /// Starts the migration in a background task.  Returns immediately; poll
    /// <see cref="GetProgress"/> for status.
    /// </summary>
    /// <returns><see langword="true"/> if the migration was started; <see langword="false"/>
    /// if one is already running.</returns>
    public bool StartMigration(
        string sqlitePath,
        string postgresConnectionString,
        string schema = "public",
        int batchSize = DefaultBatch,
        bool truncate = false,
        IApplicationPaths? applicationPaths = null)
    {
        if (!_semaphore.Wait(0))
        {
            return false; // already running
        }

        _isRunning = true;
        _isCompleted = false;
        _hasError = false;
        _errorMessage = null;
        _percentComplete = 0;
        _currentTable = string.Empty;
        _migratedRows = 0;
        _totalRows = 0;

        lock (_logLock) { _logBuffer.Clear(); }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunMigrationAsync(
                    sqlitePath, postgresConnectionString, schema, batchSize, truncate,
                    applicationPaths, CancellationToken.None).ConfigureAwait(false);
                _isCompleted = true;
            }
            catch (Exception ex)
            {
                _hasError = true;
                _errorMessage = ex.Message;
                Log($"[FATAL] {ex.Message}");
                _logger.LogError(ex, "Migration failed with unhandled exception");
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
    // Core migration logic
    // ─────────────────────────────────────────────────────────────────────────

    private async Task RunMigrationAsync(
        string sqlitePath,
        string postgresConnectionString,
        string schema,
        int batchSize,
        bool truncate,
        IApplicationPaths? applicationPaths,
        CancellationToken ct)
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

        if (!File.Exists(sqlitePath))
        {
            throw new FileNotFoundException($"SQLite file not found: {sqlitePath}", sqlitePath);
        }

        Log($"Iniciando migración: {sqlitePath} → PostgreSQL (schema={schema}, batch={batchSize})");

        await using var sqlite = new SqliteConnection($"Data Source={sqlitePath};Mode=ReadOnly;Cache=Shared");
        await sqlite.OpenAsync(ct).ConfigureAwait(false);

        await using var pg = new NpgsqlConnection(postgresConnectionString);
        await pg.OpenAsync(ct).ConfigureAwait(false);

        // Auto-create schema if needed
        await ApplySchemaIfNeededAsync(pg, schema).ConfigureAwait(false);

        // Enforce timestamptz for all timestamp columns so UTC DateTime values can be inserted
        // without Npgsql conversion errors.
        await EnsureTimestampColumnsAreTimestamptzAsync(pg, schema).ConfigureAwait(false);

        // Pre-mark code migrations from running AppDomain (no external DLL needed)
        await PreMarkCodeMigrationsAsync(pg).ConfigureAwait(false);

        var tables = await GetSqliteTablesAsync(sqlite).ConfigureAwait(false);
        var tablesToMigrate = tables
            .Where(t => !SkipTables.Contains(t))
            .ToList();

        Log($"Tablas SQLite: {tables.Count} total, {tablesToMigrate.Count} a migrar");
        _totalRows = tablesToMigrate.Count; // use as step count before we know row counts

        tablesToMigrate = await SortTablesByFkDependencyAsync(pg, schema, tablesToMigrate).ConfigureAwait(false);

        // Disable FK triggers for the entire migration.
        // SQLite never enforces FK constraints, so orphaned rows are common (e.g. AncestorIds
        // referencing deleted BaseItems). Without this, every orphaned row causes a batch
        // failure + row-by-row retry → thousands of [SKIP] messages and very slow migration.
        // DISABLE TRIGGER ALL only requires table ownership (the Jellyfin PG user owns these tables).
        await SetFkTriggersAsync(pg, schema, tablesToMigrate, enable: false).ConfigureAwait(false);

        // Users table: NormalizedUsername fixup
        var needNormalizedUsernameFixup = false;
        if (tablesToMigrate.Any(t => string.Equals(t, "Users", StringComparison.OrdinalIgnoreCase)))
        {
            var pgCols = await GetPgColumnsAsync(pg, schema, "Users").ConfigureAwait(false);
            var sqliteCols = await GetSqliteColumnNamesAsync(sqlite, "Users").ConfigureAwait(false);
            needNormalizedUsernameFixup = pgCols.ContainsKey("NormalizedUsername")
                && !sqliteCols.Any(c => string.Equals(c, "NormalizedUsername", StringComparison.OrdinalIgnoreCase));
            if (needNormalizedUsernameFixup)
            {
                Log("[Users] Permitiendo NULL en NormalizedUsername temporalmente...");
                await using var alterCmd = new NpgsqlCommand(
                    $"ALTER TABLE {QuoteIdentifier(schema)}.\"Users\" ALTER COLUMN \"NormalizedUsername\" DROP NOT NULL;", pg);
                await alterCmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        var errors = new List<(string Table, string Message)>();
        long totalRows = 0;

        try
        {
            for (var i = 0; i < tablesToMigrate.Count; i++)
            {
                var table = tablesToMigrate[i];
                _currentTable = table;
                _percentComplete = (int)Math.Round((double)i / tablesToMigrate.Count * 95);

                var sqliteColumns = await GetSqliteColumnNamesAsync(sqlite, table).ConfigureAwait(false);
                if (sqliteColumns.Count == 0)
                {
                    Log($"[{table}] sin columnas en SQLite, omitido.");
                    continue;
                }

                var pgColumns = await GetPgColumnsAsync(pg, schema, table).ConfigureAwait(false);
                if (pgColumns.Count == 0)
                {
                    Log($"[{table}] no existe en PostgreSQL, omitido.");
                    continue;
                }

                var commonColumns = sqliteColumns
                    .Where(sc => pgColumns.ContainsKey(sc))
                    .Select(sc => new TargetColumn(sc, pgColumns[sc].PgType, pgColumns[sc].MaxLength))
                    .ToList();

                if (commonColumns.Count == 0)
                {
                    Log($"[{table}] ninguna columna coincide, omitido.");
                    continue;
                }

                try
                {
                    if (truncate)
                    {
                        var pgTable = $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";
                        Log($"[{table}] truncando...");
                        await using var truncCmd = new NpgsqlCommand(
                            $"TRUNCATE TABLE {pgTable} RESTART IDENTITY CASCADE;", pg);
                        await truncCmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
                    }

                    Log($"[{table}] copiando {commonColumns.Count} columna(s)...");
                    var rowCount = await CopyTableAsync(sqlite, pg, schema, table, commonColumns, batchSize).ConfigureAwait(false);
                    totalRows += rowCount;
                    _migratedRows = totalRows;
                    Log($"[{table}] OK — {rowCount} filas.");
                }
                catch (Exception ex)
                {
                    Log($"[{table}] ERROR: {ex.Message}");
                    errors.Add((table, ex.Message));
                }
            }

            if (needNormalizedUsernameFixup)
            {
                Log("[Users] Calculando NormalizedUsername = UPPER(Username)...");
                await using var fixCmd = new NpgsqlCommand(
                    $"UPDATE {QuoteIdentifier(schema)}.\"Users\" SET \"NormalizedUsername\" = UPPER(\"Username\") WHERE \"NormalizedUsername\" IS NULL;", pg);
                var updated = await fixCmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
                Log($"[Users] {updated} filas actualizadas.");
                await using var restoreCmd = new NpgsqlCommand(
                    $"ALTER TABLE {QuoteIdentifier(schema)}.\"Users\" ALTER COLUMN \"NormalizedUsername\" SET NOT NULL;", pg);
                await restoreCmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
                Log("[Users] Restricción NOT NULL restaurada.");
            }

            Log("Reseteando secuencias PostgreSQL...");
            await ResetSequencesAsync(pg, schema).ConfigureAwait(false);
            Log("Secuencias reseteadas.");
        }
        finally
        {
            // Always re-enable FK triggers, even if migration partially fails.
            await SetFkTriggersAsync(pg, schema, tablesToMigrate, enable: true).ConfigureAwait(false);
        }

        _percentComplete = 100;

        if (errors.Count > 0)
        {
            var msg = $"{errors.Count} tabla(s) fallaron: " + string.Join(", ", errors.Select(e => e.Table));
            Log($"[WARN] {msg}");
            // Don't throw — partial migration is still useful. HasError is NOT set.
        }

        Log($"Migración completada. Total de filas copiadas: {totalRows}.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Pre-mark code migrations (uses running AppDomain — no DLL needed)
    // ─────────────────────────────────────────────────────────────────────────

    private async Task PreMarkCodeMigrationsAsync(NpgsqlConnection pg)
    {
        Type? attrType = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            attrType = asm.GetType("Jellyfin.Server.Migrations.JellyfinMigrationAttribute");
            if (attrType is not null) break;
        }

        if (attrType is null)
        {
            Log("[CodeMigrations] JellyfinMigrationAttribute no encontrado en el AppDomain (¿versión vieja de Jellyfin?).");
            return;
        }

        var orderProp = attrType.GetProperty("Order");
        var nameProp = attrType.GetProperty("Name");
        if (orderProp is null || nameProp is null)
        {
            Log("[CodeMigrations] Propiedades Order/Name no encontradas.");
            return;
        }

        var count = 0;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] allTypes;
            try { allTypes = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { allTypes = ex.Types.Where(t => t is not null).ToArray()!; }

            foreach (var type in allTypes)
            {
                if (type is null) continue;
                var attrs = type.GetCustomAttributes(attrType, false);
                if (attrs.Length == 0) continue;

                var attr = attrs[0];
                var order = (DateTime)orderProp.GetValue(attr)!;
                var name = (string)nameProp.GetValue(attr)!;

                foreach (var fmt in new[] { "yyyyMMddHHmmss", "yyyyMMddHHmmsss" })
                {
                    var migrationId = order.ToString(fmt, CultureInfo.InvariantCulture) + "_" + name;
                    await using var cmd = new NpgsqlCommand(
                        "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES (@id, @ver) ON CONFLICT DO NOTHING;",
                        pg);
                    cmd.Parameters.AddWithValue("id", migrationId);
                    cmd.Parameters.AddWithValue("ver", "0.0.0");
                    var inserted = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    if (inserted > 0) count++;
                }
            }
        }

        Log($"[CodeMigrations] {count} migration(es) pre-marcadas.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Schema auto-creation
    // ─────────────────────────────────────────────────────────────────────────

    private async Task ApplySchemaIfNeededAsync(NpgsqlConnection pg, string schema)
    {
        await using var checkCmd = new NpgsqlCommand(
            "SELECT 1 FROM information_schema.tables WHERE table_schema = @s AND table_name = 'BaseItems' LIMIT 1;", pg);
        checkCmd.Parameters.AddWithValue("s", schema);
        var exists = await checkCmd.ExecuteScalarAsync().ConfigureAwait(false);
        if (exists is not null)
        {
            Log("[Schema] Schema ya existe — omitiendo creación.");
            return;
        }

        Log("[Schema] Creando schema desde SQL embebido...");
        var assembly = GetType().Assembly;
        // Shared resource name — same SQL used by the migrator exe
        const string resourceName = "Jellyfin.Database.Providers.Postgres.Resources.schema.sql";
        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var schemaSql = await reader.ReadToEndAsync().ConfigureAwait(false);

        var statements = schemaSql
            .Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().TrimEnd(';'))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        foreach (var statement in statements)
        {
            await using var cmd = new NpgsqlCommand(statement, pg);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        Log($"[Schema] {statements.Count} sentencias aplicadas.");
    }

    private async Task EnsureTimestampColumnsAreTimestamptzAsync(NpgsqlConnection pg, string schema)
    {
        const string findSql = """
            SELECT table_name, column_name
            FROM information_schema.columns
            WHERE table_schema = @s
              AND data_type = 'timestamp without time zone'
            ORDER BY table_name, ordinal_position;
            """;

        var toConvert = new List<(string Table, string Column)>();
        await using (var findCmd = new NpgsqlCommand(findSql, pg))
        {
            findCmd.Parameters.AddWithValue("s", schema);
            await using var reader = await findCmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                toConvert.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        if (toConvert.Count == 0)
        {
            return;
        }

        Log($"[Schema] Convirtiendo {toConvert.Count} columna(s) timestamp -> timestamptz...");
        foreach (var (table, column) in toConvert)
        {
            var sql = $"""
                ALTER TABLE {QuoteIdentifier(schema)}.{QuoteIdentifier(table)}
                ALTER COLUMN {QuoteIdentifier(column)} TYPE timestamp with time zone
                USING CASE
                    WHEN {QuoteIdentifier(column)} IS NULL THEN NULL
                    ELSE {QuoteIdentifier(column)} AT TIME ZONE 'UTC'
                END;
                """;
            await using var cmd = new NpgsqlCommand(sql, pg);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        Log("[Schema] Conversión de timestamps completada.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Table / column discovery
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<List<string>> GetSqliteTablesAsync(SqliteConnection sqlite)
    {
        const string sql = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        var result = new List<string>();
        await using var cmd = new SqliteCommand(sql, sqlite);
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    private static async Task<List<string>> GetSqliteColumnNamesAsync(SqliteConnection sqlite, string table)
    {
        var result = new List<string>();
        await using var cmd = new SqliteCommand($"PRAGMA table_info({QuoteSqliteIdentifier(table)});", sqlite);
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            result.Add(reader.GetString(reader.GetOrdinal("name")));
        }
        return result;
    }

    private static async Task<Dictionary<string, (string PgType, int? MaxLength)>> GetPgColumnsAsync(
        NpgsqlConnection pg, string schema, string table)
    {
        var result = new Dictionary<string, (string, int?)>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = new NpgsqlCommand(@"
            SELECT column_name, data_type, character_maximum_length
            FROM information_schema.columns
            WHERE table_schema = @schema AND table_name = @table
            ORDER BY ordinal_position;", pg);
        cmd.Parameters.AddWithValue("schema", schema);
        cmd.Parameters.AddWithValue("table", table);
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var colName = reader.GetString(0);
            var dataType = reader.GetString(1);
            int? maxLen = await reader.IsDBNullAsync(2).ConfigureAwait(false) ? null : reader.GetInt32(2);
            result[colName] = (dataType, maxLen);
        }
        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FK topological sort (Kahn's algorithm)
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<List<string>> SortTablesByFkDependencyAsync(
        NpgsqlConnection pg, string schema, List<string> tables)
    {
        var edges = new List<(string From, string To)>();
        await using var cmd = new NpgsqlCommand(@"
            SELECT DISTINCT
                tc.table_name      AS dependent_table,
                ccu.table_name     AS referenced_table
            FROM information_schema.table_constraints tc
            JOIN information_schema.referential_constraints rc
                ON tc.constraint_name = rc.constraint_name
                AND tc.table_schema = rc.constraint_schema
            JOIN information_schema.constraint_column_usage ccu
                ON rc.unique_constraint_name = ccu.constraint_name
                AND ccu.table_schema = @schema
            WHERE tc.constraint_type = 'FOREIGN KEY'
              AND tc.table_schema = @schema;", pg);
        cmd.Parameters.AddWithValue("schema", schema);
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var from = reader.GetString(0);
            var to = reader.GetString(1);
            if (!string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            {
                edges.Add((from, to));
            }
        }

        var tableSet = new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);
        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var adjacency = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tableSet) { inDegree[t] = 0; adjacency[t] = new List<string>(); }

        foreach (var (from, to) in edges)
        {
            if (!tableSet.Contains(from) || !tableSet.Contains(to)) continue;
            inDegree[from]++;
            adjacency[to].Add(from);
        }

        var queue = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key).OrderBy(t => t));
        var sorted = new List<string>(tables.Count);
        while (queue.Count > 0)
        {
            var t = queue.Dequeue();
            sorted.Add(t);
            foreach (var dep in adjacency[t].OrderBy(x => x))
            {
                if (--inDegree[dep] == 0) queue.Enqueue(dep);
            }
        }

        foreach (var t in tables)
        {
            if (!sorted.Contains(t, StringComparer.OrdinalIgnoreCase)) sorted.Add(t);
        }

        Log($"Orden de migración: {string.Join(" → ", sorted)}");
        return sorted;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Data copy
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<long> CopyTableAsync(
        SqliteConnection sqlite, NpgsqlConnection pg, string schema,
        string table, IReadOnlyList<TargetColumn> columns, int batchSize)
    {
        var colList = string.Join(", ", columns.Select(c => QuoteSqliteIdentifier(c.Name)));
        var selectSql = $"SELECT {colList} FROM {QuoteSqliteIdentifier(table)};";
        await using var selectCmd = new SqliteCommand(selectSql, sqlite);
        await using var reader = await selectCmd.ExecuteReaderAsync().ConfigureAwait(false);

        var pgColumnList = string.Join(", ", columns.Select(c => QuoteIdentifier(c.Name)));
        var pgTable = $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";

        var effectiveBatchSize = Math.Max(1, Math.Min(batchSize, 65_535 / columns.Count));

        var normalizedUsernameIdx = -1;
        var usernameIdx = -1;
        if (string.Equals(table, "Users", StringComparison.OrdinalIgnoreCase))
        {
            for (var i = 0; i < columns.Count; i++)
            {
                if (string.Equals(columns[i].Name, "NormalizedUsername", StringComparison.OrdinalIgnoreCase)) normalizedUsernameIdx = i;
                if (string.Equals(columns[i].Name, "Username", StringComparison.OrdinalIgnoreCase)) usernameIdx = i;
            }
        }

        long total = 0;
        var rows = new List<object?[]>(effectiveBatchSize);

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var values = new object?[columns.Count];
            for (var i = 0; i < columns.Count; i++) values[i] = reader.GetValue(i);

            if (normalizedUsernameIdx >= 0 && usernameIdx >= 0
                && (values[normalizedUsernameIdx] is null or DBNull))
            {
                values[normalizedUsernameIdx] = (values[usernameIdx]?.ToString() ?? string.Empty).ToUpperInvariant();
            }

            rows.Add(values);
            if (rows.Count >= effectiveBatchSize)
            {
                total += await InsertBatchAsync(pg, pgTable, pgColumnList, columns, rows).ConfigureAwait(false);
                _migratedRows = total;
                rows.Clear();
            }
        }

        if (rows.Count > 0)
        {
            total += await InsertBatchAsync(pg, pgTable, pgColumnList, columns, rows).ConfigureAwait(false);
            _migratedRows = total;
        }

        return total;
    }

    private async Task<long> InsertBatchAsync(
        NpgsqlConnection pg, string pgTable, string pgColumnList,
        IReadOnlyList<TargetColumn> columns, IReadOnlyList<object?[]> rows)
    {
        await using var tx = await pg.BeginTransactionAsync().ConfigureAwait(false);
        var valuesSql = new List<string>(rows.Count);
        await using var cmd = new NpgsqlCommand { Connection = pg, Transaction = tx };

        var paramIndex = 0;
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var placeholders = new string[columns.Count];
            for (var colIndex = 0; colIndex < columns.Count; colIndex++)
            {
                var paramName = $"p{paramIndex++}";
                placeholders[colIndex] = $"@{paramName}";
                var rawValue = rows[rowIndex][colIndex];
                var column = columns[colIndex];
                var param = new NpgsqlParameter { ParameterName = paramName };

                if (rawValue is null or DBNull)
                {
                    param.Value = DBNull.Value;
                }
                else if (rawValue is byte[] binaryValue)
                {
                    param.NpgsqlDbType = NpgsqlDbType.Bytea;
                    param.Value = binaryValue;
                }
                else
                {
                    var pgType = column.PgType;
                    var strVal = Convert.ToString(rawValue, CultureInfo.InvariantCulture) ?? string.Empty;

                    if (pgType is "boolean")
                    {
                        if (long.TryParse(strVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var boolLong))
                        { param.NpgsqlDbType = NpgsqlDbType.Boolean; param.Value = boolLong != 0; }
                        else if (bool.TryParse(strVal, out var boolVal))
                        { param.NpgsqlDbType = NpgsqlDbType.Boolean; param.Value = boolVal; }
                        else
                        { param.NpgsqlDbType = NpgsqlDbType.Boolean; param.Value = DBNull.Value;
                          Log($"  [WARN] {pgTable}.{column.Name}: boolean esperado, valor '{strVal}' → NULL"); }
                    }
                    else if (pgType is "bigint" or "integer" or "smallint")
                    {
                        if (long.TryParse(strVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longVal))
                        { param.NpgsqlDbType = NpgsqlDbType.Bigint; param.Value = longVal; }
                                                else if (string.Equals(column.Name, "TotalDuration", StringComparison.OrdinalIgnoreCase)
                                                                 && pgTable.EndsWith("\"KeyframeData\"", StringComparison.Ordinal))
                                                { param.NpgsqlDbType = NpgsqlDbType.Bigint; param.Value = 0L;
                                                    Log($"  [WARN] {pgTable}.{column.Name}: bigint inválido '{strVal}' -> 0"); }
                        else
                        { param.NpgsqlDbType = NpgsqlDbType.Bigint; param.Value = DBNull.Value;
                          Log($"  [WARN] {pgTable}.{column.Name}: {pgType} esperado, valor '{strVal}' → NULL"); }
                    }
                    else if (pgType is "double precision" or "real" or "numeric")
                    {
                        if (double.TryParse(strVal, NumberStyles.Float, CultureInfo.InvariantCulture, out var dblVal))
                        { param.NpgsqlDbType = NpgsqlDbType.Double; param.Value = dblVal; }
                        else
                        { param.NpgsqlDbType = NpgsqlDbType.Double; param.Value = DBNull.Value;
                          Log($"  [WARN] {pgTable}.{column.Name}: {pgType} esperado, valor '{strVal}' → NULL"); }
                    }
                    else if (pgType is "timestamp without time zone" or "timestamp with time zone")
                    {
                        if (DateTime.TryParse(strVal, CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dtVal))
                        {
                            param.NpgsqlDbType = pgType == "timestamp with time zone"
                                ? NpgsqlDbType.TimestampTz : NpgsqlDbType.Timestamp;
                            param.Value = dtVal;
                        }
                        else
                        { param.NpgsqlDbType = NpgsqlDbType.Timestamp; param.Value = DBNull.Value;
                          Log($"  [WARN] {pgTable}.{column.Name}: timestamp esperado, valor '{strVal}' → NULL"); }
                    }
                    else if (pgType is "ARRAY")
                    {
                        var trimmed = strVal.Trim();
                        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                        { param.NpgsqlDbType = NpgsqlDbType.Unknown; param.Value = '{' + trimmed[1..^1] + '}'; }
                        else
                        { param.NpgsqlDbType = NpgsqlDbType.Unknown; param.Value = DBNull.Value;
                          Log($"  [WARN] {pgTable}.{column.Name}: ARRAY esperado, valor '{strVal}' no es JSON array → NULL"); }
                    }
                    else
                    {
                        var textVal = strVal;
                        if (column.MaxLength.HasValue && textVal.Length > column.MaxLength.Value)
                            textVal = textVal[..column.MaxLength.Value];
                        param.NpgsqlDbType = NpgsqlDbType.Unknown;
                        param.Value = textVal;
                    }
                }

                cmd.Parameters.Add(param);
            }
            valuesSql.Add($"({string.Join(", ", placeholders)})");
        }

        cmd.CommandText = $"INSERT INTO {pgTable} ({pgColumnList}) VALUES {string.Join(", ", valuesSql)} ON CONFLICT DO NOTHING;";
        try
        {
            var inserted = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            await tx.CommitAsync().ConfigureAwait(false);
            return inserted;
        }
        catch (PostgresException pgEx)
        {
            await tx.RollbackAsync().ConfigureAwait(false);
            if (rows.Count == 1)
            {
                Log($"  [SKIP] {pgTable}: fila saltada ({pgEx.SqlState}): {pgEx.MessageText}");
                return 0;
            }
            Log($"  [WARN] {pgTable}: batch de {rows.Count} filas falló ({pgEx.SqlState}), reintentando fila por fila...");
            long total = 0;
            foreach (var row in rows)
                total += await InsertBatchAsync(pg, pgTable, pgColumnList, columns, new[] { row }).ConfigureAwait(false);
            return total;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Sequence reset
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task ResetSequencesAsync(NpgsqlConnection pg, string schema)
    {
        var safeSchema = schema.Replace("'", "''", StringComparison.Ordinal);
        var sql = $"""
            DO $$
            DECLARE r RECORD;
            BEGIN
              FOR r IN
                                SELECT nsp.nspname AS schema_name,
                                             seq.relname AS seq_name,
                                             tab.relname AS table_name,
                                             col.attname AS col_name
                FROM pg_class seq
                                JOIN pg_namespace nsp ON nsp.oid        = seq.relnamespace
                                JOIN pg_depend    dep ON dep.objid      = seq.oid
                                JOIN pg_class     tab ON tab.oid        = dep.refobjid
                                JOIN pg_attribute col ON col.attrelid   = tab.oid
                                                                            AND col.attnum    = dep.refobjsubid
                WHERE seq.relkind = 'S'
                                    AND nsp.nspname = '{safeSchema}'
              LOOP
                EXECUTE format(
                                    'SELECT setval(%L::regclass, COALESCE((SELECT MAX(%I) FROM %I.%I), 0) + 1, false)',
                                    format('%I.%I', r.schema_name, r.seq_name),
                                    r.col_name,
                                    r.schema_name,
                                    r.table_name);
              END LOOP;
            END $$;
            """;
        await using var cmd = new NpgsqlCommand(sql, pg);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    // ─────────────────────────────────────────────────────────────────────────
    // FK trigger management
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Enables or disables all triggers (including FK constraint triggers) on the given tables.
    /// Requires the current PG user to own the tables (which is the case when Jellyfin
    /// created the schema). Falls back gracefully per table if permission is denied.
    /// </summary>
    private async Task SetFkTriggersAsync(
        NpgsqlConnection pg, string schema, IEnumerable<string> tables, bool enable)
    {
        var action = enable ? "ENABLE" : "DISABLE";
        var tableList = tables.ToList();
        Log($"[Triggers] {action} TRIGGER ALL en {tableList.Count} tabla(s)...");
        var failed = 0;
        foreach (var table in tableList)
        {
            try
            {
                await using var cmd = new NpgsqlCommand(
                    $"ALTER TABLE {QuoteIdentifier(schema)}.{QuoteIdentifier(table)} {action} TRIGGER ALL;", pg);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (PostgresException ex)
            {
                failed++;
                _logger.LogWarning(
                    "No se pudo {Action} triggers en {Table}: {Error}", action, table, ex.MessageText);
            }
        }

        if (failed == 0)
            Log($"[Triggers] {action} TRIGGER ALL completado.");
        else
            Log($"[Triggers] {action} TRIGGER ALL: {failed} tabla(s) sin permiso (verificación de FK activa en esas tablas).");
    }

    private void Log(string message)
    {
        _logger.LogInformation("{Message}", message);
        lock (_logLock)
        {
            if (_logBuffer.Count >= MaxLogLines) _logBuffer.RemoveAt(0);
            _logBuffer.Add($"[{DateTime.UtcNow:HH:mm:ss}] {message}");
        }
    }

    private static string QuoteIdentifier(string input)
        => $"\"{input.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string QuoteSqliteIdentifier(string input)
        => $"\"{input.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private sealed record TargetColumn(string Name, string PgType, int? MaxLength = null);
}
