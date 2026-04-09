using ManageUsers.Models;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace ManageUsers.Services;

/// <summary>
/// Enumerates local users and gathers session data (creation dates, last login times, profile paths).
/// Uses Win32 P/Invoke (netapi32, advapi32) and Registry — no COM, no WMI, no ADSI.
/// </summary>
public sealed class UserEnumerationService
{
    private readonly LogService _log;

    public UserEnumerationService(LogService log)
    {
        _log = log;
    }

    public List<UserSessionInfo> GetUserSessions(HashSet<string> exclusions)
    {
        var results = new List<UserSessionInfo>();
        var profiles = LoadProfiles();
        var localUsers = EnumerateLocalUsers();

        foreach (var (name, disabled) in localUsers)
        {
            if (disabled) continue;
            if (exclusions.Contains(name))
            {
                _log.Info($"Skipping excluded user: {name}");
                continue;
            }

            // Match profile by path ending
            var profile = profiles.Values.FirstOrDefault(p =>
                p.LocalPath.EndsWith($"\\{name}", StringComparison.OrdinalIgnoreCase));

            var sid = profile?.Sid ?? ResolveSid(name);
            if (string.IsNullOrEmpty(sid))
            {
                _log.Warning($"Could not resolve SID for {name} — skipping");
                continue;
            }

            var creationDate = GetCreationDate(name, profile);
            var lastLogin = profile?.LastUseTime;

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

    /// <summary>
    /// Finds profile folders in C:\Users that have no corresponding local user account.
    /// These are typically Entra ID cached profiles that accumulate on shared devices.
    /// </summary>
    public List<StaleProfileInfo> GetStaleProfiles(HashSet<string> exclusions)
    {
        var results = new List<StaleProfileInfo>();
        var localUsers = EnumerateLocalUsers();
        var localUserNames = new HashSet<string>(
            localUsers.Select(u => u.Name), StringComparer.OrdinalIgnoreCase);
        var profiles = LoadProfiles();

        var usersDir = @"C:\Users";
        if (!Directory.Exists(usersDir)) return results;

        foreach (var dir in Directory.GetDirectories(usersDir))
        {
            var folderName = Path.GetFileName(dir);
            if (folderName == null) continue;

            // Skip system folders
            if (SystemProfileFolders.Contains(folderName)) continue;

            // Skip excluded users
            if (exclusions.Contains(folderName)) continue;

            // Skip if there's a matching local account
            if (localUserNames.Contains(folderName)) continue;

            // Find matching registry entry by profile path
            var profileEntry = profiles.Values.FirstOrDefault(p =>
                p.LocalPath.Equals(dir, StringComparison.OrdinalIgnoreCase));

            DateTime creationDate;
            try { creationDate = Directory.GetCreationTime(dir); }
            catch { creationDate = DateTime.Now; }

            var lastUseTime = profileEntry?.LastUseTime ?? GetFolderLastActivity(dir);

            results.Add(new StaleProfileInfo
            {
                FolderName = folderName,
                ProfilePath = dir,
                Sid = profileEntry?.Sid,
                CreationDate = creationDate,
                LastUseTime = lastUseTime,
                HasRegistryEntry = profileEntry != null
            });

            _log.Info($"Stale profile: {folderName} | Created: {creationDate:yyyy-MM-dd} | LastUse: {lastUseTime?.ToString("yyyy-MM-dd") ?? "unknown"} | Registry: {(profileEntry != null ? "yes" : "no")}");
        }

        return results;
    }

    public List<string> FindOrphanedUsers(HashSet<string> exclusions)
    {
        var orphans = new List<string>();
        var profiles = LoadProfiles();
        var localUsers = EnumerateLocalUsers();

        foreach (var (name, _) in localUsers)
        {
            if (exclusions.Contains(name)) continue;

            var hasProfile = profiles.Values.Any(p =>
                p.LocalPath.EndsWith($"\\{name}", StringComparison.OrdinalIgnoreCase));

            if (!hasProfile)
            {
                _log.Info($"Orphaned user (no profile): {name}");
                orphans.Add(name);
            }
        }

        return orphans;
    }

    #region Local User Enumeration (netapi32 P/Invoke)

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserEnum(
        string? serverName,
        int level,
        int filter,
        out IntPtr bufptr,
        int prefmaxlen,
        out int entriesRead,
        out int totalEntries,
        ref IntPtr resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct USER_INFO_1
    {
        public string usri1_name;
        public string usri1_password;
        public int usri1_password_age;
        public int usri1_priv;
        public string usri1_home_dir;
        public string usri1_comment;
        public int usri1_flags;
        public string usri1_script_path;
    }

    private const int UF_ACCOUNTDISABLE = 0x0002;
    private const int FILTER_NORMAL_ACCOUNT = 0x0002;
    private const int MAX_PREFERRED_LENGTH = -1;

    private List<(string Name, bool Disabled)> EnumerateLocalUsers()
    {
        var users = new List<(string, bool)>();
        IntPtr buffer = IntPtr.Zero;
        IntPtr resume = IntPtr.Zero;

        try
        {
            int result = NetUserEnum(null, 1, FILTER_NORMAL_ACCOUNT,
                out buffer, MAX_PREFERRED_LENGTH, out int read, out _, ref resume);

            if (result != 0)
            {
                _log.Warning($"NetUserEnum failed with code {result}");
                return users;
            }

            IntPtr current = buffer;
            for (int i = 0; i < read; i++)
            {
                var info = Marshal.PtrToStructure<USER_INFO_1>(current);
                if (info.usri1_name != null)
                {
                    bool disabled = (info.usri1_flags & UF_ACCOUNTDISABLE) != 0;
                    users.Add((info.usri1_name, disabled));
                }
                current += Marshal.SizeOf<USER_INFO_1>();
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Error enumerating local users: {ex.Message}");
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                NetApiBufferFree(buffer);
        }

        return users;
    }

    #endregion

    #region SID Resolution (advapi32 P/Invoke)

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupAccountName(
        string? systemName,
        string accountName,
        byte[]? sid,
        ref int cbSid,
        StringBuilder? domainName,
        ref int cbDomainName,
        out int peUse);

    private static string? ResolveSid(string username)
    {
        try
        {
            int cbSid = 0;
            int cbDomain = 0;
            LookupAccountName(null, username, null, ref cbSid, null, ref cbDomain, out _);

            var sid = new byte[cbSid];
            var domain = new StringBuilder(cbDomain);

            if (LookupAccountName(null, username, sid, ref cbSid, domain, ref cbDomain, out _))
            {
                var secId = new SecurityIdentifier(sid, 0);
                return secId.Value;
            }
        }
        catch { }

        return null;
    }

    #endregion

    #region Profile Enumeration (Registry)

    private Dictionary<string, ProfileInfo> LoadProfiles()
    {
        var dict = new Dictionary<string, ProfileInfo>(StringComparer.OrdinalIgnoreCase);

        using var profileList = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
        if (profileList == null) return dict;

        foreach (var sidStr in profileList.GetSubKeyNames())
        {
            if (!sidStr.StartsWith("S-1-5-21-")) continue;

            using var sidKey = profileList.OpenSubKey(sidStr);
            if (sidKey == null) continue;

            var path = sidKey.GetValue("ProfileImagePath")?.ToString();
            if (string.IsNullOrEmpty(path)) continue;

            dict[sidStr] = new ProfileInfo
            {
                Sid = sidStr,
                LocalPath = path,
                LastUseTime = GetProfileTimestamp(sidKey)
            };
        }

        return dict;
    }

    private static DateTime? GetProfileTimestamp(RegistryKey sidKey)
    {
        var high = sidKey.GetValue("LocalProfileLoadTimeHigh");
        var low = sidKey.GetValue("LocalProfileLoadTimeLow");

        if (high is int h && low is int l)
        {
            long fileTime = ((long)h << 32) | (uint)l;
            if (fileTime > 0)
            {
                try { return DateTime.FromFileTime(fileTime); }
                catch { return null; }
            }
        }

        return null;
    }

    #endregion

    private DateTime GetCreationDate(string username, ProfileInfo? profile)
    {
        var homePath = profile?.LocalPath ?? Path.Combine(@"C:\Users", username);
        if (Directory.Exists(homePath))
        {
            try { return Directory.GetCreationTime(homePath); }
            catch { }
        }

        _log.Warning($"Could not determine creation date for {username}, using current time");
        return DateTime.Now;
    }

    private sealed class ProfileInfo
    {
        public required string Sid { get; init; }
        public required string LocalPath { get; init; }
        public DateTime? LastUseTime { get; init; }
    }

    #region Stale Profile Helpers

    private static readonly HashSet<string> SystemProfileFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Public", "Default", "Default User", "All Users"
    };

    private static DateTime? GetFolderLastActivity(string path)
    {
        try
        {
            // NTUSER.DAT last write time is the best proxy for last interactive use
            var ntuserDat = Path.Combine(path, "NTUSER.DAT");
            if (File.Exists(ntuserDat))
                return File.GetLastWriteTime(ntuserDat);

            return Directory.GetLastWriteTime(path);
        }
        catch { return null; }
    }

    #endregion
}
