using System.Globalization;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Postgres.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>
/// Jellyfin inserts the item values it believes are missing. When that belief is wrong the unique index on
/// (Type, Value) rejects the statement with 23505 and the whole batch — items and images included — is
/// rolled back, which is what left the channel libraries short of items. These tests pin the behaviour that
/// makes it impossible: the existing row is reused and the mapping is repointed at it.
/// </summary>
public sealed class ItemValueReuseTests
{
    private const string SkipReason = "POSTGRES_TEST_CONNECTION is not set; skipping PostgreSQL integration tests.";

    /// <summary>
    /// One batch with a colliding value and a genuinely new one. The colliding value must not be inserted
    /// again, the new value must be, and the item must end up mapped to both.
    /// </summary>
    [SkippableFact]
    public async Task PendingValueThatAlreadyExistsIsReusedAndTheBatchSurvives()
    {
        Skip.IfNot(TestEnvironment.PostgresAvailable, SkipReason);
        await using var scratch = await TestEnvironment.ScratchDatabaseScope.CreateAsync();

        var connectionString = scratch.Database.ConnectionString;
        using (var migration = TestEnvironment.CreateModelContext(connectionString))
        {
            await migration.Database.MigrateAsync();
        }

        var itemId = await FirstItemIdAsync(connectionString);
        var stored = Guid.NewGuid();
        await ExecuteAsync(
            connectionString,
            string.Concat(
                """INSERT INTO "ItemValues" ("ItemValueId", "Type", "Value", "CleanValue") VALUES ('""",
                stored.ToString(),
                """', 2, 'Accion', 'accion');"""));

        var pending = Guid.NewGuid();
        var fresh = Guid.NewGuid();

        using (var context = CreateContext(connectionString))
        {
            context.ItemValues.Add(new ItemValue
            {
                ItemValueId = pending,
                Type = ItemValueType.Genre,
                Value = "Accion",
                CleanValue = "accion",
            });
            context.ItemValuesMap.Add(new ItemValueMap
            {
                ItemId = itemId,
                ItemValueId = pending,
                Item = null!,
                ItemValue = null!,
            });

            context.ItemValues.Add(new ItemValue
            {
                ItemValueId = fresh,
                Type = ItemValueType.Genre,
                Value = "Recien llegada",
                CleanValue = "recien llegada",
            });
            context.ItemValuesMap.Add(new ItemValueMap
            {
                ItemId = itemId,
                ItemValueId = fresh,
                Item = null!,
                ItemValue = null!,
            });

            // The whole point: this used to throw 23505 for the whole batch.
            await context.SaveChangesAsync();
        }

        Assert.Equal(1, await ScalarAsync(connectionString, """SELECT COUNT(*) FROM "ItemValues" WHERE "Type" = 2 AND "Value" = 'Accion';"""));
        Assert.Equal(1, await ScalarAsync(connectionString, """SELECT COUNT(*) FROM "ItemValues" WHERE "Type" = 2 AND "Value" = 'Recien llegada';"""));

        var mapped = await ReadMappedValueIdsAsync(connectionString, itemId);
        Assert.Contains(stored, mapped);
        Assert.Contains(fresh, mapped);
        Assert.DoesNotContain(pending, mapped);
    }

    /// <summary>
    /// The same pair can arrive twice in one batch, which is what Jellyfin does when two scanned items share
    /// a genre: both rows have to collapse into the stored one, and the item has to keep the mapping.
    /// </summary>
    [SkippableFact]
    public async Task TwoPendingCopiesOfTheSameValueCollapseIntoOneRow()
    {
        Skip.IfNot(TestEnvironment.PostgresAvailable, SkipReason);
        await using var scratch = await TestEnvironment.ScratchDatabaseScope.CreateAsync();

        var connectionString = scratch.Database.ConnectionString;
        using (var migration = TestEnvironment.CreateModelContext(connectionString))
        {
            await migration.Database.MigrateAsync();
        }

        var itemId = await FirstItemIdAsync(connectionString);
        var stored = Guid.NewGuid();
        await ExecuteAsync(
            connectionString,
            string.Concat(
                """INSERT INTO "ItemValues" ("ItemValueId", "Type", "Value", "CleanValue") VALUES ('""",
                stored.ToString(),
                """', 4, 'Recomendados', 'recomendados');"""));

        using (var context = CreateContext(connectionString))
        {
            foreach (var ignored in new[] { 1, 2 })
            {
                var pending = Guid.NewGuid();
                context.ItemValues.Add(new ItemValue
                {
                    ItemValueId = pending,
                    Type = ItemValueType.Tags,
                    Value = "Recomendados",
                    CleanValue = "recomendados",
                });
                context.ItemValuesMap.Add(new ItemValueMap
                {
                    ItemId = itemId,
                    ItemValueId = pending,
                    Item = null!,
                    ItemValue = null!,
                });
            }

            await context.SaveChangesAsync();
        }

        Assert.Equal(1, await ScalarAsync(connectionString, """SELECT COUNT(*) FROM "ItemValues" WHERE "Type" = 4 AND "Value" = 'Recomendados';"""));
        Assert.Equal(1, await ScalarAsync(connectionString, string.Concat("""SELECT COUNT(*) FROM "ItemValuesMap" WHERE "ItemId" = '""", itemId.ToString(), """' AND "ItemValueId" = '""", stored.ToString(), "';")));
    }

    /// <summary>
    /// Values that are genuinely new must still be inserted, or the fix would trade a crash for silent data
    /// loss.
    /// </summary>
    [SkippableFact]
    public async Task ValuesThatDoNotExistAreStillInserted()
    {
        Skip.IfNot(TestEnvironment.PostgresAvailable, SkipReason);
        await using var scratch = await TestEnvironment.ScratchDatabaseScope.CreateAsync();

        var connectionString = scratch.Database.ConnectionString;
        using (var migration = TestEnvironment.CreateModelContext(connectionString))
        {
            await migration.Database.MigrateAsync();
        }

        var itemId = await FirstItemIdAsync(connectionString);
        var fresh = Guid.NewGuid();

        using (var context = CreateContext(connectionString))
        {
            context.ItemValues.Add(new ItemValue
            {
                ItemValueId = fresh,
                Type = ItemValueType.Studios,
                Value = "Estudio recien creado",
                CleanValue = "estudio recien creado",
            });
            context.ItemValuesMap.Add(new ItemValueMap
            {
                ItemId = itemId,
                ItemValueId = fresh,
                Item = null!,
                ItemValue = null!,
            });

            await context.SaveChangesAsync();
        }

        Assert.Equal(1, await ScalarAsync(connectionString, string.Concat("""SELECT COUNT(*) FROM "ItemValues" WHERE "ItemValueId" = '""", fresh.ToString(), "';")));
        Assert.Contains(fresh, await ReadMappedValueIdsAsync(connectionString, itemId));
    }

    /// <summary>
    /// El interceptor tiene que reescribir el cambio pendiente antes de que EF lo traduzca a SQL. Se
    /// comprueba sobre el rastreador de cambios, sin depender de que el guardado llegue a ejecutarse.
    /// </summary>
    [SkippableFact]
    public async Task PendingValueIsDroppedAndItsMappingRepointedBeforeSaving()
    {
        Skip.IfNot(TestEnvironment.PostgresAvailable, SkipReason);
        await using var scratch = await TestEnvironment.ScratchDatabaseScope.CreateAsync();

        var connectionString = scratch.Database.ConnectionString;
        using (var migration = TestEnvironment.CreateModelContext(connectionString))
        {
            await migration.Database.MigrateAsync();
        }

        var itemId = await FirstItemIdAsync(connectionString);
        var stored = Guid.NewGuid();
        await ExecuteAsync(
            connectionString,
            string.Concat(
                """INSERT INTO "ItemValues" ("ItemValueId", "Type", "Value", "CleanValue") VALUES ('""",
                stored.ToString(),
                """', 2, 'Accion', 'accion');"""));

        var pending = Guid.NewGuid();
        using var context = CreateContext(connectionString);

        var value = new ItemValue
        {
            ItemValueId = pending,
            Type = ItemValueType.Genre,
            Value = "Accion",
            CleanValue = "accion",
        };
        var mapping = new ItemValueMap
        {
            ItemId = itemId,
            ItemValueId = pending,
            Item = null!,
            ItemValue = null!,
        };
        context.ItemValues.Add(value);
        context.ItemValuesMap.Add(mapping);

        Assert.Equal(1, ItemValueReuseInterceptor.ReuseExisting(context));

        Assert.Equal(EntityState.Detached, context.Entry(value).State);
        Assert.Equal(EntityState.Detached, context.Entry(mapping).State);

        var repointed = context.ChangeTracker.Entries<ItemValueMap>()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity)
            .ToList();

        Assert.Single(repointed);
        Assert.Equal(stored, repointed[0].ItemValueId);
        Assert.Equal(itemId, repointed[0].ItemId);
    }

    /// <summary>
    /// Un valor realmente nuevo no debe tocarse: su fila y su mapa tienen que insertarse tal cual.
    /// </summary>
    [SkippableFact]
    public async Task PendingValueThatDoesNotExistIsLeftAlone()
    {
        Skip.IfNot(TestEnvironment.PostgresAvailable, SkipReason);
        await using var scratch = await TestEnvironment.ScratchDatabaseScope.CreateAsync();

        var connectionString = scratch.Database.ConnectionString;
        using (var migration = TestEnvironment.CreateModelContext(connectionString))
        {
            await migration.Database.MigrateAsync();
        }

        var itemId = await FirstItemIdAsync(connectionString);
        var fresh = Guid.NewGuid();
        using var context = CreateContext(connectionString);

        var value = new ItemValue
        {
            ItemValueId = fresh,
            Type = ItemValueType.Genre,
            Value = "Todavia no existe",
            CleanValue = "todavia no existe",
        };
        context.ItemValues.Add(value);
        context.ItemValuesMap.Add(new ItemValueMap
        {
            ItemId = itemId,
            ItemValueId = fresh,
            Item = null!,
            ItemValue = null!,
        });

        Assert.Equal(0, ItemValueReuseInterceptor.ReuseExisting(context));
        Assert.Equal(EntityState.Added, context.Entry(value).State);
    }

    private static JellyfinDbContext CreateContext(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<JellyfinDbContext>()
            .UseNpgsql(connectionString, pg => pg.MigrationsAssembly(typeof(PostgresDatabaseProvider).Assembly.FullName))
            .AddInterceptors(new ItemValueReuseInterceptor(), new UpsertConflictInterceptor());

        return new JellyfinDbContext(
            builder.Options,
            NullLogger<JellyfinDbContext>.Instance,
            new PostgresDatabaseProvider(null, null),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }

    private static async Task<Guid> FirstItemIdAsync(string connectionString)
        => await ScalarGuidAsync(connectionString, """SELECT "Id" FROM "BaseItems" LIMIT 1;""");

    private static async Task<List<Guid>> ReadMappedValueIdsAsync(string connectionString, Guid itemId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            string.Concat("""SELECT "ItemValueId" FROM "ItemValuesMap" WHERE "ItemId" = '""", itemId.ToString(), "';"),
            connection);
        await using var reader = await command.ExecuteReaderAsync();

        var ids = new List<Guid>();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private static async Task<Guid> ScalarGuidAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (Guid)(await command.ExecuteScalarAsync() ?? Guid.Empty);
    }

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
