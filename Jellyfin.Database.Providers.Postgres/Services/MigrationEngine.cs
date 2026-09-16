using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Providers.Postgres.Logging;
using Jellyfin.Database.Providers.Postgres.Services.Models;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// Orchestrates the SQLite to PostgreSQL migration by calling focused helpers.
/// </summary>
internal static class MigrationEngine
{
    private static readonly HashSet<string> SkipTables = new(StringComparer.OrdinalIgnoreCase)
        { "__EFMigrationsHistory", "__EFMigrationsLock" };

    /// <summary>
    /// Runs the full SQLite to PostgreSQL migration pipeline.
    /// </summary>
    /// <param name="options">Migration options (paths, schema, batch size, etc.).</param>
    /// <param name="svc">Migration service used for progress reporting and logging.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    internal static async Task RunAsync(
        MigrationOptions options,
        MigrationService svc,
        CancellationToken ct)
    {
        if (!File.Exists(options.SqlitePath))
        {
            throw new FileNotFoundException($"SQLite file not found: {options.SqlitePath}", options.SqlitePath);
        }

        svc.Log($"Iniciando migracion: {options.SqlitePath} -> PostgreSQL (schema={options.Schema}, batch={options.BatchSize})");

        using var sqlite = new SqliteConnection($"Data Source={options.SqlitePath};Mode=ReadOnly;Cache=Shared");
        var pgConnection = new NpgsqlConnectionStringBuilder(options.PostgresConnectionString)
        {
            SearchPath = MigrationDiscovery.QuoteIdentifier(options.Schema),
        };
        using var pg = new NpgsqlConnection(pgConnection.ConnectionString);

        await sqlite.OpenAsync(ct).ConfigureAwait(false);
        await MigrationCodeMigrations.ValidateSourceAsync(sqlite).ConfigureAwait(false);
        await pg.OpenAsync(ct).ConfigureAwait(false);

        await MigrationAssemblyResolver
            .ApplySchemaAsync(options.PostgresConnectionString, options.Schema, svc.Log, ct)
            .ConfigureAwait(false);
        await MigrationSchemaPreparer.EnsureTimestampColumnsAreTimestamptzAsync(pg, options.Schema, svc.Log).ConfigureAwait(false);

        var tablesToMigrate = await DiscoverTablesAsync(sqlite, pg, options.Schema, svc).ConfigureAwait(false);

        await MigrationFkManager
            .SetFkTriggersAsync(pg, options.Schema, tablesToMigrate, enable: false, svc.Log, MigrationNullLogger.Instance)
            .ConfigureAwait(false);

        var needNormalizedFix = await PrepareNormalizedUsernameAsync(sqlite, pg, options.Schema, tablesToMigrate, svc).ConfigureAwait(false);

        var errors = new List<(string Table, string Message)>();
        long totalRows = 0;

        try
        {
            totalRows = await CopyAllTablesAsync(sqlite, pg, options, tablesToMigrate, errors, svc).ConfigureAwait(false);
            await ApplyPostCopyFixesAsync(pg, options.Schema, needNormalizedFix, svc).ConfigureAwait(false);
        }
        finally
        {
            await MigrationFkManager
                .SetFkTriggersAsync(pg, options.Schema, tablesToMigrate, enable: true, svc.Log, MigrationNullLogger.Instance)
                .ConfigureAwait(false);
        }

        svc.SetPercentComplete(100);

        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"{errors.Count} tabla(s) fallaron: " + string.Join(", ", errors.Select(e => e.Table)));
        }

        await MigrationCodeMigrations.CopyAppliedAsync(sqlite, pg, svc.Log).ConfigureAwait(false);

        svc.Log($"Migracion completada. Total de filas copiadas: {totalRows}.");
        PostgresLog.Info($"[Migration] Total filas migradas: {totalRows:N0}");
    }

    // Private helpers

    private static async Task<List<string>> DiscoverTablesAsync(
        SqliteConnection sqlite,
        NpgsqlConnection pg,
        string schema,
        MigrationService svc)
    {
        var tables = await MigrationDiscovery.GetSqliteTablesAsync(sqlite).ConfigureAwait(false);
        var tablesToMigrate = tables.Where(t => !SkipTables.Contains(t)).ToList();

        svc.Log($"Tablas SQLite: {tables.Count} total, {tablesToMigrate.Count} a migrar");
        PostgresLog.Info($"[Migration] Tablas SQLite: {tables.Count} total, {tablesToMigrate.Count} a migrar");

        return await MigrationDiscovery
            .SortTablesByFkDependencyAsync(pg, schema, tablesToMigrate)
            .ConfigureAwait(false);
    }

    private static async Task<bool> PrepareNormalizedUsernameAsync(
        SqliteConnection sqlite,
        NpgsqlConnection pg,
        string schema,
        List<string> tablesToMigrate,
        MigrationService svc)
    {
        if (!tablesToMigrate.Any(t => string.Equals(t, "Users", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var pgColsUsers = await MigrationDiscovery.GetPgColumnsAsync(pg, schema, "Users").ConfigureAwait(false);
        var sqliteColsUsers = await MigrationDiscovery.GetSqliteColumnNamesAsync(sqlite, "Users").ConfigureAwait(false);
        var needFix = pgColsUsers.ContainsKey("NormalizedUsername")
            && !sqliteColsUsers.Any(c => string.Equals(c, "NormalizedUsername", StringComparison.OrdinalIgnoreCase));

        if (needFix)
        {
            svc.Log("[Users] Permitiendo NULL en NormalizedUsername temporalmente...");
            using var alterCmd = CreateAlterNormalizedUsernameDropNotNullCommand(pg, schema);
            await alterCmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return needFix;
    }

    private static async Task<long> CopyAllTablesAsync(
        SqliteConnection sqlite,
        NpgsqlConnection pg,
        MigrationOptions options,
        List<string> tablesToMigrate,
        List<(string Table, string Message)> errors,
        MigrationService svc)
    {
        long totalRows = 0;

        // Truncate all targets before copying any rows: CASCADE during a later table
        // would otherwise erase rows already imported into its dependent tables.
        if (options.Truncate)
        {
            foreach (var table in tablesToMigrate)
            {
                using var truncCmd = CreateTruncateCommand(pg, options.Schema, table);
                await truncCmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        for (var i = 0; i < tablesToMigrate.Count; i++)
        {
            var table = tablesToMigrate[i];
            svc.SetCurrentTable(table);
            svc.SetPercentComplete((int)Math.Round((double)i / tablesToMigrate.Count * 95));

            var commonCols = await ResolveCommonColumnsAsync(sqlite, pg, options.Schema, table, svc).ConfigureAwait(false);
            if (commonCols is null)
            {
                continue;
            }

            try
            {
                if (options.Truncate)
                {
                    svc.Log($"[{table}] truncando...");
                    using var truncCmd = CreateTruncateCommand(pg, options.Schema, table);
                    await truncCmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
                }

                svc.Log($"[{table}] copiando {commonCols.Count} columna(s)...");
                var rowCount = await MigrationTableCopier
                    .CopyTableAsync(sqlite, pg, options.Schema, table, commonCols, options.BatchSize, svc.Log)
                    .ConfigureAwait(false);

                totalRows += rowCount;
                svc.AddMigratedRows(rowCount);
                svc.Log($"[{table}] OK - {rowCount} filas.");
                PostgresLog.Info($"[Migration]   -> {table} ({rowCount:N0} filas)");
            }
            catch (Exception ex)
            {
                svc.Log($"[{table}] ERROR: {ex.Message}");
                errors.Add((table, ex.Message));
                PostgresLog.Error($"[Migration] ERROR en tabla {table}: {ex.Message}", ex);
            }
        }

        return totalRows;
    }

    private static async Task<List<TargetColumn>?> ResolveCommonColumnsAsync(
        SqliteConnection sqlite,
        NpgsqlConnection pg,
        string schema,
        string table,
        MigrationService svc)
    {
        var sqliteCols = await MigrationDiscovery.GetSqliteColumnNamesAsync(sqlite, table).ConfigureAwait(false);
        if (sqliteCols.Count == 0)
        {
            svc.Log($"[{table}] sin columnas en SQLite, omitido.");
            return null;
        }

        var pgCols = await MigrationDiscovery.GetPgColumnsAsync(pg, schema, table).ConfigureAwait(false);
        if (pgCols.Count == 0)
        {
            svc.Log($"[{table}] no existe en PostgreSQL, omitido.");
            return null;
        }

        var commonCols = sqliteCols
            .Where(sc => pgCols.ContainsKey(sc))
            .Select(sc => new TargetColumn(sc, pgCols[sc].PgType, pgCols[sc].MaxLength))
            .ToList();

        if (commonCols.Count == 0)
        {
            svc.Log($"[{table}] ninguna columna coincide, omitido.");
            return null;
        }

        return commonCols;
    }

    private static async Task ApplyPostCopyFixesAsync(
        NpgsqlConnection pg,
        string schema,
        bool needNormalizedFix,
        MigrationService svc)
    {
        if (needNormalizedFix)
        {
            svc.Log("[Users] Calculando NormalizedUsername = UPPER(Username)...");
            using var fixCmd = CreateFillNormalizedUsernameCommand(pg, schema);
            var updated = await fixCmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            svc.Log($"[Users] {updated} filas actualizadas.");

            using var restoreCmd = CreateAlterNormalizedUsernameSetNotNullCommand(pg, schema);
            await restoreCmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            svc.Log("[Users] Restriccion NOT NULL restaurada.");
        }

        svc.Log("Reseteando secuencias PostgreSQL...");
        await MigrationSchemaPreparer.ResetSequencesAsync(pg, schema).ConfigureAwait(false);
        svc.Log("Secuencias reseteadas.");
    }

    // Command factory methods
    // schema/table come from migration configuration or pg_tables/sqlite_master (server-controlled), never from user input.

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
