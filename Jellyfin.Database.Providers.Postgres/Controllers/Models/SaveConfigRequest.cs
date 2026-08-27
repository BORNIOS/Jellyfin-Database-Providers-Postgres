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

    /// <summary>Gets or sets optional explicit path to pg_dump executable.</summary>
    public string? PgDumpPath { get; set; }

    /// <summary>Gets or sets optional explicit path to psql executable for restore.</summary>
    public string? PgRestorePath { get; set; }
}
