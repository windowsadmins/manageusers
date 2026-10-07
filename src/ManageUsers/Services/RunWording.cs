namespace ManageUsers.Services;

/// <summary>
/// Counts for the run log, worded for what happened: a simulation removes nothing, so it
/// reports what it would remove.
/// </summary>
internal static class RunWording
{
    /// <summary>"Removed 3 orphaned recycle bin(s)" on a live run, "Would remove 3 …" in a simulation.</summary>
    public static string Removed(bool simulate, int count, string what) =>
        simulate ? $"Would remove {count} {what}" : $"Removed {count} {what}";

    /// <summary>The closing line of a run.</summary>
    public static string Summary(bool simulate, int count) =>
        simulate
            ? $"ManageUsers simulation complete — would remove {count} user(s)"
            : $"ManageUsers complete — {count} user(s) removed";
}
