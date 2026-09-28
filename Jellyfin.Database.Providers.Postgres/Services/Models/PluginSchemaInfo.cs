using System;

namespace Jellyfin.Database.Providers.Postgres.Services.Models;

/// <summary>One private schema registered by a Jellyfin plugin.</summary>
public sealed record PluginSchemaInfo(
    Guid PluginId,
    string PluginName,
    string SchemaName,
    int ProtocolVersion,
    DateTime CreatedAt,
    DateTime LastSeenAt,
    string State,
    int TableCount,
    string TotalSize);
