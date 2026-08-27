using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Providers.Postgres.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Database.Providers.Postgres.Tasks;

/// <summary>
/// Jellyfin scheduled task that runs REINDEX DATABASE on the PostgreSQL database.
/// Appears in Jellyfin's Scheduled Tasks page for automatic scheduling.
/// </summary>
public class ReindexTask : IScheduledTask
{
    private readonly MaintenanceService _maintenance;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReindexTask"/> class.
    /// </summary>
    /// <param name="maintenance">The maintenance service.</param>
    public ReindexTask(MaintenanceService maintenance)
        => _maintenance = maintenance;

    /// <inheritdoc/>
    public string Name => "PostgreSQL REINDEX DATABASE";

    /// <inheritdoc/>
    public string Key => "PostgresReindex";

    /// <inheritdoc/>
    public string Description => "Runs REINDEX DATABASE on the Jellyfin PostgreSQL database to rebuild all indexes and fix index bloat.";

    /// <inheritdoc/>
    public string Category => "PostgreSQL Maintenance";

    /// <inheritdoc/>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.WeeklyTrigger,
            DayOfWeek = DayOfWeek.Sunday,
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
        };
    }

    /// <inheritdoc/>
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var connStr = PostgresPlugin.Instance?.Configuration.ConnectionString;
        if (string.IsNullOrWhiteSpace(connStr))
        {
            return;
        }

        progress.Report(10);
        await _maintenance.ReindexAsync(connStr, cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }
}
