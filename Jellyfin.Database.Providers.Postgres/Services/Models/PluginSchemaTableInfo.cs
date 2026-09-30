namespace Jellyfin.Database.Providers.Postgres.Services.Models;

/// <summary>One table belonging to a registered private plugin schema.</summary>
/// <remarks>
/// Row estimates from <c>pg_class.reltuples</c> are intentionally not exposed: they become stale between
/// ANALYZE runs and must not be presented as an actual row count in the administration interface.
/// </remarks>
public sealed record PluginSchemaTableInfo(string TableName, string TotalSize);
