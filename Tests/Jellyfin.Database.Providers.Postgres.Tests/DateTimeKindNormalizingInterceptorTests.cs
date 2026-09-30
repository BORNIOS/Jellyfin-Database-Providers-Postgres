using Jellyfin.Database.Providers.Postgres.Services;
using Npgsql;
using Xunit;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>Ensures Npgsql never receives non-UTC timestamp query parameters.</summary>
public sealed class DateTimeKindNormalizingInterceptorTests
{
    /// <summary>Local values retain their instant while unspecified values follow Jellyfin's UTC convention.</summary>
    [Fact]
    public void ReaderParametersAreNormalisedToUtc()
    {
        var local = DateTime.Now;
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        var command = new NpgsqlCommand("SELECT @local, @unspecified");
        command.Parameters.AddWithValue("local", local);
        command.Parameters.AddWithValue("unspecified", unspecified);

        new DateTimeKindNormalizingInterceptor().ReaderExecuting(command, null!, default);

        Assert.Equal(local.ToUniversalTime(), Assert.IsType<DateTime>(command.Parameters["local"].Value));
        Assert.Equal(DateTimeKind.Utc, Assert.IsType<DateTime>(command.Parameters["unspecified"].Value).Kind);
    }

    /// <summary>Npgsql requires one UTC kind for every timestamp array element.</summary>
    [Fact]
    public void TimestampArrayParametersAreNormalisedToUtc()
    {
        var local = DateTime.Now;
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        var command = new NpgsqlCommand("SELECT @timestamps");
        command.Parameters.AddWithValue("timestamps", new[] { local, unspecified });

        new DateTimeKindNormalizingInterceptor().ReaderExecuting(command, null!, default);

        var values = Assert.IsType<DateTime[]>(command.Parameters["timestamps"].Value);
        Assert.All(values, value => Assert.Equal(DateTimeKind.Utc, value.Kind));
        Assert.Equal(local.ToUniversalTime(), values[0]);
    }

    /// <summary>Missing-date sentinels must remain representable after a client timezone conversion.</summary>
    [Fact]
    public void DateTimeMinimumIsReplacedWithClientSafeUtcSentinel()
    {
        var command = new NpgsqlCommand("SELECT @date");
        command.Parameters.AddWithValue("date", DateTime.MinValue);

        new DateTimeKindNormalizingInterceptor().ReaderExecuting(command, null!, default);

        Assert.Equal(DateTime.UnixEpoch, Assert.IsType<DateTime>(command.Parameters["date"].Value));
    }
}
