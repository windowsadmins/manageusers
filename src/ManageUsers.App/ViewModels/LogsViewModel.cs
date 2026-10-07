using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ManageUsers.Models;
using ManageUsers.Services;

namespace ManageUsers.App.ViewModels;

/// <summary>
/// ViewModel for the Logs tab: the audit log (which accounts were deleted, when and why)
/// pinned first, then each day's session log, newest first.
/// </summary>
public partial class LogsViewModel : ObservableObject
{
    public static readonly string LogDirectory = AppConstants.LogDir;

    public ObservableCollection<LogFile> LogFiles { get; } = [];

    [ObservableProperty] private LogFile? _selectedLog;
    [ObservableProperty] private string _logContent = string.Empty;
    [ObservableProperty] private string _filterText = string.Empty;

    public IEnumerable<LogLine> FilteredLines
    {
        get
        {
            var lines = LogContent.Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => new LogLine(l, LogLevels.Of(l)));
            if (string.IsNullOrWhiteSpace(FilterText))
                return lines;
            return lines.Where(l => l.Text.Contains(FilterText, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ── Refresh ──────────────────────────────────────────────────

    public void Refresh()
    {
        var selectedPath = SelectedLog?.Path;
        LogFiles.Clear();

        if (!Directory.Exists(LogDirectory))
            return;

        if (File.Exists(AppConstants.AuditLogFile))
            LogFiles.Add(new LogFile("Audit log", "Every deletion decision and outcome",
                AppConstants.AuditLogFile, FileSize(AppConstants.AuditLogFile), IsAudit: true));

        // A day directory holds that day's runs: logs\yyyy-MM-dd\manageusers.log.
        var days = Directory.GetDirectories(LogDirectory)
            .Select(dir => (Dir: dir, Day: ParseDay(System.IO.Path.GetFileName(dir))))
            .Where(d => d.Day is not null)
            .OrderByDescending(d => d.Day)
            .Select(d => (d.Day, Log: System.IO.Path.Combine(d.Dir, "manageusers.log")))
            .Where(d => File.Exists(d.Log));
        foreach (var (day, log) in days)
            LogFiles.Add(new LogFile(FormatDay(day!.Value), day.Value.ToString("yyyy-MM-dd"), log, FileSize(log), IsAudit: false));

        // The flat log the day layout replaced, kept until retention removes it.
        var legacy = System.IO.Path.Combine(LogDirectory, "manageusers.log");
        if (File.Exists(legacy))
            LogFiles.Add(new LogFile("Earlier log", "Before day folders", legacy, FileSize(legacy), IsAudit: false));

        SelectedLog = LogFiles.FirstOrDefault(f => f.Path == selectedPath) ?? LogFiles.FirstOrDefault();
    }

    private static DateTime? ParseDay(string name) =>
        DateTime.TryParseExact(name, AppConstants.DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static string FormatDay(DateTime day) =>
        day.Date == DateTime.Today ? "Today"
        : day.Date == DateTime.Today.AddDays(-1) ? "Yesterday"
        : day.ToString("dddd, MMMM d");

    private static long FileSize(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    // ── Load Content ─────────────────────────────────────────────

    partial void OnSelectedLogChanged(LogFile? value) => Reload();

    public void Reload()
    {
        if (SelectedLog is null)
        {
            LogContent = string.Empty;
            return;
        }

        try
        {
            // FileShare.ReadWrite so a log a run is still writing can be read.
            using var fs = new FileStream(SelectedLog.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            LogContent = reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            LogContent = $"Unable to read log file: {ex.Message}";
        }
    }

    partial void OnLogContentChanged(string value) => OnPropertyChanged(nameof(FilteredLines));
    partial void OnFilterTextChanged(string value) => OnPropertyChanged(nameof(FilteredLines));

    // ── Actions ──────────────────────────────────────────────────

    public void OpenInEditor()
    {
        if (SelectedLog is null) return;
        Process.Start(new ProcessStartInfo(SelectedLog.Path) { UseShellExecute = true });
    }

    public void OpenFolder()
    {
        if (!Directory.Exists(LogDirectory)) return;
        Process.Start(new ProcessStartInfo(LogDirectory) { UseShellExecute = true });
    }
}

// ── Models ───────────────────────────────────────────────────

public record LogFile(string Title, string Subtitle, string Path, long SizeBytes, bool IsAudit)
{
    public string Glyph => IsAudit ? "\uE9F9" : "\uE9D5";

    public string DisplaySize => SizeBytes switch
    {
        < 1024        => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:0.#} KB",
        _             => $"{SizeBytes / (1024.0 * 1024):0.#} MB",
    };
}

public record LogLine(string Text, LogLineLevel Level);
