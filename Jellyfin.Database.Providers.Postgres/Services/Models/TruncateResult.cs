using System.Collections.Generic;

namespace Jellyfin.Database.Providers.Postgres.Services.Models;

/// <summary>
/// Result returned after truncating all Jellyfin data tables in PostgreSQL.
/// </summary>
/// <param name="TablesAffected">Number of tables that were truncated.</param>
/// <param name="TotalRowsRemoved">Total rows that existed before truncation.</param>
/// <param name="RowCountsBeforeTruncation">Per-table row counts before truncation.</param>
public sealed record TruncateResult(
    int TablesAffected,
    long TotalRowsRemoved,
    Dictionary<string, long> RowCountsBeforeTruncation);
