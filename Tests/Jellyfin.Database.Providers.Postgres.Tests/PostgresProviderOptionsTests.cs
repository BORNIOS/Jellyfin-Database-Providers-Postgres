using Jellyfin.Database.Providers.Postgres.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>
/// Provider options that are applied regardless of the connection string.
/// </summary>
public sealed class PostgresProviderOptionsTests
{
    /// <summary>
    /// Jellyfin's item queries project several collection navigations, and EF Core logs
    /// <c>MultipleCollectionIncludeWarning</c> for every compiled shape while the splitting behaviour is
    /// left unset. It must be explicit: this keeps SingleQuery execution (Jellyfin's default) and stops
    /// the warning at its source instead of hiding it with <c>ConfigureWarnings</c>.
    /// </summary>
    [Fact]
    public void QuerySplittingBehaviourIsExplicitSoEfCoreDoesNotWarnOnEveryCompiledShape()
    {
        var options = BuildOptions(commandTimeout: 0);

        Assert.Equal(QuerySplittingBehavior.SingleQuery, ExtractRelationalOptions(options).QuerySplittingBehavior);
    }

    [Fact]
    public void CommandTimeoutIsOnlyAppliedWhenPositive()
    {
        Assert.Null(ExtractRelationalOptions(BuildOptions(commandTimeout: 0)).CommandTimeout);
        Assert.Equal(300, ExtractRelationalOptions(BuildOptions(commandTimeout: 300)).CommandTimeout);
    }

    [Fact]
    public void MigrationsAssemblyIsAlwaysConfigured()
    {
        var migrationsAssembly = ExtractRelationalOptions(BuildOptions(commandTimeout: 0)).MigrationsAssembly;

        Assert.False(string.IsNullOrWhiteSpace(migrationsAssembly));
    }

    private static DbContextOptions BuildOptions(int commandTimeout)
    {
        var builder = new DbContextOptionsBuilder();
        builder.UseNpgsql(
            "Host=localhost;Port=5432;Database=jellyfin;Username=jellyfin",
            npgsql => PostgresDatabaseProvider.ConfigureNpgsql(npgsql, commandTimeout));
        return builder.Options;
    }

    private static RelationalOptionsExtension ExtractRelationalOptions(DbContextOptions options)
        => options.Extensions.OfType<RelationalOptionsExtension>().Single();
}
