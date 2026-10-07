using ManageUsers.Models;
using Microsoft.Win32;
using System.Runtime.Versioning;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ManageUsers.Services;

/// <summary>
/// Writes machine settings (HKLM\SOFTWARE\ManageUsers\Settings, 64-bit view) in the
/// form <see cref="SettingsResolver"/> reads back. Used by the app's Prefs tab once it
/// runs elevated. A setting that policy manages is never written: policy wins anyway,
/// and a machine value hidden under it would only surprise someone later.
/// </summary>
[SupportedOSPlatform("windows")]
public static class MachineSettingsStore
{
    private static readonly ISerializer Yaml = new SerializerBuilder()
        .WithNamingConvention(NullNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    /// <summary>
    /// The registry form of a setting value: string lists as REG_MULTI_SZ, booleans and
    /// numbers as REG_DWORD, the rule list as REG_MULTI_SZ lines of YAML, term dates as
    /// "month-day" entries.
    /// </summary>
    public static (object Value, RegistryValueKind Kind) Encode(object value) => value switch
    {
        bool b => (b ? 1 : 0, RegistryValueKind.DWord),
        int i => (i, RegistryValueKind.DWord),
        string s => (s, RegistryValueKind.String),
        IEnumerable<PolicyRule> rules => (EncodePolicies(rules), RegistryValueKind.MultiString),
        IEnumerable<TermDate> dates => (dates.Select(d => $"{d.Month}-{d.Day}").ToArray(), RegistryValueKind.MultiString),
        IEnumerable<string> list => (list.ToArray(), RegistryValueKind.MultiString),
        _ => throw new ArgumentException($"Unsupported setting value type {value.GetType().Name}")
    };

    private static string[] EncodePolicies(IEnumerable<PolicyRule> rules)
    {
        var text = Yaml.Serialize(rules.ToList()).TrimEnd();
        return text.Length == 0 ? [] : text.Replace("\r\n", "\n").Split('\n');
    }

    /// <summary>
    /// Writes <paramref name="values"/> by setting name. A null value removes the setting,
    /// so the next layer (Config.yaml, then the default) applies. Names policy sets are
    /// skipped and returned. Needs an elevated process.
    /// </summary>
    public static List<string> Save(IReadOnlyDictionary<string, object?> values, ISettingsSource policy)
    {
        var skipped = new List<string>();
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(AppConstants.SettingsRegistryPath, writable: true);
        foreach (var (name, value) in values)
        {
            if (!SettingsResolver.AllSettingNames.Contains(name))
                throw new ArgumentException($"Unknown setting {name}");
            if (policy.GetValue(name) != null)
            {
                skipped.Add(name);
                continue;
            }
            if (value == null)
            {
                key.DeleteValue(name, throwOnMissingValue: false);
                continue;
            }
            var (encoded, kind) = Encode(value);
            key.SetValue(name, encoded, kind);
        }
        return skipped;
    }
}
