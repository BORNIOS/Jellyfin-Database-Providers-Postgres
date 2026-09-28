namespace Jellyfin.Database.Providers.Postgres.Services.Models;

/// <summary>One table belonging to a registered private plugin schema.</summary>
public sealed record PluginSchemaTableInfo(string TableName, long EstimatedRows, string TotalSize);
