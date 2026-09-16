using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

using var context = new JellyfinDbContext(
    new DbContextOptionsBuilder<JellyfinDbContext>()
        .UseNpgsql("Host=localhost;Database=unused;Username=unused",
            pg => pg.MigrationsAssembly(typeof(PostgresDatabaseProvider).Assembly.FullName))
        .Options,
    NullLogger<JellyfinDbContext>.Instance,
    new PostgresDatabaseProvider(null, null),
    new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));

Check(!context.Database.HasPendingModelChanges(), "Snapshot matches Jellyfin 12.1 model");
var script = context.GetService<IMigrator>().GenerateScript();
Check(script.Contains("CREATE TABLE \"LinkedChildren\""), "Playlist membership schema exists");
Check(script.Contains("USING CASE WHEN replace(\"PrimaryVersionId\""), "Legacy version IDs explicitly convert to UUID");
Check(!script.Contains("RENAME COLUMN \"ExtraIds\" TO \"OriginalLanguage\""), "Language does not inherit legacy extra IDs");
Check(!script.Contains("[UserId]"), "Generated PostgreSQL SQL has no SQLite identifier syntax");
var table = context.Model.GetRelationalModel().Tables.Single(t => t.Name == "LinkedChildren");
Check(table.PrimaryKey!.Columns.Select(c => c.Name).SequenceEqual(new[] { "ParentId", "SortOrder" }),
    "Playlists support repeated children at different positions");

// Contracts the plugin relies on in the Jellyfin server itself, not in the database engine.
ContractChecks.VerifyServerContract();
ContractChecks.VerifyDatabaseConfigurationContract();
ContractChecks.VerifyVersionAlignment();
ContractChecks.VerifyRawSqlSchema(context);
ContractChecks.VerifyLogging();
var connection = Environment.GetEnvironmentVariable("POSTGRES_TEST_CONNECTION");
if (!string.IsNullOrWhiteSpace(connection)) await PostgresChecks.RunAsync(connection);
Console.WriteLine("All compatibility checks passed.");
static void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    Console.WriteLine($"PASS: {description}");
}
