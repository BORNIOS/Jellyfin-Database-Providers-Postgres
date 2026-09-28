namespace Jellyfin.Database.Providers.Postgres.Api;

/// <summary>
/// A named value bound to a private-schema SQL statement.
/// </summary>
/// <param name="Name">Parameter name without SQL syntax, for example <c>itemId</c>.</param>
/// <param name="Value">Value to bind. A null value is sent as <see cref="System.DBNull.Value"/>.</param>
public sealed record PluginSqlParameter(string Name, object? Value);
