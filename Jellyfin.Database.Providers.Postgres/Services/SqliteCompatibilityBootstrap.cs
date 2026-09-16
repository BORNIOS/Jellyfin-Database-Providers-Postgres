using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Jellyfin.Database.Providers.Postgres.Logging;
using Npgsql;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// Creates the small set of database objects that Jellyfin's queries need on PostgreSQL but that
/// SQLite provides out of the box.
/// </summary>
/// <remarks>
/// <para>
/// Jellyfin builds some LINQ queries as <c>min(uuid)</c>/<c>max(uuid)</c> (for example
/// <c>GetLatestChannelItems</c> on the home page, which groups by <c>PresentationUniqueKey</c>).
/// SQLite lets <c>min</c>/<c>max</c> work on any type, PostgreSQL does not define those aggregates
/// for <c>uuid</c>, so the query fails with <c>42883: no existe la función min(uuid)</c>.
/// </para>
/// <para>
/// Creating the aggregates is the provider-side adaptation: it fixes every query that uses them,
/// instead of rewriting Jellyfin's SQL statement by statement.
/// </para>
/// </remarks>
internal static class SqliteCompatibilityBootstrap
{
    // The helpers compare uuids through LEAST/GREATEST, which use the btree operators PostgreSQL
    // already ships for the uuid type. Both are created with CREATE OR REPLACE, and the aggregates
    // only when their signature is missing, so running this repeatedly is harmless.
    private static readonly string[] Statements =
    {
        """
        CREATE OR REPLACE FUNCTION uuid_smaller(uuid, uuid) RETURNS uuid
            LANGUAGE sql IMMUTABLE PARALLEL SAFE
            AS 'SELECT LEAST($1, $2)';
        """,
        """
        CREATE OR REPLACE FUNCTION uuid_larger(uuid, uuid) RETURNS uuid
            LANGUAGE sql IMMUTABLE PARALLEL SAFE
            AS 'SELECT GREATEST($1, $2)';
        """,
        """
        DO $do$
        BEGIN
            IF NOT EXISTS (
                SELECT 1
                FROM pg_aggregate a
                JOIN pg_proc p ON p.oid = a.aggfnoid
                WHERE p.proname = 'min'
                  AND pg_get_function_identity_arguments(p.oid) = 'uuid')
            THEN
                CREATE AGGREGATE min(uuid) (SFUNC = uuid_smaller, STYPE = uuid, PARALLEL = SAFE);
            END IF;
        END
        $do$;
        """,
        """
        DO $do$
        BEGIN
            IF NOT EXISTS (
                SELECT 1
                FROM pg_aggregate a
                JOIN pg_proc p ON p.oid = a.aggfnoid
                WHERE p.proname = 'max'
                  AND pg_get_function_identity_arguments(p.oid) = 'uuid')
            THEN
                CREATE AGGREGATE max(uuid) (SFUNC = uuid_larger, STYPE = uuid, PARALLEL = SAFE);
            END IF;
        END
        $do$;
        """,
    };

    // Remembers the last database that was prepared. Keyed per database instead of per process so a
    // server pointed at another database also gets the objects, and so repeated Initialise calls
    // (EF Core may call them more than once) stay cheap.
    private static readonly object EnsureLock = new();

    private static string? _ensuredFor;

    /// <summary>
    /// Creates the compatibility objects once per database, using an open connection.
    /// </summary>
    /// <param name="conn">Open PostgreSQL connection (with the target schema in the search path).</param>
    internal static void Ensure(NpgsqlConnection conn)
    {
        ArgumentNullException.ThrowIfNull(conn);

        if (!ShouldEnsure(conn.Host, conn.Port, conn.Database))
        {
            return;
        }

        Create(conn);
    }

    /// <summary>
    /// Creates the compatibility objects using its own connection. Safe to call repeatedly.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    internal static void Ensure(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!ShouldEnsure(builder.Host, builder.Port, builder.Database))
        {
            return;
        }

        try
        {
            using var conn = new NpgsqlConnection(connectionString);
            conn.Open();
            Create(conn);
        }
        catch (Exception ex)
        {
            // This runs during provider initialisation: a failure here must never stop Jellyfin
            // from starting, it only means the compatibility aggregates are missing.
            PostgresLog.Error("[Compatibility] No se pudo conectar para crear los agregados min/max(uuid)", ex);
        }
    }

    private static bool ShouldEnsure(string? host, int port, string? database)
    {
        var target = string.Create(CultureInfo.InvariantCulture, $"{host}:{port}/{database}");
        lock (EnsureLock)
        {
            if (string.Equals(_ensuredFor, target, StringComparison.Ordinal))
            {
                return false;
            }

            _ensuredFor = target;
            return true;
        }
    }

    private static void Create(NpgsqlConnection conn)
    {
        try
        {
            foreach (var statement in Statements)
            {
                using var cmd = CreateCommand(conn, statement);
                cmd.ExecuteNonQuery();
            }

            PostgresLog.Info("[Compatibility] Agregados min/max sobre uuid disponibles (compatibilidad SQLite).");
        }
        catch (Exception ex)
        {
            PostgresLog.Error("[Compatibility] No se pudieron crear los agregados min/max(uuid)", ex);
        }
    }

    // Statements are hardcoded DDL (no user input).
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Hardcoded DDL statements, no user input.")]
    private static NpgsqlCommand CreateCommand(NpgsqlConnection conn, string statement)
        => new NpgsqlCommand(statement, conn);
}
