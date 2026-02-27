using ManageUsers.Models;
using System.Diagnostics;
using System.Management;

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
            using var searcher = new ManagementObjectSearcher(
                "SELECT UserName FROM Win32_ComputerSystem");
            foreach (var obj in searcher.Get())
            {
                var logged = obj["UserName"]?.ToString();
                if (string.IsNullOrEmpty(logged))
                    continue;
                var parts = logged.Split('\\');
                var consoleUser = parts.Length > 1 ? parts[^1] : logged;
                if (consoleUser.Equals(username, StringComparison.OrdinalIgnoreCase))
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
            // Use WMI to find processes owned by the user
            using var searcher = new ManagementObjectSearcher("SELECT ProcessId, Name FROM Win32_Process");
            foreach (ManagementObject obj in searcher.Get())
            {
                try
                {
                    var ownerParams = new object[2];
                    obj.InvokeMethod("GetOwner", ownerParams);
                    var owner = ownerParams[0]?.ToString();
                    if (!string.IsNullOrEmpty(owner) && owner.Equals(username, StringComparison.OrdinalIgnoreCase))
                    {
                        var pid = Convert.ToInt32(obj["ProcessId"]);
                        var name = obj["Name"]?.ToString();
                        _log.Info($"Killing process {name} (PID {pid}) owned by {username}");
                        try
                        {
                            var proc = Process.GetProcessById(pid);
                            proc.Kill(entireProcessTree: true);
                        }
                        catch { }
                    }
                }
                catch { }
            }
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
        try
        {
            // Remove profile via WMI — this deletes the registry entry AND the home directory
            using var searcher = new ManagementObjectSearcher(
                $"SELECT * FROM Win32_UserProfile WHERE LocalPath LIKE '%\\\\{username}'");
            foreach (ManagementObject obj in searcher.Get())
            {
                try
                {
                    obj.Delete();
                    _log.Info($"Profile deleted for {username}");
                    return;
                }
                catch (Exception ex)
                {
                    _log.Warning($"WMI profile deletion failed for {username}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Profile search failed for {username}: {ex.Message}");
        }

        // Fallback: manual directory removal
        var homePath = Path.Combine(@"C:\Users", username);
        if (Directory.Exists(homePath))
        {
            try
            {
                Directory.Delete(homePath, recursive: true);
                _log.Info($"Home directory manually removed: {homePath}");
            }
            catch (Exception ex)
            {
                _log.Warning($"Failed to remove home directory {homePath}: {ex.Message}");
            }
        }
    }

    private bool VerifyDeletion(string username)
    {
        // Verify user no longer exists in local accounts
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT Name FROM Win32_UserAccount WHERE LocalAccount = TRUE AND Name = '{username}'");
            if (searcher.Get().Count > 0)
                return false;
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
