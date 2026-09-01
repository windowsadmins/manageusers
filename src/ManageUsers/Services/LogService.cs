using ManageUsers.Models;

namespace ManageUsers.Services;

/// <summary>
/// Handles log file writing, rotation, and console output. Operational messages go
/// to manageusers.log; account-deletion decisions and outcomes additionally go to
/// manageusers.audit.log, which is append-only — history is only discarded when the
/// size-based retention cap pushes the oldest rotated generation out, never by age.
/// Lines are written as <c>[yyyy-MM-dd HH:mm:ss] LEVEL message</c> in local time,
/// with the level left-aligned to five characters (DEBUG, INFO, WARN, ERROR).
/// </summary>
public sealed class LogService : IDisposable
{
    private readonly string _logFile;
    private readonly string _auditFile;
    private StreamWriter? _writer;
    private StreamWriter? _auditWriter;
    private readonly object _lock = new();
    private bool _disposed;

    public LogService()
    {
        _logFile = AppConstants.LogFile;
        _auditFile = AppConstants.AuditLogFile;
        Directory.CreateDirectory(Path.GetDirectoryName(_logFile)!);
        MigrateLegacyLog(AppConstants.LegacyLogFile, _logFile);
        MigrateLegacyLog(AppConstants.LegacyAuditLogFile, _auditFile);
        RotateIfNeeded(_logFile);
        RotateIfNeeded(_auditFile);
        _writer = new StreamWriter(_logFile, append: true) { AutoFlush = true };
        _auditWriter = new StreamWriter(_auditFile, append: true) { AutoFlush = true };
    }

    public void Info(string message) => Write("INFO", message);
    public void Warning(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    /// <summary>
    /// Record an account-deletion decision or outcome so "which accounts were
    /// deleted, when, and why" stays answerable after the operational log rotates.
    /// Entries land in both the audit log and the operational log.
    /// </summary>
    public void Audit(string action, string detail)
    {
        var message = $"{action} | {detail}";
        var line = FormatLine("INFO", message);

        // Both writes happen under one lock acquisition (Monitor is reentrant for
        // the nested Write) so audit entries can't reorder against the mirrored
        // operational-log lines under concurrent logging.
        lock (_lock)
        {
            try
            {
                _auditWriter?.WriteLine(line);
            }
            catch
            {
                // Audit write failure must not break the run; the operational
                // log below still carries the entry.
            }

            Write("INFO", message);
        }
    }

    /// <summary>
    /// Build one log line: <c>[yyyy-MM-dd HH:mm:ss] LEVEL message</c>, local time,
    /// level padded to five characters so messages line up across levels.
    /// </summary>
    private static string FormatLine(string level, string message)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        return $"[{timestamp}] {level,-5} {message}";
    }

    private void Write(string level, string message)
    {
        var line = FormatLine(level, message);

        lock (_lock)
        {
            try
            {
                _writer?.WriteLine(line);
            }
            catch
            {
                // If file write fails, still output to console
            }
        }

        // Mirror to console
        var color = level switch
        {
            "ERROR" => ConsoleColor.Red,
            "WARN" => ConsoleColor.Yellow,
            _ => ConsoleColor.Gray
        };

        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(line);
        Console.ForegroundColor = prev;
    }

    /// <summary>
    /// One-time move of a log (and its rotated generations) from the location used
    /// by earlier releases. Best effort: a failure leaves the old files in place and
    /// logging starts fresh at the new path.
    /// </summary>
    private static void MigrateLegacyLog(string legacyFile, string newFile)
    {
        try
        {
            if (File.Exists(newFile) || !File.Exists(legacyFile))
                return;

            File.Move(legacyFile, newFile);

            for (var i = 1; i <= AppConstants.MaxRotatedLogs; i++)
            {
                var rotated = $"{legacyFile}.{i}";
                var target = $"{newFile}.{i}";
                if (File.Exists(rotated) && !File.Exists(target))
                    File.Move(rotated, target);
            }
        }
        catch
        {
            // Keeping history is nice to have; never block logging on it.
        }
    }

    private static void RotateIfNeeded(string file)
    {
        if (!File.Exists(file))
            return;

        if (new FileInfo(file).Length <= AppConstants.MaxLogSizeBytes)
            return;

        // Shift file.N → file.N+1 (dropping the oldest), then move the current
        // file into the .1 slot.
        try
        {
            var oldest = $"{file}.{AppConstants.MaxRotatedLogs}";
            if (File.Exists(oldest))
                File.Delete(oldest);

            for (var i = AppConstants.MaxRotatedLogs - 1; i >= 1; i--)
            {
                var rotated = $"{file}.{i}";
                if (File.Exists(rotated))
                    File.Move(rotated, $"{file}.{i + 1}");
            }

            File.Move(file, $"{file}.1");
        }
        catch
        {
            // Rotation failure must not prevent logging; keep appending to the
            // oversized file rather than losing entries.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
            _auditWriter?.Dispose();
            _auditWriter = null;
        }
    }
}
