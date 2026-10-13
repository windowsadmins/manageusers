using ManageUsers.Services;
using Xunit;

namespace ManageUsers.Tests;

/// <summary>The Prefs tab's Services card: task state, last result and triggers in plain words.</summary>
public sealed class InstallFootprintTests
{
    private const string Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static string Task(string triggers, string principal = "", string actions = "") =>
        $"""<?xml version="1.0" encoding="UTF-16"?><Task version="1.2" xmlns="{Ns}"><Triggers>{triggers}</Triggers><Principals>{principal}</Principals><Actions Context="Author">{actions}</Actions></Task>""";

    [Fact]
    public void FootprintIsTheScheduledTaskTheDeployCheckLooksFor()
    {
        var task = Assert.Single(InstallFootprint.Components);
        Assert.Equal(@"\WinAdmins-ManageUsers", task.Name);
        Assert.Equal(InstallFootprint.ScheduledTask, task.Kind);
    }

    [Theory]
    [InlineData(3, true, "Ready")]
    [InlineData(2, true, "Ready")]
    [InlineData(4, true, "Running")]
    [InlineData(1, true, "Disabled")]
    [InlineData(3, false, "Disabled")]
    [InlineData(0, true, "Unknown")]
    public void TaskStateInWords(int state, bool enabled, string expected) =>
        Assert.Equal(expected, InstallFootprint.TaskStateText(state, enabled));

    [Fact]
    public void TaskResultInWords()
    {
        var when = new DateTime(2026, 10, 12, 3, 0, 0);
        Assert.Equal("Succeeded", InstallFootprint.TaskResultText(when, 0));
        Assert.Equal("Failed (0x80070005)", InstallFootprint.TaskResultText(when, unchecked((int)0x80070005)));
        Assert.Equal("Failed (0x00000001)", InstallFootprint.TaskResultText(when, 1));
        Assert.Equal("Running now", InstallFootprint.TaskResultText(when, 0x41301));
    }

    [Fact]
    public void RunTimesInWords()
    {
        Assert.Equal("Oct 12, 2026 3:00 AM", InstallFootprint.RunTimeText(new DateTime(2026, 10, 12, 3, 0, 0), "Never"));
        Assert.Equal("Never", InstallFootprint.RunTimeText(new DateTime(1899, 12, 30), "Never"));
        Assert.Equal("Not scheduled", InstallFootprint.RunTimeText(null, "Not scheduled"));
    }

    [Theory]
    [InlineData("""<Principal id="Author"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal>""", "SYSTEM")]
    [InlineData(@"<Principal id=""Author""><UserId>NT AUTHORITY\SYSTEM</UserId></Principal>", "SYSTEM")]
    [InlineData("""<Principal id="Author"><GroupId>S-1-5-32-545</GroupId></Principal>""", "Users")]
    [InlineData(@"<Principal id=""Author""><UserId>DOMAIN\svc-run</UserId></Principal>", @"DOMAIN\svc-run")]
    public void PrincipalAsAccount(string principal, string expected) =>
        Assert.Equal(expected, InstallFootprint.AccountText(Task("", principal)));

    [Fact]
    public void TaskWithNoPrincipalHasNoAccountRow() =>
        Assert.Null(InstallFootprint.AccountText(Task("")));

    [Fact]
    public void ExecActionsAsPathAndArguments()
    {
        var xml = Task("", actions: """
            <Exec><Command>C:\Program Files\ManageUsers\manageusers.exe</Command><Arguments>--quiet</Arguments></Exec>
            <Exec><Command>"C:\Tools\a b.exe"</Command></Exec>
            <Exec><Command>cmd.exe</Command><Arguments>/c exit 0</Arguments></Exec>
            <ComHandler><ClassId>{00000000-0000-0000-0000-000000000000}</ClassId></ComHandler>
            """);
        Assert.Equal(
            "\"C:\\Program Files\\ManageUsers\\manageusers.exe\" --quiet\n\"C:\\Tools\\a b.exe\"\ncmd.exe /c exit 0",
            InstallFootprint.ActionsText(xml));
        Assert.Null(InstallFootprint.ActionsText(Task("")));
    }

    [Fact]
    public void ReadableTaskHasEveryRowInOrder()
    {
        var xml = Task(
            "<BootTrigger />",
            """<Principal id="Author"><UserId>S-1-5-18</UserId></Principal>""",
            """<Exec><Command>C:\Program Files\ManageUsers\manageusers.exe</Command></Exec>""");
        var status = InstallFootprint.TaskDetails(@"\WinAdmins-ManageUsers", 3, true, new DateTime(1899, 12, 30), 0x41303, new DateTime(2026, 10, 13, 3, 0, 0), xml);

        Assert.Equal("Ready", status.Status);
        Assert.Equal("Scheduled task · At startup · Never run", status.Summary);
        Assert.Equal(
            [("Name", @"\WinAdmins-ManageUsers"), ("Start type", "Enabled"), ("Status", "Ready"), ("Account", "SYSTEM"),
             ("Path and arguments", "\"C:\\Program Files\\ManageUsers\\manageusers.exe\""), ("Triggers", "At startup"),
             ("Last run", "Never"), ("Last result", "Never run"), ("Next run", "Oct 13, 2026 3:00 AM")],
            status.Details.Select(d => (d.Label, d.Value)));
    }

    [Fact]
    public void DisabledTaskWithoutPrincipalOrActionsOmitsThoseRows()
    {
        var status = InstallFootprint.TaskDetails(@"\T", 1, false, new DateTime(2026, 10, 12, 3, 0, 0), 1, null, Task(""));

        Assert.Equal("Disabled", status.Status);
        Assert.Equal("Scheduled task · No trigger (runs only on demand) · Last result 0x00000001", status.Summary);
        Assert.Equal(
            ["Name", "Start type", "Status", "Triggers", "Last run", "Last result", "Next run"],
            status.Details.Select(d => d.Label));
        Assert.Equal("Disabled", status.Details[1].Value);
        Assert.Equal("Failed (0x00000001)", status.Details.Single(d => d.Label == "Last result").Value);
        Assert.Equal("Not scheduled", status.Details.Single(d => d.Label == "Next run").Value);
    }

    [Fact]
    public void UnreadableTaskShowsUnknownAndMissingTaskShowsOnlyItsStatus()
    {
        var unreadable = InstallFootprint.UnreadableTask(@"\T");
        Assert.Equal("Unknown", unreadable.Status);
        Assert.Equal(9, unreadable.Details.Count);
        Assert.Equal(("Name", @"\T"), (unreadable.Details[0].Label, unreadable.Details[0].Value));
        Assert.All(unreadable.Details.Skip(1), d => Assert.Equal("Unknown", d.Value));

        var missing = InstallFootprint.MissingTask(@"\T");
        Assert.Equal("Not installed", missing.Status);
        Assert.Equal([("Name", @"\T"), ("Status", "Not installed")], missing.Details.Select(d => (d.Label, d.Value)));
    }

    [Fact]
    public void TaskThatNeverRanSaysSo()
    {
        Assert.Equal("Never run", InstallFootprint.TaskResultText(new DateTime(1899, 12, 30), 0));
        Assert.Equal("Never run", InstallFootprint.TaskResultText(null, 0));
        Assert.Equal("Never run", InstallFootprint.TaskResultText(new DateTime(2026, 1, 1), 0x41303));
    }

    [Fact]
    public void DailyAndStartupTriggersAsTheReadmeDescribesThem()
    {
        var xml = Task("""
            <CalendarTrigger><StartBoundary>2026-01-01T03:00:00</StartBoundary><Enabled>true</Enabled>
              <ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay></CalendarTrigger>
            <BootTrigger><Enabled>true</Enabled></BootTrigger>
            """);
        Assert.Equal("Daily at 3:00 AM; At startup", InstallFootprint.TriggerText(xml));
    }

    [Theory]
    [InlineData("<LogonTrigger><Enabled>true</Enabled></LogonTrigger>", "At logon of any user")]
    [InlineData(@"<LogonTrigger><UserId>DOMAIN\someone</UserId></LogonTrigger>", @"At logon of DOMAIN\someone")]
    [InlineData("<BootTrigger><Enabled>false</Enabled></BootTrigger>", "At startup (disabled)")]
    [InlineData("<CalendarTrigger><StartBoundary>2026-01-01T15:30:00</StartBoundary><ScheduleByDay><DaysInterval>3</DaysInterval></ScheduleByDay></CalendarTrigger>",
        "Every 3 days at 3:30 PM")]
    [InlineData("<CalendarTrigger><StartBoundary>2026-01-01T09:00:00</StartBoundary><ScheduleByWeek><WeeksInterval>1</WeeksInterval><DaysOfWeek><Monday /><Friday /></DaysOfWeek></ScheduleByWeek></CalendarTrigger>",
        "Weekly on Monday, Friday at 9:00 AM")]
    [InlineData("<CalendarTrigger><StartBoundary>2026-01-01T00:00:00</StartBoundary><ScheduleByMonth /></CalendarTrigger>",
        "Monthly at 12:00 AM")]
    [InlineData("<TimeTrigger><StartBoundary>2026-11-02T08:15:00</StartBoundary></TimeTrigger>", "Once on Nov 2, 2026 at 8:15 AM")]
    [InlineData("<BootTrigger><Repetition><Interval>PT4H</Interval></Repetition></BootTrigger>", "At startup, then every 4 hours")]
    [InlineData("<BootTrigger><Repetition><Interval>PT1H</Interval></Repetition></BootTrigger>", "At startup, then every hour")]
    [InlineData("<IdleTrigger />", "When the device is idle")]
    public void TriggerInWords(string trigger, string expected) =>
        Assert.Equal(expected, InstallFootprint.TriggerText(Task(trigger)));

    [Fact]
    public void TaskWithNoTriggerRunsOnlyOnDemand() =>
        Assert.Equal("No trigger (runs only on demand)", InstallFootprint.TriggerText(Task("")));

    [Fact]
    public void UnreadableXmlIsUnknown() =>
        Assert.Equal("Unknown", InstallFootprint.TriggerText("not xml"));

    [Fact]
    public void MissingTaskIsNotInstalled()
    {
        var status = InstallFootprint.QueryTask(@"\ManageUsersTests-NoSuchTask-" + Guid.NewGuid().ToString("N"));
        Assert.Equal("Not installed", status.Status);
    }

    [Fact]
    public void SummaryShowsTriggerAndResult()
    {
        var ran = new DateTime(2026, 10, 12, 3, 0, 0);
        Assert.Equal("Scheduled task · Daily at 3:00 AM · Last result 0", InstallFootprint.TaskSummary("Daily at 3:00 AM", ran, 0));
        Assert.Equal("Scheduled task · At startup · Last result 0x80070005", InstallFootprint.TaskSummary("At startup", ran, unchecked((int)0x80070005)));
        Assert.Equal("Scheduled task · At startup · Never run", InstallFootprint.TaskSummary("At startup", null, 0));
    }
}
