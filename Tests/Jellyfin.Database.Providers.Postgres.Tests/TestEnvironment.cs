using System.Reflection;
using System.Runtime.CompilerServices;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Postgres.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>
/// Shared context for the suite: repository paths, the scratch database and the plugin logger.
/// </summary>
internal static class TestEnvironment
{
    /// <summary>Environment variable holding an admin connection string used only to create scratch databases.</summary>
    internal const string ConnectionVariable = "POSTGRES_TEST_CONNECTION";

    /// <summary>Optional environment variable that enables the pg_dump based integration tests.</summary>
    internal const string BackupsVariable = "POSTGRES_TEST_BACKUPS";

    private static string? _repositoryRoot;

    internal static string? PostgresConnection => Environment.GetEnvironmentVariable(ConnectionVariable);

    internal static bool PostgresAvailable => !string.IsNullOrWhiteSpace(PostgresConnection);

    internal static bool BackupTestsEnabled => Environment.GetEnvironmentVariable(BackupsVariable) == "1";

    internal static string RepositoryRoot => _repositoryRoot ??= FindRepositoryRoot();

    internal static string PluginSourceDirectory => Path.Combine(RepositoryRoot, "Jellyfin.Database.Providers.Postgres");

    /// <summary>
    /// Builds the context Jellyfin itself uses, so the model under test is the server's own one.
    /// </summary>
    /// <param name="connectionString">Connection string of the database to point at.</param>
    /// <returns>The context.</returns>
    internal static JellyfinDbContext CreateModelContext(string connectionString = "Host=localhost;Database=unused;Username=unused")
        => new(
            new DbContextOptionsBuilder<JellyfinDbContext>()
                .UseNpgsql(connectionString, pg => pg.MigrationsAssembly(typeof(PostgresDatabaseProvider).Assembly.FullName))
                .Options,
            NullLogger<JellyfinDbContext>.Instance,
            new PostgresDatabaseProvider(null, null),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));

    /// <summary>
    /// Creates an empty database for a single test. The connection string in the environment variable is
    /// only used to issue CREATE DATABASE, never as the database under test, so a run cannot touch real data.
    /// </summary>
    /// <returns>Name and connection string of the scratch database.</returns>
    internal static async Task<ScratchDatabase> CreateScratchDatabaseAsync()
    {
        var admin = PostgresConnection
            ?? throw new InvalidOperationException($"{ConnectionVariable} is not set.");
        var name = string.Concat("jellyfin_provider_test_", Guid.NewGuid().ToString("N"));

        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand(string.Concat("CREATE DATABASE \"", name, "\""), connection);
            await create.ExecuteNonQueryAsync();
        }

        return new ScratchDatabase(name, new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString);
    }

    /// <summary>Scratch database created exclusively for one test.</summary>
    /// <param name="Name">Database name.</param>
    /// <param name="ConnectionString">Connection string pointing at it.</param>
    internal sealed record ScratchDatabase(string Name, string ConnectionString);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Jellyfin.Database.Providers.Postgres.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException($"Repository root not found above '{AppContext.BaseDirectory}'.");
    }
}

/// <summary>
/// Redirects the plugin log to a temporary directory for the whole run, so executing the suite never
/// writes to the log directory of an installed server.
/// </summary>
internal static class TestModule
{
    internal static string LogDirectory { get; } = Path.Combine(Path.GetTempPath(), "jellyfin-plugin-tests-log");

    [ModuleInitializer]
    internal static void Initialize() => PostgresLog.SetLogDirectory(LogDirectory);
}
