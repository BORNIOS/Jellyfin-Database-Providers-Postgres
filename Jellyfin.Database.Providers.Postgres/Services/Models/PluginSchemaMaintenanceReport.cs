using System;
using System.Collections.Generic;

namespace Jellyfin.Database.Providers.Postgres.Services.Models;

#pragma warning disable SA1402

/// <summary>Bounded health and maintenance information for one registered private plugin schema.</summary>
public sealed record PluginSchemaMaintenanceReport(
    string SchemaName,
    IReadOnlyList<PluginSchemaMaintenanceTable> Tables,
    int TablesNeedingAnalyze,
    int TablesNeedingVacuum,
    bool IncludeAnalyze,
    bool IncludeVacuum,
    bool IncludeReindex,
    DateTime CollectedAt);

/// <summary>Operational statistics for one table that belongs to a registered private plugin schema.</summary>
public sealed record PluginSchemaMaintenanceTable(
    string TableName,
    long EstimatedRows,
    string TotalSize,
    string IndexSize,
    long DeadTuples,
    long ModifiedSinceAnalyze,
    DateTime? LastAnalyze,
    DateTime? LastAutoAnalyze,
    DateTime? LastVacuum,
    DateTime? LastAutoVacuum,
    int IndexCount,
    bool NeedsAnalyze,
    bool NeedsVacuum);

/// <summary>Result of an explicitly requested private-schema maintenance operation.</summary>
public sealed record PluginSchemaMaintenanceResult(string Operation, string SchemaName, string? TableName, int TablesAffected, long DurationMs);
#pragma warning restore SA1402
