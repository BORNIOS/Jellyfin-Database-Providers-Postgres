namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>Describes a target column with its PostgreSQL type and max length.</summary>
internal sealed record TargetColumn(string Name, string PgType, int? MaxLength = null);
