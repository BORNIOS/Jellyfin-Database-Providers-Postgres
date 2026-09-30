using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Providers.Postgres.Api;
using Jellyfin.Database.Providers.Postgres.Logging;
using MediaBrowser.Common.Configuration;
using Npgsql;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// Implements the private-schema storage contract without leaking PostgreSQL credentials or connections.
/// </summary>
[SuppressMessage(
    "Security",
    "CA2100:Review SQL queries for security vulnerabilities",
    Justification = "Schema identifiers are generated exclusively from a GUID. Consumer SQL is validated as one unqualified statement before command construction, and all values are bound parameters.")]
internal sealed partial class PostgresPluginSchemaHost : IPluginSchemaHost
{
    private const string MigrationTable = "__pg_provider_schema_migrations";
    private const string RegistrySchema = "jellyfin_provider";
    private const int MaxSqlLength = 65_536;

    private readonly Func<string?> _connectionStringFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresPluginSchemaHost"/> class using Jellyfin's active provider configuration.
    /// </summary>
    /// <param name="applicationPaths">Application paths used to read the active database configuration.</param>
    public PostgresPluginSchemaHost(IApplicationPaths applicationPaths)
        : this(() => PostgresPlugin.ReadActivePgConnectionString(applicationPaths)
            ?? PostgresPlugin.Instance?.Configuration.ConnectionString)
    {
    }

    internal PostgresPluginSchemaHost(Func<string?> connectionStringFactory)
    {
        _connectionStringFactory = connectionStringFactory ?? throw new ArgumentNullException(nameof(connectionStringFactory));
    }

    /// <inheritdoc/>
    public async Task<PluginSchema> OpenSchemaAsync(Guid pluginId, CancellationToken cancellationToken = default)
        => await OpenSchemaAsync(
            new PluginSchemaRequest(pluginId, string.Concat("plugin_", pluginId.ToString("N", CultureInfo.InvariantCulture))),
            cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<PluginSchema> OpenSchemaAsync(PluginSchemaRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PluginId == Guid.Empty)
        {
            throw new ArgumentException("A plugin identifier is required.", nameof(request));
        }

        ValidateSchemaName(request.SchemaName);
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureRegistryAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var existed = await SchemaExistsAsync(connection, transaction, request.SchemaName, cancellationToken).ConfigureAwait(false);
            using (var command = new NpgsqlCommand($"CREATE SCHEMA IF NOT EXISTS {QuoteIdentifier(request.SchemaName)};", connection, transaction))
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var schema = new PluginSchema(request.PluginId, request.SchemaName);
            await RegisterSchemaAsync(connection, transaction, request, existed, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            PostgresLog.Info($"[PluginSchema] Esquema privado disponible: {request.SchemaName}.");
            return schema;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<PluginSchema> MigratePreRegistrySchemaAsync(
        PluginSchemaRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PluginId == Guid.Empty)
        {
            throw new ArgumentException("A plugin identifier is required.", nameof(request));
        }

        ValidateSchemaName(request.SchemaName);
        var legacySchemaName = request.SchemaName[..request.SchemaName.LastIndexOf('_')];
        ValidateLegacySchemaName(legacySchemaName);

        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureRegistryAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (await SchemaExistsAsync(connection, transaction, request.SchemaName, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"Target schema '{request.SchemaName}' already exists; it will not be overwritten by a pre-registry migration.");
            }

            if (!await SchemaExistsAsync(connection, transaction, legacySchemaName, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"Pre-registry schema '{legacySchemaName}' does not exist.");
            }

            await EnsureLegacySchemaIsUnregisteredAsync(connection, transaction, legacySchemaName, cancellationToken).ConfigureAwait(false);
            using (var rename = new NpgsqlCommand($"ALTER SCHEMA {QuoteIdentifier(legacySchemaName)} RENAME TO {QuoteIdentifier(request.SchemaName)};", connection, transaction))
            {
                await rename.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await RegisterSchemaAsync(
                connection,
                transaction,
                request with { AdoptExistingSchema = true },
                existed: true,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            PostgresLog.Info($"[PluginSchema] Esquema pre-registro migrado: {legacySchemaName} -> {request.SchemaName}.");
            return new PluginSchema(request.PluginId, request.SchemaName);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task ApplyMigrationsAsync(
        PluginSchema schema,
        IReadOnlyList<PluginSchemaMigration> migrations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(migrations);
        ValidateSchema(schema);
        ValidateMigrations(migrations);

        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SetSchemaAsync(connection, transaction, schema, cancellationToken).ConfigureAwait(false);
            await EnsureMigrationTableAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await AcquireMigrationLockAsync(connection, transaction, schema, cancellationToken).ConfigureAwait(false);

            foreach (var migration in migrations)
            {
                var appliedChecksum = await GetAppliedChecksumAsync(connection, transaction, migration.Id, cancellationToken).ConfigureAwait(false);
                if (appliedChecksum is not null)
                {
                    if (!string.Equals(appliedChecksum, migration.Checksum, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"Applied migration '{migration.Id}' was modified.");
                    }

                    continue;
                }

                await ExecuteInTransactionAsync(connection, transaction, migration.Sql, null, cancellationToken).ConfigureAwait(false);
                await RecordMigrationAsync(connection, transaction, migration, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            PostgresLog.Info($"[PluginSchema] Migraciones aplicadas para {schema.Name}: {migrations.Count} declaradas.");
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            PostgresLog.Error($"[PluginSchema] Migración fallida para {schema.Name}", ex);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<int> ExecuteAsync(
        PluginSchema schema,
        string sql,
        IReadOnlyList<PluginSqlParameter>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ValidateSchema(schema);
        ValidateDataSql(sql);

        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SetSchemaAsync(connection, transaction, schema, cancellationToken).ConfigureAwait(false);
            var affected = await ExecuteInTransactionAsync(connection, transaction, sql, parameters, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return affected;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            PostgresLog.Error($"[PluginSchema] Escritura fallida para {schema.Name}", ex);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<T> QueryAsync<T>(
        PluginSchema schema,
        string sql,
        Func<DbDataReader, T> project,
        IReadOnlyList<PluginSqlParameter>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(project);
        ValidateSchema(schema);
        ValidateQuerySql(sql);

        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        try
        {
            await SetSchemaAsync(connection, transaction, schema, cancellationToken).ConfigureAwait(false);
            T result;
            using (var command = CreateCommand(connection, transaction, sql, parameters))
            using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                result = project(reader);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            PostgresLog.Error($"[PluginSchema] Consulta fallida para {schema.Name}", ex);
            throw;
        }
    }

    private static async Task<bool> SchemaExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string schemaName, CancellationToken cancellationToken)
    {
        using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = @schema);", connection, transaction);
        command.Parameters.AddWithValue("schema", schemaName);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static async Task EnsureRegistryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        using (var schema = new NpgsqlCommand($"CREATE SCHEMA IF NOT EXISTS {QuoteIdentifier(RegistrySchema)};", connection, transaction))
        {
            await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        const string sql = "CREATE TABLE IF NOT EXISTS jellyfin_provider.plugin_schemas (plugin_id uuid PRIMARY KEY, plugin_name text NOT NULL, schema_name text NOT NULL UNIQUE, protocol_version integer NOT NULL, created_at timestamp with time zone NOT NULL DEFAULT now(), last_seen_at timestamp with time zone NOT NULL DEFAULT now(), state text NOT NULL DEFAULT 'active')";
        await ExecuteInTransactionAsync(connection, transaction, sql, null, cancellationToken).ConfigureAwait(false);
        await ExecuteInTransactionAsync(connection, transaction, "ALTER TABLE jellyfin_provider.plugin_schemas ADD COLUMN IF NOT EXISTS include_analyze boolean NOT NULL DEFAULT true", null, cancellationToken).ConfigureAwait(false);
        await ExecuteInTransactionAsync(connection, transaction, "ALTER TABLE jellyfin_provider.plugin_schemas ADD COLUMN IF NOT EXISTS include_vacuum boolean NOT NULL DEFAULT false", null, cancellationToken).ConfigureAwait(false);
        await ExecuteInTransactionAsync(connection, transaction, "ALTER TABLE jellyfin_provider.plugin_schemas ADD COLUMN IF NOT EXISTS include_reindex boolean NOT NULL DEFAULT false", null, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RegisterSchemaAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PluginSchemaRequest request, bool existed, CancellationToken cancellationToken)
    {
        const string lookupSql = "SELECT plugin_id, schema_name FROM jellyfin_provider.plugin_schemas WHERE plugin_id = @pluginId OR schema_name = @schema;";
        using var lookup = new NpgsqlCommand(lookupSql, connection, transaction);
        lookup.Parameters.AddWithValue("pluginId", request.PluginId);
        lookup.Parameters.AddWithValue("schema", request.SchemaName);
        using var reader = await lookup.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        Guid? existingPluginId = null;
        string? existingSchema = null;
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            existingPluginId = reader.GetGuid(0);
            existingSchema = reader.GetString(1);
        }

        await reader.CloseAsync().ConfigureAwait(false);

        if (existingPluginId is not null
            && (existingPluginId != request.PluginId || !string.Equals(existingSchema, request.SchemaName, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Schema '{request.SchemaName}' is already bound to another plugin registration.");
        }

        if (existingPluginId is null && existed && !request.AdoptExistingSchema)
        {
            throw new InvalidOperationException($"Schema '{request.SchemaName}' already exists and must be explicitly adopted.");
        }

        var pluginName = request.SchemaName[..request.SchemaName.LastIndexOf('_')];
        const string upsertSql = "INSERT INTO jellyfin_provider.plugin_schemas (plugin_id, plugin_name, schema_name, protocol_version) VALUES (@pluginId, @pluginName, @schema, 1) ON CONFLICT (plugin_id) DO UPDATE SET last_seen_at = now(), state = 'active';";
        using var upsert = new NpgsqlCommand(upsertSql, connection, transaction);
        upsert.Parameters.AddWithValue("pluginId", request.PluginId);
        upsert.Parameters.AddWithValue("pluginName", pluginName);
        upsert.Parameters.AddWithValue("schema", request.SchemaName);
        await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureLegacySchemaIsUnregisteredAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string legacySchemaName, CancellationToken cancellationToken)
    {
        const string sql = "SELECT EXISTS (SELECT 1 FROM jellyfin_provider.plugin_schemas WHERE schema_name = @schema);";
        using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("schema", legacySchemaName);
        if ((bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
        {
            throw new InvalidOperationException($"Pre-registry schema '{legacySchemaName}' is already registered and cannot be claimed.");
        }
    }

    private static async Task SetSchemaAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PluginSchema schema, CancellationToken cancellationToken)
    {
        using var command = new NpgsqlCommand($"SET LOCAL search_path TO {QuoteIdentifier(schema.Name)}, pg_catalog;", connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureMigrationTableAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        const string sql = "CREATE TABLE IF NOT EXISTS \"__pg_provider_schema_migrations\" (\"Id\" text PRIMARY KEY, \"Checksum\" text NOT NULL, \"AppliedAt\" timestamp with time zone NOT NULL DEFAULT now())";
        await ExecuteInTransactionAsync(connection, transaction, sql, null, cancellationToken).ConfigureAwait(false);
    }

    private static async Task AcquireMigrationLockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PluginSchema schema, CancellationToken cancellationToken)
    {
        using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext(@schema));", connection, transaction);
        command.Parameters.AddWithValue("schema", schema.Name);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> GetAppliedChecksumAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string id, CancellationToken cancellationToken)
    {
        using var command = new NpgsqlCommand($"SELECT \"Checksum\" FROM {QuoteIdentifier(MigrationTable)} WHERE \"Id\" = @id;", connection, transaction);
        command.Parameters.AddWithValue("id", id);
        return (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RecordMigrationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PluginSchemaMigration migration, CancellationToken cancellationToken)
    {
        using var command = new NpgsqlCommand($"INSERT INTO {QuoteIdentifier(MigrationTable)} (\"Id\", \"Checksum\") VALUES (@id, @checksum);", connection, transaction);
        command.Parameters.AddWithValue("id", migration.Id);
        command.Parameters.AddWithValue("checksum", migration.Checksum);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ExecuteInTransactionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, IReadOnlyList<PluginSqlParameter>? parameters, CancellationToken cancellationToken)
    {
        using var command = CreateCommand(connection, transaction, sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static NpgsqlCommand CreateCommand(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, IReadOnlyList<PluginSqlParameter>? parameters)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        if (parameters is null)
        {
            return command;
        }

        foreach (var parameter in parameters)
        {
            ArgumentNullException.ThrowIfNull(parameter);
            if (!ParameterNameRegex().IsMatch(parameter.Name))
            {
                command.Dispose();
                throw new ArgumentException($"Invalid parameter name '{parameter.Name}'.", nameof(parameters));
            }

            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        return command;
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var configured = _connectionStringFactory();
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException("PostgreSQL is not the active Jellyfin database provider.");
        }

        var builder = PostgresDatabaseProvider.BuildTunedConnectionString(
            configured,
            PostgresPlugin.Instance?.Configuration,
            PostgresPlugin.Instance?.Configuration.CommandTimeout ?? 600);
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static void ValidateSchema(PluginSchema schema)
    {
        if (schema.PluginId == Guid.Empty)
        {
            throw new ArgumentException("The schema handle has no plugin owner.", nameof(schema));
        }

        ValidateSchemaName(schema.Name);
    }

    private static void ValidateSchemaName(string schemaName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);
        if (!SchemaNameRegex().IsMatch(schemaName) || string.Equals(schemaName, "public", StringComparison.Ordinal) || !SchemaGuidSuffixRegex().IsMatch(schemaName))
        {
            throw new ArgumentException("Schema names must use lowercase <plugin_name>_<guid-without-hyphens> format and cannot be public.", nameof(schemaName));
        }
    }

    private static void ValidateLegacySchemaName(string schemaName)
    {
        if (!SchemaNameRegex().IsMatch(schemaName)
            || string.Equals(schemaName, "public", StringComparison.Ordinal)
            || string.Equals(schemaName, RegistrySchema, StringComparison.Ordinal))
        {
            throw new ArgumentException("The derived pre-registry schema name is not eligible for migration.", nameof(schemaName));
        }
    }

    private static void ValidateMigrations(IReadOnlyList<PluginSchemaMigration> migrations)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var migration in migrations)
        {
            ArgumentNullException.ThrowIfNull(migration);
            if (string.IsNullOrWhiteSpace(migration.Id) || migration.Id.Length > 128 || !seen.Add(migration.Id))
            {
                throw new ArgumentException("Migration identifiers must be unique, non-empty, and at most 128 characters.", nameof(migrations));
            }

            ValidateDdlSql(migration.Sql);
        }
    }

    private static void ValidateDdlSql(string sql) => ValidateSql(sql, "CREATE", "ALTER", "DROP");

    private static void ValidateDataSql(string sql) => ValidateSql(sql, "WITH", "INSERT", "UPDATE", "DELETE");

    private static void ValidateQuerySql(string sql) => ValidateSql(sql, "SELECT", "WITH");

    private static void ValidateSql(string sql, params string[] allowedPrefixes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        var normalized = sql.Trim();
        if (normalized.EndsWith(';'))
        {
            normalized = normalized[..^1].TrimEnd();
        }

        if (normalized.Length == 0 || normalized.Length > MaxSqlLength || !allowedPrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("The SQL operation is not allowed by the private-schema contract.", nameof(sql));
        }

        var withoutExcludedReferences = ExcludedIdentifierRegex().Replace(normalized, string.Empty);
        if (normalized.Contains(';', StringComparison.Ordinal) || ForbiddenSqlRegex().IsMatch(normalized) || QualifiedIdentifierRegex().IsMatch(withoutExcludedReferences))
        {
            throw new ArgumentException("SQL must be one unqualified statement limited to the plugin's private schema.", nameof(sql));
        }
    }

    private static string QuoteIdentifier(string identifier) => string.Concat("\"", identifier.Replace("\"", "\"\"", StringComparison.Ordinal), "\"");

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterNameRegex();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaNameRegex();

    [GeneratedRegex("_[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaGuidSuffixRegex();

    [GeneratedRegex("(?:--|/\\*|\\*/|\\b(?:public|information_schema|pg_catalog|set_config|dblink|copy|vacuum|analyze|grant|revoke|create\\s+(?:role|database|schema|extension)|alter\\s+role|drop\\s+schema)\\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForbiddenSqlRegex();

    [GeneratedRegex("(?:\\\"[A-Za-z_][A-Za-z0-9_]*\\\"|[A-Za-z_][A-Za-z0-9_]*)\\s*\\.", RegexOptions.CultureInvariant)]
    private static partial Regex QualifiedIdentifierRegex();

    [GeneratedRegex("\\bexcluded\\s*\\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExcludedIdentifierRegex();
}
