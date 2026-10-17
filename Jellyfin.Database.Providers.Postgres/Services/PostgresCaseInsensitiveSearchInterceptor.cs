using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// Makes Jellyfin's built-in BaseItems title searches case-insensitive on PostgreSQL.
/// </summary>
internal sealed class PostgresCaseInsensitiveSearchInterceptor : DbCommandInterceptor
{
    private static readonly Regex LikeOperator = new(
        @"\bLIKE\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <inheritdoc />
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        RewriteSearch(command);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        RewriteSearch(command);
        return ValueTask.FromResult(result);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Jellyfin constructs the EF query with parameterized search terms; this only changes the LIKE operator.")]
    private static void RewriteSearch(DbCommand command)
    {
        var sql = command.CommandText;
        if (sql.Contains("FROM \"BaseItems\"", System.StringComparison.Ordinal)
            && sql.Contains("\"CleanName\"", System.StringComparison.Ordinal)
            && sql.Contains("\"OriginalTitle\"", System.StringComparison.Ordinal))
        {
            command.CommandText = LikeOperator.Replace(sql, "ILIKE");
        }
    }
}
