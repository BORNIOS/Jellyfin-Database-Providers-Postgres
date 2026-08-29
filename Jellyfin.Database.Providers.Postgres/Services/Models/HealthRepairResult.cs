using System;
using System.Collections.Generic;

namespace Jellyfin.Database.Providers.Postgres.Services.Models;

/// <summary>Aggregated result of an auto-repair run.</summary>
/// <param name="OverallSuccess">Whether all repair actions succeeded.</param>
/// <param name="Items">All individual repair actions.</param>
/// <param name="CompletedAtUtc">Timestamp when the repair finished.</param>
/// <param name="DurationMs">How long the full repair took in milliseconds.</param>
public sealed record HealthRepairResult(
    bool OverallSuccess,
    IReadOnlyList<HealthRepairItem> Items,
    DateTime CompletedAtUtc,
    long DurationMs);
