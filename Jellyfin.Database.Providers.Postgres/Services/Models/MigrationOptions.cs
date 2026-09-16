using MediaBrowser.Common.Configuration;

namespace Jellyfin.Database.Providers.Postgres.Services.Models;

/// <summary>
/// Encapsulates all options needed to run a SQLite → PostgreSQL migration.
/// Using a parameter object keeps <see cref="Services.MigrationEngine.RunAsync"/> within
/// the 7-parameter SonarQube limit (S107) and makes call-sites more readable.
/// </summary>
/// <param name="SqlitePath">Absolute path to the source SQLite database file.</param>
/// <param name="PostgresConnectionString">PostgreSQL connection string for the target database.</param>
/// <param name="Schema">Target PostgreSQL schema name (e.g. "public").</param>
/// <param name="BatchSize">Number of rows per insert batch.</param>
/// <param name="Truncate">When <see langword="true"/>, truncates each table before copying data.</param>
/// <param name="ApplicationPaths">Jellyfin application paths, used to locate the default SQLite file.</param>
public sealed record MigrationOptions(
    string SqlitePath,
    string PostgresConnectionString,
    string Schema,
    int BatchSize,
    bool Truncate,
    IApplicationPaths? ApplicationPaths);
