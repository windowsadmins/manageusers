using ManageUsers.Models;

namespace ManageUsers.Services;

/// <summary>
/// Handles log file writing, rotation, and console output. Operational messages go to
/// the day directory this run belongs to, <c>logs\yyyy-MM-dd\manageusers.log</c>, with
/// their structured form in <c>events.jsonl</c> beside them; account-deletion decisions
/// and outcomes additionally go to manageusers.audit.log at the logs root, which is
/// append-only — history is only discarded when the size-based retention cap pushes the
/// oldest rotated generation out, never by age, which is why it stays outside the day
/// directories that retention sweeps.
/// Lines are written as <c>[yyyy-MM-dd HH:mm:ss] LEVEL message</c> in local time,
/// with the level left-aligned to five characters (DEBUG, INFO, WARN, ERROR).
/// </summary>
public sealed class LogService : IDisposable
{
    private readonly string _logFile;
    private readonly string _auditFile;
    private readonly string _eventsFile;
    private readonly string _invocationId = Guid.NewGuid().ToString();
    private StreamWriter? _writer;
    private StreamWriter? _auditWriter;
    private StreamWriter? _eventsWriter;
    private readonly object _lock = new();
    private bool _disposed;

    public LogService()
    {
        var now = DateTime.Now;
        _logFile = AppConstants.LogFileFor(now);
        _eventsFile = AppConstants.EventsFileFor(now);
        _auditFile = AppConstants.AuditLogFile;
        Directory.CreateDirectory(AppConstants.LogDir);
        Directory.CreateDirectory(Path.GetDirectoryName(_logFile)!);
        PruneDayDirectories(now);
        // The flat log this layout replaced lives at the root, so a migration from an
        // even older location still lands there and ages out with the other loose files.
        MigrateLegacyLog(AppConstants.LegacyLogFile, Path.Combine(AppConstants.LogDir, "manageusers.log"));
        MigrateLegacyLog(AppConstants.LegacyAuditLogFile, _auditFile);
        RotateIfNeeded(_logFile);
        RotateIfNeeded(_auditFile);
        _writer = new StreamWriter(_logFile, append: true) { AutoFlush = true };
        _auditWriter = new StreamWriter(_auditFile, append: true) { AutoFlush = true };
        _eventsWriter = new StreamWriter(_eventsFile, append: true) { AutoFlush = true };
    }

    /// <summary>
    /// Removes day directories past the retention window, and the loose files the flat
    /// layout left at the logs root, by the same age rule. The audit log and its rotated
    /// generations are exempt: they are the durable record. Best-effort throughout.
    /// </summary>
    private static void PruneDayDirectories(DateTime now)
    {
        try
        {
            var cutoff = now.Date.AddDays(-AppConstants.LogRetentionDays);
            foreach (var candidate in Directory.GetDirectories(AppConstants.LogDir))
            {
                if (!DateTime.TryParseExact(Path.GetFileName(candidate), AppConstants.DayFormat,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var day) || day >= cutoff)
                    continue;
                try { Directory.Delete(candidate, recursive: true); } catch { }
            }

            var auditName = Path.GetFileName(AppConstants.AuditLogFile);
            foreach (var file in Directory.GetFiles(AppConstants.LogDir))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith(auditName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (File.GetLastWriteTime(file) >= cutoff)
                    continue;
                try { File.Delete(file); } catch { }
            }
        }
        catch
        {
            // Retention never stops a run.
        }
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

            WriteEvent(level, message);
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
    /// The same entry, structured, in the day directory's event stream. Written by hand
    /// rather than through a serializer so this service keeps its dependency-free shape.
    /// Several tools share a day directory, so each record names its tool, its process
    /// and the invocation that wrote it.
    /// </summary>
    private void WriteEvent(string level, string message)
    {
        try
        {
            var record = new System.Text.StringBuilder();
            record.Append('{');
            AppendField(record, "timestamp", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"), first: true);
            AppendField(record, "level", level);
            AppendField(record, "event_type", level == "ERROR" ? "error" : "message");
            AppendField(record, "tool", "manageusers");
            AppendField(record, "pid", Environment.ProcessId.ToString());
            AppendField(record, "invocation_id", _invocationId);
            AppendField(record, "message", message);
            record.Append('}');
            _eventsWriter?.WriteLine(record.ToString());
        }
        catch
        {
            // The structured stream is a convenience, never a failure mode.
        }
    }

    private static void AppendField(System.Text.StringBuilder builder, string name, string value, bool first = false)
    {
        if (!first)
            builder.Append(',');
        builder.Append('"').Append(name).Append("\":\"");
        foreach (var c in value ?? string.Empty)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < ' ') builder.Append("\\u").Append(((int)c).ToString("x4"));
                    else builder.Append(c);
                    break;
            }
        }
        builder.Append('"');
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
            _eventsWriter?.Dispose();
            _eventsWriter = null;
        }
    }
}
