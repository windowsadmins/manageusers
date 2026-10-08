using ManageUsers.Models;
using ManageUsers.Services;
using Xunit;

namespace ManageUsers.Tests;

/// <summary>What a run reports: wording, each account once, the version shown.</summary>
public sealed class RunReportingTests
{
    [Fact]
    public void SimulationSaysWouldRemove()
    {
        Assert.Equal("Would remove 1 orphaned recycle bin(s)", RunWording.Removed(true, 1, "orphaned recycle bin(s)"));
        Assert.Equal("ManageUsers simulation complete — would remove 4 user(s)", RunWording.Summary(true, 4));
        Assert.DoesNotContain("removed", RunWording.Summary(true, 4));
    }

    [Fact]
    public void LiveRunSaysRemoved()
    {
        Assert.Equal("Removed 1 orphaned recycle bin(s)", RunWording.Removed(false, 1, "orphaned recycle bin(s)"));
        Assert.Equal("ManageUsers complete — 4 user(s) removed", RunWording.Summary(false, 4));
    }

    [Fact]
    public void AnAccountWithNoProfileIsLeftToTheOrphanPass()
    {
        var users = new List<UserSessionInfo>
        {
            new() { Username = "student1", Sid = "S-1-5-21-1-2-3-1001", HasProfile = true },
            new() { Username = "lab01", Sid = "S-1-5-21-1-2-3-1002", HasProfile = false },
        };

        Assert.Equal(["student1"], ManageUsersEngine.WithProfile(users).Select(u => u.Username));
    }

    [Theory]
    [InlineData("S-1-5-21-1-2-3-1001", @"C:\Users\student1", true)]
    [InlineData("S-1-5-21-1-2-3-1002", null, false)]
    [InlineData(null, null, false)]
    public void DeleteProfileWIsOnlyCalledForAProfile(string? sid, string? path, bool expected) =>
        Assert.Equal(expected, UserDeletionService.HasProfileToDelete(sid, path));

    [Theory]
    [InlineData("2026.10.06.2019+3b6a0bdea4f173dfe8862882c355d79842213430", "2026.10.06.2019")]
    [InlineData("2026.10.06.2019", "2026.10.06.2019")]
    [InlineData("", "dev")]
    [InlineData(null, "dev")]
    public void VersionShownIsTheTagsZeroPaddedForm(string? informational, string expected) =>
        Assert.Equal(expected, VersionText.FromInformational(informational));
}
