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
        serviceCollection.AddSingleton<IScheduledTask, BackupTask>();
        serviceCollection.AddSingleton<IScheduledTask, VacuumAnalyzeTask>();
        serviceCollection.AddSingleton<IScheduledTask, ReindexTask>();
        serviceCollection.AddSingleton<IScheduledTask, OptimizeIndexesTask>();
    }
}
