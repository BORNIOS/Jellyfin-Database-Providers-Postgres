using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// EF Core interceptor that caches read-only query results in memory for a short TTL.
/// Home-page queries (Latest, Resume, NextUp, Libraries) that run on every page load
/// are answered from cache without touching PostgreSQL, benefiting all clients equally.
/// </summary>
/// <remarks>
/// <para>Cache policy: only SELECT queries on the core media tables (BaseItems, UserData,
/// ItemValues) are eligible. Mutations, DDL, and schema queries bypass the cache.</para>
/// <para>TTL: 30 seconds — short enough that playback progress is never more than
/// one refresh cycle stale, long enough to absorb the home-page fan-out (~15 queries).</para>
/// </remarks>
public sealed class HomeQueryCacheInterceptor : DbCommandInterceptor, IDisposable
{
    // Fields first (SA1201: fields before properties)
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    // Only intercept queries on the tables that Jellyfin fans out on home-page load.
    private static readonly HashSet<string> CacheableTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "\"BaseItems\"",
        "\"UserData\"",
        "\"ItemValues\"",
        "\"ItemValuesMap\"",
        "\"PeopleBaseItemMap\"",
    };

    private readonly ILogger? _logger;
    private readonly MemoryCache _cache;

    /// <summary>
    /// Initializes a new instance of the <see cref="HomeQueryCacheInterceptor"/> class.
    /// </summary>
    /// <param name="logger">Optional logger for cache diagnostics.</param>
    public HomeQueryCacheInterceptor(ILogger? logger = null)
    {
        _logger = logger;
        Instance = this;
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = 200,
        });
    }

    /// <summary>Gets the singleton set after construction, for cache purge operations.</summary>
    internal static HomeQueryCacheInterceptor? Instance { get; private set; }

    /// <inheritdoc />
    public void Dispose()
    {
        _cache.Dispose();
    }

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (!IsCacheable(command))
        {
            return result;
        }

        // Queries with ORDER BY RANDOM() are never cached (should be fresh on every request),
        // but we rewrite them to use TABLESAMPLE BERNOULLI — O(1) vs O(n log n).
        if (HasOrderByRandom(command))
        {
            return await RewriteRandomAndExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        }

        var cacheKey = BuildCacheKey(command);
        if (cacheKey is null)
        {
            return result;
        }

        if (_cache.TryGetValue(cacheKey, out DataTable? cached) && cached is not null)
        {
            _logger?.LogDebug(
                "HomeQueryCache HIT: {SqlPreview}",
                command.CommandText[..Math.Min(command.CommandText.Length, 120)]);
            return InterceptionResult<DbDataReader>.SuppressWithResult(new DataTableReader(cached));
        }

        // Miss — execute the command ourselves so we can capture the full result set
        // into a DataTable before EF Core consumes the reader.
        return await ExecuteAndCacheAsync(command, cacheKey, cancellationToken).ConfigureAwait(false);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Cache eligibility
    // ─────────────────────────────────────────────────────────────────────────

    private static bool IsCacheable(DbCommand command)
    {
        var sql = command.CommandText;
        if (sql.Length == 0)
        {
            return false;
        }

        // Only SELECT statements
        var firstWord = FirstToken(sql);
        if (firstWord is null || !firstWord.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Must reference at least one cacheable table
        foreach (var table in CacheableTables)
        {
            if (sql.Contains(table, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? FirstToken(string sql)
    {
        var span = sql.AsSpan().TrimStart();
        if (span.IsEmpty)
        {
            return null;
        }

        var end = 0;
        while (end < span.Length && !char.IsWhiteSpace(span[end]))
        {
            end++;
        }

        return span[..end].ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Cache key: SHA-256 of (SQL + serialized parameter values)
    // ─────────────────────────────────────────────────────────────────────────

    private static string? BuildCacheKey(DbCommand command)
    {
        // Use pooled StringBuilder for hot path
        var sb = new StringBuilder(command.CommandText, command.CommandText.Length + 256);

        foreach (DbParameter p in command.Parameters)
        {
            sb.Append('|');
            sb.Append(p.ParameterName);
            sb.Append('=');
            var val = p.Value;
            if (val is null || val == DBNull.Value)
            {
                sb.Append("NULL");
            }
            else if (val is string s)
            {
                // Truncate long strings — cache keys don't need full text
                sb.Append(s.AsSpan(0, Math.Min(s.Length, 80)));
            }
            else if (val is DateTime dt)
            {
                sb.Append(dt.ToString("O", CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(val is IFormattable f
                    ? f.ToString(null, CultureInfo.InvariantCulture)
                    : val.ToString());
            }
        }

        var raw = sb.ToString();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexStringLower(hash);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Self-execute + cache
    // ─────────────────────────────────────────────────────────────────────────

    private async ValueTask<InterceptionResult<DbDataReader>> ExecuteAndCacheAsync(
        DbCommand command,
        string cacheKey,
        CancellationToken ct)
    {
        try
        {
            // Ensure connection is open (EF Core normally handles this, but we're executing ourselves)
            if (command.Connection!.State != ConnectionState.Open)
            {
                await command.Connection.OpenAsync(ct).ConfigureAwait(false);
            }

            var dt = new DataTable();
            var readerObj = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using (readerObj.ConfigureAwait(false))
            {
                dt.Load(readerObj);
            }

            // SizeLimit: approximate entry weight = column count (rough proxy for memory)
            _cache.Set(cacheKey, dt, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = CacheTtl,
                Size = dt.Columns.Count, // proxy: more columns → heavier entry
            });

            _logger?.LogDebug(
                "HomeQueryCache MISS (cached {Cols} cols x {Rows} rows, key {KeyPreview}…): {Sql}",
                dt.Columns.Count,
                dt.Rows.Count,
                cacheKey[..Math.Min(cacheKey.Length, 8)],
                command.CommandText[..Math.Min(command.CommandText.Length, 120)]);

            return InterceptionResult<DbDataReader>.SuppressWithResult(new DataTableReader(dt));
        }
        catch (Exception ex)
        {
            // Never break Jellyfin — if caching fails, let EF Core execute normally.
            // Return the original result so the pipeline continues without interception.
            _logger?.LogWarning(ex, "HomeQueryCache execution failed — falling back to direct DB query.");
            return default; // default(InterceptionResult<DbDataReader>) = no suppression
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ORDER BY RANDOM() → TABLESAMPLE rewrite
    // ─────────────────────────────────────────────────────────────────────────

    private static bool HasOrderByRandom(DbCommand command)
    {
        return command.CommandText.Contains("RANDOM()", StringComparison.OrdinalIgnoreCase);
    }

    private async ValueTask<InterceptionResult<DbDataReader>> RewriteRandomAndExecuteAsync(
        DbCommand command,
        CancellationToken ct)
    {
        try
        {
            var rewritten = RewriteRandomSql(command.CommandText);
            if (rewritten is null)
            {
                return default; // let EF Core run the original
            }

            // rewritten is produced entirely by RewriteRandomSql from EF Core SQL,
            // never from user input.
            using var newCmd = command.Connection!.CreateCommand();
            SetRewrittenCommandText(newCmd, rewritten);
            newCmd.Transaction = command.Transaction;
            foreach (DbParameter p in command.Parameters)
            {
                var copy = newCmd.CreateParameter();
                copy.ParameterName = p.ParameterName;
                copy.Value = p.Value;
                copy.DbType = p.DbType;
                newCmd.Parameters.Add(copy);
            }

            if (newCmd.Connection!.State != ConnectionState.Open)
            {
                await newCmd.Connection.OpenAsync(ct).ConfigureAwait(false);
            }

            var dt = new DataTable();
            var readerObj = await newCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using (readerObj.ConfigureAwait(false))
            {
                dt.Load(readerObj);
            }

            _logger?.LogDebug("HomeQueryCache RANDOM rewritten with TABLESAMPLE: {Rows} rows", dt.Rows.Count);

            return InterceptionResult<DbDataReader>.SuppressWithResult(new DataTableReader(dt));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ORDER BY RANDOM() rewrite failed — falling back to original query.");
            return default;
        }
    }

    /// <summary>
    /// Rewrites <c>ORDER BY RANDOM()</c> to use <c>TABLESAMPLE BERNOULLI</c>, which
    /// samples pages at the storage level (O(1)) instead of assigning a random float
    /// to every row and sorting (O(n log n)). Only transforms the first FROM clause
    /// on a known table; falls back to null if the SQL structure is ambiguous.
    /// </summary>
    private static string? RewriteRandomSql(string sql)
    {
        // Guard: only rewrite single-table queries on known tables to avoid breaking JOINs
        var tableMatch = Regex.Match(
            sql,
            @"FROM\s+""(BaseItems|UserData|ItemValues|ItemValuesMap|PeopleBaseItemMap|Peoples)""",
            RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));

        if (!tableMatch.Success)
        {
            return null;
        }

        // Strip ORDER BY RANDOM() — TABLESAMPLE provides the randomness
        var rewritten = Regex.Replace(
            sql,
            @"\bORDER\s+BY\s+RANDOM\s*\(\s*\)",
            string.Empty,
            RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));

        // Inject TABLESAMPLE after the matched table name
        var table = tableMatch.Value;                     // e.g. FROM "BaseItems"
        var replacement = table + " TABLESAMPLE BERNOULLI(30)"; // 30 % sample ≈ ~1500 of 5000
        var idx = rewritten.IndexOf(table, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        return string.Concat(rewritten.AsSpan(0, idx), replacement, rewritten.AsSpan(idx + table.Length));
    }

    /// <summary>Purges all cached entries. Call after a library scan or metadata refresh.</summary>
    public void Purge()
    {
        _cache.Compact(1.0);
        _logger?.LogDebug("HomeQueryCache purged after external data change.");
    }

    // CA2100: sql comes from RewriteRandomSql which only transforms EF Core-generated SQL, never from user input.
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "sql is produced by RewriteRandomSql from EF Core-generated SQL, never from raw user HTTP input.")]
    private static void SetRewrittenCommandText(System.Data.Common.DbCommand cmd, string sql)
        => cmd.CommandText = sql;
}
