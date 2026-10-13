using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace ManageUsers.Services;

/// <summary>
/// Decisions behind the app's Prefs tab. Every setting policy does not set is editable in
/// the normal, unelevated app. Settings live in HKLM, so Save asks for administrator approval
/// at that moment: an unelevated app relaunches its own executable elevated and headless with
/// <see cref="ApplySettingsArgument"/>, which writes the pending values and exits.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PrefsElevation
{
    /// <summary>Command-line switch for the headless writer, followed by the payload file's path.</summary>
    public const string ApplySettingsArgument = "--apply-settings";

    /// <summary>Win32 ERROR_CANCELLED: the user dismissed the UAC prompt.</summary>
    public const int ErrorCancelled = 1223;

    /// <summary>A setting can be edited unless policy sets it. Elevation is asked for at save time.</summary>
    public static bool CanEdit(bool isPolicyManaged) => !isPolicyManaged;

    /// <summary>True when the exception is the user cancelling the UAC prompt, not a real failure.</summary>
    public static bool IsElevationCancelled(Exception? ex)
        => ex is Win32Exception { NativeErrorCode: ErrorCancelled };

    /// <summary>
    /// Whether this process holds an elevated administrator token. With UAC on, an admin's
    /// filtered token is not in the Administrators role, so this is false until elevated.
    /// </summary>
    public static bool IsProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The payload path when the command line asks for the headless writer, otherwise null.</summary>
    public static string? ApplySettingsPath(IReadOnlyList<string>? args)
    {
        if (args == null) return null;
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i]?.Trim(), ApplySettingsArgument, StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(args[i + 1]) ? null : args[i + 1];
        }
        return null;
    }

    /// <summary>Start info that runs <paramref name="exePath"/> elevated and headless to write <paramref name="payloadPath"/>.</summary>
    public static ProcessStartInfo BuildApplySettings(string exePath, string payloadPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadPath);
        return new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"{ApplySettingsArgument} \"{payloadPath}\"",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
        };
    }
}
