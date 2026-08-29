using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Jellyfin.Database.Providers.Postgres.Logging;
using Jellyfin.Database.Providers.Postgres.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Database.Providers.Postgres;

/// <summary>
/// Plugin entry point for the PostgreSQL database provider.
/// Inherits <see cref="BasePlugin{TConfigurationType}"/> so that Jellyfin discovers it
/// as a proper plugin, provides configuration storage, and exposes a configuration page.
/// The actual database wiring lives in <see cref="PostgresDatabaseProvider"/>.
/// </summary>
public class PostgresPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private const string PluginDisplayName = "PostgreSQL Database Provider";
    private const string PluginAssemblyName = "Jellyfin.Database.Providers.Postgres.dll";

    private static PostgresPlugin? _instance;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresPlugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths provided by Jellyfin DI.</param>
    /// <param name="xmlSerializer">XML serializer provided by Jellyfin DI.</param>
    public PostgresPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        _instance = this;
        PostgresLog.SetLogDirectory(applicationPaths.LogDirectoryPath);
        PostgresLog.Info($"PostgreSQL Database Provider v{Version} loaded. Log directory: {applicationPaths.LogDirectoryPath}");

        // Log the active database engine so the plugin log always shows which mode Jellyfin is in
        var isPostgresActive = IsPostgresActive(applicationPaths);
        if (isPostgresActive)
        {
            var activeConnStr = ReadActivePgConnectionString(applicationPaths);
            // Mask password for safety
            var maskedConn = activeConnStr is not null
                ? System.Text.RegularExpressions.Regex.Replace(
                    activeConnStr,
                    @"(?i)(Password\s*=)[^;]+",
                    "$1*****")
                : "(unknown)";
            PostgresLog.Warn($"[ENGINE] Modo activo: PostgreSQL. Connection: {maskedConn}");
        }
        else
        {
            var sqlitePath = System.IO.Path.Combine(applicationPaths.DataPath, "jellyfin.db");
            var sqliteSize = System.IO.File.Exists(sqlitePath)
                ? $"{new System.IO.FileInfo(sqlitePath).Length / 1_048_576.0:F1} MB"
                : "(no encontrado)";
            PostgresLog.Warn($"[ENGINE] Modo activo: SQLite. Archivo: {sqlitePath} ({sqliteSize})");
        }
    }

    /// <summary>Gets the singleton instance of the plugin.</summary>
    public static PostgresPlugin? Instance => _instance;

    /// <inheritdoc/>
    public override Guid Id => new Guid("a2b5f3e8-4c1d-4f7a-9e6b-8d0c2f1a3b5e");

    /// <inheritdoc/>
    public override string Name => PluginDisplayName;

    /// <inheritdoc/>
    public override string Description => "Provides PostgreSQL as the Jellyfin database backend via EF Core + Npgsql.";

    /// <summary>
    /// Returns the embedded HTML configuration page served by the Jellyfin dashboard.
    /// </summary>
    /// <returns>A collection of plugin page descriptors.</returns>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = GetType().Namespace + ".Web.configurationPage.html"
        };
    }

    // ────────────────────────────────────────────────────────────────
    // Helpers for writing / reading database.xml
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes <c>database.xml</c> to activate the PostgreSQL provider on the next restart.
    /// </summary>
    /// <param name="applicationPaths">Application paths so we know where to write the file.</param>
    /// <param name="connectionString">The Npgsql connection string.</param>
    /// <param name="commandTimeout">EF command timeout in seconds.</param>
    public static void WriteDatabaseXml(IApplicationPaths applicationPaths, string connectionString, int commandTimeout = 60)
    {
        var configDir = applicationPaths.ConfigurationDirectoryPath;
        var filePath = Path.Combine(configDir, "database.xml");

        // Escape XML special characters in the connection string (passwords can contain &, <, > etc.)
        var escapedConnStr = SecurityElementHelper.Escape(connectionString) ?? connectionString;

        var xml = new StringBuilder();
        xml.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        xml.AppendLine("<DatabaseConfigurationOptions xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">");
        xml.AppendLine("  <DatabaseType>PLUGIN_PROVIDER</DatabaseType>");
        xml.AppendLine("  <LockingBehavior>NoLock</LockingBehavior>");
        xml.AppendLine("  <CustomProviderOptions>");
        // Jellyfin resolves custom providers by matching PluginName against the plugin folder prefix.
        // Installed repository plugins land in folders like "PostgreSQL Database Provider_1.0.0.0",
        // so the display name must be written here instead of the assembly/namespace name.
        xml.AppendLine(CultureInfo.InvariantCulture, $"    <PluginName>{PluginDisplayName}</PluginName>");
        xml.AppendLine(CultureInfo.InvariantCulture, $"    <PluginAssembly>{PluginAssemblyName}</PluginAssembly>");
        xml.AppendLine(CultureInfo.InvariantCulture, $"    <ConnectionString>{escapedConnStr}</ConnectionString>");
        xml.AppendLine("    <Options>");
        xml.AppendLine("      <CustomDatabaseOption>");
        xml.AppendLine("        <Key>command-timeout</Key>");
        xml.AppendLine(CultureInfo.InvariantCulture, $"        <Value>{commandTimeout}</Value>");
        xml.AppendLine("      </CustomDatabaseOption>");
        xml.AppendLine("      <CustomDatabaseOption>");
        xml.AppendLine("        <Key>EnableSensitiveDataLogging</Key>");
        xml.AppendLine("        <Value>False</Value>");
        xml.AppendLine("      </CustomDatabaseOption>");
        xml.AppendLine("    </Options>");
        xml.AppendLine("  </CustomProviderOptions>");
        xml.AppendLine("</DatabaseConfigurationOptions>");

        File.WriteAllText(filePath, xml.ToString(), Encoding.UTF8);
    }

    /// <summary>
    /// Reads the current <c>database.xml</c> and returns the connection string if the
    /// PostgreSQL provider is currently active, otherwise <see langword="null"/>.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin application paths.</param>
    /// <returns>The active PostgreSQL connection string, or <see langword="null"/> if not active.</returns>
    public static string? ReadActivePgConnectionString(IApplicationPaths applicationPaths)
    {
        var filePath = Path.Combine(applicationPaths.ConfigurationDirectoryPath, "database.xml");
        if (!File.Exists(filePath))
        {
            return null;
        }

        var content = File.ReadAllText(filePath);
        if (!content.Contains("PLUGIN_PROVIDER", StringComparison.Ordinal))
        {
            return null;
        }

        // Simple extraction — avoids needing a full XML deserializer at this call site.
        const string startTag = "<ConnectionString>";
        const string endTag = "</ConnectionString>";
        var start = content.IndexOf(startTag, StringComparison.Ordinal);
        var end = content.IndexOf(endTag, StringComparison.Ordinal);
        if (start < 0 || end < 0)
        {
            return null;
        }

        var raw = content.Substring(start + startTag.Length, end - start - startTag.Length).Trim();
        // Un-escape XML entities
        return raw
            .Replace("&amp;", "&", StringComparison.Ordinal)
            .Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&apos;", "'", StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns <see langword="true"/> if <c>database.xml</c> currently points to the
    /// PostgreSQL provider.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin application paths.</param>
    /// <returns><see langword="true"/> if PostgreSQL is the active provider.</returns>
    public static bool IsPostgresActive(IApplicationPaths applicationPaths)
        => ReadActivePgConnectionString(applicationPaths) is not null;
}
