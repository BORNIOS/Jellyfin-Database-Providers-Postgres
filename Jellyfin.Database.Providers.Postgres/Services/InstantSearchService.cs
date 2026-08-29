using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Providers.Postgres.Services.Models;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// Low-latency search service that bypasses EF Core and queries PostgreSQL directly
/// using pg_trgm trigram similarity. Target: &lt; 15 ms round-trip for up to 8 results.
/// Falls back to plain ILIKE when pg_trgm is unavailable.
/// </summary>
public sealed class InstantSearchService
{
    private readonly ILogger<InstantSearchService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="InstantSearchService"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public InstantSearchService(ILogger<InstantSearchService> logger)
    {
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Searches <c>BaseItems</c> for media matching <paramref name="term"/> and returns
    /// a minimal, ranked result set. Uses pg_trgm when available.
    /// </summary>
    /// <param name="term">The user-typed search term (raw, not sanitised beyond trimming).</param>
    /// <param name="connectionString">Active Npgsql connection string.</param>
    /// <param name="limit">Maximum results to return (default 8, max 50).</param>
    /// <param name="mediaTypes">Optional comma-separated list of Jellyfin media types to filter by.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of ranked search results.</returns>
    public async Task<List<InstantSearchResult>> SearchAsync(
        string term,
        string connectionString,
        int limit = 8,
        string? mediaTypes = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(term) || term.Length < 1)
        {
            return new List<InstantSearchResult>();
        }

        limit = Math.Clamp(limit, 1, 50);
        term = term.Trim();

        // MaxAutoPrepare=0 prevents plan cache pollution in the shared pool.
        // Pooling remains ON (default) so TCP+TLS is reused across keystrokes.
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxAutoPrepare = 0,
            CommandTimeout = 1,
        };

        using var conn = new NpgsqlConnection(builder.ConnectionString);
        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "InstantSearch: could not open database connection.");
            return new List<InstantSearchResult>();
        }

        return PostgresDatabaseProvider.TrgmAvailable
            ? await SearchWithTrgmAsync(conn, term, limit, mediaTypes, ct).ConfigureAwait(false)
            : await SearchWithIlikeAsync(conn, term, limit, mediaTypes, ct).ConfigureAwait(false);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // pg_trgm path — ranked, uses GIN index
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<List<InstantSearchResult>> SearchWithTrgmAsync(
        NpgsqlConnection conn,
        string term,
        int limit,
        string? mediaTypes,
        CancellationToken ct)
    {
        // word_similarity is better than similarity for prefix/partial matches:
        //   word_similarity('bat', 'Batman Begins') ≈ 1.0
        //   similarity('bat', 'Batman Begins')      ≈ 0.18
        // ILIKE activates the GIN index; word_similarity ranks from that candidate set.
        var mediaTypeFilter = BuildMediaTypeFilter(mediaTypes);
        var sql = $"""
            SELECT
                "Id",
                "Name",
                "Type",
                "ProductionYear",
                "Artists",
                "Album",
                "SeriesName",
                GREATEST(
                    word_similarity(@term, coalesce("Name",         '')),
                    word_similarity(@term, coalesce("OriginalTitle",'')),
                    word_similarity(@term, coalesce("Album",        '')),
                    word_similarity(@term, coalesce("Artists",      '')),
                    word_similarity(@term, coalesce("SeriesName",   ''))
                ) AS relevance
            FROM "BaseItems"
            WHERE "IsVirtualItem" = false
              AND (
                    "Name"          ILIKE @like
                 OR "OriginalTitle" ILIKE @like
                 OR "Album"         ILIKE @like
                 OR "Artists"       ILIKE @like
                 OR "SeriesName"    ILIKE @like
              )
              {mediaTypeFilter}
            ORDER BY relevance DESC, "DateCreated" DESC NULLS LAST
            LIMIT @limit;
            """;

        return await ExecuteSearchAsync(conn, sql, term, limit, ct).ConfigureAwait(false);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Fallback path — plain ILIKE, no trgm ordering
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<List<InstantSearchResult>> SearchWithIlikeAsync(
        NpgsqlConnection conn,
        string term,
        int limit,
        string? mediaTypes,
        CancellationToken ct)
    {
        var mediaTypeFilter = BuildMediaTypeFilter(mediaTypes);
        var sql = $"""
            SELECT
                "Id",
                "Name",
                "Type",
                "ProductionYear",
                "Artists",
                "Album",
                "SeriesName",
                CASE WHEN lower("Name") = lower(@term) THEN 1.0 ELSE 0.5 END AS relevance
            FROM "BaseItems"
            WHERE "IsVirtualItem" = false
              AND (
                    "Name"          ILIKE @like
                 OR "OriginalTitle" ILIKE @like
                 OR "Album"         ILIKE @like
                 OR "Artists"       ILIKE @like
                 OR "SeriesName"    ILIKE @like
              )
              {mediaTypeFilter}
            ORDER BY relevance DESC, "DateCreated" DESC NULLS LAST
            LIMIT @limit;
            """;

        return await ExecuteSearchAsync(conn, sql, term, limit, ct).ConfigureAwait(false);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Shared execution + mapping
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<List<InstantSearchResult>> ExecuteSearchAsync(
        NpgsqlConnection conn,
        string sql,
        string term,
        int limit,
        CancellationToken ct)
    {
        var results = new List<InstantSearchResult>(limit);
        try
        {
            // sql comes from BuildSearchSql (hardcoded templates), not user input (CA2100).
            using var cmd = CreateSearchCommand(conn, sql);
            cmd.Parameters.AddWithValue("term", term);
            cmd.Parameters.AddWithValue("like", $"%{EscapeLike(term)}%");
            cmd.Parameters.AddWithValue("limit", limit);

            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                results.Add(await MapRowAsync(reader, ct).ConfigureAwait(false));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "InstantSearch query failed.");
        }

        return results;
    }

    private static async Task<InstantSearchResult> MapRowAsync(NpgsqlDataReader reader, CancellationToken ct)
        => new InstantSearchResult(
            Id: reader.GetGuid(0),
            Name: await reader.IsDBNullAsync(1, ct).ConfigureAwait(false) ? null : reader.GetString(1),
            Type: await reader.IsDBNullAsync(2, ct).ConfigureAwait(false) ? null : reader.GetString(2),
            Year: await reader.IsDBNullAsync(3, ct).ConfigureAwait(false) ? null : reader.GetInt32(3),
            Artists: await reader.IsDBNullAsync(4, ct).ConfigureAwait(false) ? null : reader.GetString(4),
            Album: await reader.IsDBNullAsync(5, ct).ConfigureAwait(false) ? null : reader.GetString(5),
            SeriesName: await reader.IsDBNullAsync(6, ct).ConfigureAwait(false) ? null : reader.GetString(6),
            Relevance: await reader.IsDBNullAsync(7, ct).ConfigureAwait(false)
                ? 0.0
                : Convert.ToDouble(reader.GetValue(7), CultureInfo.InvariantCulture));

    private static string BuildMediaTypeFilter(string? mediaTypes)
    {
        if (string.IsNullOrWhiteSpace(mediaTypes))
        {
            return string.Empty;
        }

        // e.g. "Movie,Series,Audio" → AND "MediaType" IN ('Movie','Series','Audio')
        var types = mediaTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (types.Length == 0)
        {
            return string.Empty;
        }

        // Sanitise: only allow alphanumeric
        var safe = new System.Text.StringBuilder();
        safe.Append("AND \"MediaType\" IN (");
        for (var i = 0; i < types.Length; i++)
        {
            if (i > 0)
            {
                safe.Append(',');
            }

            // Escape single-quote by doubling it (SQL standard, parameter is sanitised above)
            var t = types[i].Replace("'", "''", StringComparison.Ordinal);
            safe.Append(CultureInfo.InvariantCulture, $"'{t}'");
        }

        safe.Append(')');
        return safe.ToString();
    }

    /// <summary>Escapes LIKE special characters in the user-supplied term.</summary>
    private static string EscapeLike(string term)
        => term
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    // sql is produced by BuildSearchSql from hardcoded templates. User-supplied values are always
    // passed as parameters (term, like, limit) — never interpolated into the SQL string.
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "sql is built from hardcoded templates; user values are parameterized via NpgsqlCommand.Parameters.")]
    private static NpgsqlCommand CreateSearchCommand(NpgsqlConnection conn, string sql)
        => new NpgsqlCommand(sql, conn);
}
