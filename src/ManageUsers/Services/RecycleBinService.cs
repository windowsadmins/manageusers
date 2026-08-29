using System.Security.Principal;
using Microsoft.Win32;

namespace ManageUsers.Services;

/// <summary>
/// Removes per-SID recycle bins.
///
/// Deleting a profile does not delete what that user sent to the recycle bin: the
/// files live in a per-SID folder under "$Recycle.Bin" on the root of every volume,
/// outside the profile directory. Once the profile and its ProfileList entry are
/// gone the leftover is unattributable — no account, no profile, just a SID-named
/// folder holding someone's deleted files — and nothing ever reclaims it.
/// </summary>
public sealed class RecycleBinService
{
    private const string RecycleBinFolder = "$Recycle.Bin";

    private readonly LogService _log;
    private readonly bool _simulate;

    public RecycleBinService(LogService log, bool simulate)
    {
        _log = log;
        _simulate = simulate;
    }

    /// <summary>
    /// Delete one SID's recycle bin from every fixed volume. Called as part of
    /// removing that SID's profile; safe to call with a null/blank SID (no-op).
    /// </summary>
    public void RemoveForSid(string? sid, string owner)
    {
        if (string.IsNullOrWhiteSpace(sid))
        {
            // Without a SID there is nothing to match on: the folders are named by
            // SID only, and guessing from the username would risk another account's.
            _log.Warning($"No SID for {owner}; recycle bin could not be located and may remain");
            return;
        }

        foreach (var dir in EnumerateSidFolders())
        {
            if (!string.Equals(dir.Name, sid, StringComparison.OrdinalIgnoreCase))
                continue;

            Purge(dir, owner, sid, orphaned: false);
        }
    }

    /// <summary>
    /// Delete recycle bins whose SID has no profile on this machine.
    ///
    /// These accumulate from every profile removed before recycle bins were cleaned
    /// up, and from any partial deletion. A SID with a ProfileList entry or a loaded
    /// hive is left alone — that bin still belongs to someone.
    /// </summary>
    /// <returns>Number of orphaned bins removed.</returns>
    public int SweepOrphaned()
    {
        var known = LoadProfileListSids();
        if (known.Count == 0)
        {
            // An empty ProfileList means the read failed, not that every bin is
            // orphaned. Treating it as authoritative would delete all of them.
            _log.Warning("ProfileList could not be read; skipping orphaned recycle bin sweep");
            return 0;
        }

        var removed = 0;
        foreach (var dir in EnumerateSidFolders())
        {
            var sid = dir.Name;

            if (known.Contains(sid))
                continue;

            if (IsHiveLoaded(sid))
            {
                _log.Info($"Recycle bin {sid} skipped — hive loaded, profile in use");
                continue;
            }

            if (Purge(dir, ResolveAccountName(sid), sid, orphaned: true))
                removed++;
        }

        if (removed > 0)
            _log.Info($"Removed {removed} orphaned recycle bin(s)");

        return removed;
    }

    /// <summary>
    /// Every "$Recycle.Bin\&lt;SID&gt;" folder on every fixed volume. Recycle bins are
    /// per-volume, so a profile's deleted files can sit on a secondary data disk
    /// long after the system volume's copy is gone.
    /// </summary>
    private IEnumerable<DirectoryInfo> EnumerateSidFolders()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                continue;

            var root = Path.Combine(drive.RootDirectory.FullName, RecycleBinFolder);

            DirectoryInfo[] children;
            try
            {
                var info = new DirectoryInfo(root);
                if (!info.Exists) continue;
                children = info.GetDirectories();
            }
            catch (Exception ex)
            {
                _log.Warning($"Could not enumerate {root}: {ex.Message}");
                continue;
            }

            foreach (var child in children)
            {
                // Only SID-named folders. Anything else under $Recycle.Bin is not
                // ours to remove.
                if (child.Name.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
                    yield return child;
            }
        }
    }

    private bool Purge(DirectoryInfo dir, string owner, string sid, bool orphaned)
    {
        var action = orphaned ? "ORPHANED_RECYCLE_BIN" : "RECYCLE_BIN";
        var bytes = MeasureBytes(dir);

        if (_simulate)
        {
            _log.Audit($"{action}_REMOVE_SIMULATED", $"owner={owner} sid={sid} path={dir.FullName} bytes={bytes}");
            return true;
        }

        try
        {
            DeleteContents(dir);

            // The folder itself stays: Windows owns it, recreates it on demand, and
            // removing it can leave the volume without a working recycle bin until
            // the next boot. Only the contents are ours to clear.
            _log.Audit($"{action}_REMOVED", $"owner={owner} sid={sid} path={dir.FullName} bytes={bytes}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed to clear recycle bin {dir.FullName}: {ex.Message}");
            _log.Audit($"{action}_REMOVE_FAILED", $"owner={owner} sid={sid} path={dir.FullName} reason={ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Delete everything inside a recycle bin folder, leaving the folder and the
    /// desktop.ini that marks it as a recycle bin. Individual failures are logged and
    /// stepped over so one locked item cannot abort the rest.
    /// </summary>
    private void DeleteContents(DirectoryInfo dir)
    {
        foreach (var file in dir.EnumerateFiles())
        {
            if (file.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                if ((file.Attributes & FileAttributes.ReadOnly) != 0)
                    file.Attributes &= ~FileAttributes.ReadOnly;
                file.Delete();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                _log.Warning($"Could not delete {file.FullName}: {ex.Message}");
            }
        }

        foreach (var sub in dir.EnumerateDirectories())
        {
            try
            {
                DeleteTree(sub);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                _log.Warning($"Could not delete {sub.FullName}: {ex.Message}");
            }
        }
    }

    private static void DeleteTree(DirectoryInfo dir)
    {
        if ((dir.Attributes & FileAttributes.ReadOnly) != 0)
            dir.Attributes &= ~FileAttributes.ReadOnly;

        // Recycled directory trees can contain junctions the user had in their
        // profile; unlink without descending so the target is left alone.
        if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            dir.Delete();
            return;
        }

        foreach (var file in dir.EnumerateFiles())
        {
            try
            {
                if ((file.Attributes & FileAttributes.ReadOnly) != 0)
                    file.Attributes &= ~FileAttributes.ReadOnly;
                file.Delete();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        }

        foreach (var sub in dir.EnumerateDirectories())
        {
            try
            {
                DeleteTree(sub);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                try { sub.Delete(recursive: false); } catch { }
            }
        }

        dir.Delete(recursive: false);
    }

    private static long MeasureBytes(DirectoryInfo dir)
    {
        try
        {
            return dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f =>
            {
                try { return f.Length; } catch { return 0L; }
            });
        }
        catch
        {
            return 0;
        }
    }

    private HashSet<string> LoadProfileListSids()
    {
        var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var profileList = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (profileList == null)
                return sids;

            foreach (var name in profileList.GetSubKeyNames())
                sids.Add(name);
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed to read ProfileList: {ex.Message}");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return sids;
    }

    private static bool IsHiveLoaded(string sid)
    {
        try
        {
            using var key = Registry.Users.OpenSubKey(sid);
            return key != null;
        }
        catch
        {
            // If we can't tell, err on the side of "in use".
            return true;
        }
    }

    /// <summary>
    /// Best-effort name for the audit log. Most orphaned SIDs no longer resolve,
    /// which is the point — the bin is all that is left of them.
    /// </summary>
    private static string ResolveAccountName(string sid)
    {
        try
        {
            return ((NTAccount)new SecurityIdentifier(sid).Translate(typeof(NTAccount))).Value;
        }
        catch
        {
            return "unknown";
        }
    }
}
