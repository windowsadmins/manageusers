using ManageUsers.Models;
using ManageUsers.Services;
using Xunit;

namespace ManageUsers.Tests;

/// <summary>A simulation reports what it would remove and removes nothing.</summary>
public sealed class SimulateTests : IDisposable
{
    private readonly DirectoryInfo _logs = Directory.CreateTempSubdirectory("manageusers-simulate-");
    private readonly LogService _log;
    private readonly List<string> _commands = [];

    public SimulateTests()
    {
        _log = new LogService(_logs.FullName);
    }

    public void Dispose()
    {
        _log.Dispose();
        try { _logs.Delete(recursive: true); } catch { }
    }

    private UserDeletionService Service(bool simulate) =>
        new(_log, new ConfigService(_log), simulate)
        {
            RunCommand = (file, args) =>
            {
                _commands.Add($"{file} {args}");
                return "The command completed successfully.";
            }
        };

    [Fact]
    public void SimulationNeverDeletesAnAccountWithNoProfile()
    {
        var service = Service(simulate: true);

        service.RemoveOrphanedUsers(["orphan1", "orphan2"], new SessionsData());

        Assert.Empty(_commands);
        Assert.Empty(service.RemovedItems);
        _log.Dispose();
        var audit = File.ReadAllText(Path.Combine(_logs.FullName, "manageusers.audit.log"));
        Assert.Contains("ORPHAN_USER_REMOVE_SIMULATED | user=orphan1", audit);
        Assert.Contains("ORPHAN_USER_REMOVE_SIMULATED | user=orphan2", audit);
    }

    [Fact]
    public void SimulationNeverDeletesAnAccount()
    {
        var service = Service(simulate: true);

        Assert.True(service.DeleteUser("lab01", new SessionsData()));

        Assert.Empty(_commands);
        Assert.Empty(service.RemovedItems);
    }

    [Fact]
    public void LiveRunDeletesAnAccountWithNoProfile()
    {
        var service = Service(simulate: false);

        service.RemoveOrphanedUsers(["orphan1"], new SessionsData());

        Assert.Equal(["net user \"orphan1\" /delete"], _commands);
        Assert.Equal(["orphan1"], service.RemovedItems);
    }
}
