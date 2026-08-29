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
    /// Gets or sets the directory that contains the PostgreSQL client binaries
    /// (pg_dump, psql, pg_restore, etc.).
    /// Example on Windows: C:\Program Files\PostgreSQL\17\bin
    /// Example on Linux:   /usr/lib/postgresql/17/bin
    /// Leave empty to auto-detect (searches well-known paths) or rely on PATH.
    /// </summary>
    public string PgBinPath { get; set; } = string.Empty;

    // ── Connection Pool Tuning ──────────────────────────────────────────────

    /// <summary>
    /// Gets or sets the minimum number of connections kept alive in the pool.
    /// Keeping a few connections warm avoids cold-start latency on bursts.
    /// </summary>
    public int MinPoolSize { get; set; } = 4;

    /// <summary>
    /// Gets or sets the maximum number of connections in the pool.
    /// </summary>
    public int MaxPoolSize { get; set; } = 100;

    /// <summary>
    /// Gets or sets the maximum number of statements Npgsql will auto-prepare
    /// server-side (caches query plans). 0 disables the feature.
    /// </summary>
    public int MaxAutoPrepare { get; set; } = 50;

    // ── Search &amp; Index Optimizations ─────────────────────────────────────

    /// <summary>
    /// Gets or sets a value indicating whether to create pg_trgm GIN indexes at startup
    /// for near-instant full-text search. Requires pg_trgm extension and CREATE EXTENSION
    /// privilege. Disable if your DB user lacks that privilege.
    /// </summary>
    public bool EnableSearchOptimizations { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to apply aggressive autovacuum settings
    /// on high-churn tables (UserData, ActivityLogs) to prevent bloat.
    /// </summary>
    public bool EnableAutovacuumTuning { get; set; } = true;
}
