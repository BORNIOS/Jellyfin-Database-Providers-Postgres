using System.Reflection;
using System.Text.RegularExpressions;
using Jellyfin.Database.Implementations;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using Xunit;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>
/// Verifies the parts of the contract with the Jellyfin server that the compiler cannot enforce: how the
/// provider is discovered, which types the server instantiates and that no member was left unimplemented.
/// </summary>
public sealed class ProviderContractTests
{
    /// <summary>
    /// Jellyfin.Server.Implementations loads a plugin provider with
    /// <c>assembly.GetExportedTypes().FirstOrDefault(f =&gt; f.IsAssignableTo(typeof(IJellyfinDatabaseProvider)))</c>,
    /// so a second exported implementation would make the selected provider depend on type order.
    /// </summary>
    [Fact]
    public void JellyfinDiscoversExactlyOneProvider()
    {
        var providers = typeof(PostgresDatabaseProvider).Assembly.GetExportedTypes()
            .Where(t => typeof(IJellyfinDatabaseProvider).IsAssignableFrom(t))
            .ToArray();

        Assert.Equal([typeof(PostgresDatabaseProvider)], providers);
    }

    /// <summary>
    /// The service registrator is created with <c>Activator.CreateInstance</c> and the plugin has to be a
    /// <c>BasePlugin</c> exposing the dashboard page, or Jellyfin loads nothing at all.
    /// </summary>
    [Fact]
    public void EntryPointsMatchWhatJellyfinInstantiates()
    {
        Assert.NotNull(typeof(PostgresServiceRegistrator).GetConstructor(Type.EmptyTypes));
        Assert.True(typeof(IPluginServiceRegistrator).IsAssignableFrom(typeof(PostgresServiceRegistrator)));
        Assert.True(typeof(BasePlugin<PluginConfiguration>).IsAssignableFrom(typeof(PostgresPlugin)));
        Assert.True(typeof(IHasWebPages).IsAssignableFrom(typeof(PostgresPlugin)));
    }

    /// <summary>
    /// A member of <see cref="IJellyfinDatabaseProvider"/> left as <c>throw new NotImplementedException()</c>
    /// only fails when the server calls it: <c>PurgeDatabase</c>, for instance, is used when restoring a
    /// full system backup.
    /// </summary>
    [Fact]
    public void NoInterfaceMemberIsLeftUnimplemented()
    {
        var stubs = typeof(PostgresDatabaseProvider)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.DeclaringType == typeof(PostgresDatabaseProvider))
            .Where(ThrowsNotImplementedException)
            .Select(m => m.Name)
            .ToArray();

        Assert.Empty(stubs);
    }

    /// <summary>
    /// The plugin compiles against the Jellyfin packages it declares; upgrading the server without bumping
    /// the declared version is what silently ships a provider built for another release.
    /// </summary>
    [Fact]
    public void DeclaredJellyfinVersionMatchesTheReferencedSdk()
    {
        var projectFile = Path.Combine(
            TestEnvironment.RepositoryRoot,
            "Jellyfin.Database.Providers.Postgres",
            "Jellyfin.Database.Providers.Postgres.csproj");
        var declared = Regex.Match(File.ReadAllText(projectFile), "<JellyfinVersion>([^<]+)</JellyfinVersion>").Groups[1].Value;
        var referenced = typeof(IJellyfinDatabaseProvider).Assembly.GetName().Version ?? new Version();

        Assert.Equal($"{referenced.Major}.{referenced.Minor}.{referenced.Build}", declared);
    }

    /// <summary>Detects a method body that constructs a <see cref="NotImplementedException"/>.</summary>
    /// <param name="method">Method to inspect.</param>
    /// <returns><see langword="true"/> when the body throws it.</returns>
    private static bool ThrowsNotImplementedException(MethodInfo method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            return false;
        }

        // 0x73 is 'newobj', followed by a 4 byte metadata token.
        for (var offset = 0; offset + 4 < il.Length; offset++)
        {
            if (il[offset] != 0x73)
            {
                continue;
            }

            try
            {
                if (method.Module.ResolveMethod(BitConverter.ToInt32(il, offset + 1)) is ConstructorInfo ctor
                    && ctor.DeclaringType == typeof(NotImplementedException))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                // Not a method token.
            }
        }

        return false;
    }
}
