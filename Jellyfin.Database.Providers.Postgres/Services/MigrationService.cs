using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Providers.Postgres.Logging;
using Jellyfin.Database.Providers.Postgres.Services.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// Runs the SQLite to PostgreSQL data migration in-process.
/// Thread-safe: only one migration can run at a time.
/// </summary>
public sealed class MigrationService : IDisposable
{
    private const int DefaultBatch = 1000;
    private const int MaxLogLines = 500;

    internal static readonly string[] SqlStatementSeparators = { "\r\n\r\n", "\n\n" };

    private readonly ILogger<MigrationService> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly List<string> _logBuffer = new(MaxLogLines);
    private readonly object _logLock = new();

    private volatile bool _isRunning;
    private volatile bool _isCompleted;
    private volatile bool _hasError;
    private string? _errorMessage;
    private int _percentComplete;
    private string _currentTable = string.Empty;
    private long _migratedRows;
    private long _totalRows;

    /// <summary>
    /// Initializes a new instance of the <see cref="MigrationService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public MigrationService(ILogger<MigrationService> logger) => _logger = logger;

    /// <inheritdoc/>
    public void Dispose() => _semaphore.Dispose();

    /// <summary>Gets a snapshot of the current migration progress.</summary>
    /// <returns>Current <see cref="MigrationProgress"/> snapshot.</returns>
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
    /// Starts the migration in a background task. Returns false if one is already running.
    /// </summary>
    /// <param name="sqlitePath">Absolute path to the source SQLite database file.</param>
    /// <param name="postgresConnectionString">PostgreSQL connection string for the target database.</param>
    /// <param name="schema">Target PostgreSQL schema name.</param>
    /// <param name="batchSize">Number of rows per insert batch.</param>
    /// <param name="truncate">When <see langword="true"/>, truncates each table before copying.</param>
    /// <param name="applicationPaths">Jellyfin application paths, used to locate the default SQLite file.</param>
    /// <returns><see langword="true"/> if migration started; <see langword="false"/> if one is already running.</returns>
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
            return false;
        }

        _isRunning = true;
        _isCompleted = false;
        _hasError = false;
        _errorMessage = null;
        _percentComplete = 0;
        _currentTable = string.Empty;
        _migratedRows = 0;
        _totalRows = 0;

        lock (_logLock)
        {
            _logBuffer.Clear();
        }

        PostgresLog.Warn($"[Migration] INICIO: {sqlitePath} → PostgreSQL schema={schema} batch={batchSize} truncate={truncate}");

        _ = Task.Run(async () =>
        {
            try
            {
                await MigrationEngine.RunAsync(
                    sqlitePath,
                    postgresConnectionString,
                    schema,
                    batchSize,
                    truncate,
                    applicationPaths,
                    this,
                    CancellationToken.None).ConfigureAwait(false);
                _isCompleted = true;
                PostgresLog.Warn($"[Migration] COMPLETADA: {_migratedRows:N0} filas migradas.");
            }
            catch (Exception ex)
            {
                _hasError = true;
                _errorMessage = ex.Message;
                Log($"[FATAL] {ex.Message}");
                _logger.LogError(ex, "Migration failed with unhandled exception");
                PostgresLog.Error("[Migration] FALLIDA", ex);
            }
            finally
            {
                _isRunning = false;
                _semaphore.Release();
            }
        });

        return true;
    }

    // Internal state setters used by MigrationEngine
    internal void SetPercentComplete(int value) => _percentComplete = value;

    internal void SetCurrentTable(string value) => _currentTable = value;

    internal void AddMigratedRows(long count) => _migratedRows += count;

    internal void Log(string message)
    {
        _logger.LogInformation("{Message}", message);
        lock (_logLock)
        {
            if (_logBuffer.Count >= MaxLogLines)
            {
                _logBuffer.RemoveAt(0);
            }

            _logBuffer.Add($"[{DateTime.UtcNow:HH:mm:ss}] {message}");
        }
    }
}
