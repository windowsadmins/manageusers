using System.Xml.Linq;
using ManageUsers.Services;
using Xunit;

namespace ManageUsers.Tests;

/// <summary>
/// The ADMX template in resources/ must cover every setting the tool reads, write each one
/// in a shape the resolver accepts, and resolve every string and presentation it names.
/// </summary>
public class AdmxTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions";
    private const string PolicyKey = @"SOFTWARE\Policies\ManageUsers";

    private static string ResourcesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ManageUsers.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "resources");
    }

    private static XDocument Admx() => XDocument.Load(Path.Combine(ResourcesDir(), "ManageUsers.admx"));
    private static XDocument Adml() => XDocument.Load(Path.Combine(ResourcesDir(), "en-US", "ManageUsers.adml"));

    /// <summary>Each policy's value name, and the element kind that writes it.</summary>
    private static Dictionary<string, string> ValueNames()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var policy in Admx().Descendants(Ns + "policy"))
        {
            Assert.Equal(PolicyKey, (string?)policy.Attribute("key"));
            Assert.Equal("Machine", (string?)policy.Attribute("class"));
            if ((string?)policy.Attribute("valueName") is { } direct)
                result.Add(direct, "boolean");
            foreach (var element in policy.Element(Ns + "elements")?.Elements() ?? [])
                result.Add((string)element.Attribute("valueName")!, element.Name.LocalName);
        }
        return result;
    }

    [Fact]
    public void Admx_CoversEverySettingAndNothingElse()
    {
        Assert.Equal(
            SettingsResolver.AllSettingNames.OrderBy(n => n),
            ValueNames().Keys.OrderBy(n => n));
    }

    [Fact]
    public void Admx_EveryReferenceResolves()
    {
        var admx = Admx();
        var adml = Adml();
        var strings = adml.Descendants(Ns + "string").Select(s => (string)s.Attribute("id")!).ToHashSet();
        var presentations = adml.Descendants(Ns + "presentation").ToDictionary(p => (string)p.Attribute("id")!);
        var categories = admx.Descendants(Ns + "category").Select(c => (string)c.Attribute("name")!).ToHashSet();
        var supported = admx.Descendants(Ns + "definition").Select(d => (string)d.Attribute("name")!).ToHashSet();

        foreach (var attr in admx.Descendants().Attributes())
        {
            var v = attr.Value;
            if (v.StartsWith("$(string.", StringComparison.Ordinal))
                Assert.Contains(v["$(string.".Length..^1], strings);
        }
        foreach (var parent in admx.Descendants(Ns + "parentCategory"))
            Assert.Contains((string)parent.Attribute("ref")!, categories);
        foreach (var on in admx.Descendants(Ns + "supportedOn").Where(e => e.Attribute("ref") != null))
            Assert.Contains((string)on.Attribute("ref")!, supported);

        foreach (var policy in admx.Descendants(Ns + "policy"))
        {
            var elements = policy.Element(Ns + "elements")?.Elements().ToList() ?? [];
            var presentationRef = (string?)policy.Attribute("presentation");
            if (elements.Count == 0)
            {
                Assert.Null(presentationRef);
                continue;
            }
            Assert.NotNull(presentationRef);
            var presentation = presentations[presentationRef!["$(presentation.".Length..^1]];
            var refIds = presentation.Elements().Select(e => (string?)e.Attribute("refId")).ToHashSet();
            foreach (var element in elements)
                Assert.Contains((string)element.Attribute("id")!, refIds);
        }
    }

    /// <summary>The registry value Group Policy writes for each element kind, as RegistryKey.GetValue returns it.</summary>
    private static object SampleValue(string name, string kind) => (name, kind) switch
    {
        (SettingsResolver.EndOfTermDates, "multiText") => new[] { "4-30", "12-31" },
        (SettingsResolver.Policies, "multiText") => new[]
        {
            "- name: Kiosks",
            "  match:",
            "    catalog: ^Kiosk$",
            "  duration_days: -1",
            "  strategy: creation_only"
        },
        (_, "multiText") => new[] { "svc-one", "svc-two" },
        (SettingsResolver.DefaultPolicyDurationDays, "text") => "-1",
        (_, "boolean") or (_, "decimal") => 1,
        (_, "enum") => "creation_only",
        (_, "text") => @"D:\Inventory.yaml",
        _ => throw new InvalidOperationException($"No sample for {kind}")
    };

    [Fact]
    public void Admx_EveryValueItWritesIsReadAsPolicy()
    {
        var values = ValueNames().ToDictionary(kv => kv.Key, kv => SampleValue(kv.Key, kv.Value));
        var resolved = SettingsResolver.Resolve(
            new DictionarySettingsSource(values), DictionarySettingsSource.Empty, file: null);

        Assert.Empty(resolved.Notes);
        foreach (var name in SettingsResolver.AllSettingNames)
            Assert.Equal(SettingSource.Policy, resolved.Sources[name]);
        Assert.Equal(["svc-one", "svc-two"], resolved.Config.Exclusions);
        Assert.True(resolved.Config.DeleteAdmins);
        var rule = Assert.Single(resolved.Config.Policies);
        Assert.Equal("Kiosks", rule.Name);
        Assert.Equal(-1, rule.DurationDays);
        Assert.Equal(-1, resolved.Config.DefaultPolicy.DurationDays);
        Assert.Equal("creation_only", resolved.Config.DefaultPolicy.Strategy);
        Assert.Equal(2, resolved.Config.EndOfTermDates.Count);
        Assert.Equal(@"D:\Inventory.yaml", resolved.InventoryPath);
    }

    /// <summary>
    /// -1 (never delete) is a real duration, and an ADMX decimal is unsigned, so the
    /// duration is a text element: REG_SZ, which the resolver parses as a whole number.
    /// </summary>
    [Fact]
    public void Admx_DurationIsTextSoMinusOneFits()
    {
        var element = Admx().Descendants()
            .Single(e => (string?)e.Attribute("valueName") == SettingsResolver.DefaultPolicyDurationDays);
        Assert.Equal("text", element.Name.LocalName);
        Assert.Equal("DefaultPolicyDurationDays_Value", (string?)element.Attribute("id"));

        var resolved = SettingsResolver.Resolve(
            new DictionarySettingsSource(new Dictionary<string, object> { [SettingsResolver.DefaultPolicyDurationDays] = "-1" }),
            DictionarySettingsSource.Empty, file: null);
        Assert.Equal(-1, resolved.Config.DefaultPolicy.DurationDays);
        Assert.Equal(SettingSource.Policy, resolved.Sources[SettingsResolver.DefaultPolicyDurationDays]);
    }

    [Fact]
    public void Admx_EnumValuesAreStrategiesTheAppOffers()
    {
        var items = Admx().Descendants(Ns + "enum")
            .Single(e => (string?)e.Attribute("valueName") == SettingsResolver.DefaultPolicyStrategy)
            .Descendants(Ns + "string").Select(s => s.Value);
        Assert.Equal(["login_and_creation", "creation_only"], items);
    }
}
