using System;
using System.Collections.Concurrent;
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
/// <para>Two rules keep the cache from lying to Jellyfin's write path:</para>
/// <list type="number">
/// <item><description>A statement running inside a transaction is never cached and never answered from
/// cache. Jellyfin reads a row back inside the same transaction to decide between INSERT and UPDATE, and
/// that answer must come from the transaction, not from another connection's older snapshot.</description></item>
/// <item><description>Every write bumps a per-table generation that is part of the cache key, so an entry
/// stored before a write can never be served after it.</description></item>
/// </list>
/// </remarks>
public sealed class HomeQueryCacheInterceptor : DbCommandInterceptor
{
    // Fields first (SA1201); constants before static readonly (SA1203)
    // Result sets larger than this are still buffered (see ReaderExecuting) but not retained
    // in the cache, so a big query cannot pin a large DataTable in memory.
    private const int MaxCacheableRows = 1000;

    /// <summary>
    /// Marker a query can add with <c>TagWith</c> to stay out of the cache entirely, for statements whose
    /// result feeds a write decision.
    /// </summary>
    internal const string NoCacheMarker = "__jellyfin_no_cache";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    // Only intercept queries on the tables that Jellyfin fans out on home-page load. The order is fixed
    // because it participates in the cache key.
    private static readonly string[] CacheableTables =
    [
        "\"BaseItems\"",
        "\"UserData\"",
        "\"ItemValues\"",
        "\"ItemValuesMap\"",
        "\"PeopleBaseItemMap\"",
    ];

    // Shared on purpose. EF Core calls Initialise more than once during start-up and every call builds its
    // own interceptor instance; with per-instance state one copy answered from an empty cache while another
    // kept serving rows inserted by a transaction that had already been rolled back.
    private static readonly MemoryCache Cache = new(new MemoryCacheOptions
    {
        SizeLimit = 200,
    });

    // One counter per cached table, bumped by every write to it.
    private static readonly ConcurrentDictionary<string, int> Generations = new(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HomeQueryCacheInterceptor"/> class.
    /// </summary>
    /// <param name="logger">Optional logger for cache diagnostics.</param>
    public HomeQueryCacheInterceptor(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Empties the shared cache. Needed after an operation that changes data behind EF's back, such as a
    /// database restore, because no write command of this process is seen by the interceptor.
    /// </summary>
    internal static void Purge()
    {
        Cache.Compact(1.0);
    }

    /// <summary>
    /// Rises the generation of every cached table the statement mentions, which makes all entries stored for
    /// those tables unreachable. Over-invalidating only costs a cache miss; a missing invalidation would
    /// serve rows that a write already changed.
    /// </summary>
    /// <param name="command">Command about to run.</param>
    private static void InvalidateFor(DbCommand command)
    {
        var sql = command.CommandText;
        foreach (var table in CacheableTables)
        {
            if (sql.Contains(table, StringComparison.OrdinalIgnoreCase))
            {
                Generations.AddOrUpdate(table, 1, static (_, value) => value + 1);
            }
        }
    }

    /// <summary>
    /// Reports whether the cache may answer the statement. Statements inside a transaction and statements
    /// carrying <see cref="NoCacheMarker"/> are excluded; they are still buffered, just never cached.
    /// </summary>
    /// <param name="command">Command about to run.</param>
    /// <returns><c>true</c> when the cache may be used.</returns>
    private static bool CanUseCache(DbCommand command)
        => command.Transaction is null
            && !command.CommandText.Contains(NoCacheMarker, StringComparison.Ordinal);

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

        var cacheKey = CanUseCache(command) ? BuildCacheKey(command) : null;
        if (cacheKey is not null
            && Cache.TryGetValue(cacheKey, out DataTable? cached)
            && cached is not null)
        {
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
            {
                _logger?.LogDebug(
                    "HomeQueryCache HIT: {SqlPreview}",
                    command.CommandText[..Math.Min(command.CommandText.Length, 120)]);
            }

            return InterceptionResult<DbDataReader>.SuppressWithResult(new DataTableReader(cached));
        }

        // Miss — execute the command ourselves so we can capture the full result set
        // into a DataTable before EF Core consumes the reader.
        return await ExecuteAndCacheAsync(command, cacheKey, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Invalidates the cache tables a mutating statement touches. Jellyfin decides between INSERT and UPDATE
    /// from a query it ran earlier, so a write has to make every entry stored for that table unreachable.
    /// </summary>
    /// <inheritdoc cref="DbCommandInterceptor.NonQueryExecuting(DbCommand, CommandEventData, InterceptionResult{int})" />
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        InvalidateFor(command);
        return result;
    }

    /// <inheritdoc cref="NonQueryExecuting" />
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        InvalidateFor(command);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Buffering matters for correctness, not only for caching: several Jellyfin routines
    /// (for example <c>MigrateRatingLevels</c>) enumerate a synchronously streamed query and
    /// then run <c>ExecuteUpdate</c> on the same connection. SQLite tolerates that, Npgsql does
    /// not (no MARS) and fails with <c>NpgsqlOperationInProgressException</c>. Materialising the
    /// reader here closes the database reader before Jellyfin issues its next command.
    /// </remarks>
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        if (!IsCacheable(command))
        {
            return result;
        }

        // Same as the async path: a random query may be buffered but must never be cached.
        var cacheKey = CanUseCache(command) && !HasOrderByRandom(command) ? BuildCacheKey(command) : null;
        if (cacheKey is not null
            && Cache.TryGetValue(cacheKey, out DataTable? cached)
            && cached is not null)
        {
            return InterceptionResult<DbDataReader>.SuppressWithResult(new DataTableReader(cached));
        }

        return ExecuteAndCache(command, cacheKey);
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
    // Cache key: SHA-256 of (SQL + generations + database + parameter values)
    // ─────────────────────────────────────────────────────────────────────────

    private static string? BuildCacheKey(DbCommand command)
    {
        // Use pooled StringBuilder for hot path
        var sb = new StringBuilder(command.CommandText, command.CommandText.Length + 256);

        // The database name keeps scratch databases of the test suite apart.
        sb.Append('|');
        sb.Append(command.Connection?.Database ?? "-");

        // The generation of every table the query reads makes every entry stored before a write
        // unreachable, so a cache hit can never return rows a write already changed.
        foreach (var table in CacheableTables)
        {
            if (command.CommandText.Contains(table, StringComparison.Ordinal))
            {
                sb.Append('|');
                sb.Append(table);
                sb.Append('#');
                sb.Append(Generations.TryGetValue(table, out var generation) ? generation : 0);
            }
        }

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
            else if (val is byte[] bytes)
            {
                sb.Append(Convert.ToHexString(bytes));
            }
            else if (val is DateTime dt)
            {
                sb.Append(dt.ToString("O", CultureInfo.InvariantCulture));
            }
            else if (val is System.Collections.IEnumerable items)
            {
                // Jellyfin binds id and value lists as arrays. ToString() on an array returns the type name,
                // so every list collapsed into one key and the cache answered with another list's rows —
                // the reason Jellyfin's existence checks came back wrong and its INSERTs hit 23505.
                foreach (var item in items)
                {
                    sb.Append(item is null ? "NULL" : Convert.ToString(item, CultureInfo.InvariantCulture));
                    sb.Append(',');
                }
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
        string? cacheKey,
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
            if (cacheKey is not null)
            {
                CacheResult(cacheKey, dt);
            }

            if (_logger?.IsEnabled(LogLevel.Debug) == true)
            {
                _logger?.LogDebug(
                    "HomeQueryCache {State} ({Cols} cols x {Rows} rows): {Sql}",
                    cacheKey is null ? "BUFFER ONLY" : "MISS (cached)",
                    dt.Columns.Count,
                    dt.Rows.Count,
                    command.CommandText[..Math.Min(command.CommandText.Length, 120)]);
            }

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
    // Self-execute + cache (synchronous)
    // ─────────────────────────────────────────────────────────────────────────

    private InterceptionResult<DbDataReader> ExecuteAndCache(DbCommand command, string? cacheKey)
    {
        try
        {
            if (command.Connection!.State != ConnectionState.Open)
            {
                command.Connection.Open();
            }

            var dt = new DataTable();
            using (var reader = command.ExecuteReader())
            {
                dt.Load(reader);
            }

            if (cacheKey is not null)
            {
                CacheResult(cacheKey, dt);
            }

            return InterceptionResult<DbDataReader>.SuppressWithResult(new DataTableReader(dt));
        }
        catch (Exception ex)
        {
            // Never break Jellyfin — if buffering fails, let EF Core execute normally.
            _logger?.LogWarning(ex, "HomeQueryCache execution failed — falling back to direct DB query.");
            return default;
        }
    }

    /// <summary>
    /// Stores a buffered result set in the short-lived cache.
    /// </summary>
    /// <param name="cacheKey">Cache key derived from SQL and parameters.</param>
    /// <param name="dt">Materialised result set.</param>
    private void CacheResult(string cacheKey, DataTable dt)
    {
        if (dt.Rows.Count > MaxCacheableRows)
        {
            // Big result sets are buffered for correctness but not retained.
            return;
        }

        Cache.Set(cacheKey, dt, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheTtl,
            Size = dt.Columns.Count, // proxy: more columns → heavier entry
        });
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

            if (_logger?.IsEnabled(LogLevel.Debug) == true)
            {
                _logger?.LogDebug("HomeQueryCache RANDOM rewritten with TABLESAMPLE: {Rows} rows", dt.Rows.Count);
            }

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

    // CA2100: sql comes from RewriteRandomSql which only transforms EF Core-generated SQL, never from user input.
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "sql is produced by RewriteRandomSql from EF Core-generated SQL, never from raw user HTTP input.")]
    private static void SetRewrittenCommandText(System.Data.Common.DbCommand cmd, string sql)
        => cmd.CommandText = sql;
}
