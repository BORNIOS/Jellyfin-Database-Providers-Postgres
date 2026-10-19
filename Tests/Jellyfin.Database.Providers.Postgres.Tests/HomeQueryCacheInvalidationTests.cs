using Jellyfin.Database.Providers.Postgres.Services;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>
/// EF Core runs the INSERT/UPDATE/DELETE of SaveChanges through a data reader, so the cache has to notice
/// writes there too. Without it the cache kept serving UserData, ItemValues and BaseItems rows that had just
/// been changed for as long as the entry lived.
/// </summary>
public sealed class HomeQueryCacheInvalidationTests
{
    [Theory]
    [InlineData("INSERT INTO \"ItemValues\" (\"Value\") VALUES ('a') RETURNING \"ItemValueId\"")]
    [InlineData("  update \"UserData\" SET \"Played\" = true")]
    [InlineData("DELETE FROM \"BaseItems\" WHERE \"Id\" = 1")]
    [InlineData("WITH moved AS (DELETE FROM \"ItemValuesMap\" RETURNING *) SELECT 1")]
    public void WritesAreRecognisedAsMutations(string sql)
    {
        using var command = new NpgsqlCommand(sql);

        Assert.True(HomeQueryCacheInterceptor.IsMutation(command));
    }

    [Theory]
    [InlineData("SELECT * FROM \"BaseItems\"")]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x")]
    [InlineData("")]
    public void ReadsAreNotMutations(string sql)
    {
        using var command = new NpgsqlCommand(sql);

        Assert.False(HomeQueryCacheInterceptor.IsMutation(command));
    }

    [Fact]
    public void AWriteExecutedAsAReaderRaisesTheGenerationOfTheTablesItTouches()
    {
        var interceptor = new HomeQueryCacheInterceptor();
        var before = HomeQueryCacheInterceptor.GenerationOf("\"UserData\"");
        var untouched = HomeQueryCacheInterceptor.GenerationOf("\"PeopleBaseItemMap\"");
        using var command = new NpgsqlCommand("UPDATE \"UserData\" SET \"Played\" = true WHERE \"ItemId\" = 1");

        interceptor.ReaderExecuting(command, null!, default);

        Assert.True(HomeQueryCacheInterceptor.GenerationOf("\"UserData\"") > before);
        Assert.Equal(untouched, HomeQueryCacheInterceptor.GenerationOf("\"PeopleBaseItemMap\""));
    }

    [Fact]
    public void CommittingATransactionInvalidatesEveryTable()
    {
        var before = HomeQueryCacheInterceptor.GenerationOf("\"PeopleBaseItemMap\"");

        new HomeQueryCacheCommitInterceptor().TransactionCommitted(null!, null!);

        Assert.True(HomeQueryCacheInterceptor.GenerationOf("\"PeopleBaseItemMap\"") > before);
    }
}
