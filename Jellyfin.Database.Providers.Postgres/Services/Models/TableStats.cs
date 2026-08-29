namespace Jellyfin.Database.Providers.Postgres.Services.Models;

/// <summary>Table size statistics returned by the maintenance API.</summary>
public sealed record TableStats(
    string TableName,
    long RowCount,
    string TotalSize,
    string TableSize,
    string IndexSize);
