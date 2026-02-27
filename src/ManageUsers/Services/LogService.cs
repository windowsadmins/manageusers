using ManageUsers.Models;

namespace ManageUsers.Services;

/// <summary>
/// Handles log file writing, rotation, and console output.
/// </summary>
public sealed class LogService : IDisposable
{
    private readonly string _logFile;
    private StreamWriter? _writer;
    private readonly object _lock = new();
    private bool _disposed;

    public LogService()
    {
        _logFile = AppConstants.LogFile;
        Directory.CreateDirectory(Path.GetDirectoryName(_logFile)!);
        RotateIfNeeded();
        _writer = new StreamWriter(_logFile, append: true) { AutoFlush = true };
    }

    public void Info(string message) => Write("INFO", message);
    public void Warning(string message) => Write("WARNING", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var line = $"[{timestamp}] [{level}] {message}";

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
            "WARNING" => ConsoleColor.Yellow,
            _ => ConsoleColor.Gray
        };

        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(line);
        Console.ForegroundColor = prev;
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(_logFile))
            return;

        var fi = new FileInfo(_logFile);

        // Rotate if exceeds max size
        if (fi.Length > AppConstants.MaxLogSizeBytes)
        {
            var rotated = _logFile + ".1";
            if (File.Exists(rotated))
                File.Delete(rotated);
            File.Move(_logFile, rotated);
            return;
        }

        // Truncate if older than 24 hours
        if (fi.LastWriteTime < DateTime.Now.AddHours(-24))
        {
            File.WriteAllText(_logFile, "");
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
        }
    }
}
