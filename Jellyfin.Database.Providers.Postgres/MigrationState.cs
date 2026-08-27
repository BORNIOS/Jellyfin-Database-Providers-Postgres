namespace Jellyfin.Database.Providers.Postgres;

/// <summary>
/// Represents the state of the SQLite → PostgreSQL migration.
/// </summary>
public enum MigrationState
{
    /// <summary>Migration has not been started yet.</summary>
    NotStarted,

    /// <summary>Migration is currently in progress.</summary>
    InProgress,

    /// <summary>Migration completed successfully.</summary>
    Completed,

    /// <summary>Migration failed. See LastMigrationError for details.</summary>
    Failed,
}
