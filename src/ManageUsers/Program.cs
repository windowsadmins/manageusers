using System.CommandLine;
using System.Reflection;
using System.Text;
using ManageUsers.Services;

namespace ManageUsers;

public class Program
{
    private const string MutexName = @"Global\ManageUsers";

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var rootCommand = new RootCommand("Manage local user accounts on shared Windows devices");

        var simulateOption = new Option<bool>(
            ["--simulate", "-s"],
            "Dry-run mode — log what would be deleted without making changes");

        var forceOption = new Option<bool>(
            ["--force", "-f"],
            "Force mode — set deletion threshold to 0 days");

        var liveOption = new Option<bool>(
            "--live",
            "Explicit live mode (no-op, default is live unless --simulate)");

        var versionOption = new Option<bool>(
            ["--version", "-v"],
            "Print version and exit");

        var inventoryOption = new Option<string?>(
            "--inventory",
            "Path to a custom inventory YAML file (default: C:\\ProgramData\\Management\\Inventory.yaml)");

        rootCommand.AddOption(simulateOption);
        rootCommand.AddOption(forceOption);
        rootCommand.AddOption(liveOption);
        rootCommand.AddOption(versionOption);
        rootCommand.AddOption(inventoryOption);

        rootCommand.SetHandler((bool simulate, bool force, bool live, bool version, string? inventory) =>
        {
            if (version)
            {
                PrintVersion();
                return;
            }

            // Single-instance guard
            bool createdNew;
            using var mutex = new Mutex(true, MutexName, out createdNew);
            if (!createdNew)
            {
                Console.Error.WriteLine("Another instance of ManageUsers is already running.");
                Environment.Exit(2);
                return;
            }

            try
            {
                var engine = new ManageUsersEngine(simulate, force, inventory);
                var exitCode = engine.Run();
                Environment.Exit(exitCode);
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }, simulateOption, forceOption, liveOption, versionOption, inventoryOption);

        return await rootCommand.InvokeAsync(args);
    }

    private static void PrintVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version ?? new Version(1, 0, 0);
        Console.WriteLine($"manageusers v{version}");
    }
}
