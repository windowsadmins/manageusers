namespace ManageUsers.Models;

/// <summary>
/// Application-wide configuration paths and constants.
/// </summary>
public static class AppConstants
{
    public static readonly string ManagementRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Management");

    public static readonly string SessionsYamlPath = Path.Combine(ManagementRoot, "Users", "Sessions.yaml");
    public static readonly string InventoryYamlPath = Path.Combine(ManagementRoot, "Inventory.yaml");
    public static readonly string LogDir = Path.Combine(ManagementRoot, "Logs");
    public static readonly string LogFile = Path.Combine(LogDir, "ManageUsers.log");

    public const long MaxLogSizeBytes = 10 * 1024 * 1024; // 10 MB

    // Duration constants (days)
    public const int TwoDays = 2;
    public const int OneWeek = 7;
    public const int FourWeeks = 28;
    public const int ThirtyDays = 30;
    public const int SixWeeks = 42;
    public const int ThirteenWeeks = 91;

    /// <summary>
    /// Users that are never deleted regardless of configuration.
    /// </summary>
    public static readonly HashSet<string> AlwaysExcludedUsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Administrator",
        "DefaultAccount",
        "Guest",
        "WDAGUtilityAccount",
        "defaultuser0",
        "admin",
        "student",
        "doc",
        "cts",
        "fvim",
        "fmsa",
        "nmsatech",
        "winadmins"
    };
}
