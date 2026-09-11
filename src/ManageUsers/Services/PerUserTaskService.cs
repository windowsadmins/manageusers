using System.Diagnostics;
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

    // Bounded on purpose. This is cleaning up after a scheduler that wedges, so it
    // must never become another unbounded call into one — that is the bug it exists
    // to prevent, and it has already been paid for once here.
    private static readonly TimeSpan DeleteTimeout = TimeSpan.FromSeconds(30);

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
    /// authoritative for what exists; deletion still goes through schtasks so the
    /// registry side stays consistent.
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
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("/Delete");
            psi.ArgumentList.Add("/TN");
            psi.ArgumentList.Add(taskName);
            psi.ArgumentList.Add("/F");

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                _log.Warning($"Could not start schtasks to delete '{taskName}'");
                return false;
            }

            if (!proc.WaitForExit((int)DeleteTimeout.TotalMilliseconds))
            {
                // The scheduler is already wedged. Stop rather than join the queue.
                try { proc.Kill(entireProcessTree: true); } catch { }
                _log.Warning($"Task Scheduler did not answer within {DeleteTimeout.TotalSeconds:N0}s deleting '{taskName}'; leaving it for the next run");
                return false;
            }

            if (proc.ExitCode != 0)
            {
                _log.Warning($"schtasks exited {proc.ExitCode} deleting '{taskName}'");
                return false;
            }

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
