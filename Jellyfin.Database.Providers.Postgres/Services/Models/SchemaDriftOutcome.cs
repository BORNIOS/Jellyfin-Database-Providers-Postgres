namespace Jellyfin.Database.Providers.Postgres.Services.Models;

/// <summary>Result of a schema drift check.</summary>
public enum SchemaDriftOutcome
{
    /// <summary>The database matches Jellyfin's model.</summary>
    InSync,

    /// <summary>The provider still has migrations to apply; the check runs again once they are applied.</summary>
    Deferred,

    /// <summary>The drift was applied in this run.</summary>
    Healed,

    /// <summary>The drift exists but every operation was already applied by an earlier run.</summary>
    AlreadyHealed,

    /// <summary>The drift was detected and left untouched because the mode is <see cref="SchemaDriftMode.ReportOnly"/>.</summary>
    Detected,

    /// <summary>The drift contains operations that cannot be applied safely; a provider update is required.</summary>
    Blocked,

    /// <summary>The check is disabled.</summary>
    Disabled,

    /// <summary>The check or the repair failed.</summary>
    Failed,
}
