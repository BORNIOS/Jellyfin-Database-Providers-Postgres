namespace Jellyfin.Database.Providers.Postgres.Services.Models;

/// <summary>What <see cref="SchemaDriftGuard"/> does when Jellyfin's model has changes the provider has no migration for.</summary>
public enum SchemaDriftMode
{
    /// <summary>Applies the drift automatically when every operation is additive and safe; reports it otherwise.</summary>
    Heal,

    /// <summary>Only detects and reports the drift.</summary>
    ReportOnly,

    /// <summary>Skips the check.</summary>
    Off,
}
