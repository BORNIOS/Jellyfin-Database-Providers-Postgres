using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// Static helpers for <see cref="ExportToSqliteService"/>:
/// PostgreSQL catalog queries and SQLite DDL utilities.
/// </summary>
internal static class ExportHelpers
{
    // ── PostgreSQL catalog queries ────────────────────────────────────────────

    /// <summary>Lists all user tables in the public schema.</summary>
    /// <param name="conn">Open PostgreSQL connection.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of table names in the public schema.</returns>
    internal static async Task<List<string>> GetUserTablesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // Hardcoded SQL — no user input (CA2100)
        const string sql = "SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename;";
        var tables = new List<string>();
        using var cmd = new NpgsqlCommand(sql, conn);
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            tables.Add(r.GetString(0));
        }

        return tables;
    }

    /// <summary>Estimates total row count for the given tables using pg_class stats.</summary>
    /// <param name="conn">Open PostgreSQL connection.</param>
    /// <param name="tables">Table names to estimate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Approximate total row count across all specified tables.</returns>
    internal static async Task<long> EstimateTotalRowsAsync(
        NpgsqlConnection conn, List<string> tables, CancellationToken ct)
    {
        const string sql = "SELECT reltuples::bigint FROM pg_class WHERE relname = ANY(@tables) AND relkind = 'r';";
        long total = 0;
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("tables", tables.ToArray());
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            total += Math.Max(0, r.GetInt64(0));
        }

        return total;
    }

    /// <summary>Returns ordered column definitions for a table from information_schema.</summary>
    /// <param name="conn">Open PostgreSQL connection.</param>
    /// <param name="table">Table name to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Ordered list of column name and type pairs.</returns>
    internal static async Task<List<ColumnInfo>> GetColumnSchemaAsync(
        NpgsqlConnection conn, string table, CancellationToken ct)
    {
        const string colSql = @"
            SELECT column_name, data_type
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @t
            ORDER BY ordinal_position;";
        var cols = new List<ColumnInfo>();
        using var cmd = new NpgsqlCommand(colSql, conn);
        cmd.Parameters.AddWithValue("t", table);
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            cols.Add(new ColumnInfo(r.GetString(0), r.GetString(1)));
        }

        return cols;
    }

    /// <summary>Returns the primary-key column names for a table.</summary>
    /// <param name="conn">Open PostgreSQL connection.</param>
    /// <param name="table">Table name to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Ordered list of primary key column names.</returns>
    internal static async Task<List<string>> GetPrimaryKeyColumnsAsync(
        NpgsqlConnection conn, string table, CancellationToken ct)
    {
        const string pkSql = @"
            SELECT kcu.column_name
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
              ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema
            WHERE tc.table_schema = 'public' AND tc.table_name = @t AND tc.constraint_type = 'PRIMARY KEY'
            ORDER BY kcu.ordinal_position;";
        var pks = new List<string>();
        using var cmd = new NpgsqlCommand(pkSql, conn);
        cmd.Parameters.AddWithValue("t", table);
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            pks.Add(r.GetString(0));
        }

        return pks;
    }

    // ── SQLite introspection ──────────────────────────────────────────────────

    /// <summary>
    /// Returns the column names of a table in SQLite, or an empty list if the table does not exist.
    /// </summary>
    /// <param name="conn">Open SQLite connection.</param>
    /// <param name="table">Table name to inspect.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of column names present in the SQLite table, or empty if the table does not exist.</returns>
    internal static async Task<List<string>> GetSqliteTableColumnsAsync(
        SqliteConnection conn, string table, CancellationToken ct)
    {
        // First check if the table exists
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@t;";
        checkCmd.Parameters.AddWithValue("@t", table);
        var exists = Convert.ToInt64(
            await checkCmd.ExecuteScalarAsync(ct).ConfigureAwait(false),
            CultureInfo.InvariantCulture) > 0;

        if (!exists)
        {
            return new List<string>();
        }

        // Use PRAGMA table_info to get column names (table comes from pg_tables catalog, not user input)
        var cols = new List<string>();
        using var pragmaCmd = conn.CreateCommand();
        SetPragmaTableInfoCommand(pragmaCmd, table);
        using var r = await pragmaCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            cols.Add(r.GetString(r.GetOrdinal("name")));
        }

        return cols;
    }

    // ── SQLite DDL ────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the table in SQLite if it doesn't already exist,
    /// mirroring the PostgreSQL column types as SQLite affinities.
    /// </summary>
    /// <param name="pgConn">Open PostgreSQL connection used to read the primary key definition.</param>
    /// <param name="sqliteConn">Open SQLite connection where the table will be created.</param>
    /// <param name="table">Table name to create.</param>
    /// <param name="schema">Column definitions from PostgreSQL.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    internal static async Task EnsureTableExistsAsync(
        NpgsqlConnection pgConn,
        SqliteConnection sqliteConn,
        string table,
        List<ColumnInfo> schema,
        CancellationToken ct)
    {
        using var checkCmd = sqliteConn.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@t;";
        checkCmd.Parameters.AddWithValue("@t", table);
        var exists = Convert.ToInt64(
            await checkCmd.ExecuteScalarAsync(ct).ConfigureAwait(false),
            CultureInfo.InvariantCulture) > 0;

        if (exists)
        {
            return;
        }

        var pks = await GetPrimaryKeyColumnsAsync(pgConn, table, ct).ConfigureAwait(false);
        var pkSet = new HashSet<string>(pks, StringComparer.OrdinalIgnoreCase);

        var colDefs = schema.Select(c =>
        {
            var affinity = MapToSqliteAffinity(c.PgDataType);
            var pk = pkSet.Contains(c.Name) ? " PRIMARY KEY" : string.Empty;
            return $"    \"{c.Name}\" {affinity}{pk}";
        });

        var ddl = $"CREATE TABLE IF NOT EXISTS \"{table}\" ({Environment.NewLine}{string.Join($",{Environment.NewLine}", colDefs)}{Environment.NewLine});";
        await ExecSqliteAsync(sqliteConn, ddl, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a DDL/pragma SQL statement on the SQLite connection.
    /// <para>The <paramref name="sql"/> parameter must only be called with hardcoded
    /// or catalog-derived SQL (no user input). (CA2100).</para>
    /// </summary>
    /// <param name="conn">Open SQLite connection.</param>
    /// <param name="sql">Hardcoded or catalog-derived DDL/pragma statement.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "sql is always a hardcoded DDL statement or catalog-derived (CREATE TABLE/PRAGMA), never user input.")]
    internal static async Task ExecSqliteAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // CA2100: table name comes from pg_tables system catalog, not user input.
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "table comes from pg_tables system catalog, not user input. Identifier is quoted to prevent injection.")]
    private static void SetPragmaTableInfoCommand(SqliteCommand cmd, string table)
    {
        cmd.CommandText = string.Concat(
            "PRAGMA table_info(\"",
            table.Replace("\"", "\"\"", StringComparison.Ordinal),
            "\");");
    }

    // ── Type mapping ──────────────────────────────────────────────────────────

    private static string MapToSqliteAffinity(string pgDataType)
        => pgDataType.ToLowerInvariant() switch
        {
            "boolean" => "INTEGER",
            "integer" or "bigint" or "smallint" => "INTEGER",
            "real" or "double precision" or "numeric" or "decimal" => "REAL",
            "bytea" => "BLOB",
            "array" => "TEXT",
            _ => "TEXT",
        };
}
