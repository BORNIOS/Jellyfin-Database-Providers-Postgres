namespace Jellyfin.Database.Providers.Postgres.Services.Models;

/// <summary>One column exposed for inspection in a registered private plugin schema.</summary>
public sealed record PluginSchemaColumnInfo(string ColumnName, string DataType, bool Nullable, int OrdinalPosition);
