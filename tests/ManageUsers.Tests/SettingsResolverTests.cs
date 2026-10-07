using ManageUsers.Models;
using ManageUsers.Services;
using Xunit;

namespace ManageUsers.Tests;

public class SettingsResolverTests
{
    private static DictionarySettingsSource Source(params (string Name, object Value)[] values) =>
        new(values.ToDictionary(v => v.Name, v => v.Value));

    private static readonly ISettingsSource None = DictionarySettingsSource.Empty;

    [Fact]
    public void PolicyBeatsMachineSettingsBeatsConfigFileBeatsDefault()
    {
        var policy = Source(("DeleteAdmins", 1));
        var machine = Source(("DeleteAdmins", 0), ("DefaultPolicyDurationDays", 14));
        var file = new ConfigFile
        {
            DeleteAdmins = false,
            DefaultPolicy = new DefaultPolicyFile { DurationDays = 7, Strategy = "creation" }
        };

        var result = SettingsResolver.Resolve(policy, machine, file);

        Assert.True(result.Config.DeleteAdmins);
        Assert.Equal(SettingSource.Policy, result.Sources["DeleteAdmins"]);
        Assert.Equal(14, result.Config.DefaultPolicy.DurationDays);
        Assert.Equal(SettingSource.MachineSettings, result.Sources["DefaultPolicyDurationDays"]);
        Assert.Equal("creation", result.Config.DefaultPolicy.Strategy);
        Assert.Equal(SettingSource.ConfigFile, result.Sources["DefaultPolicyStrategy"]);
        Assert.False(result.Config.DefaultPolicy.ForceAtEndOfTerm);
        Assert.Equal(SettingSource.Default, result.Sources["DefaultPolicyForceAtEndOfTerm"]);
    }

    [Fact]
    public void EachLayerFallsThroughToTheNextWhenUnset()
    {
        static List<string> Exclusions(ISettingsSource p, ISettingsSource m, ConfigFile? f) =>
            SettingsResolver.Resolve(p, m, f).Config.Exclusions;
        var file = new ConfigFile { Exclusions = ["file"] };

        Assert.Equal(["policy"], Exclusions(Source(("Exclusions", new[] { "policy" })), Source(("Exclusions", "machine")), file));
        Assert.Equal(["machine"], Exclusions(None, Source(("Exclusions", "machine")), file));
        Assert.Equal(["file"], Exclusions(None, None, file));
        Assert.Empty(Exclusions(None, None, null));
    }

    [Fact]
    public void EverySettingCanBeSetByPolicy()
    {
        var values = new Dictionary<string, object>
        {
            ["Exclusions"] = new[] { "svc-a" },
            ["DeleteAdmins"] = 1,
            ["DeletableAdmins"] = "old-admin",
            ["Policies"] = "- name: Lab\n  match:\n    usage: ^Shared$\n  duration_days: 3\n  strategy: creation",
            ["DefaultPolicyDurationDays"] = 10,
            ["DefaultPolicyStrategy"] = "creation",
            ["DefaultPolicyForceAtEndOfTerm"] = "true",
            ["EndOfTermDates"] = new[] { "6-30" },
            ["InventoryPath"] = @"C:\ProgramData\Management\Other.yaml"
        };
        Assert.Equal(SettingsResolver.AllSettingNames.OrderBy(n => n), values.Keys.OrderBy(n => n));

        var machine = Source(("Exclusions", "machine"), ("DeleteAdmins", 0), ("DefaultPolicyDurationDays", 99));
        var result = SettingsResolver.Resolve(new DictionarySettingsSource(values), machine, new ConfigFile
        {
            Exclusions = ["file"], DeleteAdmins = false, InventoryPath = @"C:\file.yaml"
        });

        Assert.All(SettingsResolver.AllSettingNames, name => Assert.Equal(SettingSource.Policy, result.Sources[name]));
        Assert.Empty(result.Notes);
        var config = result.Config;
        Assert.Equal(["svc-a"], config.Exclusions);
        Assert.True(config.DeleteAdmins);
        Assert.Equal(["old-admin"], config.DeletableAdmins);
        var rule = Assert.Single(config.Policies);
        Assert.Equal("Lab", rule.Name);
        Assert.Equal("^Shared$", rule.Match.Usage);
        Assert.Equal(3, rule.DurationDays);
        Assert.Equal(10, config.DefaultPolicy.DurationDays);
        Assert.Equal("creation", config.DefaultPolicy.Strategy);
        Assert.True(config.DefaultPolicy.ForceAtEndOfTerm);
        var date = Assert.Single(config.EndOfTermDates);
        Assert.Equal((6, 30), (date.Month, date.Day));
        Assert.Equal(@"C:\ProgramData\Management\Other.yaml", result.InventoryPath);
    }

    [Fact]
    public void PoliciesAcceptJsonAndMultiString()
    {
        var json = SettingsResolver.Resolve(
            Source(("Policies", "[{\"name\":\"Kiosk\",\"match\":{\"catalog\":\"^Kiosk$\"},\"duration_days\":1}]")), None, null);
        Assert.Equal("^Kiosk$", Assert.Single(json.Config.Policies).Match.Catalog);

        var multi = SettingsResolver.Resolve(
            Source(("Policies", new[] { "- name: A", "  duration_days: 2", "- name: B", "  duration_days: 4" })), None, null);
        Assert.Equal(["A", "B"], multi.Config.Policies.Select(p => p.Name));
    }

    [Fact]
    public void CommandLineBeatsPolicy()
    {
        var result = SettingsResolver.Resolve(
            Source(("InventoryPath", @"C:\policy.yaml")), None, null, commandLineInventoryPath: @"C:\cli.yaml");

        Assert.Equal(@"C:\cli.yaml", result.InventoryPath);
        Assert.Equal(SettingSource.CommandLine, result.Sources["InventoryPath"]);
    }

    [Fact]
    public void InventoryPathDefaultsWhenNothingSetsIt()
    {
        var result = SettingsResolver.Resolve(None, None, null);
        Assert.Equal(AppConstants.DefaultInventoryYamlPath, result.InventoryPath);
        Assert.Equal(SettingSource.Default, result.Sources["InventoryPath"]);
    }

    [Fact]
    public void UnusablePolicyValueIsReportedAndTheNextLayerApplies()
    {
        var result = SettingsResolver.Resolve(
            Source(("DeleteAdmins", "maybe"), ("EndOfTermDates", "13-40"), ("Policies", "{not: [valid")),
            Source(("DeleteAdmins", 1)),
            null);

        Assert.True(result.Config.DeleteAdmins);
        Assert.Equal(SettingSource.MachineSettings, result.Sources["DeleteAdmins"]);
        Assert.Equal(SettingSource.Default, result.Sources["EndOfTermDates"]);
        Assert.Equal(SettingSource.Default, result.Sources["Policies"]);
        Assert.Equal(3, result.Notes.Count);
        Assert.Contains(result.Notes, n => n.Contains("DeleteAdmins") && n.Contains("policy"));
    }

    [Fact]
    public void AnEmptyPolicyListClearsLowerLayers()
    {
        var result = SettingsResolver.Resolve(
            Source(("DeletableAdmins", Array.Empty<string>())), None, new ConfigFile { DeletableAdmins = ["rogue"] });

        Assert.Empty(result.Config.DeletableAdmins);
        Assert.Equal(SettingSource.Policy, result.Sources["DeletableAdmins"]);
    }

    [Fact]
    public void BuiltInTermDatesComeOnlyWithTheBuiltInRules()
    {
        var builtIn = SettingsResolver.Resolve(None, None, null);
        Assert.Equal(3, builtIn.Config.EndOfTermDates.Count);
        Assert.Equal(28, builtIn.Config.DefaultPolicy.DurationDays);

        var siteRules = SettingsResolver.Resolve(None, None, new ConfigFile { Policies = [new PolicyRule { Name = "Site" }] });
        Assert.Empty(siteRules.Config.EndOfTermDates);
    }
}
