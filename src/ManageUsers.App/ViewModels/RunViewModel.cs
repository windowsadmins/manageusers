using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using ManageUsers.Models;
using ManageUsers.Services;
using Microsoft.UI.Dispatching;

namespace ManageUsers.App.ViewModels;

/// <summary>
/// ViewModel for the Run tab. Starts manageusers.exe (through UAC unless the app is already
/// elevated) and streams its log as it runs. A simulation also reads back the plan the CLI
/// writes, which is the decision list. A live cleanup runs a fresh simulation first, asks
/// the caller to confirm exactly that list, and then runs live limited to it with --only.
/// </summary>
public partial class RunViewModel : ObservableObject
{
    private readonly DispatcherQueue _dispatcher;

    public RunViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    // ── Observable State ─────────────────────────────────────────

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _phase = "";
    [ObservableProperty] private int? _lastExitCode;
    [ObservableProperty] private string _resultTitle = "";
    [ObservableProperty] private string _resultMessage = "";
    [ObservableProperty] private ResultKind _result = ResultKind.None;
    [ObservableProperty] private string _planCaption = "Run a simulation to see what would be removed.";

    public enum ResultKind { None, Success, Info, Warning, Error }

    public ObservableCollection<OutputLine> OutputLines { get; } = [];
    public ObservableCollection<PlanItem> PlanItems { get; } = [];

    public record OutputLine(string Text, LogLineLevel Level);

    public bool IsElevated { get; } = PrefsElevation.IsProcessElevated();

    // ── Simulate ─────────────────────────────────────────────────

    /// <summary>Runs a simulation and shows its decision list. Returns the plan, or null when it failed.</summary>
    public async Task<SimulationPlan?> SimulateAsync()
    {
        if (IsRunning) return null;
        Begin("Simulating");
        try
        {
            return await RunSimulationAsync();
        }
        finally
        {
            IsRunning = false;
        }
    }

    private async Task<SimulationPlan?> RunSimulationAsync()
    {
        PlanItems.Clear();
        PlanCaption = "Simulating...";
        var started = DateTimeOffset.Now;

        var exit = await RunCliAsync(CliCommand.Simulate());
        if (exit is null) return null;

        var plan = SimulationPlan.Load();
        if (exit != 0 || plan is null || plan.Generated < started.AddSeconds(-5))
        {
            PlanCaption = "The simulation did not produce a decision list.";
            Finish(ResultKind.Error, $"Simulation failed{(exit != 0 ? $" (exit code {exit})" : "")}",
                "Check the output and the Logs tab for details.");
            return null;
        }

        foreach (var item in plan.Items)
            PlanItems.Add(item);
        PlanCaption = plan.Items.Count == 0
            ? "Nothing would be removed."
            : $"{plan.Items.Count} item(s) would be removed:";
        Finish(ResultKind.Success, "Simulation complete",
            plan.Items.Count == 0 ? "No accounts or profiles are due for removal." : $"{plan.Items.Count} account(s) or profile(s) would be removed.");
        return plan;
    }

    // ── Live cleanup ─────────────────────────────────────────────

    /// <summary>
    /// Simulates, asks <paramref name="confirm"/> about exactly what would be removed, and only
    /// then runs live, limited to that list.
    /// </summary>
    public async Task LiveCleanupAsync(Func<IReadOnlyList<PlanItem>, Task<bool>> confirm)
    {
        if (IsRunning) return;
        Begin("Simulating before cleanup");
        try
        {
            var plan = await RunSimulationAsync();
            if (plan is null) return;
            if (plan.Items.Count == 0)
            {
                Finish(ResultKind.Info, "Nothing to remove", "The simulation found no accounts or profiles due for removal, so no live run was started.");
                return;
            }

            IsRunning = false;
            var confirmed = await confirm(plan.Items);
            if (!confirmed)
            {
                Finish(ResultKind.Info, "Cleanup cancelled", "Nothing was removed.");
                return;
            }

            IsRunning = true;
            Phase = "Removing confirmed accounts";
            AppendLine($"[i] Live cleanup of {plan.Items.Count} confirmed item(s): {string.Join(", ", plan.Items.Select(i => i.Name))}", LogLineLevel.Header);
            var exit = await RunCliAsync(CliCommand.LiveConfirmed(plan.Items.Select(i => i.Name)));
            if (exit is null) return;
            if (exit == 0)
                Finish(ResultKind.Success, "Cleanup complete", "See the output, or the audit log on the Logs tab, for what was removed.");
            else
                Finish(ResultKind.Error, $"Cleanup failed (exit code {exit})", "Check the output and the Logs tab for details.");
        }
        finally
        {
            IsRunning = false;
        }
    }

    // ── Clear ────────────────────────────────────────────────────

    public void Clear()
    {
        OutputLines.Clear();
        LastExitCode = null;
        Result = ResultKind.None;
    }

    // ── CLI process and log tail ─────────────────────────────────

    private void Begin(string phase)
    {
        IsRunning = true;
        Phase = phase;
        LastExitCode = null;
        Result = ResultKind.None;
        OutputLines.Clear();
    }

    private void Finish(ResultKind kind, string title, string message)
    {
        ResultTitle = title;
        ResultMessage = message;
        Result = kind;
    }

    /// <summary>Runs the CLI and streams its log. Returns the exit code, or null when it never started.</summary>
    private async Task<int?> RunCliAsync(List<string> args)
    {
        var cli = CliCommand.Find(AppContext.BaseDirectory);
        if (cli is null)
        {
            AppendLine("[ERROR] manageusers.exe not found. Reinstall ManageUsers.", LogLineLevel.Error);
            Finish(ResultKind.Error, "manageusers.exe not found", $"Looked in {AppConstants.InstallDir} and beside this app.");
            return null;
        }

        AppendLine($"[i] {Path.GetFileName(cli)} {CliCommand.Join(args)}", LogLineLevel.Debug);

        // The CLI appends to the day's log; stream what it adds from here on.
        var logFile = AppConstants.LogFileFor(DateTime.Now);
        var offset = LengthOf(logFile);
        using var cts = new CancellationTokenSource();

        try
        {
            using var process = Process.Start(CliCommand.StartInfo(cli, args, IsElevated));
            if (process is null)
            {
                AppendLine("[ERROR] manageusers.exe did not start.", LogLineLevel.Error);
                return null;
            }

            var tail = TailAsync(logFile, offset, cts.Token);
            await process.WaitForExitAsync();
            LastExitCode = process.ExitCode;

            // Let the last writes land before the tail stops.
            await Task.Delay(800);
            await cts.CancelAsync();
            try { await tail; } catch (OperationCanceledException) { }
            return process.ExitCode;
        }
        catch (Exception ex) when (PrefsElevation.IsElevationCancelled(ex))
        {
            AppendLine("[!] Administrator approval was cancelled.", LogLineLevel.Warning);
            Finish(ResultKind.Warning, "Not run", "manageusers needs administrator approval to read accounts and write its logs.");
            return null;
        }
        catch (Exception ex)
        {
            AppendLine($"[ERROR] {ex.Message}", LogLineLevel.Error);
            Finish(ResultKind.Error, "Could not run manageusers", ex.Message);
            return null;
        }
    }

    private async Task TailAsync(string logFile, long position, CancellationToken ct)
    {
        var pending = "";
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(logFile))
                {
                    using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (fs.Length < position) position = 0;
                    if (fs.Length > position)
                    {
                        fs.Position = position;
                        using var reader = new StreamReader(fs);
                        var text = pending + await reader.ReadToEndAsync(ct);
                        position = fs.Length;
                        var lines = text.Split('\n');
                        pending = lines[^1];
                        foreach (var line in lines[..^1])
                        {
                            var trimmed = line.TrimEnd('\r');
                            if (!string.IsNullOrWhiteSpace(trimmed))
                                AppendLine(trimmed, LogLevels.Of(trimmed));
                        }
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            await Task.Delay(300, ct);
        }
        if (!string.IsNullOrWhiteSpace(pending))
            AppendLine(pending.TrimEnd('\r'), LogLevels.Of(pending));
    }

    private static long LengthOf(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; } catch { return 0; }
    }

    private void AppendLine(string text, LogLineLevel level) =>
        _dispatcher.TryEnqueue(() => OutputLines.Add(new OutputLine(text, level)));
}
