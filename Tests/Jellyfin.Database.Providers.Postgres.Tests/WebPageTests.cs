using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>
/// The dashboard page is a single embedded HTML file, so nothing at compile time checks that the ids the
/// script reaches for exist, that every tab has a pane, that both dictionaries carry the same keys, or that
/// the embedded resource is the one the plugin advertises. These tests cover exactly that.
/// </summary>
public sealed class WebPageTests
{
    private static readonly string Html = File.ReadAllText(
        Path.Combine(TestEnvironment.PluginSourceDirectory, "Web", "configurationPage.html"));

    [Fact]
    public void EveryElementIdUsedByTheScriptExists()
    {
        var defined = Matches("id=\"([A-Za-z][A-Za-z0-9_-]*)\"");
        var used = Matches("getElementById\\('([A-Za-z][A-Za-z0-9_-]*)'\\)");

        var missing = used.Except(defined).OrderBy(id => id, StringComparer.Ordinal).ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void EveryTabHasItsPane()
    {
        var tabs = Matches("data-tab=\"([a-z]+)\"").OrderBy(tab => tab, StringComparer.Ordinal).ToArray();
        var panes = Matches("id=\"pg-tab-([a-z]+)\"").OrderBy(pane => pane, StringComparer.Ordinal).ToArray();

        Assert.NotEmpty(tabs);
        Assert.Equal(tabs, panes);
    }

    [Fact]
    public void BothDictionariesContainEveryTranslatedKey()
    {
        var englishStart = Html.IndexOf("en: {", StringComparison.Ordinal);
        var spanishStart = Html.IndexOf("es: {", StringComparison.Ordinal);
        var dictionaryEnd = Html.IndexOf("function detectLang", StringComparison.Ordinal);

        Assert.True(englishStart > 0 && spanishStart > englishStart && dictionaryEnd > spanishStart,
            "No se pudieron delimitar los diccionarios i18n del panel.");

        // The blocks start after their own header, so "en:"/"es:" are not taken for keys.
        var english = Keys(Html[(englishStart + "en: {".Length)..spanishStart]);
        var spanish = Keys(Html[(spanishStart + "es: {".Length)..dictionaryEnd]);

        // A key missing from one of the two dictionaries shows up as an untranslated panel.
        Assert.Equal(
            english.OrderBy(key => key, StringComparer.Ordinal),
            spanish.OrderBy(key => key, StringComparer.Ordinal));

        // The lookbehind keeps calls like createElement('th') from being mistaken for t('th').
        var used = Matches("(?<![A-Za-z0-9_])t\\('([a-z0-9_]+)'").ToHashSet(StringComparer.Ordinal);
        var untranslated = used.Except(english).OrderBy(key => key, StringComparer.Ordinal).ToArray();

        Assert.Empty(untranslated);
    }

    /// <summary>
    /// Jellyfin serves the panel from the embedded resource named by <c>GetPages</c>
    /// (<c>&lt;namespace&gt;.Web.configurationPage.html</c>); if the file is renamed without renaming the
    /// resource, the dashboard shows an empty page with no compile error.
    /// </summary>
    [Fact]
    public void ConfigurationPageResourceIsEmbedded()
    {
        var expected = string.Concat(typeof(PostgresPlugin).Namespace, ".Web.configurationPage.html");

        Assert.Contains(expected, typeof(PostgresPlugin).Assembly.GetManifestResourceNames());
    }

    private static HashSet<string> Matches(string pattern)
        => Regex.Matches(Html, pattern, RegexOptions.CultureInvariant)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> Keys(string dictionaryBlock)
        => Regex.Matches(dictionaryBlock, @"^\s*([a-z0-9_]+):", RegexOptions.Multiline | RegexOptions.CultureInvariant)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
}
