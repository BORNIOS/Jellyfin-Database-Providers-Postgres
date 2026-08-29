using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Locking;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Database.Providers.Postgres;

/// <summary>
/// Design-time factory for generating EF Core migrations targeting PostgreSQL.
/// Used only by <c>dotnet ef migrations add</c> — never at runtime.
/// </summary>
internal sealed class PostgresDesignTimeDbFactory : IDesignTimeDbContextFactory<JellyfinDbContext>
{
    public JellyfinDbContext CreateDbContext(string[] args)
    {
        // Note: Npgsql.EnableLegacyTimestampBehavior is NOT set — DateTime.Kind=Unspecified
        // is handled at runtime by DateTimeKindNormalizingInterceptor, not by the global switch.

        // A real connection is not required for migration generation — EF Core
        // only needs to resolve the model from the DbContext.
        var connectionString = args.Length > 0
            ? args[0]
            : "Host=localhost;Database=jellyfindesign;Username=postgres;Password=postgres";

        var optionsBuilder = new DbContextOptionsBuilder<JellyfinDbContext>();
        optionsBuilder.UseNpgsql(
            connectionString,
            npgsqlOptions => npgsqlOptions.MigrationsAssembly(
                typeof(PostgresDesignTimeDbFactory).Assembly.GetName().Name!));

        return new JellyfinDbContext(
            optionsBuilder.Options,
            NullLogger<JellyfinDbContext>.Instance,
            new PostgresDatabaseProvider(null, NullLogger<PostgresDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }
}
