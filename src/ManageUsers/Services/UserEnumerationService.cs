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

    public List<UserSessionInfo> GetUserSessions(HashSet<string> exclusions, bool protectAdmins, HashSet<string>? deletableAdmins = null)
    {
        var results = new List<UserSessionInfo>();
        var profiles = LoadProfiles();
        var localUsers = EnumerateLocalUsers();
        var adminSids = protectAdmins ? GetAdministratorSids() : EmptySidSet;
        deletableAdmins ??= EmptySidSet;

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

            // Never delete local administrators unless explicitly opted in via
            // delete_admins: true, or this specific account is named in
            // deletable_admins. Protects service/SSH admin accounts that have no
            // profile and never log in interactively (e.g. winadmins).
            if (protectAdmins && adminSids.Contains(sid))
            {
                if (deletableAdmins.Contains(name))
                {
                    _log.Info($"Administrator {name} is listed in deletable_admins — eligible for deletion");
                }
                else
                {
                    _log.Info($"Skipping administrator account (protected; add to deletable_admins or set delete_admins: true to override): {name}");
                    continue;
                }
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
    ///
    /// Matching strategy: build the set of SIDs for every local account, then walk
    /// ProfileList and collect the LocalPath of any entry whose SID is in that set.
    /// Anything in C:\Users not in that path set (and not a system folder / exclusion)
    /// is a stale candidate. This avoids the naive foldername-equals-username match
    /// which would misclassify collision-suffixed profile directories (e.g.
    /// "jsmith.ECU" for local account "jsmith").
    /// </summary>
    public List<StaleProfileInfo> GetStaleProfiles(HashSet<string> exclusions)
    {
        var results = new List<StaleProfileInfo>();

        // SIDs of every local account (enabled or disabled — we don't want to delete
        // a disabled local user's profile by accident).
        var localUsers = EnumerateLocalUsers();
        var localUserSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var localUserNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, _) in localUsers)
        {
            localUserNames.Add(name);
            var sid = ResolveSid(name);
            if (!string.IsNullOrEmpty(sid))
                localUserSids.Add(sid);
        }

        // Pre-index ProfileList by normalized LocalPath so the join below is O(1)
        // and tolerant of env-var / trailing-separator variation in ProfileImagePath.
        var profiles = LoadProfiles();
        var profilesByNormalizedPath = new Dictionary<string, ProfileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles.Values)
        {
            var key = NormalizeProfilePath(profile.LocalPath);
            if (!string.IsNullOrEmpty(key))
                profilesByNormalizedPath[key] = profile;
        }

        // Paths owned by real local accounts — these are NOT stale, even if the
        // folder name differs from the account name (collision suffixes, casing, etc).
        var localUserProfilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles.Values)
        {
            if (localUserSids.Contains(profile.Sid))
            {
                var key = NormalizeProfilePath(profile.LocalPath);
                if (!string.IsNullOrEmpty(key))
                    localUserProfilePaths.Add(key);
            }
        }

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

            var normalizedDir = NormalizeProfilePath(dir);

            // Skip if this path is owned by a real local account (via ProfileList).
            if (localUserProfilePaths.Contains(normalizedDir)) continue;

            // Fallback: folder-name match for local users that have no ProfileList
            // entry yet (brand-new accounts that haven't logged in). Safer than
            // deleting someone's home dir due to a missing registry join.
            if (localUserNames.Contains(folderName)) continue;

            // Look up any ProfileList entry for this directory (helps surface the SID
            // for registry cleanup even when no local user owns it).
            profilesByNormalizedPath.TryGetValue(normalizedDir, out var profileEntry);

            DateTime creationDate;
            try
            {
                creationDate = Directory.GetCreationTime(dir);
            }
            catch (Exception ex)
            {
                _log.Warning($"Could not get creation time for profile folder '{dir}': {ex.Message}. Falling back to last write time.");
                try
                {
                    creationDate = Directory.GetLastWriteTime(dir);
                }
                catch (Exception fallbackEx)
                {
                    _log.Warning($"Could not get last write time for '{dir}' either: {fallbackEx.Message}. Using DateTime.Now — policy evaluation may be inaccurate.");
                    creationDate = DateTime.Now;
                }
            }

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

    /// <summary>
    /// Normalize a profile path for comparison: expand env vars, resolve to a
    /// canonical full path, and trim trailing separators. Returns empty on failure.
    /// </summary>
    private static string NormalizeProfilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path);
            return Path.GetFullPath(expanded).TrimEnd('\\', '/');
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Detect internally inconsistent profile state left behind by partial deletions:
    /// dangling ProfileList entries whose folder is gone, and folders that still have
    /// a ProfileList entry but lost their NTUSER.DAT. Both states corrupt the next
    /// logon for that SID (temp profile, explorer "Class not registered"), so the
    /// engine remediates them regardless of retention policy. Only profiles not owned
    /// by a local account are considered; loaded hives are skipped by the deleter.
    /// </summary>
    public List<StaleProfileInfo> GetCorruptProfiles(HashSet<string> exclusions)
    {
        var results = new List<StaleProfileInfo>();

        var localUserSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, _) in EnumerateLocalUsers())
        {
            var sid = ResolveSid(name);
            if (!string.IsNullOrEmpty(sid))
                localUserSids.Add(sid);
        }

        foreach (var profile in LoadProfiles().Values)
        {
            if (localUserSids.Contains(profile.Sid)) continue;

            var localPath = NormalizeProfilePath(profile.LocalPath);
            if (string.IsNullOrEmpty(localPath)) continue;

            var folderName = Path.GetFileName(localPath);
            if (string.IsNullOrEmpty(folderName)) continue;

            var folderExists = Directory.Exists(localPath);
            string? reason = null;
            if (!folderExists)
                reason = "dangling ProfileList entry (profile folder missing)";
            else if (!File.Exists(Path.Combine(localPath, "NTUSER.DAT")))
                reason = "profile folder is missing NTUSER.DAT (partial delete)";

            if (reason == null) continue;

            // A gutted folder may still hold user files (Desktop, Documents) — for
            // excluded accounts surface it instead of deleting. Dangling entries have
            // no folder, so there is nothing to preserve even for exclusions.
            if (folderExists && exclusions.Contains(folderName))
            {
                _log.Warning($"Corrupt profile state for excluded account {folderName}: {reason} — not remediating, manual review needed");
                continue;
            }

            _log.Warning($"Corrupt profile state: {folderName} (SID: {profile.Sid}) — {reason}");

            results.Add(new StaleProfileInfo
            {
                FolderName = folderName,
                ProfilePath = localPath,
                Sid = profile.Sid,
                CreationDate = DateTime.Now,
                LastUseTime = profile.LastUseTime,
                HasRegistryEntry = true,
                IsCorrupt = true,
                CorruptReason = reason
            });
        }

        return results;
    }

    public List<string> FindOrphanedUsers(HashSet<string> exclusions, bool protectAdmins, HashSet<string>? deletableAdmins = null)
    {
        var orphans = new List<string>();
        var profiles = LoadProfiles();
        var localUsers = EnumerateLocalUsers();
        var adminSids = protectAdmins ? GetAdministratorSids() : EmptySidSet;
        deletableAdmins ??= EmptySidSet;

        foreach (var (name, _) in localUsers)
        {
            if (exclusions.Contains(name)) continue;

            // An admin account with no profile (service/SSH accounts like winadmins)
            // would otherwise be deleted here every run with no age check. Protect it
            // unless delete_admins: true or it's named in deletable_admins.
            if (protectAdmins && !deletableAdmins.Contains(name))
            {
                var sid = ResolveSid(name);
                if (!string.IsNullOrEmpty(sid) && adminSids.Contains(sid))
                {
                    _log.Info($"Skipping orphaned administrator account (protected; add to deletable_admins or set delete_admins: true to override): {name}");
                    continue;
                }
            }

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

    #region Administrators Group Membership (netapi32 P/Invoke)

    private static readonly HashSet<string> EmptySidSet = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string>? _adminSidCache;

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupGetMembers(
        string? serverName,
        string localGroupName,
        int level,
        out IntPtr bufptr,
        int prefmaxlen,
        out int entriesRead,
        out int totalEntries,
        ref IntPtr resumeHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct LOCALGROUP_MEMBERS_INFO_0
    {
        public IntPtr lgrmi0_sid;
    }

    private const int NERR_Success = 0;
    private const int ERROR_MORE_DATA = 234;

    /// <summary>
    /// Returns the SID strings of every member of the local Administrators group.
    /// Resolved by the well-known BUILTIN\Administrators SID so it works regardless
    /// of OS display language. Cached for the lifetime of this service instance.
    /// On failure returns whatever was gathered (possibly empty) and logs a warning —
    /// callers should treat an empty set as "could not determine admins".
    /// </summary>
    public HashSet<string> GetAdministratorSids()
    {
        if (_adminSidCache != null) return _adminSidCache;

        var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groupName = ResolveAdministratorsGroupName();
        IntPtr resume = IntPtr.Zero;
        var failed = false;

        // Page through the membership. With MAX_PREFERRED_LENGTH the API usually
        // returns everything in one call, but a large Administrators group can come
        // back as ERROR_MORE_DATA with a partially-filled buffer plus a non-zero
        // resume handle. Treat both NERR_Success and ERROR_MORE_DATA as "got a page,
        // process it"; only other codes are real failures. Critically, a hard
        // failure must NOT silently disable protection, so we log loudly.
        try
        {
            int result;
            do
            {
                IntPtr buffer = IntPtr.Zero;
                result = NetLocalGroupGetMembers(null, groupName, 0,
                    out buffer, MAX_PREFERRED_LENGTH, out int read, out _, ref resume);

                if (result != NERR_Success && result != ERROR_MORE_DATA)
                {
                    failed = true;
                    _log.Warning($"NetLocalGroupGetMembers('{groupName}') failed with code {result} — administrator protection may be incomplete");
                    if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
                    break;
                }

                try
                {
                    IntPtr current = buffer;
                    for (int i = 0; i < read; i++)
                    {
                        var info = Marshal.PtrToStructure<LOCALGROUP_MEMBERS_INFO_0>(current);
                        if (info.lgrmi0_sid != IntPtr.Zero)
                        {
                            try
                            {
                                var sid = new SecurityIdentifier(info.lgrmi0_sid);
                                sids.Add(sid.Value);
                            }
                            catch (Exception ex)
                            {
                                _log.Warning($"Could not convert an Administrators member SID: {ex.Message}");
                            }
                        }
                        current += Marshal.SizeOf<LOCALGROUP_MEMBERS_INFO_0>();
                    }
                }
                finally
                {
                    if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
                }
            }
            while (result == ERROR_MORE_DATA && resume != IntPtr.Zero);
        }
        catch (Exception ex)
        {
            failed = true;
            _log.Warning($"Error enumerating Administrators group members: {ex.Message} — administrator protection may be incomplete");
        }

        if (failed && sids.Count == 0)
            _log.Warning("Administrator enumeration returned no members — admin accounts may be left unprotected this run");
        else
            _log.Info($"Administrators group has {sids.Count} member SID(s)");

        return _adminSidCache = sids;
    }

    /// <summary>
    /// Resolve the localized name of the BUILTIN\Administrators group from its
    /// well-known SID (S-1-5-32-544). Falls back to "Administrators" on failure.
    /// </summary>
    private static string ResolveAdministratorsGroupName()
    {
        try
        {
            var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var account = (NTAccount)adminsSid.Translate(typeof(NTAccount));
            var value = account.Value; // e.g. "BUILTIN\Administrators"
            var slash = value.IndexOf('\\');
            return slash >= 0 ? value[(slash + 1)..] : value;
        }
        catch
        {
            return "Administrators";
        }
    }

    #endregion

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
            // S-1-5-21- = local/AD accounts. S-1-12-1- = Microsoft Entra (Azure AD)
            // cached profiles, the common case on shared lab devices.
            if (!sidStr.StartsWith("S-1-5-21-", StringComparison.Ordinal)
                && !sidStr.StartsWith("S-1-12-1-", StringComparison.Ordinal)) continue;

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

    // Registry hives and their transactional log/snapshot siblings get touched
    // by background system tasks (Windows Search, User Profile Service hive
    // maintenance, AV hive scans) that load every stale profile on the same
    // day, which makes their LastWriteTime useless as a proxy for actual user
    // activity. Exclude any file whose name starts with one of these prefixes
    // — this covers NTUSER.DAT.LOG1/2, UsrClass.dat{GUID}.TMContainer*,
    // *.regtrans-ms, and friends.
    private static readonly string[] HiveFileNamePrefixes =
    {
        "ntuser.dat", "usrclass.dat"
    };

    private static bool IsHiveFile(string name)
    {
        foreach (var prefix in HiveFileNamePrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static DateTime? GetFolderLastActivity(string path)
    {
        DateTime? latest = null;

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                var name = Path.GetFileName(entry);
                if (IsHiveFile(name)) continue;

                DateTime mtime;
                try { mtime = File.GetLastWriteTime(entry); }
                catch { continue; }

                if (latest == null || mtime > latest) latest = mtime;
            }
        }
        catch
        {
            // Enumeration failed (e.g., access denied). Fall through to the
            // folder-mtime fallback so callers still get a best-effort value.
        }

        if (latest != null) return latest;

        try { return Directory.GetLastWriteTime(path); }
        catch { return null; }
    }

    #endregion
}
