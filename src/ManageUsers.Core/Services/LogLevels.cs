namespace ManageUsers.Services;

public enum LogLineLevel { Info, Debug, Warning, Error, Audit, Header }

/// <summary>
/// Reads the level of a line LogService wrote: <c>[yyyy-MM-dd HH:mm:ss] LEVEL message</c>,
/// with an unbracketed, space-padded level token. Deletion decisions and outcomes
/// (audit entries, "ACTION | detail") get their own colour.
/// </summary>
public static class LogLevels
{
    private static readonly string[] AuditActions =
    [
        "DELETE_DECISION", "USER_DELETED", "USER_DELETE_SIMULATED", "USER_DELETE_DEFERRED",
        "ORPHAN_USER_REMOVED", "ORPHAN_USER_REMOVE_SIMULATED", "STALE_PROFILE_REMOVED",
        "STALE_PROFILE_REMOVE_SIMULATED", "DELETE_SKIPPED", "RUN_START", "RUN_SUMMARY"
    ];

    public static LogLineLevel Of(string line)
    {
        if (HasLevel(line, "ERROR")) return LogLineLevel.Error;
        if (HasLevel(line, "WARN")) return LogLineLevel.Warning;
        if (HasLevel(line, "DEBUG")) return LogLineLevel.Debug;
        if (line.Contains("_FAILED |", StringComparison.Ordinal)) return LogLineLevel.Error;
        if (AuditActions.Any(a => line.Contains(a + " |", StringComparison.Ordinal))) return LogLineLevel.Audit;
        if (line.Contains("] INFO  ====", StringComparison.Ordinal) || line.StartsWith("===", StringComparison.Ordinal))
            return LogLineLevel.Header;
        return LogLineLevel.Info;
    }

    private static bool HasLevel(string line, string level)
    {
        var close = line.IndexOf("] ", StringComparison.Ordinal);
        if (close < 0) return false;
        var rest = line.AsSpan(close + 2).TrimStart();
        return rest.StartsWith(level, StringComparison.Ordinal)
            && (rest.Length == level.Length || rest[level.Length] == ' ');
    }
}
