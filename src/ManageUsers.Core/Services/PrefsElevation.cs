using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace ManageUsers.Services;

/// <summary>
/// Decisions behind the app's Prefs tab. Every setting policy does not set is editable in
/// the normal, unelevated app. Settings live in HKLM, so Save asks for administrator approval
/// at that moment: an unelevated app relaunches its own executable elevated and headless with
/// <see cref="ApplySettingsArgument"/> and a pipe name, sends the pending values over that pipe,
/// and the headless copy writes them and exits. Nothing is written to disk on the way.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PrefsElevation
{
    /// <summary>Command-line switch for the headless writer, followed by the payload pipe's name.</summary>
    public const string ApplySettingsArgument = "--apply-settings";

    /// <summary>How long Save waits for the headless writer to connect, which covers the UAC prompt.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(60);

    private const string PipePrefix = "ManageUsers-settings-";
    private static readonly Regex PipeName = new($"^{PipePrefix}[0-9a-f]{{32}}$", RegexOptions.CultureInvariant);

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

    /// <summary>
    /// True when the command line asks for the headless writer. <paramref name="pipeName"/> is the
    /// payload pipe, or null when the name after the switch is missing or not one Save creates.
    /// </summary>
    public static bool IsApplySettings(IReadOnlyList<string>? args, out string? pipeName)
    {
        pipeName = null;
        if (args == null) return false;
        for (var i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i]?.Trim(), ApplySettingsArgument, StringComparison.OrdinalIgnoreCase))
                continue;
            if (i + 1 < args.Count && PipeName.IsMatch(args[i + 1] ?? ""))
                pipeName = args[i + 1];
            return true;
        }
        return false;
    }

    /// <summary>A new, unguessable payload pipe name.</summary>
    public static string NewPipeName() => PipePrefix + Guid.NewGuid().ToString("N");

    /// <summary>
    /// The payload pipe's server end. Only the current user, Administrators and SYSTEM can open
    /// it, and only one client can connect.
    /// </summary>
    public static NamedPipeServerStream CreatePayloadPipe(string pipeName)
    {
        var security = new PipeSecurity();
        using var identity = WindowsIdentity.GetCurrent();
        foreach (var sid in new[]
                 {
                     identity.User!,
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)
                 })
            security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
    }

    /// <summary>Sends <paramref name="json"/> to the connected writer and closes the pipe.</summary>
    public static async Task SendPayloadAsync(NamedPipeServerStream pipe, string json, CancellationToken cancel)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await pipe.WriteAsync(bytes, cancel);
        await pipe.FlushAsync(cancel);
        pipe.WaitForPipeDrain();
        pipe.Disconnect();
    }

    /// <summary>The headless writer's side: reads the whole payload from the pipe, or null when it cannot.</summary>
    public static string? ReceivePayload(string pipeName, TimeSpan timeout)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.In);
            pipe.Connect((int)timeout.TotalMilliseconds);
            using var reader = new StreamReader(pipe, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Start info that runs <paramref name="exePath"/> elevated and headless, reading the payload from <paramref name="pipeName"/>.</summary>
    public static ProcessStartInfo BuildApplySettings(string exePath, string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        if (!PipeName.IsMatch(pipeName))
            throw new ArgumentException("Not a payload pipe name.", nameof(pipeName));
        return new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"{ApplySettingsArgument} {pipeName}",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
        };
    }
}
