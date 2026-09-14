using System.Security.Principal;
using Microsoft.Win32;

namespace ManageUsers.Services;

/// <summary>
/// Removes per-user scheduled tasks left behind by a deleted profile.
///
/// Deleting a profile does not delete the scheduled tasks created for that user.
/// OneDrive registers two per account — "OneDrive Reporting Task-&lt;SID&gt;" and
/// "OneDrive Startup Task-&lt;SID&gt;" — in the root of the task folder, outside the
/// profile, and nothing ever reclaims them. Same shape as the recycle bin
/// leftovers in <see cref="RecycleBinService"/>, with a worse ending.
///
/// They accumulate at roughly 1.6 per profile and the Task Scheduler degrades as
/// the store grows. Measured on a Digital Fabrication workstation carrying 288
/// profiles: 806 task files, at which point schtasks.exe stopped returning at all.
/// That wedges any caller without a timeout — a Cimian session hung on it for 286
/// minutes, so the machine installed nothing for most of a day — and it also blocks
/// shutdown, so the host cannot be rebooted remotely to clear it. A machine in that
/// state reports no errors; it simply stops changing.
/// </summary>
public sealed class PerUserTaskService
{
    private const string TaskFolder = @"C:\Windows\System32\Tasks";

    private const string TaskCache =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache";

    // Where a task GUID is filed alongside TaskCache\Tasks. A task appears in
    // exactly one of these, decided by its trigger, and which one is not derivable
    // from the task — so removal tries each.
    private static readonly string[] ScheduleBuckets =
        { "Plain", "Logon", "Boot", "Maintenance", "Critical" };

    // The account SID forms a per-user task name can end in. S-1-5-21 is a local or
    // on-premises domain account; S-1-12-1 is an Entra account, which is what every
    // profile on an Entra-joined machine actually is.
    private static readonly string[] SidPrefixes = { "S-1-5-21-", "S-1-12-1-" };

    private readonly LogService _log;
    private readonly bool _simulate;

    public PerUserTaskService(LogService log, bool simulate)
    {
        _log = log;
        _simulate = simulate;
    }

    /// <summary>
    /// Delete one SID's per-user tasks. Called as part of removing that SID's
    /// profile; safe to call with a null/blank SID (no-op).
    /// </summary>
    public void RemoveForSid(string? sid, string owner)
    {
        if (string.IsNullOrWhiteSpace(sid))
        {
            // The task names carry the SID and nothing else identifying, so without
            // one there is nothing safe to match on.
            _log.Warning($"No SID for {owner}; per-user scheduled tasks could not be located and may remain");
            return;
        }

        foreach (var name in EnumerateTaskNamesForSid(sid))
            Delete(name, owner, sid, orphaned: false);
    }

    /// <summary>
    /// Delete per-user tasks whose SID has no profile on this machine.
    ///
    /// These accumulate from every profile removed before this cleanup existed. A
    /// SID with a ProfileList entry or a loaded hive is left alone — those tasks
    /// still belong to someone.
    /// </summary>
    /// <returns>Number of orphaned tasks removed.</returns>
    public int SweepOrphaned()
    {
        if (!_simulate && !CanWriteTaskCache())
        {
            // One line instead of several hundred. Without these rights the sweep
            // cannot remove anything, and the previous behaviour was to carry on
            // and delete the XML files regardless.
            _log.Warning(
                "TaskCache is not writable by this process; skipping the scheduled task sweep. " +
                "It requires SYSTEM — an elevated administrator is refused.");
            return 0;
        }

        var known = LoadProfileListSids();
        if (known.Count == 0)
        {
            // An empty ProfileList means the read failed, not that every task is
            // orphaned. Treating it as authoritative would delete all of them.
            _log.Warning("ProfileList could not be read; skipping orphaned scheduled task sweep");
            return 0;
        }

        var removed = 0;
        foreach (var (name, sid) in EnumerateSidTasks())
        {
            if (known.Contains(sid))
                continue;

            if (IsHiveLoaded(sid))
            {
                _log.Info($"Scheduled task {name} skipped — hive loaded, profile in use");
                continue;
            }

            if (Delete(name, ResolveAccountName(sid), sid, orphaned: true))
                removed++;
        }

        removed += SweepStrandedCacheEntries();

        if (removed > 0)
            _log.Info($"Removed {removed} orphaned per-user scheduled task(s)");

        return removed;
    }

    /// <summary>
    /// Remove TaskCache entries whose task file no longer exists.
    /// </summary>
    /// <remarks>
    /// Repairs damage this cleanup used to cause itself. When the registry delete
    /// failed silently and the XML file was removed anyway, the task ended up
    /// registered but with nothing on disk — invisible to a sweep that enumerates
    /// files, and still counted by the scheduler. Nothing would ever have reclaimed
    /// those, so the sweep has to come at them from the registry side as well.
    ///
    /// Every entry records the task's path under the task folder, and a path naming
    /// a file that is not there is by definition not a live task. Windows' own tasks
    /// all have their files, so this does not reach them.
    /// </remarks>
    private int SweepStrandedCacheEntries()
    {
        var removed = 0;

        string[] ids;
        try
        {
            using var tasks = Registry.LocalMachine.OpenSubKey($@"{TaskCache}\Tasks");
            if (tasks == null)
                return 0;

            ids = tasks.GetSubKeyNames();
        }
        catch (Exception ex)
        {
            _log.Warning($"Could not enumerate TaskCache: {ex.Message}");
            return 0;
        }

        foreach (var id in ids)
        {
            string? relative;
            try
            {
                using var entry = Registry.LocalMachine.OpenSubKey($@"{TaskCache}\Tasks\{id}");
                relative = entry?.GetValue("Path") as string;
            }
            catch
            {
                continue;
            }

            // No Path is not evidence of anything; leave it alone.
            if (string.IsNullOrWhiteSpace(relative))
                continue;

            var file = Path.Combine(TaskFolder, relative.TrimStart('\\'));
            if (File.Exists(file))
                continue;

            if (_simulate)
            {
                _log.Info($"SIMULATE: would remove stranded TaskCache entry for '{relative}'");
                removed++;
                continue;
            }

            if (!DeleteKey($@"{TaskCache}\Tasks\{id}"))
            {
                _log.Warning($"Stranded TaskCache entry for '{relative}' could not be removed");
                continue;
            }

            foreach (var bucket in ScheduleBuckets)
                DeleteKey($@"{TaskCache}\{bucket}\{id}");

            DeleteKey($@"{TaskCache}\Tree{relative}");

            _log.Info($"Removed stranded TaskCache entry for '{relative}'");
            _log.Audit("TASK_CACHE_STRAND_REMOVE", $"path={relative} id={id}");
            removed++;
        }

        return removed;
    }

    /// <summary>
    /// Task names in the root of the task folder that end in a SID.
    /// </summary>
    /// <remarks>
    /// Read from the task XML on disk rather than through the scheduler API. The
    /// API is exactly what stops answering on an affected machine, so enumerating
    /// through it would fail precisely where this is most needed. The files are
    /// authoritative for what exists, and deletion removes the same two things the
    /// scheduler itself stores: the XML file and its TaskCache registry entries.
    ///
    /// Only the root folder is considered. Per-user tasks are registered there, and
    /// the subfolders under \Microsoft\ belong to Windows.
    /// </remarks>
    private IEnumerable<(string Name, string Sid)> EnumerateSidTasks()
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(TaskFolder);
        }
        catch (Exception ex)
        {
            _log.Warning($"Could not enumerate scheduled tasks: {ex.Message}");
            yield break;
        }

        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            var sid = ExtractSid(name);
            if (sid != null)
                yield return (name, sid);
        }
    }

    private IEnumerable<string> EnumerateTaskNamesForSid(string sid) =>
        EnumerateSidTasks()
            .Where(t => string.Equals(t.Sid, sid, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Name);

    /// <summary>
    /// The trailing account SID of a task name, or null when there is not one.
    /// </summary>
    /// <remarks>
    /// Two SID forms count as a user here, and missing the second one is what let
    /// this leak keep running after the sweep shipped:
    ///
    ///   S-1-5-21-...   a local or on-premises domain account
    ///   S-1-12-1-...   a Microsoft Entra account
    ///
    /// On an Entra-joined machine every profile is the second form, so a sweep that
    /// recognised only the first enumerated the task folder, matched almost nothing
    /// and reported a clean result while the store kept growing. Measured on two
    /// workstations: of 136 and 148 per-user task files, 5 were S-1-5-21 and the
    /// rest were Entra — 134 and 146 of them orphaned, and all invisible.
    ///
    /// Everything else is deliberately excluded. The well-known SIDs — S-1-5-18 for
    /// SYSTEM and its neighbours — are not user profiles, and a task named after one
    /// is not a leftover.
    /// </remarks>
    internal static string? ExtractSid(string taskName)
    {
        foreach (var prefix in SidPrefixes)
        {
            var idx = taskName.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                continue;

            var sid = taskName[idx..];

            // Validate the part AFTER the well-known prefix: digits and hyphens only,
            // so a name that merely contains a SID followed by other text is not
            // matched. The prefix itself is not re-checked - it starts with a letter,
            // and folding it into the test rejects every real task name.
            var remainder = sid[prefix.Length..];

            if (remainder.Length == 0)
                continue;

            if (remainder.All(c => char.IsDigit(c) || c == '-'))
                return sid;
        }

        return null;
    }

    /// <summary>
    /// Remove one task by deleting what the scheduler itself stores for it.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT shell out to schtasks.exe, and that is the whole point
    /// of this method.
    ///
    /// schtasks is a client of the Task Scheduler service over RPC, and that service
    /// is what degrades on an affected machine. Deleting several hundred tasks meant
    /// several hundred RPC round trips into a service already struggling, so the
    /// cleanup became the largest single cause of the condition it exists to prevent:
    /// a machine mid-sweep stops answering, the sweep stalls, and a clean shutdown is
    /// then impossible because a wedged scheduler blocks it. Measured on a workstation
    /// carrying 439 of these tasks.
    ///
    /// A task is two things on disk: the XML file under System32\Tasks, and a set of
    /// TaskCache registry entries keyed by a GUID. Removing both reaches the same end
    /// state schtasks would, without waking the service once.
    ///
    /// The running service keeps its own in-memory view, so a deleted task can linger
    /// there until the service restarts, and a machine already degraded stays degraded
    /// until then. It does not need a reboot: Schedule runs alone in its own svchost
    /// and reports CanStop, so restarting the service is enough to pick up the
    /// shrunken store.
    /// </remarks>
    private bool Delete(string taskName, string owner, string sid, bool orphaned)
    {
        var label = orphaned ? "orphaned " : "";

        if (_simulate)
        {
            _log.Info($"SIMULATE: would delete {label}scheduled task '{taskName}' ({owner})");
            return true;
        }

        try
        {
            var id = ReadTaskId(taskName);

            if (id != null)
            {
                if (!DeleteKey($@"{TaskCache}\Tasks\{id}"))
                    return FailedRegistryDelete(taskName, $@"Tasks\{id}");

                foreach (var bucket in ScheduleBuckets)
                {
                    if (!DeleteKey($@"{TaskCache}\{bucket}\{id}"))
                        return FailedRegistryDelete(taskName, $@"{bucket}\{id}");
                }
            }

            if (!DeleteKey($@"{TaskCache}\Tree\{taskName}"))
                return FailedRegistryDelete(taskName, $@"Tree\{taskName}");

            // Last, so a failure above leaves the task still discoverable by the next
            // run rather than leaving registry state pointing at a file that is gone.
            var path = Path.Combine(TaskFolder, taskName);
            if (File.Exists(path))
                File.Delete(path);

            _log.Info($"Deleted {label}scheduled task '{taskName}' ({owner})");
            _log.Audit("TASK_DELETE", $"task={taskName} sid={sid} orphaned={orphaned}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed to delete scheduled task '{taskName}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Delete a registry key and confirm it is actually gone.
    /// </summary>
    /// <remarks>
    /// The confirmation is the entire point. <c>DeleteSubKeyTree</c> called with
    /// <c>throwOnMissingSubKey: false</c> — which is what this code wants, because a
    /// half-removed task legitimately has keys missing — cannot distinguish "not
    /// there" from "not allowed to open it", and returns quietly for both. Under an
    /// elevated administrator, which is not enough for TaskCache, every one of these
    /// calls returns success and deletes nothing.
    ///
    /// That combination produced the worst possible outcome on two workstations: the
    /// registry delete silently did nothing, the XML file below it was removed
    /// anyway, and the task became invisible to this sweep while still counting
    /// toward the store the scheduler enumerates. 134 and 146 tasks each, gone from
    /// disk and permanently registered. Verifying here is what keeps the file
    /// deletion honest.
    /// </remarks>
    private static bool DeleteKey(string path)
    {
        try
        {
            Registry.LocalMachine.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }

        using var check = Registry.LocalMachine.OpenSubKey(path);
        return check == null;
    }

    private bool FailedRegistryDelete(string taskName, string key)
    {
        _log.Warning(
            $@"Scheduled task '{taskName}' left in place: TaskCache\{key} could not be removed. " +
            "These keys are writable only by SYSTEM; an elevated administrator is refused.");
        return false;
    }

    /// <summary>
    /// Whether this process can write TaskCache at all.
    /// </summary>
    /// <remarks>
    /// Checked once up front so a run without the rights says so in one line, rather
    /// than reporting a per-task failure several hundred times over.
    /// </remarks>
    private static bool CanWriteTaskCache()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{TaskCache}\Tasks", writable: true);
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The GUID the scheduler files a task under, or null when there is no Tree entry.
    /// </summary>
    /// <remarks>
    /// A missing Tree entry is not an error: it means the task is already half gone,
    /// which is one of the states this sweep exists to tidy. The file is still removed.
    /// </remarks>
    private string? ReadTaskId(string taskName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{TaskCache}\Tree\{taskName}");
            var id = key?.GetValue("Id") as string;
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }
        catch (Exception ex)
        {
            _log.Warning($"Could not read the TaskCache entry for '{taskName}': {ex.Message}");
            return null;
        }
    }

    private static string ResolveAccountName(string sid)
    {
        try
        {
            return new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
        }
        catch
        {
            return sid;
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
}
