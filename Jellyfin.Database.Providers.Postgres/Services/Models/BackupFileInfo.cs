using System;

namespace Jellyfin.Database.Providers.Postgres.Services.Models;

/// <summary>Metadata about a backup file found in the backup directory.</summary>
/// <param name="Name">File name without directory path.</param>
/// <param name="Path">Absolute path to the backup file.</param>
/// <param name="SizeBytes">File size in bytes.</param>
/// <param name="SizeFormatted">Human-readable file size (e.g. "45.3 MB").</param>
/// <param name="CreatedAtUtc">File last-write timestamp in UTC.</param>
public sealed record BackupFileInfo(
    string Name,
    string Path,
    long SizeBytes,
    string SizeFormatted,
    DateTime CreatedAtUtc);
