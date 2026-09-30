using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Providers.Postgres.Services;
using MediaBrowser.Model.Tasks;

#pragma warning disable SA1611, SA1513

namespace Jellyfin.Database.Providers.Postgres.Tasks;

/// <summary>
/// Jellyfin scheduled task that rebuilds indexes for Jellyfin and opted-in private plugin schemas.
/// Appears in Jellyfin's Scheduled Tasks page for automatic scheduling.
/// </summary>
public class ReindexTask : IScheduledTask
{
    private readonly MaintenanceService _maintenance;
    private readonly PluginSchemaMaintenanceService _pluginMaintenance;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReindexTask"/> class.
    /// </summary>
    /// <param name="maintenance">The maintenance service.</param>
    /// <param name="pluginMaintenance">The isolated private-schema maintenance service.</param>
    public ReindexTask(MaintenanceService maintenance, PluginSchemaMaintenanceService pluginMaintenance)
    {
        _maintenance = maintenance;
        _pluginMaintenance = pluginMaintenance;
    }

    /// <inheritdoc/>
    public string Name => "PostgreSQL REINDEX (managed schemas)";

    /// <inheritdoc/>
    public string Key => "PostgresReindex";

    /// <inheritdoc/>
    public string Description => "Rebuilds indexes in Jellyfin public tables and only private plugin schemas opted into scheduled REINDEX.";

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
        await _maintenance.ReindexSchemaAsync(connStr, "public", cancellationToken).ConfigureAwait(false);
        var schemas = await _pluginMaintenance.ListScheduledSchemasAsync(connStr, "reindex", cancellationToken).ConfigureAwait(false);
        foreach (var schema in schemas)
        {
            var tables = await _pluginMaintenance.GetReportAsync(connStr, schema, cancellationToken).ConfigureAwait(false);
            foreach (var table in tables.Tables)
            {
                await _pluginMaintenance.ReindexTableAsync(connStr, schema, table.TableName, cancellationToken).ConfigureAwait(false);
            }
        }
        progress.Report(100);
    }
}
#pragma warning restore SA1611, SA1513
