namespace Jellyfin.Database.Providers.Postgres.Controllers.Models;

/// <summary>Request body for activating PostgreSQL.</summary>
public sealed class ActivateRequest
{
    /// <summary>Gets or sets the connection string to write to database.xml.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Gets or sets the EF command timeout in seconds.</summary>
    public int CommandTimeout { get; set; } = 60;
}
