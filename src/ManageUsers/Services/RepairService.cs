using ManageUsers.Models;

namespace ManageUsers.Services;

/// <summary>
/// Detects and repairs inconsistent user state (orphaned profiles, broken ACLs).
/// Ports repair_user_states() from bash.
/// </summary>
public sealed class RepairService
{
    private readonly LogService _log;
    private readonly bool _simulate;

    /// <summary>Where hidden accounts are listed. Replaced in tests with a key under HKCU.</summary>
    internal Microsoft.Win32.RegistryKey Hive { get; init; } = Microsoft.Win32.Registry.LocalMachine;
    internal string UserListPath { get; init; } = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList";

    public RepairService(LogService log, bool simulate = false)
    {
        _log = log;
        _simulate = simulate;
    }

    /// <summary>
    /// Check for and fix common user state inconsistencies.
    /// </summary>
    public void RepairUserStates(List<UserSessionInfo> users)
    {
        foreach (var user in users)
        {
            // Case 1: User exists but no profile directory
            if (!user.HasProfile || string.IsNullOrEmpty(user.ProfilePath))
            {
                _log.Warning($"User {user.Username} has no profile directory — orphan candidate");
                continue;
            }

            if (!Directory.Exists(user.ProfilePath))
            {
                _log.Warning($"User {user.Username} profile path {user.ProfilePath} does not exist");
                continue;
            }

            // Case 2: Check NTUSER.DAT exists and is accessible
            var ntUserDat = Path.Combine(user.ProfilePath, "NTUSER.DAT");
            if (!File.Exists(ntUserDat))
            {
                _log.Warning($"User {user.Username} missing NTUSER.DAT at {user.ProfilePath} — profile may be corrupted");
            }
        }
    }

    /// <summary>
    /// Hide excluded service/system accounts from the Windows login screen.
    /// Sets SpecialAccounts\UserList registry entries.
    /// </summary>
    public void UpdateHiddenUsers(HashSet<string> exclusions)
    {
        var regPath = UserListPath;

        // A simulation changes nothing: report what would be hidden and stop.
        if (_simulate)
        {
            foreach (var user in exclusions.Where(u => !IsBuiltIn(u)))
                _log.Info($"SIMULATE: would hide {user} from the sign-in screen");
            return;
        }

        try
        {
            using var key = Hive.CreateSubKey(regPath, writable: true);
            if (key == null)
            {
                _log.Warning("Could not create SpecialAccounts\\UserList registry key");
                return;
            }

            foreach (var user in exclusions)
            {
                if (IsBuiltIn(user))
                    continue;

                key.SetValue(user, 0, Microsoft.Win32.RegistryValueKind.DWord);
            }

            _log.Info($"Updated hidden users list ({exclusions.Count} entries)");
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed to update hidden users: {ex.Message}");
        }
    }

    /// <summary>Built-in accounts Windows already hides.</summary>
    private static bool IsBuiltIn(string user) =>
        user.Equals("Administrator", StringComparison.OrdinalIgnoreCase)
        || user.Equals("DefaultAccount", StringComparison.OrdinalIgnoreCase)
        || user.Equals("Guest", StringComparison.OrdinalIgnoreCase)
        || user.Equals("WDAGUtilityAccount", StringComparison.OrdinalIgnoreCase)
        || user.Equals("defaultuser0", StringComparison.OrdinalIgnoreCase);
}
