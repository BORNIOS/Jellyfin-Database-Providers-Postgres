using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Postgres.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>
/// A duplicate row must behave like Jellyfin's SQLite write path: keep what is stored and count as a
/// successful save.
/// </summary>
/// <remarks>
/// The conflict rewrite exists because Jellyfin inserts an item's dependent rows again for an item that
/// already exists. Skipping the insert with <c>ON CONFLICT DO NOTHING</c> fixes the unique violation but
/// reports zero affected rows, and EF Core turns a row count below the expected one into
/// <c>DbUpdateConcurrencyException</c> — the 500 a client sees when it opens an artist page. The rewrite
/// therefore performs a no-op update so both outcomes report one row.
/// </remarks>
public sealed class UpsertConflictIntegrationTests
{
    private const string SkipReason = "POSTGRES_TEST_CONNECTION is not set; skipping PostgreSQL integration tests.";

    [SkippableFact]
    public async Task DuplicateProviderInsertKeepsTheStoredRowWithoutFailingTheSave()
    {
        Skip.IfNot(TestEnvironment.PostgresAvailable, SkipReason);
        await using var scratch = await TestEnvironment.ScratchDatabaseScope.CreateAsync();

        var id = Guid.NewGuid();
        using (var seed = CreateContext(scratch.Database.ConnectionString))
        {
            await seed.Database.MigrateAsync();
            seed.BaseItems.Add(new BaseItemEntity { Id = id, Type = "Person", Name = "Artista" });
            seed.BaseItemProviders.Add(new BaseItemProvider
            {
                ItemId = id,
                ProviderId = "Tmdb",
                ProviderValue = "123",
                Item = null!,
            });
            await seed.SaveChangesAsync();
        }

        // ItemPersistenceService adds the providers again when the item it refreshes already exists.
        using (var writer = CreateContext(scratch.Database.ConnectionString, new UpsertConflictInterceptor()))
        {
            writer.BaseItemProviders.Add(new BaseItemProvider
            {
                ItemId = id,
                ProviderId = "Tmdb",
                ProviderValue = "123",
                Item = null!,
            });

            await writer.SaveChangesAsync();
        }

        using var verify = CreateContext(scratch.Database.ConnectionString);
        Assert.Equal(
            "123",
            await verify.BaseItemProviders
                .Where(provider => provider.ItemId == id)
                .Select(provider => provider.ProviderValue)
                .SingleAsync());
    }

    [SkippableFact]
    public async Task DuplicateMetadataFieldInsertKeepsTheStoredRowWithoutFailingTheSave()
    {
        Skip.IfNot(TestEnvironment.PostgresAvailable, SkipReason);
        await using var scratch = await TestEnvironment.ScratchDatabaseScope.CreateAsync();

        var id = Guid.NewGuid();
        using (var seed = CreateContext(scratch.Database.ConnectionString))
        {
            await seed.Database.MigrateAsync();
            seed.BaseItems.Add(new BaseItemEntity { Id = id, Type = "Person", Name = "Artista" });
            seed.BaseItemMetadataFields.Add(new BaseItemMetadataField { Id = 1, ItemId = id, Item = null! });
            await seed.SaveChangesAsync();
        }

        using (var writer = CreateContext(scratch.Database.ConnectionString, new UpsertConflictInterceptor()))
        {
            writer.BaseItemMetadataFields.Add(new BaseItemMetadataField { Id = 1, ItemId = id, Item = null! });

            await writer.SaveChangesAsync();
        }

        using var verify = CreateContext(scratch.Database.ConnectionString);
        Assert.Equal(1, await verify.BaseItemMetadataFields.CountAsync(field => field.ItemId == id));
    }

    private static JellyfinDbContext CreateContext(string connectionString, params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<JellyfinDbContext>()
            .UseNpgsql(connectionString, pg => pg.MigrationsAssembly(typeof(PostgresDatabaseProvider).Assembly.FullName));
        if (interceptors.Length > 0)
        {
            builder.AddInterceptors(interceptors);
        }

        return new JellyfinDbContext(
            builder.Options,
            NullLogger<JellyfinDbContext>.Instance,
            new PostgresDatabaseProvider(null, null),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }
}
