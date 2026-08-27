using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Providers.Postgres.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Database.Providers.Postgres.Tasks;

/// <summary>
/// Jellyfin scheduled task that creates PostgreSQL backups via pg_dump.
/// </summary>
public class BackupTask : IScheduledTask
{
    private readonly MaintenanceService _maintenance;
    private readonly IApplicationPaths _applicationPaths;

    /// <summary>
    /// Initializes a new instance of the <see cref="BackupTask"/> class.
    /// </summary>
    /// <param name="maintenance">The maintenance service.</param>
    /// <param name="applicationPaths">Jellyfin application paths.</param>
    public BackupTask(MaintenanceService maintenance, IApplicationPaths applicationPaths)
    {
        _maintenance = maintenance;
        _applicationPaths = applicationPaths;
    }

    /// <inheritdoc/>
    public string Name => "PostgreSQL Backup";

    /// <inheritdoc/>
    public string Key => "PostgresBackup";

    /// <inheritdoc/>
    public string Description => "Creates a PostgreSQL backup file using pg_dump. Uses plugin backup settings for path and compression.";

    /// <inheritdoc/>
    public string Category => "PostgreSQL Maintenance";

    /// <inheritdoc/>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(2).Ticks
        };
    }

    /// <inheritdoc/>
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = PostgresPlugin.Instance?.Configuration;
        if (config is null || string.IsNullOrWhiteSpace(config.ConnectionString))
        {
            return;
        }

        var backupDir = string.IsNullOrWhiteSpace(config.BackupDirectory)
            ? Path.Combine(_applicationPaths.DataPath, "postgres-backups")
            : config.BackupDirectory;

        progress.Report(10);
        await _maintenance.CreateBackupAsync(
            config.ConnectionString,
            backupDir,
            config.BackupCompression,
            config.PgDumpPath,
            cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }
}
