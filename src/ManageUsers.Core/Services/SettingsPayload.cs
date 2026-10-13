using System.Text.Json;
using System.Text.Json.Nodes;
using ManageUsers.Models;

namespace ManageUsers.Services;

/// <summary>
/// The Prefs tab's pending changes as a JSON file, handed from the unelevated app to its
/// elevated, headless copy that writes them to machine settings. Each value is checked with
/// the rules the app's editors apply, and a setting policy sets is never written.
/// </summary>
public static class SettingsPayload
{
    /// <summary>The counting strategies a rule or the default policy can use.</summary>
    public static readonly string[] Strategies = ["login_and_creation", "creation_only"];

    public const int MinDurationDays = -1;
    public const int MaxDurationDays = 3650;

    /// <summary>Exit codes of the headless writer.</summary>
    public const int Saved = 0, InvalidPayload = 1, WriteFailed = 2;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Serializes setting values by name. A null value clears that machine setting.</summary>
    public static string Serialize(IReadOnlyDictionary<string, object?> values)
    {
        var root = new JsonObject();
        foreach (var (name, value) in values)
            root[name] = value == null ? null : JsonSerializer.SerializeToNode(value, value.GetType(), Json);
        return root.ToJsonString(Json);
    }

    /// <summary>
    /// Reads and checks a payload. Throws <see cref="InvalidDataException"/> naming the first
    /// value that is unknown, of the wrong shape, or outside what the app allows.
    /// </summary>
    public static Dictionary<string, object?> Deserialize(string json)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("The payload is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The payload is not valid JSON: {ex.Message}");
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, node) in root)
        {
            if (!SettingsResolver.AllSettingNames.Contains(name))
                throw new InvalidDataException($"Unknown setting {name}.");
            try
            {
                values[name] = node == null ? null : Read(name, node);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                throw new InvalidDataException($"{name} is not in the expected form: {ex.Message}");
            }
        }
        return values;
    }

    private static object Read(string name, JsonNode node) => name switch
    {
        SettingsResolver.Exclusions or SettingsResolver.DeletableAdmins => Names(name, node.Deserialize<List<string>>()),
        SettingsResolver.DeleteAdmins or SettingsResolver.DefaultPolicyForceAtEndOfTerm => node.GetValue<bool>(),
        SettingsResolver.DefaultPolicyDurationDays => Days(name, node.GetValue<int>()),
        SettingsResolver.DefaultPolicyStrategy => Strategy(name, node.GetValue<string>()),
        SettingsResolver.InventoryPath => node.GetValue<string>() is { } path && !string.IsNullOrWhiteSpace(path)
            ? path.Trim()
            : throw new InvalidDataException($"{name} is empty."),
        SettingsResolver.EndOfTermDates => Dates(name, node.Deserialize<List<TermDate>>()),
        SettingsResolver.Policies => Rules(name, node.Deserialize<List<PolicyRule>>()),
        _ => throw new InvalidDataException($"Unknown setting {name}.")
    };

    private static List<string> Names(string name, List<string>? list)
    {
        if (list == null || list.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException($"{name} must be a list of account names.");
        return list.Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static int Days(string name, int days) => days is >= MinDurationDays and <= MaxDurationDays
        ? days
        : throw new InvalidDataException($"{name} must be between {MinDurationDays} and {MaxDurationDays} days.");

    private static string Strategy(string name, string? strategy) => strategy != null && Strategies.Contains(strategy)
        ? strategy
        : throw new InvalidDataException($"{name} must be one of {string.Join(", ", Strategies)}.");

    private static List<TermDate> Dates(string name, List<TermDate>? dates)
    {
        if (dates == null || dates.Any(d => d == null || !TermDateText.TryParse($"{d.Month}-{d.Day}", out _, out _)))
            throw new InvalidDataException($"{name} must be month-day pairs such as 4-30.");
        return dates;
    }

    private static List<PolicyRule> Rules(string name, List<PolicyRule>? rules)
    {
        if (rules == null || rules.Any(r => r == null || r.Match == null))
            throw new InvalidDataException($"{name} must be a list of rules.");
        foreach (var rule in rules)
        {
            Days(name, rule.DurationDays);
            Strategy(name, rule.Strategy);
        }
        return rules;
    }

    /// <summary>
    /// The headless writer: reads the payload at <paramref name="path"/>, drops every setting
    /// <paramref name="policy"/> sets, and hands the rest to <paramref name="write"/>. Returns an
    /// exit code from this class. The payload file is deleted either way.
    /// </summary>
    public static int Apply(string path, ISettingsSource policy, Action<IReadOnlyDictionary<string, object?>> write)
    {
        Dictionary<string, object?> values;
        try
        {
            values = Deserialize(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            TryDelete(path);
            return InvalidPayload;
        }
        TryDelete(path);

        var unmanaged = values.Where(kv => policy.GetValue(kv.Key) == null)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        try
        {
            write(unmanaged);
            return Saved;
        }
        catch (Exception)
        {
            return WriteFailed;
        }
    }

    /// <summary>A headless writer's exit code in words, for the app's "Not saved" caption.</summary>
    public static string ExitCodeText(int exitCode) => exitCode switch
    {
        Saved => "Saved",
        InvalidPayload => "the changes could not be read back, or a value was not valid",
        WriteFailed => "the machine settings could not be written",
        _ => $"the settings writer stopped with exit code {exitCode}"
    };

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
