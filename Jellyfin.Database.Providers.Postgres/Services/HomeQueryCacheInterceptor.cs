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
using Jellyfin.Database.Providers.Postgres.Logging;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Npgsql;

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
/// <item><description>Concurrent misses for the same key share one database execution. Waiters re-check the
/// cache after the owner finishes; if that execution failed, each safely falls back to Jellyfin's normal
/// command pipeline and a later miss may retry.</description></item>
/// </list>
/// </remarks>
public sealed class HomeQueryCacheInterceptor : DbCommandInterceptor
{
    // Fields first (SA1201); constants before static readonly (SA1203)
    // Result sets larger than this are still buffered (see ReaderExecuting) but not retained
    // in the cache, so a big query cannot pin a large DataTable in memory.
    private const int MaxCacheableRows = 1000;
    private const int MaxCacheableCells = 10000;
    private const int CacheSizeLimit = 50000;
    private const int MaxPrefetchPageSize = 100;

    /// <summary>
    /// Marker a query can add with <c>TagWith</c> to stay out of the cache entirely, for statements whose
    /// result feeds a write decision.
    /// </summary>
    internal const string NoCacheMarker = "__jellyfin_no_cache";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PrefetchTimeout = TimeSpan.FromSeconds(2);

    private static readonly Regex PagedBaseItemQueryRegex = new(
        @"\bLIMIT\s+(?<limit>@[A-Za-z0-9_]+|\d+)\s+OFFSET\s+(?<offset>@[A-Za-z0-9_]+|\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

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
        // A unit is one materialised cell. This bounds memory while retaining enough
        // compact home rows and 100-item pages to make repeat navigation immediate.
        SizeLimit = CacheSizeLimit,
    });

    // One counter per cached table, bumped by every write to it.
    private static readonly ConcurrentDictionary<string, int> Generations = new(StringComparer.OrdinalIgnoreCase);

    // A per-key gate prevents a cold or expired entry from fanning one identical query out to every
    // simultaneous home-page request. Gates are removed before release so this dictionary cannot retain
    // cache keys for the lifetime of the server.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> InFlight = new(StringComparer.Ordinal);

    // Speculative work is deliberately capped. A real Jellyfin request must never wait for a page warm-up.
    private static readonly SemaphoreSlim PrefetchSlot = new(1, 1);
    private static readonly ConcurrentDictionary<string, byte> PrefetchInFlight = new(StringComparer.Ordinal);

    private static int _prefetchCompletedCount;
    private static int _prefetchedPageHitCount;

    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HomeQueryCacheInterceptor"/> class.
    /// </summary>
    /// <param name="logger">Optional logger for cache diagnostics.</param>
    public HomeQueryCacheInterceptor(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>Gets the number of cache fills currently coordinated by the interceptor.</summary>
    /// <remarks>Exposed internally only for regression tests; it must return to zero after every fill.</remarks>
    internal static int InFlightCount => InFlight.Count;

    /// <summary>Gets the number of page prefetches currently queued or executing.</summary>
    internal static int PrefetchInFlightCount => PrefetchInFlight.Count;

    /// <summary>Gets completed prefetched pages since the last purge.</summary>
    internal static int PrefetchCompletedCount => Volatile.Read(ref _prefetchCompletedCount);

    /// <summary>Gets requests answered from a prefetched page since the last purge.</summary>
    internal static int PrefetchedPageHitCount => Volatile.Read(ref _prefetchedPageHitCount);

    /// <summary>
    /// Empties the shared cache. Needed after an operation that changes data behind EF's back, such as a
    /// database restore, because no write command of this process is seen by the interceptor.
    /// </summary>
    internal static void Purge()
    {
        Cache.Compact(1.0);
        Interlocked.Exchange(ref _prefetchCompletedCount, 0);
        Interlocked.Exchange(ref _prefetchedPageHitCount, 0);
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
        if (cacheKey is not null && TryGetCachedResult(cacheKey, out var cached))
        {
            ScheduleNextPagePrefetch(command, cached);
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
            {
                _logger?.LogDebug(
                    "HomeQueryCache HIT: {SqlPreview}",
                    command.CommandText[..Math.Min(command.CommandText.Length, 120)]);
            }

            return InterceptionResult<DbDataReader>.SuppressWithResult(CreateBufferedReader(cached));
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
        if (cacheKey is not null && TryGetCachedResult(cacheKey, out var cached))
        {
            ScheduleNextPagePrefetch(command, cached);
            return InterceptionResult<DbDataReader>.SuppressWithResult(CreateBufferedReader(cached));
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

        var firstWord = FirstToken(sql);
        if (firstWord is null || !firstWord.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return CacheableTables.Any(table => sql.Contains(table, StringComparison.Ordinal));
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
        if (cacheKey is null)
        {
            return await ExecuteAndCacheCoreAsync(command, null, ct).ConfigureAwait(false);
        }

        var gate = InFlight.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A concurrent request may have completed the same fill while this request was waiting.
            if (TryGetCachedResult(cacheKey, out var cached))
            {
                ScheduleNextPagePrefetch(command, cached);
                return InterceptionResult<DbDataReader>.SuppressWithResult(CreateBufferedReader(cached));
            }

            return await ExecuteAndCacheCoreAsync(command, cacheKey, ct).ConfigureAwait(false);
        }
        finally
        {
            // Remove before releasing: a subsequent miss can only create a new gate after this execution
            // has finished. Existing waiters still hold the same semaphore and re-check the cache above.
            InFlight.TryRemove(new KeyValuePair<string, SemaphoreSlim>(cacheKey, gate));
            gate.Release();
        }
    }

    /// <summary>Executes and buffers one cache miss without coordinating other requests.</summary>
    /// <remarks>Any error returns no interception result so Jellyfin executes its original command.</remarks>
    private async ValueTask<InterceptionResult<DbDataReader>> ExecuteAndCacheCoreAsync(
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

            var readerObj = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using (readerObj.ConfigureAwait(false))
            {
                var dt = await MaterializeTypedAsync(readerObj, ct).ConfigureAwait(false);

                if (cacheKey is not null)
                {
                    CacheResult(cacheKey, dt, command, isPrefetch: false);
                    ScheduleNextPagePrefetch(command, dt);
                }

                return InterceptionResult<DbDataReader>.SuppressWithResult(CreateBufferedReader(dt));
            }
        }
        catch (Exception ex)
        {
            // Never break Jellyfin — if caching fails, let EF Core execute normally.
            // Return the original result so the pipeline continues without interception.
            PostgresLog.Warn("[Cache] Ejecución async falló; se usará PostgreSQL directo.", ex);
            return default; // default(InterceptionResult<DbDataReader>) = no suppression
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Self-execute + cache (synchronous)
    // ─────────────────────────────────────────────────────────────────────────

    private InterceptionResult<DbDataReader> ExecuteAndCache(DbCommand command, string? cacheKey)
    {
        if (cacheKey is null)
        {
            return ExecuteAndCacheCore(command, null);
        }

        var gate = InFlight.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            if (TryGetCachedResult(cacheKey, out var cached))
            {
                ScheduleNextPagePrefetch(command, cached);
                return InterceptionResult<DbDataReader>.SuppressWithResult(CreateBufferedReader(cached));
            }

            return ExecuteAndCacheCore(command, cacheKey);
        }
        finally
        {
            InFlight.TryRemove(new KeyValuePair<string, SemaphoreSlim>(cacheKey, gate));
            gate.Release();
        }
    }

    /// <summary>Executes and buffers one synchronous cache miss without coordinating other requests.</summary>
    private InterceptionResult<DbDataReader> ExecuteAndCacheCore(DbCommand command, string? cacheKey)
    {
        try
        {
            if (command.Connection!.State != ConnectionState.Open)
            {
                command.Connection.Open();
            }

            using (var reader = command.ExecuteReader())
            {
                var dt = MaterializeTyped(reader);

                if (cacheKey is not null)
                {
                    CacheResult(cacheKey, dt, command, isPrefetch: false);
                    ScheduleNextPagePrefetch(command, dt);
                }

                return InterceptionResult<DbDataReader>.SuppressWithResult(CreateBufferedReader(dt));
            }
        }
        catch (Exception ex)
        {
            // Never break Jellyfin — if buffering fails, let EF Core execute normally.
            PostgresLog.Warn("[Cache] Ejecución sync falló; se usará PostgreSQL directo.", ex);
            return default;
        }
    }

    /// <summary>
    /// Stores a buffered result set in the short-lived cache.
    /// </summary>
    /// <param name="cacheKey">Cache key derived from SQL and parameters.</param>
    /// <param name="dt">Materialised result set.</param>
    /// <param name="command">The EF Core command that produced the result.</param>
    /// <param name="isPrefetch">Whether this result was warmed before a client requested it.</param>
    private void CacheResult(string cacheKey, DataTable dt, DbCommand command, bool isPrefetch)
    {
        var cacheSize = GetCacheSize(dt);
        if (dt.Rows.Count > MaxCacheableRows || cacheSize > MaxCacheableCells)
        {
            // Big result sets are buffered for correctness but not retained.
            return;
        }

        Cache.Set(cacheKey, new CachedResult(dt, isPrefetch), new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheTtl,
            Priority = IsHomeFanOutQuery(command) ? CacheItemPriority.High : CacheItemPriority.Normal,
            Size = cacheSize,
        });
    }

    private static bool TryGetCachedResult(string cacheKey, out DataTable result)
    {
        if (Cache.TryGetValue(cacheKey, out CachedResult? cached) && cached is not null)
        {
            if (cached.IsPrefetched)
            {
                Interlocked.Increment(ref _prefetchedPageHitCount);
            }

            result = cached.Table;
            return true;
        }

        result = null!;
        return false;
    }

    private static int GetCacheSize(DataTable table)
        => Math.Max(1, checked(table.Rows.Count * Math.Max(1, table.Columns.Count)));

    // Home fans out into several small, repeatable BaseItems queries. Retaining those before ordinary
    // library pages makes a refresh or a second client opening Home a memory-only operation.
    private static bool IsHomeFanOutQuery(DbCommand command)
        => command.CommandText.Contains("\"BaseItems\"", StringComparison.Ordinal)
            && command.CommandText.Contains("LIMIT", StringComparison.OrdinalIgnoreCase)
            && !command.CommandText.Contains("OFFSET", StringComparison.OrdinalIgnoreCase);

    private void ScheduleNextPagePrefetch(DbCommand command, DataTable currentPage)
    {
        if (!TryCreatePagePrefetchPlan(command, out var plan)
            || currentPage.Rows.Count != plan.Limit)
        {
            return;
        }

        var snapshot = CommandSnapshot.Create(command, plan);
        if (snapshot is null || !PrefetchInFlight.TryAdd(snapshot.CacheKey, 0))
        {
            return;
        }

        _ = Task.Run(() => PrefetchNextPageAsync(snapshot));
    }

    private async Task PrefetchNextPageAsync(CommandSnapshot snapshot)
    {
        // Do not queue speculative work: if the server is active, the following real request will simply
        // execute normally. This is intentionally a best-effort optimisation.
        if (!PrefetchSlot.Wait(0))
        {
            PrefetchInFlight.TryRemove(snapshot.CacheKey, out _);
            return;
        }

        try
        {
            if (Cache.TryGetValue(snapshot.CacheKey, out CachedResult? existing) && existing is not null)
            {
                return;
            }

            using var timeout = new CancellationTokenSource(PrefetchTimeout);
            using var connection = new NpgsqlConnection(snapshot.ConnectionString);
            await connection.OpenAsync(timeout.Token).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            SetGeneratedCommandText(command, snapshot.CommandText);
            foreach (var parameter in snapshot.Parameters)
            {
                command.Parameters.Add(parameter.Create(command));
            }

            using var reader = await command.ExecuteReaderAsync(timeout.Token).ConfigureAwait(false);
            var page = await MaterializeTypedAsync(reader, timeout.Token).ConfigureAwait(false);
            CacheResult(snapshot.CacheKey, page, command, isPrefetch: true);
            Interlocked.Increment(ref _prefetchCompletedCount);

            if (_logger?.IsEnabled(LogLevel.Debug) == true)
            {
                _logger.LogDebug("HomeQueryCache prefetched page at offset {Offset} ({Rows} rows).", snapshot.Offset, page.Rows.Count);
            }
        }
        catch (OperationCanceledException)
        {
            // A prefetch that cannot finish quickly is intentionally discarded.
        }
        catch (Exception ex)
        {
            PostgresLog.Warn("[Cache] La precarga de la siguiente página falló; la siguiente solicitud consultará PostgreSQL.", ex);
        }
        finally
        {
            PrefetchSlot.Release();
            PrefetchInFlight.TryRemove(snapshot.CacheKey, out _);
        }
    }

    private static bool TryCreatePagePrefetchPlan(DbCommand command, out PagePrefetchPlan plan)
    {
        plan = default;
        var sql = command.CommandText;
        if (!CanUseCache(command)
            || HasOrderByRandom(command)
            || !sql.Contains("\"BaseItems\"", StringComparison.Ordinal)
            || !sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var matches = PagedBaseItemQueryRegex.Matches(sql);
        if (matches.Count != 1)
        {
            // A query with more than one LIMIT/OFFSET pair is normally a navigation include. It cannot be
            // safely advanced by changing one offset because that would alter a different result set.
            return false;
        }

        var match = matches[0];

        if (!TryGetPagingValue(command, match.Groups["limit"].Value, out var limit)
            || !TryGetPagingValue(command, match.Groups["offset"].Value, out var offset)
            || limit is <= 0 or > MaxPrefetchPageSize
            || offset < 0
            || offset > long.MaxValue - limit)
        {
            return false;
        }

        plan = new PagePrefetchPlan(match.Groups["offset"].Value, checked(offset + limit), checked((int)limit));
        return true;
    }

    private static bool TryGetPagingValue(DbCommand command, string token, out long value)
    {
        value = 0;
        if (long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        var parameter = command.Parameters.Cast<DbParameter>().FirstOrDefault(
            p => string.Equals(p.ParameterName, token, StringComparison.Ordinal)
                || string.Equals(p.ParameterName.TrimStart('@'), token.TrimStart('@'), StringComparison.Ordinal));
        if (parameter?.Value is null || parameter.Value == DBNull.Value)
        {
            return false;
        }

        try
        {
            value = Convert.ToInt64(parameter.Value, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return false;
        }
    }

    private static async Task<DataTable> MaterializeTypedAsync(DbDataReader reader, CancellationToken ct)
    {
        var table = CreateTypedTable(reader);
        var values = new object[reader.FieldCount];
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            reader.GetValues(values);
            table.Rows.Add((object[])values.Clone());
        }

        return table;
    }

    private static DataTable MaterializeTyped(DbDataReader reader)
    {
        var table = CreateTypedTable(reader);
        var values = new object[reader.FieldCount];
        while (reader.Read())
        {
            reader.GetValues(values);
            table.Rows.Add((object[])values.Clone());
        }

        return table;
    }

    /// <summary>
    /// Creates a buffered reader that retains the numeric conversion behavior EF Core
    /// receives from Npgsql. <see cref="DataTableReader"/> requires an exact CLR type
    /// for methods such as <see cref="DbDataReader.GetFloat(int)"/>, while Npgsql can
    /// safely read an integer projection as a <see cref="float"/>. Jellyfin's search
    /// query relies on that provider conversion.
    /// </summary>
    private static NpgsqlCompatibleDataTableReader CreateBufferedReader(DataTable table)
        => new NpgsqlCompatibleDataTableReader(table.CreateDataReader());

    private static DataTable CreateTypedTable(DbDataReader reader)
    {
        var table = new DataTable();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
        {
            var name = reader.GetName(ordinal);
            if (string.IsNullOrWhiteSpace(name) || !names.Add(name))
            {
                name = string.Concat("Column", ordinal.ToString(CultureInfo.InvariantCulture));
                names.Add(name);
            }

            table.Columns.Add(name, reader.GetFieldType(ordinal));
        }

        return table;
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
            SetGeneratedCommandText(newCmd, rewritten);
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

            var readerObj = await newCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using (readerObj.ConfigureAwait(false))
            {
                var dt = await MaterializeTypedAsync(readerObj, ct).ConfigureAwait(false);

                if (_logger?.IsEnabled(LogLevel.Debug) == true)
                {
                    _logger?.LogDebug("HomeQueryCache RANDOM rewritten with TABLESAMPLE: {Rows} rows", dt.Rows.Count);
                }

                return InterceptionResult<DbDataReader>.SuppressWithResult(CreateBufferedReader(dt));
            }
        }
        catch (Exception ex)
        {
            PostgresLog.Warn("[Cache] La optimización ORDER BY RANDOM() falló; se usará la consulta original.", ex);
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

    // CA2100: sql comes from either RewriteRandomSql or a captured EF Core command, never from raw user input.
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "sql is produced by RewriteRandomSql from EF Core-generated SQL, never from raw user HTTP input.")]
    private static void SetGeneratedCommandText(System.Data.Common.DbCommand cmd, string sql)
        => cmd.CommandText = sql;

    private readonly record struct PagePrefetchPlan(string OffsetParameterName, long NextOffset, int Limit);

    private sealed class CachedResult
    {
        public CachedResult(DataTable table, bool isPrefetched)
        {
            Table = table;
            IsPrefetched = isPrefetched;
        }

        public DataTable Table { get; }

        public bool IsPrefetched { get; }
    }

    private sealed class CommandSnapshot
    {
        private CommandSnapshot(string connectionString, string commandText, ParameterSnapshot[] parameters, string cacheKey, long offset)
        {
            ConnectionString = connectionString;
            CommandText = commandText;
            Parameters = parameters;
            CacheKey = cacheKey;
            Offset = offset;
        }

        public string ConnectionString { get; }

        public string CommandText { get; }

        public ParameterSnapshot[] Parameters { get; }

        public string CacheKey { get; }

        public long Offset { get; }

        public static CommandSnapshot? Create(DbCommand source, PagePrefetchPlan plan)
        {
            var connectionString = source.Connection?.ConnectionString;
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return null;
            }

            var parameters = source.Parameters.Cast<DbParameter>()
                .Select(parameter => ParameterSnapshot.Create(parameter, plan))
                .ToArray();

            using var connection = new NpgsqlConnection(connectionString);
            using var command = connection.CreateCommand();
            SetGeneratedCommandText(command, source.CommandText);
            foreach (var parameter in parameters)
            {
                command.Parameters.Add(parameter.Create(command));
            }

            var cacheKey = BuildCacheKey(command);
            if (cacheKey is null)
            {
                return null;
            }

            return new CommandSnapshot(connectionString, source.CommandText, parameters, cacheKey, plan.NextOffset);
        }
    }

    /// <summary>
    /// Buffered reader with Npgsql-compatible scalar conversion semantics.
    /// </summary>
    private sealed class NpgsqlCompatibleDataTableReader : DbDataReader
    {
        private readonly DataTableReader _inner;

        public NpgsqlCompatibleDataTableReader(DataTableReader inner)
        {
            _inner = inner;
        }

        public override int Depth => _inner.Depth;

        public override int FieldCount => _inner.FieldCount;

        public override bool HasRows => _inner.HasRows;

        public override bool IsClosed => _inner.IsClosed;

        public override int RecordsAffected => _inner.RecordsAffected;

        public override object this[int ordinal] => GetValue(ordinal);

        public override object this[string name] => GetValue(GetOrdinal(name));

        public override bool GetBoolean(int ordinal) => GetFieldValue<bool>(ordinal);

        public override byte GetByte(int ordinal) => GetFieldValue<byte>(ordinal);

        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
            => _inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);

        public override char GetChar(int ordinal) => GetFieldValue<char>(ordinal);

        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
            => _inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);

        public override string GetDataTypeName(int ordinal) => _inner.GetDataTypeName(ordinal);

        public override DateTime GetDateTime(int ordinal) => GetFieldValue<DateTime>(ordinal);

        public override decimal GetDecimal(int ordinal) => GetFieldValue<decimal>(ordinal);

        public override double GetDouble(int ordinal) => GetFieldValue<double>(ordinal);

        public override System.Collections.IEnumerator GetEnumerator() => ((System.Collections.IEnumerable)_inner).GetEnumerator();

        public override Type GetFieldType(int ordinal) => _inner.GetFieldType(ordinal);

        public override float GetFloat(int ordinal) => GetFieldValue<float>(ordinal);

        public override Guid GetGuid(int ordinal) => GetFieldValue<Guid>(ordinal);

        public override short GetInt16(int ordinal) => GetFieldValue<short>(ordinal);

        public override int GetInt32(int ordinal) => GetFieldValue<int>(ordinal);

        public override long GetInt64(int ordinal) => GetFieldValue<long>(ordinal);

        public override string GetName(int ordinal) => _inner.GetName(ordinal);

        public override int GetOrdinal(string name) => _inner.GetOrdinal(name);

        public override string GetString(int ordinal) => GetFieldValue<string>(ordinal);

        public override object GetValue(int ordinal) => _inner.GetValue(ordinal);

        public override int GetValues(object[] values) => _inner.GetValues(values);

        public override bool IsDBNull(int ordinal) => _inner.IsDBNull(ordinal);

        public override bool NextResult() => _inner.NextResult();

        public override bool Read() => _inner.Read();

        public override Task<bool> ReadAsync(CancellationToken cancellationToken) => _inner.ReadAsync(cancellationToken);

        public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => _inner.NextResultAsync(cancellationToken);

        public override System.IO.Stream GetStream(int ordinal) => _inner.GetStream(ordinal);

        public override System.IO.TextReader GetTextReader(int ordinal) => _inner.GetTextReader(ordinal);

        public override T GetFieldValue<T>(int ordinal)
        {
            var value = _inner.GetValue(ordinal);
            if (value is T typed)
            {
                return typed;
            }

            if (value is DBNull)
            {
                throw new InvalidCastException($"Column {ordinal} contains DBNull.");
            }

            var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            if (target == typeof(Guid))
            {
                return (T)(object)Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!);
            }

            if (target.IsEnum)
            {
                return (T)Enum.ToObject(target, value);
            }

            return (T)Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
        }

        public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken)
            => Task.FromResult(GetFieldValue<T>(ordinal));

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ParameterSnapshot
    {
        private ParameterSnapshot(DbParameter source, object? value)
        {
            Name = source.ParameterName;
            Value = value;
            DbType = source.DbType;
            Direction = source.Direction;
            Size = source.Size;
            Precision = source.Precision;
            Scale = source.Scale;
            IsNullable = source.IsNullable;
            NpgsqlDbType = source is NpgsqlParameter npgsql ? npgsql.NpgsqlDbType : null;
            DataTypeName = source is NpgsqlParameter typed ? typed.DataTypeName : null;
        }

        public string Name { get; }

        public object? Value { get; }

        public System.Data.DbType DbType { get; }

        public ParameterDirection Direction { get; }

        public int Size { get; }

        public byte Precision { get; }

        public byte Scale { get; }

        public bool IsNullable { get; }

        public NpgsqlTypes.NpgsqlDbType? NpgsqlDbType { get; }

        public string? DataTypeName { get; }

        public static ParameterSnapshot Create(DbParameter source, PagePrefetchPlan plan)
        {
            var isOffset = string.Equals(source.ParameterName, plan.OffsetParameterName, StringComparison.Ordinal)
                || string.Equals(source.ParameterName.TrimStart('@'), plan.OffsetParameterName.TrimStart('@'), StringComparison.Ordinal);
            return new ParameterSnapshot(source, isOffset ? plan.NextOffset : source.Value);
        }

        public DbParameter Create(DbCommand command)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = Name;
            parameter.Value = Value ?? DBNull.Value;
            parameter.DbType = DbType;
            parameter.Direction = Direction;
            parameter.Size = Size;
            parameter.Precision = Precision;
            parameter.Scale = Scale;
            parameter.IsNullable = IsNullable;
            if (parameter is NpgsqlParameter npgsql)
            {
                if (NpgsqlDbType.HasValue)
                {
                    npgsql.NpgsqlDbType = NpgsqlDbType.Value;
                }

                if (DataTypeName is not null)
                {
                    npgsql.DataTypeName = DataTypeName;
                }
            }

            return parameter;
        }
    }
}
