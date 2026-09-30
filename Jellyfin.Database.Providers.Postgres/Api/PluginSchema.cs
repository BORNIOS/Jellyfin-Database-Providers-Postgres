using System;

namespace Jellyfin.Database.Providers.Postgres.Api;

/// <summary>
/// Opaque reference to the private PostgreSQL schema assigned to a plugin.
/// </summary>
public sealed class PluginSchema
{
    internal PluginSchema(Guid pluginId, string name)
    {
        PluginId = pluginId;
        Name = name;
    }

    /// <summary>Gets the immutable identifier of the schema owner.</summary>
    public Guid PluginId { get; }

    /// <summary>Gets the generated schema name for diagnostics only.</summary>
    /// <remarks>Consumers must not use this value to build SQL.</remarks>
    public string Name { get; }
}
