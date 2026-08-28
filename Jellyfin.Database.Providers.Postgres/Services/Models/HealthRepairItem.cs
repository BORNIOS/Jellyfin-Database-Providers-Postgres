namespace Jellyfin.Database.Providers.Postgres.Services.Models;

/// <summary>Result of a single auto-repair action.</summary>
/// <param name="Action">Short identifier for the repair action (e.g. "Reindex", "Vacuum", "FixSequence").</param>
/// <param name="Target">Identifier of the object that was repaired (index, table, sequence).</param>
/// <param name="Success">Whether the action completed successfully.</param>
/// <param name="Message">Human-readable result message (or error description).</param>
public sealed record HealthRepairItem(
    string Action,
    string Target,
    bool Success,
    string? Message = null);
