using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Database.Providers.Postgres.Logging;
using Jellyfin.Database.Providers.Postgres.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// Detects, and when it is safe repairs, the difference between the schema the provider's migrations create and
/// the model of the Jellyfin server that is actually running.
/// </summary>
/// <remarks>
/// <para>
/// Jellyfin loads a provider built for an older release on a newer server (<c>appVersion &gt;= targetAbi</c>), and it
/// only runs EF migrations that the provider ships. When a Jellyfin release changes its model, a provider without the
/// matching migration leaves the database silently behind: missing indexes make queries slow, missing columns make
/// them fail. Upgrading Jellyfin before the provider was the main obstacle to seamless upgrades.
/// </para>
/// <para>
/// The guard compares the provider's model snapshot with Jellyfin's runtime model, exactly like
/// <c>HasPendingModelChanges()</c>, and classifies every operation. When all of them are additive (indexes, new tables,
/// new nullable or defaulted columns) it applies them in one transaction and records them in
/// <c>__PostgresSchemaDrift</c>. Anything that drops, rewrites or renames data is never applied: the report says the
/// provider must be updated. Once the provider ships the real migration, <see cref="IdempotentMigrationsSqlGenerator"/>
/// lets it run over the objects the guard already created.
/// </para>
/// </remarks>
internal static class SchemaDriftGuard
{
    /// <summary>Table that records the operations applied by the guard.</summary>
    internal const string RecordTable = "__PostgresSchemaDrift";

    /// <summary>Environment variable that overrides the configured mode: <c>heal</c>, <c>report</c> or <c>off</c>.</summary>
    internal const string ModeVariable = "JELLYFIN_PG_SCHEMA_DRIFT";

    private const long AdvisoryLockKey = 0x4A46_5047_4452_4654; // "JFPGDRFT"

    // Jellyfin loads this assembly twice: once as the database provider (from database.xml) and once as a plugin,
    // in different load contexts, so a static field written by the provider is invisible to the controller and the
    // health check. The AppDomain is shared by both, and a JSON string crosses the boundary without type identity.
    private const string ReportSlot = "Jellyfin.Database.Providers.Postgres.SchemaDriftReport";

    /// <summary>The EF Core provider name of an active PostgreSQL context.</summary>
    internal const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>Gets the report of the last check run in this process, or <see langword="null"/> if none ran.</summary>
    internal static SchemaDriftReport? LastReport
        => AppDomain.CurrentDomain.GetData(ReportSlot) is string json
            ? JsonSerializer.Deserialize<SchemaDriftReport>(json)
            : null;

    private static string JellyfinVersion
        => typeof(Jellyfin.Database.Implementations.JellyfinDbContext).Assembly.GetName().Version?.ToString() ?? "?";

    private static string ProviderVersion
        => typeof(SchemaDriftGuard).Assembly.GetName().Version?.ToString() ?? "?";

    /// <summary>Resolves the effective mode: the environment variable wins over the plugin configuration.</summary>
    /// <param name="configured">Mode from the plugin configuration, if it is loaded.</param>
    /// <returns>The mode to run with.</returns>
    internal static SchemaDriftMode ResolveMode(SchemaDriftMode? configured)
    {
        var value = Environment.GetEnvironmentVariable(ModeVariable)?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return configured ?? SchemaDriftMode.Heal;
        }

        return value.ToUpperInvariant() switch
        {
            "OFF" or "0" or "FALSE" or "DISABLED" => SchemaDriftMode.Off,
            "REPORT" or "REPORTONLY" or "REPORT_ONLY" => SchemaDriftMode.ReportOnly,
            _ => SchemaDriftMode.Heal,
        };
    }

    /// <summary>
    /// Reads <see cref="PluginConfiguration.SchemaDriftMode"/> straight from the plugin's configuration file.
    /// </summary>
    /// <remarks>
    /// The provider copy of the assembly never sees the loaded plugin instance (see <c>ReportSlot</c>), so the early
    /// check reads the setting from disk, as the provider already does with <c>database.xml</c>.
    /// </remarks>
    /// <param name="pluginConfigurationsPath">Jellyfin's plugin configuration directory.</param>
    /// <returns>The configured mode, or <see langword="null"/> when the file or the element is missing.</returns>
    internal static SchemaDriftMode? ReadConfiguredMode(string? pluginConfigurationsPath)
    {
        if (string.IsNullOrEmpty(pluginConfigurationsPath))
        {
            return null;
        }

        var file = Path.Combine(pluginConfigurationsPath, typeof(SchemaDriftGuard).Assembly.GetName().Name + ".xml");
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            var value = XDocument.Load(file).Root?.Element(nameof(PluginConfiguration.SchemaDriftMode))?.Value;
            return Enum.TryParse<SchemaDriftMode>(value, ignoreCase: true, out var mode) ? mode : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            PostgresLog.Warn($"[SchemaDrift] No se pudo leer {file}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Runs the check and, depending on <paramref name="mode"/>, the repair.</summary>
    /// <param name="context">A Jellyfin context pointing at the PostgreSQL database.</param>
    /// <param name="mode">What to do with the drift.</param>
    /// <param name="baseline">
    /// Model the database is assumed to have. <see langword="null"/> uses the provider's model snapshot, which is what the
    /// database has once every provider migration is applied; tests pass an older migration's target model.
    /// </param>
    /// <param name="logger">Optional logger; the report also goes to Jellyfin's own log through it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The report, also available afterwards through <see cref="LastReport"/>.</returns>
    internal static async Task<SchemaDriftReport> RunAsync(
        DbContext context,
        SchemaDriftMode mode,
        IModel? baseline = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (mode == SchemaDriftMode.Off)
        {
            return Publish(logger, mode, SchemaDriftOutcome.Disabled, [], "Comprobación de deriva del esquema desactivada.");
        }

        try
        {
            var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
            if (pending.Count > 0)
            {
                return Publish(
                    logger,
                    mode,
                    SchemaDriftOutcome.Deferred,
                    [],
                    $"Hay {pending.Count} migración(es) del proveedor pendiente(s) ({string.Join(", ", pending)}); la comprobación se repite cuando Jellyfin las aplique.");
            }

            var differences = ComputeDifferences(context, baseline);
            if (differences.Count == 0)
            {
                return Publish(logger, mode, SchemaDriftOutcome.InSync, [], "El esquema coincide con el modelo de Jellyfin.");
            }

            var planned = Plan(context, differences);
            var applied = await ReadAppliedAsync(context, cancellationToken).ConfigureAwait(false);
            var operations = planned
                .Select(p => p.Operation with { Applied = applied.Contains(p.Hash) })
                .ToList();

            var unsafeOps = operations.Where(o => !o.Safe).ToList();
            if (unsafeOps.Count > 0)
            {
                return Publish(
                    logger,
                    mode,
                    SchemaDriftOutcome.Blocked,
                    operations,
                    $"Jellyfin {JellyfinVersion} tiene {operations.Count} cambio(s) de esquema sin migración en el proveedor {ProviderVersion}, "
                    + $"{unsafeOps.Count} de ellos no se pueden aplicar automáticamente ({string.Join("; ", unsafeOps.Select(o => o.Description))}). "
                    + "Actualiza el proveedor a la versión para esta versión de Jellyfin.");
            }

            if (operations.All(o => o.Applied))
            {
                return Publish(
                    logger,
                    mode,
                    SchemaDriftOutcome.AlreadyHealed,
                    operations,
                    $"{operations.Count} cambio(s) de esquema de Jellyfin {JellyfinVersion} ya aplicados por el proveedor; desaparecen del informe al actualizar el proveedor.");
            }

            if (mode == SchemaDriftMode.ReportOnly)
            {
                return Publish(
                    logger,
                    mode,
                    SchemaDriftOutcome.Detected,
                    operations,
                    $"Jellyfin {JellyfinVersion} tiene {operations.Count(o => !o.Applied)} cambio(s) de esquema sin aplicar. Modo ReportOnly: no se aplican.");
            }

            var newlyApplied = await ApplyAsync(context, planned, cancellationToken).ConfigureAwait(false);
            var final = planned.Select(p => p.Operation with { Applied = true }).ToList();
            return Publish(
                logger,
                mode,
                SchemaDriftOutcome.Healed,
                final,
                $"Aplicados {newlyApplied} cambio(s) de esquema de Jellyfin {JellyfinVersion} que el proveedor {ProviderVersion} aún no incluye: "
                + string.Join("; ", final.Select(o => o.Description)) + ".");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PostgresLog.Error("[SchemaDrift] La comprobación falló", ex);
            return Publish(logger, mode, SchemaDriftOutcome.Failed, [], $"La comprobación de deriva del esquema falló: {ex.Message}");
        }
    }

    /// <summary>Turns the last report into health check findings.</summary>
    /// <returns>Zero or one finding.</returns>
    internal static IReadOnlyList<HealthFinding> ToFindings() => ToFindings(LastReport);

    /// <summary>Turns a report into health check findings.</summary>
    /// <param name="report">Report, or <see langword="null"/> when no check ran.</param>
    /// <returns>Zero or one finding.</returns>
    internal static IReadOnlyList<HealthFinding> ToFindings(SchemaDriftReport? report)
    {
        if (report is null)
        {
            return [];
        }

        var severity = report.Outcome switch
        {
            SchemaDriftOutcome.Blocked or SchemaDriftOutcome.Failed => HealthSeverity.Error,
            SchemaDriftOutcome.Detected => HealthSeverity.Warn,
            _ => HealthSeverity.Ok,
        };

        if (severity == HealthSeverity.Ok && report.Outcome != SchemaDriftOutcome.Healed && report.Outcome != SchemaDriftOutcome.AlreadyHealed)
        {
            return [];
        }

        var detail = report.Operations.Count == 0
            ? null
            : string.Join("\n", report.Operations.Select(o => $"{(o.Applied ? "[aplicado]" : o.Safe ? "[pendiente]" : "[requiere actualizar]")} {o.Description}"));
        return [new HealthFinding("SchemaDrift", severity, report.Message, detail)];
    }

    /// <summary>Describes one migration operation for the report.</summary>
    /// <param name="operation">Operation.</param>
    /// <returns>Readable description.</returns>
    internal static string Describe(MigrationOperation operation)
        => operation switch
        {
            CreateIndexOperation c => $"CreateIndex {c.Name} on {c.Table}" + (c.Filter is null ? string.Empty : $" WHERE {c.Filter}"),
            DropIndexOperation d => $"DropIndex {d.Name}" + (d.Table is null ? string.Empty : $" on {d.Table}"),
            AddColumnOperation a => $"AddColumn {a.Table}.{a.Name} ({a.ColumnType ?? a.ClrType.Name}{(a.IsNullable ? ", null" : string.Empty)})",
            CreateTableOperation t => $"CreateTable {t.Name}",
            ITableMigrationOperation t => $"{OperationName(operation)} on {t.Table}",
            _ => OperationName(operation),
        };

    /// <summary>Whether an operation can be applied without losing or rewriting data.</summary>
    /// <param name="operation">Operation.</param>
    /// <returns><see langword="true"/> for additive operations.</returns>
    internal static bool IsSafe(MigrationOperation operation)
        => operation switch
        {
            CreateIndexOperation or DropIndexOperation or CreateTableOperation or EnsureSchemaOperation => true,
            AddColumnOperation a => a.IsNullable || a.DefaultValue is not null || a.DefaultValueSql is not null || a.ComputedColumnSql is not null,
            _ => false,
        };

    private static string OperationName(MigrationOperation operation)
    {
        var name = operation.GetType().Name;
        return name.EndsWith("Operation", StringComparison.Ordinal) ? name[..^"Operation".Length] : name;
    }

    private static IReadOnlyList<MigrationOperation> ComputeDifferences(DbContext context, IModel? baseline)
    {
        // Same comparison as Migrator.HasPendingModelChanges() in EF Core 10, but returning the operations.
        // Snapshot and migration target models are already finalized; Initialize only adds the runtime pieces.
        var source = baseline ?? context.GetService<IMigrationsAssembly>().ModelSnapshot?.Model;
        if (source is not null)
        {
            source = context.GetService<IModelRuntimeInitializer>().Initialize(source);
        }

        var target = context.GetService<IDesignTimeModel>().Model;
        return context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(source?.GetRelationalModel(), target.GetRelationalModel());
    }

    private static List<PlannedOperation> Plan(DbContext context, IReadOnlyList<MigrationOperation> differences)
    {
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var model = context.GetService<IDesignTimeModel>().Model;
        var plan = new List<PlannedOperation>(differences.Count);
        foreach (var operation in differences)
        {
            var commands = generator.Generate([operation], model);

            // Rewritten here as well, so the recorded SQL and its hash do not depend on which generator the context has.
            var sql = string.Concat(commands.Select(c => IdempotentMigrationsSqlGenerator.MakeIdempotent(c.CommandText)));
            var safe = IsSafe(operation) && commands.All(c => !c.TransactionSuppressed);
            plan.Add(new PlannedOperation(new SchemaDriftOperation(Describe(operation), sql, safe, Applied: false), Hash(sql)));
        }

        return plan;
    }

    private static string Hash(string sql)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)))[..32];

    private static async Task<HashSet<string>> ReadAppliedAsync(DbContext context, CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var connection = context.Database.GetDbConnection();
        var opened = await OpenAsync(context, connection, cancellationToken).ConfigureAwait(false);
        try
        {
            using var exists = connection.CreateCommand();
            exists.CommandText = "SELECT to_regclass(@name) IS NOT NULL;";
            AddParameter(exists, "name", Quote(RecordTable));
            if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                return result;
            }

            using var read = connection.CreateCommand();
            read.CommandText = $"SELECT \"OperationHash\" FROM \"{RecordTable}\";";
            using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(reader.GetString(0));
            }

            return result;
        }
        finally
        {
            if (opened)
            {
                await context.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The SQL is generated by EF Core from Jellyfin's own model, never from user input.")]
    private static async Task<int> ApplyAsync(DbContext context, List<PlannedOperation> plan, CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        var opened = await OpenAsync(context, connection, cancellationToken).ConfigureAwait(false);
        try
        {
            using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            // One server at a time: a second instance waits here and then finds the operations recorded.
            await ExecuteAsync(connection, transaction, $"SELECT pg_advisory_xact_lock({AdvisoryLockKey.ToString(CultureInfo.InvariantCulture)});", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                transaction,
                $"""
                CREATE TABLE IF NOT EXISTS "{RecordTable}" (
                    "OperationHash" text PRIMARY KEY,
                    "Description" text NOT NULL,
                    "Sql" text NOT NULL,
                    "JellyfinVersion" text NOT NULL,
                    "ProviderVersion" text NOT NULL,
                    "AppliedAt" timestamptz NOT NULL DEFAULT now());
                """,
                cancellationToken).ConfigureAwait(false);

            var applied = new HashSet<string>(StringComparer.Ordinal);
            using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = $"SELECT \"OperationHash\" FROM \"{RecordTable}\";";
                using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    applied.Add(reader.GetString(0));
                }
            }

            var count = 0;
            foreach (var step in plan.Where(p => !applied.Contains(p.Hash)))
            {
                PostgresLog.Info($"[SchemaDrift] Aplicando {step.Operation.Description}");
                await ExecuteAsync(connection, transaction, step.Operation.Sql, cancellationToken).ConfigureAwait(false);

                using var record = connection.CreateCommand();
                record.Transaction = transaction;
                record.CommandText = $"""
                    INSERT INTO "{RecordTable}" ("OperationHash", "Description", "Sql", "JellyfinVersion", "ProviderVersion")
                    VALUES (@hash, @description, @sql, @jellyfin, @provider) ON CONFLICT DO NOTHING;
                    """;
                AddParameter(record, "hash", step.Hash);
                AddParameter(record, "description", step.Operation.Description);
                AddParameter(record, "sql", step.Operation.Sql);
                AddParameter(record, "jellyfin", JellyfinVersion);
                AddParameter(record, "provider", ProviderVersion);
                await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                count++;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return count;
        }
        finally
        {
            if (opened)
            {
                await context.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<bool> OpenAsync(DbContext context, DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection.State == ConnectionState.Open)
        {
            return false;
        }

        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Callers pass SQL generated from Jellyfin's model or constant statements.")]
    private static async Task ExecuteAsync(DbConnection connection, DbTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string Quote(string identifier) => string.Concat("\"", identifier, "\"");

    private static SchemaDriftReport Publish(ILogger? logger, SchemaDriftMode mode, SchemaDriftOutcome outcome, IReadOnlyList<SchemaDriftOperation> operations, string message)
    {
        var report = new SchemaDriftReport(DateTime.UtcNow, mode, outcome, JellyfinVersion, ProviderVersion, operations, message);
        AppDomain.CurrentDomain.SetData(ReportSlot, JsonSerializer.Serialize(report));

        switch (outcome)
        {
            case SchemaDriftOutcome.Blocked:
            case SchemaDriftOutcome.Failed:
                PostgresLog.Error($"[SchemaDrift] {message}");
                break;
            case SchemaDriftOutcome.Detected:
            case SchemaDriftOutcome.Healed:
                PostgresLog.Warn($"[SchemaDrift] {message}");
                break;
            default:
                PostgresLog.Info($"[SchemaDrift] {message}");
                break;
        }

        var level = outcome switch
        {
            SchemaDriftOutcome.Blocked or SchemaDriftOutcome.Failed => LogLevel.Error,
            SchemaDriftOutcome.Detected or SchemaDriftOutcome.Healed => LogLevel.Warning,
            _ => LogLevel.Information,
        };
        if (logger is not null && logger.IsEnabled(level))
        {
            logger.Log(level, "Schema drift check ({Outcome}): {Message}", outcome, message);
        }

        return report;
    }

    private sealed record PlannedOperation(SchemaDriftOperation Operation, string Hash);
}
