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

    /// <summary>Extreme date sentinels must remain representable after a client timezone conversion.</summary>
    [Fact]
    public void DateTimeExtremesAreReplacedWithClientSafeUtcSentinel()
    {
        var command = new NpgsqlCommand("SELECT @minimum, @maximum");
        command.Parameters.AddWithValue("minimum", DateTime.MinValue);
        command.Parameters.AddWithValue("maximum", DateTime.MaxValue);

        new DateTimeKindNormalizingInterceptor().ReaderExecuting(command, null!, default);

        Assert.Equal(DateTime.UnixEpoch, Assert.IsType<DateTime>(command.Parameters["minimum"].Value));
        Assert.Equal(DateTime.UnixEpoch, Assert.IsType<DateTime>(command.Parameters["maximum"].Value));
    }

    /// <summary>Extreme offset sentinels must also be converted before reaching Npgsql.</summary>
    [Fact]
    public void DateTimeOffsetExtremesAreReplacedWithClientSafeUtcSentinel()
    {
        var command = new NpgsqlCommand("SELECT @minimum, @maximum");
        command.Parameters.AddWithValue("minimum", DateTimeOffset.MinValue);
        command.Parameters.AddWithValue("maximum", DateTimeOffset.MaxValue);

        new DateTimeKindNormalizingInterceptor().ReaderExecuting(command, null!, default);

        Assert.Equal(DateTimeOffset.UnixEpoch, Assert.IsType<DateTimeOffset>(command.Parameters["minimum"].Value));
        Assert.Equal(DateTimeOffset.UnixEpoch, Assert.IsType<DateTimeOffset>(command.Parameters["maximum"].Value));
    }
}
