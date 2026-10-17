using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Postgres.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>
/// Jellyfin stores its code migrations and the provider's schema migrations in the same history table,
/// with the same date-prefixed shape. Only the code migrations must be carried over to PostgreSQL.
/// </summary>
public sealed class MigrationCodeMigrationsTests
{
    private const string SkipReason = "POSTGRES_TEST_CONNECTION is not set; skipping PostgreSQL integration tests.";

    private static readonly HashSet<string> KnownNames = new(StringComparer.Ordinal)
    {
        "RemoveDuplicateExtras",
        "MigrateActivityLogDb",
    };

    [Theory]
    [InlineData("20250420080000_RemoveDuplicateExtras", true)]
    [InlineData("20250420070000_MigrateActivityLogDb", true)]
    [InlineData("20240101000000_SqliteOnlyMigration", false)]
    [InlineData("20250420080000_UnknownRoutine", false)]
    [InlineData("no_underscore_prefix", false)]
    [InlineData("20250420080000_", false)]
    public void OnlyKnownCodeMigrationsAreRecognised(string id, bool expected)
        => Assert.Equal(expected, MigrationCodeMigrations.IsCodeMigrationId(id, KnownNames));

    [SkippableFact]
    public async Task AppliedCodeMigrationsAreCopiedWithoutTheSchemaOnes()
    {
        Skip.IfNot(TestEnvironment.PostgresAvailable, SkipReason);
        await using var scratch = await TestEnvironment.ScratchDatabaseScope.CreateAsync();

        using (var context = CreateContext(scratch.Database.ConnectionString))
        {
            await context.Database.MigrateAsync();
        }

        var source = Path.Combine(Path.GetTempPath(), $"jf-history-{Guid.NewGuid():N}.db");
        try
        {
            await CreateSourceHistoryAsync(source);

            await using var sqlite = new SqliteConnection($"Data Source={source};Mode=ReadOnly");
            await sqlite.OpenAsync();
            await using var pg = new NpgsqlConnection(scratch.Database.ConnectionString);
            await pg.OpenAsync();

            var log = new List<string>();
            await MigrationCodeMigrations.CopyAppliedAsync(sqlite, pg, KnownNames, log.Add);

            var applied = await ReadTargetHistoryAsync(pg);
            Assert.Contains("20250420080000_RemoveDuplicateExtras", applied.Keys, StringComparer.Ordinal);
            Assert.Contains("20250420070000_MigrateActivityLogDb", applied.Keys, StringComparer.Ordinal);
            Assert.DoesNotContain("20240101000000_SqliteOnlyMigration", applied.Keys, StringComparer.Ordinal);
            Assert.Equal("9.0.0", applied["20250420080000_RemoveDuplicateExtras"]);
            Assert.Contains(log, entry => entry.Contains("2 de 3", StringComparison.Ordinal));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(source);
        }
    }

    [SkippableFact]
    public async Task NoKnownMigrationsLeavesTheHistoryUntouched()
    {
        Skip.IfNot(TestEnvironment.PostgresAvailable, SkipReason);
        await using var scratch = await TestEnvironment.ScratchDatabaseScope.CreateAsync();

        using (var context = CreateContext(scratch.Database.ConnectionString))
        {
            await context.Database.MigrateAsync();
        }

        var source = Path.Combine(Path.GetTempPath(), $"jf-history-{Guid.NewGuid():N}.db");
        try
        {
            await CreateSourceHistoryAsync(source);

            await using var sqlite = new SqliteConnection($"Data Source={source};Mode=ReadOnly");
            await sqlite.OpenAsync();
            await using var pg = new NpgsqlConnection(scratch.Database.ConnectionString);
            await pg.OpenAsync();

            var before = (await ReadTargetHistoryAsync(pg)).Count;
            var log = new List<string>();
            await MigrationCodeMigrations.CopyAppliedAsync(sqlite, pg, new HashSet<string>(StringComparer.Ordinal), log.Add);

            Assert.Equal(before, (await ReadTargetHistoryAsync(pg)).Count);
            Assert.Contains(log, entry => entry.Contains("No se encontraron tipos", StringComparison.Ordinal));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(source);
        }
    }

    private static async Task CreateSourceHistoryAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE "__EFMigrationsHistory" ("MigrationId" TEXT NOT NULL PRIMARY KEY, "ProductVersion" TEXT NOT NULL);
            INSERT INTO "__EFMigrationsHistory" VALUES ('20250420080000_RemoveDuplicateExtras', '9.0.0');
            INSERT INTO "__EFMigrationsHistory" VALUES ('20250420070000_MigrateActivityLogDb', '9.0.0');
            INSERT INTO "__EFMigrationsHistory" VALUES ('20240101000000_SqliteOnlyMigration', '9.0.0');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<Dictionary<string, string>> ReadTargetHistoryAsync(NpgsqlConnection connection)
    {
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand("SELECT \"MigrationId\", \"ProductVersion\" FROM \"__EFMigrationsHistory\";", connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows[reader.GetString(0)] = reader.GetString(1);
        }

        return rows;
    }

    private static JellyfinDbContext CreateContext(string connectionString)
        => new(
            new DbContextOptionsBuilder<JellyfinDbContext>()
                .UseNpgsql(connectionString, pg => pg.MigrationsAssembly(typeof(PostgresDatabaseProvider).Assembly.FullName))
                .Options,
            NullLogger<JellyfinDbContext>.Instance,
            new PostgresDatabaseProvider(null, null),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
}
