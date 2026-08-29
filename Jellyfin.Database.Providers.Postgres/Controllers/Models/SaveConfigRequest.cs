namespace Jellyfin.Database.Providers.Postgres.Controllers.Models;

/// <summary>Request body for saving plugin configuration.</summary>
public sealed class SaveConfigRequest
{
    /// <summary>Gets or sets the connection string to save.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Gets or sets the EF command timeout in seconds.</summary>
    public int CommandTimeout { get; set; } = 60;

    /// <summary>Gets or sets the backup output directory on the server.</summary>
    public string? BackupDirectory { get; set; }

    /// <summary>Gets or sets a value indicating whether backups should be zipped.</summary>
    public bool BackupCompression { get; set; } = true;

    /// <summary>
    /// Gets or sets the directory that contains pg_dump, psql, pg_restore, etc.
    /// Leave empty to auto-detect or rely on PATH.
    /// </summary>
    public string? PgBinPath { get; set; }
}
