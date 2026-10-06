namespace ManageUsers.Models;

/// <summary>
/// Application-wide configuration paths and constants.
/// </summary>
public static class AppConstants
{
    public static readonly string ManagementRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Management");

    public static readonly string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "sbin");

    public static readonly string ManageUsersConfigDir = Path.Combine(ManagementRoot, "ManageUsers");
    public static readonly string ConfigYamlPath = Path.Combine(ManageUsersConfigDir, "Config.yaml");
    public static readonly string SessionsYamlPath = Path.Combine(ManageUsersConfigDir, "Sessions.yaml");
    public static readonly string DefaultInventoryYamlPath = Path.Combine(ManagementRoot, "Inventory.yaml");
    public static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ManagedUsers", "logs");
    /// <summary>
    /// The operational log for a run starting at <paramref name="timestamp"/>. The day
    /// directory is this tool's session: it is invoked far too often to justify one per
    /// run, so a day's runs share a directory, with the structured event stream beside
    /// them. This is the layout every managed tool shares.
    /// </summary>
    public static string LogFileFor(DateTime timestamp) =>
        Path.Combine(LogDir, timestamp.ToString(DayFormat), "manageusers.log");

    /// <summary>The structured event stream beside a day's operational log.</summary>
    public static string EventsFileFor(DateTime timestamp) =>
        Path.Combine(LogDir, timestamp.ToString(DayFormat), "events.jsonl");

    public const string DayFormat = "yyyy-MM-dd";

    /// <summary>Day directories older than this are removed when a run starts.</summary>
    public const int LogRetentionDays = 30;

    /// <summary>
    /// The audit log stays at the root, outside the day directories and outside
    /// retention: "which accounts were deleted, when, and why" must stay answerable
    /// long after a day's operational log has aged out, so it is discarded only when
    /// the size cap pushes the oldest rotated generation out.
    /// </summary>
    public static readonly string AuditLogFile = Path.Combine(LogDir, "manageusers.audit.log");

    /// <summary>
    /// Log location used by earlier releases. Existing files are moved to
    /// <see cref="LogDir"/> once on first run so history is kept.
    /// </summary>
    public static readonly string LegacyLogDir = Path.Combine(ManagementRoot, "Logs");
    public static readonly string LegacyLogFile = Path.Combine(LegacyLogDir, "ManageUsers.log");
    public static readonly string LegacyAuditLogFile = Path.Combine(LegacyLogDir, "ManageUsers.audit.log");

    public const long MaxLogSizeBytes = 10 * 1024 * 1024; // 10 MB
    public const int MaxRotatedLogs = 5;

    /// <summary>
    /// Built-in Windows accounts that are never deleted.
    /// Additional exclusions can be configured in Sessions.yaml.
    /// </summary>
    public static readonly HashSet<string> AlwaysExcludedUsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Administrator",
        "DefaultAccount",
        "Guest",
        "WDAGUtilityAccount",
        "defaultuser0",
        // Management service account: must survive even when Config.yaml is
        // missing/stale, delete_admins is misconfigured, or the account has
        // been dropped from the Administrators group.
        "winadmins"
    };
}
