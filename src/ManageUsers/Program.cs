using System.CommandLine;
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

        var inventoryOption = new Option<string?>(
            "--inventory",
            "Path to a custom inventory YAML file (default: C:\\ProgramData\\Management\\Inventory.yaml)");

        var onlyOption = new Option<string[]>(
            "--only",
            "Remove nothing outside these account names or profile folder names (repeat for each). " +
            "Used by Managed Users Cleanup to run exactly the list a person confirmed.")
        {
            AllowMultipleArgumentsPerToken = false,
            Arity = ArgumentArity.ZeroOrMore
        };

        rootCommand.AddOption(simulateOption);
        rootCommand.AddOption(forceOption);
        rootCommand.AddOption(liveOption);
        rootCommand.AddOption(inventoryOption);
        rootCommand.AddOption(onlyOption);

        rootCommand.SetHandler((bool simulate, bool force, bool live, string? inventory, string[] only) =>
        {
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
                var engine = new ManageUsersEngine(simulate, force, inventory, only);
                var exitCode = engine.Run();
                Environment.Exit(exitCode);
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }, simulateOption, forceOption, liveOption, inventoryOption, onlyOption);

        return await rootCommand.InvokeAsync(args);
    }
}
