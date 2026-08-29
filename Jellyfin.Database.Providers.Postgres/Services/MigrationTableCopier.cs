using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// Copies rows from a SQLite table into the corresponding PostgreSQL table,
/// with type conversion and batch-insert retry logic.
/// </summary>
internal static class MigrationTableCopier
{
    /// <summary>
    /// Copies all rows of <paramref name="table"/> from SQLite into PostgreSQL.
    /// Returns the number of rows inserted.
    /// </summary>
    /// <param name="sqlite">Open SQLite source connection.</param>
    /// <param name="pg">Open PostgreSQL target connection.</param>
    /// <param name="schema">Target PostgreSQL schema.</param>
    /// <param name="table">Table name to copy.</param>
    /// <param name="columns">Columns to include in the copy.</param>
    /// <param name="batchSize">Number of rows per insert batch.</param>
    /// <param name="log">Callback for progress messages.</param>
    /// <returns>Number of rows successfully inserted.</returns>
    internal static async Task<long> CopyTableAsync(
        SqliteConnection sqlite,
        NpgsqlConnection pg,
        string schema,
        string table,
        IReadOnlyList<TargetColumn> columns,
        int batchSize,
        Action<string> log)
    {
        var colList = string.Join(", ", columns.Select(c => MigrationDiscovery.QuoteIdentifier(c.Name)));

        // selectSql uses QuoteIdentifier on names from sqlite_master (system catalog) — not user input (CA2100)
        var selectSql = $"SELECT {colList} FROM {MigrationDiscovery.QuoteIdentifier(table)};";
        var pgColumnList = string.Join(", ", columns.Select(c => MigrationDiscovery.QuoteIdentifier(c.Name)));
        var pgTable = $"{MigrationDiscovery.QuoteIdentifier(schema)}.{MigrationDiscovery.QuoteIdentifier(table)}";

        var effectiveBatchSize = Math.Max(1, Math.Min(batchSize, 65_535 / columns.Count));

        // Users table special handling: auto-fill NormalizedUsername if missing
        var normalizedUsernameIdx = -1;
        var usernameIdx = -1;
        if (string.Equals(table, "Users", StringComparison.OrdinalIgnoreCase))
        {
            for (var i = 0; i < columns.Count; i++)
            {
                if (string.Equals(columns[i].Name, "NormalizedUsername", StringComparison.OrdinalIgnoreCase))
                {
                    normalizedUsernameIdx = i;
                }

                if (string.Equals(columns[i].Name, "Username", StringComparison.OrdinalIgnoreCase))
                {
                    usernameIdx = i;
                }
            }
        }

        long total = 0;
        var rows = new List<object?[]>(effectiveBatchSize);

        using var selectCmd = CreateSqliteSelectCommand(sqlite, selectSql);
        using var reader = await selectCmd.ExecuteReaderAsync().ConfigureAwait(false);

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var values = new object?[columns.Count];
            for (var i = 0; i < columns.Count; i++)
            {
                values[i] = reader.GetValue(i);
            }

            if (normalizedUsernameIdx >= 0 && usernameIdx >= 0
                && values[normalizedUsernameIdx] is null or DBNull)
            {
                values[normalizedUsernameIdx] =
                    (values[usernameIdx]?.ToString() ?? string.Empty).ToUpperInvariant();
            }

            rows.Add(values);
            if (rows.Count >= effectiveBatchSize)
            {
                total += await InsertBatchAsync(pg, pgTable, pgColumnList, columns, rows, log)
                    .ConfigureAwait(false);
                rows.Clear();
            }
        }

        if (rows.Count > 0)
        {
            total += await InsertBatchAsync(pg, pgTable, pgColumnList, columns, rows, log)
                .ConfigureAwait(false);
        }

        return total;
    }

    // ── Batch insert ──────────────────────────────────────────────────────────

    private static async Task<long> InsertBatchAsync(
        NpgsqlConnection pg,
        string pgTable,
        string pgColumnList,
        IReadOnlyList<TargetColumn> columns,
        IReadOnlyList<object?[]> rows,
        Action<string> log)
    {
        using var tx = await pg.BeginTransactionAsync().ConfigureAwait(false);
        using var cmd = new NpgsqlCommand { Connection = pg, Transaction = tx };

        var valuesSql = new List<string>(rows.Count);
        var paramIndex = 0;

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var placeholders = new string[columns.Count];
            for (var colIndex = 0; colIndex < columns.Count; colIndex++)
            {
                var paramName = $"p{paramIndex++}";
                placeholders[colIndex] = $"@{paramName}";
                cmd.Parameters.Add(BuildParameter(paramName, rows[rowIndex][colIndex], columns[colIndex], pgTable, log));
            }

            valuesSql.Add($"({string.Join(", ", placeholders)})");
        }

        // pgTable + pgColumnList come from QuoteIdentifier on catalog data — not user input (CA2100)
        SetInsertCommandText(cmd, pgTable, pgColumnList, valuesSql);

        try
        {
            var inserted = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            await tx.CommitAsync().ConfigureAwait(false);
            var skipped = rows.Count - inserted;
            if (skipped > 0)
            {
                log($"  [SKIP] {pgTable}: {skipped} fila(s) omitidas (PK ya existe), {inserted} insertadas.");
            }

            return inserted;
        }
        catch (PostgresException pgEx)
        {
            await tx.RollbackAsync().ConfigureAwait(false);
            if (rows.Count == 1)
            {
                log($"  [SKIP] {pgTable}: fila saltada ({pgEx.SqlState}): {pgEx.MessageText}");
                return 0;
            }

            log($"  [WARN] {pgTable}: batch de {rows.Count} filas falló ({pgEx.SqlState}), reintentando fila por fila...");
            long total = 0;
            foreach (var row in rows)
            {
                total += await InsertBatchAsync(pg, pgTable, pgColumnList, columns, new[] { row }, log)
                    .ConfigureAwait(false);
            }

            return total;
        }
    }

    // ── Type conversion ───────────────────────────────────────────────────────

    private static NpgsqlParameter BuildParameter(
        string name, object? rawValue, TargetColumn column, string pgTable, Action<string> log)
    {
        var param = new NpgsqlParameter { ParameterName = name };

        if (rawValue is null or DBNull)
        {
            param.Value = DBNull.Value;
            return param;
        }

        if (rawValue is byte[] binaryValue)
        {
            param.NpgsqlDbType = NpgsqlDbType.Bytea;
            param.Value = binaryValue;
            return param;
        }

        var pgType = column.PgType;
        var strVal = Convert.ToString(rawValue, CultureInfo.InvariantCulture) ?? string.Empty;

        switch (pgType)
        {
            case "boolean":
                if (long.TryParse(strVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var boolLong))
                {
                    param.NpgsqlDbType = NpgsqlDbType.Boolean;
                    param.Value = boolLong != 0;
                }
                else if (bool.TryParse(strVal, out var boolVal))
                {
                    param.NpgsqlDbType = NpgsqlDbType.Boolean;
                    param.Value = boolVal;
                }
                else
                {
                    param.NpgsqlDbType = NpgsqlDbType.Boolean;
                    param.Value = DBNull.Value;
                    log($"  [WARN] {pgTable}.{column.Name}: boolean inválido '{strVal}' → NULL");
                }

                break;

            case "bigint":
            case "integer":
            case "smallint":
                if (long.TryParse(strVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longVal))
                {
                    param.NpgsqlDbType = NpgsqlDbType.Bigint;
                    param.Value = longVal;
                }
                else
                {
                    param.NpgsqlDbType = NpgsqlDbType.Bigint;
                    param.Value = DBNull.Value;
                    log($"  [WARN] {pgTable}.{column.Name}: {pgType} inválido '{strVal}' → NULL");
                }

                break;

            case "double precision":
            case "real":
            case "numeric":
                if (double.TryParse(strVal, NumberStyles.Float, CultureInfo.InvariantCulture, out var dblVal))
                {
                    param.NpgsqlDbType = NpgsqlDbType.Double;
                    param.Value = dblVal;
                }
                else
                {
                    param.NpgsqlDbType = NpgsqlDbType.Double;
                    param.Value = DBNull.Value;
                    log($"  [WARN] {pgTable}.{column.Name}: {pgType} inválido '{strVal}' → NULL");
                }

                break;

            case "timestamp without time zone":
            case "timestamp with time zone":
                SetTimestampParameter(param, strVal, pgType, pgTable, column.Name, log);
                break;

            case "ARRAY":
                var trimmed = strVal.Trim();
                if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                {
                    param.NpgsqlDbType = NpgsqlDbType.Unknown;
                    param.Value = '{' + trimmed[1..^1] + '}';
                }
                else
                {
                    param.NpgsqlDbType = NpgsqlDbType.Unknown;
                    param.Value = DBNull.Value;
                    log($"  [WARN] {pgTable}.{column.Name}: ARRAY inválido '{strVal}' → NULL");
                }

                break;

            default:
                var textVal = strVal;
                if (column.MaxLength.HasValue && textVal.Length > column.MaxLength.Value)
                {
                    textVal = textVal[..column.MaxLength.Value];
                }

                param.NpgsqlDbType = NpgsqlDbType.Unknown;
                param.Value = textVal;
                break;
        }

        return param;
    }

    private static void SetTimestampParameter(
        NpgsqlParameter param, string strVal, string pgType, string pgTable, string colName, Action<string> log)
    {
        var isTz = pgType == "timestamp with time zone";
        if (DateTime.TryParse(strVal, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dtVal))
        {
            if (isTz)
            {
                if (dtVal.Kind == DateTimeKind.Unspecified)
                {
                    dtVal = DateTime.SpecifyKind(dtVal, DateTimeKind.Utc);
                }
                else if (dtVal.Kind == DateTimeKind.Local)
                {
                    dtVal = dtVal.ToUniversalTime();
                }

                param.NpgsqlDbType = NpgsqlDbType.TimestampTz;
                param.Value = dtVal;
            }
            else
            {
                if (dtVal.Kind != DateTimeKind.Unspecified)
                {
                    dtVal = DateTime.SpecifyKind(dtVal, DateTimeKind.Unspecified);
                }

                param.NpgsqlDbType = NpgsqlDbType.Timestamp;
                param.Value = dtVal;
            }
        }
        else
        {
            param.NpgsqlDbType = isTz ? NpgsqlDbType.TimestampTz : NpgsqlDbType.Timestamp;
            param.Value = DBNull.Value;
            log($"  [WARN] {pgTable}.{colName}: timestamp inválido '{strVal}' → NULL");
        }
    }

    // ── Command factory methods (CA2100) ──────────────────────────────────────

    // selectSql is built from QuoteIdentifier on catalog-derived names, not user input.
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "selectSql built from QuoteIdentifier on sqlite_master catalog data, not user input.")]
    private static SqliteCommand CreateSqliteSelectCommand(SqliteConnection sqlite, string selectSql)
        => new SqliteCommand(selectSql, sqlite);

    // pgTable + pgColumnList come from QuoteIdentifier on catalog data, not user input.
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "pgTable/pgColumnList built from QuoteIdentifier on catalog data, not user input.")]
    private static void SetInsertCommandText(
        NpgsqlCommand cmd,
        string pgTable,
        string pgColumnList,
        System.Collections.Generic.List<string> valuesSql)
    {
        cmd.CommandText = string.Concat(
            "INSERT INTO ",
            pgTable,
            " (",
            pgColumnList,
            ") VALUES ",
            string.Join(", ", valuesSql),
            " ON CONFLICT DO NOTHING;");
    }
}
