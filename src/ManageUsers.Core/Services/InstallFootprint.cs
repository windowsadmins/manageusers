using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Xml.Linq;

namespace ManageUsers.Services;

/// <summary>A Windows service or scheduled task that ManageUsers' install puts on a device.</summary>
public sealed record InstalledComponent(string FriendlyName, string Name, string Kind, string Description);

/// <summary>What the device reports about an <see cref="InstalledComponent"/>, in plain words.</summary>
public sealed record ComponentStatus(string State, string LastResult, string Schedule);

/// <summary>
/// ManageUsers' install footprint: the services and scheduled tasks it runs from, with
/// their state read from the device. ManageUsers installs no Windows service. Its one
/// scheduled task comes from the companion ManageUsersPrefs package (see the README's
/// Scheduling section), which keeps the schedule a preference.
/// </summary>
public static class InstallFootprint
{
    public const string ScheduledTask = "Scheduled task";

    public static IReadOnlyList<InstalledComponent> Components { get; } =
    [
        new("Scheduled cleanup", @"\WinAdmins-ManageUsers", ScheduledTask,
            "Runs manageusers.exe as SYSTEM. Registered by the ManageUsersPrefs package.")
    ];

    // Task Scheduler result codes that are not failures.
    private const int TaskHasNotRun = 0x00041303;   // SCHED_S_TASK_HAS_NOT_RUN
    private const int TaskRunning = 0x00041301;     // SCHED_S_TASK_RUNNING

    /// <summary>A task's state from the Task Scheduler's TASK_STATE value.</summary>
    public static string TaskStateText(int state, bool enabled) => !enabled ? "Disabled" : state switch
    {
        1 => "Disabled",
        2 or 3 => "Ready",
        4 => "Running",
        _ => "Unknown"
    };

    /// <summary>
    /// A task's last run in words: when it ran and how it ended, or "Never run". The
    /// Task Scheduler reports a task that has never run with a 1899 date or result 0x41303.
    /// </summary>
    public static string TaskResultText(DateTime? lastRun, int result)
    {
        if (lastRun is not { } when || when.Year < 1900 || result == TaskHasNotRun)
            return "Never run";
        var outcome = result switch
        {
            0 => "Succeeded",
            TaskRunning => "Running now",
            _ => $"Failed (0x{result:X8})"
        };
        return $"{outcome}, {when.ToString("MMM d, yyyy h:mm tt", CultureInfo.InvariantCulture)}";
    }

    /// <summary>A task's triggers in plain words, from its registration XML.</summary>
    public static string TriggerText(string taskXml)
    {
        XElement root;
        try
        {
            root = XElement.Parse(taskXml);
        }
        catch (System.Xml.XmlException)
        {
            return "Unknown";
        }

        var triggers = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Triggers");
        var parts = triggers?.Elements().Select(DescribeTrigger).OfType<string>().ToList() ?? [];
        return parts.Count == 0 ? "No trigger (runs only on demand)" : string.Join("; ", parts);
    }

    private static string? DescribeTrigger(XElement trigger)
    {
        string? Child(XElement e, string name) => e.Elements().FirstOrDefault(c => c.Name.LocalName == name)?.Value;
        XElement? Element(XElement e, string name) => e.Elements().FirstOrDefault(c => c.Name.LocalName == name);

        var text = trigger.Name.LocalName switch
        {
            "BootTrigger" => "At startup",
            "LogonTrigger" => Child(trigger, "UserId") is { Length: > 0 } user ? $"At logon of {user}" : "At logon of any user",
            "IdleTrigger" => "When the device is idle",
            "RegistrationTrigger" => "When the task is created or changed",
            "SessionStateChangeTrigger" => "On a session change",
            "EventTrigger" => "On an event log entry",
            "TimeTrigger" => At(Child(trigger, "StartBoundary")) is { } once ? $"Once on {once:MMM d, yyyy} at {Clock(once)}" : "Once",
            "CalendarTrigger" => DescribeCalendar(trigger, Element, Child),
            _ => trigger.Name.LocalName
        };

        if (Element(trigger, "Repetition") is { } repetition && Duration(Child(repetition, "Interval")) is { } every)
            text = $"{text}, then every {every}";
        if (Child(trigger, "Enabled") is { } enabled && enabled.Trim().Equals("false", StringComparison.OrdinalIgnoreCase))
            text += " (disabled)";
        return text;
    }

    private static string DescribeCalendar(XElement trigger, Func<XElement, string, XElement?> element, Func<XElement, string, string?> child)
    {
        var at = At(child(trigger, "StartBoundary")) is { } start ? $" at {Clock(start)}" : "";

        if (element(trigger, "ScheduleByDay") is { } byDay)
        {
            var days = int.TryParse(child(byDay, "DaysInterval"), out var n) ? n : 1;
            return days <= 1 ? $"Daily{at}" : $"Every {days} days{at}";
        }
        if (element(trigger, "ScheduleByWeek") is { } byWeek)
        {
            var weeks = int.TryParse(child(byWeek, "WeeksInterval"), out var n) ? n : 1;
            var days = element(byWeek, "DaysOfWeek")?.Elements().Select(d => d.Name.LocalName).ToList() ?? [];
            var on = days.Count > 0 ? $" on {string.Join(", ", days)}" : "";
            return (weeks <= 1 ? "Weekly" : $"Every {weeks} weeks") + on + at;
        }
        if (element(trigger, "ScheduleByMonth") is not null || element(trigger, "ScheduleByMonthDayOfWeek") is not null)
            return $"Monthly{at}";
        return $"On a schedule{at}";
    }

    private static DateTime? At(string? boundary) =>
        DateTime.TryParse(boundary, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when) ? when : null;

    private static string Clock(DateTime when) => when.ToString("h:mm tt", CultureInfo.InvariantCulture);

    /// <summary>An ISO 8601 duration such as PT4H or P1D in words, or null when it cannot be read.</summary>
    private static string? Duration(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        TimeSpan span;
        try
        {
            span = System.Xml.XmlConvert.ToTimeSpan(iso.Trim());
        }
        catch (FormatException)
        {
            return null;
        }
        static string Unit(double n, string unit) => n == 1 ? unit : $"{n:0} {unit}s";
        if (span.TotalDays >= 1 && span.TotalDays % 1 == 0) return Unit(span.TotalDays, "day");
        if (span.TotalHours >= 1 && span.TotalHours % 1 == 0) return Unit(span.TotalHours, "hour");
        if (span.TotalMinutes >= 1 && span.TotalMinutes % 1 == 0) return Unit(span.TotalMinutes, "minute");
        return null;
    }

    private const int AccessDenied = unchecked((int)0x80070005);
    private const int FileNotFound = unchecked((int)0x80070002);

    /// <summary>
    /// Reads a scheduled task through the Task Scheduler's COM API. Needs no elevation for
    /// a task the caller can see; a task this user may not read reports "Unknown".
    /// Blocking, so call it off the UI thread.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static ComponentStatus QueryTask(string path)
    {
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service")
                       ?? throw new COMException("Task Scheduler is not available", FileNotFound);
            dynamic scheduler = Activator.CreateInstance(type)!;
            scheduler.Connect();
            var folderPath = path.LastIndexOf('\\') is var slash and > 0 ? path[..slash] : @"\";
            dynamic task = scheduler.GetFolder(folderPath).GetTask(path);
            var lastRun = (DateTime)task.LastRunTime;
            return new ComponentStatus(
                TaskStateText((int)task.State, (bool)task.Enabled),
                TaskResultText(lastRun, (int)task.LastTaskResult),
                TriggerText((string)task.Xml));
        }
        catch (Exception ex) when (ex.HResult == FileNotFound || ex is FileNotFoundException)
        {
            return new ComponentStatus("Not installed", "—", "—");
        }
        catch (Exception ex) when (ex.HResult == AccessDenied || ex is UnauthorizedAccessException)
        {
            return new ComponentStatus("Unknown", "Unknown (needs administrator to read)", "Unknown");
        }
        catch (Exception)
        {
            return new ComponentStatus("Unknown", "Unknown", "Unknown");
        }
    }
}
