namespace Jellyfin.Database.Providers.Postgres.Controllers.Models;

/// <summary>Request body for creating a backup.</summary>
public sealed class BackupRequest
{
    /// <summary>Gets or sets optional backup output directory override.</summary>
    public string? OutputDirectory { get; set; }

    /// <summary>Gets or sets optional compression override.</summary>
    public bool? Compress { get; set; }

    /// <summary>Gets or sets optional PostgreSQL bin directory override for this request.</summary>
    public string? PgBinPath { get; set; }
}
