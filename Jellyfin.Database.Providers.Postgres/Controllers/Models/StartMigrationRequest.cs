namespace Jellyfin.Database.Providers.Postgres.Controllers.Models;

/// <summary>Request body for starting a migration.</summary>
public sealed class StartMigrationRequest
{
    /// <summary>Gets or sets the path to the SQLite database file.</summary>
    public string SqlitePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the PostgreSQL connection string to migrate to.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Gets or sets the target schema (default "public").</summary>
    public string Schema { get; set; } = "public";

    /// <summary>Gets or sets the insert batch size (default 1000).</summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>Gets or sets a value indicating whether to TRUNCATE tables before inserting.</summary>
    public bool Truncate { get; set; }
}
