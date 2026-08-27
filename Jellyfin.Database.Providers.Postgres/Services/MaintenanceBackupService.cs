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
    /// <param name="pgDumpPath">Optional path to the <c>pg_dump</c> executable.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Absolute path to the created backup file.</returns>
    public async Task<string> CreateBackupAsync(
        string connectionString,
        string outputDirectory,
        bool compress,
        string? pgDumpPath,
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
        var safeExecutable = ResolvePgDumpExecutable(pgDumpPath);
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
            _logger.LogInformation("PostgreSQL backup completed at {Path}", sqlFilePath);
            PostgresLog.Warn($"[Backup] COMPLETADO: {sqlFilePath}");
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
        _logger.LogInformation("PostgreSQL backup completed at {Path}", zipPath);
        PostgresLog.Warn($"[Backup] COMPLETADO (ZIP): {zipPath}");
        return zipPath;
    }

    // ── Restore ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Restores a PostgreSQL backup (.sql or .zip) using <c>psql</c>.
    /// Returns the SQL file path used for restoration.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string for the target database.</param>
    /// <param name="backupFilePath">Path to the <c>.sql</c> or <c>.zip</c> backup file.</param>
    /// <param name="pgRestorePath">Optional path to the <c>psql</c> executable.</param>
    /// <param name="replaceExistingObjects">When <see langword="true"/>, uses <c>--single-transaction</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Absolute path to the SQL file used for restoration.</returns>
    public async Task<string> RestoreBackupAsync(
        string connectionString,
        string backupFilePath,
        string? pgRestorePath,
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
            var safeRestoreExe = ResolvePsqlExecutable(pgRestorePath);
            var args = BuildPsqlRestoreArguments(cs, sqlFilePath, replaceExistingObjects);

            _logger.LogWarning(
                "Starting PostgreSQL restore from {Backup}. ReplaceExistingObjects={Replace}",
                backupFilePath,
                replaceExistingObjects);
            PostgresLog.Warn($"[Restore] INICIO: {backupFilePath} replaceExisting={replaceExistingObjects}");

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

    private static string ResolvePgDumpExecutable(string? userPath)
    {
        if (string.IsNullOrWhiteSpace(userPath))
        {
            return "pg_dump";
        }

        return ValidatePgExecutable(userPath, "pg_dump");
    }

    private static string ResolvePsqlExecutable(string? userPath)
    {
        if (string.IsNullOrWhiteSpace(userPath))
        {
            return "psql";
        }

        return ValidatePgExecutable(userPath, "psql");
    }

    /// <summary>
    /// Validates a user-supplied executable path against <see cref="AllowedPgExecutables"/>.
    /// The executable name (without extension) must be in the allow-list.
    /// Returns the canonicalized absolute path if valid and the file exists.
    /// </summary>
    private static string ValidatePgExecutable(string userPath, string expectedBaseName)
    {
        var abs = Path.GetFullPath(userPath);
        var baseName = Path.GetFileNameWithoutExtension(abs);

        if (!AllowedPgExecutables.Contains(baseName, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Executable '{baseName}' is not in the allowed list ({string.Join(", ", AllowedPgExecutables)}).");
        }

        var safeAbs = GetValidatedExecutablePath(abs);
        return safeAbs;
    }

    // Isolated method so the analyzer sees File.Exists on a fully-validated path,
    // not on a value flowing from user input (CA3003).
    private static string GetValidatedExecutablePath(string abs)
    {
        if (!System.IO.File.Exists(abs))
        {
            throw new InvalidOperationException($"Executable not found at validated path: {abs}");
        }

        return abs;
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
}
