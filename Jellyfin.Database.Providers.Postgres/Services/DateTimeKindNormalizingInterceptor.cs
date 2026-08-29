using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// EF Core command interceptor that normalises <see cref="DateTime"/> parameters with
/// <see cref="DateTimeKind.Unspecified"/> to <see cref="DateTimeKind.Utc"/> before
/// each write command is sent to PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// Npgsql 9.x rejects <c>DateTime</c> values whose <c>Kind</c> is
/// <see cref="DateTimeKind.Unspecified"/> when the target column is
/// <c>timestamp with time zone</c>.  Jellyfin's external metadata providers
/// (TVDB, TMDB, …) produce air-date and premiere-date values without an explicit
/// <c>DateTimeKind</c>, causing <c>DbUpdateException</c> during library scans.
/// </para>
/// <para>
/// The previous workaround was to set the AppContext switch
/// <c>Npgsql.EnableLegacyTimestampBehavior = true</c>, but that switch also
/// disables the server-side timezone conversion for <em>reads</em>, forcing every
/// timestamp to be returned as UTC regardless of the PostgreSQL server's
/// <c>timezone</c> setting.
/// </para>
/// <para>
/// This interceptor applies the minimal fix — it only affects write parameters with
/// <c>Kind == Unspecified</c> — so the server timezone is honoured for read queries
/// while the <c>DbUpdateException</c> on writes is fully prevented.
/// </para>
/// </remarks>
public sealed class DateTimeKindNormalizingInterceptor : DbCommandInterceptor
{
    /// <inheritdoc />
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        NormalizeParameters(command);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        NormalizeParameters(command);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        NormalizeParameters(command);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        NormalizeParameters(command);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        NormalizeParameters(command);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        NormalizeParameters(command);
        return ValueTask.FromResult(result);
    }

    // ── Implementation ────────────────────────────────────────────────────────

    private static void NormalizeParameters(DbCommand command)
    {
        foreach (DbParameter param in command.Parameters)
        {
            if (param.DbType is not (DbType.DateTime or DbType.DateTime2 or DbType.DateTimeOffset))
            {
                continue;
            }

            if (param.Value is DateTime dt && dt.Kind == DateTimeKind.Unspecified)
            {
                // Treat Unspecified as UTC — matches what Jellyfin's SQLite path
                // assumes and what the legacy Npgsql switch used to do, but only
                // for write parameters, leaving read-side timezone handling intact.
                param.Value = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            }
        }
    }
}
