using System.Xml.Serialization;
using Jellyfin.Database.Implementations.DbConfiguration;
using Npgsql;
using Xunit;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>
/// <c>database.xml</c> is the only thing that activates the provider, and Jellyfin parses it with its own
/// serializer. These tests pin the file the plugin writes to the type the server deserialises.
/// </summary>
public sealed class DatabaseConfigurationTests
{
    [Fact]
    public void WrittenDatabaseXmlRoundTripsThroughJellyfinOptions()
    {
        var root = Directory.CreateTempSubdirectory("jellyfin-database-xml-");
        try
        {
            var paths = TestPaths.Create(root.FullName);

            // The password carries XML special characters on purpose: the connection string is written
            // inside an XML element, so it has to survive escaping and unescaping.
            const string connectionString = "Host=127.0.0.1;Port=5432;Database=Jellyfin;Username=Jellyfin;Password=\"p&w<x>y\"";
            PostgresPlugin.WriteDatabaseXml(paths, connectionString, 600);

            DatabaseConfigurationOptions? options;
            using (var stream = File.OpenRead(Path.Combine(root.FullName, "database.xml")))
            {
                options = (DatabaseConfigurationOptions?)new XmlSerializer(typeof(DatabaseConfigurationOptions)).Deserialize(stream);
            }

            Assert.NotNull(options);
            Assert.Equal("PLUGIN_PROVIDER", options!.DatabaseType, ignoreCase: true);

            var custom = Assert.IsType<CustomDatabaseOptions>(options.CustomProviderOptions);

            // Jellyfin locates the plugin folder by prefix, so PluginName must stay the display name.
            Assert.Equal("PostgreSQL Database Provider", custom.PluginName);
            Assert.Equal(
                Path.GetFileName(typeof(PostgresDatabaseProvider).Assembly.Location),
                Path.GetFileName(Path.ChangeExtension(custom.PluginAssembly, "dll")),
                ignoreCase: true);
            Assert.Contains(
                custom.Options,
                option => string.Equals(option.Key, "command-timeout", StringComparison.OrdinalIgnoreCase) && option.Value == "600");

            // WriteDatabaseXml normalises the connection string through NpgsqlConnectionStringBuilder,
            // so the values are compared instead of the raw text.
            var readBack = PostgresPlugin.ReadActivePgConnectionString(paths);
            Assert.NotNull(readBack);
            var actual = new NpgsqlConnectionStringBuilder(readBack);
            var expected = new NpgsqlConnectionStringBuilder(connectionString);
            Assert.Equal(expected.Host, actual.Host);
            Assert.Equal(expected.Port, actual.Port);
            Assert.Equal(expected.Database, actual.Database);
            Assert.Equal(expected.Username, actual.Username);
            Assert.Equal(expected.Password, actual.Password);

            // The provider queries with the schema as search path, so raw SQL resolves where the tables are.
            Assert.False(string.IsNullOrWhiteSpace(actual.SearchPath));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void EngineSwitchIsDetectedFromDatabaseXml()
    {
        var root = Directory.CreateTempSubdirectory("jellyfin-engine-");
        try
        {
            var paths = TestPaths.Create(root.FullName);
            Assert.False(PostgresPlugin.IsPostgresActive(paths));

            PostgresPlugin.WriteDatabaseXml(paths, "Host=127.0.0.1;Database=Jellyfin;Username=Jellyfin;Password=x");

            Assert.True(PostgresPlugin.IsPostgresActive(paths));
            Assert.Equal("Jellyfin", new NpgsqlConnectionStringBuilder(PostgresPlugin.ReadActivePgConnectionString(paths)).Database);
        }
        finally
        {
            root.Delete(true);
        }
    }
}
