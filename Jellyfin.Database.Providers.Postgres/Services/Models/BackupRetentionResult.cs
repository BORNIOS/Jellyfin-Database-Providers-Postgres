using System.Collections.Generic;

namespace Jellyfin.Database.Providers.Postgres.Services.Models;

/// <summary>Outcome of a retention run.</summary>
/// <param name="Names">Names of the removed (or removable) backups.</param>
/// <param name="Count">How many backups.</param>
/// <param name="Bytes">Space they take.</param>
/// <param name="BytesFormatted">Human readable size.</param>
/// <param name="Failures">Files that could not be deleted.</param>
/// <param name="DryRun">Whether this was only a preview.</param>
public sealed record BackupRetentionResult(
    IReadOnlyList<string> Names,
    int Count,
    long Bytes,
    string BytesFormatted,
    IReadOnlyList<string> Failures,
    bool DryRun);
