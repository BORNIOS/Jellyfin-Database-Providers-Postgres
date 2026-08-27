namespace Jellyfin.Database.Providers.Postgres.Controllers.Models;

/// <summary>Request body for restoring a backup.</summary>
public sealed class RestoreBackupRequest
{
    /// <summary>Gets or sets absolute path to .sql or .zip backup file.</summary>
    public string BackupPath { get; set; } = string.Empty;

    /// <summary>Gets or sets optional psql path override.</summary>
    public string? PgRestorePath { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether existing objects should be replaced before restore.
    /// Defaults to true for compatibility with plain SQL backups.
    /// </summary>
    public bool? ReplaceExistingObjects { get; set; }
}
