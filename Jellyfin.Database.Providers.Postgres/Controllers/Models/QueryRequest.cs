namespace Jellyfin.Database.Providers.Postgres.Controllers.Models;

/// <summary>
/// Request of the ad-hoc SQL console. Only read-only statements are accepted; the service that validates
/// and executes it is <c>QueryConsoleService</c>.
/// </summary>
public class QueryRequest
{
    /// <summary>Gets or sets the statement to execute.</summary>
    public string Sql { get; set; } = string.Empty;

    /// <summary>Gets or sets the maximum number of rows to return.</summary>
    public int MaxRows { get; set; } = 200;

    /// <summary>
    /// Gets or sets a value indicating whether the execution plan should be returned instead of the rows.
    /// The plan is generated without ANALYZE, so the statement is only planned, never run for real.
    /// </summary>
    public bool Explain { get; set; }
}
