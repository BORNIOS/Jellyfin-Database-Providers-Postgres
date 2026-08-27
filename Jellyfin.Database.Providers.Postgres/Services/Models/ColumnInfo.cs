namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>Describes a PostgreSQL column (name + data type).</summary>
internal sealed record ColumnInfo(string Name, string PgDataType);
