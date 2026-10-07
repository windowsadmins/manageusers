using ManageUsers.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ManageUsers.Services;

/// <summary>Where a resolved setting came from, highest precedence first.</summary>
public enum SettingSource
{
    CommandLine,
    Policy,
    MachineSettings,
    ConfigFile,
    Default
}

/// <summary>The effective settings for a run, and where each one came from.</summary>
public sealed class ResolvedSettings
{
    public PolicyConfig Config { get; } = new();
    public string InventoryPath { get; set; } = AppConstants.DefaultInventoryYamlPath;
    public Dictionary<string, SettingSource> Sources { get; } = new(StringComparer.Ordinal);

    /// <summary>Values that were set but could not be used, for the caller to log.</summary>
    public List<string> Notes { get; } = [];
}

/// <summary>
/// Resolves every setting independently, highest precedence first: a command-line flag
/// for this run, then policy (HKLM\SOFTWARE\Policies\ManageUsers), then machine settings
/// (HKLM\SOFTWARE\ManageUsers\Settings), then Config.yaml, then the built-in default.
/// A value that is present but unusable is reported in <see cref="ResolvedSettings.Notes"/>
/// and the next layer applies.
/// </summary>
/// <remarks>
/// Registry values, by name:
///   Exclusions, DeletableAdmins       REG_MULTI_SZ, or REG_SZ separated by ; , or newlines
///   DeleteAdmins                      REG_DWORD 0/1, or REG_SZ true/false
///   Policies                          REG_SZ or REG_MULTI_SZ holding the YAML (or JSON)
///                                     list that Config.yaml's policies: key holds
///   DefaultPolicyDurationDays         REG_DWORD
///   DefaultPolicyStrategy             REG_SZ
///   DefaultPolicyForceAtEndOfTerm     REG_DWORD 0/1, or REG_SZ true/false
///   EndOfTermDates                    REG_MULTI_SZ, or REG_SZ list, of month-day pairs: 4-30
///   InventoryPath                     REG_SZ
/// </remarks>
public static class SettingsResolver
{
    public const string Exclusions = "Exclusions";
    public const string DeleteAdmins = "DeleteAdmins";
    public const string DeletableAdmins = "DeletableAdmins";
    public const string Policies = "Policies";
    public const string DefaultPolicyDurationDays = "DefaultPolicyDurationDays";
    public const string DefaultPolicyStrategy = "DefaultPolicyStrategy";
    public const string DefaultPolicyForceAtEndOfTerm = "DefaultPolicyForceAtEndOfTerm";
    public const string EndOfTermDates = "EndOfTermDates";
    public const string InventoryPath = "InventoryPath";

    /// <summary>Every setting the tool reads; each one can be set by policy.</summary>
    public static IReadOnlyList<string> AllSettingNames { get; } =
    [
        Exclusions, DeleteAdmins, DeletableAdmins, Policies, DefaultPolicyDurationDays,
        DefaultPolicyStrategy, DefaultPolicyForceAtEndOfTerm, EndOfTermDates, InventoryPath
    ];

    public static readonly List<TermDate> DefaultEndOfTermDates =
    [
        new TermDate { Month = 4, Day = 30 },
        new TermDate { Month = 8, Day = 31 },
        new TermDate { Month = 12, Day = 31 }
    ];

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(NullNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static ResolvedSettings Resolve(
        ISettingsSource policy,
        ISettingsSource machine,
        ConfigFile? file,
        string? commandLineInventoryPath = null)
    {
        var result = new ResolvedSettings();
        var layers = new (SettingSource Source, ISettingsSource Values)[]
        {
            (SettingSource.Policy, policy),
            (SettingSource.MachineSettings, machine)
        };

        T Pick<T>(string name, Func<object, T?> parse, T? fromFile, T fallback, string expected) where T : class
        {
            foreach (var (source, values) in layers)
            {
                if (values.GetValue(name) is not { } raw) continue;
                if (parse(raw) is { } parsed)
                {
                    result.Sources[name] = source;
                    return parsed;
                }
                result.Notes.Add($"Ignored {name} from {Describe(source)}: expected {expected}");
            }
            if (fromFile != null)
            {
                result.Sources[name] = SettingSource.ConfigFile;
                return fromFile;
            }
            result.Sources[name] = SettingSource.Default;
            return fallback;
        }

        T PickValue<T>(string name, Func<object, T?> parse, T? fromFile, T fallback, string expected) where T : struct =>
            Pick(name, raw => parse(raw) is { } v ? new Box<T>(v) : null,
                fromFile is { } f ? new Box<T>(f) : null, new Box<T>(fallback), expected).Value;

        var config = result.Config;
        config.Exclusions = Pick(Exclusions, ParseList, file?.Exclusions, [], "a list of account names");
        config.DeleteAdmins = PickValue(DeleteAdmins, ParseBool, file?.DeleteAdmins, false, "0/1 or true/false");
        config.DeletableAdmins = Pick(DeletableAdmins, ParseList, file?.DeletableAdmins, [], "a list of account names");
        config.Policies = Pick(Policies, ParsePolicies, file?.Policies, [], "a YAML or JSON list of policy rules");
        config.DefaultPolicy = new DefaultPolicyRule
        {
            DurationDays = PickValue(DefaultPolicyDurationDays, ParseInt, file?.DefaultPolicy?.DurationDays, 28, "a whole number of days"),
            Strategy = Pick(DefaultPolicyStrategy, ParseString, file?.DefaultPolicy?.Strategy, "login_and_creation", "a strategy name"),
            ForceAtEndOfTerm = PickValue(DefaultPolicyForceAtEndOfTerm, ParseBool, file?.DefaultPolicy?.ForceAtEndOfTerm, false, "0/1 or true/false")
        };

        // The built-in term dates belong to the built-in rule set: a site that supplies its
        // own rules and no term dates has none, as it always has.
        var defaultDates = result.Sources[Policies] == SettingSource.Default
            ? DefaultEndOfTermDates.Select(d => new TermDate { Month = d.Month, Day = d.Day }).ToList()
            : [];
        config.EndOfTermDates = Pick(EndOfTermDates, ParseTermDates, file?.EndOfTermDates, defaultDates, "month-day pairs such as 4-30");

        if (!string.IsNullOrWhiteSpace(commandLineInventoryPath))
        {
            result.InventoryPath = commandLineInventoryPath;
            result.Sources[InventoryPath] = SettingSource.CommandLine;
        }
        else
        {
            result.InventoryPath = Pick(InventoryPath, ParseString,
                string.IsNullOrWhiteSpace(file?.InventoryPath) ? null : file.InventoryPath,
                AppConstants.DefaultInventoryYamlPath, "a file path");
        }

        return result;
    }

    public static string Describe(SettingSource source) => source switch
    {
        SettingSource.CommandLine => "the command line",
        SettingSource.Policy => $@"policy (HKLM\{AppConstants.PolicyRegistryPath})",
        SettingSource.MachineSettings => $@"machine settings (HKLM\{AppConstants.SettingsRegistryPath})",
        SettingSource.ConfigFile => "Config.yaml",
        _ => "the built-in default"
    };

    private sealed class Box<T>(T value) where T : struct
    {
        public T Value { get; } = value;
    }

    private static string? ParseString(object raw) => raw switch
    {
        string s when !string.IsNullOrWhiteSpace(s) => s.Trim(),
        _ => null
    };

    private static bool? ParseBool(object raw) => raw switch
    {
        int i => i != 0,
        long l => l != 0,
        string s => s.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" => true,
            "0" or "false" or "no" => false,
            _ => null
        },
        _ => null
    };

    private static int? ParseInt(object raw) => raw switch
    {
        int i => i,
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        string s when int.TryParse(s.Trim(), out var i) => i,
        _ => null
    };

    /// <summary>An empty list is a valid value: it clears what a lower layer would set.</summary>
    private static List<string>? ParseList(object raw)
    {
        IEnumerable<string>? items = raw switch
        {
            string[] multi => multi,
            string s => s.Split([';', ',', '\r', '\n']),
            _ => null
        };
        return items?.Select(i => i.Trim()).Where(i => i.Length > 0).ToList();
    }

    private static List<PolicyRule>? ParsePolicies(object raw)
    {
        var text = raw switch
        {
            string[] lines => string.Join("\n", lines),
            string s => s,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return Yaml.Deserialize<List<PolicyRule>>(text);
        }
        catch
        {
            return null;
        }
    }

    private static List<TermDate>? ParseTermDates(object raw)
    {
        if (ParseList(raw) is not { } items) return null;
        var dates = new List<TermDate>();
        foreach (var item in items)
        {
            if (!TermDateText.TryParse(item, out var month, out var day))
                return null;
            dates.Add(new TermDate { Month = month, Day = day });
        }
        return dates;
    }
}
