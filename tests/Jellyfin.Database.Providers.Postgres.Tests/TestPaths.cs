using System.Reflection;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>
/// Minimal <see cref="IApplicationPaths"/> implementation: only the paths the plugin writes to are
/// supported, everything else throws so a test never silently depends on an unimplemented path.
/// </summary>
public class TestPaths : DispatchProxy
{
    /// <summary>Gets or sets the directory used for every path.</summary>
    public string Root { get; set; } = string.Empty;

    /// <summary>Creates a paths instance rooted at <paramref name="root"/>.</summary>
    /// <param name="root">Root directory.</param>
    /// <returns>The paths instance.</returns>
    public static IApplicationPaths Create(string root)
    {
        var paths = Create<IApplicationPaths, TestPaths>();
        ((TestPaths)(object)paths).Root = root;
        return paths;
    }

    /// <inheritdoc/>
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => targetMethod?.Name is "get_DataPath" or "get_ConfigurationDirectoryPath"
            ? Root
            : throw new NotSupportedException(targetMethod?.Name);
}
