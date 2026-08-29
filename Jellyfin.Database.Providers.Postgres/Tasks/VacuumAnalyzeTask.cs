using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Providers.Postgres.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Database.Providers.Postgres.Tasks;

/// <summary>
/// Jellyfin scheduled task that runs VACUUM ANALYZE on the PostgreSQL database.
/// Appears in Jellyfin's Scheduled Tasks page for automatic scheduling.
/// </summary>
public class VacuumAnalyzeTask : IScheduledTask
{
    private readonly MaintenanceService _maintenance;

    /// <summary>
    /// Initializes a new instance of the <see cref="VacuumAnalyzeTask"/> class.
    /// </summary>
    /// <param name="maintenance">The maintenance service.</param>
    public VacuumAnalyzeTask(MaintenanceService maintenance)
        => _maintenance = maintenance;

    /// <inheritdoc/>
    public string Name => "PostgreSQL VACUUM ANALYZE";

    /// <inheritdoc/>
    public string Key => "PostgresVacuumAnalyze";

    /// <inheritdoc/>
    public string Description => "Runs VACUUM ANALYZE on the Jellyfin PostgreSQL database to reclaim storage and update query-planner statistics.";

    /// <inheritdoc/>
    public string Category => "PostgreSQL Maintenance";

    /// <inheritdoc/>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.WeeklyTrigger,
            DayOfWeek = DayOfWeek.Sunday,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
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
        await _maintenance.VacuumAnalyzeAsync(connStr, cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }
}
