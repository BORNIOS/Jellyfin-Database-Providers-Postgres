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
/// Jellyfin scheduled task that deletes PostgreSQL backups older than the configured retention.
/// </summary>
public class BackupRetentionTask : IScheduledTask
{
    private readonly IApplicationPaths _applicationPaths;

    /// <summary>
    /// Initializes a new instance of the <see cref="BackupRetentionTask"/> class.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin application paths.</param>
    public BackupRetentionTask(IApplicationPaths applicationPaths)
    {
        _applicationPaths = applicationPaths;
    }

    /// <inheritdoc/>
    public string Name => "PostgreSQL Backup Retention";

    /// <inheritdoc/>
    public string Key => "PostgresBackupRetention";

    /// <inheritdoc/>
    public string Description => "Deletes PostgreSQL backups older than the retention configured in the plugin, always keeping the newest ones.";

    /// <inheritdoc/>
    public string Category => "PostgreSQL Maintenance";

    /// <inheritdoc/>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        };
    }

    /// <inheritdoc/>
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = PostgresPlugin.Instance?.Configuration;
        if (config is not null && config.BackupRetentionDays > 0)
        {
            var directory = string.IsNullOrWhiteSpace(config.BackupDirectory)
                ? Path.Combine(_applicationPaths.DataPath, "postgres-backups")
                : config.BackupDirectory;
            BackupRetentionService.Apply(directory, config.BackupRetentionDays, config.BackupRetentionMinKeep, dryRun: false);
        }

        progress.Report(100);
        return Task.CompletedTask;
    }
}
