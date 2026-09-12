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

        if (removed > 0)
            _log.Info($"Removed {removed} orphaned per-user scheduled task(s)");

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
    /// The trailing "S-1-5-21-..." of a task name, or null when there is not one.
    /// </summary>
    /// <remarks>
    /// Deliberately restricted to S-1-5-21, which is machine and domain accounts.
    /// The well-known SIDs — S-1-5-18 for SYSTEM and its neighbours — are not user
    /// profiles, and a task named after one is not a leftover.
    /// </remarks>
    internal static string? ExtractSid(string taskName)
    {
        var idx = taskName.IndexOf("S-1-5-21-", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;

        var sid = taskName[idx..];

        // Validate the part AFTER the well-known prefix: digits and hyphens only, so
        // a name that merely contains a SID followed by other text is not matched.
        // The prefix itself is not re-checked - it starts with a letter, and folding
        // it into the test rejects every real task name.
        const int prefixLength = 9; // "S-1-5-21-"
        var remainder = sid[prefixLength..];

        if (remainder.Length == 0)
            return null;

        return remainder.All(c => char.IsDigit(c) || c == '-') ? sid : null;
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
    /// there until the service restarts. That is cosmetic: it is gone from the store,
    /// it does not come back across a reboot, and it no longer counts toward the file
    /// count that drives the degradation.
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
                Registry.LocalMachine.DeleteSubKeyTree($@"{TaskCache}\Tasks\{id}", throwOnMissingSubKey: false);

                foreach (var bucket in ScheduleBuckets)
                    Registry.LocalMachine.DeleteSubKeyTree($@"{TaskCache}\{bucket}\{id}", throwOnMissingSubKey: false);
            }

            Registry.LocalMachine.DeleteSubKeyTree($@"{TaskCache}\Tree\{taskName}", throwOnMissingSubKey: false);

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
