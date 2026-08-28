using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Providers.Postgres.Logging;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// Backup and restore operations using <c>pg_dump</c> / <c>psql</c>.
/// General maintenance methods are in <see cref="MaintenanceService"/>.
/// </summary>
public sealed class MaintenanceBackupService
{
    // Allowed executable base names — only these are accepted as custom paths (CA3006).
    private static readonly string[] AllowedPgExecutables = { "pg_dump", "pg_dumpall", "psql", "pg_restore" };

    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MaintenanceBackupService"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public MaintenanceBackupService(ILogger logger) => _logger = logger;

    // ── Backup ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a database backup using <c>pg_dump</c>.
    /// Optionally compresses the output into a ZIP.
    /// Returns the final backup file path.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string for the source database.</param>
    /// <param name="outputDirectory">Directory where the backup file will be written.</param>
    /// <param name="compress">When <see langword="true"/>, wraps the SQL dump in a ZIP file.</param>
    /// <param name="pgBinPath">Optional directory containing pg_dump, psql, etc. Leave null/empty to auto-detect or rely on PATH.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Absolute path to the created backup file.</returns>
    public async Task<string> CreateBackupAsync(
        string connectionString,
        string outputDirectory,
        bool compress,
        string? pgBinPath,
        CancellationToken ct = default)
    {
        var cs = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(cs.Database))
        {
            throw new InvalidOperationException("Connection string must contain Database.");
        }

        // Canonicalize to prevent path traversal (CA3003)
        var safeOutputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(safeOutputDirectory);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var safeDbName = MaintenanceService.SanitizeFileName(cs.Database ?? string.Empty);
        var sqlFilePath = Path.Combine(safeOutputDirectory, $"{safeDbName}_{timestamp}.sql");

        // Resolve + validate the executable path; name must be in AllowedPgExecutables (CA3006)
        var safeExecutable = ResolveExecutable(pgBinPath, "pg_dump");
        var args = BuildPgDumpArguments(cs, sqlFilePath);

        _logger.LogInformation("Starting PostgreSQL backup with pg_dump to {Path}", sqlFilePath);
        PostgresLog.Warn($"[Backup] INICIO: pg_dump → {sqlFilePath}");

        var psi = CreateProcessStartInfo(safeExecutable, args);

        if (!string.IsNullOrWhiteSpace(cs.Password))
        {
            psi.Environment["PGPASSWORD"] = cs.Password;
        }

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start pg_dump process.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        var stdOut = await stdoutTask.ConfigureAwait(false);
        var stdErr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stdErr) ? stdOut : stdErr;
            throw new InvalidOperationException(
                $"pg_dump failed (exit {process.ExitCode}). {detail}".Trim());
        }

        if (!SafeFileExists(sqlFilePath))
        {
            throw new InvalidOperationException("Backup completed but SQL output file was not created.");
        }

        if (!compress)
        {
            var sqlSize = MaintenanceService.FormatBytes(GetFileSize(sqlFilePath));
            _logger.LogInformation("PostgreSQL backup completed at {Path} ({Size})", sqlFilePath, sqlSize);
            PostgresLog.Warn($"[Backup] COMPLETADO: {sqlFilePath} ({sqlSize})");
            return sqlFilePath;
        }

        var zipPath = sqlFilePath + ".zip";
        if (SafeFileExists(zipPath))
        {
            SafeFileDelete(zipPath);
        }

        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(sqlFilePath, Path.GetFileName(sqlFilePath), CompressionLevel.Optimal);
        }

        SafeFileDelete(sqlFilePath);
        var zipSize = MaintenanceService.FormatBytes(GetFileSize(zipPath));
        _logger.LogInformation("PostgreSQL backup completed at {Path} ({Size})", zipPath, zipSize);
        PostgresLog.Warn($"[Backup] COMPLETADO (ZIP): {zipPath} ({zipSize})");
        return zipPath;
    }

    // ── Restore ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Restores a PostgreSQL backup (.sql or .zip) using <c>psql</c>.
    /// Returns the SQL file path used for restoration.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string for the target database.</param>
    /// <param name="backupFilePath">Path to the <c>.sql</c> or <c>.zip</c> backup file.</param>
    /// <param name="pgBinPath">Optional directory containing pg_dump, psql, etc. Leave null/empty to auto-detect or rely on PATH.</param>
    /// <param name="replaceExistingObjects">When <see langword="true"/>, uses <c>--single-transaction</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Absolute path to the SQL file used for restoration.</returns>
    public async Task<string> RestoreBackupAsync(
        string connectionString,
        string backupFilePath,
        string? pgBinPath,
        bool replaceExistingObjects,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(backupFilePath))
        {
            throw new InvalidOperationException("Backup file path is required.");
        }

        // Canonicalize input path — prevents path traversal (CA3003)
        backupFilePath = MaintenanceService.ValidateBackupFilePath(backupFilePath);

        if (!SafeFileExists(backupFilePath))
        {
            throw new FileNotFoundException("Backup file not found.", backupFilePath);
        }

        var cs = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(cs.Database))
        {
            throw new InvalidOperationException("Connection string must contain Database.");
        }

        string? tempDir = null;
        var sqlFilePath = backupFilePath;

        try
        {
            var ext = Path.GetExtension(backupFilePath);
            if (string.Equals(ext, ".zip", StringComparison.OrdinalIgnoreCase))
            {
                tempDir = Path.Combine(
                    Path.GetTempPath(),
                    "jellyfin-pg-restore-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(tempDir);

                using var archive = ZipFile.OpenRead(backupFilePath);
                ZipArchiveEntry? sqlEntry = null;
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                    {
                        sqlEntry = entry;
                        break;
                    }
                }

                if (sqlEntry is null)
                {
                    throw new InvalidOperationException("ZIP backup does not contain a .sql file.");
                }

                // Canonicalize extracted path to prevent zip-slip (CA3003)
                sqlFilePath = Path.GetFullPath(Path.Combine(tempDir, Path.GetFileName(sqlEntry.FullName)));
                sqlEntry.ExtractToFile(sqlFilePath, overwrite: true);
            }

            if (!SafeFileExists(sqlFilePath) || !sqlFilePath.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Restore requires a .sql backup file.");
            }

            // Resolve + validate before assigning to ProcessStartInfo (CA3006)
            var safeRestoreExe = ResolveExecutable(pgBinPath, "psql");
            var args = BuildPsqlRestoreArguments(cs, sqlFilePath, replaceExistingObjects);

            _logger.LogWarning(
                "Starting PostgreSQL restore from {Backup}. ReplaceExistingObjects={Replace}",
                backupFilePath,
                replaceExistingObjects);
            var sourceSize = MaintenanceService.FormatBytes(GetFileSize(backupFilePath));
            PostgresLog.Warn($"[Restore] INICIO: {backupFilePath} ({sourceSize}) replaceExisting={replaceExistingObjects}");

            var psi = CreateProcessStartInfo(safeRestoreExe, args);

            if (!string.IsNullOrWhiteSpace(cs.Password))
            {
                psi.Environment["PGPASSWORD"] = cs.Password;
            }

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start psql restore process.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdOut = await stdoutTask.ConfigureAwait(false);
            var stdErr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(stdErr) ? stdOut : stdErr;
                _logger.LogWarning("psql restore exited with code {Code}: {Detail}", process.ExitCode, detail);
            }

            return sqlFilePath;
        }
        finally
        {
            if (tempDir is not null && Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch (IOException)
                {
                    // best-effort cleanup — ignore
                }
            }
        }
    }

    // ── Process helpers (CA3003/CA3006: validate executables against allowed names) ──────

    /// <summary>
    /// Resolves a PostgreSQL client executable (pg_dump, psql, …) from a bin directory.
    /// <list type="bullet">
    ///   <item>If <paramref name="pgBinPath"/> is provided, we look for <c>&lt;pgBinPath&gt;/&lt;name&gt;[.exe]</c>.</item>
    ///   <item>Otherwise we search well-known OS installation directories.</item>
    ///   <item>If still not found, we fall back to the bare name and rely on PATH.</item>
    /// </list>
    /// </summary>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "pgBinPath is a user-configured directory validated against AllowedPgExecutables name. Constructed path is canonicalized with Path.GetFullPath.")]
    private static string ResolveExecutable(string? pgBinPath, string executableName)
    {
        if (!AllowedPgExecutables.Contains(executableName, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Executable '{executableName}' is not in the allowed list ({string.Join(", ", AllowedPgExecutables)}).");
        }

        // 1. User-configured bin directory takes priority.
        if (!string.IsNullOrWhiteSpace(pgBinPath))
        {
            var userBin = Path.GetFullPath(pgBinPath);
            var candidate = FindInDirectory(userBin, executableName);
            if (candidate is not null)
            {
                return candidate;
            }

            // Dir was provided but exe not found there — throw so the user knows the config is wrong.
            throw new InvalidOperationException(
                $"Executable '{executableName}' not found in configured PgBinPath: {userBin}");
        }

        // 2. Search well-known installation directories (Windows: Program Files; Linux: postgresql version dirs).
        var autoFound = FindInKnownLocations(executableName);
        if (autoFound is not null)
        {
            return autoFound;
        }

        // 3. Rely on PATH (works on most Linux/macOS installations).
        return executableName;
    }

    // Returns the full path if the executable exists in <dir>, otherwise null.
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "dir is either Path.GetFullPath of a user config value or a hard-coded well-known path. executableName is validated against AllowedPgExecutables.")]
    private static string? FindInDirectory(string dir, string executableName)
    {
        // Try with .exe extension first (Windows), then bare name (Linux/macOS).
        var withExt = Path.Combine(dir, executableName + ".exe");
        if (File.Exists(withExt))
        {
            return withExt;
        }

        var bare = Path.Combine(dir, executableName);
        if (File.Exists(bare))
        {
            return bare;
        }

        return null;
    }

    /// <summary>
    /// Searches well-known PostgreSQL installation directories for the given executable.
    /// Checks newest version first. Returns <see langword="null"/> when not found.
    /// </summary>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Paths are constructed from hard-coded OS roots and the allow-listed executable name — no user input flows here.")]
    private static string? FindInKnownLocations(string executableName)
    {
        // ── Windows: C:\Program Files\PostgreSQL\<ver>\bin\
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows))
        {
            var programFiles = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            };

            foreach (var pf in programFiles)
            {
                if (string.IsNullOrEmpty(pf))
                {
                    continue;
                }

                var pgRoot = Path.Combine(pf, "PostgreSQL");
                if (!Directory.Exists(pgRoot))
                {
                    continue;
                }

                var versionDirs = Directory.GetDirectories(pgRoot);
                System.Array.Sort(versionDirs, StringComparer.OrdinalIgnoreCase);
                System.Array.Reverse(versionDirs); // newest first

                foreach (var versionDir in versionDirs)
                {
                    var found = FindInDirectory(Path.Combine(versionDir, "bin"), executableName);
                    if (found is not null)
                    {
                        return found;
                    }
                }
            }

            return null;
        }

        // ── Linux: /usr/lib/postgresql/<ver>/bin/  (Debian/Ubuntu packages)
        const string linuxPgRoot = "/usr/lib/postgresql";
        if (Directory.Exists(linuxPgRoot))
        {
            var versionDirs = Directory.GetDirectories(linuxPgRoot);
            System.Array.Sort(versionDirs, StringComparer.OrdinalIgnoreCase);
            System.Array.Reverse(versionDirs); // newest first

            foreach (var versionDir in versionDirs)
            {
                var found = FindInDirectory(Path.Combine(versionDir, "bin"), executableName);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        // ── macOS (Homebrew): /opt/homebrew/opt/postgresql@<ver>/bin/
        const string brewRoot = "/opt/homebrew/opt";
        if (Directory.Exists(brewRoot))
        {
            var pgDirs = Directory.GetDirectories(brewRoot, "postgresql*");
            System.Array.Sort(pgDirs, StringComparer.OrdinalIgnoreCase);
            System.Array.Reverse(pgDirs);

            foreach (var pgDir in pgDirs)
            {
                var found = FindInDirectory(Path.Combine(pgDir, "bin"), executableName);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static string BuildPgDumpArguments(NpgsqlConnectionStringBuilder cs, string outputPath)
    {
        // Use string.Join / string concatenation to avoid CA1305 (culture-sensitive Append)
        var parts = new[]
        {
            string.Concat("--host=", QuoteArg(cs.Host ?? "localhost")),
            string.Format(System.Globalization.CultureInfo.InvariantCulture, "--port={0}", cs.Port),
            string.Concat("--username=", QuoteArg(cs.Username ?? "postgres")),
            string.Concat("--dbname=", QuoteArg(cs.Database ?? string.Empty)),
            string.Concat("--file=", QuoteArg(outputPath)),
            "--format=plain --no-password",
        };
        return string.Join(" ", parts);
    }

    private static string BuildPsqlRestoreArguments(
        NpgsqlConnectionStringBuilder cs,
        string sqlFilePath,
        bool replaceExistingObjects)
    {
        var parts = new System.Collections.Generic.List<string>
        {
            string.Concat("--host=", QuoteArg(cs.Host ?? "localhost")),
            string.Format(System.Globalization.CultureInfo.InvariantCulture, "--port={0}", cs.Port),
            string.Concat("--username=", QuoteArg(cs.Username ?? "postgres")),
            string.Concat("--dbname=", QuoteArg(cs.Database ?? string.Empty)),
        };

        if (replaceExistingObjects)
        {
            parts.Add("--single-transaction");
        }

        parts.Add(string.Concat("--file=", QuoteArg(sqlFilePath)));
        parts.Add("--no-password");
        return string.Join(" ", parts);
    }

    private static string QuoteArg(string value)
        => '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"';

    // ── Process start helper (CA3006) ─────────────────────────────────────────
    // executable has been validated against AllowedPgExecutables before reaching here.
    [SuppressMessage("Security", "CA3006:Review code for process command injection vulnerabilities", Justification = "executable is validated against AllowedPgExecutables whitelist and File.Exists. args are built from NpgsqlConnectionStringBuilder (server-trusted) + QuoteArg-escaped output path.")]
    private static ProcessStartInfo CreateProcessStartInfo(string executable, string arguments)
        => new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

    // ── Isolated file I/O helpers (CA3003) ────────────────────────────────────
    // All paths reaching here have been canonicalized with Path.GetFullPath or
    // constructed programmatically (safeOutputDirectory + timestamp filename).

    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Paths are canonicalized via Path.GetFullPath or built from trusted internal values before reaching this method.")]
    private static bool SafeFileExists(string path)
        => File.Exists(path);

    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Paths are canonicalized via Path.GetFullPath or built from trusted internal values before reaching this method.")]
    private static void SafeFileDelete(string path)
        => File.Delete(path);

    // Path has been canonicalized by Path.GetFullPath before reaching here (CA3003).
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Path is canonicalized with Path.GetFullPath or constructed from trusted internal values before this call.")]
    private static long GetFileSize(string path)
        => new FileInfo(path).Length;
}
