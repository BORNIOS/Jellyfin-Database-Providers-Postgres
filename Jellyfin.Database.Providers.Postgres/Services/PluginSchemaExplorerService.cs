using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Providers.Postgres.Services.Models;
using Npgsql;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// Provides a bounded read-only inventory of schemas registered through <c>IPluginSchemaHost</c>.
/// </summary>
/// <remarks>
/// The explorer deliberately reads only the provider registry and its registered schemas. It never treats
/// an arbitrary schema name as trusted input, and it does not expose the Jellyfin <c>public</c> schema.
/// </remarks>
public sealed class PluginSchemaExplorerService
{
    private const int MaximumPreviewRows = 200;

    /// <summary>Lists registered private schemas with table counts and their total on-disk size.</summary>
    /// <param name="connectionString">Active PostgreSQL connection string.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The registered private schemas.</returns>
    public async Task<IReadOnlyList<PluginSchemaInfo>> ListSchemasAsync(string connectionString, CancellationToken cancellationToken)
    {
        const string sql = "SELECT r.plugin_id, r.plugin_name, r.schema_name, r.protocol_version, r.created_at, r.last_seen_at, r.state, count(c.oid)::int, coalesce(pg_size_pretty(sum(pg_total_relation_size(c.oid))), '0 bytes') FROM jellyfin_provider.plugin_schemas r LEFT JOIN pg_namespace n ON n.nspname = r.schema_name LEFT JOIN pg_class c ON c.relnamespace = n.oid AND c.relkind IN ('r', 'p') GROUP BY r.plugin_id, r.plugin_name, r.schema_name, r.protocol_version, r.created_at, r.last_seen_at, r.state ORDER BY r.plugin_name, r.schema_name";
        var result = new List<PluginSchemaInfo>();
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new NpgsqlCommand(sql, connection);
        try
        {
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(new PluginSchemaInfo(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetDateTime(4), reader.GetDateTime(5), reader.GetString(6), reader.GetInt32(7), reader.GetString(8)));
            }
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            return result;
        }

        return result;
    }

    /// <summary>Lists tables that belong to one registered private schema.</summary>
    /// <param name="connectionString">Active PostgreSQL connection string.</param>
    /// <param name="schemaName">Registered private schema name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The schema's tables.</returns>
    public async Task<IReadOnlyList<PluginSchemaTableInfo>> ListTablesAsync(string connectionString, string schemaName, CancellationToken cancellationToken)
    {
        await EnsureRegisteredAsync(connectionString, schemaName, cancellationToken).ConfigureAwait(false);
        const string sql = "SELECT c.relname, pg_size_pretty(pg_total_relation_size(c.oid)) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = @schema AND c.relkind IN ('r', 'p') ORDER BY pg_total_relation_size(c.oid) DESC, c.relname";
        var result = new List<PluginSchemaTableInfo>();
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", schemaName);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new PluginSchemaTableInfo(reader.GetString(0), reader.GetString(1)));
        }

        return result;
    }

    /// <summary>Lists columns of one table inside a registered private schema.</summary>
    /// <param name="connectionString">Active PostgreSQL connection string.</param>
    /// <param name="schemaName">Registered private schema name.</param>
    /// <param name="tableName">Existing table name in that schema.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Column metadata ordered as stored.</returns>
    public async Task<IReadOnlyList<PluginSchemaColumnInfo>> ListColumnsAsync(string connectionString, string schemaName, string tableName, CancellationToken cancellationToken)
    {
        await EnsureTableAsync(connectionString, schemaName, tableName, cancellationToken).ConfigureAwait(false);
        const string sql = "SELECT column_name, data_type, is_nullable = 'YES', ordinal_position FROM information_schema.columns WHERE table_schema = @schema AND table_name = @table ORDER BY ordinal_position";
        var result = new List<PluginSchemaColumnInfo>();
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", schemaName);
        command.Parameters.AddWithValue("table", tableName);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new PluginSchemaColumnInfo(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetInt32(3)));
        }

        return result;
    }

    /// <summary>Reads a small preview from a verified private plugin table.</summary>
    /// <param name="connectionString">Active PostgreSQL connection string.</param>
    /// <param name="schemaName">Registered private schema name.</param>
    /// <param name="tableName">Existing table name in that schema.</param>
    /// <param name="limit">Requested number of rows, capped to a safe bound.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Columns and at most <paramref name="limit"/> rows.</returns>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Both identifiers are read from PostgreSQL catalog queries after verifying that the schema is registered and the table belongs to it. Values are not interpolated into this command.")]
    public async Task<PluginSchemaRows> PreviewRowsAsync(string connectionString, string schemaName, string tableName, int limit, CancellationToken cancellationToken)
    {
        await EnsureTableAsync(connectionString, schemaName, tableName, cancellationToken).ConfigureAwait(false);
        var boundedLimit = Math.Clamp(limit, 1, MaximumPreviewRows);
        var sql = string.Concat("SELECT * FROM ", QuoteIdentifier(schemaName), ".", QuoteIdentifier(tableName), " LIMIT @limit");
        var columns = new List<string>();
        var rows = new List<IReadOnlyList<string?>>();
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("limit", boundedLimit + 1);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
        {
            columns.Add(reader.GetName(ordinal));
        }

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && rows.Count <= boundedLimit)
        {
            var row = new string?[reader.FieldCount];
            for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            {
                row[ordinal] = await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false) ? null : FormatValue(reader.GetValue(ordinal));
            }

            rows.Add(row);
        }

        var truncated = rows.Count > boundedLimit;
        if (truncated)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return new PluginSchemaRows(columns, rows, truncated);
    }

    private static async Task EnsureRegisteredAsync(string connectionString, string schemaName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);
        const string sql = "SELECT EXISTS (SELECT 1 FROM jellyfin_provider.plugin_schemas WHERE schema_name = @schema)";
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", schemaName);
        var registered = (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        if (!registered)
        {
            throw new ArgumentException("El esquema solicitado no está registrado por PG Provider.", nameof(schemaName));
        }
    }

    private static async Task EnsureTableAsync(string connectionString, string schemaName, string tableName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        await EnsureRegisteredAsync(connectionString, schemaName, cancellationToken).ConfigureAwait(false);
        const string sql = "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = @schema AND table_name = @table AND table_type = 'BASE TABLE')";
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", schemaName);
        command.Parameters.AddWithValue("table", tableName);
        if (!(bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
        {
            throw new ArgumentException("La tabla solicitada no pertenece al esquema registrado.", nameof(tableName));
        }
    }

    private static string QuoteIdentifier(string identifier) => string.Concat("\"", identifier.Replace("\"", "\"\"", StringComparison.Ordinal), "\"");

    private static string FormatValue(object value)
        => value switch
        {
            byte[] bytes => string.Create(CultureInfo.InvariantCulture, $"byte[{bytes.Length}]"),
            Array array => string.Create(CultureInfo.InvariantCulture, $"[{array.Length} elementos]"),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };
}
