using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Locking;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// Handles schema creation, timestamp-column normalization, and sequence reset
/// for the SQLite → PostgreSQL migration.
/// </summary>
internal static class MigrationSchemaPreparer
{
    // ── Schema auto-creation ──────────────────────────────────────────────────

    /// <summary>
    /// Applies pending PostgreSQL migrations, including upgrades of existing schemas.
    /// </summary>
    /// <remarks>
    /// Takes a connection string instead of an open connection on purpose: when the plugin runs in
    /// a collectible load context (Jellyfin 12) this method is executed through
    /// <see cref="MigrationAssemblyResolver.ApplySchemaAsync"/> inside the default context, so it
    /// has to own its connection.
    /// </remarks>
    /// <param name="pgConnStr">PostgreSQL connection string.</param>
    /// <param name="schema">Target schema name.</param>
    /// <param name="log">Callback for progress messages.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    internal static async Task ApplySchemaIfNeededAsync(string pgConnStr, string schema, Action<string> log, CancellationToken ct)
    {
        var builder = new NpgsqlConnectionStringBuilder(pgConnStr)
        {
            SearchPath = MigrationDiscovery.QuoteIdentifier(schema),
        };

        using var pg = new NpgsqlConnection(builder.ConnectionString);
        await pg.OpenAsync(ct).ConfigureAwait(false);

        using (var createSchema = CreateSchemaStatementCommand(pg, "CREATE SCHEMA IF NOT EXISTS " + MigrationDiscovery.QuoteIdentifier(schema)))
        {
            await createSchema.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Leave the target database ready for Jellyfin's own queries (see SqliteCompatibilityBootstrap).
        SqliteCompatibilityBootstrap.Ensure(pg);

        var options = new DbContextOptionsBuilder<JellyfinDbContext>();
        options.UseNpgsql(pg, npgsql => npgsql.MigrationsAssembly(MigrationAssemblyResolver.EnsureRegistered()));
        using var context = new JellyfinDbContext(
            options.Options,
            NullLogger<JellyfinDbContext>.Instance,
            new PostgresDatabaseProvider(null, null),
            new NoLockBehavior(
                NullLogger<NoLockBehavior>.Instance));
        log("[Schema] Aplicando migraciones PostgreSQL para Jellyfin 12.1...");
        await context.Database.MigrateAsync(ct).ConfigureAwait(false);
    }

    // ── Timestamp column normalization ────────────────────────────────────────

    /// <summary>
    /// Converts all <c>timestamp without time zone</c> columns to <c>timestamptz</c>
    /// so Npgsql can insert UTC <see cref="DateTime"/> values without conversion errors.
    /// </summary>
    /// <param name="pg">Open PostgreSQL connection.</param>
    /// <param name="schema">Target schema name.</param>
    /// <param name="log">Callback for progress messages.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    internal static async Task EnsureTimestampColumnsAreTimestamptzAsync(NpgsqlConnection pg, string schema, Action<string> log)
    {
        const string findSql = """
            SELECT table_name, column_name
            FROM information_schema.columns
            WHERE table_schema = @s
              AND data_type = 'timestamp without time zone'
            ORDER BY table_name, ordinal_position;
            """;

        var toConvert = new List<(string Table, string Column)>();
        using (var findCmd = new NpgsqlCommand(findSql, pg))
        {
            findCmd.Parameters.AddWithValue("s", schema);
            using var reader = await findCmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                toConvert.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        if (toConvert.Count == 0)
        {
            return;
        }

        log($"[Schema] Convirtiendo {toConvert.Count} columna(s) timestamp → timestamptz...");
        foreach (var (table, column) in toConvert)
        {
            using var cmd = CreateAlterTimestamptzCommand(pg, schema, table, column);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        log("[Schema] Conversión de timestamps completada.");
    }

    // ── Sequence reset ────────────────────────────────────────────────────────

    /// <summary>
    /// Resets all sequences to <c>MAX(column) + 1</c> after data import
    /// so that subsequent INSERTs do not conflict with migrated IDs.
    /// </summary>
    /// <param name="pg">Open PostgreSQL connection.</param>
    /// <param name="schema">Target schema name.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    internal static async Task ResetSequencesAsync(NpgsqlConnection pg, string schema)
    {
        using var cmd = CreateResetSequencesCommand(pg, schema);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    // ── Command factory methods (CA2100) ──────────────────────────────────────

    // Schema names are quoted as identifiers before reaching this command.
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Schema DDL uses a fixed statement and an identifier escaped by QuoteIdentifier.")]
    private static NpgsqlCommand CreateSchemaStatementCommand(NpgsqlConnection pg, string statement)
        => new NpgsqlCommand(statement, pg);

    // All identifiers come from information_schema system catalog.
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "schema/table/column come from information_schema system catalog, not user input. QuoteIdentifier prevents injection.")]
    private static NpgsqlCommand CreateAlterTimestamptzCommand(
        NpgsqlConnection pg,
        string schema,
        string table,
        string column)
    {
        var q = MigrationDiscovery.QuoteIdentifier;
        var sql = string.Concat(
            "ALTER TABLE ",
            q(schema),
            ".",
            q(table),
            " ALTER COLUMN ",
            q(column),
            " TYPE timestamp with time zone",
            " USING CASE WHEN ",
            q(column),
            " IS NULL THEN NULL ELSE ",
            q(column),
            " AT TIME ZONE 'UTC' END;");
        return new NpgsqlCommand(sql, pg);
    }

    // Schema is a trusted internal value (not raw HTTP input); escaped for SQL literal safety.
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "schema is an internal configuration value escaped with Replace, not raw user HTTP input.")]
    private static NpgsqlCommand CreateResetSequencesCommand(NpgsqlConnection pg, string schema)
    {
        var safeSchema = schema.Replace("'", "''", StringComparison.Ordinal);
        var sql = string.Concat(
            "DO $$ DECLARE r RECORD; BEGIN FOR r IN ",
            "SELECT nsp.nspname AS schema_name, seq.relname AS seq_name, tab.relname AS table_name, col.attname AS col_name ",
            "FROM pg_class seq JOIN pg_namespace nsp ON nsp.oid = seq.relnamespace ",
            "JOIN pg_depend dep ON dep.objid = seq.oid JOIN pg_class tab ON tab.oid = dep.refobjid ",
            "JOIN pg_attribute col ON col.attrelid = tab.oid AND col.attnum = dep.refobjsubid ",
            "WHERE seq.relkind = 'S' AND nsp.nspname = '",
            safeSchema,
            "' ",
            "LOOP EXECUTE format(",
            "'SELECT setval(%L::regclass, COALESCE((SELECT MAX(%I) FROM %I.%I), 0) + 1, false)',",
            "format('%I.%I', r.schema_name, r.seq_name),",
            " r.col_name,",
            " r.schema_name,",
            " r.table_name);",
            " END LOOP; END $$;");
        return new NpgsqlCommand(sql, pg);
    }
}
