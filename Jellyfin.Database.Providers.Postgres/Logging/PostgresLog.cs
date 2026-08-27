using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Jellyfin.Database.Providers.Postgres.Logging;

/// <summary>
/// Dedicated file logger for the PostgreSQL Database Provider plugin.
/// Writes timestamped lines to a daily-rotating log file
/// (<c>Postgres-yyyy-MM-dd.log</c>) inside Jellyfin's log directory.
/// Files older than <see cref="RetentionDays"/> days are deleted automatically.
/// Never throws — logging failures are silently swallowed to protect the host.
/// </summary>
internal static class PostgresLog
{
    /// <summary>Number of days to keep log files before automatic deletion.</summary>
    internal const int RetentionDays = 14;

    private static readonly object Lock = new();

    // Default path — overridden at plugin startup via SetLogDirectory().
    private static string _logDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "jellyfin",
        "log");

    private static DateTimeOffset _lastWrite = DateTimeOffset.MinValue;

    // ── Public API ─────────────────────────────────────────────────────────

    /// <summary>Gets the absolute path of today's log file.</summary>
    public static string CurrentLogPath =>
        Path.Combine(_logDir, $"Postgres-{DateTimeOffset.Now:yyyy-MM-dd}.log");

    /// <summary>
    /// Sets the directory where log files are written, trying each candidate
    /// in priority order until a writable path is found.
    /// </summary>
    /// <param name="candidates">Directory paths in priority order.</param>
    public static void SetLogDirectory(params string[] candidates)
    {
        foreach (var dir in candidates)
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(dir);
                _logDir = dir;
                return;
            }
            catch
            {
                // Try next candidate.
            }
        }
    }

    /// <summary>Appends a timestamped line at the given level.</summary>
    /// <param name="level">Short level label, e.g. "INFO ", "WARN ".</param>
    /// <param name="message">The message to write.</param>
    public static void Write(string level, string message)
    {
        try
        {
            var now = DateTimeOffset.Now;
            var line = string.Format(
                CultureInfo.InvariantCulture,
                "[{0:yyyy-MM-dd HH:mm:ss.fff}] [{1}] {2}{3}",
                now,
                level,
                message,
                Environment.NewLine);

            lock (Lock)
            {
                EnsureDirectory();

                if (now.Date != _lastWrite.Date)
                {
                    PurgeOldLogs();
                    _lastWrite = now;
                }

                File.AppendAllText(CurrentLogPath, line);
            }
        }
        catch
        {
            // Never crash Jellyfin due to a logging failure.
        }
    }

    /// <summary>Writes an informational message.</summary>
    /// <param name="msg">The message to write.</param>
    public static void Info(string msg) => Write("INFO ", msg);

    /// <summary>Writes a debug-level message.</summary>
    /// <param name="msg">The message to write.</param>
    public static void Debug(string msg) => Write("DEBUG", msg);

    /// <summary>Writes a warning message.</summary>
    /// <param name="msg">The message to write.</param>
    public static void Warn(string msg) => Write("WARN ", msg);

    /// <summary>Writes an error message.</summary>
    /// <param name="msg">The message to write.</param>
    public static void Error(string msg) => Write("ERROR", msg);

    /// <summary>Writes an error message including exception details.</summary>
    /// <param name="msg">Context description.</param>
    /// <param name="ex">The exception to include.</param>
    public static void Error(string msg, Exception ex) =>
        Write(
            "ERROR",
            string.Format(
                CultureInfo.InvariantCulture,
                "{0} | {1}: {2}",
                msg,
                ex.GetType().Name,
                ex.Message));

    // ── Private helpers ────────────────────────────────────────────────────

    private static void EnsureDirectory()
    {
        try
        {
            if (!Directory.Exists(_logDir))
            {
                Directory.CreateDirectory(_logDir);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    private static void PurgeOldLogs()
    {
        try
        {
            var cutoff = DateTimeOffset.Now.Date.AddDays(-RetentionDays);
            foreach (var file in Directory.GetFiles(_logDir, "Postgres-*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var datePart = name.Length >= 10 ? name[^10..] : null;

                if (datePart is not null
                    && DateTimeOffset.TryParseExact(
                        datePart,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var fileDate)
                    && fileDate.Date < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // Best effort.
        }
    }

    // ── TaskScope ──────────────────────────────────────────────────────────

    /// <summary>
    /// Scope for a scheduled task or long-running operation.
    /// Writes a DEBUG line on start and a single INFO or ERROR summary on completion.
    /// Intermediate messages should use <see cref="Debug"/> to keep logs readable.
    /// </summary>
    internal sealed class TaskScope : IDisposable
    {
        private readonly string _task;
        private readonly Stopwatch _stopwatch;
        private bool _done;

        private TaskScope(string task)
        {
            _task = task;
            _stopwatch = Stopwatch.StartNew();
            Debug(string.Format(CultureInfo.InvariantCulture, "[{0}] Iniciando.", task));
        }

        /// <summary>
        /// Starts a new task execution scope.
        /// </summary>
        /// <param name="task">Human-readable task name.</param>
        /// <returns>The scope; dispose to close without an explicit outcome.</returns>
        public static TaskScope Begin(string task) => new(task);

        /// <summary>Marks the task as completed with a one-line summary.</summary>
        /// <param name="summary">Short outcome description.</param>
        public void Complete(string summary)
        {
            if (_done)
            {
                return;
            }

            _done = true;
            Info(string.Format(
                CultureInfo.InvariantCulture,
                "[{0}] Completado en {1} ms: {2}",
                _task,
                _stopwatch.ElapsedMilliseconds,
                summary));
        }

        /// <summary>Marks the task as failed.</summary>
        /// <param name="exception">The exception that caused the failure.</param>
        /// <param name="summary">Short failure description.</param>
        public void Fail(Exception exception, string summary)
        {
            if (_done)
            {
                return;
            }

            _done = true;
            Error(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "[{0}] Falló tras {1} ms: {2}",
                    _task,
                    _stopwatch.ElapsedMilliseconds,
                    summary),
                exception);
        }

        /// <summary>Marks the task as cancelled (normal condition).</summary>
        /// <param name="summary">Short description.</param>
        public void Cancel(string summary)
        {
            if (_done)
            {
                return;
            }

            _done = true;
            Info(string.Format(
                CultureInfo.InvariantCulture,
                "[{0}] Cancelado tras {1} ms: {2}",
                _task,
                _stopwatch.ElapsedMilliseconds,
                summary));
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (!_done)
            {
                _done = true;
                Debug(string.Format(
                    CultureInfo.InvariantCulture,
                    "[{0}] Finalizado tras {1} ms.",
                    _task,
                    _stopwatch.ElapsedMilliseconds));
            }
        }
    }
}
