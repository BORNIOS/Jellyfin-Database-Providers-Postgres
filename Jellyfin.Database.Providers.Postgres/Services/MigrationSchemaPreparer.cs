using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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
    /// Applies the embedded schema SQL if the target schema does not exist yet.
    /// </summary>
    /// <param name="pg">Open PostgreSQL connection.</param>
    /// <param name="schema">Target schema name.</param>
    /// <param name="log">Callback for progress messages.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    internal static async Task ApplySchemaIfNeededAsync(NpgsqlConnection pg, string schema, Action<string> log)
    {
        const string checkSql =
            "SELECT 1 FROM information_schema.tables WHERE table_schema = @s AND table_name = 'BaseItems' LIMIT 1;";

        object? exists;
        using var checkCmd = new NpgsqlCommand(checkSql, pg);
        checkCmd.Parameters.AddWithValue("s", schema);
        exists = await checkCmd.ExecuteScalarAsync().ConfigureAwait(false);

        if (exists is not null)
        {
            log("[Schema] Schema ya existe — omitiendo creación.");
            return;
        }

        log("[Schema] Creando schema desde SQL embebido...");

        const string resourceName = "Jellyfin.Database.Providers.Postgres.Resources.schema.sql";
        string schemaSql;
        using (var stream = typeof(MigrationSchemaPreparer).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}"))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            schemaSql = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        // Static separator array (CA1861: avoid constant array allocations per call)
        var statements = schemaSql
            .Split(MigrationService.SqlStatementSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().TrimEnd(';'))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        foreach (var statement in statements)
        {
            using var cmd = CreateSchemaStatementCommand(pg, statement);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        log($"[Schema] {statements.Count} sentencias aplicadas.");
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

    // Embedded-resource schema statements are never user input.
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "statement is sourced from embedded resource (schema.sql), not from user input.")]
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
