namespace Jellyfin.Database.Providers.Postgres.Services.Models;

/// <summary>Single health check finding.</summary>
/// <param name="Check">Short identifier for the check (e.g. "InvalidIndexes").</param>
/// <param name="Severity">Severity of this finding.</param>
/// <param name="Message">Human-readable description.</param>
/// <param name="Detail">Optional structured detail (table name, index name, etc.).</param>
/// <param name="Repairable">Whether this finding can be auto-repaired by the plugin.</param>
public sealed record HealthFinding(
    string Check,
    HealthSeverity Severity,
    string Message,
    string? Detail = null,
    bool Repairable = false);
