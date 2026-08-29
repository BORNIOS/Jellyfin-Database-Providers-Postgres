// ──────────────────────────────────────────────────────────────────────────────
// BUILD-TIME CONTRACT — do NOT modify.
//
// This file is a compile-time mirror of the public API declared in the
// JellyTrend plugin (Jellyfin.Plugin.JellyTrend.Api.IRecommendationQueryProvider).
// It exists so this project can implement the interface without referencing the
// JellyTrend DLL at build time (no HintPath, no NuGet dependency).
//
// Rules:
//  • Namespace, type names and method signatures MUST stay identical to the
//    originals in JellyTrend — any drift will cause a TypeLoadException at runtime.
//  • When the JellyTrend contract changes, update this file to match.
//  • This file ships in the Postgres plugin DLL but is NEVER loaded at runtime
//    for its type identity — Jellyfin loads the real types from the JellyTrend DLL.
// ──────────────────────────────────────────────────────────────────────────────
using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyTrend.Api;

/// <summary>
/// Optional database-provider-specific query backend for the recommendation engine.
/// When registered in the Jellyfin DI container (e.g. by the PostgreSQL Database Provider
/// plugin), the recommendation engine delegates its library queries to this interface
/// instead of using <c>ILibraryManager.GetItemList</c>.
/// </summary>
/// <remarks>
/// Implementations must never throw for recoverable errors — return an empty list instead.
/// If the provider is not registered, the engine falls back to <c>ILibraryManager</c>
/// transparently so JellyTrend works on SQLite without changes.
/// </remarks>
public interface IRecommendationQueryProvider
{
    /// <summary>
    /// Returns movies the user has played, up to <paramref name="limit"/> items.
    /// </summary>
    /// <param name="userId">The user whose play history to query.</param>
    /// <param name="limit">Maximum number of items to return.</param>
    /// <returns>Lightweight projections of played movies.</returns>
    IReadOnlyList<RecommendationItem> GetPlayedMovies(Guid userId, int limit);

    /// <summary>
    /// Returns movies the user has started but not finished (resume candidates).
    /// </summary>
    /// <param name="userId">The user whose resume queue to query.</param>
    /// <param name="limit">Maximum number of items to return.</param>
    /// <returns>Lightweight projections of in-progress movies.</returns>
    IReadOnlyList<RecommendationItem> GetResumableMovies(Guid userId, int limit);

    /// <summary>
    /// Returns unwatched movie candidates that match the given genres.
    /// </summary>
    /// <param name="userId">User whose played status is checked.</param>
    /// <param name="genres">Genres to filter by (OR logic — any match qualifies).</param>
    /// <param name="topParentIds">Library root ids to scope the query.</param>
    /// <param name="limit">Maximum number of items to return.</param>
    /// <returns>Lightweight projections of matching unwatched movies.</returns>
    IReadOnlyList<RecommendationItem> GetUnwatchedMoviesByGenres(
        Guid userId,
        IReadOnlyList<string> genres,
        IReadOnlyList<Guid> topParentIds,
        int limit);

    /// <summary>
    /// Returns unwatched movie candidates associated with the given person ids.
    /// </summary>
    /// <param name="userId">User whose played status is checked.</param>
    /// <param name="personIds">Person ids to filter by (OR logic).</param>
    /// <param name="topParentIds">Library root ids to scope the query.</param>
    /// <param name="limit">Maximum number of items to return.</param>
    /// <returns>Lightweight projections of matching unwatched movies.</returns>
    IReadOnlyList<RecommendationItem> GetUnwatchedMoviesByPersons(
        Guid userId,
        IReadOnlyList<Guid> personIds,
        IReadOnlyList<Guid> topParentIds,
        int limit);

    /// <summary>
    /// Returns unwatched movie candidates that carry the given tags.
    /// </summary>
    /// <param name="userId">User whose played status is checked.</param>
    /// <param name="tags">Tags to filter by (OR logic).</param>
    /// <param name="topParentIds">Library root ids to scope the query.</param>
    /// <param name="limit">Maximum number of items to return.</param>
    /// <returns>Lightweight projections of matching unwatched movies.</returns>
    IReadOnlyList<RecommendationItem> GetUnwatchedMoviesByTags(
        Guid userId,
        IReadOnlyList<string> tags,
        IReadOnlyList<Guid> topParentIds,
        int limit);

    /// <summary>
    /// Returns a random sample of unwatched movies for cold-start users.
    /// </summary>
    /// <param name="userId">User whose played status is checked.</param>
    /// <param name="topParentIds">Library root ids to scope the query.</param>
    /// <param name="limit">Maximum number of items to return.</param>
    /// <returns>Lightweight projections of random unwatched movies.</returns>
    IReadOnlyList<RecommendationItem> GetRandomUnwatchedMovies(
        Guid userId,
        IReadOnlyList<Guid> topParentIds,
        int limit);
}

/// <summary>
/// Minimal projection of a library item used by <see cref="IRecommendationQueryProvider"/>.
/// Contains only the fields needed by the recommendation engine for scoring.
/// </summary>
/// <param name="Id">Jellyfin item GUID.</param>
/// <param name="TmdbId">TMDB provider id, or null if not available.</param>
/// <param name="Genres">Genre names.</param>
/// <param name="Tags">Tag names.</param>
/// <param name="Studios">Studio names.</param>
/// <param name="CommunityRating">Community rating (0–10), or null.</param>
/// <param name="PremiereDate">Premiere date in UTC, or null.</param>
public sealed record RecommendationItem(
    Guid Id,
    string? TmdbId,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Studios,
    float? CommunityRating,
    DateTime? PremiereDate);
