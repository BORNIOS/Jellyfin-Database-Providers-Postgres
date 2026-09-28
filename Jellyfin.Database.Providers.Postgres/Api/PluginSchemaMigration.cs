using System;

namespace Jellyfin.Database.Providers.Postgres.Api;

/// <summary>
/// One immutable, plugin-owned schema migration.
/// </summary>
/// <param name="Id">Stable migration identifier, unique within the plugin.</param>
/// <param name="Sql">One unqualified DDL statement to execute.</param>
public sealed record PluginSchemaMigration(string Id, string Sql)
{
    /// <summary>Gets a deterministic checksum used to detect modified applied migrations.</summary>
    public string Checksum { get; } = PluginSchemaMigrationChecksum.Compute(Id, Sql);
}
