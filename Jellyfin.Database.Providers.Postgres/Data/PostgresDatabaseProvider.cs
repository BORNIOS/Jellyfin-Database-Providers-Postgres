using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using MediaBrowser.Common.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Jellyfin.Database.Providers.Postgres;

/// <summary>
/// Configures Jellyfin to use PostgreSQL through EF Core and provides
/// static optimization helpers shared across the plugin.
/// </summary>
public sealed class PostgresDatabaseProvider : IJellyfinDatabaseProvider
{
    // ── Fields: readonly first (SA1214), then mutable (SA1201: fields before constructor) ───
    // Hardcoded DDL index definitions — no user input (satisfies CA2100 and CA1861)
    private static readonly (string IdxName, string Table, string Col)[] GinIndexDefinitions =
    {
        ("ix_baseitems_name_gin_trgm",    "\"BaseItems\"", "\"Name\""),
        ("ix_baseitems_sortname_gin_trgm", "\"BaseItems\"", "\"SortName\""),
        ("ix_peoples_name_gin_trgm",       "\"Peoples\"",  "\"Name\""),
    };

    private static readonly string[] AutovacuumTables = { "BaseItems", "UserData", "ActivityLogs" };

    private static readonly int[] RetryDelays = { 30, 60, 120 };

    private readonly IApplicationPaths? _applicationPaths;
    private readonly ILogger<PostgresDatabaseProvider> _logger;

    // Mutable static field (after readonly fields — SA1214)
    private static bool _trgmAvailable;

    // ── Constructor ───────────────────────────────────────────────────────────

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresDatabaseProvider"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths (may be null at design time).</param>
    /// <param name="logger">Logger instance.</param>
    public PostgresDatabaseProvider(
        IApplicationPaths? applicationPaths,
        ILogger<PostgresDatabaseProvider>? logger)
    {
        _applicationPaths = applicationPaths;
        _logger = logger ?? NullLogger<PostgresDatabaseProvider>.Instance;
    }

    // ── Properties (SA1201: after constructor) ────────────────────────────────

    /// <summary>
    /// Gets a value indicating whether the <c>pg_trgm</c> extension is available on the server.
    /// Set during <see cref="RunOptimizationsAsync"/> after the first successful probe.
    /// </summary>
    internal static bool TrgmAvailable => _trgmAvailable;

    /// <inheritdoc/>
    public IDbContextFactory<JellyfinDbContext>? DbContextFactory { get; set; }

    // ── IJellyfinDatabaseProvider ─────────────────────────────────────────────

    /// <inheritdoc/>
    public void Initialise(DbContextOptionsBuilder options, DatabaseConfigurationOptions databaseConfiguration)
    {
        var connStr = (_applicationPaths is not null
                ? PostgresPlugin.ReadActivePgConnectionString(_applicationPaths)
                : null)
            ?? PostgresPlugin.Instance?.Configuration?.ConnectionString;

        Logging.PostgresLog.Warn($"[Provider] Initialise llamado. ConnectionString resuelto: {(string.IsNullOrWhiteSpace(connStr) ? "(vacío)" : "OK")}");

        if (string.IsNullOrWhiteSpace(connStr))
        {
            throw new InvalidOperationException(
                "PostgreSQL connection string is not configured. " +
                "Configure it through the plugin settings before activating the PostgreSQL provider.");
        }

        options.UseNpgsql(
            connStr,
            npgsql => npgsql.MigrationsAssembly(typeof(PostgresDatabaseProvider).Assembly.GetName().Name!));
    }

    /// <inheritdoc/>
    public void OnModelCreating(ModelBuilder modelBuilder)
    {
    }

    /// <inheritdoc/>
    public void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
    }

    /// <inheritdoc/>
    public async Task RunScheduledOptimisation(CancellationToken cancellationToken)
    {
        var connStr = PostgresPlugin.Instance?.Configuration?.ConnectionString;
        if (string.IsNullOrWhiteSpace(connStr))
        {
            return;
        }

        await RunOptimizationsAsync(connStr, enableSearch: true, enableVacuum: true, _logger, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task RunShutdownTask(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task<string> MigrationBackupFast(CancellationToken cancellationToken)
        => throw new NotImplementedException("Use the plugin backup task instead.");

    /// <inheritdoc/>
    public Task RestoreBackupFast(string key, CancellationToken cancellationToken)
        => throw new NotImplementedException("Use the plugin restore task instead.");

    /// <inheritdoc/>
    public Task PurgeDatabase(JellyfinDbContext dbContext, IEnumerable<string>? tableNames)
        => throw new NotImplementedException();

    /// <inheritdoc/>
    public Task DeleteBackup(string key) => throw new NotImplementedException();

    // ── Internal optimization entry point ────────────────────────────────────

    /// <summary>
    /// Creates pg_trgm GIN indexes for near-instant search and tunes autovacuum on hot tables.
    /// All statements are idempotent (<c>CREATE INDEX CONCURRENTLY IF NOT EXISTS</c>).
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    /// <param name="enableSearch">Whether to create GIN trigram indexes for instant search.</param>
    /// <param name="enableVacuum">Whether to apply aggressive autovacuum settings on hot tables.</param>
    /// <param name="logger">Logger for progress and error reporting.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    internal static async Task RunOptimizationsAsync(
        string connectionString,
        bool enableSearch,
        bool enableVacuum,
        ILogger logger,
        CancellationToken ct)
    {
        using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        _trgmAvailable = await CheckTrgmAsync(conn, logger, ct).ConfigureAwait(false);

        if (enableSearch && _trgmAvailable)
        {
            await ApplySearchIndexesAsync(conn, logger, ct).ConfigureAwait(false);
        }

        if (enableVacuum)
        {
            await ApplyAutovacuumTuningAsync(conn, logger, ct).ConfigureAwait(false);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static async Task<bool> CheckTrgmAsync(NpgsqlConnection conn, ILogger logger, CancellationToken ct)
    {
        // Hardcoded SQL literal — no user input (CA2100)
        const string sql = "SELECT COUNT(*) FROM pg_extension WHERE extname = 'pg_trgm';";
        try
        {
            using var cmd = new NpgsqlCommand(sql, conn);
            var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            var available = Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture) > 0;
            if (!available)
            {
                logger.LogWarning("pg_trgm extension not found. Run: CREATE EXTENSION pg_trgm;");
            }

            return available;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not probe pg_trgm availability.");
            return false;
        }
    }

    private static async Task ApplySearchIndexesAsync(NpgsqlConnection conn, ILogger logger, CancellationToken ct)
    {
        foreach (var (idxName, table, col) in GinIndexDefinitions)
        {
            // SQL built from GinIndexDefinitions — hardcoded, no user input (CA2100)
            var sql = $"CREATE INDEX CONCURRENTLY IF NOT EXISTS {idxName} ON {table} USING gin({col} gin_trgm_ops);";
            await TryExecuteIndexWithRetryAsync(sql, idxName, conn, logger, ct).ConfigureAwait(false);
        }
    }

    private static async Task ApplyAutovacuumTuningAsync(NpgsqlConnection conn, ILogger logger, CancellationToken ct)
    {
        // AutovacuumTables is a hardcoded static array — not user input (CA2100).
        foreach (var table in AutovacuumTables)
        {
            try
            {
                using var cmd = CreateAutovacuumCommand(conn, table);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not apply autovacuum tuning for {Table}.", table);
            }
        }
    }

    private static async Task TryExecuteIndexWithRetryAsync(
        string sql,
        string indexName,
        NpgsqlConnection connection,
        ILogger logger,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            try
            {
                // sql comes from GinIndexDefinitions — hardcoded static array (CA2100).
                using var cmd = CreateIndexCommand(connection, sql);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                logger.LogInformation("Index applied: {IndexName}", indexName);
                return;
            }
            catch (PostgresException pgEx) when (pgEx.SqlState == "40P01" && attempt < RetryDelays.Length)
            {
                var delay = RetryDelays[attempt];
                logger.LogWarning(
                    "Deadlock creating {IndexName} (attempt {Attempt}). Retrying in {Delay}s.",
                    indexName,
                    attempt + 1,
                    delay);
                await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not create index {IndexName}. Skipping.", indexName);
                return;
            }
        }
    }

    // ── Command factory methods (CA2100) ──────────────────────────────────────

    // table comes from AutovacuumTables (private static readonly array), not user input.
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "table comes from AutovacuumTables private static readonly array, not user input. Identifier is quoted.")]
    private static NpgsqlCommand CreateAutovacuumCommand(NpgsqlConnection conn, string table)
    {
        var sql = string.Concat(
            "ALTER TABLE \"",
            table.Replace("\"", "\"\"", StringComparison.Ordinal),
            "\" SET (autovacuum_vacuum_scale_factor = 0.01, autovacuum_analyze_scale_factor = 0.005);");
        return new NpgsqlCommand(sql, conn);
    }

    // sql comes from GinIndexDefinitions (private static readonly array), not user input.
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "sql comes from GinIndexDefinitions private static readonly array, not user input.")]
    private static NpgsqlCommand CreateIndexCommand(NpgsqlConnection conn, string sql)
        => new NpgsqlCommand(sql, conn) { CommandTimeout = 0 };
}
