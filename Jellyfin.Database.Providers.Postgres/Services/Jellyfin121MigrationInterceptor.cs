using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// Adapts Jellyfin 12.1's migration SQL to PostgreSQL: translates its SQLite-only JSON cleanup
/// and avoids the pointless row rewrites performed by <c>MigrateRatingLevels</c>.
/// </summary>
internal sealed class Jellyfin121MigrationInterceptor : DbCommandInterceptor
{
    internal const string SqliteCleanup = "json_remove(\"Data\", '$.LinkedChildren', '$.ExtraIds', '$.SupportsExternalTransfer')";

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Rewrite(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Rewrite(command);
        return ValueTask.FromResult(result);
    }

    private static void Rewrite(DbCommand command)
    {
        if (command.CommandText.Contains(SqliteCleanup, System.StringComparison.Ordinal)
            && command.CommandText.Contains("json_valid(\"Data\") = 1", System.StringComparison.Ordinal)
            && command.CommandText.TrimStart().StartsWith("UPDATE \"BaseItems\"", System.StringComparison.Ordinal))
        {
            // CASE guards the cast itself: PostgreSQL can reorder independent WHERE terms.
            command.CommandText = """
                UPDATE "BaseItems"
                SET "Data" = CASE WHEN "Data" IS JSON OBJECT
                    THEN ("Data"::jsonb - ARRAY['LinkedChildren', 'ExtraIds', 'SupportsExternalTransfer'])::text
                    ELSE "Data" END
                WHERE CASE WHEN "Data" IS JSON OBJECT
                    THEN "Data"::jsonb ?| ARRAY['LinkedChildren', 'ExtraIds', 'SupportsExternalTransfer']
                    ELSE false END;
                """;
            return;
        }

        RewriteRatingLevelsNullUpdate(command);
    }

    /// <summary>
    /// Restricted form of the null-rating UPDATE so it only touches rows whose value changes.
    /// </summary>
    /// <remarks>
    /// <c>MigrateRatingLevels</c> sets <c>InheritedParentalRatingValue</c> and
    /// <c>InheritedParentalRatingSubValue</c> to NULL for every item without a rating. Setting NULL
    /// on a row that is already NULL is a no-op, but PostgreSQL still rewrites the row (heap and
    /// TOAST), which over a large library takes longer than the command timeout and aborts the whole
    /// migration. Restricting the statement to rows that actually change keeps the result identical
    /// with a fraction of the write volume.
    /// </remarks>
    /// <param name="command">Command about to be executed.</param>
    private static void RewriteRatingLevelsNullUpdate(DbCommand command)
    {
        var sql = command.CommandText;
        if (!sql.Contains("\"OfficialRating\" IS NULL", System.StringComparison.Ordinal)
            || !sql.TrimStart().StartsWith("UPDATE \"BaseItems\"", System.StringComparison.Ordinal))
        {
            return;
        }

        if (sql.Contains("SET \"InheritedParentalRatingValue\" = NULL", System.StringComparison.Ordinal))
        {
            if (sql.Contains("\"InheritedParentalRatingValue\" IS NOT NULL", System.StringComparison.Ordinal))
            {
                return; // already rewritten
            }

            command.CommandText = """
                UPDATE "BaseItems" AS b
                SET "InheritedParentalRatingValue" = NULL
                WHERE (b."OfficialRating" IS NULL OR b."OfficialRating" = '')
                  AND b."InheritedParentalRatingValue" IS NOT NULL;
                """;
            return;
        }

        if (sql.Contains("SET \"InheritedParentalRatingSubValue\" = NULL", System.StringComparison.Ordinal)
            && !sql.Contains("\"InheritedParentalRatingSubValue\" IS NOT NULL", System.StringComparison.Ordinal))
        {
            command.CommandText = """
                UPDATE "BaseItems" AS b
                SET "InheritedParentalRatingSubValue" = NULL
                WHERE (b."OfficialRating" IS NULL OR b."OfficialRating" = '')
                  AND b."InheritedParentalRatingSubValue" IS NOT NULL;
                """;
        }
    }
}
