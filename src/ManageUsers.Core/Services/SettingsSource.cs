using ManageUsers.Models;
using Microsoft.Win32;
using System.Runtime.Versioning;

namespace ManageUsers.Services;

/// <summary>A layer of named setting values, such as a registry key.</summary>
public interface ISettingsSource
{
    /// <summary>The raw value of <paramref name="name"/>, or null when it is not set here.</summary>
    object? GetValue(string name);
}

/// <summary>
/// Values under an HKLM key, read in the 64-bit view so a 32-bit writer's
/// WOW6432Node copy never shadows the real one.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistrySettingsSource : ISettingsSource
{
    private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);

    public RegistrySettingsSource(string subKeyPath)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(subKeyPath);
            if (key == null) return;
            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is { } value)
                    _values[name] = value;
            }
        }
        catch
        {
            // Unreadable key: treated as not set, and the next layer applies.
        }
    }

    public object? GetValue(string name) => _values.TryGetValue(name, out var v) ? v : null;

    public static RegistrySettingsSource Policy() => new(AppConstants.PolicyRegistryPath);
    public static RegistrySettingsSource MachineSettings() => new(AppConstants.SettingsRegistryPath);
}

/// <summary>An in-memory layer, for tests.</summary>
public sealed class DictionarySettingsSource(IDictionary<string, object> values) : ISettingsSource
{
    private readonly Dictionary<string, object> _values = new(values, StringComparer.OrdinalIgnoreCase);

    public static DictionarySettingsSource Empty { get; } = new(new Dictionary<string, object>());

    public object? GetValue(string name) => _values.TryGetValue(name, out var v) ? v : null;
}
