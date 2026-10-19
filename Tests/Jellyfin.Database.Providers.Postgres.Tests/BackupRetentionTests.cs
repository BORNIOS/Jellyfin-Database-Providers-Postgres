using Jellyfin.Database.Providers.Postgres.Services;
using Jellyfin.Database.Providers.Postgres.Services.Models;
using Xunit;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>
/// Retention deletes files, so its rules are the part that must never be wrong: it keeps the newest backups,
/// ignores anything that is not a backup of this plugin, and does nothing when it is disabled.
/// </summary>
public sealed class BackupRetentionTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pg-retention-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void DisabledRetentionNeverSelectsAnything()
    {
        var files = Enumerable.Range(1, 10).Select(i => Backup("Jellyfin_20250101_0000" + (10 + i) + ".sql.zip", daysOld: 400 + i)).ToList();

        Assert.Empty(BackupRetentionService.Plan(files, 0, 5, Now));
    }

    [Fact]
    public void OnlyBackupsOlderThanTheRetentionAndOutsideTheNewestAreSelected()
    {
        var files = new[]
        {
            Backup("Jellyfin_20261017_020000.sql.zip", 1),
            Backup("Jellyfin_20261010_020000.sql.zip", 8),
            Backup("Jellyfin_20260901_020000.sql.zip", 47),
            Backup("Jellyfin_20260801_020000.sql.zip", 78),
            Backup("Jellyfin_20260701_020000.sql", 109),
        };

        var doomed = BackupRetentionService.Plan(files, 30, 2, Now).Select(b => b.Name).ToList();

        Assert.Equal(new[] { "Jellyfin_20260901_020000.sql.zip", "Jellyfin_20260801_020000.sql.zip", "Jellyfin_20260701_020000.sql" }, doomed.OrderByDescending(n => n));
    }

    [Fact]
    public void TheNewestBackupsSurviveEvenWhenAllAreOlderThanTheRetention()
    {
        var files = Enumerable.Range(1, 6).Select(i => Backup($"Jellyfin_2025010{i}_020000.sql.zip", 300 + i)).ToList();

        var doomed = BackupRetentionService.Plan(files, 30, 3, Now);

        Assert.Equal(3, doomed.Count);
        Assert.DoesNotContain(doomed, b => b.Name.Contains("20250101", StringComparison.Ordinal));
    }

    [Fact]
    public void FilesThatAreNotThePluginsOwnBackupsAreIgnored()
    {
        var files = new[]
        {
            Backup("vacaciones.zip", 900),
            Backup("notas.sql", 900),
            Backup("Jellyfin_20250101_020000.sql.zip", 900),
        };

        var doomed = BackupRetentionService.Plan(files, 30, 0, Now);

        Assert.Equal("Jellyfin_20250101_020000.sql.zip", Assert.Single(doomed).Name);
    }

    [Fact]
    public void ApplyDeletesOnlyWhatThePlanSelectsAndDryRunDeletesNothing()
    {
        Directory.CreateDirectory(_root);
        var old = Touch("Jellyfin_20250101_020000.sql.zip", 400);
        var recent = Touch("Jellyfin_20261017_020000.sql.zip", 1);
        var foreign = Touch("fotos.zip", 900);

        var preview = BackupRetentionService.Apply(_root, 30, 1, dryRun: true);
        Assert.Equal(1, preview.Count);
        Assert.True(File.Exists(old));

        var applied = BackupRetentionService.Apply(_root, 30, 1, dryRun: false);
        Assert.Equal(1, applied.Count);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(foreign));
    }

    private static BackupFileInfo Backup(string name, int daysOld)
        => new(name, "/x/" + name, 1000, "1 KB", Now.AddDays(-daysOld));

    private string Touch(string name, int daysOld)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-daysOld));
        return path;
    }
}
