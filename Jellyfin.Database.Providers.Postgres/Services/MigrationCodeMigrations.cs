using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Npgsql;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// Pre-marks Jellyfin code-based migrations in <c>__EFMigrationsHistory</c>
/// so EF Core does not try to re-run them after the data import.
/// </summary>
internal static class MigrationCodeMigrations
{
    private static readonly string[] MigrationIdFormats = { "yyyyMMddHHmmss", "yyyyMMddHHmmsss" };

    /// <summary>
    /// Discovers Jellyfin migration types in the running AppDomain and inserts
    /// their IDs into <c>__EFMigrationsHistory</c> using ON CONFLICT DO NOTHING.
    /// </summary>
    /// <param name="pg">Open PostgreSQL connection.</param>
    /// <param name="log">Callback for progress messages.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    internal static async Task PreMarkAsync(NpgsqlConnection pg, Action<string> log)
    {
        Type? attrType = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            attrType = asm.GetType("Jellyfin.Server.Migrations.JellyfinMigrationAttribute");
            if (attrType is not null)
            {
                break;
            }
        }

        if (attrType is null)
        {
            log("[CodeMigrations] JellyfinMigrationAttribute no encontrado en el AppDomain.");
            return;
        }

        var orderProp = attrType.GetProperty("Order");
        var nameProp = attrType.GetProperty("Name");
        if (orderProp is null || nameProp is null)
        {
            log("[CodeMigrations] Propiedades Order/Name no encontradas.");
            return;
        }

        const string insertSql =
            "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES (@id, @ver) ON CONFLICT DO NOTHING;";

        var count = 0;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] allTypes;
            try
            {
                allTypes = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                allTypes = ex.Types.Where(t => t is not null).ToArray()!;
            }

            foreach (var type in allTypes)
            {
                if (type is null)
                {
                    continue;
                }

                var attrs = type.GetCustomAttributes(attrType, false);
                if (attrs.Length == 0)
                {
                    continue;
                }

                var attr = attrs[0];
                var order = (DateTime)orderProp.GetValue(attr)!;
                var migName = (string)nameProp.GetValue(attr)!;

                foreach (var fmt in MigrationIdFormats)
                {
                    var migrationId = order.ToString(fmt, CultureInfo.InvariantCulture) + "_" + migName;
                    using var cmd = new NpgsqlCommand(insertSql, pg);
                    cmd.Parameters.AddWithValue("id", migrationId);
                    cmd.Parameters.AddWithValue("ver", "0.0.0");
                    var inserted = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    if (inserted > 0)
                    {
                        count++;
                    }
                }
            }
        }

        log($"[CodeMigrations] {count} migration(es) pre-marcadas.");
    }
}
