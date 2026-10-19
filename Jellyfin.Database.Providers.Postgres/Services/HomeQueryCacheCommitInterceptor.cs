using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// Drops the shared query cache entries when a transaction commits. The writes of a transaction are invisible
/// to other connections until then, so a read that ran in between could store the old rows under a generation
/// that was already raised by the write itself.
/// </summary>
public sealed class HomeQueryCacheCommitInterceptor : DbTransactionInterceptor
{
    /// <inheritdoc />
    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
        => HomeQueryCacheInterceptor.InvalidateAll();

    /// <inheritdoc />
    public override Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        HomeQueryCacheInterceptor.InvalidateAll();
        return Task.CompletedTask;
    }
}
