using System.Data;
using Jellyfin.Database.Providers.Postgres.Services;
using Xunit;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>
/// Buffering a result set must not cost the <see cref="DateTime.Kind"/> of a timestamp.
/// </summary>
/// <remarks>
/// A <see cref="DataColumn"/> of type <see cref="DateTime"/> defaults to
/// <see cref="DataSetDateTime.UnspecifiedLocal"/>, which drops <see cref="DateTimeKind.Utc"/> while the row
/// is copied. Jellyfin then serialises the value without a timezone offset whenever the milliseconds are
/// not zero, and the Android client cannot parse that: it substitutes <c>LocalDateTime.MIN</c> for the date
/// and crashes on the next navigation. Every Jellyfin date column is <c>timestamp with time zone</c>, so the
/// buffered reader must always hand the values back as UTC.
/// </remarks>
public sealed class HomeQueryCacheDateTimeKindTests
{
    private static readonly DateTime Instant = new DateTime(2026, 4, 18, 18, 49, 38, DateTimeKind.Utc).AddTicks(1138350);

    [Fact]
    public void TypedTableKeepsDateTimesAsUtc()
    {
        var table = HomeQueryCacheInterceptor.CreateTypedTable(CreateSource().CreateDataReader());

        var column = Assert.Single(table.Columns.Cast<DataColumn>(), c => c.DataType == typeof(DateTime));
        Assert.Equal(DataSetDateTime.Utc, column.DateTimeMode);
    }

    [Fact]
    public void BufferedResultKeepsUtcKindAndInstant()
    {
        var table = HomeQueryCacheInterceptor.MaterializeTyped(CreateSource().CreateDataReader());

        using var reader = HomeQueryCacheInterceptor.CreateBufferedReader(table);
        Assert.True(reader.Read());

        var viaGetDateTime = reader.GetDateTime(0);
        Assert.Equal(DateTimeKind.Utc, viaGetDateTime.Kind);
        Assert.Equal(Instant, viaGetDateTime);

        var viaGetValue = Assert.IsType<DateTime>(reader.GetValue(0));
        Assert.Equal(DateTimeKind.Utc, viaGetValue.Kind);
        Assert.Equal(Instant, viaGetValue);

        var viaGetFieldValue = reader.GetFieldValue<DateTime>(0);
        Assert.Equal(DateTimeKind.Utc, viaGetFieldValue.Kind);
        Assert.Equal(Instant, viaGetFieldValue);
    }

    [Fact]
    public void BufferedResultReturnsUtcKindEvenForTablesBuiltWithoutUtcMode()
    {
        // Simulates a buffered table created outside CreateTypedTable: the default column mode already
        // stripped the kind before the reader sees the row.
        var table = new DataTable();
        table.Columns.Add("DateCreated", typeof(DateTime));
        table.Rows.Add(new object[] { Instant });

        var stored = Assert.IsType<DateTime>(table.Rows[0][0]);
        Assert.Equal(DateTimeKind.Unspecified, stored.Kind);

        using var reader = HomeQueryCacheInterceptor.CreateBufferedReader(table);
        Assert.True(reader.Read());

        Assert.Equal(DateTimeKind.Utc, reader.GetFieldValue<DateTime>(0).Kind);
        Assert.Equal(Instant, reader.GetFieldValue<DateTime>(0));
    }

    [Fact]
    public void NullableDateTimesAreBufferedAsUtc()
    {
        var table = HomeQueryCacheInterceptor.MaterializeTyped(CreateSource().CreateDataReader());

        using var reader = HomeQueryCacheInterceptor.CreateBufferedReader(table);
        Assert.True(reader.Read());

        var values = new object[reader.FieldCount];
        Assert.Equal(reader.FieldCount, reader.GetValues(values));

        Assert.Equal(DateTimeKind.Utc, Assert.IsType<DateTime>(values[0]).Kind);
        Assert.DoesNotContain(values, value => value is DateTime { Kind: DateTimeKind.Unspecified });
    }

    private static DataTable CreateSource()
    {
        // Emulates what Npgsql returns for "timestamp with time zone": a DateTime with Kind=Utc.
        var source = new DataTable();
        var column = source.Columns.Add("DateCreated", typeof(DateTime));
        column.DateTimeMode = DataSetDateTime.Utc;
        source.Rows.Add(new object[] { Instant });
        return source;
    }
}
