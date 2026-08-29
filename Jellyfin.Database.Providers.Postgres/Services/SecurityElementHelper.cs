namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// Shim for <c>System.Security.SecurityElement.Escape</c> — replaces XML special
/// characters with their entity equivalents. Used when writing <c>database.xml</c>.
/// </summary>
internal static class SecurityElementHelper
{
    /// <summary>
    /// Escapes XML special characters in <paramref name="text"/>.
    /// Returns <see langword="null"/> if <paramref name="text"/> is <see langword="null"/>.
    /// </summary>
    /// <param name="text">The text to escape.</param>
    /// <returns>The escaped text, or <see langword="null"/>.</returns>
    internal static string? Escape(string? text)
    {
        if (text is null)
        {
            return null;
        }

        return text
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
    }
}
