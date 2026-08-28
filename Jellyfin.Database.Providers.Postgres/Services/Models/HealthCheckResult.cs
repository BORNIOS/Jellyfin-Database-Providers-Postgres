using System;
using System.Collections.Generic;

namespace Jellyfin.Database.Providers.Postgres.Services.Models;

/// <summary>Aggregated result of all health checks.</summary>
/// <param name="OverallSeverity">Worst severity across all findings.</param>
/// <param name="Findings">All individual findings.</param>
/// <param name="CheckedAtUtc">Timestamp when the check ran.</param>
/// <param name="DurationMs">How long the full check took in milliseconds.</param>
public sealed record HealthCheckResult(
    HealthSeverity OverallSeverity,
    IReadOnlyList<HealthFinding> Findings,
    DateTime CheckedAtUtc,
    long DurationMs);
