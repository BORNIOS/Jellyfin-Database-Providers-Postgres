using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// Publishes a non-collectible copy of the plugin assembly in the default load context so that
/// EF Core can resolve the migrations assembly by simple name.
/// </summary>
/// <remarks>
/// <para>
/// Jellyfin 12 loads regular plugins into a <c>PluginLoadContext</c>, which is <b>collectible</b>
/// (<c>AssemblyLoadContext(isCollectible: true)</c>). EF Core's migrations-assembly service resolves
/// the configured migrations assembly with <c>Assembly.Load(AssemblyName)</c> against the default
/// context, and the runtime refuses to resolve it from the collectible context:
/// <c>FileNotFoundException</c> first, and
/// <c>NotSupportedException: A non-collectible assembly may not reference a collectible assembly</c>
/// if a resolving handler hands back the collectible instance.
/// </para>
/// <para>
/// Jellyfin solves this for provider plugins by itself: with <c>DatabaseType=PLUGIN_PROVIDER</c>,
/// <c>LoadDatabasePlugin</c> runs <c>Assembly.LoadFrom</c> on the plugin assembly, which loads it
/// into the default context. This helper replicates that behaviour for the code paths that run
/// <b>before</b> PostgreSQL is the active provider (the SQLite to PostgreSQL migration), where no
/// copy exists in the default context yet.
/// </para>
/// </remarks>
internal static class MigrationAssemblyResolver
{
    private const string SchemaPreparerTypeName = "Jellyfin.Database.Providers.Postgres.Services.MigrationSchemaPreparer";
    private const string ApplySchemaMethodName = "ApplySchemaIfNeededAsync";

    private static int _registered;

    /// <summary>
    /// Registers the dependency resolver and loads a non-collectible copy of the plugin assembly
    /// once, then returns the plugin assembly simple name.
    /// </summary>
    /// <returns>Simple name of the plugin assembly, suitable for the EF Core migrations assembly option.</returns>
    internal static string EnsureRegistered()
    {
        var assembly = typeof(MigrationAssemblyResolver).Assembly;
        var simpleName = assembly.GetName().Name!;
        var location = assembly.Location;

        if (string.IsNullOrEmpty(location) || !File.Exists(location))
        {
            // No file on disk to preload (single-file deployment).
            return simpleName;
        }

        if (Interlocked.Exchange(ref _registered, 1) == 0)
        {
            var pluginDirectory = Path.GetDirectoryName(location)!;

            // The plugin bundles its own dependencies (Npgsql, Npgsql EF provider) which are not in
            // the server directory, so the default context must be told where to find them before
            // the plugin copy is loaded there.
            AssemblyLoadContext.Default.Resolving += (_, requested) =>
            {
                if (string.IsNullOrEmpty(requested.Name))
                {
                    return null;
                }

                var candidate = Path.Combine(pluginDirectory, requested.Name + ".dll");
                return File.Exists(candidate)
                    ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate)
                    : null;
            };

            if (!IsLoadedInDefaultContext(simpleName))
            {
                AssemblyLoadContext.Default.LoadFromAssemblyPath(location);
            }
        }

        return simpleName;
    }

    /// <summary>
    /// Creates or upgrades the PostgreSQL schema, running EF Core inside the default load context.
    /// </summary>
    /// <param name="pgConnStr">PostgreSQL connection string.</param>
    /// <param name="schema">Target schema name.</param>
    /// <param name="log">Callback for progress messages.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    internal static async Task ApplySchemaAsync(string pgConnStr, string schema, Action<string> log, CancellationToken ct)
    {
        var assemblyName = EnsureRegistered();
        var defaultAssembly = AssemblyLoadContext.Default.Assemblies
            .FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));

        if (defaultAssembly is null || ReferenceEquals(defaultAssembly, typeof(MigrationAssemblyResolver).Assembly))
        {
            // Already running inside the default context (Jellyfin 10.11, or the provider path in 12).
            await MigrationSchemaPreparer.ApplySchemaIfNeededAsync(pgConnStr, schema, log, ct).ConfigureAwait(false);
            return;
        }

        // EF Core has to run inside the default context. The plugin context holds its own copy of
        // Npgsql.EntityFrameworkCore.PostgreSQL, and handing EF types coming from both copies makes
        // it throw InvalidCastException while comparing model annotations.
        var preparer = defaultAssembly.GetType(SchemaPreparerTypeName, throwOnError: true)!;
        var method = preparer.GetMethod(ApplySchemaMethodName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(preparer.FullName, ApplySchemaMethodName);

        try
        {
            if (method.Invoke(null, new object?[] { pgConnStr, schema, log, ct }) is Task task)
            {
                await task.ConfigureAwait(false);
            }
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Surface the real failure instead of the reflection wrapper.
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
    }

    private static bool IsLoadedInDefaultContext(string simpleName)
        => AssemblyLoadContext.Default.Assemblies
            .Any(a => string.Equals(a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase));
}
