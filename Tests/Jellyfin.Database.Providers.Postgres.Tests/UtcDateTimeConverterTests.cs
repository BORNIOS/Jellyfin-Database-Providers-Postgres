using Jellyfin.Database.Providers.Postgres.Services;
using Xunit;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>Ensures dates read through the EF model always carry the UTC kind.</summary>
public sealed class UtcDateTimeConverterTests
{
    /// <summary>A value that lost its kind while being read becomes UTC again, keeping the same ticks.</summary>
    [Fact]
    public void ReadingAnUnspecifiedValueRestoresUtc()
    {
        var unspecified = new DateTime(2026, 8, 24, 22, 56, 53, 183, DateTimeKind.Unspecified);
        var converter = new UtcDateTimeConverter();

        var result = (DateTime)converter.ConvertFromProvider(unspecified)!;

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(unspecified.Ticks, result.Ticks);
    }

    /// <summary>Local values are converted to the same instant in UTC before being written.</summary>
    [Fact]
    public void WritingALocalValueKeepsTheInstant()
    {
        var local = DateTime.Now;
        var converter = new UtcDateTimeConverter();

        var result = (DateTime)converter.ConvertToProvider(local)!;

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(local.ToUniversalTime(), result);
    }

    /// <summary>Unspecified values written by metadata providers follow Jellyfin's UTC convention.</summary>
    [Fact]
    public void WritingAnUnspecifiedValueTreatsItAsUtc()
    {
        var unspecified = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified);

        var result = (DateTime)new UtcDateTimeConverter().ConvertToProvider(unspecified)!;

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(unspecified.Ticks, result.Ticks);
    }
}
