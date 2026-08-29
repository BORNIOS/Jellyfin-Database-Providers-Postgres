using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Providers.Postgres.Logging;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// Orchestrates the SQLite → PostgreSQL migration by calling focused helpers.
/// </summary>
internal static class MigrationEngine
{
    private static readonly HashSet<string> SkipTables = new(StringComparer.OrdinalIgnoreCase)
        { "__EFMigrationsHistory", "__EFMigrationsLock" };

    internal static async Task RunAsync(
        string sqlitePath,
        string postgresConnectionString,
        string schema,
        int batchSize,
        bool truncate,
        IApplicationPaths? applicationPaths,
        MigrationService svc,
        CancellationToken ct)
    {
        if (!File.Exists(sqlitePath))
        {
            throw new FileNotFoundException($"SQLite file not found: {sqlitePath}", sqlitePath);
        }

        svc.Log($"Iniciando migración: {sqlitePath} → PostgreSQL (schema={schema}, batch={batchSize})");

        using var sqlite = new SqliteConnection($"Data Source={sqlitePath};Mode=ReadOnly;Cache=Shared");
        using var pg = new NpgsqlConnection(postgresConnectionString);

        await sqlite.OpenAsync(ct).ConfigureAwait(false);
        await pg.OpenAsync(ct).ConfigureAwait(false);

        await MigrationSchemaPreparer.ApplySchemaIfNeededAsync(pg, schema, svc.Log).ConfigureAwait(false);
        await MigrationSchemaPreparer.EnsureTimestampColumnsAreTimestamptzAsync(pg, schema, svc.Log).ConfigureAwait(false);
        await MigrationCodeMigrations.PreMarkAsync(pg, svc.Log).ConfigureAwait(false);

        var tables = await MigrationDiscovery.GetSqliteTablesAsync(sqlite).ConfigureAwait(false);
        var tablesToMigrate = tables.Where(t => !SkipTables.Contains(t)).ToList();
        svc.Log($"Tablas SQLite: {tables.Count} total, {tablesToMigrate.Count} a migrar");
        PostgresLog.Warn($"[Migration] Tablas SQLite: {tables.Count} total, {tablesToMigrate.Count} a migrar");

        tablesToMigrate = await MigrationDiscovery
            .SortTablesByFkDependencyAsync(pg, schema, tablesToMigrate)
            .ConfigureAwait(false);

        await MigrationFkManager
            .SetFkTriggersAsync(pg, schema, tablesToMigrate, enable: false, svc.Log, MigrationNullLogger.Instance)
            .ConfigureAwait(false);

        var needNormalizedFix = false;
        if (tablesToMigrate.Any(t => string.Equals(t, "Users", StringComparison.OrdinalIgnoreCase)))
        {
            var pgColsUsers = await MigrationDiscovery.GetPgColumnsAsync(pg, schema, "Users").ConfigureAwait(false);
            var sqliteColsUsers = await MigrationDiscovery.GetSqliteColumnNamesAsync(sqlite, "Users").ConfigureAwait(false);
            needNormalizedFix = pgColsUsers.ContainsKey("NormalizedUsername")
                && !sqliteColsUsers.Any(c => string.Equals(c, "NormalizedUsername", StringComparison.OrdinalIgnoreCase));

            if (needNormalizedFix)
            {
                svc.Log("[Users] Permitiendo NULL en NormalizedUsername temporalmente...");
                using var alterCmd = CreateAlterNormalizedUsernameDropNotNullCommand(pg, schema);
                await alterCmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        var errors = new List<(string Table, string Message)>();
        long totalRows = 0;

        try
        {
            for (var i = 0; i < tablesToMigrate.Count; i++)
            {
                var table = tablesToMigrate[i];
                svc.SetCurrentTable(table);
                svc.SetPercentComplete((int)Math.Round((double)i / tablesToMigrate.Count * 95));

                var sqliteCols = await MigrationDiscovery.GetSqliteColumnNamesAsync(sqlite, table).ConfigureAwait(false);
                if (sqliteCols.Count == 0)
                {
                    svc.Log($"[{table}] sin columnas en SQLite, omitido.");
                    continue;
                }

                var pgCols = await MigrationDiscovery.GetPgColumnsAsync(pg, schema, table).ConfigureAwait(false);
                if (pgCols.Count == 0)
                {
                    svc.Log($"[{table}] no existe en PostgreSQL, omitido.");
                    continue;
                }

                var commonCols = sqliteCols
                    .Where(sc => pgCols.ContainsKey(sc))
                    .Select(sc => new TargetColumn(sc, pgCols[sc].PgType, pgCols[sc].MaxLength))
                    .ToList();

                if (commonCols.Count == 0)
                {
                    svc.Log($"[{table}] ninguna columna coincide, omitido.");
                    continue;
                }

                try
                {
                    if (truncate)
                    {
                        svc.Log($"[{table}] truncando...");
                        using var truncCmd = CreateTruncateCommand(pg, schema, table);
                        await truncCmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
                    }

                    svc.Log($"[{table}] copiando {commonCols.Count} columna(s)...");
                    var rowCount = await MigrationTableCopier
                        .CopyTableAsync(sqlite, pg, schema, table, commonCols, batchSize, svc.Log)
                        .ConfigureAwait(false);

                    totalRows += rowCount;
                    svc.AddMigratedRows(rowCount);
                    svc.Log($"[{table}] OK — {rowCount} filas.");
                    PostgresLog.Warn($"[Migration]   → {table} ({rowCount:N0} filas)");
                }
                catch (Exception ex)
                {
                    svc.Log($"[{table}] ERROR: {ex.Message}");
                    errors.Add((table, ex.Message));
                    PostgresLog.Error($"[Migration] ERROR en tabla {table}: {ex.Message}", ex);
                }
            }

            if (needNormalizedFix)
            {
                svc.Log("[Users] Calculando NormalizedUsername = UPPER(Username)...");
                using var fixCmd = CreateFillNormalizedUsernameCommand(pg, schema);
                var updated = await fixCmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
                svc.Log($"[Users] {updated} filas actualizadas.");

                using var restoreCmd = CreateAlterNormalizedUsernameSetNotNullCommand(pg, schema);
                await restoreCmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
                svc.Log("[Users] Restricción NOT NULL restaurada.");
            }

            svc.Log("Reseteando secuencias PostgreSQL...");
            await MigrationSchemaPreparer.ResetSequencesAsync(pg, schema).ConfigureAwait(false);
            svc.Log("Secuencias reseteadas.");
        }
        finally
        {
            await MigrationFkManager
                .SetFkTriggersAsync(pg, schema, tablesToMigrate, enable: true, svc.Log, MigrationNullLogger.Instance)
                .ConfigureAwait(false);
        }

        svc.SetPercentComplete(100);

        if (errors.Count > 0)
        {
            svc.Log($"[WARN] {errors.Count} tabla(s) fallaron: " + string.Join(", ", errors.Select(e => e.Table)));
        }

        svc.Log($"Migración completada. Total de filas copiadas: {totalRows}.");
        PostgresLog.Warn($"[Migration] Total filas migradas: {totalRows:N0}");
    }

    // ── Command factory methods ─────────────────────────────────────────────────────────
    // schema/table come from the migration configuration or from pg_tables/sqlite_master
    // (server-controlled catalog data), never from raw HTTP user input.

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "SQL is built from QuoteIdentifier on schema/table names sourced from server catalog, not user input.")]
    private static NpgsqlCommand CreateAlterNormalizedUsernameDropNotNullCommand(NpgsqlConnection pg, string schema)
    {
        var sql = string.Concat(
            "ALTER TABLE ",
            MigrationDiscovery.QuoteIdentifier(schema),
            ".\"Users\" ALTER COLUMN \"NormalizedUsername\" DROP NOT NULL;");
        return new NpgsqlCommand(sql, pg);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "SQL built from server catalog data via QuoteIdentifier, not user input.")]
    private static NpgsqlCommand CreateAlterNormalizedUsernameSetNotNullCommand(NpgsqlConnection pg, string schema)
    {
        var sql = string.Concat(
            "ALTER TABLE ",
            MigrationDiscovery.QuoteIdentifier(schema),
            ".\"Users\" ALTER COLUMN \"NormalizedUsername\" SET NOT NULL;");
        return new NpgsqlCommand(sql, pg);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "SQL built from server catalog data via QuoteIdentifier, not user input.")]
    private static NpgsqlCommand CreateFillNormalizedUsernameCommand(NpgsqlConnection pg, string schema)
    {
        var sql = string.Concat(
            "UPDATE ",
            MigrationDiscovery.QuoteIdentifier(schema),
            ".\"Users\" SET \"NormalizedUsername\" = UPPER(\"Username\") WHERE \"NormalizedUsername\" IS NULL;");
        return new NpgsqlCommand(sql, pg);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "SQL built from QuoteIdentifier on schema/table from server catalog, not user input.")]
    private static NpgsqlCommand CreateTruncateCommand(NpgsqlConnection pg, string schema, string table)
    {
        var sql = string.Concat(
            "TRUNCATE TABLE ",
            MigrationDiscovery.QuoteIdentifier(schema),
            ".",
            MigrationDiscovery.QuoteIdentifier(table),
            " RESTART IDENTITY CASCADE;");
        return new NpgsqlCommand(sql, pg);
    }
}
