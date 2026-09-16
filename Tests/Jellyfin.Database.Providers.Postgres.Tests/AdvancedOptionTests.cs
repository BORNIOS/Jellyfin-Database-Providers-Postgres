using Jellyfin.Database.Providers.Postgres.Controllers.Models;
using Npgsql;
using Xunit;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>
/// The advanced options (pool size, prepared statement cache, timeout) are the ones an administrator tunes
/// from the dashboard. These tests pin the rule that made them work: what the connection string states is
/// respected, and the plugin configuration only fills in what it does not.
/// </summary>
public sealed class AdvancedOptionTests
{
    private const string BaseConnectionString = "Host=127.0.0.1;Port=5432;Database=Jellyfin;Username=Jellyfin;Password=x";

    /// <summary>
    /// The dashboard writes the pool into the connection string, so the provider must not overwrite it with
    /// its own defaults (that is exactly why editing "Max pool size" changed nothing).
    /// </summary>
    [Fact]
    public void PoolFromTheConnectionStringIsRespected()
    {
        var config = new PluginConfiguration { MinPoolSize = 4, MaxPoolSize = 100, MaxAutoPrepare = 50 };

        var tuned = PostgresDatabaseProvider.BuildTunedConnectionString(
            string.Concat(BaseConnectionString, ";Minimum Pool Size=10;Maximum Pool Size=200;Max Auto Prepare=80"),
            config,
            600);

        Assert.Equal(10, tuned.MinPoolSize);
        Assert.Equal(200, tuned.MaxPoolSize);
        Assert.Equal(80, tuned.MaxAutoPrepare);
    }

    /// <summary>
    /// When the connection string does not mention the pool, the saved configuration is the source.
    /// </summary>
    [Fact]
    public void PoolFallsBackToTheSavedConfiguration()
    {
        var config = new PluginConfiguration { MinPoolSize = 6, MaxPoolSize = 250, MaxAutoPrepare = 120 };

        var tuned = PostgresDatabaseProvider.BuildTunedConnectionString(BaseConnectionString, config, 600);

        Assert.Equal(6, tuned.MinPoolSize);
        Assert.Equal(250, tuned.MaxPoolSize);
        Assert.Equal(120, tuned.MaxAutoPrepare);
    }

    /// <summary>
    /// The command timeout is always applied from the saved option: it is what keeps Jellyfin's long code
    /// migrations from aborting halfway.
    /// </summary>
    [Fact]
    public void CommandTimeoutIsAlwaysApplied()
    {
        var tuned = PostgresDatabaseProvider.BuildTunedConnectionString(
            string.Concat(BaseConnectionString, ";Command Timeout=30"),
            new PluginConfiguration(),
            600);

        Assert.Equal(600, tuned.CommandTimeout);
    }

    [Fact]
    public void SavedConfigurationSurvivesTheRoundTrip()
    {
        var config = new PluginConfiguration { MinPoolSize = 8, MaxPoolSize = 300, MaxAutoPrepare = 200 };

        var tuned = PostgresDatabaseProvider.BuildTunedConnectionString(BaseConnectionString, config, 300);
        var reopened = PostgresDatabaseProvider.BuildTunedConnectionString(tuned.ConnectionString, new PluginConfiguration(), 300);

        Assert.Equal(8, reopened.MinPoolSize);
        Assert.Equal(300, reopened.MaxPoolSize);
        Assert.Equal(200, reopened.MaxAutoPrepare);
    }

    /// <summary>
    /// If the request defaults and the plugin defaults drift apart, the dashboard would save a pool nobody
    /// asked for.
    /// </summary>
    [Fact]
    public void RequestDefaultsMatchThePluginDefaults()
    {
        var request = new SaveConfigRequest();
        var config = new PluginConfiguration();

        Assert.Equal(config.MinPoolSize, request.MinPoolSize);
        Assert.Equal(config.MaxPoolSize, request.MaxPoolSize);
        Assert.Equal(config.MaxAutoPrepare, request.MaxAutoPrepare);
    }
}
