namespace Jellyfin.Database.Providers.Postgres.Controllers.Models;

/// <summary>Request body for the PostgreSQL → SQLite export.</summary>
public sealed class StartExportToSqliteRequest
{
    /// <summary>
    /// Gets or sets the target SQLite .db file path.
    /// Leave empty to use the auto-detected default: {DataPath}/jellyfin.db.
    /// </summary>
    public string? TargetSqlitePath { get; set; }

    /// <summary>
    /// Gets or sets how to handle an existing file at the target path.
    /// <list type="bullet">
    /// <item><c>overwrite</c> — delete the existing file and recreate from the Jellyfin native schema.</item>
    /// <item><c>backup</c> — rename the existing file to <c>.bkp</c> before proceeding.</item>
    /// <item><c>use</c> (default) — use the existing file as-is (must already have the correct schema).</item>
    /// </list>
    /// </summary>
    public string? OverwriteMode { get; set; }
}
