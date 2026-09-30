using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// EF Core command interceptor that normalises <see cref="DateTime"/> parameters with
/// a PostgreSQL-compatible UTC value before each command is sent to PostgreSQL.
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
/// This interceptor preserves instants: UTC values are unchanged, local values are
/// converted to UTC, and unspecified values are explicitly treated as UTC. It applies
/// to reads as well because query predicates are parameters too.
/// </para>
/// </remarks>
public sealed class DateTimeKindNormalizingInterceptor : DbCommandInterceptor
{
    // Android's java.time cannot represent DateTime.MinValue after a timezone conversion. Npgsql also
    // maps this sentinel to PostgreSQL -infinity by default, which can later reach clients as an invalid
    // ISO date. Preserve a valid, explicit sentinel instead.
    private static readonly DateTime ClientSafeSentinelUtc = DateTime.UnixEpoch;

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
            if (param.Value is DateTime dt)
            {
                param.Value = NormalizeDateTime(dt);
            }
            else if (param.Value is DateTimeOffset offset)
            {
                param.Value = offset.ToUniversalTime();
            }
            else if (param.Value is DateTime[] dateTimes)
            {
                // Npgsql validates every element of timestamp arrays and rejects an entire parameter if
                // even one is Local or Unspecified. Preserve the array shape while normalizing each value.
                param.Value = Array.ConvertAll(dateTimes, NormalizeDateTime);
            }
            else if (param.Value is DateTimeOffset[] offsets)
            {
                param.Value = Array.ConvertAll(offsets, static offset => offset.ToUniversalTime());
            }
        }
    }

    private static DateTime NormalizeDateTime(DateTime value)
    {
        if (value == DateTime.MinValue)
        {
            return ClientSafeSentinelUtc;
        }

        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
    }
}
