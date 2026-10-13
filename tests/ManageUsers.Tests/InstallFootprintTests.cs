using ManageUsers.Services;
using Xunit;

namespace ManageUsers.Tests;

/// <summary>The Prefs tab's Services card: task state, last result and triggers in plain words.</summary>
public sealed class InstallFootprintTests
{
    private const string Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static string Task(string triggers) =>
        $"""<?xml version="1.0" encoding="UTF-16"?><Task version="1.2" xmlns="{Ns}"><Triggers>{triggers}</Triggers></Task>""";

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
        Assert.Equal("Succeeded, Oct 12, 2026 3:00 AM", InstallFootprint.TaskResultText(when, 0));
        Assert.Equal("Failed (0x80070005), Oct 12, 2026 3:00 AM", InstallFootprint.TaskResultText(when, unchecked((int)0x80070005)));
        Assert.Equal("Failed (0x00000001), Oct 12, 2026 3:00 AM", InstallFootprint.TaskResultText(when, 1));
        Assert.Equal("Running now, Oct 12, 2026 3:00 AM", InstallFootprint.TaskResultText(when, 0x41301));
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
        Assert.Equal("Not installed", status.State);
    }
}
