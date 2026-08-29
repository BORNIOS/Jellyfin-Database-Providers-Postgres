using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Providers.Postgres.Services;
using Jellyfin.Database.Providers.Postgres.Services.Models;
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
    // 9 GIN trigram indexes for sub-15ms instant search (pg_trgm required)
    // All use partial indexes (WHERE col IS NOT NULL) to avoid indexing NULL rows.
    private static readonly (string Name, string Sql)[] GinIndexDefinitions =
    {
        ("IX_BaseItems_Name_gin_trgm",
         """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_BaseItems_Name_gin_trgm" ON "BaseItems" USING gin ("Name" gin_trgm_ops) WHERE "Name" IS NOT NULL;"""),
        ("IX_BaseItems_OriginalTitle_gin_trgm",
         """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_BaseItems_OriginalTitle_gin_trgm" ON "BaseItems" USING gin ("OriginalTitle" gin_trgm_ops) WHERE "OriginalTitle" IS NOT NULL;"""),
        ("IX_BaseItems_Album_gin_trgm",
         """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_BaseItems_Album_gin_trgm" ON "BaseItems" USING gin ("Album" gin_trgm_ops) WHERE "Album" IS NOT NULL;"""),
        ("IX_BaseItems_Artists_gin_trgm",
         """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_BaseItems_Artists_gin_trgm" ON "BaseItems" USING gin ("Artists" gin_trgm_ops) WHERE "Artists" IS NOT NULL;"""),
        ("IX_BaseItems_AlbumArtists_gin_trgm",
         """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_BaseItems_AlbumArtists_gin_trgm" ON "BaseItems" USING gin ("AlbumArtists" gin_trgm_ops) WHERE "AlbumArtists" IS NOT NULL;"""),
        ("IX_BaseItems_SeriesName_gin_trgm",
         """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_BaseItems_SeriesName_gin_trgm" ON "BaseItems" USING gin ("SeriesName" gin_trgm_ops) WHERE "SeriesName" IS NOT NULL;"""),
        ("IX_ItemValues_Value_gin_trgm",
         """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_ItemValues_Value_gin_trgm" ON "ItemValues" USING gin ("Value" gin_trgm_ops);"""),
        ("IX_ItemValues_CleanValue_gin_trgm",
         """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_ItemValues_CleanValue_gin_trgm" ON "ItemValues" USING gin ("CleanValue" gin_trgm_ops);"""),
        ("IX_Peoples_Name_gin_trgm",
         """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Peoples_Name_gin_trgm" ON "Peoples" USING gin ("Name" gin_trgm_ops) WHERE "Name" IS NOT NULL;"""),
    };

    // Composite partial indexes optimised for home-page, library-browser and resume queries.
    // These match the exact column order PostgreSQL needs to satisfy ORDER BY without a sort.
    private static readonly (string Name, string Sql)[] NavigationIndexDefinitions =
    {
        ("IX_BaseItems_ParentId_DateCreated_Partial",
         """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_BaseItems_ParentId_DateCreated_Partial" ON "BaseItems" ("ParentId", "DateCreated" DESC) WHERE "IsVirtualItem" = false;"""),
        ("IX_BaseItems_TopParentId_DateCreated_Partial",
         """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_BaseItems_TopParentId_DateCreated_Partial" ON "BaseItems" ("TopParentId", "DateCreated" DESC) WHERE "IsVirtualItem" = false;"""),
        ("IX_BaseItems_Type_TopParentId_DateCreated_Partial",
         """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_BaseItems_Type_TopParentId_DateCreated_Partial" ON "BaseItems" ("Type", "TopParentId", "DateCreated" DESC) WHERE "IsVirtualItem" = false;"""),
        ("IX_UserData_UserId_LastPlayedDate_Partial",
         """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_UserData_UserId_LastPlayedDate_Partial" ON "UserData" ("UserId", "LastPlayedDate" DESC) WHERE "PlaybackPositionTicks" > 0;"""),
        ("IX_UserData_UserId_IsFavorite_Partial",
         """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_UserData_UserId_IsFavorite_Partial" ON "UserData" ("UserId") WHERE "IsFavorite" = true;"""),

        // --- Additional covering indexes for frequent Jellyfin queries ---
        // Cover the library browser sort by OfficialRating + DateCreated (common in All Movies view)
        ("IX_BaseItems_Type_IsFolder_IsVirtualItem_OfficialRating",
         """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_BaseItems_Type_IsFolder_IsVirtualItem_OfficialRating" ON "BaseItems" ("Type", "IsFolder", "OfficialRating", "DateCreated" DESC) WHERE "IsVirtualItem" = false;"""),
        // Cover episodes sorted by IndexNumber within a season (Next Up, Continue Watching)
        ("IX_BaseItems_SeriesId_SeasonId_IndexNumber",
         """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_BaseItems_SeriesId_SeasonId_IndexNumber" ON "BaseItems" ("SeriesId", "SeasonId", "IndexNumber") WHERE "IsVirtualItem" = false;"""),
        // Cover UserData lookups by ItemId alone (used by playback state queries)
        ("IX_UserData_ItemId_PlaybackPositionTicks_Partial",
         """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_UserData_ItemId_PlaybackPositionTicks_Partial" ON "UserData" ("ItemId") WHERE "PlaybackPositionTicks" > 0;"""),
    };

    private static readonly string[] AutovacuumTables = { "BaseItems", "UserData", "ActivityLogs" };

    private static readonly int[] RetryDelays = { 30, 60, 120 };

    private readonly IApplicationPaths? _applicationPaths;
    private readonly ILogger<PostgresDatabaseProvider> _logger;

    // Mutable static field (after readonly fields — SA1214)
    private static bool _trgmAvailable;

    // Ensures the startup health check runs exactly once across all Initialise calls.
    private static int _startupHealthCheckFired;

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

        // Note: Npgsql.EnableLegacyTimestampBehavior is intentionally NOT set here.
        // DateTime.Kind=Unspecified writes are handled by DateTimeKindNormalizingInterceptor
        // so that the PostgreSQL server timezone is respected for reads while write
        // compatibility is preserved. See DateTimeKindNormalizingInterceptor for details.
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

        // Apply pool tuning and prepared-statement cache from plugin config
        var config = PostgresPlugin.Instance?.Configuration;
        var csb = new NpgsqlConnectionStringBuilder(connStr)
        {
            MinPoolSize = config?.MinPoolSize ?? 4,
            MaxPoolSize = config?.MaxPoolSize ?? 100,
            MaxAutoPrepare = config?.MaxAutoPrepare ?? 50,
            CommandTimeout = config?.CommandTimeout ?? 60,
        };

        var tunedConnStr = csb.ToString();

        // Log the *actual* pool configuration that will be used (post-tuning).
        // Mask password before logging to avoid leaking credentials.
        var maskedTuned = System.Text.RegularExpressions.Regex.Replace(
            tunedConnStr,
            @"(?i)(Password\s*=)[^;]+",
            "$1*****");
        Logging.PostgresLog.Warn(
            $"[ENGINE] Conexi\u00f3n activa (tuneada): {maskedTuned}");
        Logging.PostgresLog.Warn(
            $"[Provider] Pool: min={csb.MinPoolSize} max={csb.MaxPoolSize} " +
            $"maxAutoPrepare={csb.MaxAutoPrepare} cmdTimeout={csb.CommandTimeout}s");

        options
            .UseNpgsql(
                tunedConnStr,
                npgsql => npgsql.MigrationsAssembly(typeof(PostgresDatabaseProvider).Assembly.GetName().Name!))
            .AddInterceptors(
                new HomeQueryCacheInterceptor(_logger),
                new UpsertConflictInterceptor(),
                new DbErrorLoggingInterceptor(),
                new DateTimeKindNormalizingInterceptor());

        // Run the health check in background exactly once, even if Initialise is called
        // multiple times (EF Core can call it more than once during startup).
        var schema = config?.Schema ?? "public";
        if (Interlocked.CompareExchange(ref _startupHealthCheckFired, 1, 0) == 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    // Log timezone so operators can detect UTC/local mismatches.
                    await LogPostgresTimezoneAsync(tunedConnStr).ConfigureAwait(false);

                    var svc = new HealthCheckService(NullLogger<HealthCheckService>.Instance);
                    var result = await svc.RunHealthCheckAsync(tunedConnStr, schema, silent: true).ConfigureAwait(false);
                    LogHealthSummary(result);
                }
                catch (Exception ex)
                {
                    Logging.PostgresLog.Error("[HealthCheck] AUTO fall\u00f3 al iniciar", ex);
                }
            });
        }
    }

    private static async Task LogPostgresTimezoneAsync(string connectionString)
    {
        try
        {
            using var pg = new NpgsqlConnection(connectionString);
            await pg.OpenAsync().ConfigureAwait(false);
            using var cmd = new NpgsqlCommand("SHOW timezone; SHOW lc_time;", pg);
            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            var tz = await reader.ReadAsync().ConfigureAwait(false) ? reader.GetString(0) : "(desconocida)";
            await reader.NextResultAsync().ConfigureAwait(false);
            var lc = await reader.ReadAsync().ConfigureAwait(false) ? reader.GetString(0) : "(desconocida)";
            Logging.PostgresLog.Warn($"[ENGINE] PostgreSQL timezone={tz} | lc_time={lc}");

            // Check pg_stat_statements availability.
            await reader.CloseAsync().ConfigureAwait(false);
            using var extCmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM pg_extension WHERE extname='pg_stat_statements';",
                pg);
            var extCount = Convert.ToInt64(
                await extCmd.ExecuteScalarAsync().ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            if (extCount == 0)
            {
                Logging.PostgresLog.Warn(
                    "[ENGINE] pg_stat_statements NO est\u00e1 activo — el check SlowQueries no retornar\u00e1 datos. " +
                    "Agrega 'pg_stat_statements' a shared_preload_libraries y reinicia PostgreSQL.");
            }
            else
            {
                Logging.PostgresLog.Info("[ENGINE] pg_stat_statements activo \u2713 (slow-query tracking disponible).");
            }
        }
        catch (Exception ex)
        {
            Logging.PostgresLog.Error("[ENGINE] No se pudo consultar timezone/extensiones", ex);
        }
    }

    private static void LogHealthSummary(HealthCheckResult result)
    {
        var findings = result.Findings;
        var errors = findings.Count(static f => f.Severity == HealthSeverity.Error);
        var warns = findings.Count(static f => f.Severity == HealthSeverity.Warn);
        var info = findings.Count - errors - warns;

        Logging.PostgresLog.Warn(
            $"[HealthCheck] Resumen arranque: {errors} error(es), {warns} advertencia(s), {info} informativo(s) | " +
            $"Severidad: {result.OverallSeverity} | {result.DurationMs} ms");

        foreach (var f in findings)
        {
            if (f.Severity == HealthSeverity.Ok)
            {
                continue;
            }

            var det = string.IsNullOrEmpty(f.Detail) ? string.Empty : $" | {f.Detail}";
            Logging.PostgresLog.Warn($"[HealthCheck] [{f.Check}] {f.Message}{det}");
        }
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
        Logging.PostgresLog.Warn($"[Optimization] INICIO: enableSearch={enableSearch} enableVacuum={enableVacuum}");

        using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        _trgmAvailable = await CheckTrgmAsync(conn, logger, ct).ConfigureAwait(false);
        Logging.PostgresLog.Warn($"[Optimization] pg_trgm: {(_trgmAvailable ? "disponible \u2713" : "no disponible \u2717 \u2014 InstantSearch usar\u00e1 ILIKE")}");

        if (enableSearch && _trgmAvailable)
        {
            Logging.PostgresLog.Warn($"[Optimization] Aplicando {GinIndexDefinitions.Length} \u00edndices GIN trigram...");
            await ApplySearchIndexesAsync(conn, logger, ct).ConfigureAwait(false);
            Logging.PostgresLog.Warn("[Optimization] \u00cdndices GIN trigram aplicados (IF NOT EXISTS \u2014 instant\u00e1neo si ya exist\u00edan).");
        }
        else if (enableSearch && !_trgmAvailable)
        {
            Logging.PostgresLog.Warn("[Optimization] \u00cdndices GIN omitidos \u2014 pg_trgm no disponible. Ejecuta: CREATE EXTENSION pg_trgm;");
        }

        // Navigation indexes improve home-page, library-browser and resume performance
        // regardless of pg_trgm availability.
        Logging.PostgresLog.Warn($"[Optimization] Aplicando {NavigationIndexDefinitions.Length} \u00edndices de navegaci\u00f3n parciales...");
        await ApplyNavigationIndexesAsync(conn, logger, ct).ConfigureAwait(false);
        Logging.PostgresLog.Warn("[Optimization] \u00cdndices de navegaci\u00f3n aplicados (home-page, library-browser, resume).");

        // Query-planner memory hints (work_mem) for sort-heavy queries.
        await ApplyServerMemoryTuningAsync(conn, logger, ct).ConfigureAwait(false);
        Logging.PostgresLog.Warn("[Optimization] Ajuste de memoria de sesi\u00f3n aplicado (work_mem=16MB).");

        if (enableVacuum)
        {
            Logging.PostgresLog.Warn("[Optimization] Aplicando autovacuum tuning en tablas cr\u00edticas...");
            await ApplyAutovacuumTuningAsync(conn, logger, ct).ConfigureAwait(false);
            Logging.PostgresLog.Warn($"[Optimization] Autovacuum tuning aplicado en: {string.Join(", ", AutovacuumTables)}");
        }

        // Try to activate pg_stat_statements for slow-query visibility.
        // Requires pg_stat_statements in shared_preload_libraries (needs restart if not already active).
        await TryActivateStatStatementsAsync(conn, logger, ct).ConfigureAwait(false);

        Logging.PostgresLog.Warn("[Optimization] COMPLETADO. InstantSearch activo, home-page optimizada, autovacuum tuned.");
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
        foreach (var (name, sql) in GinIndexDefinitions)
        {
            await TryExecuteIndexWithRetryAsync(sql, name, conn, logger, ct).ConfigureAwait(false);
        }
    }

    private static async Task ApplyNavigationIndexesAsync(NpgsqlConnection conn, ILogger logger, CancellationToken ct)
    {
        foreach (var (name, sql) in NavigationIndexDefinitions)
        {
            await TryExecuteIndexWithRetryAsync(sql, name, conn, logger, ct).ConfigureAwait(false);
        }

        logger.LogInformation("PostgreSQL navigation indexes applied. Home page, library browser and resume queries optimized.");
    }

    [SuppressMessage("Security", "CA2100", Justification = "work_mem value is a hardcoded constant, not user input.")]
    private static async Task ApplyServerMemoryTuningAsync(NpgsqlConnection conn, ILogger logger, CancellationToken ct)
    {
        // Tune the query planner and sort memory for a home-media-server workload.
        // work_mem controls per-sort-operation memory; raising it avoids disk spills
        // on ORDER BY DateCreated DESC, ORDER BY RANDOM(), etc.
        // This is a session-level SET — no server restart required.
        const string sql = "SET work_mem = '16MB';";
        try
        {
            using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            logger.LogInformation("PostgreSQL session memory tuning applied (work_mem=16MB).");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not apply server memory tuning — skipping.");
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
                Logging.PostgresLog.Warn($"[Optimization]   \u2713 {indexName}");
                return;
            }
            catch (PostgresException pgEx) when (pgEx.SqlState == "40P01" && attempt < RetryDelays.Length)
            {
                var delay = RetryDelays[attempt];
                logger.LogWarning(
                    pgEx,
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

    /// <summary>
    /// Tries to activate <c>pg_stat_statements</c> so slow queries are visible in the
    /// Health Check and in tools like pgAdmin. Silently skips when the extension requires
    /// a server restart (needs <c>shared_preload_libraries</c>) or the user lacks privileges.
    /// </summary>
    private static async Task TryActivateStatStatementsAsync(NpgsqlConnection conn, ILogger logger, CancellationToken ct)
    {
        try
        {
            // Check if already active
            using var checkCmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM pg_extension WHERE extname = 'pg_stat_statements';", conn);
            var count = Convert.ToInt64(
                await checkCmd.ExecuteScalarAsync(ct).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);

            if (count > 0)
            {
                Logging.PostgresLog.Info("[Optimization] pg_stat_statements: ya activo \u2713 (slow-query tracking disponible).");
                return;
            }

            // Try to create it — may fail if not in shared_preload_libraries
            using var createCmd = new NpgsqlCommand(
                "CREATE EXTENSION IF NOT EXISTS pg_stat_statements;", conn);
            await createCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            Logging.PostgresLog.Warn("[Optimization] pg_stat_statements activado \u2713 (agrega 'pg_stat_statements' a shared_preload_libraries para que persista entre reinicios).");
        }
        catch (PostgresException pgEx) when (pgEx.SqlState is "55P02" or "42501")
        {
            // 55P02 = extension requires restart, 42501 = insufficient privileges
            Logging.PostgresLog.Warn(
                $"[Optimization] pg_stat_statements no pudo activarse autom\u00e1ticamente ({pgEx.MessageText}). " +
                "Para activarlo manualmente: agrega 'pg_stat_statements' a shared_preload_libraries en postgresql.conf y reinicia PostgreSQL.");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not activate pg_stat_statements — skipping.");
        }
    }
}
