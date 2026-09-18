// ──────────────────────────────────────────────────────────────────────────────
// BUILD-TIME CONTRACT — do NOT modify.
//
// This file is a compile-time mirror of the public API declared in the
// JellyTrend plugin (Jellyfin.Plugin.JellyTrend.Api.IJellyTrendStoreProvider).
// It exists so this project can implement the interface without referencing the
// JellyTrend DLL at build time (no HintPath, no NuGet dependency).
//
// Rules:
//  • Namespace, type names and method signatures MUST stay identical to the
//    originals in JellyTrend — any drift will cause a MissingMethodException at
//    runtime, because the call is forwarded by name and parameter type.
//  • Every member uses only BCL types: a type declared in either assembly would
//    never match the mirror on the other side.
//  • When the JellyTrend contract changes, update this file to match.
//  • This file ships in the Postgres plugin DLL but is NEVER loaded at runtime
//    for its type identity — JellyTrend loads the real types from its own DLL.
// ──────────────────────────────────────────────────────────────────────────────
using System;

namespace Jellyfin.Plugin.JellyTrend.Api;

/// <summary>
/// Optional persistent store offered by a database provider plugin.
/// </summary>
/// <remarks>
/// <para>
/// When a provider implements this contract, JellyTrend keeps its data in that database instead of in
/// loose JSON files: the feature cache, the trending list and the per-user recommendations become rows
/// with a real history, so an update or a concurrent run cannot lose them and the next run only has to
/// process what changed.
/// </para>
/// <para>
/// <b>Contract rules.</b> Every member uses only BCL types on purpose: JellyTrend compiles its own copy
/// of the interface (same full name, different assembly), and the calls are forwarded by name and by
/// exact parameter type. A type declared in one of the two assemblies would never match.
/// </para>
/// <para>
/// <b>Never throw</b> for recoverable errors: return an empty array or <see langword="false"/> instead, so
/// the caller can fall back to JSON without failing the task. The schema is created by the provider only
/// when <see cref="EnsureSchema"/> is called — a server without JellyTrend installed never gets these
/// tables.
/// </para>
/// </remarks>
public interface IJellyTrendStoreProvider
{
    /// <summary>
    /// Creates the schema and tables when they are missing. Idempotent.
    /// </summary>
    /// <returns><see langword="true"/> when the store is ready to be used.</returns>
    bool EnsureSchema();

    /// <summary>
    /// Gets the version of the schema this provider manages.
    /// </summary>
    /// <returns>The schema version, or 0 when the schema does not exist yet.</returns>
    int GetSchemaVersion();

    /// <summary>
    /// Describes the store for the log ("PostgreSQL 17.11, schema jellytrend").
    /// </summary>
    /// <returns>A short description of the backend that holds the data.</returns>
    string DescribeBackend();

    /// <summary>
    /// Replaces the cached features of the given items.
    /// </summary>
    /// <param name="itemIds">Item ids, one per entry.</param>
    /// <param name="facetsJson">Features of each item as a JSON object, aligned with <paramref name="itemIds"/>.</param>
    /// <param name="contentHashes">Fingerprint of the item metadata, used to skip unchanged items.</param>
    /// <returns>The number of rows written.</returns>
    int ReplaceItemFeatures(Guid[] itemIds, string[] facetsJson, string[] contentHashes);

    /// <summary>
    /// Reads the cached features of the given items.
    /// </summary>
    /// <param name="itemIds">Item ids to read.</param>
    /// <returns>One JSON object per id, in the same order; null for ids with no cached features.</returns>
    string?[] GetItemFeatures(Guid[] itemIds);

    /// <summary>
    /// Reads every cached feature document at once.
    /// </summary>
    /// <returns>A JSON object keyed by item id, or null when there is nothing cached.</returns>
    string? GetItemFeaturesJson();

    /// <summary>
    /// Replaces the whole trending list.
    /// </summary>
    /// <param name="itemIds">Local item ids of the trending entries.</param>
    /// <param name="entriesJson">One trending entry per id, as JSON, aligned with <paramref name="itemIds"/>.</param>
    /// <param name="syncedAt">Moment the list was refreshed from the external provider.</param>
    void ReplaceTrending(Guid[] itemIds, string[] entriesJson, DateTime syncedAt);

    /// <summary>
    /// Reads the trending list as the JSON document JellyTrend already understands.
    /// </summary>
    /// <returns>The document, or null when there is nothing stored.</returns>
    string? GetTrendingJson();

    /// <summary>
    /// Replaces the stored recommendations of a user.
    /// </summary>
    /// <param name="userId">User the recommendations belong to.</param>
    /// <param name="itemIds">Recommended item ids, best first.</param>
    /// <param name="scores">Score of each item, aligned with <paramref name="itemIds"/>.</param>
    /// <param name="generatedAt">Moment the run produced them.</param>
    /// <param name="runId">Run that produced them, for the history.</param>
    void ReplaceUserRecommendations(Guid userId, Guid[] itemIds, double[] scores, DateTime generatedAt, Guid runId);

    /// <summary>
    /// Reads the stored recommendations of a user.
    /// </summary>
    /// <param name="userId">User to read.</param>
    /// <returns>The recommended item ids, best first; empty when there is nothing stored.</returns>
    Guid[] GetUserRecommendations(Guid userId);

    /// <summary>
    /// Lists the users that have stored recommendations.
    /// </summary>
    /// <returns>User ids, newest run first.</returns>
    Guid[] GetUsersWithRecommendations();

    /// <summary>
    /// Replaces the items a user must not be shown again (already seen, dismissed).
    /// </summary>
    /// <param name="userId">User the suppression belongs to.</param>
    /// <param name="itemIds">Suppressed item ids.</param>
    /// <param name="reasons">Reason of each item, aligned with <paramref name="itemIds"/>.</param>
    void ReplaceSuppressed(Guid userId, Guid[] itemIds, string[] reasons);

    /// <summary>
    /// Reads the items a user must not be shown again.
    /// </summary>
    /// <param name="userId">User to read.</param>
    /// <returns>The suppressed item ids; empty when there are none.</returns>
    Guid[] GetSuppressed(Guid userId);

    /// <summary>
    /// Records the start of a run.
    /// </summary>
    /// <param name="kind">Kind of run ("recommendations" or "trending").</param>
    /// <param name="startedAt">Moment the run started.</param>
    /// <returns>The run id, to pass to <see cref="FinishRun"/>.</returns>
    Guid StartRun(string kind, DateTime startedAt);

    /// <summary>
    /// Records the outcome of a run.
    /// </summary>
    /// <param name="runId">Run to close.</param>
    /// <param name="status">Outcome ("ok", "partial", "failed" or "canceled").</param>
    /// <param name="usersOk">Users that finished without errors.</param>
    /// <param name="usersFailed">Users that failed.</param>
    /// <param name="durationMs">Wall-clock duration in milliseconds.</param>
    /// <param name="message">Optional detail for the history.</param>
    void FinishRun(Guid runId, string status, int usersOk, int usersFailed, int durationMs, string? message);
}
