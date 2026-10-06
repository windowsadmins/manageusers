using ManageUsers.Models;
using ManageUsers.Services;
using Microsoft.Win32;
using Xunit;

namespace ManageUsers.Tests;

/// <summary>The shared logic the Managed Users Cleanup app relies on.</summary>
public class GuiSupportTests
{
    private static ResolvedSettings ResolveEncoded(string name, object value)
    {
        var (encoded, _) = MachineSettingsStore.Encode(value);
        var machine = new DictionarySettingsSource(new Dictionary<string, object> { [name] = encoded });
        return SettingsResolver.Resolve(DictionarySettingsSource.Empty, machine, null);
    }

    [Fact]
    public void EverySettingTheAppSavesReadsBackTheSame()
    {
        var rules = new List<PolicyRule>
        {
            new() { Name = "Kiosks", Match = new MatchCriteria { Catalog = "^Kiosk$" }, DurationDays = 1, Strategy = "creation_only" },
            new() { Name = "Labs: shared", Match = new MatchCriteria { Usage = "^Shared$", Room = "A[0-9]+" }, DurationDays = -1, ForceAtEndOfTerm = true }
        };

        var result = ResolveEncoded("Policies", rules);
        Assert.Empty(result.Notes);
        Assert.Equal(SettingSource.MachineSettings, result.Sources["Policies"]);
        Assert.Collection(result.Config.Policies,
            r =>
            {
                Assert.Equal("Kiosks", r.Name);
                Assert.Equal("^Kiosk$", r.Match.Catalog);
                Assert.Null(r.Match.Usage);
                Assert.Equal(1, r.DurationDays);
                Assert.Equal("creation_only", r.Strategy);
            },
            r =>
            {
                Assert.Equal("Labs: shared", r.Name);
                Assert.Equal("A[0-9]+", r.Match.Room);
                Assert.Equal(-1, r.DurationDays);
                Assert.True(r.ForceAtEndOfTerm);
            });

        Assert.Equal(["svc-a", "svc b"], ResolveEncoded("Exclusions", new List<string> { "svc-a", "svc b" }).Config.Exclusions);
        Assert.True(ResolveEncoded("DeleteAdmins", true).Config.DeleteAdmins);
        Assert.False(ResolveEncoded("DeleteAdmins", false).Config.DeleteAdmins);
        Assert.Equal(["old"], ResolveEncoded("DeletableAdmins", new List<string> { "old" }).Config.DeletableAdmins);
        Assert.Equal(9, ResolveEncoded("DefaultPolicyDurationDays", 9).Config.DefaultPolicy.DurationDays);
        Assert.Equal("creation_only", ResolveEncoded("DefaultPolicyStrategy", "creation_only").Config.DefaultPolicy.Strategy);
        Assert.True(ResolveEncoded("DefaultPolicyForceAtEndOfTerm", true).Config.DefaultPolicy.ForceAtEndOfTerm);
        var dates = ResolveEncoded("EndOfTermDates", new List<TermDate> { new() { Month = 6, Day = 15 } }).Config.EndOfTermDates;
        Assert.Equal((6, 15), (Assert.Single(dates).Month, dates[0].Day));
        Assert.Equal(@"D:\inv.yaml", ResolveEncoded("InventoryPath", @"D:\inv.yaml").InventoryPath);
    }

    [Fact]
    public void ValuesAreWrittenAsReadableRegistryTypes()
    {
        Assert.Equal(RegistryValueKind.DWord, MachineSettingsStore.Encode(true).Kind);
        Assert.Equal(RegistryValueKind.DWord, MachineSettingsStore.Encode(28).Kind);
        Assert.Equal(RegistryValueKind.String, MachineSettingsStore.Encode("x").Kind);
        Assert.Equal(RegistryValueKind.MultiString, MachineSettingsStore.Encode(new List<string> { "a" }).Kind);
        var (dates, kind) = MachineSettingsStore.Encode(new List<TermDate> { new() { Month = 4, Day = 30 } });
        Assert.Equal(RegistryValueKind.MultiString, kind);
        Assert.Equal(new[] { "4-30" }, (string[])dates);
        var (rules, _) = MachineSettingsStore.Encode(new List<PolicyRule> { new() { Name = "A", DurationDays = 3 } });
        Assert.Contains("- name: A", (string[])rules);
    }

    [Fact]
    public void LiveRunIsLimitedToTheConfirmedNames()
    {
        Assert.Equal(["--live", "--only", "lab01", "--only", "Old Profile"],
            CliCommand.LiveConfirmed(["lab01", "Old Profile", " "]));
        Assert.Throws<ArgumentException>(() => CliCommand.LiveConfirmed([]));
        Assert.Equal(["--simulate"], CliCommand.Simulate());
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("", "\"\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\dir with space\", "\"C:\\dir with space\\\\\"")]
    [InlineData(@"C:\no\space", @"C:\no\space")]
    public void ArgumentsAreQuotedForTheCRuntime(string arg, string expected)
    {
        Assert.Equal(expected, CliCommand.Quote(arg));
    }

    [Theory]
    [InlineData("[2026-10-06 03:00:01] ERROR Fatal error: x", LogLineLevel.Error)]
    [InlineData("[2026-10-06 03:00:01] WARN  Ignoring Config.yaml", LogLineLevel.Warning)]
    [InlineData("[2026-10-06 03:00:01] INFO  USER_DELETED | user=lab01 sid=S-1", LogLineLevel.Audit)]
    [InlineData("[2026-10-06 03:00:01] INFO  USER_DELETE_FAILED | user=lab01", LogLineLevel.Error)]
    [InlineData("[2026-10-06 03:00:01] INFO  ========================================", LogLineLevel.Header)]
    [InlineData("[2026-10-06 03:00:01] INFO  Found 3 user(s)", LogLineLevel.Info)]
    [InlineData("[2026-10-06 03:00:01] INFO  WARNING is only a word here", LogLineLevel.Info)]
    public void LogLinesAreColouredByLevel(string line, LogLineLevel expected)
    {
        Assert.Equal(expected, LogLevels.Of(line));
    }

    [Fact]
    public void PlanRoundTripsThroughJson()
    {
        var plan = new SimulationPlan
        {
            Generated = new DateTimeOffset(2026, 10, 6, 3, 0, 0, TimeSpan.FromHours(-7)),
            Items =
            [
                new PlanItem { Kind = PlanItem.Account, Name = "lab01", Reason = "created 40d ago" },
                new PlanItem { Kind = PlanItem.Profile, Name = "old.DEVICE", Reason = "stale", Path = @"C:\Users\old.DEVICE" }
            ]
        };

        var back = SimulationPlan.FromJson(plan.ToJson());

        Assert.NotNull(back);
        Assert.Equal(plan.Generated, back.Generated);
        Assert.Equal(["lab01", "old.DEVICE"], back.Items.Select(i => i.Name));
        Assert.Equal(@"C:\Users\old.DEVICE", back.Items[1].Path);
        Assert.Null(SimulationPlan.FromJson("not json"));
    }

    [Theory]
    [InlineData("4-30", true, 4, 30)]
    [InlineData("12/31", true, 12, 31)]
    [InlineData("2-29", true, 2, 29)]
    [InlineData("2-30", false, 0, 0)]
    [InlineData("13-1", false, 0, 0)]
    [InlineData("April 30", false, 0, 0)]
    public void TermDatesParse(string text, bool ok, int month, int day)
    {
        Assert.Equal(ok, TermDateText.TryParse(text, out var m, out var d));
        if (ok) Assert.Equal((month, day), (m, d));
    }

    [Fact]
    public void PrefsAreEditableOnlyElevatedAndUnmanaged()
    {
        Assert.True(PrefsElevation.CanEdit(isElevated: true, isPolicyManaged: false));
        Assert.False(PrefsElevation.CanEdit(isElevated: true, isPolicyManaged: true));
        Assert.False(PrefsElevation.CanEdit(isElevated: false, isPolicyManaged: false));
        Assert.True(PrefsElevation.OpensOnPrefs(["--prefs"]));
        Assert.False(PrefsElevation.OpensOnPrefs(["--simulate"]));
        var relaunch = PrefsElevation.BuildElevatedRelaunch(@"C:\Program Files\ManageUsers\Managed Users Cleanup.exe");
        Assert.Equal("runas", relaunch.Verb);
        Assert.Equal("--prefs", relaunch.Arguments);
    }

    [Fact]
    public void CliIsFoundBesideTheApp()
    {
        var root = Directory.CreateTempSubdirectory("manageusers-find-");
        try
        {
            var cli = Path.Combine(root.FullName, "manageusers.exe");
            File.WriteAllText(cli, "");
            var found = CliCommand.Find(root.FullName);
            // The real install folder wins when this machine has ManageUsers installed.
            if (!File.Exists(Path.Combine(AppConstants.InstallDir, AppConstants.CliExecutableName)))
                Assert.Equal(cli, found);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
