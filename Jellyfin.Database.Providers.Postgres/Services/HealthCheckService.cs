using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Providers.Postgres.Logging;
using Jellyfin.Database.Providers.Postgres.Services.Models;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// PostgreSQL health diagnostics: runs a battery of read-only checks against
/// pg_catalog / pg_stat views and reports structured findings with a severity
/// semaphore. Also supports auto-repair of issues that are safely fixable
/// at runtime (invalid indexes, table bloat, out-of-sync sequences).
/// </summary>
public sealed partial class HealthCheckService
{
    /// <summary>Bloat ratio (dead tuples over live+dead) above which a table is flagged.</summary>
    private const double BloatWarnRatio = 0.20;

    /// <summary>Tables are only flagged for stale statistics when they contain at least this many rows.</summary>
    private const long AnalyzeMinLiveTuples = 100;

    /// <summary>
    /// PostgreSQL internal schemas where only a superuser can DROP indexes.
    /// Orphan indexes in these schemas are reported as Warn with a superuser hint.
    /// </summary>
    private static readonly HashSet<string> SuperuserSchemas =
        new(StringComparer.OrdinalIgnoreCase) { "pg_toast", "pg_catalog", "information_schema" };

    private readonly ILogger<HealthCheckService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HealthCheckService"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public HealthCheckService(ILogger<HealthCheckService> logger) => _logger = logger;

    /// <summary>
    /// Runs all health checks in parallel and aggregates the findings.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    /// <param name="schema">Schema to inspect. Defaults to <c>public</c>.</param>
    /// <param name="silent">
    /// When <see langword="true"/>, suppresses the INICIO/COMPLETADO log lines.
    /// Use for the automatic startup check to avoid duplicating the summary written
    /// by <c>LogHealthSummary</c> in <c>PostgresDatabaseProvider</c>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Aggregated <see cref="HealthCheckResult"/>.</returns>
    public async Task<HealthCheckResult> RunHealthCheckAsync(
        string connectionString,
        string schema = "public",
        bool silent = false,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (!silent)
        {
            PostgresLog.Info($"[HealthCheck] INICIO: diagnóstico en schema '{schema}'...");
        }

        // Each check runs in parallel and is wrapped so a single failure
        // (e.g. an unsupported catalog view) never aborts the whole report.
        var invalidIndexesTask = CaptureCheckAsync("InvalidIndexes", CheckInvalidIndexesAsync(connectionString, ct));
        var bloatTask = CaptureCheckAsync("TableBloat", CheckTableBloatAsync(connectionString, schema, ct));
        var sequencesTask = CaptureCheckAsync("SequenceDesync", CheckSequenceSyncAsync(connectionString, schema, ct));
        var analyzeTask = CaptureCheckAsync("StaleAnalyze", CheckStaleAnalyzeAsync(connectionString, schema, ct));
        var unusedIndexesTask = CaptureCheckAsync("UnusedIndex", CheckUnusedIndexesAsync(connectionString, schema, ct));
        var slowQueriesTask = CaptureCheckAsync("SlowQueries", CheckSlowQueriesAsync(connectionString, ct));

        // Tasks are already running in parallel; awaiting each one lets
        // CaptureCheckAsync turn any individual failure into a finding.
        var findings = new List<HealthFinding>(64);
        findings.AddRange(await invalidIndexesTask.ConfigureAwait(false));
        findings.AddRange(await bloatTask.ConfigureAwait(false));
        findings.AddRange(await sequencesTask.ConfigureAwait(false));
        findings.AddRange(await analyzeTask.ConfigureAwait(false));
        findings.AddRange(await unusedIndexesTask.ConfigureAwait(false));
        findings.AddRange(await slowQueriesTask.ConfigureAwait(false));

        var overall = WorstSeverity(findings);
        sw.Stop();

        if (!silent)
        {
            LogFindings(findings);
            PostgresLog.Warn(
                $"[HealthCheck] COMPLETADO en {sw.Elapsed.TotalSeconds:F1}s | Severidad: {overall} | " +
                $"Hallazgos: {findings.Count} | Schema: {schema}");
        }

        _logger.LogInformation(
            "Health check completed: {Severity} ({Count} findings) in {Ms} ms",
            overall,
            findings.Count,
            sw.ElapsedMilliseconds);

        return new HealthCheckResult(overall, findings, DateTime.UtcNow, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Auto-repairs the issues detected by <see cref="RunHealthCheckAsync"/>.
    /// Re-runs detection internally so it is idempotent and never trusts client-supplied
    /// identifiers. Executes: REINDEX CONCURRENTLY for invalid indexes, VACUUM ANALYZE
    /// for bloated tables, and setval for out-of-sync sequences.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    /// <param name="schema">Schema to inspect. Defaults to <c>public</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Aggregated <see cref="HealthRepairResult"/>.</returns>
    public async Task<HealthRepairResult> RepairAsync(
        string connectionString,
        string schema = "public",
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var items = new List<HealthRepairItem>();

        PostgresLog.Warn($"[HealthCheck] INICIO: Auto-repair en schema '{schema}'...");

        // Re-detect across all schemas so we never try to REINDEX an orphan in pg_toast.
        var invalidIndexes = await FindInvalidIndexesAsync(connectionString, ct).ConfigureAwait(false);
        var ccnewSkipped = new List<string>();
        foreach (var (idxSchema, table, index) in invalidIndexes)
        {
            var target = $"{idxSchema}.{table}.{index}";
            var isCcnew = index.EndsWith("_ccnew", StringComparison.OrdinalIgnoreCase)
                       || index.Contains("_ccnew_", StringComparison.OrdinalIgnoreCase);

            if (isCcnew || SuperuserSchemas.Contains(idxSchema))
            {
                ccnewSkipped.Add(target);
                var hint = SuperuserSchemas.Contains(idxSchema)
                    ? "Requiere superusuario (postgres): DROP INDEX pg_toast.\"" + index + "\";"
                    : "Ejecuta DROP INDEX para eliminarlo manualmente.";
                items.Add(new HealthRepairItem("SkipCcnew", target, false, hint));
                continue;
            }

            items.Add(await RepairIndexAsync(connectionString, idxSchema, table, index, ct).ConfigureAwait(false));
        }

        if (ccnewSkipped.Count > 0)
        {
            PostgresLog.Warn(
                $"[HealthCheck] SKIP {ccnewSkipped.Count} índice(s) — requieren intervención manual: " +
                string.Join(", ", ccnewSkipped));
        }

        var bloatedTables = await FindBloatedTablesAsync(connectionString, schema, ct).ConfigureAwait(false);
        foreach (var (table, deadPct) in bloatedTables)
        {
            items.Add(await RepairBloatAsync(connectionString, schema, table, deadPct, ct).ConfigureAwait(false));
        }

        var staleSequences = await FindSequenceIssuesAsync(connectionString, schema, ct).ConfigureAwait(false);
        foreach (var seq in staleSequences)
        {
            items.Add(await RepairSequenceAsync(connectionString, schema, seq, ct).ConfigureAwait(false));
        }

        var overallSuccess = items.All(static i => i.Success);
        sw.Stop();

        PostgresLog.Warn(
            $"[HealthCheck] COMPLETADO en {sw.Elapsed.TotalSeconds:F1}s: " +
            $"{items.Count} acciones ({items.Count(static i => i.Success)} ok, {items.Count(static i => !i.Success)} fallaron).");

        return new HealthRepairResult(overallSuccess, items, DateTime.UtcNow, sw.ElapsedMilliseconds);
    }

    // ── Check 1: invalid indexes ──────────────────────────────────────────────

    /// <summary>
    /// Detects indexes marked as invalid across ALL schemas. Separates:
    /// <list type="bullet">
    /// <item><c>_ccnew</c> orphans in system schemas — require superuser DROP INDEX</item>
    /// <item><c>_ccnew</c> orphans in user schemas — require manual DROP INDEX</item>
    /// <item>Ordinary invalid indexes — auto-repairable with REINDEX CONCURRENTLY</item>
    /// </list>
    /// </summary>
    private static async Task<IReadOnlyList<HealthFinding>> CheckInvalidIndexesAsync(
        string connectionString, CancellationToken ct)
    {
        // Search all schemas so pg_toast orphans are also visible.
        var invalid = await FindInvalidIndexesAsync(connectionString, ct).ConfigureAwait(false);
        if (invalid.Count == 0)
        {
            return Array.Empty<HealthFinding>();
        }

        static bool IsCcnew(string idx) =>
            idx.EndsWith("_ccnew", StringComparison.OrdinalIgnoreCase) ||
            idx.Contains("_ccnew_", StringComparison.OrdinalIgnoreCase);

        var ccnewSuperuser = invalid.Where(p => SuperuserSchemas.Contains(p.Schema) && IsCcnew(p.Index)).ToList();
        var ccnewUser = invalid.Where(p => !SuperuserSchemas.Contains(p.Schema) && IsCcnew(p.Index)).ToList();
        var repairable = invalid.Except(ccnewSuperuser).Except(ccnewUser).ToList();

        var findings = new List<HealthFinding>(3);

        if (ccnewSuperuser.Count > 0)
        {
            var names = string.Join(", ", ccnewSuperuser.Select(static p => $"{p.Schema}.{p.Table}.{p.Index}"));
            findings.Add(new HealthFinding(
                Check: "OrphanCcnewIndexesSuperuser",
                Severity: HealthSeverity.Warn,
                Message: $"{ccnewSuperuser.Count} índice(s) _ccnew huérfano(s) en schemas del sistema ({string.Join(", ", ccnewSuperuser.Select(static p => p.Schema).Distinct())}). "
                       + "El usuario de la BD no tiene permisos para eliminarlos. "
                       + "Conectáte como superusuario (postgres) y ejecuta DROP INDEX para cada uno.",
                Detail: names,
                Repairable: false));
        }

        if (ccnewUser.Count > 0)
        {
            var names = string.Join(", ", ccnewUser.Select(static p => $"{p.Table}.{p.Index}"));
            findings.Add(new HealthFinding(
                Check: "OrphanCcnewIndexes",
                Severity: HealthSeverity.Error,
                Message: $"{ccnewUser.Count} índice(s) _ccnew huérfano(s) de un REINDEX CONCURRENTLY interrumpido. "
                       + "No pueden repararse automáticamente — ejecuta DROP INDEX para cada uno.",
                Detail: names,
                Repairable: false));
        }

        if (repairable.Count > 0)
        {
            var names = string.Join(", ", repairable.Select(static p => $"{p.Table}.{p.Index}"));
            findings.Add(new HealthFinding(
                Check: "InvalidIndexes",
                Severity: HealthSeverity.Error,
                Message: $"{repairable.Count} índice(s) inválido(s) encontrado(s). Los queries que los usan fallarán o serán muy lentos.",
                Detail: names,
                Repairable: true));
        }

        return findings;
    }

    // ── Check 2: table bloat ──────────────────────────────────────────────────

    /// <summary>Detects tables whose dead-tuple ratio exceeds the warn threshold.</summary>
    private static async Task<IReadOnlyList<HealthFinding>> CheckTableBloatAsync(
        string connectionString, string schema, CancellationToken ct)
    {
        var bloated = await FindBloatedTablesAsync(connectionString, schema, ct).ConfigureAwait(false);
        if (bloated.Count == 0)
        {
            return Array.Empty<HealthFinding>();
        }

        var findings = new List<HealthFinding>(bloated.Count);
        foreach (var (table, deadPct) in bloated)
        {
            findings.Add(new HealthFinding(
                Check: "TableBloat",
                Severity: HealthSeverity.Warn,
                Message: $"'{table}' tiene {deadPct.ToString("F1", CultureInfo.InvariantCulture)}% de tuplas muertas. " +
                         "Un VACUUM ANALYZE recuperará espacio y refrescará el planner.",
                Detail: $"{table} ({deadPct.ToString("F1", CultureInfo.InvariantCulture)}%)",
                Repairable: true));
        }

        return findings;
    }

    // ── Check 3: sequence sync ────────────────────────────────────────────────

    /// <summary>Detects identity sequences whose <c>last_value</c> trails the max column value.</summary>
    private static async Task<IReadOnlyList<HealthFinding>> CheckSequenceSyncAsync(
        string connectionString, string schema, CancellationToken ct)
    {
        var issues = await FindSequenceIssuesAsync(connectionString, schema, ct).ConfigureAwait(false);
        if (issues.Count == 0)
        {
            return Array.Empty<HealthFinding>();
        }

        var findings = new List<HealthFinding>(issues.Count);
        foreach (var seq in issues)
        {
            findings.Add(new HealthFinding(
                Check: "SequenceDesync",
                Severity: HealthSeverity.Error,
                Message: $"Secuencia '{seq.Sequence}' está detrás del máximo de {seq.Table}.{seq.Column} " +
                         $"(last_value={seq.LastValue:N0}, max={seq.MaxValue:N0}). El próximo INSERT puede fallar con PK duplicado.",
                Detail: $"{seq.Sequence}",
                Repairable: true));
        }

        return findings;
    }

    // ── Check 4: stale analyze stats ──────────────────────────────────────────

    /// <summary>Detects populated tables without a recent ANALYZE (7+ days).</summary>
    private static async Task<IReadOnlyList<HealthFinding>> CheckStaleAnalyzeAsync(
        string connectionString, string schema, CancellationToken ct)
    {
        const string sql = @"
            SELECT relname, n_live_tup
            FROM pg_stat_user_tables
            WHERE schemaname = @schema
              AND n_live_tup >= @minLive
              AND (last_analyze IS NULL OR last_analyze < NOW() - INTERVAL '7 days')
              AND (last_autoanalyze IS NULL OR last_autoanalyze < NOW() - INTERVAL '7 days')
            ORDER BY relname;";

        var findings = new List<HealthFinding>();
        using var pg = new NpgsqlConnection(connectionString);
        await pg.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = new NpgsqlCommand(sql, pg);
        cmd.Parameters.AddWithValue("schema", schema);
        cmd.Parameters.AddWithValue("minLive", AnalyzeMinLiveTuples);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var table = reader.GetString(0);
            var live = reader.GetInt64(1);
            findings.Add(new HealthFinding(
                Check: "StaleAnalyze",
                Severity: HealthSeverity.Warn,
                Message: $"'{table}' ({live:N0} filas) no tiene estadísticas ANALYZE recientes. " +
                         "El planner puede elegir planes subóptimos.",
                Detail: table,
                Repairable: false));
        }

        return findings;
    }

    // ── Check 6: slow queries (pg_stat_statements) ───────────────────────

    /// <summary>
    /// Reports the top slow queries from <c>pg_stat_statements</c>.
    /// Silently skips when the extension is not installed — no finding is emitted.
    /// Queries are classified as Warn (≥ 500 ms mean) or Ok (informational).
    /// </summary>
    private static async Task<IReadOnlyList<HealthFinding>> CheckSlowQueriesAsync(
        string connectionString, CancellationToken ct)
    {
        const string checkSql = """
            SELECT COUNT(*) FROM pg_extension WHERE extname = 'pg_stat_statements';
            """;

        const string slowSql = """
            SELECT
                LEFT(query, 120)                                AS query_preview,
                ROUND(mean_exec_time::numeric, 2)               AS mean_ms,
                calls,
                ROUND(total_exec_time::numeric, 2)              AS total_ms,
                ROUND(rows::numeric / NULLIF(calls, 0), 1)      AS avg_rows
            FROM pg_stat_statements
            WHERE dbid = (SELECT oid FROM pg_database WHERE datname = current_database())
              AND query NOT LIKE '%pg\_%'
              AND query NOT LIKE '%pg\_stat%'
              AND mean_exec_time > 100
              AND calls > 2
            ORDER BY mean_exec_time DESC
            LIMIT 10;
            """;

        // Warn threshold: queries averaging more than this are flagged.
        const double WarnThresholdMs = 500.0;

        using var pg = new NpgsqlConnection(connectionString);
        await pg.OpenAsync(ct).ConfigureAwait(false);

        // Check if extension is installed; skip silently if not.
        using (var checkCmd = new NpgsqlCommand(checkSql, pg))
        {
            var extCount = Convert.ToInt64(
                await checkCmd.ExecuteScalarAsync(ct).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            if (extCount == 0)
            {
                return Array.Empty<HealthFinding>();
            }
        }

        var slow = new List<SlowQueryInfo>();
        using (var cmd = new NpgsqlCommand(slowSql, pg))
        using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                slow.Add(new SlowQueryInfo(
                    QueryPreview: reader.GetString(0).Trim(),
                    MeanMs: Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture),
                    Calls: reader.GetInt64(2),
                    TotalMs: Convert.ToDouble(reader.GetValue(3), CultureInfo.InvariantCulture),
                    Rows: Convert.ToDouble(reader.GetValue(4), CultureInfo.InvariantCulture)));
            }
        }

        if (slow.Count == 0)
        {
            return Array.Empty<HealthFinding>();
        }

        var findings = new List<HealthFinding>(slow.Count);
        foreach (var q in slow)
        {
            var severity = q.MeanMs >= WarnThresholdMs ? HealthSeverity.Warn : HealthSeverity.Ok;
            var detail = $"media={q.MeanMs:F0} ms | llamadas={q.Calls:N0} | total={q.TotalMs / 1000.0:F1} s | filas̅={q.Rows:F0}";
            findings.Add(new HealthFinding(
                Check: "SlowQuery",
                Severity: severity,
                Message: $"Query lenta detectada (media {q.MeanMs:F0} ms en {q.Calls:N0} llamada(s)): {q.QueryPreview}",
                Detail: detail,
                Repairable: false));
        }

        return findings;
    }

    // ── Check 5: unused indexes ───────────────────────────────────────────────

    /// <summary>Detects user indexes never scanned (candidates for removal).</summary>
    private static async Task<IReadOnlyList<HealthFinding>> CheckUnusedIndexesAsync(
        string connectionString, string schema, CancellationToken ct)
    {
        const string sql = @"
            SELECT relname, indexrelname
            FROM pg_stat_user_indexes
            WHERE schemaname = @schema
              AND idx_scan = 0
              AND indexrelname NOT LIKE '%_pkey'
            ORDER BY relname, indexrelname;";

        var findings = new List<HealthFinding>();
        using var pg = new NpgsqlConnection(connectionString);
        await pg.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = new NpgsqlCommand(sql, pg);
        cmd.Parameters.AddWithValue("schema", schema);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var table = reader.GetString(0);
            var index = reader.GetString(1);
            findings.Add(new HealthFinding(
                Check: "UnusedIndex",
                Severity: HealthSeverity.Ok,
                Message: $"El índice '{index}' de '{table}' nunca ha sido usado por el planner. " +
                         "Podría eliminarse para reducir el costo de escritura.",
                Detail: $"{table}.{index}",
                Repairable: false));
        }

        return findings;
    }

    // ── Detection helpers (shared by checks and repair) ──────────────────────

    /// <summary>
    /// Returns (schema, table, index) tuples for every invalid index across all schemas.
    /// Searching all schemas ensures pg_toast orphans left by interrupted REINDEX operations
    /// are also detected, even though only a superuser can drop them.
    /// </summary>
    private static async Task<List<(string Schema, string Table, string Index)>> FindInvalidIndexesAsync(
        string connectionString, CancellationToken ct)
    {
        const string sql = @"
            SELECT n.nspname AS schema_name, t.relname AS table_name, c.relname AS index_name
            FROM pg_index i
            JOIN pg_class c ON c.oid = i.indexrelid
            JOIN pg_class t ON t.oid = i.indrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE NOT i.indisvalid
            ORDER BY n.nspname, t.relname, c.relname;";

        var result = new List<(string, string, string)>();
        using var pg = new NpgsqlConnection(connectionString);
        await pg.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = new NpgsqlCommand(sql, pg);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return result;
    }

    /// <summary>Returns (table, deadPct) pairs for bloated tables above the warn threshold.</summary>
    private static async Task<List<(string Table, double DeadPct)>> FindBloatedTablesAsync(
        string connectionString, string schema, CancellationToken ct)
    {
        const string sql = @"
            SELECT relname,
                   n_live_tup,
                   n_dead_tup,
                   ROUND(100.0 * n_dead_tup / NULLIF(n_live_tup + n_dead_tup, 0), 1) AS dead_pct
            FROM pg_stat_user_tables
            WHERE schemaname = @schema
              AND n_dead_tup > 1000
            ORDER BY dead_pct DESC
            LIMIT 20;";

        var result = new List<(string, double)>();
        using var pg = new NpgsqlConnection(connectionString);
        await pg.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = new NpgsqlCommand(sql, pg);
        cmd.Parameters.AddWithValue("schema", schema);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var table = reader.GetString(0);
            var live = reader.GetInt64(1);
            var dead = reader.GetInt64(2);
            var deadPct = reader.GetDouble(3);

            // Only flag when the table is actually bloated, not just recently churned.
            var total = live + dead;
            if (total > 0 && dead / (double)total >= BloatWarnRatio)
            {
                result.Add((table, deadPct));
            }
        }

        return result;
    }

    /// <summary>Returns identity sequences whose last_value trails the max column value.</summary>
    private static async Task<List<SequenceIssue>> FindSequenceIssuesAsync(
        string connectionString, string schema, CancellationToken ct)
    {
        const string sequenceSql = @"
            SELECT n.nspname AS schema_name,
                   c.relname AS table_name,
                   a.attname AS column_name,
                   pg_get_expr(ad.adbin, ad.adrelid) AS default_expr
            FROM pg_attrdef ad
            JOIN pg_class c ON c.oid = ad.adrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = ad.adnum
            WHERE n.nspname = @schema
              AND pg_get_expr(ad.adbin, ad.adrelid) LIKE 'nextval%'
            ORDER BY c.relname, a.attname;";

        var issues = new List<SequenceIssue>();
        using var pg = new NpgsqlConnection(connectionString);
        await pg.OpenAsync(ct).ConfigureAwait(false);

        using (var cmd = new NpgsqlCommand(sequenceSql, pg))
        {
            cmd.Parameters.AddWithValue("schema", schema);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var candidates = new List<(string Schema, string Table, string Column, string Sequence)>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var expr = reader.GetString(3);
                var seqMatch = NextvalRegex().Match(expr);
                if (!seqMatch.Success)
                {
                    continue;
                }

                var seqName = seqMatch.Groups[1].Value.Trim('\'', '"', ' ');
                candidates.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), seqName));
            }

            foreach (var cand in candidates)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                var issue = await ProbeSequenceAsync(pg, cand.Schema, cand.Table, cand.Column, cand.Sequence, ct)
                    .ConfigureAwait(false);
                if (issue is not null)
                {
                    issues.Add(issue);
                }
            }
        }

        return issues;
    }

    /// <summary>Compares a sequence's last_value against the max of its owning column.</summary>
    private static async Task<SequenceIssue?> ProbeSequenceAsync(
        NpgsqlConnection pg,
        string schema,
        string table,
        string column,
        string sequence,
        CancellationToken ct)
    {
        try
        {
            // Fail-closed: identifiers returned by the catalog are validated before use.
            var safeSchema = RequireSqlIdentifier(schema, "schema");
            var safeTable = RequireSqlIdentifier(table, "tabla");
            var safeColumn = RequireSqlIdentifier(column, "columna");
            var safeSequence = RequireSqlIdentifier(sequence, "secuencia");

            var lastValue = await ExecuteScalarLongAsync(
                pg,
                $"SELECT last_value FROM {MaintenanceService.QuoteIdentifier(safeSequence)};",
                ct).ConfigureAwait(false);

            var maxValue = await ExecuteScalarLongAsync(
                pg,
                $"SELECT COALESCE(MAX({MaintenanceService.QuoteIdentifier(safeColumn)}), 0) " +
                $"FROM {MaintenanceService.QuoteIdentifier(safeSchema)}.{MaintenanceService.QuoteIdentifier(safeTable)};",
                ct).ConfigureAwait(false);

            // A sequence that trails the column max would hand out an existing value
            // on the next INSERT → duplicate PK error.
            return lastValue < maxValue
                ? new SequenceIssue(schema, table, column, sequence, lastValue, maxValue)
                : null;
        }
        catch (Exception ex)
        {
            // Some sequences may be detached or otherwise unreadable — skip, don't fail the whole check.
            System.Diagnostics.Debug.WriteLine(ex.Message);
            return null;
        }
    }

    // ── Repair actions ────────────────────────────────────────────────────────

    /// <summary>Rebuilds a single invalid index with <c>REINDEX INDEX CONCURRENTLY</c>.</summary>
    private async Task<HealthRepairItem> RepairIndexAsync(
        string connectionString, string schema, string table, string index, CancellationToken ct)
    {
        var target = $"{schema}.{table}.{index}";
        try
        {
            // Fail-closed: validate before interpolating into DDL.
            var safeSchema = RequireSqlIdentifier(schema, "schema");
            var safeIndex = RequireSqlIdentifier(index, "índice");

            using var pg = new NpgsqlConnection(connectionString);
            await pg.OpenAsync(ct).ConfigureAwait(false);
            var qualified = $"{MaintenanceService.QuoteIdentifier(safeSchema)}.{MaintenanceService.QuoteIdentifier(safeIndex)}";
            await ExecuteNonQueryAsync(pg, $"REINDEX INDEX CONCURRENTLY {qualified};", ct).ConfigureAwait(false);

            _logger.LogInformation("Reindexed invalid index {Target}", target);
            PostgresLog.Warn($"[HealthCheck] REINDEX OK: {target}");
            return new HealthRepairItem("Reindex", target, true, "Índice reconstruido correctamente.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reindex {Target}", target);
            PostgresLog.Error($"[HealthCheck] REINDEX falló: {target} — {ex.Message}");
            return new HealthRepairItem("Reindex", target, false, ex.Message);
        }
    }

    /// <summary>Runs <c>VACUUM ANALYZE</c> on a single bloated table.</summary>
    private async Task<HealthRepairItem> RepairBloatAsync(
        string connectionString, string schema, string table, double deadPct, CancellationToken ct)
    {
        var target = $"{schema}.{table}";
        try
        {
            // Fail-closed: validate before interpolating into DDL.
            var safeSchema = RequireSqlIdentifier(schema, "schema");
            var safeTable = RequireSqlIdentifier(table, "tabla");

            using var pg = new NpgsqlConnection(connectionString);
            await pg.OpenAsync(ct).ConfigureAwait(false);
            var qualified = $"{MaintenanceService.QuoteIdentifier(safeSchema)}.{MaintenanceService.QuoteIdentifier(safeTable)}";
            await ExecuteNonQueryAsync(pg, $"VACUUM ANALYZE {qualified};", ct).ConfigureAwait(false);

            _logger.LogInformation("VACUUM ANALYZE {Target} (bloat {Pct:F1}%)", target, deadPct);
            PostgresLog.Warn($"[HealthCheck] VACUUM OK: {target}");
            return new HealthRepairItem(
                "Vacuum",
                target,
                true,
                $"VACUUM ANALYZE completado (bloat {deadPct.ToString("F1", CultureInfo.InvariantCulture)}%).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to vacuum {Target}", target);
            PostgresLog.Error($"[HealthCheck] VACUUM falló: {target} — {ex.Message}");
            return new HealthRepairItem("Vacuum", target, false, ex.Message);
        }
    }

    /// <summary>Advances a stale sequence to its column max with <c>setval</c>.</summary>
    private async Task<HealthRepairItem> RepairSequenceAsync(
        string connectionString, string schema, SequenceIssue issue, CancellationToken ct)
    {
        var target = $"{schema}.{issue.Sequence}";
        try
        {
            // Fail-closed: validate before interpolating into DDL.
            var safeSequence = RequireSqlIdentifier(issue.Sequence, "secuencia");

            using var pg = new NpgsqlConnection(connectionString);
            await pg.OpenAsync(ct).ConfigureAwait(false);
            var sql = $"SELECT setval({MaintenanceService.QuoteIdentifier(safeSequence)}, {issue.MaxValue.ToString(CultureInfo.InvariantCulture)});";
            await ExecuteNonQueryAsync(pg, sql, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Fixed sequence {Target}: setval({Max:N0}) for {Table}.{Column}",
                target,
                issue.MaxValue,
                issue.Table,
                issue.Column);
            PostgresLog.Warn($"[HealthCheck] FIX SEQ OK: {target} → {issue.MaxValue}");
            return new HealthRepairItem(
                "FixSequence",
                target,
                true,
                $"setval({issue.MaxValue.ToString("N0", CultureInfo.InvariantCulture)}) aplicado a {issue.Table}.{issue.Column}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fix sequence {Target}", target);
            PostgresLog.Error($"[HealthCheck] FIX SEQ falló: {target} — {ex.Message}");
            return new HealthRepairItem("FixSequence", target, false, ex.Message);
        }
    }

    // ── Shared SQL helpers (defense in depth) ─────────────────────────────────

    /// <summary>Computes the worst severity across all findings (Error &gt; Warn &gt; Ok).</summary>
    private static HealthSeverity WorstSeverity(List<HealthFinding> findings)
    {
        if (findings.Any(static f => f.Severity == HealthSeverity.Error))
        {
            return HealthSeverity.Error;
        }

        return findings.Any(static f => f.Severity == HealthSeverity.Warn)
            ? HealthSeverity.Warn
            : HealthSeverity.Ok;
    }

    /// <summary>
    /// Awaits a check task and converts any exception into an Error finding so the
    /// remaining checks still complete and the failure is visible in the report.
    /// </summary>
    /// <param name="checkName">Short identifier of the check.</param>
    /// <param name="task">The running check task.</param>
    /// <returns>The check findings, or a single Error finding if the check threw.</returns>
    private static async Task<IReadOnlyList<HealthFinding>> CaptureCheckAsync(
        string checkName,
        Task<IReadOnlyList<HealthFinding>> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PostgresLog.Error($"[HealthCheck] '{checkName}' falló: {ex.Message}");
            return new[]
            {
                new HealthFinding(
                    Check: checkName,
                    Severity: HealthSeverity.Error,
                    Message: $"No se pudo ejecutar el check '{checkName}': {ex.Message}",
                    Detail: ex.GetType().Name,
                    Repairable: false),
            };
        }
    }

    /// <summary>
    /// Writes a compact summary to the plugin log: a per-severity count, then the
    /// individual findings that require attention (Warn/Error). Informational findings
    /// are only counted so the log stays readable on large databases.
    /// </summary>
    /// <param name="findings">Findings to summarize in the plugin log.</param>
    private static void LogFindings(List<HealthFinding> findings)
    {
        var errors = findings.Count(static f => f.Severity == HealthSeverity.Error);
        var warns = findings.Count(static f => f.Severity == HealthSeverity.Warn);
        var info = findings.Count - errors - warns;

        PostgresLog.Warn(
            $"[HealthCheck] Resumen: {errors} error(es), {warns} advertencia(s), {info} informativo(s) | Total: {findings.Count}");

        // Only findings that need attention are listed individually; the rest are just counted.
        foreach (var f in findings)
        {
            if (f.Severity == HealthSeverity.Ok)
            {
                continue;
            }

            var detail = string.IsNullOrEmpty(f.Detail) ? string.Empty : $" | {f.Detail}";
            PostgresLog.Warn($"[HealthCheck] [{f.Check}] {f.Message}{detail}");
        }
    }

    /// <summary>
    /// Validates that a name returned by the server catalog is a plain SQL identifier
    /// before it is ever interpolated into a command. Fail-closed: any unexpected value
    /// throws and aborts the operation instead of being embedded into SQL.
    /// </summary>
    /// <param name="name">Identifier returned by PostgreSQL catalog views.</param>
    /// <param name="kind">Human-readable kind used in the error message (e.g. "tabla").</param>
    /// <returns>The validated identifier.</returns>
    /// <exception cref="InvalidOperationException">When <paramref name="name"/> is not a safe SQL identifier.</exception>
    private static string RequireSqlIdentifier(string name, string kind)
    {
        if (string.IsNullOrWhiteSpace(name) || !SqlIdentifierRegex().IsMatch(name))
        {
            throw new InvalidOperationException(
                $"El servidor devolvió un {kind} no esperado ('{name}'); se cancela la operación por seguridad.");
        }

        return name;
    }

    // SQL text is only ever assembled by this class from identifiers that passed
    // RequireSqlIdentifier (strict ^[A-Za-z_][A-Za-z0-9_]*$) and QuoteIdentifier,
    // plus numeric long values formatted with the invariant culture. No user input
    // can reach the command text, so the analyzer flags are false positives here.
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "SQL is assembled by this class only from identifiers validated with RequireSqlIdentifier (strict ^[A-Za-z_][A-Za-z0-9_]*$) and escaped with QuoteIdentifier; values are numeric longs. No user input.")]
    [SuppressMessage("Security", "CA3001:Review code for SQL injection vulnerabilities", Justification = "SQL is assembled by this class only from identifiers validated with RequireSqlIdentifier (strict ^[A-Za-z_][A-Za-z0-9_]*$) and escaped with QuoteIdentifier; values are numeric longs. No user input.")]
    private static async Task ExecuteNonQueryAsync(NpgsqlConnection pg, string sql, CancellationToken ct)
    {
        using var cmd = new NpgsqlCommand(sql, pg) { CommandTimeout = 0 };
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // Same guarantees as ExecuteNonQueryAsync: receives SQL already built from
    // validated identifiers. This is the only other place a command is created.
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "SQL is assembled by this class only from identifiers validated with RequireSqlIdentifier (strict ^[A-Za-z_][A-Za-z0-9_]*$) and escaped with QuoteIdentifier; values are numeric longs. No user input.")]
    [SuppressMessage("Security", "CA3001:Review code for SQL injection vulnerabilities", Justification = "SQL is assembled by this class only from identifiers validated with RequireSqlIdentifier (strict ^[A-Za-z_][A-Za-z0-9_]*$) and escaped with QuoteIdentifier; values are numeric longs. No user input.")]
    private static async Task<long> ExecuteScalarLongAsync(NpgsqlConnection pg, string sql, CancellationToken ct)
    {
        using var cmd = new NpgsqlCommand(sql, pg);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SqlIdentifierRegex();

    [GeneratedRegex("nextval\\('([^']+)'::regclass\\)", RegexOptions.IgnoreCase)]
    private static partial Regex NextvalRegex();

    /// <summary>Describes an identity sequence that has fallen behind its column maximum.</summary>
    /// <param name="Schema">Schema owning the sequence.</param>
    /// <param name="Table">Table that owns the sequence.</param>
    /// <param name="Column">Column that consumes the sequence.</param>
    /// <param name="Sequence">Sequence name.</param>
    /// <param name="LastValue">Current <c>last_value</c> of the sequence.</param>
    /// <param name="MaxValue">Maximum value present in the column.</param>
    private sealed record SequenceIssue(
        string Schema,
        string Table,
        string Column,
        string Sequence,
        long LastValue,
        long MaxValue);
}
