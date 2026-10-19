using Jellyfin.Database.Providers.Postgres.Services;
using Xunit;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>
/// A stale copy of the plugin next to the running one is the reason a fixed build could keep behaving like the
/// old one. The inspector has to point at it instead of leaving the operator to find it by comparing hashes.
/// </summary>
public sealed class PluginInstallInspectorTests : IDisposable
{
    private const string Prefix = "PostgreSQL Database Provider";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pg-inspector-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void ACleanInstallationHasNoWarnings()
    {
        var assembly = CreatePlugin("3.0.4.0", "Active");

        Assert.Empty(PluginInstallInspector.Inspect(assembly, new Version(3, 0, 4, 0)));
    }

    [Fact]
    public void AnOlderCopyMarkedDeletedIsReported()
    {
        var assembly = CreatePlugin("3.0.4.0", "Active");
        CreatePlugin("3.0.3.0", "Deleted");

        var warnings = PluginInstallInspector.Inspect(assembly, new Version(3, 0, 4, 0));

        var warning = Assert.Single(warnings);
        Assert.Contains("3.0.3.0", warning, StringComparison.Ordinal);
        Assert.Contains("Deleted", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("MÁS NUEVA", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ANewerCopyThanTheRunningOneIsCalledOut()
    {
        var assembly = CreatePlugin("3.0.3.0", "Active");
        CreatePlugin("3.0.4.0", "Active");

        var warning = Assert.Single(PluginInstallInspector.Inspect(assembly, new Version(3, 0, 3, 0)));

        Assert.Contains("MÁS NUEVA", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void FoldersOfOtherPluginsAreIgnored()
    {
        var assembly = CreatePlugin("3.0.4.0", "Active");
        Directory.CreateDirectory(Path.Combine(_root, "JellyTV_1.0.0.0"));

        Assert.Empty(PluginInstallInspector.Inspect(assembly, new Version(3, 0, 4, 0)));
    }

    [Fact]
    public void AMissingAssemblyPathIsHarmless()
    {
        Assert.Empty(PluginInstallInspector.Inspect(null, new Version(1, 0)));
        Assert.Empty(PluginInstallInspector.Inspect(Path.Combine(_root, "nope", "x.dll"), new Version(1, 0)));
    }

    private string CreatePlugin(string version, string status)
    {
        var folder = Path.Combine(_root, Prefix + "_" + version);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "meta.json"), "{\"status\":\"" + status + "\"}");
        var assembly = Path.Combine(folder, "Jellyfin.Database.Providers.Postgres.dll");
        File.WriteAllText(assembly, string.Empty);
        return assembly;
    }
}
