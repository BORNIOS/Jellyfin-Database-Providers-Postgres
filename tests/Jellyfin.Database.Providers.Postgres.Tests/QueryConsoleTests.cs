using Jellyfin.Database.Providers.Postgres.Services;
using Xunit;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>
/// The SQL console is exposed to administrators, so its restrictions are part of the product and are tested
/// as such: only read-only, single statements may reach PostgreSQL.
/// </summary>
public sealed class QueryConsoleTests
{
    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("select \"Id\" from \"BaseItems\" limit 5;")]
    [InlineData("SELECT COUNT(*) FROM \"UserData\" -- comentario final")]
    [InlineData("/* plan primero */ EXPLAIN SELECT 1")]
    [InlineData("WITH valores AS (SELECT 1 AS n) SELECT n FROM valores")]
    [InlineData("SHOW search_path")]
    [InlineData("TABLE \"BaseItems\"")]
    [InlineData("VALUES (1), (2)")]
    [InlineData("SELECT 'UPDATE' AS palabra, \"SET\" FROM (VALUES (1)) AS t(\"SET\")")]
    public void ReadOnlyStatementsAreAccepted(string sql)
    {
        var validated = QueryConsoleService.Validate(sql);

        Assert.False(string.IsNullOrWhiteSpace(validated));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("UPDATE \"BaseItems\" SET \"Name\" = 'x'")]
    [InlineData("DELETE FROM \"UserData\"")]
    [InlineData("INSERT INTO \"UserData\" VALUES (1)")]
    [InlineData("DROP TABLE \"BaseItems\"")]
    [InlineData("TRUNCATE \"UserData\"")]
    [InlineData("ALTER TABLE \"BaseItems\" ADD COLUMN x int")]
    [InlineData("CREATE INDEX ix ON \"BaseItems\" (\"Name\")")]
    [InlineData("SET search_path = public")]
    [InlineData("VACUUM")]
    [InlineData("SELECT 1; DROP TABLE \"BaseItems\"")]
    [InlineData("SELECT * INTO copia FROM \"BaseItems\"")]
    [InlineData("-- despista\nUPDATE \"BaseItems\" SET \"Name\" = 'x'")]
    [InlineData("/* despista */ DELETE FROM \"UserData\"")]
    public void MutatingOrMultiStatementSqlIsRejected(string sql)
    {
        var exception = Assert.Throws<ArgumentException>(() => QueryConsoleService.Validate(sql));

        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
    }

    /// <summary>
    /// Comments and literals are ignored while validating, so neither can be used to hide a keyword.
    /// </summary>
    [Fact]
    public void KeywordsHiddenInLiteralsAreNotRejected()
    {
        const string sql = "SELECT 'DELETE FROM \"UserData\"' AS ejemplo";

        Assert.Equal(sql, QueryConsoleService.Validate(sql));
    }

    /// <summary>
    /// Validating must not change what is executed: quoted identifiers of the Jellyfin schema are preserved
    /// (a previous version returned the masked statement, which broke every quoted table name).
    /// </summary>
    [Fact]
    public void TheStatementIsReturnedUnchangedExceptForComments()
    {
        const string sql = """SELECT COUNT(*) AS total FROM "BaseItems" WHERE "Type" = 'Movie';""";

        Assert.Equal(sql, QueryConsoleService.Validate(sql));

        // Comments are removed; everything else, including quoted identifiers, is preserved.
        Assert.Equal(
            "SELECT COUNT(*) FROM \"BaseItems\"",
            QueryConsoleService.Validate("SELECT COUNT(*) FROM \"BaseItems\" -- nota"));
    }
}
