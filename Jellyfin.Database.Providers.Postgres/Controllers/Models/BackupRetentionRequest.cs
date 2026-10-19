namespace Jellyfin.Database.Providers.Postgres.Controllers.Models;

/// <summary>Request to apply or preview the backup retention policy.</summary>
public sealed class BackupRetentionRequest
{
    /// <summary>Gets or sets the retention in days; null uses the saved value.</summary>
    public int? Days { get; set; }

    /// <summary>Gets or sets how many newest backups are always kept; null uses the saved value.</summary>
    public int? MinKeep { get; set; }

    /// <summary>Gets or sets a value indicating whether to only preview (default) instead of deleting.</summary>
    public bool DryRun { get; set; } = true;
}
