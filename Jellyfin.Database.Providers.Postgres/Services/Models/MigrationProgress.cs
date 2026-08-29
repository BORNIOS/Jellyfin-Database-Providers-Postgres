using System.Collections.Generic;

namespace Jellyfin.Database.Providers.Postgres.Services.Models;

/// <summary>Immutable snapshot of migration progress returned to the API layer.</summary>
public sealed record MigrationProgress(
    bool IsRunning,
    bool IsCompleted,
    bool HasError,
    string? ErrorMessage,
    int PercentComplete,
    string CurrentTable,
    long MigratedRows,
    long TotalRows,
    IReadOnlyList<string> LogLines);
