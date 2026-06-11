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
    public static readonly string LogDir = Path.Combine(ManagementRoot, "Logs");
    public static readonly string LogFile = Path.Combine(LogDir, "ManageUsers.log");
    public static readonly string AuditLogFile = Path.Combine(LogDir, "ManageUsers.audit.log");

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
        "defaultuser0"
    };
}
