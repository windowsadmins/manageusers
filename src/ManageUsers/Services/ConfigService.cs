using ManageUsers.Models;
using System.Diagnostics;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ManageUsers.Services;

/// <summary>
/// Reads Inventory.yaml and Sessions.yaml, writes Sessions.yaml updates.
/// </summary>
public sealed class ConfigService
{
    private readonly LogService _log;
    private readonly string _inventoryPath;
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(NullNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(NullNamingConvention.Instance)
        .Build();

    public ConfigService(LogService log, string? inventoryPath = null)
    {
        _log = log;
        _inventoryPath = inventoryPath ?? AppConstants.DefaultInventoryYamlPath;
    }

    public PolicyConfig LoadPolicyConfig()
    {
        var path = AppConstants.ConfigYamlPath;
        if (!File.Exists(path))
        {
            _log.Warning($"Config file not found: {path} — using built-in defaults");
            return GetDefaultPolicyConfig();
        }

        try
        {
            var yaml = File.ReadAllText(path);
            var config = Deserializer.Deserialize<PolicyConfig>(yaml);
            if (config?.Policies == null || config.Policies.Count == 0)
            {
                _log.Warning("Config.yaml has no policies defined — using built-in defaults");
                var defaults = GetDefaultPolicyConfig();
                defaults.Exclusions = config?.Exclusions ?? [];
                defaults.ProtectedSids = config?.ProtectedSids ?? [];
                defaults.DeleteAdmins = config?.DeleteAdmins ?? false;
                defaults.DeletableAdmins = config?.DeletableAdmins ?? [];
                return defaults;
            }
            _log.Info($"Loaded {config.Policies.Count} policy rule(s) from Config.yaml");
            return config;
        }
        catch (Exception ex)
        {
            // A Config.yaml that exists but cannot be read is not the same as no
            // Config.yaml. Its exclusions and protected SIDs are unknown, and the
            // built-in 28-day default would then reap the very accounts the file
            // was written to protect. Refuse to delete until the file is fixed.
            _log.Error($"Failed to parse Config.yaml: {ex.Message} — deleting nothing this run (never-delete fallback)");
            var fallback = GetDefaultPolicyConfig();
            fallback.DefaultPolicy = new DefaultPolicyRule { DurationDays = -1, Strategy = "login_and_creation" };
            fallback.Unreadable = true;
            return fallback;
        }
    }

    private static PolicyConfig GetDefaultPolicyConfig() => new()
    {
        DefaultPolicy = new DefaultPolicyRule { DurationDays = 28, Strategy = "login_and_creation" },
        EndOfTermDates =
        [
            new TermDate { Month = 4, Day = 30 },
            new TermDate { Month = 8, Day = 31 },
            new TermDate { Month = 12, Day = 31 }
        ]
    };

    public InventoryData LoadInventory()
    {
        if (!File.Exists(_inventoryPath))
        {
            _log.Warning($"Inventory file not found: {_inventoryPath}");
            return new InventoryData();
        }

        var yaml = File.ReadAllText(_inventoryPath);
        return Deserializer.Deserialize<InventoryData>(yaml) ?? new InventoryData();
    }

    public SessionsData LoadSessions()
    {
        var path = AppConstants.SessionsYamlPath;
        if (!File.Exists(path))
        {
            _log.Warning($"Sessions file not found: {path} — using defaults");
            return new SessionsData();
        }

        var yaml = File.ReadAllText(path);
        return Deserializer.Deserialize<SessionsData>(yaml) ?? new SessionsData();
    }

    public void SaveSessions(SessionsData data)
    {
        var path = AppConstants.SessionsYamlPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var yaml = Serializer.Serialize(data);
        File.WriteAllText(path, yaml);
    }

    /// <summary>
    /// Returns the merged exclusion set: always-excluded + Config.yaml exclusions + Sessions.yaml exclusions + currently logged-in user.
    /// </summary>
    public HashSet<string> GetEffectiveExclusions(SessionsData sessions, List<string>? configExclusions = null)
    {
        var exclusions = new HashSet<string>(AppConstants.AlwaysExcludedUsers, StringComparer.OrdinalIgnoreCase);

        // Merge from Config.yaml exclusions (service accounts defined fleet-wide)
        if (configExclusions != null)
        {
            foreach (var user in configExclusions)
            {
                if (!string.IsNullOrWhiteSpace(user))
                    exclusions.Add(user.Trim());
            }
        }

        // Merge from Sessions.yaml exclusions (machine-specific overrides)
        foreach (var user in sessions.Exclusions)
        {
            if (!string.IsNullOrWhiteSpace(user))
                exclusions.Add(user.Trim());
        }

        // Detect currently logged-in console user
        var consoleUser = GetConsoleUser();
        if (!string.IsNullOrEmpty(consoleUser))
        {
            exclusions.Add(consoleUser);
            _log.Info($"Console user detected and excluded: {consoleUser}");
        }

        return exclusions;
    }

    /// <summary>
    /// Returns the set of SIDs from Config.yaml <c>protected_sids:</c>. Entries that
    /// are not SID strings are dropped with a warning rather than failing the run:
    /// a malformed entry cannot protect anything, and dropping it leaves every
    /// well-formed entry in force.
    /// </summary>
    public HashSet<string> GetProtectedSids(List<string>? configSids)
    {
        var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (configSids == null) return sids;

        var rejected = 0;
        foreach (var entry in configSids)
        {
            var sid = entry?.Trim();
            if (string.IsNullOrEmpty(sid)) continue;
            if (!sid.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
            {
                rejected++;
                continue;
            }
            sids.Add(sid);
        }

        if (rejected > 0)
            _log.Warning($"Ignored {rejected} protected_sids entr{(rejected == 1 ? "y" : "ies")} that are not SID strings");

        return sids;
    }

    private static string? GetConsoleUser()
    {
        try
        {
            var psi = new ProcessStartInfo("quser")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            // quser output: USERNAME  SESSIONNAME  ID  STATE  IDLE TIME  LOGON TIME
            foreach (var line in output.Split('\n').Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Contains("Active", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split([' '], StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0)
                    {
                        var user = parts[0].TrimStart('>');
                        return user;
                    }
                }
            }
        }
        catch
        {
            // quser failed — not critical, just means we can't detect console user
        }

        return null;
    }
}
