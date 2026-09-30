namespace Jellyfin.Database.Providers.Postgres.Services.Models;

/// <summary>Administrator-controlled schedule policy for one registered private plugin schema.</summary>
public sealed record PluginSchemaMaintenancePolicy(
    bool IncludeAnalyze,
    bool IncludeVacuum,
    bool IncludeReindex);
