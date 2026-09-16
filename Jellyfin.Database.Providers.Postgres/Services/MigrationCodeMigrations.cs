using System;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// Validates the source schema and transfers only code migrations already applied to its data.
/// </summary>
internal static class MigrationCodeMigrations
{
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

    internal static async Task CopyAppliedAsync(SqliteConnection sqlite, NpgsqlConnection pg, Action<string> log)
    {
        using var select = sqlite.CreateCommand();
        select.CommandText = "SELECT \"MigrationId\", \"ProductVersion\" FROM \"__EFMigrationsHistory\";";
        using var reader = await select.ExecuteReaderAsync().ConfigureAwait(false);
        var count = 0;
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            // Jellyfin code IDs use yyyyMMddHHmmsss; provider schema IDs use 14 digits.
            if (!IsCodeMigrationId(id))
            {
                continue;
            }

            using var insert = new NpgsqlCommand(
                "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES (@id, @version) ON CONFLICT DO NOTHING;", pg);
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("version", reader.GetString(1));
            count += await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        log($"[CodeMigrations] {count} migraciones aplicadas copiadas desde SQLite.");
    }

    internal static bool IsCodeMigrationId(string id)
        => id.Length > 16 && id[15] == '_' && !id.AsSpan(0, 15).ContainsAnyExceptInRange('0', '9');
}
