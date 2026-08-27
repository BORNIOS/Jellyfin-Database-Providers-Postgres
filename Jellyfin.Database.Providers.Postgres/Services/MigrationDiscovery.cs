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
/// Table and column discovery helpers for SQLite and PostgreSQL.
/// </summary>
internal static class MigrationDiscovery
{
    // ── SQLite helpers ────────────────────────────────────────────────────────

    internal static async Task<List<string>> GetSqliteTablesAsync(SqliteConnection sqlite)
    {
        const string sql = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        var result = new List<string>();
        using var cmd = new SqliteCommand(sql, sqlite);
        using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    internal static async Task<List<string>> GetSqliteColumnNamesAsync(SqliteConnection sqlite, string table)
    {
        var result = new List<string>();
        using var cmd = CreatePragmaTableInfoCommand(sqlite, table);
        using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            result.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        return result;
    }

    // ── PostgreSQL helpers ────────────────────────────────────────────────────

    internal static async Task<Dictionary<string, (string PgType, int? MaxLength)>> GetPgColumnsAsync(
        NpgsqlConnection pg, string schema, string table)
    {
        const string sql = @"
            SELECT column_name, data_type, character_maximum_length
            FROM information_schema.columns
            WHERE table_schema = @schema AND table_name = @table
            ORDER BY ordinal_position;";
        var result = new Dictionary<string, (string, int?)>(StringComparer.OrdinalIgnoreCase);
        using var cmd = new NpgsqlCommand(sql, pg);
        cmd.Parameters.AddWithValue("schema", schema);
        cmd.Parameters.AddWithValue("table", table);
        using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var colName = reader.GetString(0);
            var dataType = reader.GetString(1);
            int? maxLen = await reader.IsDBNullAsync(2).ConfigureAwait(false) ? null : reader.GetInt32(2);
            result[colName] = (dataType, maxLen);
        }

        return result;
    }

    internal static async Task<List<string>> SortTablesByFkDependencyAsync(
        NpgsqlConnection pg, string schema, List<string> tables)
    {
        const string fkSql = @"
            SELECT DISTINCT
                tc.table_name      AS dependent_table,
                ccu.table_name     AS referenced_table
            FROM information_schema.table_constraints tc
            JOIN information_schema.referential_constraints rc
                ON tc.constraint_name = rc.constraint_name
                AND tc.table_schema = rc.constraint_schema
            JOIN information_schema.constraint_column_usage ccu
                ON rc.unique_constraint_name = ccu.constraint_name
                AND ccu.table_schema = @schema
            WHERE tc.constraint_type = 'FOREIGN KEY'
              AND tc.table_schema = @schema;";

        var edges = new List<(string From, string To)>();
        using var cmd = new NpgsqlCommand(fkSql, pg);
        cmd.Parameters.AddWithValue("schema", schema);
        using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var from = reader.GetString(0);
            var to = reader.GetString(1);
            if (!string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            {
                edges.Add((from, to));
            }
        }

        var tableSet = new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);
        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var adjacency = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tableSet)
        {
            inDegree[t] = 0;
            adjacency[t] = new List<string>();
        }

        foreach (var (from, to) in edges)
        {
            if (!tableSet.Contains(from) || !tableSet.Contains(to))
            {
                continue;
            }

            inDegree[from]++;
            adjacency[to].Add(from);
        }

        var queue = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key).OrderBy(t => t));
        var sorted = new List<string>(tables.Count);
        while (queue.Count > 0)
        {
            var t = queue.Dequeue();
            sorted.Add(t);
            foreach (var dep in adjacency[t].OrderBy(x => x))
            {
                if (--inDegree[dep] == 0)
                {
                    queue.Enqueue(dep);
                }
            }
        }

        foreach (var t in tables)
        {
            if (!sorted.Contains(t, StringComparer.OrdinalIgnoreCase))
            {
                sorted.Add(t);
            }
        }

        return sorted;
    }

    // ── Quoting helpers ───────────────────────────────────────────────────────

    internal static string QuoteIdentifier(string input)
        => $"\"{input.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    // ── Command factory (CA2100: table from sqlite_master, not user input) ────

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "table comes from sqlite_master system catalog, not user input. Identifier is quoted to prevent injection.")]
    private static SqliteCommand CreatePragmaTableInfoCommand(SqliteConnection sqlite, string table)
    {
        var sql = string.Concat("PRAGMA table_info(\"", table.Replace("\"", "\"\"", StringComparison.Ordinal), "\");");
        return new SqliteCommand(sql, sqlite);
    }
}
