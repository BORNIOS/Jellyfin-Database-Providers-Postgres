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
/// migrations to apply (provider and Jellyfin upgraded together). Hosted services start after all of Jellyfin's
/// migration stages, so this pass covers whatever drift is left at that point.
/// </remarks>
internal sealed class SchemaDriftStartupService : IHostedService
{
    private readonly IDbContextFactory<JellyfinDbContext> _contextFactory;
    private readonly ILogger<SchemaDriftStartupService> _logger;

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
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var previous = SchemaDriftGuard.LastReport;
        if (previous is not null && previous.Outcome != SchemaDriftOutcome.Deferred && previous.Outcome != SchemaDriftOutcome.Failed)
        {
            return;
        }

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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PostgresLog.Error("[SchemaDrift] La comprobación posterior a las migraciones falló", ex);
            _logger.LogError(ex, "Schema drift check after migrations failed");
        }
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
