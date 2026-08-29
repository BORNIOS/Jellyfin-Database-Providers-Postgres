// Controller endpoints follow ASP.NET conventions — parameter doc is implicit via model bindings.
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Postgres;
using Jellyfin.Database.Providers.Postgres.Controllers.Models;
using Jellyfin.Database.Providers.Postgres.Services;
using Jellyfin.Database.Providers.Postgres.Services.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Database.Providers.Postgres.Controllers;

/// <summary>
/// REST API for the PostgreSQL database provider plugin.
/// Endpoints are consumed by the in-dashboard configuration page.
/// All endpoints require administrator authentication.
/// </summary>
[ApiController]
[Route("Plugins/PostgresProvider")]
[Authorize(Policy = "RequiresElevation")]
public class PostgresController : ControllerBase
{
    private const string DefaultSchema = "public";

    private readonly IApplicationPaths _appPaths;
    private readonly ISystemManager _systemManager;
    private readonly MigrationService _migrationService;
    private readonly MaintenanceService _maintenanceService;
    private readonly InstantSearchService _instantSearch;
    private readonly ExportToSqliteService _exportService;
    private readonly ILogger<PostgresController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresController"/> class.
    /// </summary>
    /// <param name="appPaths">Jellyfin application paths.</param>
    /// <param name="systemManager">Jellyfin system manager used to restart the server.</param>
    /// <param name="migrationService">Migration service instance.</param>
    /// <param name="maintenanceService">Maintenance service instance.</param>
    /// <param name="instantSearch">Instant search service instance.</param>
    /// <param name="exportService">Export service instance.</param>
    /// <param name="logger">Logger instance.</param>
    public PostgresController(
        IApplicationPaths appPaths,
        ISystemManager systemManager,
        MigrationService migrationService,
        MaintenanceService maintenanceService,
        InstantSearchService instantSearch,
        ExportToSqliteService exportService,
        ILogger<PostgresController> logger)
    {
        _appPaths = appPaths;
        _systemManager = systemManager;
        _migrationService = migrationService;
        _maintenanceService = maintenanceService;
        _instantSearch = instantSearch;
        _exportService = exportService;
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Status
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Returns the plugin configuration and activation status.</summary>
    /// <returns>Plugin configuration and activation status object.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetStatus()
    {
        var isActive = PostgresPlugin.IsPostgresActive(_appPaths);
        var activeConn = PostgresPlugin.ReadActivePgConnectionString(_appPaths);
        var config = PostgresPlugin.Instance?.Configuration;

        var sqliteDefault = Path.Combine(_appPaths.DataPath, "jellyfin.db");
        var sqliteExists = System.IO.File.Exists(sqliteDefault);
        var defaultBackupDir = Path.Combine(_appPaths.DataPath, "postgres-backups");

        return Ok(new
        {
            IsPostgresActive = isActive,
            ActiveConnectionString = isActive ? MaskPassword(activeConn) : null,
            SqliteDefaultPath = sqliteDefault,
            SqliteExists = sqliteExists,
            SqliteSize = sqliteExists
                ? FormatBytes(new FileInfo(sqliteDefault).Length)
                : null,
            SavedConnectionString = config?.ConnectionString,
            SavedSchema = config?.Schema ?? DefaultSchema,
            SavedCommandTimeout = config?.CommandTimeout ?? 60,
            SavedBackupDirectory = string.IsNullOrWhiteSpace(config?.BackupDirectory)
                ? defaultBackupDir
                : config?.BackupDirectory ?? defaultBackupDir,
            SavedBackupCompression = config?.BackupCompression ?? true,
            SavedPgBinPath = config?.PgBinPath ?? string.Empty,
            DefaultBackupDirectory = defaultBackupDir,
            MigrationState = config?.MigrationState.ToString() ?? "NotStarted",
            LastMigrationError = config?.LastMigrationError,
            MigrationCompletedAt = config?.MigrationCompletedAt
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Connection test
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Tests a PostgreSQL connection string.</summary>
    /// <param name="request">Request body containing the connection string to test.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Object with <c>Success</c> and optional <c>Error</c> fields.</returns>
    [HttpPost("TestConnection")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> TestConnection(
        [FromBody] ConnectionRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ConnectionString))
        {
            return BadRequest(new { Success = false, Error = "Connection string is required." });
        }

        var error = await MaintenanceService.TestConnectionAsync(request.ConnectionString, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return Ok(new { Success = false, Error = error });
        }

        try
        {
            var version = await MaintenanceService.GetServerVersionAsync(request.ConnectionString, cancellationToken)
                .ConfigureAwait(false);
            return Ok(new { Success = true, Version = version });
        }
        catch (Exception ex)
        {
            return Ok(new { Success = false, Error = ex.Message });
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Save configuration
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Saves the connection string and options to the plugin configuration.</summary>
    /// <param name="request">Request body containing connection string and plugin options.</param>
    /// <returns>Object confirming the configuration was saved.</returns>
    [HttpPost("SaveConfig")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> SaveConfig([FromBody] SaveConfigRequest request)
    {
        var plugin = PostgresPlugin.Instance;
        if (plugin is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { Error = "Plugin not loaded." });
        }

        plugin.Configuration.ConnectionString = request.ConnectionString;
        plugin.Configuration.CommandTimeout = request.CommandTimeout;
        if (request.BackupDirectory is not null)
        {
            plugin.Configuration.BackupDirectory = request.BackupDirectory;
        }

        plugin.Configuration.BackupCompression = request.BackupCompression;

        if (request.PgBinPath is not null)
        {
            plugin.Configuration.PgBinPath = request.PgBinPath;
        }

        plugin.SaveConfiguration();

        return Ok(new { Success = true });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Migration
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Starts the SQLite → PostgreSQL migration in the background.</summary>
    /// <param name="request">Request body with SQLite path, connection string and migration options.</param>
    /// <returns>202 Accepted if started, 409 Conflict if already running.</returns>
    [HttpPost("StartMigration")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<object> StartMigration([FromBody] StartMigrationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SqlitePath))
        {
            request.SqlitePath = Path.Combine(_appPaths.DataPath, "jellyfin.db");
        }

        // Canonicalize before checking existence to prevent path traversal (CA3003).
        var safeSqlitePath = Path.GetFullPath(request.SqlitePath);
        if (!FileExistsAtValidatedPath(safeSqlitePath))
        {
            return BadRequest(new { Error = $"SQLite file not found: {safeSqlitePath}" });
        }

        request.SqlitePath = safeSqlitePath;

        if (string.IsNullOrWhiteSpace(request.ConnectionString))
        {
            return BadRequest(new { Error = "ConnectionString is required." });
        }

        var started = _migrationService.StartMigration(
            sqlitePath: request.SqlitePath,
            postgresConnectionString: request.ConnectionString,
            schema: request.Schema,
            batchSize: request.BatchSize,
            truncate: request.Truncate,
            applicationPaths: _appPaths);

        if (!started)
        {
            return Conflict(new { Error = "A migration is already in progress." });
        }

        // Save connection string to plugin config so the UI can pre-populate it
        var plugin = PostgresPlugin.Instance;
        if (plugin is not null)
        {
            plugin.Configuration.ConnectionString = request.ConnectionString;
            plugin.Configuration.Schema = request.Schema;
            plugin.Configuration.MigrationState = MigrationState.InProgress;
            plugin.Configuration.LastMigrationError = null;
            plugin.SaveConfiguration();
        }

        return Accepted(new { Status = "Migration started." });
    }

    /// <summary>Polls the current migration progress.</summary>
    /// <returns>Current migration progress object.</returns>
    [HttpGet("MigrationStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetMigrationStatus()
    {
        var progress = _migrationService.GetProgress();

        // Persist final state to plugin config
        var plugin = PostgresPlugin.Instance;
        if (plugin is not null && !progress.IsRunning)
        {
            if (progress.IsCompleted && plugin.Configuration.MigrationState != MigrationState.Completed)
            {
                plugin.Configuration.MigrationState = MigrationState.Completed;
                plugin.Configuration.MigratedRows = progress.MigratedRows;
                plugin.Configuration.MigrationCompletedAt = DateTime.UtcNow;
                plugin.Configuration.LastMigrationError = null;
                plugin.SaveConfiguration();
            }
            else if (progress.HasError && plugin.Configuration.MigrationState != MigrationState.Failed)
            {
                plugin.Configuration.MigrationState = MigrationState.Failed;
                plugin.Configuration.LastMigrationError = progress.ErrorMessage;
                plugin.SaveConfiguration();
            }
        }

        return Ok(new
        {
            IsRunning = progress.IsRunning,
            IsCompleted = progress.IsCompleted,
            HasError = progress.HasError,
            ErrorMessage = progress.ErrorMessage,
            PercentComplete = progress.PercentComplete,
            CurrentTable = progress.CurrentTable,
            MigratedRows = progress.MigratedRows,
            LogLines = progress.LogLines
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Activate / Switch to PostgreSQL
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes <c>database.xml</c> to activate the PostgreSQL provider and restarts Jellyfin.
    /// This is the final step after migration completes.
    /// </summary>
    /// <param name="request">Request body containing the connection string to activate.</param>
    /// <returns>202 Accepted if the provider was activated and server restart was requested.</returns>
    [HttpPost("Activate")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public ActionResult<object> Activate([FromBody] ActivateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ConnectionString))
        {
            return BadRequest(new { Error = "ConnectionString is required." });
        }

        _logger.LogInformation(
            "Activating PostgreSQL provider. Writing database.xml and scheduling restart...");

        try
        {
            PostgresPlugin.WriteDatabaseXml(_appPaths, request.ConnectionString, request.CommandTimeout);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write database.xml");
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { Error = $"Failed to write database.xml: {ex.Message}" });
        }

        // Update plugin config to reflect active state
        var plugin = PostgresPlugin.Instance;
        if (plugin is not null)
        {
            plugin.Configuration.ConnectionString = request.ConnectionString;
            plugin.Configuration.CommandTimeout = request.CommandTimeout;
            plugin.SaveConfiguration();
        }

        // Schedule restart after response is sent
        _ = Task.Run(async () =>
        {
            await Task.Delay(500).ConfigureAwait(false);
            _systemManager.Restart();
        });

        return Accepted(new
        {
            Status = "PostgreSQL activated. Jellyfin is restarting..."
        });
    }

    /// <summary>
    /// Reverts <c>database.xml</c> to use SQLite and restarts Jellyfin.
    /// Use this to roll back if PostgreSQL causes issues.
    /// </summary>
    /// <returns>202 Accepted after removing the database configuration and requesting a restart.</returns>
    [HttpPost("Deactivate")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public ActionResult<object> Deactivate()
    {
        var configPath = Path.Combine(_appPaths.ConfigurationDirectoryPath, "database.xml");
        if (System.IO.File.Exists(configPath))
        {
            System.IO.File.Delete(configPath);
            _logger.LogInformation("database.xml deleted — Jellyfin will use SQLite on next start.");

            // Log the engine switch prominently so it appears in the plugin log
            var sqlitePath = Services.ExportToSqliteService.DetectDefaultSqlitePath(_appPaths.DataPath);
            var sqliteSize = System.IO.File.Exists(sqlitePath)
                ? $"{new System.IO.FileInfo(sqlitePath).Length / 1_048_576.0:F1} MB"
                : "(file not found)";

            Logging.PostgresLog.Warn("[ENGINE SWITCH] PostgreSQL → SQLite");
            Logging.PostgresLog.Warn($"[ENGINE SWITCH] database.xml eliminado — Jellyfin usará SQLite al reiniciar.");
            Logging.PostgresLog.Warn($"[ENGINE SWITCH] SQLite destino: {sqlitePath} ({sqliteSize})");
            Logging.PostgresLog.Warn("[ENGINE SWITCH] Reiniciando Jellyfin...");
        }
        else
        {
            Logging.PostgresLog.Warn("[ENGINE SWITCH] Deactivate llamado pero database.xml no existía.");
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(500).ConfigureAwait(false);
            _systemManager.Restart();
        });

        return Accepted(new { Status = "Reverted to SQLite. Jellyfin is restarting..." });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Maintenance
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Truncates all Jellyfin data tables in PostgreSQL (RESTART IDENTITY CASCADE).
    /// Use before a fresh migration from SQLite to avoid PK conflicts.
    /// The database schema is preserved — only data is removed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 OK with row counts per table before truncation.</returns>
    [HttpPost("TruncateAllTables")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> TruncateAllTables(CancellationToken cancellationToken)
    {
        var connStr = GetActiveConnectionString();
        if (connStr is null)
        {
            return NoActiveConnection();
        }

        try
        {
            var result = await MaintenanceService.TruncateAllTablesAsync(connStr, cancellationToken)
                .ConfigureAwait(false);
            Logging.PostgresLog.Warn($"[Maintenance] TruncateAllTables: {result.TablesAffected} tablas truncadas.");
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TruncateAllTables failed");
            return StatusCode(StatusCodes.Status500InternalServerError, new { Error = ex.Message });
        }
    }

    /// <summary>Returns table size statistics for the PostgreSQL database.</summary>
    /// <param name="schema">PostgreSQL schema to query. Defaults to the configured schema.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Object with database size, active connections and per-table statistics.</returns>
    [HttpGet("Stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> GetStats(
        [FromQuery] string? schema,
        CancellationToken cancellationToken)
    {
        var connStr = GetActiveConnectionString();
        if (connStr is null)
        {
            return NoActiveConnection();
        }

        var effectiveSchema = schema ?? PostgresPlugin.Instance?.Configuration.Schema ?? DefaultSchema;
        var tables = await MaintenanceService.GetTableStatsAsync(connStr, effectiveSchema, cancellationToken)
            .ConfigureAwait(false);
        var dbSize = await MaintenanceService.GetDatabaseSizeAsync(connStr, cancellationToken)
            .ConfigureAwait(false);
        var connCount = await MaintenanceService.GetConnectionCountAsync(connStr, cancellationToken)
            .ConfigureAwait(false);

        Logging.PostgresLog.Warn(
            $"[Stats] BD: {dbSize} | Conexiones activas: {connCount} | Tablas: {tables.Count} | Schema: {effectiveSchema}");

        return Ok(new
        {
            DatabaseSize = dbSize,
            ActiveConnections = connCount,
            Tables = tables
        });
    }

    /// <summary>Runs VACUUM ANALYZE on the PostgreSQL database.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 OK with duration once the operation completes.</returns>
    [HttpPost("Vacuum")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> Vacuum(CancellationToken cancellationToken)
    {
        var connStr = GetActiveConnectionString();
        if (connStr is null)
        {
            return NoActiveConnection();
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _maintenanceService.VacuumAnalyzeAsync(connStr, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            var msg = $"VACUUM ANALYZE completado en {sw.Elapsed.TotalSeconds:F1}s.";
            return Ok(new { Status = msg, DurationSeconds = sw.Elapsed.TotalSeconds });
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "VACUUM ANALYZE failed");
            return StatusCode(StatusCodes.Status500InternalServerError, new { Error = ex.Message });
        }
    }

    /// <summary>Runs REINDEX DATABASE on the PostgreSQL database.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 OK with duration once the operation completes.</returns>
    [HttpPost("Reindex")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> Reindex(CancellationToken cancellationToken)
    {
        var connStr = GetActiveConnectionString();
        if (connStr is null)
        {
            return NoActiveConnection();
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _maintenanceService.ReindexAsync(connStr, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            var msg = $"REINDEX DATABASE completado en {sw.Elapsed.TotalSeconds:F1}s.";
            return Ok(new { Status = msg, DurationSeconds = sw.Elapsed.TotalSeconds });
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "REINDEX DATABASE failed");
            return StatusCode(StatusCodes.Status500InternalServerError, new { Error = ex.Message });
        }
    }

    /// <summary>
    /// Runs 5 PostgreSQL health diagnostics in parallel and returns a structured
    /// report with a severity semaphore (Ok / Warn / Error).
    /// </summary>
    /// <param name="healthCheckService">Health check service instance.</param>
    /// <param name="schema">PostgreSQL schema to inspect. Defaults to the configured schema.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Object with overall severity, findings and duration.</returns>
    [HttpGet("HealthCheck")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> GetHealthCheck(
        [FromServices] HealthCheckService healthCheckService,
        [FromQuery] string? schema,
        CancellationToken cancellationToken)
    {
        var connStr = GetActiveConnectionString();
        if (connStr is null)
        {
            return NoActiveConnection();
        }

        var effectiveSchema = schema ?? PostgresPlugin.Instance?.Configuration.Schema ?? DefaultSchema;
        var result = await healthCheckService
            .RunHealthCheckAsync(connStr, effectiveSchema, silent: false, cancellationToken).ConfigureAwait(false);

        return Ok(result);
    }

    /// <summary>
    /// Auto-repairs the issues detected by <see cref="GetHealthCheck"/>:
    /// rebuilds invalid indexes (REINDEX CONCURRENTLY), vacuums bloated tables
    /// and fixes out-of-sync sequences. Idempotent and safe to call repeatedly.
    /// </summary>
    /// <param name="healthCheckService">Health check service instance.</param>
    /// <param name="schema">PostgreSQL schema to inspect. Defaults to the configured schema.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Object with per-action repair results and overall success.</returns>
    [HttpPost("HealthCheck/Repair")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> RepairHealthCheck(
        [FromServices] HealthCheckService healthCheckService,
        [FromQuery] string? schema,
        CancellationToken cancellationToken)
    {
        var connStr = GetActiveConnectionString();
        if (connStr is null)
        {
            return NoActiveConnection();
        }

        var effectiveSchema = schema ?? PostgresPlugin.Instance?.Configuration.Schema ?? DefaultSchema;
        var result = await healthCheckService
            .RepairAsync(connStr, effectiveSchema, cancellationToken).ConfigureAwait(false);

        return Ok(result);
    }

    /// <summary>Creates a PostgreSQL backup file using pg_dump.</summary>
    /// <param name="request">Request body with output directory and optional pg_dump path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Object with the backup file path on success.</returns>
    [HttpPost("Backup")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> Backup([FromBody] BackupRequest? request, CancellationToken cancellationToken)
    {
        var connStr = GetActiveConnectionString();
        if (connStr is null)
        {
            return NoActiveConnection();
        }

        var config = PostgresPlugin.Instance?.Configuration;
        var outputDir = request?.OutputDirectory;
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            outputDir = config?.BackupDirectory;
        }

        if (string.IsNullOrWhiteSpace(outputDir))
        {
            outputDir = Path.Combine(_appPaths.DataPath, "postgres-backups");
        }

        var compress = request?.Compress ?? config?.BackupCompression ?? true;
        var pgBinPath = string.IsNullOrWhiteSpace(request?.PgBinPath)
            ? config?.PgBinPath
            : request?.PgBinPath;

        try
        {
            var backupPath = await _maintenanceService
                .CreateBackupAsync(connStr, outputDir, compress, pgBinPath, cancellationToken)
                .ConfigureAwait(false);

            var backupSize = GetFileSizeBytes(backupPath);
            return Ok(new
            {
                Success = true,
                BackupPath = backupPath,
                Compressed = compress,
                SizeBytes = backupSize,
                SizeFormatted = MaintenanceService.FormatBytes(backupSize)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Backup operation failed");
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                Success = false,
                Error = ex.Message
            });
        }
    }

    /// <summary>
    /// Lists all backup files (.sql, .zip) found in the configured backup directory, newest first.
    /// If <paramref name="directory"/> is provided it overrides the saved configuration.
    /// </summary>
    /// <param name="directory">Optional directory override. Defaults to the saved BackupDirectory or the data path.</param>
    /// <returns>Array of backup file metadata objects.</returns>
    [HttpGet("ListBackups")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> ListBackups([FromQuery] string? directory = null)
    {
        var config = PostgresPlugin.Instance?.Configuration;
        var dir = directory;

        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = config?.BackupDirectory;
        }

        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = Path.Combine(_appPaths.DataPath, "postgres-backups");
        }

        var files = MaintenanceService.ListBackups(dir);
        return Ok(new
        {
            Directory = dir,
            Files = files
        });
    }

    /// <summary>Restores a PostgreSQL backup file (.sql or .zip) using psql.</summary>
    /// <param name="request">Request body with backup file path and restore options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Object with the restored SQL file path on success.</returns>
    [HttpPost("RestoreBackup")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> RestoreBackup([FromBody] RestoreBackupRequest request, CancellationToken cancellationToken)
    {
        var connStr = GetActiveConnectionString();
        if (connStr is null)
        {
            return NoActiveConnection();
        }

        if (string.IsNullOrWhiteSpace(request.BackupPath))
        {
            return BadRequest(new { Success = false, Error = "BackupPath is required." });
        }

        var config = PostgresPlugin.Instance?.Configuration;
        var pgBinPath = string.IsNullOrWhiteSpace(request.PgBinPath)
            ? config?.PgBinPath
            : request.PgBinPath;
        var replaceExistingObjects = request.ReplaceExistingObjects ?? true;

        try
        {
            var restoredFrom = await _maintenanceService
                .RestoreBackupAsync(connStr, request.BackupPath, pgBinPath, replaceExistingObjects, cancellationToken)
                .ConfigureAwait(false);

            return Ok(new
            {
                Success = true,
                RestoredFrom = restoredFrom
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restore backup operation failed");
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                Success = false,
                Error = ex.Message
            });
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PostgreSQL → SQLite Export
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Checks whether a file already exists at the resolved export target path.
    /// The UI uses this to decide whether to show the overwrite/backup dialog.
    /// </summary>
    /// <param name="path">Optional path; defaults to the auto-detected jellyfin.db path.</param>
    /// <returns>Object with <c>Exists</c> boolean and the resolved <c>Path</c>.</returns>
    [HttpGet("CheckExportTarget")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> CheckExportTarget([FromQuery] string? path)
    {
        var rawPath = string.IsNullOrWhiteSpace(path)
            ? ExportToSqliteService.DetectDefaultSqlitePath(_appPaths.DataPath)
            : path;
        var resolved = ResolveExportFilePath(rawPath);
        return Ok(new { Exists = ValidatedFileExists(resolved), Path = resolved });
    }

    /// <summary>
    /// Starts a PostgreSQL → SQLite export in the background.
    /// Writes all data directly into a SQLite <c>.db</c> file.
    /// Returns immediately; poll <see cref="GetExportToSqliteProgress"/> for status.
    /// </summary>
    /// <param name="request">Request body with optional target SQLite path.</param>
    /// <returns>202 Accepted if started; 409 Conflict if already running.</returns>
    [HttpPost("StartExportToSqlite")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<object> StartExportToSqlite([FromBody] StartExportToSqliteRequest? request)
    {
        var connStr = GetActiveConnectionString();
        if (connStr is null)
        {
            return NoActiveConnection();
        }

        var rawPath = string.IsNullOrWhiteSpace(request?.TargetSqlitePath)
            ? ExportToSqliteService.DetectDefaultSqlitePath(_appPaths.DataPath)
            : request?.TargetSqlitePath ?? ExportToSqliteService.DetectDefaultSqlitePath(_appPaths.DataPath);

        // Resolve final .db file path: if the user gave a directory, append jellyfin.db.
        var sqlitePath = ResolveExportFilePath(rawPath);

        var mode = (request?.OverwriteMode ?? "use").ToLowerInvariant();

        if (ValidatedFileExists(sqlitePath))
        {
            if (mode == "backup")
            {
                var bkpPath = sqlitePath + ".bkp";
                if (ValidatedFileExists(bkpPath))
                {
                    ValidatedFileDelete(bkpPath);
                }

                ValidatedFileMove(sqlitePath, bkpPath);
                _logger.LogInformation("Existing SQLite file renamed to {Bkp}", bkpPath);
            }
            else if (mode == "overwrite")
            {
                ValidatedFileDelete(sqlitePath);
                _logger.LogInformation("Existing SQLite file deleted for overwrite: {Path}", sqlitePath);
            }

            // mode == "use": keep as-is
        }

        // If the file still doesn't exist after backup/overwrite handling, seed the schema.
        // Priority: 1) .bkp just made  2) native jellyfin.db  3) EF Core Migrations (last resort)
        if (!ValidatedFileExists(sqlitePath))
        {
            var seeded = TrySeedSqliteSchema(sqlitePath, _appPaths.DataPath, (ILogger)_logger);
            if (!seeded)
            {
                return BadRequest(new
                {
                    Error = "No se pudo crear el schema SQLite. " +
                            "Asegúrate de que exista un jellyfin.db válido como fuente del schema."
                });
            }
        }

        var started = _exportService.StartExport(connStr, sqlitePath);
        if (!started)
        {
            return Conflict(new { Error = "An export is already in progress." });
        }

        return Accepted(new { Status = "Export started.", TargetSqlitePath = sqlitePath });
    }

    // CA3003: paths validated/canonicalized before reaching these helpers.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3003", Justification = "Paths are canonicalized with Path.GetFullPath before use.")]
    private static bool ValidatedFileExists(string path) => System.IO.File.Exists(path);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3003", Justification = "Paths are canonicalized with Path.GetFullPath before use.")]
    private static void ValidatedFileDelete(string path) => System.IO.File.Delete(path);

    /// <summary>Returns the auto-detected SQLite .db path for the current Jellyfin installation.</summary>
    /// <returns>Object with the detected <c>Path</c> string.</returns>
    [HttpGet("ExportToSqliteDefaultPath")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetExportToSqliteDefaultPath()
        => Ok(new { Path = ExportToSqliteService.DetectDefaultSqlitePath(_appPaths.DataPath) });

    /// <summary>Returns the progress of the current or last PostgreSQL → SQLite export.</summary>
    /// <returns>Current export progress object.</returns>
    [HttpGet("ExportToSqliteProgress")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetExportToSqliteProgress()
    {
        var p = _exportService.GetProgress();
        return Ok(new
        {
            p.IsRunning,
            p.IsCompleted,
            p.HasError,
            p.ErrorMessage,
            p.PercentComplete,
            p.CurrentTable,
            p.ExportedRows,
            p.TotalRows,
            p.TargetSqlitePath,
            p.LogLines,
        });
    }

    // ─────────────────────────────────────────────────────────────────────────    // Instant Search (Spotify-style)
    // ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lightweight instant-search endpoint designed for as-you-type UX.
    /// Returns a minimal JSON array ranked by pg_trgm word similarity.
    /// Bypasses EF Core entirely — direct Npgsql query, target latency &lt; 15 ms.
    /// </summary>
    /// <param name="term">The user-typed search term.</param>
    /// <param name="limit">Maximum results to return (1-50, default 8).</param>
    /// <param name="mediaTypes">Optional comma-separated Jellyfin MediaType filter (e.g. "Audio,Video").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Array of ranked search result objects.</returns>
    [HttpGet("Search/Instant")]
    [AllowAnonymous] // Search must work without re-auth; access is scoped to the DB user's data
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<object>> InstantSearch(
        [FromQuery] string? term,
        [FromQuery] int limit = 8,
        [FromQuery] string? mediaTypes = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return Ok(Array.Empty<object>());
        }

        var connStr = GetActiveConnectionString();
        if (connStr is null)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { Error = "PostgreSQL provider is not active." });
        }

        var results = await _instantSearch.SearchAsync(term, connStr, limit, mediaTypes, cancellationToken)
            .ConfigureAwait(false);

        // Return compact JSON — clients use this for as-you-type suggestions
        return Ok(results.ConvertAll(r => new
        {
            id = r.Id,
            name = r.Name,
            type = r.Type,
            year = r.Year,
            artists = r.Artists,
            album = r.Album,
            series = r.SeriesName,
            relevance = Math.Round(r.Relevance, 3),
        }));
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Performance Optimizations
    // ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the current state of performance optimizations:
    /// whether pg_trgm is available and which GIN indexes exist.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Object with GIN index list and optimization flags.</returns>
    [HttpGet("OptimizationStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> GetOptimizationStatus(CancellationToken cancellationToken)
    {
        var connStr = GetActiveConnectionString();
        if (connStr is null)
        {
            return NoActiveConnection();
        }

        var config = PostgresPlugin.Instance?.Configuration;
        var indexList = await MaintenanceService
            .GetGinIndexStatusAsync(connStr, cancellationToken).ConfigureAwait(false);

        return Ok(new
        {
            TrgmAvailable = PostgresDatabaseProvider.TrgmAvailable,
            GinIndexes = indexList,
            SearchOptimizations = config?.EnableSearchOptimizations ?? true,
            AutovacuumTuning = config?.EnableAutovacuumTuning ?? true,
            PoolMin = config?.MinPoolSize ?? 4,
            PoolMax = config?.MaxPoolSize ?? 100,
            MaxAutoPrepare = config?.MaxAutoPrepare ?? 50,
        });
    }

    /// <summary>
    /// Manually triggers GIN index creation and autovacuum tuning.
    /// Safe to call multiple times — all statements are idempotent.
    /// The work runs in the background; this endpoint returns immediately.
    /// </summary>
    /// <returns>202 Accepted — the optimization job runs in the background.</returns>
    [HttpPost("ApplyOptimizations")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public ActionResult<object> ApplyOptimizations()
    {
        var connStr = GetActiveConnectionString();
        if (connStr is null)
        {
            return NoActiveConnection();
        }

        var config = PostgresPlugin.Instance?.Configuration;
        var enableSearch = config?.EnableSearchOptimizations ?? true;
        var enableVacuum = config?.EnableAutovacuumTuning ?? true;

        _ = Task.Run(async () =>
        {
            await PostgresDatabaseProvider.RunOptimizationsAsync(
                connStr, enableSearch, enableVacuum, _logger, CancellationToken.None)
                .ConfigureAwait(false);
        });

        return Accepted(new { Status = "Optimization job started in background." });
    }

    // ───────────────────────────────────────────────────────────────────────────    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private string? GetActiveConnectionString()
    {
        var active = PostgresPlugin.ReadActivePgConnectionString(_appPaths);
        if (!string.IsNullOrWhiteSpace(active))
        {
            return active;
        }

        var configured = PostgresPlugin.Instance?.Configuration.ConnectionString;
        return string.IsNullOrWhiteSpace(configured) ? null : configured;
    }

    private ObjectResult NoActiveConnection()
        => StatusCode(
            StatusCodes.Status409Conflict,
            new { Error = "PostgreSQL is not active. Activate it first or provide a connection string." });

    /// <summary>Masks the password in a connection string so it's safe to return to the UI.</summary>
    private static string? MaskPassword(string? connectionString)
    {
        if (connectionString is null)
        {
            return null;
        }

        // Replace Password=... (up to next semicolon or end) with Password=*****
        return System.Text.RegularExpressions.Regex.Replace(
            connectionString,
            @"(?i)(Password\s*=)[^;]+",
            "$1*****");
    }

    // backupPath is built internally from a trusted output directory + timestamp — not user input (CA3003).
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "backupPath is constructed by CreateBackupAsync from a server-controlled directory and timestamp, not from HTTP input.")]
    private static long GetFileSizeBytes(string backupPath)
        => new System.IO.FileInfo(backupPath).Length;

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824)
        {
            return $"{bytes / 1_073_741_824.0:F1} GB";
        }

        if (bytes >= 1_048_576)
        {
            return $"{bytes / 1_048_576.0:F1} MB";
        }

        if (bytes >= 1024)
        {
            return $"{bytes / 1024.0:F1} KB";
        }

        return $"{bytes} B";
    }

    // CA3003: path has been canonicalized via Path.GetFullPath before arriving here.
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "validatedPath has been canonicalized with Path.GetFullPath before this call. Not raw user input.")]
    private static bool FileExistsAtValidatedPath(string validatedPath)
        => System.IO.File.Exists(validatedPath);

    [SuppressMessage("Security", "CA3003", Justification = "Paths are canonicalized with Path.GetFullPath before use.")]
    private static void ValidatedFileMove(string source, string dest) => System.IO.File.Move(source, dest);

    /// <summary>
    /// Creates a fresh SQLite database at <paramref name="sqlitePath"/> with the full
    /// Jellyfin schema (including FK constraints) by running EF Core Migrations against
    /// the SQLite provider. This guarantees structural correctness regardless of whether
    /// an existing <c>jellyfin.db</c> is available.
    /// </summary>
    /// <summary>
    /// Creates a fresh SQLite database at <paramref name="sqlitePath"/> by running the official
    /// Jellyfin SQLite provider migrations. This produces the exact same schema that Jellyfin
    /// would create natively, including all tables and FK constraints.
    /// </summary>
    private bool TrySeedSqliteSchema(string sqlitePath, string dataPath, ILogger logger)
    {
        _ = dataPath; // reserved for future fallback
        try
        {
            CreateSqliteSchemaViaEfCore(sqlitePath);
            logger.LogInformation("SQLite schema created via Jellyfin SQLite provider at {Path}", sqlitePath);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create SQLite schema at {Path}", sqlitePath);
            return false;
        }
    }

    [SuppressMessage("Security", "CA3003", Justification = "sqlitePath is canonicalized with Path.GetFullPath before this call.")]
    private void CreateSqliteSchemaViaEfCore(string sqlitePath)
    {
        var dir = Path.GetDirectoryName(sqlitePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Locate Jellyfin.Database.Providers.Sqlite.dll.
        // Candidates in priority order: next to jellyfin.exe, or next to jellyfin.dll.
        var candidates = new[]
        {
            Path.Combine(_appPaths.ProgramSystemPath, "Jellyfin.Database.Providers.Sqlite.dll"),
            Path.Combine(
                Path.GetDirectoryName(
                    AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => a.GetName().Name == "Jellyfin.Database.Implementations")?.Location
                    ?? string.Empty) ?? string.Empty,
                "Jellyfin.Database.Providers.Sqlite.dll"),
        };

        var sqliteDllPath = candidates.FirstOrDefault(System.IO.File.Exists)
            ?? throw new FileNotFoundException(
                "Jellyfin.Database.Providers.Sqlite.dll not found. " +
                $"Searched: {string.Join(", ", candidates)}");

        _logger.LogInformation("Loading SQLite provider from: {Path}", sqliteDllPath);

        // Load into the default ALC — all EF Core dependencies are already present in the server.
        var sqliteAsm = System.Runtime.Loader.AssemblyLoadContext.Default
            .LoadFromAssemblyPath(sqliteDllPath);

        var providerType = sqliteAsm.GetType("Jellyfin.Database.Providers.Sqlite.SqliteDatabaseProvider")
            ?? throw new InvalidOperationException("SqliteDatabaseProvider type not found in loaded assembly.");

        // Build ILogger<SqliteDatabaseProvider> via NullLoggerFactory so the generic type matches exactly.
        using var loggerFactory = new NullLoggerFactory();
        var createLoggerMethod = typeof(LoggerFactoryExtensions)
            .GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)
            .First(m => m.Name == "CreateLogger" && m.IsGenericMethod)
            .MakeGenericMethod(providerType);
        var typedLogger = createLoggerMethod.Invoke(null, new object[] { loggerFactory })!;

        var provider = (IJellyfinDatabaseProvider)Activator.CreateInstance(
            providerType,
            _appPaths,
            typedLogger)!;

        var optionsBuilder = new DbContextOptionsBuilder<JellyfinDbContext>()
            .UseSqlite(
                $"Data Source={sqlitePath}",
                opts => opts.MigrationsAssembly(sqliteAsm.GetName().Name));

        using var ctx = new JellyfinDbContext(
            optionsBuilder.Options,
            NullLogger<JellyfinDbContext>.Instance,
            provider,
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));

        ctx.Database.Migrate();
    }

    /// <summary>
    /// Resolves the final export <c>.db</c> file path from a user-supplied value.
    /// If the resolved path is a directory, appends <c>jellyfin.db</c>.
    /// If it has no extension, appends <c>.db</c>.
    /// </summary>
    [SuppressMessage("Security", "CA3003", Justification = "Path is canonicalized with Path.GetFullPath; Directory.Exists operates on the resolved value, not raw user input.")]
    private static string ResolveExportFilePath(string rawPath)
    {
        var full = Path.GetFullPath(rawPath);

        // User gave a directory path → use jellyfin.db inside it
        if (Directory.Exists(full))
        {
            return Path.Combine(full, "jellyfin.db");
        }

        // User gave a path without extension → add .db
        if (string.IsNullOrEmpty(Path.GetExtension(full)))
        {
            return full + ".db";
        }

        return full;
    }
}
