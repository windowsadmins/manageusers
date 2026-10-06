using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ManageUsers.Models;

namespace ManageUsers.Services;

/// <summary>How the Managed Users Cleanup app finds and starts manageusers.exe.</summary>
public static class CliCommand
{
    /// <summary>Arguments for a simulation, which also writes the plan the app reads back.</summary>
    public static List<string> Simulate() => ["--simulate"];

    /// <summary>
    /// Arguments for a live run limited to <paramref name="confirmed"/>: the run deletes
    /// nothing a person did not see in the confirmation.
    /// </summary>
    public static List<string> LiveConfirmed(IEnumerable<string> confirmed)
    {
        var names = confirmed.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (names.Count == 0)
            throw new ArgumentException("A live run needs at least one confirmed name.", nameof(confirmed));
        var args = new List<string> { "--live" };
        foreach (var name in names)
        {
            args.Add("--only");
            args.Add(name);
        }
        return args;
    }

    /// <summary>
    /// One command line from <paramref name="args"/>, quoted so the C runtime splits it back
    /// into the same arguments (backslashes doubled only before a quote).
    /// </summary>
    public static string Join(IEnumerable<string> args) => string.Join(" ", args.Select(Quote));

    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0)
            return arg;

        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(c);
            }
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Where manageusers.exe is: the install folder, where the app sits beside it, or the
    /// app's own folder.
    /// </summary>
    public static string? Find(string appDirectory)
    {
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var candidates = new[]
        {
            Path.Combine(AppConstants.InstallDir, AppConstants.CliExecutableName),
            Path.Combine(appDirectory, AppConstants.CliExecutableName),
            // Dev layout: build.ps1 publishes the CLI to release\<arch>\ and the app to release\<arch>\app\.
            Path.Combine(appDirectory, "..", AppConstants.CliExecutableName),
            Path.Combine(appDirectory, "..", "..", "..", "release", arch, AppConstants.CliExecutableName),
        };
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Start info for the CLI. An elevated app starts it directly; otherwise UAC is asked,
    /// since a run needs an administrator to read accounts and write its protected logs.
    /// </summary>
    public static ProcessStartInfo StartInfo(string cliPath, IEnumerable<string> args, bool alreadyElevated) => alreadyElevated
        ? new ProcessStartInfo(cliPath, Join(args)) { UseShellExecute = false, CreateNoWindow = true }
        : new ProcessStartInfo(cliPath, Join(args))
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
}
