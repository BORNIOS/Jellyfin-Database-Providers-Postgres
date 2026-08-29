using System;

namespace Jellyfin.Database.Providers.Postgres.Services.Models;

/// <summary>
/// A minimal result record for the instant-search endpoint.
/// Short property names keep the JSON payload under 1 KB for typical result sets.
/// </summary>
public sealed record InstantSearchResult(
    Guid Id,
    string? Name,
    string? Type,
    int? Year,
    string? Artists,
    string? Album,
    string? SeriesName,
    double Relevance);
