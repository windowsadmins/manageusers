using ManageUsers.Models;
using System.Diagnostics;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ManageUsers.Services;

/// <summary>
/// Resolves the run's settings (policy, machine settings, Config.yaml, defaults), reads
/// Inventory.yaml and Sessions.yaml, and writes Sessions.yaml updates. Each file is
/// read only when no non-administrator could have written it; see <see cref="FileTrust"/>.
/// </summary>
public sealed class ConfigService
{
    private readonly LogService _log;
    private readonly string? _commandLineInventoryPath;
    private readonly ISettingsSource _policy;
    private readonly ISettingsSource _machine;
    private readonly FileTrust _trust;
    private string _inventoryPath = AppConstants.DefaultInventoryYamlPath;
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(NullNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(NullNamingConvention.Instance)
        .Build();

    public ConfigService(LogService log, string? inventoryPath = null)
        : this(log, inventoryPath, RegistrySettingsSource.Policy(), RegistrySettingsSource.MachineSettings(), FileTrust.Default)
    {
    }

    public ConfigService(LogService log, string? inventoryPath, ISettingsSource policy, ISettingsSource machine, FileTrust trust)
    {
        _log = log;
        _commandLineInventoryPath = inventoryPath;
        _policy = policy;
        _machine = machine;
        _trust = trust;
    }

    public PolicyConfig LoadPolicyConfig()
    {
        var resolved = SettingsResolver.Resolve(_policy, _machine, LoadConfigFile(), _commandLineInventoryPath);
        foreach (var note in resolved.Notes)
            _log.Warning(note);
        foreach (var name in SettingsResolver.AllSettingNames)
        {
            if (resolved.Sources.TryGetValue(name, out var source) && source != SettingSource.Default)
                _log.Info($"Setting {name} from {SettingsResolver.Describe(source)}");
        }

        var config = resolved.Config;
        _inventoryPath = resolved.InventoryPath;
        if (resolved.Sources[SettingsResolver.Policies] == SettingSource.Default)
            _log.Warning("No policy rules are set — using the built-in default policy");
        else
            _log.Info($"Loaded {config.Policies.Count} policy rule(s) from {SettingsResolver.Describe(resolved.Sources[SettingsResolver.Policies])}");
        return config;
    }

    private ConfigFile? LoadConfigFile()
    {
        var path = AppConstants.ConfigYamlPath;
        if (!File.Exists(path))
        {
            _log.Info($"Config file not found: {path}");
            return null;
        }
        if (!IsTrusted(path, "Config.yaml"))
            return null;

        try
        {
            return Deserializer.Deserialize<ConfigFile>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed to parse Config.yaml: {ex.Message} — ignoring it");
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> may be read. Otherwise logs which account could
    /// have written it and returns false, so the caller carries on without it.
    /// </summary>
    private bool IsTrusted(string path, string what)
    {
        // A run is SYSTEM (or elevated), so it can hand an administrator's own file to
        // Administrators before reading it; see FileTrust.
        if (_trust.NormalizeOwner(path) is { } note)
            _log.Info(note);

        var reason = _trust.WhyUntrusted(path);
        if (reason == null) return true;
        _log.Warning($"Ignoring {what} because a non-administrator could have written it: {reason}. " +
                     "Replace it as an administrator, or reinstall ManageUsers to reset the folder's permissions.");
        return false;
    }

    public InventoryData LoadInventory()
    {
        if (!File.Exists(_inventoryPath))
        {
            _log.Warning($"Inventory file not found: {_inventoryPath}");
            return new InventoryData();
        }
        if (!IsTrusted(_inventoryPath, "the inventory file"))
            return new InventoryData();

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
        if (!IsTrusted(path, "Sessions.yaml"))
            return new SessionsData();

        var yaml = File.ReadAllText(path);
        return Deserializer.Deserialize<SessionsData>(yaml) ?? new SessionsData();
    }

    /// <summary>
    /// Writes Sessions.yaml through a new file moved into place, so the result is owned by
    /// this process and inherits the folder's ACL, and a link left at the path is replaced
    /// rather than written through. Skipped when the folder itself is not trusted.
    /// </summary>
    public void SaveSessions(SessionsData data)
    {
        var path = AppConstants.SessionsYamlPath;
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var reason = _trust.WhyUntrusted(dir);
        if (reason != null)
        {
            _log.Warning($"Not saving Sessions.yaml because a non-administrator could change its folder: {reason}");
            return;
        }

        var temp = Path.Combine(dir, $".Sessions.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(stream))
                writer.Write(Serializer.Serialize(data));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
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
