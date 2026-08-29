namespace Jellyfin.Database.Providers.Postgres.Services.Models;

/// <summary>Overall severity level for a health check result.</summary>
public enum HealthSeverity
{
    /// <summary>No issues detected.</summary>
    Ok,

    /// <summary>Non-critical issues that may degrade performance.</summary>
    Warn,

    /// <summary>Critical issues that require immediate attention.</summary>
    Error,
}
