namespace Jellyfin.Database.Providers.Postgres.Controllers.Models;

/// <summary>Request body for testing / saving a connection string.</summary>
public sealed class ConnectionRequest
{
    /// <summary>Gets or sets the PostgreSQL connection string to test.</summary>
    public string ConnectionString { get; set; } = string.Empty;
}
