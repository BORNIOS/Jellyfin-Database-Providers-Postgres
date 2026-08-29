using System.Collections.Generic;

namespace Jellyfin.Database.Providers.Postgres.Services.Models;

/// <summary>Snapshot of an in-progress PostgreSQL → SQLite export.</summary>
/// <param name="IsRunning">Whether the export is currently running.</param>
/// <param name="IsCompleted">Whether the export has completed successfully.</param>
/// <param name="HasError">Whether the export encountered a fatal error.</param>
/// <param name="ErrorMessage">The error message, if any.</param>
/// <param name="PercentComplete">Estimated completion percentage (0–100).</param>
/// <param name="CurrentTable">The table currently being exported.</param>
/// <param name="ExportedRows">Total rows written to SQLite so far.</param>
/// <param name="TotalRows">Estimated total rows to export.</param>
/// <param name="TargetSqlitePath">Destination SQLite file path.</param>
/// <param name="LogLines">Recent log lines from the export process.</param>
public sealed record ExportToSqliteProgress(
    bool IsRunning,
    bool IsCompleted,
    bool HasError,
    string? ErrorMessage,
    int PercentComplete,
    string CurrentTable,
    long ExportedRows,
    long TotalRows,
    string? TargetSqlitePath,
    IReadOnlyList<string> LogLines);
