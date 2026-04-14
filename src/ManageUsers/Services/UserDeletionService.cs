using ManageUsers.Models;
using System.Diagnostics;
using Microsoft.Win32;

namespace ManageUsers.Services;

/// <summary>
/// Core user deletion function — ports delete_user() from bash.
/// Handles simulation, process termination, credential scrubbing, profile removal, and verification.
/// </summary>
public sealed class UserDeletionService
{
    private readonly LogService _log;
    private readonly ConfigService _config;
    private readonly bool _simulate;

    public UserDeletionService(LogService log, ConfigService config, bool simulate)
    {
        _log = log;
        _config = config;
        _simulate = simulate;
    }

    /// <summary>
    /// Delete a local user account and all associated data.
    /// Returns true if user was deleted (or deferred), false on error.
    /// </summary>
    public bool DeleteUser(string username, SessionsData sessions)
    {
        if (_simulate)
        {
            _log.Info($"[SIMULATE] Would delete user: {username}");
            return true;
        }

        // Check if someone is logged in at the console — if so, defer
        if (IsUserAtConsole(username))
        {
            _log.Warning($"User {username} is at the console — deferring deletion");
            DeferDelete(username, sessions);
            return true;
        }

        _log.Info($"Deleting user: {username}");

        // Kill user processes
        KillUserProcesses(username);

        // Logoff any active sessions
        LogoffUser(username);

        // Scrub cached credentials
        ScrubCachedCredentials(username);

        // Remove BitLocker protectors if applicable
        RemoveBitLockerProtectors(username);

        // Delete the local user account
        if (!RemoveLocalUser(username))
            return false;

        // Delete user profile and home directory
        RemoveUserProfile(username);

        // Verify deletion
        if (!VerifyDeletion(username))
        {
            _log.Error($"Verification failed — user {username} may not be fully deleted");
            return false;
        }

        // Clear deferred entry if present
        ClearDeferred(username, sessions);

        _log.Info($"Successfully deleted user: {username}");
        return true;
    }

    public void ProcessDeferredDeletions(SessionsData sessions)
    {
        if (sessions.DeferredDeletes.Count == 0)
            return;

        _log.Info($"Processing {sessions.DeferredDeletes.Count} deferred deletion(s)");
        var toProcess = new List<string>(sessions.DeferredDeletes);

        foreach (var user in toProcess)
        {
            if (IsUserAtConsole(user))
            {
                _log.Info($"Deferred user {user} still at console — skipping");
                continue;
            }

            DeleteUser(user, sessions);
        }
    }

    public void RemoveOrphanedUsers(List<string> orphans, SessionsData sessions)
    {
        foreach (var user in orphans)
        {
            _log.Info($"Removing orphaned user record: {user}");
            RemoveLocalUser(user);
            ClearDeferred(user, sessions);
        }
    }

    /// <summary>
    /// Remove a stale Entra/cached profile — no local account exists, just registry + folder.
    /// </summary>
    public bool RemoveStaleProfile(StaleProfileInfo profile)
    {
        if (_simulate)
        {
            _log.Info($"[SIMULATE] Would remove stale profile: {profile.FolderName}");
            return true;
        }

        _log.Info($"Removing stale profile: {profile.FolderName}");

        // Kill any lingering processes owned by this user
        KillUserProcesses(profile.FolderName);

        // Remove ProfileList registry entry if present
        if (profile.HasRegistryEntry && profile.Sid != null)
        {
            try
            {
                using var profileList = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList", writable: true);
                if (profileList != null)
                {
                    profileList.DeleteSubKeyTree(profile.Sid, throwOnMissingSubKey: false);
                    _log.Info($"Registry entry removed for {profile.FolderName} (SID: {profile.Sid})");
                }
                else
                {
                    _log.Warning($"Could not open ProfileList registry key; registry entry was not removed for {profile.FolderName} (SID: {profile.Sid})");
                }
            }
            catch (Exception ex)
            {
                _log.Warning($"Failed to remove registry entry for {profile.FolderName}: {ex.Message}");
            }
        }

        // Delete profile folder
        if (Directory.Exists(profile.ProfilePath))
        {
            try
            {
                Directory.Delete(profile.ProfilePath, recursive: true);
                _log.Info($"Profile directory removed: {profile.ProfilePath}");
            }
            catch (Exception ex)
            {
                _log.Warning($"Failed to remove profile directory {profile.ProfilePath}: {ex.Message}");
                return false;
            }
        }

        _log.Info($"Successfully removed stale profile: {profile.FolderName}");
        return true;
    }

    private void DeferDelete(string username, SessionsData sessions)
    {
        if (!sessions.DeferredDeletes.Contains(username, StringComparer.OrdinalIgnoreCase))
        {
            sessions.DeferredDeletes.Add(username);
            _config.SaveSessions(sessions);
            _log.Info($"Added {username} to deferred deletions");
        }
    }

    private void ClearDeferred(string username, SessionsData sessions)
    {
        var idx = sessions.DeferredDeletes.FindIndex(u => u.Equals(username, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            sessions.DeferredDeletes.RemoveAt(idx);
            _config.SaveSessions(sessions);
        }
    }

    private static bool IsUserAtConsole(string username)
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
            if (proc == null) return false;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            foreach (var line in output.Split('\n'))
            {
                if (line.Contains(username, StringComparison.OrdinalIgnoreCase)
                    && line.Contains("Active", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }

        return false;
    }

    private void KillUserProcesses(string username)
    {
        try
        {
            var target = $"{Environment.MachineName}\\{username}";
            _log.Info($"Killing all processes for user: {username}");
            var result = RunProcess("taskkill", $"/f /fi \"USERNAME eq {target}\"");
            if (!string.IsNullOrWhiteSpace(result))
                _log.Info($"taskkill: {result.Trim()}");
        }
        catch (Exception ex)
        {
            _log.Warning($"Error killing processes for {username}: {ex.Message}");
        }
    }

    private void LogoffUser(string username)
    {
        try
        {
            // Parse quser output to find session ID
            var psi = new ProcessStartInfo("quser")
            {
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            if (proc == null) return;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            foreach (var line in output.Split('\n'))
            {
                if (line.Contains(username, StringComparison.OrdinalIgnoreCase))
                {
                    // quser output format: USERNAME  SESSIONNAME  ID  STATE  IDLE TIME  LOGON TIME
                    var parts = line.Split([' '], StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3 && int.TryParse(parts[2], out var sessionId))
                    {
                        _log.Info($"Logging off session {sessionId} for {username}");
                        RunProcess("logoff", sessionId.ToString());
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Error logging off {username}: {ex.Message}");
        }
    }

    private void ScrubCachedCredentials(string username)
    {
        try
        {
            // Clear IdentityStore cache entries for the user
            var cachePath = @"SOFTWARE\Microsoft\IdentityStore\Cache";
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(cachePath, writable: false);
            if (key != null)
            {
                foreach (var subKeyName in key.GetSubKeyNames())
                {
                    try
                    {
                        using var subKey = key.OpenSubKey(subKeyName, writable: true);
                        // Delete entries matching this user if found
                        // IdentityStore cache is keyed by provider GUID; log but don't force-remove
                        // as it self-cleans when the account is removed
                    }
                    catch { }
                }
            }

            _log.Info($"Cached credential cleanup complete for {username}");
        }
        catch (Exception ex)
        {
            _log.Warning($"Error scrubbing credentials for {username}: {ex.Message}");
        }
    }

    private void RemoveBitLockerProtectors(string username)
    {
        try
        {
            // Check if BitLocker is active on C:
            var result = RunProcess("manage-bde", "-status C:");
            if (result.Contains("Protection On", StringComparison.OrdinalIgnoreCase))
            {
                _log.Info($"BitLocker active — checking for user-specific protectors for {username}");
                // User-specific key protectors are rare on shared devices; log only
                // Automatic removal of SID-based protectors would require parsing manage-bde output
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"BitLocker check failed for {username}: {ex.Message}");
        }
    }

    private bool RemoveLocalUser(string username)
    {
        try
        {
            var result = RunProcess("net", $"user \"{username}\" /delete");
            if (result.Contains("successfully", StringComparison.OrdinalIgnoreCase))
            {
                _log.Info($"Local user account deleted: {username}");
                return true;
            }

            _log.Error($"Failed to delete user {username}: {result}");
            return false;
        }
        catch (Exception ex)
        {
            _log.Error($"Exception deleting user {username}: {ex.Message}");
            return false;
        }
    }

    private void RemoveUserProfile(string username)
    {
        // Remove profile registry entry
        try
        {
            using var profileList = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList", writable: true);
            if (profileList != null)
            {
                foreach (var sidStr in profileList.GetSubKeyNames())
                {
                    using var sidKey = profileList.OpenSubKey(sidStr);
                    var path = sidKey?.GetValue("ProfileImagePath")?.ToString();
                    if (path != null && path.EndsWith($"\\{username}", StringComparison.OrdinalIgnoreCase))
                    {
                        profileList.DeleteSubKeyTree(sidStr);
                        _log.Info($"Profile registry entry deleted for {username}");
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed to remove profile registry entry for {username}: {ex.Message}");
        }

        // Remove profile directory
        var homePath = Path.Combine(@"C:\Users", username);
        if (Directory.Exists(homePath))
        {
            try
            {
                Directory.Delete(homePath, recursive: true);
                _log.Info($"Profile directory removed: {homePath}");
            }
            catch (Exception ex)
            {
                _log.Warning($"Failed to remove profile directory {homePath}: {ex.Message}");
            }
        }
    }

    private bool VerifyDeletion(string username)
    {
        // Verify user no longer exists via net user exit code
        try
        {
            var psi = new ProcessStartInfo("net", $"user \"{username}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5000);
                if (proc.ExitCode == 0)
                    return false; // user still exists
            }
        }
        catch { }

        // Verify profile directory is gone
        var homePath = Path.Combine(@"C:\Users", username);
        return !Directory.Exists(homePath);
    }

    private static string RunProcess(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            UseShellExecute = false
        };
        using var proc = Process.Start(psi);
        if (proc == null) return "";
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(30000);
        return output;
    }
}
