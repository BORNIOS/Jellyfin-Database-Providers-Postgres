using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Providers.Postgres.Logging;
using Jellyfin.Database.Providers.Postgres.Services.Models;
using Npgsql;

#pragma warning disable SA1611, SA1615

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// Performs explicitly requested maintenance on schemas registered through <c>IPluginSchemaHost</c>.
/// </summary>
/// <remarks>
/// This service is intentionally separate from database-wide maintenance. Every public operation verifies
/// the schema in the provider registry, and table operations verify the table in that schema before an
/// identifier is quoted into SQL. It never accepts <c>public</c> or arbitrary PostgreSQL schemas.
/// </remarks>
public sealed class PluginSchemaMaintenanceService
{
    private const long AnalyzeMinimumChanges = 1_000;
    private const long VacuumMinimumDeadTuples = 1_000;

    /// <summary>Gets operational statistics for one registered private schema.</summary>
    public async Task<PluginSchemaMaintenanceReport> GetReportAsync(string connectionString, string schemaName, CancellationToken cancellationToken)
    {
        await EnsureRegisteredAsync(connectionString, schemaName, cancellationToken).ConfigureAwait(false);
        var policy = await ReadPolicyAsync(connectionString, schemaName, cancellationToken).ConfigureAwait(false);
        const string sql = @"
SELECT c.relname,
       GREATEST(c.reltuples::bigint, 0),
       pg_size_pretty(pg_total_relation_size(c.oid)),
       pg_size_pretty(pg_total_relation_size(c.oid) - pg_relation_size(c.oid)),
       COALESCE(s.n_dead_tup, 0)::bigint,
       COALESCE(s.n_mod_since_analyze, 0)::bigint,
       s.last_analyze,
       s.last_autoanalyze,
       s.last_vacuum,
       s.last_autovacuum,
       COALESCE(i.index_count, 0)::int
FROM pg_class c
JOIN pg_namespace n ON n.oid = c.relnamespace
LEFT JOIN pg_stat_all_tables s ON s.relid = c.oid
LEFT JOIN LATERAL (
    SELECT count(*)::int AS index_count
    FROM pg_index ix
    WHERE ix.indrelid = c.oid
) i ON true
WHERE n.nspname = @schema
  AND c.relkind IN ('r', 'p')
ORDER BY pg_total_relation_size(c.oid) DESC, c.relname";

        var tables = new List<PluginSchemaMaintenanceTable>();
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", schemaName);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var estimatedRows = reader.GetInt64(1);
            var deadTuples = reader.GetInt64(4);
            var modifiedSinceAnalyze = reader.GetInt64(5);
            var needsAnalyze = modifiedSinceAnalyze >= Math.Max(AnalyzeMinimumChanges, estimatedRows / 10);
            var needsVacuum = deadTuples >= Math.Max(VacuumMinimumDeadTuples, estimatedRows / 5);
            tables.Add(new PluginSchemaMaintenanceTable(
                reader.GetString(0),
                estimatedRows,
                reader.GetString(2),
                reader.GetString(3),
                deadTuples,
                modifiedSinceAnalyze,
                ReadNullableDateTime(reader, 6),
                ReadNullableDateTime(reader, 7),
                ReadNullableDateTime(reader, 8),
                ReadNullableDateTime(reader, 9),
                reader.GetInt32(10),
                needsAnalyze,
                needsVacuum));
        }

        return new PluginSchemaMaintenanceReport(
            schemaName,
            tables,
            tables.Count(static table => table.NeedsAnalyze),
            tables.Count(static table => table.NeedsVacuum),
            policy.IncludeAnalyze,
            policy.IncludeVacuum,
            policy.IncludeReindex,
            DateTime.UtcNow);
    }

    /// <summary>Updates the administrator-controlled scheduled-maintenance policy for one registered schema.</summary>
    public async Task<PluginSchemaMaintenancePolicy> UpdatePolicyAsync(string connectionString, string schemaName, PluginSchemaMaintenancePolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        await EnsureRegisteredAsync(connectionString, schemaName, cancellationToken).ConfigureAwait(false);
        const string sql = "UPDATE jellyfin_provider.plugin_schemas SET include_analyze = @analyze, include_vacuum = @vacuum, include_reindex = @reindex WHERE schema_name = @schema";
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("analyze", policy.IncludeAnalyze);
        command.Parameters.AddWithValue("vacuum", policy.IncludeVacuum);
        command.Parameters.AddWithValue("reindex", policy.IncludeReindex);
        command.Parameters.AddWithValue("schema", schemaName);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new ArgumentException("El esquema solicitado no está registrado por PG Provider.", nameof(schemaName));
        }

        PostgresLog.Info($"[PluginSchemas] Política actualizada: esquema={schemaName}, analyze={policy.IncludeAnalyze}, vacuum={policy.IncludeVacuum}, reindex={policy.IncludeReindex}.");
        return policy;
    }

    /// <summary>Lists registered schemas opted into a scheduled private-schema operation.</summary>
    public async Task<IReadOnlyList<string>> ListScheduledSchemasAsync(string connectionString, string policyColumn, CancellationToken cancellationToken)
    {
        var sql = policyColumn switch
        {
            "analyze" => "SELECT schema_name FROM jellyfin_provider.plugin_schemas WHERE include_analyze = true ORDER BY schema_name",
            "vacuum" => "SELECT schema_name FROM jellyfin_provider.plugin_schemas WHERE include_vacuum = true ORDER BY schema_name",
            "reindex" => "SELECT schema_name FROM jellyfin_provider.plugin_schemas WHERE include_reindex = true ORDER BY schema_name",
            _ => throw new ArgumentException("Política de mantenimiento no reconocida.", nameof(policyColumn)),
        };
        var schemas = new List<string>();
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new NpgsqlCommand(sql, connection);
        try
        {
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                schemas.Add(reader.GetString(0));
            }
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            return schemas;
        }

        return schemas;
    }

    /// <summary>Runs ANALYZE for every table in one registered private schema.</summary>
    public Task<PluginSchemaMaintenanceResult> AnalyzeAsync(string connectionString, string schemaName, CancellationToken cancellationToken)
        => RunSchemaOperationAsync(connectionString, schemaName, "ANALYZE", "ANALYZE", cancellationToken);

    /// <summary>Runs VACUUM (ANALYZE) for one verified private-plugin table.</summary>
    public Task<PluginSchemaMaintenanceResult> VacuumAnalyzeTableAsync(string connectionString, string schemaName, string tableName, CancellationToken cancellationToken)
        => RunTableOperationAsync(connectionString, schemaName, tableName, "VACUUM (ANALYZE)", "VACUUM (ANALYZE)", cancellationToken);

    /// <summary>Rebuilds the indexes for one verified private-plugin table without blocking normal writes.</summary>
    public Task<PluginSchemaMaintenanceResult> ReindexTableAsync(string connectionString, string schemaName, string tableName, CancellationToken cancellationToken)
        => RunTableOperationAsync(connectionString, schemaName, tableName, "REINDEX CONCURRENTLY", "REINDEX TABLE CONCURRENTLY", cancellationToken);

    private static async Task<PluginSchemaMaintenanceResult> RunSchemaOperationAsync(
        string connectionString,
        string schemaName,
        string operation,
        string statement,
        CancellationToken cancellationToken)
    {
        var registeredSchema = await ResolveRegisteredSchemaAsync(connectionString, schemaName, cancellationToken).ConfigureAwait(false);
        var tableNames = await ListTablesAsync(connectionString, registeredSchema, cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await AcquireSchemaLockAsync(connection, registeredSchema, cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var tableName in tableNames)
            {
                await ExecuteMaintenanceAsync(connection, statement, registeredSchema, tableName, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await ReleaseSchemaLockAsync(connection, registeredSchema).ConfigureAwait(false);
        }

        stopwatch.Stop();
        PostgresLog.Info($"[PluginSchemas] {operation} completado: esquema={registeredSchema}, tablas={tableNames.Count}, duracion={stopwatch.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s.");
        return new PluginSchemaMaintenanceResult(operation, registeredSchema, null, tableNames.Count, stopwatch.ElapsedMilliseconds);
    }

    private static async Task<PluginSchemaMaintenanceResult> RunTableOperationAsync(
        string connectionString,
        string schemaName,
        string tableName,
        string operation,
        string statement,
        CancellationToken cancellationToken)
    {
        var table = await ResolveTableAsync(connectionString, schemaName, tableName, cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await AcquireSchemaLockAsync(connection, table.SchemaName, cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteMaintenanceAsync(connection, statement, table.SchemaName, table.TableName, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseSchemaLockAsync(connection, table.SchemaName).ConfigureAwait(false);
        }

        stopwatch.Stop();
        PostgresLog.Info($"[PluginSchemas] {operation} completado: esquema={table.SchemaName}, tabla={table.TableName}, duracion={stopwatch.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s.");
        return new PluginSchemaMaintenanceResult(operation, table.SchemaName, table.TableName, 1, stopwatch.ElapsedMilliseconds);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The statement is an internal constant and both identifiers are re-read from PostgreSQL catalog rows after parameterized registry and table membership checks.")]
    private static async Task ExecuteMaintenanceAsync(NpgsqlConnection connection, string statement, string schemaName, string tableName, CancellationToken cancellationToken)
    {
        var target = string.Concat(MaintenanceService.QuoteIdentifier(schemaName), ".", MaintenanceService.QuoteIdentifier(tableName));
        using var command = new NpgsqlCommand(string.Concat(statement, " ", target), connection) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<string>> ListTablesAsync(string connectionString, string schemaName, CancellationToken cancellationToken)
    {
        const string sql = "SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = @schema AND c.relkind IN ('r', 'p') ORDER BY c.relname";
        var names = new List<string>();
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", schemaName);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task EnsureRegisteredAsync(string connectionString, string schemaName, CancellationToken cancellationToken)
        => _ = await ResolveRegisteredSchemaAsync(connectionString, schemaName, cancellationToken).ConfigureAwait(false);

    private static async Task<string> ResolveRegisteredSchemaAsync(string connectionString, string schemaName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);
        const string sql = "SELECT schema_name FROM jellyfin_provider.plugin_schemas WHERE schema_name = @schema";
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", schemaName);
        var registeredSchema = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (string.IsNullOrWhiteSpace(registeredSchema))
        {
            throw new ArgumentException("El esquema solicitado no está registrado por PG Provider.", nameof(schemaName));
        }

        return registeredSchema;
    }

    private static async Task<PluginSchemaMaintenancePolicy> ReadPolicyAsync(string connectionString, string schemaName, CancellationToken cancellationToken)
    {
        const string sql = "SELECT include_analyze, include_vacuum, include_reindex FROM jellyfin_provider.plugin_schemas WHERE schema_name = @schema";
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", schemaName);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new ArgumentException("El esquema solicitado no está registrado por PG Provider.", nameof(schemaName));
        }

        return new PluginSchemaMaintenancePolicy(reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2));
    }

    private static async Task<(string SchemaName, string TableName)> ResolveTableAsync(string connectionString, string schemaName, string tableName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        var registeredSchema = await ResolveRegisteredSchemaAsync(connectionString, schemaName, cancellationToken).ConfigureAwait(false);
        const string sql = "SELECT table_schema, table_name FROM information_schema.tables WHERE table_schema = @schema AND table_name = @table AND table_type = 'BASE TABLE'";
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", registeredSchema);
        command.Parameters.AddWithValue("table", tableName);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new ArgumentException("La tabla solicitada no pertenece al esquema registrado.", nameof(tableName));
        }

        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task AcquireSchemaLockAsync(NpgsqlConnection connection, string schemaName, CancellationToken cancellationToken)
    {
        const string sql = "SELECT pg_try_advisory_lock(hashtextextended(@schema, 0))";
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", schemaName);
        if (!(bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
        {
            throw new InvalidOperationException("Ya hay una operación de mantenimiento en curso para este esquema.");
        }
    }

    private static async Task ReleaseSchemaLockAsync(NpgsqlConnection connection, string schemaName)
    {
        try
        {
            using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended(@schema, 0))", connection);
            command.Parameters.AddWithValue("schema", schemaName);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PostgresLog.Warn($"[PluginSchemas] No se pudo liberar el bloqueo de mantenimiento para {schemaName}: {ex.Message}");
        }
    }

    private static DateTime? ReadNullableDateTime(NpgsqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
}
#pragma warning restore SA1611, SA1615
