using MediaBrowser.Model.Plugins;

namespace Jellyfin.Database.Providers.Postgres;

/// <summary>
/// Plugin configuration for the PostgreSQL database provider.
/// Persisted to plugins/Jellyfin.Database.Providers.Postgres_x.y.z/config.xml by Jellyfin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the PostgreSQL connection string.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the schema name (default "public").
    /// </summary>
    public string Schema { get; set; } = "public";

    /// <summary>
    /// Gets or sets the EF command timeout in seconds (default 60).
    /// </summary>
    public int CommandTimeout { get; set; } = 60;

    /// <summary>
    /// Gets or sets the current migration state.
    /// This is persisted across restarts so the UI knows where it left off.
    /// </summary>
    public MigrationState MigrationState { get; set; } = MigrationState.NotStarted;

    /// <summary>
    /// Gets or sets the last migration error message, if any.
    /// </summary>
    public string? LastMigrationError { get; set; }

    /// <summary>
    /// Gets or sets the number of rows migrated during the last migration run.
    /// </summary>
    public long MigratedRows { get; set; }

    /// <summary>
    /// Gets or sets the timestamp of the last completed migration.
    /// </summary>
    public DateTime? MigrationCompletedAt { get; set; }

    /// <summary>
    /// Gets or sets the directory where PostgreSQL backups are stored.
    /// </summary>
    public string BackupDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether backups should be compressed as zip.
    /// </summary>
    public bool BackupCompression { get; set; } = true;

    /// <summary>
    /// Gets or sets an optional full path to pg_dump executable.
    /// Leave empty to use pg_dump from PATH.
    /// </summary>
    public string PgDumpPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional full path to psql executable for restore operations.
    /// Leave empty to use psql from PATH.
    /// </summary>
    public string PgRestorePath { get; set; } = string.Empty;
}

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
    Failed
}
