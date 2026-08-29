namespace Jellyfin.Database.Providers.Postgres.Services.Models;

/// <summary>Summary of a slow query captured from <c>pg_stat_statements</c>.</summary>
/// <param name="QueryPreview">First 120 characters of the normalized query text.</param>
/// <param name="MeanMs">Mean execution time in milliseconds.</param>
/// <param name="Calls">Total number of executions.</param>
/// <param name="TotalMs">Cumulative execution time in milliseconds.</param>
/// <param name="Rows">Average rows returned per execution.</param>
public sealed record SlowQueryInfo(
    string QueryPreview,
    double MeanMs,
    long Calls,
    double TotalMs,
    double Rows);
