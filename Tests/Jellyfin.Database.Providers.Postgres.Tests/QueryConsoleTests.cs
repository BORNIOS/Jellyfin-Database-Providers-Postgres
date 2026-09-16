using System.Text.RegularExpressions;
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
    /// The templates the panel offers have to be accepted by the console itself: a template using a blocked
    /// keyword would be rejected the moment an administrator picked it.
    /// </summary>
    [Fact]
    public void PanelTemplatesAreReadOnlyStatements()
    {
        var presets = PanelTemplates();

        Assert.True(presets.Count >= 8, $"Se esperaban al menos 8 plantillas en el panel, encontradas {presets.Count}.");
        foreach (var preset in presets.Where(p => !string.IsNullOrWhiteSpace(p.Sql)))
        {
            Assert.False(
                string.IsNullOrWhiteSpace(QueryConsoleService.Validate(preset.Sql)),
                $"La plantilla '{preset.Key}' quedó vacía al validarse.");
        }
    }

    /// <summary>
    /// pg_stat_statements accumulates statements from every database of the server, so the templates that read
    /// it must filter by the current database. Without the filter the panel shows unrelated maintenance noise
    /// (creating and dropping scratch databases, for instance).
    /// </summary>
    [Fact]
    public void PanelTemplatesDoNotShowStatisticsFromOtherDatabases()
    {
        var presets = PanelTemplates()
            .Where(p => p.Sql.Contains("pg_stat_statements", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(presets);
        foreach (var preset in presets)
        {
            Assert.Contains("dbid", preset.Sql, StringComparison.Ordinal);
        }
    }

    private static List<(string Key, string Sql)> PanelTemplates()
    {
        var html = File.ReadAllText(Path.Combine(TestEnvironment.PluginSourceDirectory, "Web", "configurationPage.html"));
        var presets = new List<(string Key, string Sql)>();

        foreach (Match match in Regex.Matches(
            html,
            @"\{\s*key:\s*'(preset_[a-z0-9_]+)',\s*sql:\s*'((?:[^'\\]|\\.)*)'\s*\}",
            RegexOptions.CultureInvariant))
        {
            presets.Add((match.Groups[1].Value, UnescapeSql(match.Groups[2].Value)));
        }

        return presets;
    }

    private static string UnescapeSql(string value)
        => value.Replace("\\'", "'", StringComparison.Ordinal).Replace("\\n", "\n", StringComparison.Ordinal);

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
