using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// Validates the source schema and carries over the code migrations the source already applied.
/// </summary>
internal static class MigrationCodeMigrations
{
    private const string CodeMigrationAttributeName = "Jellyfin.Server.Migrations.JellyfinMigrationAttribute";

    private static HashSet<string>? _codeMigrationNames;

    internal static async Task ValidateSourceAsync(SqliteConnection sqlite)
    {
        using var context = new PostgresDesignTimeDbFactory().CreateDbContext(Array.Empty<string>());
        foreach (var table in context.Model.GetRelationalModel().Tables)
        {
            var columns = await MigrationDiscovery.GetSqliteColumnNamesAsync(sqlite, table.Name).ConfigureAwait(false);
            foreach (var column in table.Columns)
            {
                if (!columns.Contains(column.Name, StringComparer.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"SQLite no tiene {table.Name}.{column.Name}. Actualiza y arranca Jellyfin 12.1 con SQLite antes de importar a PostgreSQL.");
                }
            }
        }
    }

    /// <summary>
    /// Copies the history rows of the code migrations the SQLite database already applied.
    /// </summary>
    /// <remarks>
    /// Jellyfin records both its code migrations and the provider schema migrations in
    /// <c>__EFMigrationsHistory</c>, and every id is <c>yyyyMMddHHmmss_name</c>, so the two cannot be told
    /// apart by shape. Re-running a routine that already succeeded is not harmless: legacy ones such as
    /// <c>RemoveDuplicateExtras</c> open <c>library.db</c>, which a fresh 12.x install no longer has, and the
    /// host then fails to start. Only the ids that match a known migration type are copied, which leaves the
    /// provider's own history to EF.
    /// </remarks>
    /// <param name="sqlite">Source database, opened read-only.</param>
    /// <param name="pg">Target connection, already pointing at the destination schema.</param>
    /// <param name="log">Migration log sink.</param>
    /// <returns>A task that completes when the history has been copied.</returns>
    internal static Task CopyAppliedAsync(SqliteConnection sqlite, NpgsqlConnection pg, Action<string> log)
        => CopyAppliedAsync(sqlite, pg, DiscoverCodeMigrationNames(), log);

    /// <summary>
    /// Copies the applied code migrations from the source history using an explicit set of known names.
    /// </summary>
    /// <param name="sqlite">Source database, opened read-only.</param>
    /// <param name="pg">Target connection, already pointing at the destination schema.</param>
    /// <param name="codeMigrationNames">Names of the code migrations Jellyfin knows about.</param>
    /// <param name="log">Migration log sink.</param>
    /// <returns>A task that completes when the history has been copied.</returns>
    internal static async Task CopyAppliedAsync(
        SqliteConnection sqlite,
        NpgsqlConnection pg,
        IReadOnlySet<string> codeMigrationNames,
        Action<string> log)
    {
        if (codeMigrationNames.Count == 0)
        {
            log("[CodeMigrations] No se encontraron tipos de migracion de codigo de Jellyfin; el historial no se copia.");
            return;
        }

        var sourceRows = await ReadSourceHistoryAsync(sqlite).ConfigureAwait(false);
        var copied = 0;
        foreach (var (id, productVersion) in sourceRows)
        {
            if (!IsCodeMigrationId(id, codeMigrationNames))
            {
                continue;
            }

            using var insert = new NpgsqlCommand(
                "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES (@id, @version) ON CONFLICT DO NOTHING;",
                pg);
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("version", productVersion);
            copied += await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        log($"[CodeMigrations] {copied} de {sourceRows.Count} filas del historial SQLite son migraciones de codigo aplicadas.");

        if (copied == 0)
        {
            log("[CodeMigrations] ADVERTENCIA: no se copio ninguna migracion de codigo; Jellyfin volvera a ejecutar rutinas que pueden necesitar library.db.");
        }
    }

    /// <summary>
    /// Reports whether an id belongs to a Jellyfin code migration.
    /// </summary>
    /// <param name="id">History id, shaped as <c>yyyyMMddHHmmss_name</c>.</param>
    /// <param name="codeMigrationNames">Names of the code migrations Jellyfin knows about.</param>
    /// <returns><c>true</c> when the id is a known code migration.</returns>
    internal static bool IsCodeMigrationId(string id, IReadOnlySet<string> codeMigrationNames)
    {
        var separator = id.IndexOf('_', StringComparison.Ordinal);
        if (separator < 14 || separator > 15 || separator == id.Length - 1)
        {
            return false;
        }

        if (id.AsSpan(0, separator).ContainsAnyExceptInRange('0', '9'))
        {
            return false;
        }

        return codeMigrationNames.Contains(id[(separator + 1)..]);
    }

    /// <summary>
    /// Collects the names of the code migrations declared by the host assemblies.
    /// </summary>
    /// <returns>Names read from <c>JellyfinMigrationAttribute</c>; empty when no host assembly is loaded.</returns>
    internal static HashSet<string> DiscoverCodeMigrationNames()
        => _codeMigrationNames ??= ScanCodeMigrationNames();

    private static HashSet<string> ScanCodeMigrationNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var type in GetLoadableTypes(assembly))
            {
                foreach (var attribute in type.GetCustomAttributes(inherit: false))
                {
                    if (!string.Equals(attribute.GetType().FullName, CodeMigrationAttributeName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (attribute.GetType().GetProperty("Name")?.GetValue(attribute) is string name && name.Length > 0)
                    {
                        names.Add(name);
                    }
                }
            }
        }

        return names;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(static type => type is not null).Cast<Type>();
        }
        catch (Exception ex) when (ex is FileNotFoundException or NotSupportedException or BadImageFormatException)
        {
            return Array.Empty<Type>();
        }
    }

    private static async Task<List<(string Id, string ProductVersion)>> ReadSourceHistoryAsync(SqliteConnection sqlite)
    {
        var rows = new List<(string Id, string ProductVersion)>();
        using var select = sqlite.CreateCommand();
        select.CommandText = "SELECT \"MigrationId\", \"ProductVersion\" FROM \"__EFMigrationsHistory\";";
        using var reader = await select.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return rows;
    }
}
