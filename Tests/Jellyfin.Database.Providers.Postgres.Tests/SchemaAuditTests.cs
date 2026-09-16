using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>
/// Audits the raw SQL of the plugin against Jellyfin's own EF model. This is the check that turns a
/// Jellyfin upgrade that renames or drops a table into a failing test instead of a runtime crash.
/// </summary>
public sealed partial class SchemaAuditTests
{
    // Objects the plugin uses that are not part of Jellyfin's EF model (EF Core bookkeeping).
    private static readonly string[] BookkeepingTables = ["__EFMigrationsHistory", "__EFMigrationsLock"];

    // Directories of the plugin whose raw SQL does not target the live schema. Paths use forward
    // slashes and the file path is normalised before comparing, so Windows and Linux behave the same.
    private static readonly string[] IgnoredSourcePaths = ["/bin/", "/obj/", "/Migrations/"];

    [Fact]
    public void EveryTableAndQualifiedColumnUsedByRawSqlExistsInTheModel()
    {
        using var context = TestEnvironment.CreateModelContext();
        var modelTables = context.Model.GetRelationalModel().Tables.ToDictionary(
            table => table.Name,
            table => table.Columns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

        var unknownTables = new SortedSet<string>(StringComparer.Ordinal);
        var unknownColumns = new SortedSet<string>(StringComparer.Ordinal);
        var references = 0;

        foreach (var file in Directory.EnumerateFiles(TestEnvironment.PluginSourceDirectory, "*.cs", SearchOption.AllDirectories))
        {
            if (IsIgnoredSourcePath(file))
            {
                continue;
            }

            foreach (Match match in SqlTableReference().Matches(File.ReadAllText(file)))
            {
                var table = match.Groups[1].Value;
                var column = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
                if (BookkeepingTables.Contains(table, StringComparer.Ordinal)
                    || table.StartsWith("pg_", StringComparison.OrdinalIgnoreCase)
                    || table.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!modelTables.TryGetValue(table, out var columns))
                {
                    unknownTables.Add($"{table} ({Path.GetFileName(file)})");
                    continue;
                }

                references++;
                if (!string.IsNullOrEmpty(column) && !columns.Contains(column))
                {
                    unknownColumns.Add($"{table}.{column} ({Path.GetFileName(file)})");
                }
            }
        }

        // A pattern that stops matching would turn the two assertions below into a vacuous pass.
        Assert.True(references >= 15, $"The auditor should find raw SQL references, it found {references}.");
        Assert.Empty(unknownTables);
        Assert.Empty(unknownColumns);
    }

    /// <summary>
    /// The EF snapshot has to describe the model Jellyfin 12.1 ships, otherwise the next migration
    /// generates SQL for a schema the server does not have.
    /// </summary>
    [Fact]
    public void SnapshotMatchesTheJellyfinModel()
    {
        using var context = TestEnvironment.CreateModelContext();
        Assert.False(context.Database.HasPendingModelChanges());
    }

    /// <summary>
    /// Path filtering has to behave identically on the developer machine (backslashes) and on the Linux
    /// runners (forward slashes): a separator-specific filter silently audited the migration files in CI.
    /// </summary>
    /// <param name="filePath">Source file path to classify.</param>
    /// <param name="expected">Whether the file must be skipped by the audit.</param>
    [Theory]
    [InlineData(@"D:\repo\Jellyfin.Database.Providers.Postgres\Migrations\20260915211546_Jellyfin121.cs", true)]
    [InlineData("/home/runner/work/repo/Jellyfin.Database.Providers.Postgres/Migrations/20260915211546_Jellyfin121.cs", true)]
    [InlineData(@"D:\repo\Jellyfin.Database.Providers.Postgres\obj\Debug\x.cs", true)]
    [InlineData("/home/runner/work/repo/Jellyfin.Database.Providers.Postgres/obj/Debug/x.cs", true)]
    [InlineData(@"D:\repo\Jellyfin.Database.Providers.Postgres\Services\MaintenanceService.cs", false)]
    [InlineData("/home/runner/work/repo/Jellyfin.Database.Providers.Postgres/Services/MaintenanceService.cs", false)]
    public void IgnoredSourcePathsMatchOnEveryPlatform(string filePath, bool expected)
        => Assert.Equal(expected, IsIgnoredSourcePath(filePath));

    private static bool IsIgnoredSourcePath(string filePath)
    {
        var normalized = filePath.Replace('\\', '/');
        return IgnoredSourcePaths.Any(path => normalized.Contains(path, StringComparison.OrdinalIgnoreCase));
    }

    // Table references in raw SQL: FROM/JOIN/INTO/ON/UPDATE/COPY/TRUNCATE followed by a quoted identifier,
    // plus an optional qualified column ("Table"."Column" or "Table".Column).
    [GeneratedRegex(
        """(?:\bFROM|\bJOIN|\bINTO|\bON|\bUPDATE|\bCOPY|\bTRUNCATE)\s+"([A-Za-z_][A-Za-z0-9_]*)"\s*(?:\.\s*"?([A-Za-z_][A-Za-z0-9_]*)"?)?""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SqlTableReference();
}
