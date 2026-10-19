using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Database.Providers.Postgres.Logging;
using Jellyfin.Database.Providers.Postgres.Services.Models;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// Prunes old backups. It only ever touches files that carry the plugin's own naming pattern
/// (<c>&lt;database&gt;_yyyyMMdd_HHmmss.sql[.zip]</c>), so anything else a person keeps in the folder is safe, and it
/// always keeps the newest few so a retention mistake can never leave the server without a restore point.
/// </summary>
public static class BackupRetentionService
{
    /// <summary>Default number of most recent backups that are never deleted.</summary>
    public const int DefaultMinKeep = 5;

    private static readonly Regex OwnBackupName = new(
        @"^.+_\d{8}_\d{6}\.sql(\.zip)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Decides which backups would be removed. Pure function, so the rule is testable without touching disk.
    /// </summary>
    /// <param name="backups">Backups found in the folder (any order).</param>
    /// <param name="retentionDays">Delete backups older than this many days. 0 or less disables retention.</param>
    /// <param name="minKeep">Number of newest backups that are always kept.</param>
    /// <param name="nowUtc">Reference time.</param>
    /// <returns>The backups that should be deleted.</returns>
    public static IReadOnlyList<BackupFileInfo> Plan(
        IEnumerable<BackupFileInfo> backups,
        int retentionDays,
        int minKeep,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(backups);
        if (retentionDays <= 0)
        {
            return Array.Empty<BackupFileInfo>();
        }

        var cutoff = nowUtc - TimeSpan.FromDays(retentionDays);
        return backups
            .Where(static b => OwnBackupName.IsMatch(b.Name))
            .OrderByDescending(static b => b.CreatedAtUtc)
            .Skip(Math.Max(0, minKeep))
            .Where(b => b.CreatedAtUtc < cutoff)
            .ToList();
    }

    /// <summary>
    /// Applies the retention policy to a backup folder.
    /// </summary>
    /// <param name="directory">Backup folder.</param>
    /// <param name="retentionDays">Delete backups older than this many days. 0 or less disables retention.</param>
    /// <param name="minKeep">Number of newest backups that are always kept.</param>
    /// <param name="dryRun">When true nothing is deleted; the result only reports what would be.</param>
    /// <returns>What was (or would be) removed.</returns>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Deleted paths come from listing the configured backup folder and only files matching the plugin backup naming pattern are selected.")]
    public static BackupRetentionResult Apply(string directory, int retentionDays, int minKeep, bool dryRun)
    {
        var candidates = Plan(MaintenanceService.ListBackups(directory), retentionDays, minKeep, DateTime.UtcNow);
        var removed = new List<BackupFileInfo>();
        var failed = new List<string>();

        foreach (var backup in candidates)
        {
            if (dryRun)
            {
                removed.Add(backup);
                continue;
            }

            try
            {
                File.Delete(backup.Path);
                removed.Add(backup);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add($"{backup.Name}: {ex.Message}");
            }
        }

        var bytes = removed.Sum(static b => b.SizeBytes);
        if (!dryRun && (removed.Count > 0 || failed.Count > 0))
        {
            PostgresLog.Info($"[Retención] {removed.Count} respaldo(s) eliminado(s) ({MaintenanceService.FormatBytes(bytes)}) en {directory}; fallos: {failed.Count}.");
        }

        return new BackupRetentionResult(removed.Select(static b => b.Name).ToList(), removed.Count, bytes, MaintenanceService.FormatBytes(bytes), failed, dryRun);
    }
}
