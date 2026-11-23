using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Migrations;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// Migration SQL generator that makes the additive operations idempotent: <c>CREATE INDEX IF NOT EXISTS</c>,
/// <c>DROP INDEX IF EXISTS</c>, <c>CREATE TABLE IF NOT EXISTS</c> and <c>ADD COLUMN IF NOT EXISTS</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SchemaDriftGuard"/> applies the changes of a newer Jellyfin release before the provider ships the
/// migration for them. When that migration finally arrives it performs the same operations again; with the plain
/// generator it would stop at "relation already exists" and Jellyfin would refuse to start.
/// </para>
/// <para>
/// Only these four statement shapes are rewritten. On a database that does not have the object yet the result is
/// identical to the plain generator, so fresh installs and ordinary upgrades behave exactly as before.
/// </para>
/// </remarks>
[SuppressMessage("Usage", "EF1001:Internal EF Core API usage.", Justification = "The Npgsql generator can only be subclassed through its public constructor, which takes the provider's singleton options.")]
internal sealed partial class IdempotentMigrationsSqlGenerator : NpgsqlMigrationsSqlGenerator
{
    /// <summary>
    /// Initializes a new instance of the <see cref="IdempotentMigrationsSqlGenerator"/> class.
    /// </summary>
    /// <param name="dependencies">Generator dependencies.</param>
    /// <param name="npgsqlSingletonOptions">Npgsql singleton options.</param>
    public IdempotentMigrationsSqlGenerator(
        MigrationsSqlGeneratorDependencies dependencies,
        INpgsqlSingletonOptions npgsqlSingletonOptions)
        : base(dependencies, npgsqlSingletonOptions)
    {
    }

    /// <summary>Rewrites one generated statement into its idempotent form; other statements are returned unchanged.</summary>
    /// <param name="sql">Statement produced by the Npgsql generator.</param>
    /// <returns>The idempotent statement.</returns>
    internal static string MakeIdempotent(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        sql = CreateIndex().Replace(sql, "${head}IF NOT EXISTS ", 1);
        sql = DropIndex().Replace(sql, "${head}IF EXISTS ", 1);
        sql = CreateTable().Replace(sql, "${head}IF NOT EXISTS ", 1);
        return AddColumn().Replace(sql, "${head}ADD COLUMN IF NOT EXISTS ", 1);
    }

    /// <inheritdoc/>
    protected override void Generate(CreateIndexOperation operation, IModel? model, MigrationCommandListBuilder builder, bool terminate = true)
    {
        if (!terminate)
        {
            base.Generate(operation, model, builder, terminate);
            return;
        }

        var temp = new MigrationCommandListBuilder(Dependencies);
        base.Generate(operation, model, temp, terminate: true);
        AppendRewritten(temp, builder);
    }

    /// <inheritdoc/>
    protected override void Generate(DropIndexOperation operation, IModel? model, MigrationCommandListBuilder builder, bool terminate = true)
    {
        if (!terminate)
        {
            base.Generate(operation, model, builder, terminate);
            return;
        }

        var temp = new MigrationCommandListBuilder(Dependencies);
        base.Generate(operation, model, temp, terminate: true);
        AppendRewritten(temp, builder);
    }

    /// <inheritdoc/>
    protected override void Generate(CreateTableOperation operation, IModel? model, MigrationCommandListBuilder builder, bool terminate = true)
    {
        // EF's own bookkeeping tables: the Npgsql history repository already turns them into IF NOT EXISTS itself.
        if (!terminate || operation.Name.StartsWith("__EFMigrations", StringComparison.Ordinal))
        {
            base.Generate(operation, model, builder, terminate);
            return;
        }

        var temp = new MigrationCommandListBuilder(Dependencies);
        base.Generate(operation, model, temp, terminate: true);
        AppendRewritten(temp, builder);
    }

    /// <inheritdoc/>
    protected override void Generate(AddColumnOperation operation, IModel? model, MigrationCommandListBuilder builder, bool terminate = true)
    {
        if (!terminate)
        {
            base.Generate(operation, model, builder, terminate);
            return;
        }

        var temp = new MigrationCommandListBuilder(Dependencies);
        base.Generate(operation, model, temp, terminate: true);
        AppendRewritten(temp, builder);
    }

    private static void AppendRewritten(MigrationCommandListBuilder generated, MigrationCommandListBuilder builder)
    {
        foreach (var command in generated.GetCommandList())
        {
            builder.Append(MakeIdempotent(command.CommandText));
            builder.EndCommand(command.TransactionSuppressed);
        }
    }

    // Npgsql emits: CREATE [UNIQUE] INDEX [CONCURRENTLY] "name" ON ...
    [GeneratedRegex("""^(?<head>\s*CREATE\s+(?:UNIQUE\s+)?INDEX\s+(?:CONCURRENTLY\s+)?)(?!IF\s+NOT\s+EXISTS)(?=")""", RegexOptions.CultureInvariant)]
    private static partial Regex CreateIndex();

    // Npgsql emits: DROP INDEX [CONCURRENTLY] "schema"."name"
    [GeneratedRegex("""^(?<head>\s*DROP\s+INDEX\s+(?:CONCURRENTLY\s+)?)(?!IF\s+EXISTS)(?=")""", RegexOptions.CultureInvariant)]
    private static partial Regex DropIndex();

    // Npgsql emits: CREATE [UNLOGGED] TABLE "name" (
    [GeneratedRegex("""^(?<head>\s*CREATE\s+(?:UNLOGGED\s+)?TABLE\s+)(?!IF\s+NOT\s+EXISTS)(?=")""", RegexOptions.CultureInvariant)]
    private static partial Regex CreateTable();

    // Npgsql emits: ALTER TABLE "name" ADD "column" type ...; constraints are ADD CONSTRAINT and stay untouched.
    [GeneratedRegex("""^(?<head>\s*ALTER\s+TABLE\s+(?:"[^"]+"\.)?"[^"]+"\s+)ADD\s+(?=")""", RegexOptions.CultureInvariant)]
    private static partial Regex AddColumn();
}
