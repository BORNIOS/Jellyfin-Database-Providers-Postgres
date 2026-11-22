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
using System.Text.RegularExpressions;
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
/// new nullable or constant-default columns) it applies them and logs them in <c>__PostgresSchemaDrift</c>. Indexes are
/// built and dropped with <c>CONCURRENTLY</c>, so a large <c>BaseItems</c> table stays writable meanwhile; an index left
/// invalid by an interrupted build is found in the catalog and rebuilt on the next check. Anything that drops, rewrites
/// or renames data, or a unique index, is never applied: the report says the provider must be updated. Whether a
/// change is in place is always read from the catalog (<see cref="SchemaFacts"/>), and every index of the model is
/// checked against its definition in the database, so an object left with another definition (for example by an
/// idempotent provider migration) is found and rebuilt. If a later provider build ships the real migration,
/// <see cref="IdempotentMigrationsSqlGenerator"/> lets it run over the objects the guard already created.
/// </para>
/// </remarks>
internal static partial class SchemaDriftGuard
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

    // How long a server waits for another one that is applying the same changes.
    private static readonly TimeSpan LockWait = TimeSpan.FromMinutes(15);

    private enum OperationState
    {
        /// <summary>The database already reflects the operation.</summary>
        Satisfied,

        /// <summary>Not applied yet.</summary>
        Pending,

        /// <summary>Applied once for this Jellyfin and provider version, yet the database still differs.</summary>
        Retried,
    }

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
    /// <remarks>
    /// An unrecognised value never turns on automatic changes: it falls back to the configured mode, or to
    /// <see cref="SchemaDriftMode.ReportOnly"/> when nothing is configured, and logs a warning.
    /// </remarks>
    /// <param name="configured">Mode from the plugin configuration, if it is loaded.</param>
    /// <returns>The mode to run with.</returns>
    internal static SchemaDriftMode ResolveMode(SchemaDriftMode? configured)
    {
        var value = Environment.GetEnvironmentVariable(ModeVariable)?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return configured ?? SchemaDriftMode.Heal;
        }

        switch (value.ToUpperInvariant().Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal))
        {
            case "HEAL" or "ON" or "1" or "TRUE" or "YES":
                return SchemaDriftMode.Heal;
            case "REPORT" or "REPORTONLY":
                return SchemaDriftMode.ReportOnly;
            case "OFF" or "0" or "FALSE" or "NO" or "NONE" or "DISABLE" or "DISABLED":
                return SchemaDriftMode.Off;
            default:
                var fallback = configured ?? SchemaDriftMode.ReportOnly;
                PostgresLog.Warn($"[SchemaDrift] Valor no reconocido en {ModeVariable}='{value}'; se usa {fallback}.");
                return fallback;
        }
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

            var value = XDocument.Load(file).Root?.Element(nameof(PluginConfiguration.SchemaDriftMode))?.Value?.Trim();

            // Names only: Enum.TryParse also accepts any number ("5"), which no check below would treat as Off or ReportOnly.
            return !string.IsNullOrEmpty(value) && !char.IsAsciiDigit(value[0]) && value[0] != '-'
                && Enum.TryParse<SchemaDriftMode>(value, ignoreCase: true, out var mode) && Enum.IsDefined(mode)
                ? mode
                : null;
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
    /// <param name="deferIndexes">
    /// Leaves index changes for later (outcome <see cref="SchemaDriftOutcome.Deferred"/>) and only applies table and column
    /// changes. Used on Jellyfin's startup path: a concurrent index build waits for every older transaction in the
    /// database, with no upper bound, and Jellyfin's migrations do not need the indexes.
    /// </param>
    /// <param name="logger">Optional logger; the report also goes to Jellyfin's own log through it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The report, also available afterwards through <see cref="LastReport"/>.</returns>
    internal static async Task<SchemaDriftReport> RunAsync(
        DbContext context,
        SchemaDriftMode mode,
        IModel? baseline = null,
        bool deferIndexes = false,
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
            var facts = await SchemaFacts.LoadAsync(context, cancellationToken).ConfigureAwait(false);
            var plan = Plan(context, differences, facts);
            if (plan.Count == 0)
            {
                return Publish(logger, mode, SchemaDriftOutcome.InSync, [], "El esquema coincide con el modelo de Jellyfin.");
            }

            var recorded = await ReadRecordedAsync(context, cancellationToken).ConfigureAwait(false);
            var states = Evaluate(plan, facts, recorded);

            var unsafeOps = plan.Where(p => !p.Operation.Safe).ToList();
            if (unsafeOps.Count > 0)
            {
                return Publish(
                    logger,
                    mode,
                    SchemaDriftOutcome.Blocked,
                    Report(plan, states),
                    $"Jellyfin {JellyfinVersion} tiene {plan.Count} diferencia(s) de esquema sin migración en el proveedor {ProviderVersion}, "
                    + $"{unsafeOps.Count} de ellas no se pueden aplicar automáticamente ({string.Join("; ", unsafeOps.Select(o => o.Operation.Description))}). "
                    + "Actualiza el proveedor a la versión para esta versión de Jellyfin.");
            }

            var pendingOps = plan.Where(p => states[p] == OperationState.Pending).ToList();
            if (pendingOps.Count > 0 && mode == SchemaDriftMode.ReportOnly)
            {
                return Publish(
                    logger,
                    mode,
                    SchemaDriftOutcome.Detected,
                    Report(plan, states),
                    $"Jellyfin {JellyfinVersion} tiene {pendingOps.Count} cambio(s) de esquema sin aplicar. Modo ReportOnly: no se aplican.");
            }

            var deferred = deferIndexes ? pendingOps.Where(p => p.Migration is CreateIndexOperation or DropIndexOperation).ToList() : [];
            var applyNow = pendingOps.Except(deferred).ToList();
            var applied = 0;
            if (applyNow.Count > 0)
            {
                applied = await ApplyAsync(context, plan, applyNow, cancellationToken).ConfigureAwait(false);
                facts = await SchemaFacts.LoadAsync(context, cancellationToken).ConfigureAwait(false);
                recorded = await ReadRecordedAsync(context, cancellationToken).ConfigureAwait(false);
                states = Evaluate(plan, facts, recorded);
            }

            if (deferred.Count > 0)
            {
                return Publish(
                    logger,
                    mode,
                    SchemaDriftOutcome.Deferred,
                    Report(plan, states),
                    $"{deferred.Count} cambio(s) de índices de Jellyfin {JellyfinVersion} se aplican en segundo plano cuando termine el arranque"
                    + (applied > 0 ? $"; ya se aplicaron {applied} cambio(s) de tablas y columnas." : "."));
            }

            var unresolved = plan.Where(p => states[p] != OperationState.Satisfied).ToList();
            if (unresolved.Count > 0)
            {
                // Applied once for this Jellyfin and provider version, and the database still differs: retrying on
                // every start would rebuild the same index forever, so it is reported instead.
                return Publish(
                    logger,
                    mode,
                    SchemaDriftOutcome.Detected,
                    Report(plan, states),
                    $"{unresolved.Count} cambio(s) de esquema de Jellyfin {JellyfinVersion} ya se aplicaron en esta versión, pero la base sigue sin coincidir con el modelo "
                    + $"({string.Join("; ", unresolved.Select(o => o.Operation.Description))}). No se reintentan hasta la próxima versión de Jellyfin o del proveedor.");
            }

            if (applied == 0)
            {
                return Publish(
                    logger,
                    mode,
                    SchemaDriftOutcome.AlreadyHealed,
                    Report(plan, states),
                    $"{plan.Count} cambio(s) de esquema de Jellyfin {JellyfinVersion} ya están en la base; desaparecen del informe al actualizar el proveedor.");
            }

            return Publish(
                logger,
                mode,
                SchemaDriftOutcome.Healed,
                Report(plan, states),
                $"Aplicados {applied} cambio(s) de esquema de Jellyfin {JellyfinVersion} que el proveedor {ProviderVersion} aún no incluye: "
                + string.Join("; ", pendingOps.Select(o => o.Operation.Description)) + ".");
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
    /// <remarks>
    /// Unique indexes are excluded: existing duplicates make them fail, and Jellyfin's own migrations clean those up
    /// with SQL the model diff does not contain. Columns are only safe when PostgreSQL adds them without rewriting the
    /// table: nullable without a default, or with a constant default. A computed column or a SQL default (which may be
    /// volatile, such as <c>now()</c>) rewrites the table under an exclusive lock.
    /// </remarks>
    /// <param name="operation">Operation.</param>
    /// <returns><see langword="true"/> for additive operations.</returns>
    internal static bool IsSafe(MigrationOperation operation)
        => operation switch
        {
            CreateIndexOperation c => !c.IsUnique,
            DropIndexOperation or CreateTableOperation or EnsureSchemaOperation => true,
            AddColumnOperation a => a.ComputedColumnSql is null
                && a.DefaultValueSql is null
                && (a.IsNullable || a.DefaultValue is not null),
            _ => false,
        };

    /// <summary>Turns an index build or drop into its <c>CONCURRENTLY</c> form, which does not block writes.</summary>
    /// <param name="sql">One <c>CREATE INDEX</c> or <c>DROP INDEX</c> statement; anything else is returned unchanged.</param>
    /// <returns>The statement to run outside a transaction.</returns>
    internal static string Concurrently(string sql) => ConcurrentIndexStatement().Replace(sql, "$1 CONCURRENTLY ");

    private static string OperationName(MigrationOperation operation)
    {
        var name = operation.GetType().Name;
        return name.EndsWith("Operation", StringComparison.Ordinal) ? name[..^"Operation".Length] : name;
    }

    /// <summary>The model diff between <paramref name="baseline"/> (default: the provider's snapshot) and Jellyfin's runtime model.</summary>
    /// <param name="context">A Jellyfin context.</param>
    /// <param name="baseline">Model the database is assumed to have, or <see langword="null"/> for the snapshot.</param>
    /// <returns>The operations a migration would contain.</returns>
    internal static IReadOnlyList<MigrationOperation> ComputeDifferences(DbContext context, IModel? baseline = null)
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

    /// <summary>
    /// The operations to bring the database to Jellyfin's model: the model diff, plus every index of the model whose
    /// definition in the database differs (or is missing) and every missing table or column the diff does not explain.
    /// </summary>
    /// <remarks>
    /// The second part checks the database itself. A provider migration made idempotent can run over an object created
    /// earlier with another definition; comparing snapshot and model would never notice, the catalog does.
    /// </remarks>
    private static List<PlannedOperation> Plan(DbContext context, IReadOnlyList<MigrationOperation> differences, SchemaFacts facts)
    {
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var model = context.GetService<IDesignTimeModel>().Model;
        var plan = new List<PlannedOperation>(differences.Count);

        void Add(MigrationOperation operation, string? note = null)
        {
            var commands = generator.Generate([operation], model);
            var sql = string.Concat(commands.Select(c => IdempotentMigrationsSqlGenerator.MakeIdempotent(c.CommandText)));
            var safe = IsSafe(operation) && commands.All(c => !c.TransactionSuppressed);
            var description = note is null ? Describe(operation) : $"{Describe(operation)} ({note})";
            plan.Add(new PlannedOperation(new SchemaDriftOperation(description, sql, safe, Applied: false), operation, Key(sql)));
        }

        void AddUnsafe(string description)
            => plan.Add(new PlannedOperation(new SchemaDriftOperation(description, string.Empty, Safe: false, Applied: false), null, Key(description)));

        // What the diff already explains, per object: a column added to a table does not exempt the table's other
        // columns and indexes from the catalog check.
        var coveredIndexes = new HashSet<string>(StringComparer.Ordinal);
        var coveredTables = new HashSet<string>(StringComparer.Ordinal);
        var coveredColumns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in differences)
        {
            Add(operation);
            switch (operation)
            {
                case CreateIndexOperation c: coveredIndexes.Add(c.Name); break;
                case DropIndexOperation d: coveredIndexes.Add(d.Name); break;
                case RenameIndexOperation r: coveredIndexes.Add(r.Name); coveredIndexes.Add(r.NewName); break;
                case CreateTableOperation t: coveredTables.Add(t.Name); break;
                case DropTableOperation t: coveredTables.Add(t.Name); break;
                case RenameTableOperation t: coveredTables.Add(t.Name); coveredTables.Add(t.NewName ?? t.Name); break;
                case RenameColumnOperation c: coveredColumns.Add($"{c.Table}.{c.Name}"); coveredColumns.Add($"{c.Table}.{c.NewName}"); break;
                case ColumnOperation c: coveredColumns.Add($"{c.Table}.{c.Name}"); break;
                case DropColumnOperation c: coveredColumns.Add($"{c.Table}.{c.Name}"); break;
            }
        }

        foreach (var table in model.GetRelationalModel().Tables)
        {
            if (coveredTables.Contains(table.Name))
            {
                continue;
            }

            if (!facts.Tables.Contains(table.Name))
            {
                AddUnsafe($"MissingTable {table.Name} (falta en la base y ninguna migración la crea)");
                continue;
            }

            foreach (var column in table.Columns)
            {
                var key = string.Concat(table.Name, ".", column.Name);
                if (!coveredColumns.Contains(key) && !facts.Columns.Contains(key))
                {
                    AddUnsafe($"MissingColumn {table.Name}.{column.Name} (falta en la base y ninguna migración la crea)");
                }
            }

            foreach (var index in table.Indexes)
            {
                if (coveredIndexes.Contains(index.Name))
                {
                    continue;
                }

                var create = CreateIndexOperation.CreateFrom(index);
                if (facts.Matches(create))
                {
                    continue;
                }

                var exists = facts.TryGetIndex(index.Name, out _);
                if (exists)
                {
                    Add(new DropIndexOperation { Name = index.Name, Table = table.Name, Schema = table.Schema }, "definición distinta del modelo");
                }

                Add(create, exists ? "definición distinta del modelo" : "falta en la base");
            }
        }

        return plan;
    }

    /// <summary>Whether each planned operation is already reflected in the database.</summary>
    /// <remarks>
    /// An operation not reflected yet counts as <see cref="OperationState.Retried"/> only when the guard already applied
    /// it for this Jellyfin and provider version and the object still looks exactly as the guard left it: applying it
    /// again would end the same way. If the object changed since (a rollback to an older Jellyfin rebuilt it, someone
    /// altered it), it is pending again. A drop paired with a create of the same name is part of replacing that index and
    /// takes the create's state.
    /// </remarks>
    private static Dictionary<PlannedOperation, OperationState> Evaluate(List<PlannedOperation> plan, SchemaFacts facts, Dictionary<string, string?> recorded)
    {
        var creates = new Dictionary<string, PlannedOperation>(StringComparer.Ordinal);
        foreach (var step in plan)
        {
            if (step.Migration is CreateIndexOperation create)
            {
                creates.TryAdd(create.Name, step);
            }
        }

        var states = new Dictionary<PlannedOperation, OperationState>();
        foreach (var step in plan)
        {
            if (step.Migration is DropIndexOperation drop && creates.ContainsKey(drop.Name))
            {
                continue;
            }

            var satisfied = step.Migration switch
            {
                CreateIndexOperation c => facts.Matches(c),
                DropIndexOperation d => !facts.TryGetIndex(d.Name, out _),
                CreateTableOperation t => facts.Tables.Contains(t.Name),
                AddColumnOperation a => facts.Columns.Contains(string.Concat(a.Table, ".", a.Name)),
                EnsureSchemaOperation => true,
                _ => false,
            };

            states[step] = satisfied
                ? OperationState.Satisfied
                : step.Operation.Safe && step.Migration is not null
                    && recorded.TryGetValue(step.Key, out var result)
                    && string.Equals(result, facts.Fingerprint(step.Migration), StringComparison.Ordinal)
                    ? OperationState.Retried
                    : OperationState.Pending;
        }

        foreach (var step in plan)
        {
            if (step.Migration is DropIndexOperation drop && creates.TryGetValue(drop.Name, out var create))
            {
                states[step] = states[create];
            }
        }

        return states;
    }

    private static List<SchemaDriftOperation> Report(List<PlannedOperation> plan, Dictionary<PlannedOperation, OperationState> states)
        => plan.Select(p => states[p] switch
        {
            OperationState.Satisfied => p.Operation with { Applied = true },
            OperationState.Retried => p.Operation with { Description = p.Operation.Description + " [ya aplicado en esta versión; la base sigue sin coincidir]" },
            _ => p.Operation,
        }).ToList();

    // Keyed on the Jellyfin and provider versions as well: the same SQL in a later version is a new change, not one
    // that is already done (a DROP INDEX IF EXISTS hashes the same whatever definition it replaces).
    private static string Key(string sql)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', JellyfinVersion, ProviderVersion, sql))))[..32];

    private static async Task<Dictionary<string, string?>> ReadRecordedAsync(DbContext context, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
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
            // Through to_jsonb: tables created before the Result column existed have no such column yet.
            read.CommandText = $"SELECT \"OperationHash\", to_jsonb(r) ->> 'Result' FROM \"{RecordTable}\" r;";
            using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result[reader.GetString(0)] = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(1);
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
    private static async Task<int> ApplyAsync(DbContext context, List<PlannedOperation> plan, List<PlannedOperation> steps, CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        var opened = await OpenAsync(context, connection, cancellationToken).ConfigureAwait(false);
        try
        {
            await AcquireLockAsync(connection, cancellationToken).ConfigureAwait(false);
            try
            {
                await ExecuteAsync(
                    connection,
                    null,
                    $"""
                    CREATE TABLE IF NOT EXISTS "{RecordTable}" (
                        "OperationHash" text PRIMARY KEY,
                        "Description" text NOT NULL,
                        "Sql" text NOT NULL,
                        "JellyfinVersion" text NOT NULL,
                        "ProviderVersion" text NOT NULL,
                        "AppliedAt" timestamptz NOT NULL DEFAULT now());
                    ALTER TABLE "{RecordTable}" ADD COLUMN IF NOT EXISTS "Result" text;
                    """,
                    cancellationToken).ConfigureAwait(false);

                // Another server may have applied some of the changes while this one waited for the lock.
                var facts = await SchemaFacts.LoadAsync(context, cancellationToken).ConfigureAwait(false);
                var states = Evaluate(plan, facts, await ReadRecordedAsync(context, cancellationToken).ConfigureAwait(false));
                var done = new List<PlannedOperation>();
                var replacedDrops = 0;
                foreach (var step in steps.Where(p => states[p] == OperationState.Pending))
                {
                    switch (step.Migration)
                    {
                        // Part of replacing the index: the CreateIndex of the same name builds the new definition first.
                        case DropIndexOperation drop when plan.Any(p => p.Migration is CreateIndexOperation c && c.Name == drop.Name):
                            replacedDrops++;
                            continue;
                        case CreateIndexOperation create:
                            PostgresLog.Info($"[SchemaDrift] Aplicando {step.Operation.Description}");
                            await BuildIndexAsync(connection, create, step.Operation.Sql, facts.TryGetIndex(create.Name, out _), cancellationToken).ConfigureAwait(false);
                            break;
                        case DropIndexOperation:
                            PostgresLog.Info($"[SchemaDrift] Aplicando {step.Operation.Description}");
                            await ExecuteAsync(connection, null, Concurrently(step.Operation.Sql), cancellationToken, noTimeout: true).ConfigureAwait(false);
                            break;
                        default:
                            PostgresLog.Info($"[SchemaDrift] Aplicando {step.Operation.Description}");
                            using (var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
                            {
                                await ExecuteAsync(connection, transaction, step.Operation.Sql, cancellationToken).ConfigureAwait(false);
                                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                            }

                            break;
                    }

                    done.Add(step);
                }

                // What each change left in the catalog: a later check retries it only if that has changed since.
                facts = await SchemaFacts.LoadAsync(context, cancellationToken).ConfigureAwait(false);
                foreach (var step in done)
                {
                    await RecordAsync(connection, step, facts.Fingerprint(step.Migration!), CancellationToken.None).ConfigureAwait(false);
                }

                return done.Count + replacedDrops;
            }
            finally
            {
                await ExecuteAsync(connection, null, $"SELECT pg_advisory_unlock({AdvisoryLockKey.ToString(CultureInfo.InvariantCulture)});", CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            if (opened)
            {
                await context.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Builds an index of the model without blocking writes on its table.</summary>
    /// <remarks>
    /// An index that exists with another definition is replaced by building the new one under a temporary name and
    /// swapping it in, so whatever interrupts the build (a cancelled request, a shutdown, an error) the table keeps its
    /// old index: an interrupted <c>CONCURRENTLY</c> build only leaves an invalid temporary index, which the next attempt
    /// drops first.
    /// </remarks>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The SQL is generated by EF Core from Jellyfin's own model; names are quoted identifiers from the same model.")]
    private static async Task BuildIndexAsync(DbConnection connection, CreateIndexOperation create, string sql, bool replacing, CancellationToken cancellationToken)
    {
        var temporary = Quote(TemporaryIndexName(create.Name));
        await ExecuteAsync(connection, null, $"DROP INDEX CONCURRENTLY IF EXISTS {temporary};", cancellationToken, noTimeout: true).ConfigureAwait(false);
        if (!replacing)
        {
            await ExecuteAsync(connection, null, Concurrently(sql), cancellationToken, noTimeout: true).ConfigureAwait(false);
            return;
        }

        var modelName = Quote(create.Name);
        var at = sql.IndexOf(modelName, StringComparison.Ordinal);
        if (at < 0)
        {
            throw new InvalidOperationException($"El SQL del índice no contiene su nombre {modelName}: {sql}");
        }

        await ExecuteAsync(connection, null, Concurrently(string.Concat(sql.AsSpan(0, at), temporary, sql.AsSpan(at + modelName.Length))), cancellationToken, noTimeout: true).ConfigureAwait(false);

        // The swap itself must not stop halfway: after the drop, the index only exists under its temporary name.
        await ExecuteAsync(connection, null, $"DROP INDEX CONCURRENTLY IF EXISTS {Quote(SchemaFacts.StoredName(create.Name))};", CancellationToken.None, noTimeout: true).ConfigureAwait(false);
        await ExecuteAsync(connection, null, $"ALTER INDEX {temporary} RENAME TO {Quote(SchemaFacts.StoredName(create.Name))};", CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Name under which a replacement index is built: short, and the same on every attempt for that index.</summary>
    /// <param name="name">Index name in the model.</param>
    /// <returns>The temporary name.</returns>
    internal static string TemporaryIndexName(string name)
        => "IX_drift_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..16];

    /// <summary>Takes the session lock that lets one server at a time apply changes.</summary>
    /// <remarks>
    /// It is a session lock because <c>CREATE INDEX CONCURRENTLY</c> cannot run in a transaction, and it is polled with
    /// short <c>pg_try_advisory_lock</c> calls instead of a blocking <c>pg_advisory_lock</c>: a session waiting inside a
    /// statement holds a snapshot, and a concurrent index build waits for every older snapshot to end, so the two
    /// servers would wait for each other.
    /// </remarks>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Constant statement; the key is a compile-time constant.")]
    private static async Task AcquireLockAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + LockWait;
        var logged = false;
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT pg_try_advisory_lock({AdvisoryLockKey.ToString(CultureInfo.InvariantCulture)});";
        while (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Otro servidor lleva más de {LockWait.TotalMinutes:F0} min aplicando cambios de esquema en esta base.");
            }

            if (!logged)
            {
                PostgresLog.Info("[SchemaDrift] Otro servidor está aplicando cambios de esquema en esta base; se espera a que termine.");
                logged = true;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RecordAsync(DbConnection connection, PlannedOperation step, string result, CancellationToken cancellationToken)
    {
        using var record = connection.CreateCommand();
        record.CommandText = $"""
            INSERT INTO "{RecordTable}" ("OperationHash", "Description", "Sql", "JellyfinVersion", "ProviderVersion", "Result")
            VALUES (@hash, @description, @sql, @jellyfin, @provider, @result)
            ON CONFLICT ("OperationHash") DO UPDATE SET "Result" = EXCLUDED."Result", "AppliedAt" = now();
            """;
        AddParameter(record, "hash", step.Key);
        AddParameter(record, "description", step.Operation.Description);
        AddParameter(record, "sql", step.Operation.Sql);
        AddParameter(record, "jellyfin", JellyfinVersion);
        AddParameter(record, "provider", ProviderVersion);
        AddParameter(record, "result", result);
        await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
    private static async Task ExecuteAsync(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken cancellationToken, bool noTimeout = false)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        // An index build on a large table outlasts the command timeout; cancelling it halfway only leaves an invalid index.
        if (noTimeout)
        {
            command.CommandTimeout = 0;
        }

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

    [GeneratedRegex(@"^(\s*(?:CREATE(?:\s+UNIQUE)?\s+INDEX|DROP\s+INDEX))\s+(?!CONCURRENTLY\b)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ConcurrentIndexStatement();

    private sealed record PlannedOperation(SchemaDriftOperation Operation, MigrationOperation? Migration, string Key);
}
