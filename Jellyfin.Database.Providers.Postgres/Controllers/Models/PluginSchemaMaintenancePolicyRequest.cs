namespace Jellyfin.Database.Providers.Postgres.Controllers.Models;

/// <summary>Administrator-selected scheduled-maintenance policy for a private plugin schema.</summary>
public sealed class PluginSchemaMaintenancePolicyRequest
{
    /// <summary>Gets or sets a value indicating whether scheduled provider maintenance refreshes planner statistics.</summary>
    public bool IncludeAnalyze { get; set; }

    /// <summary>Gets or sets a value indicating whether scheduled provider maintenance runs VACUUM (ANALYZE).</summary>
    public bool IncludeVacuum { get; set; }

    /// <summary>Gets or sets a value indicating whether scheduled provider maintenance rebuilds indexes concurrently.</summary>
    public bool IncludeReindex { get; set; }
}
