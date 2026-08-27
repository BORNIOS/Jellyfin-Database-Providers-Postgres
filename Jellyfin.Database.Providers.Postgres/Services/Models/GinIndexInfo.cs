namespace Jellyfin.Database.Providers.Postgres.Services.Models;

/// <summary>Status of a plugin-managed GIN trigram index.</summary>
public sealed record GinIndexInfo(
    string IndexName,
    string TableName,
    string IndexSize,
    bool IsValid);
