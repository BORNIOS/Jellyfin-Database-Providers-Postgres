using System.Globalization;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Postgres.Services;
using Jellyfin.Database.Providers.Postgres.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>
/// Schema drift: a Jellyfin release whose model is ahead of the provider's migrations. The integration tests rebuild
/// the 12.1 shape of the two OwnerId indexes on a fully migrated database and compare against the model of the last
/// migration before <c>Jellyfin124</c>, which is exactly what plugin 3.1.0 saw when running on Jellyfin 12.4.
/// </summary>
public sealed class SchemaDriftGuardTests
{
    private const string BeforeJellyfin124 = "_NormalizeInvalidBaseItemDates";

    [Theory]
    [InlineData("CREATE INDEX \"IX_A\" ON \"T\" (\"c\");", "CREATE INDEX IF NOT EXISTS \"IX_A\" ON \"T\" (\"c\");")]
    [InlineData("CREATE UNIQUE INDEX \"IX_A\" ON \"T\" (\"c\");", "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_A\" ON \"T\" (\"c\");")]
    [InlineData("CREATE INDEX CONCURRENTLY \"IX_A\" ON \"T\" (\"c\");", "CREATE INDEX CONCURRENTLY IF NOT EXISTS \"IX_A\" ON \"T\" (\"c\");")]
    [InlineData("DROP INDEX \"IX_A\";", "DROP INDEX IF EXISTS \"IX_A\";")]
    [InlineData("DROP INDEX \"public\".\"IX_A\";", "DROP INDEX IF EXISTS \"public\".\"IX_A\";")]
    [InlineData("CREATE TABLE \"T\" (\n    \"Id\" integer);", "CREATE TABLE IF NOT EXISTS \"T\" (\n    \"Id\" integer);")]
    [InlineData("ALTER TABLE \"T\" ADD \"c\" text;", "ALTER TABLE \"T\" ADD COLUMN IF NOT EXISTS \"c\" text;")]
    [InlineData("ALTER TABLE \"s\".\"T\" ADD \"c\" text;", "ALTER TABLE \"s\".\"T\" ADD COLUMN IF NOT EXISTS \"c\" text;")]
    [InlineData("ALTER TABLE \"T\" ADD CONSTRAINT \"FK\" FOREIGN KEY (\"c\") REFERENCES \"U\" (\"Id\");", null)]
    [InlineData("ALTER TABLE \"T\" ALTER COLUMN \"c\" TYPE uuid;", null)]
    [InlineData("CREATE INDEX IF NOT EXISTS \"IX_A\" ON \"T\" (\"c\");", null)]
    [InlineData("UPDATE \"T\" SET \"c\" = 'CREATE INDEX \"x\"';", null)]
    public void MakeIdempotentRewritesOnlyAdditiveStatements(string sql, string? expected)
        => Assert.Equal(expected ?? sql, IdempotentMigrationsSqlGenerator.MakeIdempotent(sql));

    [Fact]
    public void TheProviderGeneratorEmitsIdempotentSql()
    {
        using var context = CreateContext("Host=localhost;Database=unused;Username=unused");
        var generator = context.GetService<IMigrationsSqlGenerator>();
        Assert.IsType<IdempotentMigrationsSqlGenerator>(generator);

        var sql = string.Concat(generator.Generate(
            [
                new CreateTableOperation { Name = "DriftT", Columns = { new AddColumnOperation { Name = "Id", Table = "DriftT", ClrType = typeof(int), ColumnType = "integer" } } },
                new AddColumnOperation { Table = "DriftT", Name = "Extra", ClrType = typeof(string), ColumnType = "text", IsNullable = true },
                new CreateIndexOperation { Name = "IX_DriftT_Extra", Table = "DriftT", Columns = ["Extra"], Filter = "\"Extra\" IS NOT NULL" },
                new DropIndexOperation { Name = "IX_DriftT_Extra", Table = "DriftT" },
            ]).Select(c => c.CommandText));

        Assert.Contains("CREATE TABLE IF NOT EXISTS \"DriftT\"", sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN IF NOT EXISTS \"Extra\" text", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX IF NOT EXISTS \"IX_DriftT_Extra\"", sql, StringComparison.Ordinal);
        Assert.Contains("DROP INDEX IF EXISTS \"IX_DriftT_Extra\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyAdditiveOperationsAreSafe()
    {
        Assert.True(SchemaDriftGuard.IsSafe(new CreateIndexOperation { Name = "i", Table = "t", Columns = ["c"] }));
        Assert.True(SchemaDriftGuard.IsSafe(new DropIndexOperation { Name = "i" }));
        Assert.True(SchemaDriftGuard.IsSafe(new CreateTableOperation { Name = "t" }));
        Assert.True(SchemaDriftGuard.IsSafe(new AddColumnOperation { Table = "t", Name = "c", ClrType = typeof(string), IsNullable = true }));
        Assert.True(SchemaDriftGuard.IsSafe(new AddColumnOperation { Table = "t", Name = "c", ClrType = typeof(int), DefaultValue = 0 }));

        // Duplicates make a unique index fail; Jellyfin cleans them up with SQL the model diff does not contain.
        Assert.False(SchemaDriftGuard.IsSafe(new CreateIndexOperation { Name = "i", Table = "t", Columns = ["c"], IsUnique = true }));

        // These rewrite the whole table under an exclusive lock.
        Assert.False(SchemaDriftGuard.IsSafe(new AddColumnOperation { Table = "t", Name = "c", ClrType = typeof(DateTime), DefaultValueSql = "now()" }));
        Assert.False(SchemaDriftGuard.IsSafe(new AddColumnOperation { Table = "t", Name = "c", ClrType = typeof(int), IsNullable = true, ComputedColumnSql = "\"a\" + 1", IsStored = true }));

        Assert.False(SchemaDriftGuard.IsSafe(new AddColumnOperation { Table = "t", Name = "c", ClrType = typeof(int) }));
        Assert.False(SchemaDriftGuard.IsSafe(new DropColumnOperation { Table = "t", Name = "c" }));
        Assert.False(SchemaDriftGuard.IsSafe(new AlterColumnOperation { Table = "t", Name = "c", ClrType = typeof(Guid) }));
        Assert.False(SchemaDriftGuard.IsSafe(new DropTableOperation { Name = "t" }));
        Assert.False(SchemaDriftGuard.IsSafe(new RenameColumnOperation { Table = "t", Name = "a", NewName = "b" }));
        Assert.False(SchemaDriftGuard.IsSafe(new AddForeignKeyOperation { Table = "t", Name = "fk", Columns = ["c"], PrincipalTable = "u" }));
    }

    [Theory]
    [InlineData("heal", null, SchemaDriftMode.Heal)]
    [InlineData("REPORT", null, SchemaDriftMode.ReportOnly)]
    [InlineData("report-only", null, SchemaDriftMode.ReportOnly)]
    [InlineData("none", null, SchemaDriftMode.Off)]
    [InlineData("disable", SchemaDriftMode.Heal, SchemaDriftMode.Off)]
    [InlineData("typo", null, SchemaDriftMode.ReportOnly)]
    [InlineData("typo", SchemaDriftMode.Off, SchemaDriftMode.Off)]
    [InlineData("", SchemaDriftMode.ReportOnly, SchemaDriftMode.ReportOnly)]
    public void AnUnknownModeNeverTurnsOnAutomaticChanges(string value, SchemaDriftMode? configured, SchemaDriftMode expected)
    {
        var previous = Environment.GetEnvironmentVariable(SchemaDriftGuard.ModeVariable);
        try
        {
            Environment.SetEnvironmentVariable(SchemaDriftGuard.ModeVariable, value);
            Assert.Equal(expected, SchemaDriftGuard.ResolveMode(configured));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SchemaDriftGuard.ModeVariable, previous);
        }
    }

    [Theory]
    [InlineData("\"OwnerId\" IS NOT NULL", "(\"OwnerId\" IS NOT NULL)")]
    [InlineData("\"Type\" = 'Movie'", "((\"Type\")::text = 'Movie'::text)")]
    [InlineData("\"A\" IS NULL AND \"B\" = 0", "((\"A\" IS NULL) AND (\"B\" = 0))")]
    [InlineData(null, null)]
    public void PredicatesMatchTheFormPostgreSqlStores(string? model, string? stored)
        => Assert.Equal(SchemaFacts.NormalizePredicate(model), SchemaFacts.NormalizePredicate(stored));

    [Fact]
    public void DifferentPredicatesDoNotMatch()
        => Assert.NotEqual(SchemaFacts.NormalizePredicate("\"OwnerId\" IS NOT NULL"), SchemaFacts.NormalizePredicate("(\"OwnerId\" IS NULL)"));

    [Fact]
    public void IndexNamesAreLookedUpAsPostgreSqlTruncatesThem()
    {
        var name = "IX_BaseItems_Type_SeriesPresentationUniqueKey_PresentationUniqueKey_SortName";
        var stored = SchemaFacts.StoredName(name);
        Assert.Equal(63, stored.Length);
        var facts = SchemaFacts.Create(["BaseItems"], [], new Dictionary<string, SchemaFacts.IndexFact> { [stored] = new("BaseItems", false, ["Type"], null) });
        Assert.True(facts.Matches(new CreateIndexOperation { Name = name, Table = "BaseItems", Columns = ["Type"] }));
        Assert.False(facts.Matches(new CreateIndexOperation { Name = name, Table = "BaseItems", Columns = ["Type"], Filter = "\"Type\" IS NOT NULL" }));
    }

    [Fact]
    public void HealthFindingsFollowTheOutcome()
    {
        static SchemaDriftReport Report(SchemaDriftOutcome outcome)
            => new(DateTime.UtcNow, SchemaDriftMode.Heal, outcome, "12.4", "3.2", [new SchemaDriftOperation("CreateIndex i on t", "sql", true, false)], "msg");

        Assert.Empty(SchemaDriftGuard.ToFindings(null));
        Assert.Empty(SchemaDriftGuard.ToFindings(Report(SchemaDriftOutcome.InSync)));
        Assert.Equal(HealthSeverity.Error, Assert.Single(SchemaDriftGuard.ToFindings(Report(SchemaDriftOutcome.Blocked))).Severity);
        Assert.Equal(HealthSeverity.Warn, Assert.Single(SchemaDriftGuard.ToFindings(Report(SchemaDriftOutcome.Detected))).Severity);
        Assert.Equal(HealthSeverity.Ok, Assert.Single(SchemaDriftGuard.ToFindings(Report(SchemaDriftOutcome.Healed))).Severity);
    }

    [Theory]
    [InlineData("<PluginConfiguration><SchemaDriftMode>ReportOnly</SchemaDriftMode></PluginConfiguration>", SchemaDriftMode.ReportOnly)]
    [InlineData("<PluginConfiguration><SchemaDriftMode>Off</SchemaDriftMode></PluginConfiguration>", SchemaDriftMode.Off)]
    [InlineData("<PluginConfiguration><Schema>public</Schema></PluginConfiguration>", null)]
    [InlineData("not xml", null)]
    public void TheEarlyCheckReadsTheModeFromThePluginConfigurationFile(string xml, SchemaDriftMode? expected)
    {
        var directory = Directory.CreateTempSubdirectory("schema-drift-config-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "Jellyfin.Database.Providers.Postgres.xml"), xml);
            Assert.Equal(expected, SchemaDriftGuard.ReadConfiguredMode(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void TheReportSurvivesTheRoundTripBetweenLoadContexts()
    {
        var report = new SchemaDriftReport(DateTime.UtcNow, SchemaDriftMode.Heal, SchemaDriftOutcome.Healed, "12.4", "3.2", [new SchemaDriftOperation("CreateIndex i on t", "CREATE INDEX IF NOT EXISTS i", true, true)], "msg");
        var copy = System.Text.Json.JsonSerializer.Deserialize<SchemaDriftReport>(System.Text.Json.JsonSerializer.Serialize(report));
        Assert.NotNull(copy);
        Assert.Equal(report.Outcome, copy.Outcome);
        Assert.Equal(report.Operations.Single(), copy.Operations.Single());
    }

    [SkippableFact]
    public async Task AFullyMigratedDatabaseIsInSync()
    {
        RequirePostgres();
        await using var scratch = await TestEnvironment.ScratchDatabaseScope.CreateAsync();
        using var context = await MigratedContextAsync(scratch.Database.ConnectionString);

        var report = await SchemaDriftGuard.RunAsync(context, SchemaDriftMode.Heal);

        Assert.True(report.Outcome == SchemaDriftOutcome.InSync, report.Message);
        Assert.Empty(report.Operations);
    }

    [SkippableFact]
    public async Task PendingProviderMigrationsDeferTheCheck()
    {
        RequirePostgres();
        await using var scratch = await TestEnvironment.ScratchDatabaseScope.CreateAsync();
        using var context = CreateContext(scratch.Database.ConnectionString);
        await context.GetService<IMigrator>().MigrateAsync("00000000000000_InitialCreate");

        var report = await SchemaDriftGuard.RunAsync(context, SchemaDriftMode.Heal);

        Assert.Equal(SchemaDriftOutcome.Deferred, report.Outcome);
    }

    /// <summary>
    /// Plugin 3.1.0 on Jellyfin 12.4: the guard turns both OwnerId indexes partial, a second start finds them
    /// recorded, and the provider's own Jellyfin124 migration still applies afterwards.
    /// </summary>
    [SkippableFact]
    public async Task IndexDriftIsHealedOnceAndTheLaterMigrationStillApplies()
    {
        RequirePostgres();
        await using var scratch = await TestEnvironment.ScratchDatabaseScope.CreateAsync();
        var connectionString = scratch.Database.ConnectionString;
        using var context = await MigratedContextAsync(connectionString);
        await RecreateJellyfin121IndexesAsync(connectionString);
        Assert.Equal(0, await PartialOwnerIndexesAsync(connectionString));

        var baseline = TargetModelOf(context, BeforeJellyfin124);
        var healed = await SchemaDriftGuard.RunAsync(context, SchemaDriftMode.Heal, baseline);

        Assert.True(healed.Outcome == SchemaDriftOutcome.Healed, healed.Message);
        Assert.Equal(4, healed.Operations.Count);
        Assert.All(healed.Operations, o => Assert.True(o.Safe && o.Applied));
        Assert.Equal(2, await PartialOwnerIndexesAsync(connectionString));
        Assert.Equal(4, await ScalarAsync(connectionString, $"SELECT COUNT(*) FROM \"{SchemaDriftGuard.RecordTable}\";"));

        var again = await SchemaDriftGuard.RunAsync(context, SchemaDriftMode.Heal, baseline);
        Assert.Equal(SchemaDriftOutcome.AlreadyHealed, again.Outcome);
        Assert.Equal(4, await ScalarAsync(connectionString, $"SELECT COUNT(*) FROM \"{SchemaDriftGuard.RecordTable}\";"));

        // The provider update arrives: its migration runs over the objects the guard created.
        await ExecuteAsync(connectionString, "DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" LIKE '%\\_Jellyfin124';");
        await context.Database.MigrateAsync();
        Assert.Equal(2, await PartialOwnerIndexesAsync(connectionString));
        var final = await SchemaDriftGuard.RunAsync(context, SchemaDriftMode.Heal);
        Assert.True(final.Outcome == SchemaDriftOutcome.InSync, final.Message);
    }

    [SkippableFact]
    public async Task ReportOnlyLeavesTheSchemaUntouched()
    {
        RequirePostgres();
        await using var scratch = await TestEnvironment.ScratchDatabaseScope.CreateAsync();
        var connectionString = scratch.Database.ConnectionString;
        using var context = await MigratedContextAsync(connectionString);
        await RecreateJellyfin121IndexesAsync(connectionString);

        var report = await SchemaDriftGuard.RunAsync(context, SchemaDriftMode.ReportOnly, TargetModelOf(context, BeforeJellyfin124));

        Assert.Equal(SchemaDriftOutcome.Detected, report.Outcome);
        Assert.All(report.Operations, o => Assert.False(o.Applied));
        Assert.Equal(0, await PartialOwnerIndexesAsync(connectionString));
    }

    /// <summary>Drift that would drop or retype columns is never applied, whatever the mode.</summary>
    [SkippableFact]
    public async Task UnsafeDriftIsBlockedAndNothingIsApplied()
    {
        RequirePostgres();
        await using var scratch = await TestEnvironment.ScratchDatabaseScope.CreateAsync();
        var connectionString = scratch.Database.ConnectionString;
        using var context = await MigratedContextAsync(connectionString);
        await RecreateJellyfin121IndexesAsync(connectionString);

        var report = await SchemaDriftGuard.RunAsync(context, SchemaDriftMode.Heal, TargetModelOf(context, "_InitialCreate"));

        Assert.Equal(SchemaDriftOutcome.Blocked, report.Outcome);
        Assert.Contains(report.Operations, o => !o.Safe);

        // "Applied" reports what the catalog shows; what matters is that the guard changed nothing.
        Assert.Equal(0, await PartialOwnerIndexesAsync(connectionString));
        Assert.Equal(0, await ScalarAsync(connectionString, $"SELECT COUNT(*) FROM pg_class WHERE relname = '{SchemaDriftGuard.RecordTable}';"));
    }

    /// <summary>
    /// An index left with an older definition (for example by an idempotent provider migration that found it already
    /// there) is invisible to the snapshot/model comparison; the guard finds it in the catalog and rebuilds it.
    /// </summary>
    [SkippableFact]
    public async Task AnIndexWithAnotherDefinitionIsFoundInTheCatalogAndRebuilt()
    {
        RequirePostgres();
        await using var scratch = await TestEnvironment.ScratchDatabaseScope.CreateAsync();
        var connectionString = scratch.Database.ConnectionString;
        using var context = await MigratedContextAsync(connectionString);
        await RecreateJellyfin121IndexesAsync(connectionString);

        var report = await SchemaDriftGuard.RunAsync(context, SchemaDriftMode.Heal);

        Assert.True(report.Outcome == SchemaDriftOutcome.Healed, report.Message);
        Assert.Contains(report.Operations, o => o.Description.StartsWith("CreateIndex IX_BaseItems_OwnerId", StringComparison.Ordinal));
        Assert.Equal(2, await PartialOwnerIndexesAsync(connectionString));
        var again = await SchemaDriftGuard.RunAsync(context, SchemaDriftMode.Heal);
        Assert.True(again.Outcome == SchemaDriftOutcome.InSync, again.Message);
    }

    /// <summary>
    /// What is in place is read from the catalog, not from the guard's records: a recorded change that the database
    /// no longer reflects is not reported as healed (and is not rebuilt again in the same version).
    /// </summary>
    [SkippableFact]
    public async Task ARecordedChangeIsNotReportedAsHealedWhenTheDatabaseDiffers()
    {
        RequirePostgres();
        await using var scratch = await TestEnvironment.ScratchDatabaseScope.CreateAsync();
        var connectionString = scratch.Database.ConnectionString;
        using var context = await MigratedContextAsync(connectionString);
        await RecreateJellyfin121IndexesAsync(connectionString);
        var baseline = TargetModelOf(context, BeforeJellyfin124);
        Assert.Equal(SchemaDriftOutcome.Healed, (await SchemaDriftGuard.RunAsync(context, SchemaDriftMode.Heal, baseline)).Outcome);

        await RecreateJellyfin121IndexesAsync(connectionString);
        var report = await SchemaDriftGuard.RunAsync(context, SchemaDriftMode.Heal, baseline);

        Assert.True(report.Outcome == SchemaDriftOutcome.Detected, report.Message);
        Assert.Contains(report.Operations, o => !o.Applied && o.Description.Contains("ya aplicado en esta versión", StringComparison.Ordinal));
        Assert.Equal(0, await PartialOwnerIndexesAsync(connectionString));
    }

    [SkippableFact]
    public async Task StatisticsRefreshSkipsTablesTheDatabaseDoesNotHave()
    {
        RequirePostgres();
        await using var scratch = await TestEnvironment.ScratchDatabaseScope.CreateAsync();
        var connectionString = scratch.Database.ConnectionString;
        using (await MigratedContextAsync(connectionString))
        {
        }

        await ExecuteAsync(connectionString, "DROP TABLE \"TrickplayInfos\";");
        var provider = new PostgresDatabaseProvider(null, null) { DbContextFactory = new Factory(() => CreateContext(connectionString)) };

        await provider.RefreshStatistics(CancellationToken.None);

        Assert.Equal(0, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM pg_stat_user_tables WHERE relname IN ('BaseItems', 'UserData') AND last_analyze IS NULL;"));
    }

    private static void RequirePostgres()
        => Skip.IfNot(TestEnvironment.PostgresAvailable, $"{TestEnvironment.ConnectionVariable} is not set; skipping PostgreSQL integration tests.");

    private static JellyfinDbContext CreateContext(string connectionString)
        => new(
            new DbContextOptionsBuilder<JellyfinDbContext>()
                .UseNpgsql(connectionString, pg => pg.MigrationsAssembly(typeof(PostgresDatabaseProvider).Assembly.FullName))
                .AddInterceptors(new Jellyfin121MigrationInterceptor())
                .ReplaceService<IMigrationsSqlGenerator, IdempotentMigrationsSqlGenerator>()
                .Options,
            NullLogger<JellyfinDbContext>.Instance,
            new PostgresDatabaseProvider(null, null),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));

    private static async Task<JellyfinDbContext> MigratedContextAsync(string connectionString)
    {
        var context = CreateContext(connectionString);
        await context.Database.MigrateAsync();
        return context;
    }

    private static IModel TargetModelOf(DbContext context, string migrationSuffix)
    {
        var assembly = context.GetService<IMigrationsAssembly>();
        var id = assembly.Migrations.Keys.Single(k => k.EndsWith(migrationSuffix, StringComparison.Ordinal));
        return assembly.CreateMigration(assembly.Migrations[id], context.Database.ProviderName!).TargetModel!;
    }

    // The 12.1 definitions, as in Jellyfin124.Down.
    private static Task RecreateJellyfin121IndexesAsync(string connectionString)
        => ExecuteAsync(
            connectionString,
            """
            DROP INDEX "IX_BaseItems_OwnerId";
            DROP INDEX "IX_BaseItems_ExtraType_OwnerId";
            CREATE INDEX "IX_BaseItems_OwnerId" ON "BaseItems" ("OwnerId");
            CREATE INDEX "IX_BaseItems_ExtraType_OwnerId" ON "BaseItems" ("ExtraType", "OwnerId");
            """);

    private static Task<long> PartialOwnerIndexesAsync(string connectionString)
        => ScalarAsync(
            connectionString,
            """
            SELECT COUNT(*) FROM pg_indexes
            WHERE tablename = 'BaseItems'
              AND indexname IN ('IX_BaseItems_OwnerId', 'IX_BaseItems_ExtraType_OwnerId')
              AND indexdef LIKE '% WHERE %IS NOT NULL%';
            """);

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private sealed class Factory(Func<JellyfinDbContext> create) : IDbContextFactory<JellyfinDbContext>
    {
        public JellyfinDbContext CreateDbContext() => create();
    }
}
