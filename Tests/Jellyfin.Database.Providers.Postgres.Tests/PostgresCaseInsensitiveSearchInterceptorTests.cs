using Jellyfin.Database.Providers.Postgres.Services;
using Npgsql;
using Xunit;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>Verifies PostgreSQL adapts Jellyfin's case-sensitive search operators.</summary>
public sealed class PostgresCaseInsensitiveSearchInterceptorTests
{
    [Fact]
    public void JellyfinTitleSearchUsesCaseInsensitiveLikeForAllTitleFields()
    {
        var command = new NpgsqlCommand(
            """
            SELECT "Id" FROM "BaseItems" AS b
            WHERE b."CleanName" LIKE @clean
               OR b."OriginalTitle" LIKE @original
               OR b."SortName" LIKE @sort
               OR b."Name" ILIKE @display
            """);

        new PostgresCaseInsensitiveSearchInterceptor().ReaderExecuting(command, null!, default);

        Assert.Contains("b.\"CleanName\" ILIKE @clean", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("b.\"OriginalTitle\" ILIKE @original", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("b.\"SortName\" ILIKE @sort", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("b.\"Name\" ILIKE @display", command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("IILIKE", command.CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void DoesNotRewriteLikeOutsideJellyfinTitleSearch()
    {
        const string Sql = """SELECT "Id" FROM "BaseItems" WHERE "Path" LIKE @path""";
        var command = new NpgsqlCommand(Sql);

        new PostgresCaseInsensitiveSearchInterceptor().ReaderExecuting(command, null!, default);

        Assert.Equal(Sql, command.CommandText);
    }
}
