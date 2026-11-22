using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Providers.Postgres.Logging;
using Jellyfin.Database.Providers.Postgres.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// Second pass of <see cref="SchemaDriftGuard"/>, once Jellyfin has applied every pending migration.
/// </summary>
/// <remarks>
/// The early check in <see cref="PostgresDatabaseProvider.Initialise"/> defers itself while the provider still has
/// migrations to apply (provider and Jellyfin upgraded together), and always leaves index changes for later. Hosted
/// services start after all of Jellyfin's migration stages, so this pass covers whatever drift is left at that point.
/// It runs in the background: a concurrent index build on a large library takes a while and waits for older
/// transactions, and the server must not wait for it. Stopping the server cancels it, which leaves at most an invalid
/// temporary index that the next start removes.
/// </remarks>
internal sealed class SchemaDriftStartupService : IHostedService, IDisposable
{
    private readonly IDbContextFactory<JellyfinDbContext> _contextFactory;
    private readonly ILogger<SchemaDriftStartupService> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _run;

    /// <summary>
    /// Initializes a new instance of the <see cref="SchemaDriftStartupService"/> class.
    /// </summary>
    /// <param name="contextFactory">Jellyfin's context factory.</param>
    /// <param name="logger">Logger.</param>
    public SchemaDriftStartupService(
        IDbContextFactory<JellyfinDbContext> contextFactory,
        ILogger<SchemaDriftStartupService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var previous = SchemaDriftGuard.LastReport;
        if (previous is not null && previous.Outcome != SchemaDriftOutcome.Deferred && previous.Outcome != SchemaDriftOutcome.Failed)
        {
            return Task.CompletedTask;
        }

        _run = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_run is null)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        await _run.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <inheritdoc/>
    public void Dispose() => _stopping.Dispose();

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var mode = SchemaDriftGuard.ResolveMode(PostgresPlugin.Instance?.Configuration?.SchemaDriftMode);
            var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                // By provider name, not by type: the provider instance comes from another load context.
                if (!string.Equals(context.Database.ProviderName, SchemaDriftGuard.NpgsqlProviderName, StringComparison.Ordinal))
                {
                    return; // Plugin installed, SQLite active.
                }

                await SchemaDriftGuard.RunAsync(context, mode, logger: _logger, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PostgresLog.Info("[SchemaDrift] Comprobación posterior a las migraciones interrumpida al detener el servidor; se repite en el próximo arranque.");
        }
        catch (Exception ex)
        {
            PostgresLog.Error("[SchemaDrift] La comprobación posterior a las migraciones falló", ex);
            _logger.LogError(ex, "Schema drift check after migrations failed");
        }
    }
}
