using System;
using Jellyfin.Database.Providers.Postgres.Services;
using Jellyfin.Database.Providers.Postgres.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Database.Providers.Postgres;

/// <summary>
/// Registers this plugin's services with Jellyfin's DI container.
/// Jellyfin discovers this class via <c>GetExportTypes&lt;IPluginServiceRegistrator&gt;()</c>
/// and instantiates it with <c>Activator.CreateInstance</c>, so a parameterless constructor
/// is required (cannot share the class with <see cref="PostgresPlugin"/>).
/// </summary>
public class PostgresServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc/>
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<MigrationService>();
        serviceCollection.AddSingleton<MaintenanceService>();
        serviceCollection.AddSingleton<InstantSearchService>();
        serviceCollection.AddSingleton<ExportToSqliteService>();
        serviceCollection.AddSingleton<HealthCheckService>();
        serviceCollection.AddSingleton<QueryConsoleService>();
        serviceCollection.AddSingleton<IScheduledTask, BackupTask>();
        serviceCollection.AddSingleton<IScheduledTask, VacuumAnalyzeTask>();
        serviceCollection.AddSingleton<IScheduledTask, ReindexTask>();
        serviceCollection.AddSingleton<IScheduledTask, OptimizeIndexesTask>();

        // No JellyTrend registration happens here on purpose. This assembly carries a mirror of the
        // JellyTrend contract, which is the same full name in a different assembly: registering it would
        // hand JellyTrend an object that does not implement ITS interface, and resolving it throws
        // InvalidCastException. The plugin that needs the data looks for it, so JellyTrend discovers
        // PostgresRecommendationQueryProvider by reflection over the loaded plugins.
    }
}
