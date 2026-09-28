namespace Jellyfin.Database.Providers.Postgres.Api;

/// <summary>
/// Public, dependency-free marker for discovering the private-schema host from another Jellyfin plugin.
/// </summary>
/// <remarks>
/// A consumer obtains <see cref="IPluginSchemaHost"/> by asking Jellyfin's service provider for that
/// interface type from this loaded assembly. Consumers that cannot reference this assembly at compile time
/// may discover this marker and the interface by their full names through reflection.
/// </remarks>
public static class PostgresSchemaHostCapability
{
    /// <summary>Stable protocol identifier used by reflection-based consumers.</summary>
    public const string ProtocolName = "jellyfin.postgres.private-schema-host";

    /// <summary>Current backwards-compatible protocol version.</summary>
    public const int ProtocolVersion = 1;

    /// <summary>Full name of the service contract to request from Jellyfin DI.</summary>
    public const string ContractTypeName = "Jellyfin.Database.Providers.Postgres.Api.IPluginSchemaHost";
}
