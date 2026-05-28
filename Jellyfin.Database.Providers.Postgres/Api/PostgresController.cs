using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Providers.Postgres.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Database.Providers.Postgres.Api;

/// <summary>Request body for starting a migration.</summary>
public sealed class StartMigrationRequest
{
    /// <summary>Gets or sets the path to the SQLite database file.</summary>
    public string SqlitePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the PostgreSQL connection string to migrate to.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Gets or sets the target schema (default "public").</summary>
    public string Schema { get; set; } = "public";

    /// <summary>Gets or sets the insert batch size (default 1000).</summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>Gets or sets whether to TRUNCATE tables before inserting.</summary>
    public bool Truncate { get; set; }
}

/// <summary>Request body for testing / saving a connection string.</summary>
public sealed class ConnectionRequest
{
    /// <summary>Gets or sets the PostgreSQL connection string to test.</summary>
    public string ConnectionString { get; set; } = string.Empty;
}

/// <summary>Request body for activating PostgreSQL.</summary>
public sealed class ActivateRequest
{
    /// <summary>Gets or sets the connection string to write to database.xml.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Gets or sets the EF command timeout in seconds.</summary>
    public int CommandTimeout { get; set; } = 60;
}

/// <summary>Request body for saving plugin configuration.</summary>
public sealed class SaveConfigRequest
{
    /// <summary>Gets or sets the connection string to save.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Gets or sets the EF command timeout in seconds.</summary>
    public int CommandTimeout { get; set; } = 60;

    /// <summary>Gets or sets the backup output directory on the server.</summary>
    public string? BackupDirectory { get; set; }

    /// <summary>Gets or sets a value indicating whether backups should be zipped.</summary>
    public bool BackupCompression { get; set; } = true;

    /// <summary>Gets or sets optional explicit path to pg_dump executable.</summary>
    public string? PgDumpPath { get; set; }

    /// <summary>Gets or sets optional explicit path to psql executable for restore.</summary>
    public string? PgRestorePath { get; set; }
}

/// <summary>Request body for creating a backup.</summary>
public sealed class BackupRequest
{
    /// <summary>Gets or sets optional backup output directory override.</summary>
    public string? OutputDirectory { get; set; }

    /// <summary>Gets or sets optional compression override.</summary>
    public bool? Compress { get; set; }

    /// <summary>Gets or sets optional pg_dump path override.</summary>
    public string? PgDumpPath { get; set; }
}

/// <summary>Request body for restoring a backup.</summary>
public sealed class RestoreBackupRequest
{
    /// <summary>Gets or sets absolute path to .sql or .zip backup file.</summary>
    public string BackupPath { get; set; } = string.Empty;

    /// <summary>Gets or sets optional psql path override.</summary>
    public string? PgRestorePath { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether existing objects should be replaced before restore.
    /// Defaults to true for compatibility with plain SQL backups.
    /// </summary>
    public bool? ReplaceExistingObjects { get; set; }
}

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
    private readonly IApplicationPaths _appPaths;
    private readonly ISystemManager _systemManager;
    private readonly MigrationService _migrationService;
    private readonly MaintenanceService _maintenanceService;
    private readonly ILogger<PostgresController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="PostgresController"/>.
    /// </summary>
    public PostgresController(
        IApplicationPaths appPaths,
        ISystemManager systemManager,
        MigrationService migrationService,
        MaintenanceService maintenanceService,
        ILogger<PostgresController> logger)
    {
        _appPaths = appPaths;
        _systemManager = systemManager;
        _migrationService = migrationService;
        _maintenanceService = maintenanceService;
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Status
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Returns the plugin configuration and activation status.</summary>
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
            SavedSchema = config?.Schema ?? "public",
            SavedCommandTimeout = config?.CommandTimeout ?? 60,
            SavedBackupDirectory = string.IsNullOrWhiteSpace(config?.BackupDirectory)
                ? defaultBackupDir
                : config!.BackupDirectory,
            SavedBackupCompression = config?.BackupCompression ?? true,
            SavedPgDumpPath = config?.PgDumpPath ?? string.Empty,
            SavedPgRestorePath = config?.PgRestorePath ?? string.Empty,
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

        if (request.PgDumpPath is not null)
        {
            plugin.Configuration.PgDumpPath = request.PgDumpPath;
        }

        if (request.PgRestorePath is not null)
        {
            plugin.Configuration.PgRestorePath = request.PgRestorePath;
        }

        plugin.SaveConfiguration();

        return Ok(new { Success = true });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Migration
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Starts the SQLite → PostgreSQL migration in the background.</summary>
    [HttpPost("StartMigration")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<object> StartMigration([FromBody] StartMigrationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SqlitePath))
        {
            request.SqlitePath = Path.Combine(_appPaths.DataPath, "jellyfin.db");
        }

        if (!System.IO.File.Exists(request.SqlitePath))
        {
            return BadRequest(new { Error = $"SQLite file not found: {request.SqlitePath}" });
        }

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
            return StatusCode(StatusCodes.Status500InternalServerError,
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
    [HttpPost("Deactivate")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public ActionResult<object> Deactivate()
    {
        var configPath = Path.Combine(_appPaths.ConfigurationDirectoryPath, "database.xml");
        if (System.IO.File.Exists(configPath))
        {
            System.IO.File.Delete(configPath);
            _logger.LogInformation("database.xml deleted — Jellyfin will use SQLite on next start.");
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

    /// <summary>Returns table size statistics for the PostgreSQL database.</summary>
    [HttpGet("Stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> GetStats(
        [FromQuery] string? schema,
        CancellationToken cancellationToken)
    {
        var connStr = GetActiveConnectionString();
        if (connStr is null) return NoActiveConnection();

        var effectiveSchema = schema ?? PostgresPlugin.Instance?.Configuration.Schema ?? "public";
        var tables = await MaintenanceService.GetTableStatsAsync(connStr, effectiveSchema, cancellationToken)
            .ConfigureAwait(false);
        var dbSize = await MaintenanceService.GetDatabaseSizeAsync(connStr, cancellationToken)
            .ConfigureAwait(false);
        var connCount = await MaintenanceService.GetConnectionCountAsync(connStr, cancellationToken)
            .ConfigureAwait(false);

        return Ok(new
        {
            DatabaseSize = dbSize,
            ActiveConnections = connCount,
            Tables = tables
        });
    }

    /// <summary>Runs VACUUM ANALYZE on the PostgreSQL database.</summary>
    [HttpPost("Vacuum")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public ActionResult<object> Vacuum()
    {
        var connStr = GetActiveConnectionString();
        if (connStr is null) return NoActiveConnection();

        _ = Task.Run(async () =>
        {
            try
            {
                await _maintenanceService.VacuumAnalyzeAsync(connStr).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "VACUUM ANALYZE failed");
            }
        });

        return Accepted(new { Status = "VACUUM ANALYZE started in background." });
    }

    /// <summary>Runs REINDEX DATABASE on the PostgreSQL database.</summary>
    [HttpPost("Reindex")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public ActionResult<object> Reindex()
    {
        var connStr = GetActiveConnectionString();
        if (connStr is null) return NoActiveConnection();

        _ = Task.Run(async () =>
        {
            try
            {
                await _maintenanceService.ReindexAsync(connStr).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "REINDEX failed");
            }
        });

        return Accepted(new { Status = "REINDEX DATABASE started in background." });
    }

    /// <summary>Creates a PostgreSQL backup file using pg_dump.</summary>
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
        var pgDumpPath = string.IsNullOrWhiteSpace(request?.PgDumpPath)
            ? config?.PgDumpPath
            : request!.PgDumpPath;

        try
        {
            var backupPath = await _maintenanceService
                .CreateBackupAsync(connStr, outputDir, compress, pgDumpPath, cancellationToken)
                .ConfigureAwait(false);

            return Ok(new
            {
                Success = true,
                BackupPath = backupPath,
                Compressed = compress
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

    /// <summary>Restores a PostgreSQL backup file (.sql or .zip) using psql.</summary>
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
        var pgRestorePath = string.IsNullOrWhiteSpace(request.PgRestorePath)
            ? config?.PgRestorePath
            : request.PgRestorePath;
        var replaceExistingObjects = request.ReplaceExistingObjects ?? true;

        try
        {
            var restoredFrom = await _maintenanceService
                .RestoreBackupAsync(connStr, request.BackupPath, pgRestorePath, replaceExistingObjects, cancellationToken)
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
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private string? GetActiveConnectionString()
    {
        var active = PostgresPlugin.ReadActivePgConnectionString(_appPaths);
        if (!string.IsNullOrWhiteSpace(active)) return active;
        var configured = PostgresPlugin.Instance?.Configuration.ConnectionString;
        return string.IsNullOrWhiteSpace(configured) ? null : configured;
    }

    private ObjectResult NoActiveConnection()
        => StatusCode(StatusCodes.Status409Conflict,
            new { Error = "PostgreSQL is not active. Activate it first or provide a connection string." });

    /// <summary>Masks the password in a connection string so it's safe to return to the UI.</summary>
    private static string? MaskPassword(string? connectionString)
    {
        if (connectionString is null) return null;
        // Replace Password=... (up to next semicolon or end) with Password=*****
        return System.Text.RegularExpressions.Regex.Replace(
            connectionString,
            @"(?i)(Password\s*=)[^;]+",
            "$1*****");
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }
}
