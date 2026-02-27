using ManageUsers.Models;
using System.Management;

namespace ManageUsers.Services;

/// <summary>
/// Enumerates local users and gathers session data (creation dates, last login times, profile paths).
/// </summary>
public sealed class UserEnumerationService
{
    private readonly LogService _log;

    public UserEnumerationService(LogService log)
    {
        _log = log;
    }

    /// <summary>
    /// Builds session info for all enabled local users, excluding those in the exclusion set.
    /// </summary>
    public List<UserSessionInfo> GetUserSessions(HashSet<string> exclusions)
    {
        var results = new List<UserSessionInfo>();
        var profiles = LoadProfiles();

        using var userSearch = new ManagementObjectSearcher(
            "SELECT Name, SID, Disabled FROM Win32_UserAccount WHERE LocalAccount = TRUE");

        foreach (var obj in userSearch.Get())
        {
            var name = obj["Name"]?.ToString();
            var sid = obj["SID"]?.ToString();
            var disabled = obj["Disabled"] is bool d && d;

            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(sid))
                continue;
            if (disabled)
                continue;
            if (exclusions.Contains(name))
            {
                _log.Info($"Skipping excluded user: {name}");
                continue;
            }

            var profile = profiles.GetValueOrDefault(sid);
            var creationDate = GetCreationDate(name, profile);
            var lastLogin = GetLastLogin(sid, profile);

            results.Add(new UserSessionInfo
            {
                Username = name,
                Sid = sid,
                LastLogin = lastLogin,
                CreationDate = creationDate,
                ProfilePath = profile?.LocalPath,
                HasProfile = profile != null
            });

            _log.Info($"User: {name} | Created: {creationDate:yyyy-MM-dd} | LastLogin: {lastLogin?.ToString("yyyy-MM-dd") ?? "never"} | Profile: {(profile != null ? "yes" : "no")}");
        }

        return results;
    }

    private Dictionary<string, ProfileInfo> LoadProfiles()
    {
        var dict = new Dictionary<string, ProfileInfo>(StringComparer.OrdinalIgnoreCase);
        using var searcher = new ManagementObjectSearcher(
            "SELECT SID, LocalPath, LastUseTime FROM Win32_UserProfile WHERE Special = FALSE");

        foreach (var obj in searcher.Get())
        {
            var sid = obj["SID"]?.ToString();
            if (string.IsNullOrEmpty(sid))
                continue;

            DateTime? lastUse = null;
            var lastUseRaw = obj["LastUseTime"]?.ToString();
            if (!string.IsNullOrEmpty(lastUseRaw))
                lastUse = ManagementDateTimeConverter.ToDateTime(lastUseRaw);

            dict[sid] = new ProfileInfo
            {
                Sid = sid,
                LocalPath = obj["LocalPath"]?.ToString() ?? "",
                LastUseTime = lastUse
            };
        }

        return dict;
    }

    private DateTime GetCreationDate(string username, ProfileInfo? profile)
    {
        // Try profile folder creation time first
        if (profile != null && !string.IsNullOrEmpty(profile.LocalPath) && Directory.Exists(profile.LocalPath))
        {
            return Directory.GetCreationTime(profile.LocalPath);
        }

        // Fallback: use "now" as creation date (account exists but no profile — likely just created)
        _log.Warning($"Could not determine creation date for {username}, using current time");
        return DateTime.Now;
    }

    private DateTime? GetLastLogin(string sid, ProfileInfo? profile)
    {
        // Primary: Win32_UserProfile.LastUseTime
        if (profile?.LastUseTime != null)
            return profile.LastUseTime;

        // Fallback: Security Event Log — Event ID 4624, LogonType 2 (interactive), 10 (RemoteInteractive), 11 (CachedInteractive)
        return GetLastLoginFromEventLog(sid);
    }

    private DateTime? GetLastLoginFromEventLog(string sid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                $"SELECT TimeGenerated FROM Win32_NTLogEvent WHERE Logfile = 'Security' AND EventCode = 4624 AND Message LIKE '%{sid}%'");

            // WMI event log queries can be slow; limit is acceptable
            DateTime? latest = null;
            foreach (var obj in searcher.Get())
            {
                var tg = obj["TimeGenerated"]?.ToString();
                if (!string.IsNullOrEmpty(tg))
                {
                    var dt = ManagementDateTimeConverter.ToDateTime(tg);
                    if (latest == null || dt > latest)
                        latest = dt;
                }
            }
            return latest;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Finds local user accounts that have no matching Win32_UserProfile (orphaned records).
    /// </summary>
    public List<string> FindOrphanedUsers(HashSet<string> exclusions)
    {
        var orphans = new List<string>();
        var profiles = LoadProfiles();

        using var userSearch = new ManagementObjectSearcher(
            "SELECT Name, SID FROM Win32_UserAccount WHERE LocalAccount = TRUE");

        foreach (var obj in userSearch.Get())
        {
            var name = obj["Name"]?.ToString();
            var sid = obj["SID"]?.ToString();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(sid))
                continue;
            if (exclusions.Contains(name))
                continue;
            if (!profiles.ContainsKey(sid))
            {
                _log.Warning($"Orphaned user (no profile): {name} ({sid})");
                orphans.Add(name);
            }
        }

        return orphans;
    }

    private sealed class ProfileInfo
    {
        public required string Sid { get; init; }
        public required string LocalPath { get; init; }
        public DateTime? LastUseTime { get; init; }
    }
}
