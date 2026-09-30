using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Database.Providers.Postgres.Api;

/// <summary>
/// Provides isolated PostgreSQL storage for a plugin-owned schema.
/// </summary>
/// <remarks>
/// This contract deliberately does not expose a connection string, an Npgsql connection, or Jellyfin's
/// <c>public</c> schema. Consumers must use Jellyfin's own contracts for Jellyfin data and use this host
/// only for state they own. Loaded plugins are trusted in-process code; this API is an isolation boundary
/// against accidental cross-plugin access, not a security boundary against malicious code.
/// </remarks>
public interface IPluginSchemaHost
{
    /// <summary>
    /// Opens a named private schema and binds it to its plugin owner.
    /// </summary>
    /// <param name="request">Schema name and immutable plugin owner.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An opaque handle for the plugin's schema.</returns>
    Task<PluginSchema> OpenSchemaAsync(PluginSchemaRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the generated private schema assigned to <paramref name="pluginId"/>, creating it when needed.
    /// </summary>
    /// <param name="pluginId">The immutable identifier of the consuming plugin.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An opaque handle for the plugin's schema.</returns>
    Task<PluginSchema> OpenSchemaAsync(Guid pluginId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims the immediate pre-registry schema matching the target plugin name and moves it to the
    /// target private-schema name.
    /// </summary>
    /// <param name="request">The target schema name and immutable plugin owner.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The handle of the registered target schema.</returns>
    /// <remarks>
    /// The only legacy source accepted is the exact prefix of <paramref name="request"/>'s target
    /// name: <c>name_guid</c> may claim only <c>name</c>. The target must not exist and the source must
    /// be unregistered. This preserves the source tables by an atomic PostgreSQL schema rename.
    /// </remarks>
    Task<PluginSchema> MigratePreRegistrySchemaAsync(
        PluginSchemaRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies versioned DDL migrations in the schema represented by <paramref name="schema"/>.
    /// </summary>
    /// <param name="schema">Schema handle returned by <see cref="OpenSchemaAsync(PluginSchemaRequest, CancellationToken)"/>.</param>
    /// <param name="migrations">Migrations to apply in their supplied order.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes after all migrations have been committed.</returns>
    Task ApplyMigrationsAsync(
        PluginSchema schema,
        IReadOnlyList<PluginSchemaMigration> migrations,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes one parameterized data command inside the plugin's schema.
    /// </summary>
    /// <param name="schema">Schema handle returned by <see cref="OpenSchemaAsync(PluginSchemaRequest, CancellationToken)"/>.</param>
    /// <param name="sql">A single unqualified SQL statement.</param>
    /// <param name="parameters">Parameters for <paramref name="sql"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows affected.</returns>
    Task<int> ExecuteAsync(
        PluginSchema schema,
        string sql,
        IReadOnlyList<PluginSqlParameter>? parameters = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes one parameterized query inside the plugin's schema and projects its rows without exposing
    /// the underlying connection.
    /// </summary>
    /// <typeparam name="T">Projection result type.</typeparam>
    /// <param name="schema">Schema handle returned by <see cref="OpenSchemaAsync(PluginSchemaRequest, CancellationToken)"/>.</param>
    /// <param name="sql">A single unqualified SQL query.</param>
    /// <param name="project">Synchronous projection of the open reader.</param>
    /// <param name="parameters">Parameters for <paramref name="sql"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The projected result.</returns>
    Task<T> QueryAsync<T>(
        PluginSchema schema,
        string sql,
        Func<DbDataReader, T> project,
        IReadOnlyList<PluginSqlParameter>? parameters = null,
        CancellationToken cancellationToken = default);
}
