using System.Globalization;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Postgres.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>
/// The read cache must never answer Jellyfin's own write path with rows that belong to another snapshot.
/// These are the three ways it did: rows inserted by a transaction that was later rolled back, rows added
/// after an entry was stored, and any id list at all, because arrays serialised to the same key.
/// Each test creates its own throw-away database, so a run cannot touch real data.
/// </summary>
public sealed class HomeQueryCacheTests
{
    private const string SkipReason = "POSTGRES_TEST_CONNECTION is not set; skipping PostgreSQL integration tests.";

    /// <summary>
    /// A batch that inserts an item value inside a transaction and reads it back used to leave that read in
    /// the cache. When the batch was rolled back (for example because another statement failed), the next
    /// caller — a library scan checking whether a value already exists — received rows that were never
    /// committed. That is how channel items ended up being inserted twice.
    /// </summary>
    [SkippableFact]
    public async Task ValueReadBackInsideARolledBackTransactionIsNeverServedFromCache()
    {
        Skip.IfNot(TestEnvironment.PostgresAvailable, SkipReason);
        await using var scratch = await TestEnvironment.ScratchDatabaseScope.CreateAsync();

        var interceptor = new HomeQueryCacheInterceptor();
        await MigrateAsync(scratch.Database.ConnectionString);

        var pending = Guid.NewGuid();
        using (var writer = CreateContext(scratch.Database.ConnectionString, interceptor))
        {
            using var transaction = writer.Database.BeginTransaction();
            await writer.Database.ExecuteSqlRawAsync(
                string.Concat(
                    """INSERT INTO "ItemValues" ("ItemValueId", "Type", "Value", "CleanValue") VALUES ('""",
                    pending.ToString(),
                    """', 2, 'Fantasma', 'fantasma');"""));

            // Jellyfin does exactly this: it reads the value back to decide whether to insert it.
            Assert.Equal(new[] { pending }, await ReadValueIdsAsync(writer, "Fantasma"));

            transaction.Rollback();
        }

        using (var reader = CreateContext(scratch.Database.ConnectionString, interceptor))
        {
            Assert.Empty(await ReadValueIdsAsync(reader, "Fantasma"));
        }

        Assert.Equal(0, await ScalarAsync(scratch.Database.ConnectionString, """SELECT COUNT(*) FROM "ItemValues" WHERE "Value" = 'Fantasma';"""));
    }

    /// <summary>
    /// An entry stored before a write must not survive it. Without this, a scan that adds a genre kept
    /// answering "that genre does not exist yet" for the whole time to live and every item after it failed
    /// with 23505 on the unique value index.
    /// </summary>
    [SkippableFact]
    public async Task WritingToATableInvalidatesTheEntriesStoredForIt()
    {
        Skip.IfNot(TestEnvironment.PostgresAvailable, SkipReason);
        await using var scratch = await TestEnvironment.ScratchDatabaseScope.CreateAsync();

        var interceptor = new HomeQueryCacheInterceptor();
        await MigrateAsync(scratch.Database.ConnectionString);

        using (var reader = CreateContext(scratch.Database.ConnectionString, interceptor))
        {
            Assert.Empty(await ReadValueIdsAsync(reader, "Nueva"));
        }

        var stored = Guid.NewGuid();
        using (var writer = CreateContext(scratch.Database.ConnectionString, interceptor))
        {
            await writer.Database.ExecuteSqlRawAsync(
                string.Concat(
                    """INSERT INTO "ItemValues" ("ItemValueId", "Type", "Value", "CleanValue") VALUES ('""",
                    stored.ToString(),
                    """', 2, 'Nueva', 'nueva');"""));
        }

        using (var reader = CreateContext(scratch.Database.ConnectionString, interceptor))
        {
            Assert.Equal(new[] { stored }, await ReadValueIdsAsync(reader, "Nueva"));
        }
    }

    /// <summary>
    /// Two different lists of ids have to be two different entries. Arrays were serialised with
    /// <c>ToString()</c>, which returns the type name, so every list shared one key and the cache answered
    /// with another list's rows — the wrong answer for the existence checks Jellyfin makes before writing.
    /// </summary>
    [SkippableFact]
    public async Task TwoDifferentListParametersDoNotShareAnEntry()
    {
        Skip.IfNot(TestEnvironment.PostgresAvailable, SkipReason);
        await using var scratch = await TestEnvironment.ScratchDatabaseScope.CreateAsync();

        var interceptor = new HomeQueryCacheInterceptor();
        await MigrateAsync(scratch.Database.ConnectionString);

        var alfa = Guid.NewGuid();
        var beta = Guid.NewGuid();
        await ExecuteAsync(
            scratch.Database.ConnectionString,
            string.Concat(
                """INSERT INTO "ItemValues" ("ItemValueId", "Type", "Value", "CleanValue") VALUES ('""",
                alfa.ToString(),
                """', 2, 'Alfa', 'alfa'), ('""",
                beta.ToString(),
                """', 2, 'Beta', 'beta');"""));

        using (var reader = CreateContext(scratch.Database.ConnectionString, interceptor))
        {
            Assert.Equal(new[] { alfa }, await ReadValueIdsFromListAsync(reader, [alfa.ToString(), "Alfa"]));
        }

        using (var reader = CreateContext(scratch.Database.ConnectionString, interceptor))
        {
            Assert.Equal(new[] { beta }, await ReadValueIdsFromListAsync(reader, [beta.ToString(), "Beta"]));
        }
    }

    /// <summary>Reads the identifiers of the values of a kind, using the shape Jellyfin's checks use.</summary>
    /// <param name="context">Context to query with.</param>
    /// <param name="value">Value to look for.</param>
    /// <returns>Identifiers found.</returns>
    private static Task<List<Guid>> ReadValueIdsAsync(JellyfinDbContext context, string value)
        => context.Database
            .SqlQuery<Guid>($"""SELECT "ItemValueId" AS "Value" FROM "ItemValues" WHERE "Type" = 2 AND "Value" = {value}""")
            .ToListAsync();

    /// <summary>Reads the identifiers of the values whose id is in a list, which binds an array parameter.</summary>
    /// <param name="context">Context to query with.</param>
    /// <param name="values">List to bind.</param>
    /// <returns>Identifiers found.</returns>
    private static Task<List<Guid>> ReadValueIdsFromListAsync(JellyfinDbContext context, string[] values)
        => context.Database
            .SqlQuery<Guid>($"""SELECT "ItemValueId" AS "Value" FROM "ItemValues" WHERE "Type" = 2 AND "Value" IN (SELECT unnest({values}))""")
            .ToListAsync();

    private static async Task MigrateAsync(string connectionString)
    {
        using var context = TestEnvironment.CreateModelContext(connectionString);
        await context.Database.MigrateAsync();
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
