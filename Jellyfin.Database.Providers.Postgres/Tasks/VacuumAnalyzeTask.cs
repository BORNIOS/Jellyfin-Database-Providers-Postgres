using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Providers.Postgres.Services;
using MediaBrowser.Model.Tasks;

#pragma warning disable SA1611, SA1513

namespace Jellyfin.Database.Providers.Postgres.Tasks;

/// <summary>
/// Jellyfin scheduled task that maintains Jellyfin and opted-in private plugin schemas.
/// Appears in Jellyfin's Scheduled Tasks page for automatic scheduling.
/// </summary>
public class VacuumAnalyzeTask : IScheduledTask
{
    private readonly MaintenanceService _maintenance;
    private readonly PluginSchemaMaintenanceService _pluginMaintenance;

    /// <summary>
    /// Initializes a new instance of the <see cref="VacuumAnalyzeTask"/> class.
    /// </summary>
    /// <param name="maintenance">The maintenance service.</param>
    /// <param name="pluginMaintenance">The isolated private-schema maintenance service.</param>
    public VacuumAnalyzeTask(MaintenanceService maintenance, PluginSchemaMaintenanceService pluginMaintenance)
    {
        _maintenance = maintenance;
        _pluginMaintenance = pluginMaintenance;
    }

    /// <inheritdoc/>
    public string Name => "PostgreSQL VACUUM ANALYZE (managed schemas)";

    /// <inheritdoc/>
    public string Key => "PostgresVacuumAnalyze";

    /// <inheritdoc/>
    public string Description => "Maintains Jellyfin public tables and only private plugin schemas opted into scheduled ANALYZE or VACUUM.";

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
        await _maintenance.VacuumAnalyzeSchemaAsync(connStr, "public", cancellationToken).ConfigureAwait(false);
        var analyzeSchemas = await _pluginMaintenance.ListScheduledSchemasAsync(connStr, "analyze", cancellationToken).ConfigureAwait(false);
        var vacuumSchemas = await _pluginMaintenance.ListScheduledSchemasAsync(connStr, "vacuum", cancellationToken).ConfigureAwait(false);
        foreach (var schema in analyzeSchemas.Except(vacuumSchemas, StringComparer.Ordinal))
        {
            await _pluginMaintenance.AnalyzeAsync(connStr, schema, cancellationToken).ConfigureAwait(false);
        }
        foreach (var schema in vacuumSchemas)
        {
            var tables = await _pluginMaintenance.GetReportAsync(connStr, schema, cancellationToken).ConfigureAwait(false);
            foreach (var table in tables.Tables)
            {
                await _pluginMaintenance.VacuumAnalyzeTableAsync(connStr, schema, table.TableName, cancellationToken).ConfigureAwait(false);
            }
        }
        progress.Report(100);
    }
}
#pragma warning restore SA1611, SA1513
