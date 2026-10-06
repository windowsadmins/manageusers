using ManageUsers.Services;
using Microsoft.Win32;
using Xunit;

namespace ManageUsers.Tests;

/// <summary>A simulation leaves the sign-in screen's hidden-account list alone.</summary>
public sealed class HiddenUsersTests : IDisposable
{
    private readonly string _keyPath = $@"Software\ManageUsersTests\{Guid.NewGuid():N}\UserList";
    private readonly DirectoryInfo _logs = Directory.CreateTempSubdirectory("manageusers-hidden-");
    private readonly LogService _log;

    public HiddenUsersTests()
    {
        _log = new LogService(_logs.FullName);
    }

    public void Dispose()
    {
        _log.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\ManageUsersTests", throwOnMissingSubKey: false);
        try { _logs.Delete(recursive: true); } catch { }
    }

    private RepairService Service(bool simulate) =>
        new(_log, simulate) { Hive = Registry.CurrentUser, UserListPath = _keyPath };

    [Fact]
    public void SimulationWritesNothingAndLogsWhatItWouldHide()
    {
        Service(simulate: true).UpdateHiddenUsers(["svc-backup", "Guest"]);

        Assert.Null(Registry.CurrentUser.OpenSubKey(_keyPath));
        _log.Dispose();
        var log = File.ReadAllText(Path.Combine(_logs.FullName, "manageusers.log"));
        Assert.Contains("SIMULATE: would hide svc-backup from the sign-in screen", log);
        Assert.DoesNotContain("would hide Guest", log);
    }

    [Fact]
    public void LiveRunHidesExcludedAccounts()
    {
        Service(simulate: false).UpdateHiddenUsers(["svc-backup", "Guest"]);

        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        Assert.NotNull(key);
        Assert.Equal(0, key.GetValue("svc-backup"));
        Assert.Null(key.GetValue("Guest"));
    }
}
