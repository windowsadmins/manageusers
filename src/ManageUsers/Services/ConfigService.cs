using ManageUsers.Models;
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
                return GetDefaultPolicyConfig();
            }
            _log.Info($"Loaded {config.Policies.Count} policy rule(s) from Config.yaml");
            return config;
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed to parse Config.yaml: {ex.Message} — using built-in defaults");
            return GetDefaultPolicyConfig();
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
    /// Returns the merged exclusion set: always-excluded + Sessions.yaml Exclusions + currently logged-in user.
    /// </summary>
    public HashSet<string> GetEffectiveExclusions(SessionsData sessions)
    {
        var exclusions = new HashSet<string>(AppConstants.AlwaysExcludedUsers, StringComparer.OrdinalIgnoreCase);

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

    private string? GetConsoleUser()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT UserName FROM Win32_ComputerSystem");
            foreach (var obj in searcher.Get())
            {
                var username = obj["UserName"]?.ToString();
                if (!string.IsNullOrEmpty(username))
                {
                    // Strip domain prefix (DOMAIN\user or AzureAD\user)
                    var parts = username.Split('\\');
                    return parts.Length > 1 ? parts[^1] : username;
                }
            }
        }
        catch
        {
            // WMI query failed — not critical, just means we can't detect console user
        }

        return null;
    }
}
