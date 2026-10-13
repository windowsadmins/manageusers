using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Xml.Linq;

namespace ManageUsers.Services;

/// <summary>A Windows service or scheduled task that ManageUsers' install puts on a device.</summary>
public sealed record InstalledComponent(string FriendlyName, string Name, string Kind);

/// <summary>One labelled value in a component's details.</summary>
public sealed record ComponentDetail(string Label, string Value);

/// <summary>
/// What the device reports about an <see cref="InstalledComponent"/>, in plain words: its status,
/// a one-line summary for the collapsed row, and the detail rows in display order. A row that
/// does not apply is left out, never invented.
/// </summary>
public sealed record ComponentStatus(string Status, string Summary, IReadOnlyList<ComponentDetail> Details);

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
        new("Scheduled cleanup", @"\WinAdmins-ManageUsers", ScheduledTask)
    ];

    // Detail labels, in display order.
    public const string NameLabel = "Name", StartTypeLabel = "Start type", StatusLabel = "Status", AccountLabel = "Account",
        PathLabel = "Path and arguments", TriggersLabel = "Triggers", LastRunLabel = "Last run",
        LastResultLabel = "Last result", NextRunLabel = "Next run";

    private static readonly string[] TaskLabels =
        [StartTypeLabel, StatusLabel, AccountLabel, PathLabel, TriggersLabel, LastRunLabel, LastResultLabel, NextRunLabel];

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
    /// How a task's last run ended, or "Never run". The Task Scheduler reports a task that has
    /// never run with a 1899 date or result 0x41303.
    /// </summary>
    public static string TaskResultText(DateTime? lastRun, int result)
    {
        if (RunTimeText(lastRun, null) == null || result == TaskHasNotRun)
            return "Never run";
        return result switch
        {
            0 => "Succeeded",
            TaskRunning => "Running now",
            _ => $"Failed (0x{result:X8})"
        };
    }

    /// <summary>A task's run time as a local date and time, or <paramref name="none"/> for the Task Scheduler's empty 1899 date.</summary>
    public static string? RunTimeText(DateTime? when, string? none) =>
        when is { Year: >= 1900 } t ? t.ToString("MMM d, yyyy h:mm tt", CultureInfo.InvariantCulture) : none;

    private static readonly Dictionary<string, string> WellKnownAccounts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["S-1-5-18"] = "SYSTEM",
        ["S-1-5-19"] = "LOCAL SERVICE",
        ["S-1-5-20"] = "NETWORK SERVICE",
        ["S-1-5-32-544"] = "Administrators",
        ["S-1-5-32-545"] = "Users",
        [@"NT AUTHORITY\SYSTEM"] = "SYSTEM"
    };

    /// <summary>The account a task runs as, from its principal's user or group, or null when it names none.</summary>
    public static string? AccountText(string taskXml)
    {
        if (Parse(taskXml) is not { } root) return null;
        var principal = Find(Find(root, "Principals"), "Principal");
        var id = Find(principal, "UserId")?.Value.Trim() is { Length: > 0 } user ? user : Find(principal, "GroupId")?.Value.Trim();
        if (string.IsNullOrEmpty(id)) return null;
        return WellKnownAccounts.TryGetValue(id, out var name) ? name : id;
    }

    /// <summary>Each command a task runs, with its arguments, one per line; null when it runs none.</summary>
    public static string? ActionsText(string taskXml)
    {
        if (Parse(taskXml) is not { } root) return null;
        var lines = Find(root, "Actions")?.Elements().Where(e => e.Name.LocalName == "Exec").Select(exec =>
        {
            var command = Find(exec, "Command")?.Value.Trim() ?? "";
            if (command.Contains(' ') && !command.StartsWith('"')) command = $"\"{command}\"";
            var args = Find(exec, "Arguments")?.Value.Trim();
            return string.IsNullOrEmpty(args) ? command : $"{command} {args}";
        }).Where(l => l.Length > 0).ToList();
        return lines is { Count: > 0 } ? string.Join("\n", lines) : null;
    }

    /// <summary>
    /// The collapsed row's summary: "Scheduled task · Daily at 3:00 AM · Last result 0", or
    /// "· Never run" in place of the result when the task has not run.
    /// </summary>
    public static string TaskSummary(string triggers, DateTime? lastRun, int lastResult) =>
        $"{ScheduledTask} · {triggers} · " + (TaskResultText(lastRun, lastResult) == "Never run"
            ? "Never run"
            : $"Last result {(lastResult == 0 ? "0" : $"0x{lastResult:X8}")}");

    /// <summary>A readable task's detail rows, in display order.</summary>
    public static ComponentStatus TaskDetails(string path, int state, bool enabled, DateTime? lastRun, int lastResult, DateTime? nextRun, string taskXml)
    {
        var status = TaskStateText(state, enabled);
        var triggers = TriggerText(taskXml);
        var rows = new List<ComponentDetail>
        {
            new(NameLabel, path),
            new(StartTypeLabel, enabled ? "Enabled" : "Disabled"),
            new(StatusLabel, status)
        };
        if (AccountText(taskXml) is { } account) rows.Add(new(AccountLabel, account));
        if (ActionsText(taskXml) is { } actions) rows.Add(new(PathLabel, actions));
        rows.Add(new(TriggersLabel, triggers));
        rows.Add(new(LastRunLabel, RunTimeText(lastRun, "Never")!));
        rows.Add(new(LastResultLabel, TaskResultText(lastRun, lastResult)));
        rows.Add(new(NextRunLabel, RunTimeText(nextRun, "Not scheduled")!));
        return new ComponentStatus(status, TaskSummary(triggers, lastRun, lastResult), rows);
    }

    /// <summary>A task that is there but this user may not read: every row past its name is Unknown.</summary>
    public static ComponentStatus UnreadableTask(string path) =>
        new("Unknown", $"{ScheduledTask} · details need administrator to read",
            [new ComponentDetail(NameLabel, path), .. TaskLabels.Select(l => new ComponentDetail(l, "Unknown"))]);

    /// <summary>A task that is not on this device: its name and status only.</summary>
    public static ComponentStatus MissingTask(string path) =>
        new("Not installed", $"{ScheduledTask} · not on this device",
            [new ComponentDetail(NameLabel, path), new ComponentDetail(StatusLabel, "Not installed")]);

    private static XElement? Parse(string xml)
    {
        try
        {
            return XElement.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static XElement? Find(XElement? parent, string name) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name);

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
            return TaskDetails(path, (int)task.State, (bool)task.Enabled, (DateTime)task.LastRunTime,
                (int)task.LastTaskResult, (DateTime)task.NextRunTime, (string)task.Xml);
        }
        catch (Exception ex) when (ex.HResult == FileNotFound || ex is FileNotFoundException)
        {
            return MissingTask(path);
        }
        catch (Exception)
        {
            // Access denied without elevation, or a scheduler that would not answer.
            return UnreadableTask(path);
        }
    }
}
