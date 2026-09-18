using System;
using System.Collections.Generic;
using System.Linq;

using Jellyfin.Database.Providers.Postgres.Logging;
using Jellyfin.Plugin.JellyTrend.Api;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// Persists JellyTrend's own data (feature cache, trending list, recommendations, history) in a
/// dedicated <c>jellytrend</c> schema of the Jellyfin database.
/// </summary>
/// <remarks>
/// <para>
/// The schema lives outside <c>public</c> on purpose: the PostgreSQL to SQLite export enumerates
/// <c>pg_tables</c> of the <c>public</c> schema, so these tables can never end up inside a SQLite file
/// that Jellyfin would open without them.
/// </para>
/// <para>
/// Nothing is created until JellyTrend calls <see cref="EnsureSchema"/>, so a server without that plugin
/// never gets these tables. The features of an item are stored as one <c>jsonb</c> document instead of
/// modelling every facet in SQL: the provider does not need to understand them to keep them, and a GIN
/// index still allows querying inside the document later.
/// </para>
/// <para>
/// No method throws: a broken store degrades to "no data" so JellyTrend can fall back to its JSON files
/// and the task still finishes.
/// </para>
/// </remarks>
public sealed class JellyTrendPostgresStore : IJellyTrendStoreProvider
{
    /// <summary>Version of the schema this class creates.</summary>
    public const int CurrentSchemaVersion = 2;

    private const string SchemaName = "jellytrend";

    private const string SchemaSql = """
        CREATE SCHEMA IF NOT EXISTS jellytrend;

        CREATE TABLE IF NOT EXISTS jellytrend.schema_version (
            version    integer PRIMARY KEY,
            applied_at timestamptz NOT NULL DEFAULT now());

        CREATE TABLE IF NOT EXISTS jellytrend.item_features (
            item_id      uuid PRIMARY KEY,
            facets       jsonb NOT NULL,
            content_hash text NOT NULL,
            computed_at  timestamptz NOT NULL DEFAULT now());

        CREATE INDEX IF NOT EXISTS ix_jt_item_features_facets
            ON jellytrend.item_features USING gin (facets);

        CREATE TABLE IF NOT EXISTS jellytrend.trending_item (
            item_id    uuid PRIMARY KEY,
            entry      jsonb NOT NULL,
            media_type text,
            synced_at  timestamptz NOT NULL);

        CREATE TABLE IF NOT EXISTS jellytrend.user_recommendation (
            user_id      uuid NOT NULL,
            item_id      uuid NOT NULL,
            rank         integer NOT NULL,
            score        double precision NOT NULL DEFAULT 0,
            run_id       uuid NOT NULL,
            generated_at timestamptz NOT NULL,
            PRIMARY KEY (user_id, item_id));

        CREATE INDEX IF NOT EXISTS ix_jt_user_recommendation_rank
            ON jellytrend.user_recommendation (user_id, rank);

        CREATE TABLE IF NOT EXISTS jellytrend.user_suppression (
            user_id uuid NOT NULL,
            item_id uuid NOT NULL,
            reason  text NOT NULL,
            PRIMARY KEY (user_id, item_id));

        CREATE TABLE IF NOT EXISTS jellytrend.sync_run (
            run_id       uuid PRIMARY KEY,
            kind         text NOT NULL,
            started_at   timestamptz NOT NULL,
            finished_at  timestamptz,
            status       text,
            users_ok     integer NOT NULL DEFAULT 0,
            users_failed integer NOT NULL DEFAULT 0,
            duration_ms  integer,
            message      text);

        CREATE TABLE IF NOT EXISTS jellytrend.user_item_consumption (
            user_id    uuid NOT NULL,
            item_id    uuid NOT NULL,
            data       jsonb NOT NULL,
            updated_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (user_id, item_id));
        """;

    private readonly IApplicationPaths _applicationPaths;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyTrendPostgresStore"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths, used to read the active connection string.</param>
    /// <param name="loggerFactory">Logger factory supplied by the server.</param>
    public JellyTrendPostgresStore(IApplicationPaths applicationPaths, ILoggerFactory loggerFactory)
    {
        _applicationPaths = applicationPaths;
        _ = loggerFactory;
    }

    /// <summary>
    /// Gets the connection string Jellyfin is using right now, or null when PostgreSQL is not active.
    /// </summary>
    private string? ConnectionString
        => PostgresPlugin.ReadActivePgConnectionString(_applicationPaths);

    /// <inheritdoc/>
    public bool EnsureSchema()
    {
        PostgresLog.Info($"[JellyTrend] Preparando el esquema '{SchemaName}' para los datos del plugin.");

        var created = Run(
            conn =>
            {
                using var command = new NpgsqlCommand(SchemaSql, conn) { CommandTimeout = 120 };
                command.ExecuteNonQuery();

                using var version = new NpgsqlCommand(
                    "INSERT INTO jellytrend.schema_version (version) VALUES (@v) ON CONFLICT (version) DO NOTHING;",
                    conn);
                version.Parameters.AddWithValue("v", CurrentSchemaVersion);
                version.ExecuteNonQuery();
                return true;
            },
            false,
            "EnsureSchema");

        PostgresLog.Info(created
            ? $"[JellyTrend] Esquema listo ({DescribeBackend()})."
            : "[JellyTrend] No se pudo preparar el esquema; se usaran los archivos JSON.");

        return created;
    }

    /// <inheritdoc/>
    public int GetSchemaVersion()
        => Run(
            conn =>
            {
                using var command = new NpgsqlCommand("SELECT max(version) FROM jellytrend.schema_version;", conn);
                var value = command.ExecuteScalar();
                return value is null or DBNull ? 0 : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
            },
            0,
            "GetSchemaVersion");

    /// <inheritdoc/>
    public string DescribeBackend()
        => Run(
            conn =>
            {
                using var command = new NpgsqlCommand(
                    $"SELECT 'PostgreSQL ' || current_setting('server_version') || ', esquema {SchemaName}';",
                    conn);
                return command.ExecuteScalar() as string ?? $"esquema {SchemaName}";
            },
            $"esquema {SchemaName} (sin conexion)",
            "DescribeBackend");

    /// <inheritdoc/>
    public int ReplaceItemFeatures(Guid[] itemIds, string[] facetsJson, string[] contentHashes)
    {
        ArgumentNullException.ThrowIfNull(itemIds);

        if (itemIds.Length == 0)
        {
            return 0;
        }

        return Run(
            conn =>
            {
                using var transaction = conn.BeginTransaction();
                var written = 0;

                using var command = new NpgsqlCommand(
                    """
                    INSERT INTO jellytrend.item_features (item_id, facets, content_hash, computed_at)
                    VALUES (@id, @facets, @hash, now())
                    ON CONFLICT (item_id) DO UPDATE
                        SET facets = EXCLUDED.facets,
                            content_hash = EXCLUDED.content_hash,
                            computed_at = now();
                    """,
                    conn,
                    transaction);

                var idParameter = command.Parameters.Add("id", NpgsqlDbType.Uuid);
                var facetsParameter = command.Parameters.Add("facets", NpgsqlDbType.Jsonb);
                var hashParameter = command.Parameters.Add("hash", NpgsqlDbType.Text);

                for (var i = 0; i < itemIds.Length; i++)
                {
                    if (itemIds[i] == Guid.Empty)
                    {
                        continue;
                    }

                    idParameter.Value = itemIds[i];
                    facetsParameter.Value = i < facetsJson.Length ? facetsJson[i] : "{}";
                    hashParameter.Value = i < contentHashes.Length ? contentHashes[i] : string.Empty;
                    written += command.ExecuteNonQuery();
                }

                transaction.Commit();
                return written;
            },
            0,
            "ReplaceItemFeatures");
    }

    /// <inheritdoc/>
    public string?[] GetItemFeatures(Guid[] itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);

        var missing = new string?[itemIds.Length];

        if (itemIds.Length == 0)
        {
            return missing;
        }

        var found = Run(
            conn =>
            {
                using var command = new NpgsqlCommand(
                    "SELECT item_id, facets FROM jellytrend.item_features WHERE item_id = ANY(@ids);",
                    conn);
                command.Parameters.AddWithValue("ids", itemIds);

                var rows = new Dictionary<Guid, string>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows[reader.GetGuid(0)] = reader.GetString(1);
                }

                return rows;
            },
            new Dictionary<Guid, string>(),
            "GetItemFeatures");

        for (var i = 0; i < itemIds.Length; i++)
        {
            if (found.TryGetValue(itemIds[i], out var facets))
            {
                missing[i] = facets;
            }
        }

        return missing;
    }

    /// <inheritdoc/>
    public string? GetItemFeaturesJson()
        => Run(
            conn =>
            {
                // El documento lo arma PostgreSQL: una sola consulta y sin serializar a mano.
                using var command = new NpgsqlCommand(
                    "SELECT jsonb_object_agg(item_id::text, facets) FROM jellytrend.item_features;",
                    conn);
                var value = command.ExecuteScalar();
                return value is null or DBNull ? null : value as string;
            },
            null,
            "GetItemFeaturesJson");

    /// <inheritdoc/>
    public void ReplaceTrending(Guid[] itemIds, string[] entriesJson, DateTime syncedAt)
    {
        ArgumentNullException.ThrowIfNull(itemIds);

        Run(
            conn =>
            {
                using var transaction = conn.BeginTransaction();

                using (var clear = new NpgsqlCommand("DELETE FROM jellytrend.trending_item;", conn, transaction))
                {
                    clear.ExecuteNonQuery();
                }

                using (var command = new NpgsqlCommand(
                    """
                    INSERT INTO jellytrend.trending_item (item_id, entry, media_type, synced_at)
                    VALUES (@id, @entry, @media, @synced)
                    ON CONFLICT (item_id) DO UPDATE
                        SET entry = EXCLUDED.entry,
                            media_type = EXCLUDED.media_type,
                            synced_at = EXCLUDED.synced_at;
                    """,
                    conn,
                    transaction))
                {
                    var idParameter = command.Parameters.Add("id", NpgsqlDbType.Uuid);
                    var entryParameter = command.Parameters.Add("entry", NpgsqlDbType.Jsonb);
                    var mediaParameter = command.Parameters.Add("media", NpgsqlDbType.Text);
                    var syncedParameter = command.Parameters.Add("synced", NpgsqlDbType.TimestampTz);
                    syncedParameter.Value = DateTime.SpecifyKind(syncedAt, DateTimeKind.Utc);

                    for (var i = 0; i < itemIds.Length; i++)
                    {
                        if (itemIds[i] == Guid.Empty)
                        {
                            continue;
                        }

                        idParameter.Value = itemIds[i];
                        entryParameter.Value = i < entriesJson.Length ? entriesJson[i] : "{}";
                        mediaParameter.Value = ReadMediaType(entriesJson, i);
                        command.ExecuteNonQuery();
                    }
                }

                transaction.Commit();
                return true;
            },
            false,
            "ReplaceTrending");
    }

    /// <inheritdoc/>
    public string? GetTrendingJson()
        => Run(
            conn =>
            {
                // PostgreSQL arma el documento: mismas claves que el archivo JSON, sin serializar a mano.
                using var command = new NpgsqlCommand(
                    """
                    SELECT jsonb_build_object(
                               'Items', jsonb_agg(entry ORDER BY item_id),
                               'LastUpdated', max(synced_at))
                    FROM jellytrend.trending_item;
                    """,
                    conn);
                var value = command.ExecuteScalar();
                return value is null or DBNull ? null : value as string;
            },
            null,
            "GetTrendingJson");

    /// <inheritdoc/>
    public void ReplaceUserRecommendations(Guid userId, Guid[] itemIds, double[] scores, DateTime generatedAt, Guid runId)
    {
        ArgumentNullException.ThrowIfNull(itemIds);

        Run(
            conn =>
            {
                using var transaction = conn.BeginTransaction();

                using (var clear = new NpgsqlCommand(
                    "DELETE FROM jellytrend.user_recommendation WHERE user_id = @user;",
                    conn,
                    transaction))
                {
                    clear.Parameters.AddWithValue("user", userId);
                    clear.ExecuteNonQuery();
                }

                using (var command = new NpgsqlCommand(
                    """
                    INSERT INTO jellytrend.user_recommendation (user_id, item_id, rank, score, run_id, generated_at)
                    VALUES (@user, @item, @rank, @score, @run, @generated)
                    ON CONFLICT (user_id, item_id) DO UPDATE
                        SET rank = EXCLUDED.rank,
                            score = EXCLUDED.score,
                            run_id = EXCLUDED.run_id,
                            generated_at = EXCLUDED.generated_at;
                    """,
                    conn,
                    transaction))
                {
                    var itemParameter = command.Parameters.Add("item", NpgsqlDbType.Uuid);
                    var rankParameter = command.Parameters.Add("rank", NpgsqlDbType.Integer);
                    var scoreParameter = command.Parameters.Add("score", NpgsqlDbType.Double);
                    command.Parameters.AddWithValue("user", userId);
                    command.Parameters.AddWithValue("run", runId);
                    command.Parameters.AddWithValue("generated", DateTime.SpecifyKind(generatedAt, DateTimeKind.Utc));

                    for (var i = 0; i < itemIds.Length; i++)
                    {
                        if (itemIds[i] == Guid.Empty)
                        {
                            continue;
                        }

                        itemParameter.Value = itemIds[i];
                        rankParameter.Value = i;
                        scoreParameter.Value = i < scores.Length ? scores[i] : 0d;
                        command.ExecuteNonQuery();
                    }
                }

                transaction.Commit();
                return true;
            },
            false,
            "ReplaceUserRecommendations");
    }

    /// <inheritdoc/>
    public Guid[] GetUserRecommendations(Guid userId)
        => Run(
            conn =>
            {
                using var command = new NpgsqlCommand(
                    "SELECT item_id FROM jellytrend.user_recommendation WHERE user_id = @user ORDER BY rank;",
                    conn);
                command.Parameters.AddWithValue("user", userId);
                return ReadGuids(command);
            },
            [],
            "GetUserRecommendations");

    /// <inheritdoc/>
    public Guid[] GetUsersWithRecommendations()
        => Run(
            conn =>
            {
                using var command = new NpgsqlCommand(
                    "SELECT user_id FROM jellytrend.user_recommendation GROUP BY user_id ORDER BY max(generated_at) DESC;",
                    conn);
                return ReadGuids(command);
            },
            [],
            "GetUsersWithRecommendations");

    /// <inheritdoc/>
    public void ReplaceSuppressed(Guid userId, Guid[] itemIds, string[] reasons)
    {
        ArgumentNullException.ThrowIfNull(itemIds);

        Run(
            conn =>
            {
                using var transaction = conn.BeginTransaction();

                using (var clear = new NpgsqlCommand(
                    "DELETE FROM jellytrend.user_suppression WHERE user_id = @user;",
                    conn,
                    transaction))
                {
                    clear.Parameters.AddWithValue("user", userId);
                    clear.ExecuteNonQuery();
                }

                using (var command = new NpgsqlCommand(
                    """
                    INSERT INTO jellytrend.user_suppression (user_id, item_id, reason)
                    VALUES (@user, @item, @reason)
                    ON CONFLICT (user_id, item_id) DO UPDATE SET reason = EXCLUDED.reason;
                    """,
                    conn,
                    transaction))
                {
                    var itemParameter = command.Parameters.Add("item", NpgsqlDbType.Uuid);
                    var reasonParameter = command.Parameters.Add("reason", NpgsqlDbType.Text);
                    command.Parameters.AddWithValue("user", userId);

                    for (var i = 0; i < itemIds.Length; i++)
                    {
                        if (itemIds[i] == Guid.Empty)
                        {
                            continue;
                        }

                        itemParameter.Value = itemIds[i];
                        reasonParameter.Value = i < reasons.Length ? reasons[i] : "played";
                        command.ExecuteNonQuery();
                    }
                }

                transaction.Commit();
                return true;
            },
            false,
            "ReplaceSuppressed");
    }

    /// <inheritdoc/>
    public Guid[] GetSuppressed(Guid userId)
        => Run(
            conn =>
            {
                using var command = new NpgsqlCommand(
                    "SELECT item_id FROM jellytrend.user_suppression WHERE user_id = @user;",
                    conn);
                command.Parameters.AddWithValue("user", userId);
                return ReadGuids(command);
            },
            [],
            "GetSuppressed");

    /// <inheritdoc/>
    public int ReplaceConsumption(Guid userId, Guid[] itemIds, string[] consumptionJson)
    {
        ArgumentNullException.ThrowIfNull(itemIds);

        if (itemIds.Length == 0)
        {
            return 0;
        }

        return Run(
            conn =>
            {
                using var transaction = conn.BeginTransaction();
                var written = 0;

                using var command = new NpgsqlCommand(
                    """
                    INSERT INTO jellytrend.user_item_consumption (user_id, item_id, data, updated_at)
                    VALUES (@user, @item, @data, now())
                    ON CONFLICT (user_id, item_id) DO UPDATE
                        SET data = EXCLUDED.data,
                            updated_at = now();
                    """,
                    conn,
                    transaction);

                command.Parameters.AddWithValue("user", userId);
                var itemParameter = command.Parameters.Add("item", NpgsqlDbType.Uuid);
                var dataParameter = command.Parameters.Add("data", NpgsqlDbType.Jsonb);

                for (var i = 0; i < itemIds.Length; i++)
                {
                    if (itemIds[i] == Guid.Empty)
                    {
                        continue;
                    }

                    itemParameter.Value = itemIds[i];
                    dataParameter.Value = i < consumptionJson.Length ? consumptionJson[i] : "{}";
                    written += command.ExecuteNonQuery();
                }

                transaction.Commit();
                return written;
            },
            0,
            "ReplaceConsumption");
    }

    /// <inheritdoc/>
    public string? GetUserConsumption(Guid userId)
        => Run(
            conn =>
            {
                using var command = new NpgsqlCommand(
                    "SELECT jsonb_object_agg(item_id::text, data) FROM jellytrend.user_item_consumption WHERE user_id = @user;",
                    conn);
                command.Parameters.AddWithValue("user", userId);
                var value = command.ExecuteScalar();
                return value is null or DBNull ? null : value as string;
            },
            null,
            "GetUserConsumption");

    /// <inheritdoc/>
    public Guid StartRun(string kind, DateTime startedAt)
    {
        var runId = Guid.NewGuid();

        Run(
            conn =>
            {
                using var command = new NpgsqlCommand(
                    """
                    INSERT INTO jellytrend.sync_run (run_id, kind, started_at, status)
                    VALUES (@run, @kind, @started, 'running');
                    """,
                    conn);
                command.Parameters.AddWithValue("run", runId);
                command.Parameters.AddWithValue("kind", kind);
                command.Parameters.AddWithValue("started", DateTime.SpecifyKind(startedAt, DateTimeKind.Utc));
                command.ExecuteNonQuery();
                return true;
            },
            false,
            "StartRun");

        return runId;
    }

    /// <inheritdoc/>
    public void FinishRun(Guid runId, string status, int usersOk, int usersFailed, int durationMs, string? message)
        => Run(
            conn =>
            {
                using var command = new NpgsqlCommand(
                    """
                    UPDATE jellytrend.sync_run
                       SET finished_at = now(),
                           status = @status,
                           users_ok = @ok,
                           users_failed = @failed,
                           duration_ms = @duration,
                           message = @message
                     WHERE run_id = @run;
                    """,
                    conn);
                command.Parameters.AddWithValue("run", runId);
                command.Parameters.AddWithValue("status", status);
                command.Parameters.AddWithValue("ok", usersOk);
                command.Parameters.AddWithValue("failed", usersFailed);
                command.Parameters.AddWithValue("duration", durationMs);
                command.Parameters.AddWithValue("message", (object?)message ?? DBNull.Value);
                command.ExecuteNonQuery();
                return true;
            },
            false,
            "FinishRun");

    private static Guid[] ReadGuids(NpgsqlCommand command)
    {
        var ids = new List<Guid>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetGuid(0));
        }

        return [.. ids];
    }

    private static string? ReadMediaType(string[] entriesJson, int index)
    {
        if (index >= entriesJson.Length)
        {
            return null;
        }

        // El entry ya trae su media type: se guarda aparte solo para poder filtrar por tipo en SQL.
        var marker = "\"MediaType\":";
        var at = entriesJson[index].IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return null;
        }

        var rest = entriesJson[index][(at + marker.Length)..].TrimStart();
        if (rest.StartsWith('"'))
        {
            var end = rest.IndexOf('"', 1);
            return end > 1 ? rest[1..end] : null;
        }

        var digits = new string([.. rest.TakeWhile(char.IsDigit)]);
        return digits.Length == 0 ? null : digits;
    }

    private T Run<T>(Func<NpgsqlConnection, T> action, T fallback, string operation)
    {
        var connectionString = ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return fallback;
        }

        try
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            return action(connection);
        }
        catch (Exception ex)
        {
            PostgresLog.Warn($"[JellyTrend] {operation} fallo; se sigue con el almacen JSON: {ex.Message}");
            return fallback;
        }
    }

    private bool Run(Action<NpgsqlConnection> action, string operation)
        => Run<object?>(
            connection =>
            {
                action(connection);
                return null;
            },
            null,
            operation) is null;
}
