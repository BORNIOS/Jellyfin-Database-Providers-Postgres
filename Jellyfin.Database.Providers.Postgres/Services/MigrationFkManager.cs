using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// Manages FK trigger suspension and re-enabling during the migration.
/// Suspending FK triggers prevents orphaned-row errors from SQLite data.
/// </summary>
internal static class MigrationFkManager
{
    /// <summary>
    /// Enables or disables all triggers (including FK constraint triggers) on the given tables.
    /// Falls back gracefully per table if permission is denied.
    /// </summary>
    /// <param name="pg">Open PostgreSQL connection.</param>
    /// <param name="schema">Target schema name.</param>
    /// <param name="tables">Tables on which triggers will be toggled.</param>
    /// <param name="enable"><see langword="true"/> to enable triggers; <see langword="false"/> to disable.</param>
    /// <param name="log">Callback for progress messages.</param>
    /// <param name="logger">Logger for per-table warnings.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    internal static async Task SetFkTriggersAsync(
        NpgsqlConnection pg,
        string schema,
        IEnumerable<string> tables,
        bool enable,
        Action<string> log,
        ILogger logger)
    {
        var action = enable ? "ENABLE" : "DISABLE";
        var tableList = tables.ToList();
        log($"[Triggers] {action} TRIGGER ALL en {tableList.Count} tabla(s)...");

        var failed = 0;
        foreach (var table in tableList)
        {
            try
            {
                using var cmd = CreateTriggerCommand(pg, schema, table, enable);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (PostgresException ex)
            {
                failed++;
                logger.LogWarning(
                    "No se pudo {Action} triggers en {Table}: {Error}", action, table, ex.MessageText);
            }
        }

        if (failed == 0)
        {
            log($"[Triggers] {action} TRIGGER ALL completado.");
        }
        else
        {
            log($"[Triggers] {action} TRIGGER ALL: {failed} tabla(s) sin permiso.");
        }
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "SQL uses hardcoded ENABLE/DISABLE keywords + QuoteIdentifier on schema/table from server catalog, not user input.")]
    private static NpgsqlCommand CreateTriggerCommand(NpgsqlConnection pg, string schema, string table, bool enable)
    {
        var sql = enable
            ? string.Concat("ALTER TABLE ", MigrationDiscovery.QuoteIdentifier(schema), ".", MigrationDiscovery.QuoteIdentifier(table), " ENABLE TRIGGER ALL;")
            : string.Concat("ALTER TABLE ", MigrationDiscovery.QuoteIdentifier(schema), ".", MigrationDiscovery.QuoteIdentifier(table), " DISABLE TRIGGER ALL;");
        return new NpgsqlCommand(sql, pg);
    }
}
