using System;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>Minimal no-op logger for contexts where no logger is injected.</summary>
internal sealed class MigrationNullLogger : ILogger
{
    internal static readonly MigrationNullLogger Instance = new();

    /// <inheritdoc/>
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc/>
    public bool IsEnabled(LogLevel logLevel) => false;

    /// <inheritdoc/>
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
    }
}
