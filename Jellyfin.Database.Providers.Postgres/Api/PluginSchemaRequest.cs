using System;

namespace Jellyfin.Database.Providers.Postgres.Api;

/// <summary>
/// Identifies a plugin-owned PostgreSQL schema.
/// </summary>
/// <param name="PluginId">Immutable identifier of the plugin that owns the schema.</param>
/// <param name="SchemaName">Stable lowercase PostgreSQL schema name, for example <c>jellytrend</c>.</param>
/// <param name="AdoptExistingSchema">Whether an existing unclaimed schema may be bound to <paramref name="PluginId"/>.</param>
/// <remarks>
/// Adoption exists only for a deliberate migration from a predecessor contract. Once claimed, the host
/// writes an ownership marker and rejects future requests by a different plugin identifier.
/// </remarks>
public sealed record PluginSchemaRequest(Guid PluginId, string SchemaName, bool AdoptExistingSchema = false);
