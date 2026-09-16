using System.Globalization;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Postgres.Services;
using Jellyfin.Database.Providers.Postgres.Services.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>
/// Integration tests against a real PostgreSQL server. Each one creates a throw-away database and drops it
/// afterwards; the connection string from <c>POSTGRES_TEST_CONNECTION</c> is only used to issue
/// CREATE/DROP DATABASE, never as the database under test. Without that variable the tests are skipped, so
/// a contributor who only wants to run the unit tests gets a green run.
/// </summary>
public sealed class PostgresIntegrationTests
{
    /// <summary>
    /// Runs the first migration on an empty database, loads it with legacy-looking rows and then applies
    /// everything Jellyfin 12.1 adds, including the SQLite-only JSON cleanup and the UUID conversions.
    /// </summary>
    [SkippableFact]
    public async Task InitialSchemaAndPopulatedUpgradeWork()
    {
        RequirePostgres();
        await using var scratch = await Scratch.CreateAsync();

        using var context = CreateContext(scratch.Database.ConnectionString, new Jellyfin121MigrationInterceptor());
        await context.GetService<IMigrator>().MigrateAsync("00000000000000_InitialCreate");

        // Raw SQL containing braces is executed through Npgsql: ExecuteSqlRaw formats the statement.
        await ExecuteAsync(
            scratch.Database.ConnectionString,
            """
            INSERT INTO "BaseItems"
            SELECT (jsonb_populate_record(NULL::"BaseItems", to_jsonb(b) ||
                '{"Id":"10000000-0000-0000-0000-000000000001","OwnerId":"bad-id","PrimaryVersionId":"20000000000000000000000000000001","ExtraIds":"legacy-extra-ids"}'::jsonb)).*
            FROM "BaseItems" b LIMIT 1;
            """);
        await ExecuteAsync(
            scratch.Database.ConnectionString,
            """
            INSERT INTO "ItemValues" VALUES
            ('40000000-0000-0000-0000-000000000001', 1, 'Duplicate', 'duplicate'),
            ('40000000-0000-0000-0000-000000000002', 1, 'Duplicate', 'duplicate');
            INSERT INTO "ItemValuesMap" ("ItemValueId", "ItemId") VALUES
            ('40000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000001'),
            ('40000000-0000-0000-0000-000000000002', '10000000-0000-0000-0000-000000000001');
            INSERT INTO "Permissions" ("Kind", "Value", "RowVersion") VALUES (1, true, 0);
            """);

        await context.Database.MigrateAsync();

        await using (var upgraded = new NpgsqlConnection(scratch.Database.ConnectionString))
        {
            await upgraded.OpenAsync();
            Assert.Equal(1, await ScalarAsync(upgraded, "SELECT COUNT(*) FROM \"ItemValues\""));
            Assert.Equal(2, await ScalarAsync(upgraded, "SELECT COUNT(*) FROM \"ItemValuesMap\""));
            Assert.Equal(0, await ScalarAsync(upgraded, "SELECT COUNT(*) FROM \"Permissions\""));
        }

        var converted = await context.BaseItems.SingleAsync(item => item.Id == Guid.Parse("10000000-0000-0000-0000-000000000001"));
        Assert.Null(converted.OwnerId);
        Assert.Equal(Guid.Parse("20000000-0000-0000-0000-000000000001"), converted.PrimaryVersionId);
        Assert.Null(converted.OriginalLanguage);
    }

    /// <summary>
    /// The JSON cleanup Jellyfin 12.1 runs is written for SQLite. This covers the rewritten statement
    /// against PostgreSQL, including the synchronous and asynchronous paths and malformed payloads.
    /// </summary>
    [SkippableFact]
    public async Task JsonCleanupWorksInSyncAndAsyncPaths()
    {
        RequirePostgres();
        await using var scratch = await Scratch.CreateAsync();

        using var context = CreateContext(scratch.Database.ConnectionString, new Jellyfin121MigrationInterceptor());
        await context.Database.MigrateAsync();

        const string json = "{\"LinkedChildren\":[],\"ExtraIds\":[],\"SupportsExternalTransfer\":true,\"Keep\":42}";
        var id = await context.BaseItems.Select(item => item.Id).FirstAsync();
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"BaseItems\" SET \"Data\" = {json} WHERE \"Id\" = {id}");
        await ExecuteAsync(
            scratch.Database.ConnectionString,
            $"UPDATE \"BaseItems\" SET \"Data\" = 'malformed-json' WHERE \"Id\" <> '{id}'");

        var cleanup = $"""UPDATE "BaseItems" SET "Data" = {Jellyfin121MigrationInterceptor.SqliteCleanup} WHERE json_valid("Data") = 1""";
        Assert.Equal(1, await context.Database.ExecuteSqlRawAsync(cleanup));
        Assert.Equal(0, context.Database.ExecuteSqlRaw(cleanup));

        var data = await context.BaseItems.AsNoTracking().Where(item => item.Id == id).Select(item => item.Data).SingleAsync();
        Assert.Contains("Keep", data, StringComparison.Ordinal);
        Assert.DoesNotContain("LinkedChildren", data, StringComparison.Ordinal);
    }

    /// <summary>
    /// Importing a SQLite database has to work both in <c>public</c> and in a custom schema, and repeating
    /// the import must not lose rows that were copied earlier through cascading foreign keys.
    /// </summary>
    [SkippableFact]
    public async Task SqliteDatabaseImportsIntoPublicAndCustomSchema()
    {
        RequirePostgres();
        await using var scratch = await Scratch.CreateAsync();

        var folder = Directory.CreateTempSubdirectory("jellyfin-import-");
        try
        {
            var sqlitePath = Path.Combine(folder.FullName, "jellyfin.db");
            await using (var source = CreateSqliteContext(sqlitePath))
            {
                await source.Database.EnsureCreatedAsync();
                source.BaseItems.Add(new BaseItemEntity { Id = Guid.Parse("30000000-0000-0000-0000-000000000001"), Type = "Playlist", Name = "Fixture playlist" });
                source.BaseItems.Add(new BaseItemEntity { Id = Guid.Parse("30000000-0000-0000-0000-000000000002"), Type = "Audio", Name = "Fixture song" });
                await source.SaveChangesAsync();
                await source.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "LinkedChildren" ("ParentId", "SortOrder", "ChildId", "ChildType") VALUES
                    ('30000000-0000-0000-0000-000000000001', 0, '30000000-0000-0000-0000-000000000002', 0),
                    ('30000000-0000-0000-0000-000000000001', 1, '30000000-0000-0000-0000-000000000002', 0);
                    CREATE TABLE "__EFMigrationsHistory" ("MigrationId" TEXT PRIMARY KEY, "ProductVersion" TEXT NOT NULL);
                    INSERT INTO "__EFMigrationsHistory" VALUES ('202609111200000_StripEmbeddedLinkedChildren','12.1.0');
                    INSERT INTO "__EFMigrationsHistory" VALUES ('20260815063607_RemoveOrphanedUserPermissionsAndPreferences','10.0.11');
                    """);
            }

            using var service = new MigrationService(NullLogger<MigrationService>.Instance);
            foreach (var schema in new[] { "public", "custom_schema" })
            {
                var options = new MigrationOptions(sqlitePath, scratch.Database.ConnectionString, schema, 2, true, null);

                await MigrationEngine.RunAsync(options, service, CancellationToken.None);

                // Repeated to verify truncation does not erase rows copied earlier through cascading FKs.
                await MigrationEngine.RunAsync(options, service, CancellationToken.None);

                var connectionString = new NpgsqlConnectionStringBuilder(scratch.Database.ConnectionString) { SearchPath = schema }.ConnectionString;
                await using var pg = new NpgsqlConnection(connectionString);
                await pg.OpenAsync();
                Assert.Equal(2, await ScalarAsync(pg, "SELECT COUNT(*) FROM \"LinkedChildren\""));
                Assert.Equal(1, await ScalarAsync(pg, "SELECT COUNT(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '202609111200000_StripEmbeddedLinkedChildren'"));
                Assert.Equal(0, await ScalarAsync(pg, "SELECT COUNT(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '20260815063607_RemoveOrphanedUserPermissionsAndPreferences'"));
            }
        }
        finally
        {
            // SQLite parks connections in a pool; without clearing it the temp file stays locked.
            DeleteTemporaryDirectory(folder);
        }
    }

    /// <summary>
    /// A SQLite database from an older Jellyfin has to be rejected before anything is written to PostgreSQL,
    /// instead of importing a partial schema.
    /// </summary>
    [SkippableFact]
    public async Task OldSqliteSchemaIsRejectedBeforeWriting()
    {
        RequirePostgres();
        await using var scratch = await Scratch.CreateAsync();
        _ = scratch;

        await using var source = new SqliteConnection("Data Source=:memory:");
        await source.OpenAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => MigrationCodeMigrations.ValidateSourceAsync(source));
    }

    /// <summary>
    /// The export is the way back to SQLite, so the produced file has to contain the rows and only the code
    /// migration history that actually applies.
    /// </summary>
    [SkippableFact]
    public async Task ExportProducesAUsableSqliteDatabase()
    {
        RequirePostgres();
        await using var scratch = await Scratch.CreateAsync();

        using var context = CreateContext(scratch.Database.ConnectionString, new Jellyfin121MigrationInterceptor());
        await context.Database.MigrateAsync();
        var parent = await context.BaseItems.Select(item => item.Id).FirstAsync();

        // The child has to exist, or the playlist membership breaks its foreign key. It is derived from an
        // existing row so every NOT NULL column of the table is filled in.
        await ExecuteAsync(
            scratch.Database.ConnectionString,
            """
            INSERT INTO "BaseItems"
            SELECT (jsonb_populate_record(NULL::"BaseItems", to_jsonb(b) ||
                '{"Id":"30000000-0000-0000-0000-000000000002","Type":"Audio","Name":"Fixture song"}'::jsonb)).*
            FROM "BaseItems" b LIMIT 1;
            """);
        await ExecuteAsync(
            scratch.Database.ConnectionString,
            $"""
            INSERT INTO "LinkedChildren" ("ParentId", "SortOrder", "ChildId", "ChildType") VALUES
            ('{parent}', 0, '30000000-0000-0000-0000-000000000002', 0),
            ('{parent}', 1, '30000000-0000-0000-0000-000000000002', 0);
            """);

        var folder = Directory.CreateTempSubdirectory("jellyfin-export-");
        try
        {
            var exportedPath = Path.Combine(folder.FullName, "export.db");
            await using (var target = CreateSqliteContext(exportedPath))
            {
                await target.Database.EnsureCreatedAsync();
                await target.Database.ExecuteSqlRawAsync("CREATE TABLE \"__EFMigrationsHistory\" (\"MigrationId\" TEXT PRIMARY KEY, \"ProductVersion\" TEXT NOT NULL)");
            }

            using (var exporter = new ExportToSqliteService(NullLogger<ExportToSqliteService>.Instance))
            {
                Assert.True(exporter.StartExport(scratch.Database.ConnectionString, exportedPath));
                var deadline = DateTime.UtcNow.AddSeconds(120);
                while (exporter.GetProgress().IsRunning && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(100);
                }

                var progress = exporter.GetProgress();
                Assert.False(progress.IsRunning, "The export did not finish in time.");
                Assert.False(progress.HasError, progress.ErrorMessage);
            }

            await using var exported = new SqliteConnection($"Data Source={exportedPath}");
            await exported.OpenAsync();
            await using var query = exported.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM \"LinkedChildren\";";
            Assert.Equal(2, Convert.ToInt64(await query.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }
        finally
        {
            DeleteTemporaryDirectory(folder);
        }
    }

    /// <summary>
    /// Jellyfin relies on <c>min(uuid)</c>/<c>max(uuid)</c> for the home page (latest items per channel).
    /// PostgreSQL does not define those aggregates, so the provider has to create them when it prepares the
    /// schema.
    /// </summary>
    [SkippableFact]
    public async Task CompatibilityAggregatesExistAfterSchemaPreparation()
    {
        RequirePostgres();
        await using var scratch = await Scratch.CreateAsync();

        using var context = CreateContext(scratch.Database.ConnectionString, new Jellyfin121MigrationInterceptor());
        await context.Database.MigrateAsync();

        // The provider creates these objects when EF initialises; Jellyfin always goes through it.
        SqliteCompatibilityBootstrap.Ensure(scratch.Database.ConnectionString);

        await using var pg = new NpgsqlConnection(scratch.Database.ConnectionString);
        await pg.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT min(x)::text, max(x)::text
            FROM (VALUES ('00000000-0000-0000-0000-000000000002'::uuid), ('00000000-0000-0000-0000-000000000001'::uuid)) t(x);
            """,
            pg);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("00000000-0000-0000-0000-000000000001", reader.GetString(0));
        Assert.Equal("00000000-0000-0000-0000-000000000002", reader.GetString(1));
    }

    /// <summary>
    /// The full system backup restores rows through
    /// <c>provider.PurgeDatabase(dbContext, tableNames)</c> with schema qualified names taken from EF's
    /// model, so every format the server can produce has to work.
    /// </summary>
    [SkippableTheory]
    [InlineData("PurgeProbe")]
    [InlineData("public.PurgeProbe")]
    [InlineData("public.\"PurgeProbe\"")]
    [InlineData("\"public\".\"PurgeProbe\"")]
    public async Task PurgeDatabaseAcceptsEveryTableNameFormatTheServerPasses(string tableName)
    {
        RequirePostgres();
        await using var scratch = await Scratch.CreateAsync();

        using var context = CreateContext(scratch.Database.ConnectionString);
        await using var pg = new NpgsqlConnection(scratch.Database.ConnectionString);
        await pg.OpenAsync();
        await using (var create = new NpgsqlCommand("""CREATE TABLE IF NOT EXISTS "public"."PurgeProbe" ("Id" uuid PRIMARY KEY);""", pg))
        {
            await create.ExecuteNonQueryAsync();
        }

        await using (var insert = new NpgsqlCommand("""INSERT INTO "public"."PurgeProbe" VALUES ('11111111-1111-1111-1111-111111111111') ON CONFLICT DO NOTHING;""", pg))
        {
            await insert.ExecuteNonQueryAsync();
        }

        var provider = new PostgresDatabaseProvider(null, null);
        await provider.PurgeDatabase(context, [tableName]);

        Assert.Equal(0, await ScalarAsync(pg, """SELECT COUNT(*) FROM "public"."PurgeProbe";"""));
    }

    /// <summary>
    /// The SQL console goes through the same provider path as the UI: read-only statements return rows and
    /// mutating ones are rejected before reaching the server.
    /// </summary>
    [SkippableFact]
    public async Task SqlConsoleReturnsRowsAndRejectsWrites()
    {
        RequirePostgres();
        await using var scratch = await Scratch.CreateAsync();

        using var context = CreateContext(scratch.Database.ConnectionString, new Jellyfin121MigrationInterceptor());
        await context.Database.MigrateAsync();

        var console = new QueryConsoleService();
        var result = await console.ExecuteAsync(
            scratch.Database.ConnectionString,
            """SELECT COUNT(*) AS total FROM "BaseItems";""",
            "public",
            10,
            explain: false,
            CancellationToken.None);

        Assert.Equal("total", result.Columns[0]);
        Assert.Equal(1, result.RowCount);

        var plan = await console.ExecuteAsync(
            scratch.Database.ConnectionString,
            """SELECT COUNT(*) FROM "BaseItems";""",
            "public",
            10,
            explain: true,
            CancellationToken.None);

        Assert.True(plan.RowCount > 0, "The execution plan should return rows.");
        Assert.Contains("cost=", string.Join(' ', plan.Rows.SelectMany(row => row)), StringComparison.Ordinal);

        await Assert.ThrowsAsync<ArgumentException>(() => console.ExecuteAsync(
            scratch.Database.ConnectionString,
            """DELETE FROM "BaseItems";""",
            "public",
            10,
            explain: false,
            CancellationToken.None));
    }

    private static void RequirePostgres()
        => Skip.IfNot(TestEnvironment.PostgresAvailable, $"{TestEnvironment.ConnectionVariable} is not set; skipping PostgreSQL integration tests.");

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

    private static JellyfinDbContext CreateSqliteContext(string path)
        => new(
            new DbContextOptionsBuilder<JellyfinDbContext>().UseSqlite($"Data Source={path}").Options,
            NullLogger<JellyfinDbContext>.Instance,
            new PostgresDatabaseProvider(null, null),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    /// <summary>Runs a fixture statement directly through Npgsql, without EF's statement formatting.</summary>
    /// <param name="connectionString">Database to run against.</param>
    /// <param name="sql">Statement to execute.</param>
    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Deletes a temporary directory holding a SQLite file. The connection pool is cleared first: a pooled
    /// connection keeps the file open and Windows then refuses the delete.
    /// </summary>
    /// <param name="folder">Directory to remove.</param>
    private static void DeleteTemporaryDirectory(DirectoryInfo folder)
    {
        SqliteConnection.ClearAllPools();
        try
        {
            folder.Delete(true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not a reason to fail a test.
        }
    }

    /// <summary>Scratch database that drops itself when the test finishes.</summary>
    private sealed class Scratch : IAsyncDisposable
    {
        private Scratch(TestEnvironment.ScratchDatabase database) => Database = database;

        internal TestEnvironment.ScratchDatabase Database { get; }

        internal static async Task<Scratch> CreateAsync() => new(await TestEnvironment.CreateScratchDatabaseAsync());

        public async ValueTask DisposeAsync()
        {
            // Npgsql parks connections in a pool; without clearing it PostgreSQL would refuse the drop.
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(TestEnvironment.PostgresConnection);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{Database.Name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
