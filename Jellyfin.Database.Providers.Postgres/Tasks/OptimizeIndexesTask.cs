using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Providers.Postgres;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Database.Providers.Postgres.Tasks;

/// <summary>
/// Scheduled task that applies (or re-applies) all PostgreSQL performance optimizations:
/// pg_trgm GIN indexes for near-instant search and aggressive autovacuum settings on
/// high-churn tables (UserData, ActivityLogs, BaseItems).
///
/// All work is idempotent — safe to run multiple times. Index creation uses
/// <c>CREATE INDEX CONCURRENTLY IF NOT EXISTS</c> so there are no table locks.
/// </summary>
public sealed class OptimizeIndexesTask : IScheduledTask
{
    private readonly ILogger<OptimizeIndexesTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OptimizeIndexesTask"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public OptimizeIndexesTask(ILogger<OptimizeIndexesTask> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public string Name => "PostgreSQL Apply Performance Optimizations";

    /// <inheritdoc/>
    public string Key => "PostgresOptimizeIndexes";

    /// <inheritdoc/>
    public string Description =>
        "Creates pg_trgm GIN indexes for near-instant text search and tunes autovacuum " +
        "on high-churn tables. Safe to run multiple times — all statements are idempotent.";

    /// <inheritdoc/>
    public string Category => "PostgreSQL Maintenance";

    /// <inheritdoc/>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Run once a week on Saturday at 01:00 to pick up any missed startup run
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.WeeklyTrigger,
            DayOfWeek = DayOfWeek.Saturday,
            TimeOfDayTicks = TimeSpan.FromHours(1).Ticks,
        };
    }

    /// <inheritdoc/>
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = PostgresPlugin.Instance?.Configuration;
        var connStr = config?.ConnectionString;

        if (string.IsNullOrWhiteSpace(connStr))
        {
            return;
        }

        progress.Report(5);

        await PostgresDatabaseProvider.RunOptimizationsAsync(
            connStr,
            enableSearch: config?.EnableSearchOptimizations ?? true,
            enableVacuum: config?.EnableAutovacuumTuning ?? true,
            logger: _logger,
            ct: cancellationToken).ConfigureAwait(false);

        progress.Report(100);
    }
}
