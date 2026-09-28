using System;
using System.Text.RegularExpressions;

namespace Jellyfin.Database.Providers.Postgres.Logging;

/// <summary>
/// Routes EF Core lifecycle, warning and error diagnostics into the PostgreSQL provider log.
/// </summary>
internal static class EfCoreDatabaseDiagnostics
{
    private const int MaxMessageLength = 2000;

    private static readonly Regex SecretRegex = new(
        @"(?i)(password|token|access[ _-]?key)\s*=\s*[^;\s,]+",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <summary>Writes one EF Core diagnostic without exposing connection secrets.</summary>
    /// <param name="message">Message emitted by EF Core.</param>
    public static void Write(string message)
    {
        var safe = SecretRegex.Replace(message, "$1=*****");
        if (safe.Length > MaxMessageLength)
        {
            safe = string.Concat(safe.AsSpan(0, MaxMessageLength), "…");
        }

        safe = string.Concat("[EF] ", safe);
        if (safe.Contains("fail:", StringComparison.OrdinalIgnoreCase))
        {
            PostgresLog.Error(safe);
        }
        else if (safe.Contains("warn:", StringComparison.OrdinalIgnoreCase))
        {
            PostgresLog.Warn(safe);
        }
        else
        {
            PostgresLog.Info(safe);
        }
    }
}
