using System;
using Jellyfin.Database.Providers.Postgres.Services;
using Jellyfin.Database.Providers.Postgres.Tasks;
using Jellyfin.Plugin.JellyTrend.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
        serviceCollection.AddSingleton<IScheduledTask, BackupTask>();
        serviceCollection.AddSingleton<IScheduledTask, VacuumAnalyzeTask>();
        serviceCollection.AddSingleton<IScheduledTask, ReindexTask>();
        serviceCollection.AddSingleton<IScheduledTask, OptimizeIndexesTask>();

        // JellyTrend integration — register only when JellyTrend plugin is also installed.
        // The interface type is known at compile time (JellyTrendContract mirror), but at
        // runtime we resolve it from the JellyTrend assembly so DI wires up correctly.
        // If JellyTrend is absent the AppDomain lookup returns null and we skip silently.
        TryRegisterJellyTrendProvider(serviceCollection);
    }

    private static void TryRegisterJellyTrendProvider(IServiceCollection serviceCollection)
    {
        try
        {
            // Resolve the real interface type from the loaded JellyTrend assembly.
            // Our local mirror (JellyTrendContract/) has the same fully-qualified name,
            // so we use it only as a compile-time anchor — DI must use the JellyTrend type.
            var interfaceType = Type.GetType(
                "Jellyfin.Plugin.JellyTrend.Api.IRecommendationQueryProvider, Jellyfin.Plugin.JellyTrend",
                throwOnError: false);

            if (interfaceType is null)
            {
                return;
            }

            serviceCollection.AddSingleton(
                interfaceType,
                sp => new PostgresRecommendationQueryProvider(
                    sp.GetRequiredService<ILogger<PostgresRecommendationQueryProvider>>()));

            Logging.PostgresLog.Info("[JellyTrend] IRecommendationQueryProvider registrado. Las recomendaciones usarán SQL optimizado de PostgreSQL.");
        }
        catch (Exception ex)
        {
            Logging.PostgresLog.Warn($"[JellyTrend] No se pudo registrar IRecommendationQueryProvider: {ex.Message}");
        }
    }
}
