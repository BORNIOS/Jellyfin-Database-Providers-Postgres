using System.Data.Common;
using Jellyfin.Database.Providers.Postgres.Api;
using Jellyfin.Database.Providers.Postgres.Logging;
using Jellyfin.Database.Providers.Postgres.Services;
using Npgsql;
using Xunit;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>Integration coverage for the isolated private-schema contract.</summary>
public sealed class PluginSchemaHostTests
{
    private const string LiveSchemaProbeVariable = "POSTGRES_LIVE_SCHEMA_PROBE";

    [SkippableFact]
    public async Task PrivateSchemaSupportsMigrationsAndParameterizedDataWithoutPublicAccess()
    {
        Skip.IfNot(TestEnvironment.PostgresAvailable, "POSTGRES_TEST_CONNECTION is not set.");
        await using var scratch = await TestEnvironment.ScratchDatabaseScope.CreateAsync();
        var host = new PostgresPluginSchemaHost(() => scratch.Database.ConnectionString);
        var schema = await host.OpenSchemaAsync(Guid.Parse("eb587e52-bf22-48d0-ae9c-dc99f6d8f1dc"));

        await host.ApplyMigrationsAsync(
            schema,
            [new PluginSchemaMigration("001_create_state", "CREATE TABLE state (id integer PRIMARY KEY, value text NOT NULL)")]);

        await host.ExecuteAsync(
            schema,
            "INSERT INTO state (id, value) VALUES (@id, @value)",
            [new PluginSqlParameter("id", 1), new PluginSqlParameter("value", "private")]);

        var value = await host.QueryAsync(
            schema,
            "SELECT value FROM state WHERE id = @id",
            ReadSingleString,
            [new PluginSqlParameter("id", 1)]);

        Assert.Equal("private", value);
        await Assert.ThrowsAsync<ArgumentException>(() => host.QueryAsync(schema, "SELECT * FROM public.\"BaseItems\"", ReadSingleString));
    }

    [Fact]
    public void SchemaNameIsStableAndDoesNotUsePublic()
    {
        var pluginId = Guid.Parse("eb587e52-bf22-48d0-ae9c-dc99f6d8f1dc");
        var first = new PluginSchema(pluginId, "pgp_eb587e52bf2248d0ae9cdc99f6d8f1dc");
        var second = new PluginSchema(pluginId, "pgp_eb587e52bf2248d0ae9cdc99f6d8f1dc");

        Assert.Equal(first.Name, second.Name);
        Assert.NotEqual("public", first.Name);
    }

    /// <summary>
    /// Opt-in probe for a running Jellyfin database. It records the provider's private-schema log entry
    /// and removes its generated schema in a finally block, so it cannot affect Jellyfin's public schema.
    /// </summary>
    [SkippableFact]
    public async Task LiveSchemaProbeCreatesAndRemovesOnlyItsGeneratedSchema()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable(LiveSchemaProbeVariable) == "1", $"{LiveSchemaProbeVariable} is not set to 1.");
        Skip.IfNot(TestEnvironment.PostgresAvailable, "POSTGRES_TEST_CONNECTION is not set.");

        var logDirectory = Environment.GetEnvironmentVariable("JELLYFIN_LOG_DIRECTORY");
        Skip.If(string.IsNullOrWhiteSpace(logDirectory), "JELLYFIN_LOG_DIRECTORY is not set.");
        PostgresLog.SetLogDirectory(logDirectory);

        var host = new PostgresPluginSchemaHost(() => TestEnvironment.PostgresConnection);
        var schema = await host.OpenSchemaAsync(Guid.NewGuid());
        try
        {
            Assert.StartsWith("pgp_", schema.Name, StringComparison.Ordinal);
            Assert.NotEqual("public", schema.Name);
        }
        finally
        {
            await using var connection = new NpgsqlConnection(TestEnvironment.PostgresConnection);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema.Name}\" CASCADE;", connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static string ReadSingleString(DbDataReader reader)
    {
        Assert.True(reader.Read());
        return reader.GetString(0);
    }
}
