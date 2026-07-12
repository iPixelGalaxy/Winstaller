using Winstaller.Configuration;
using Winstaller.Models;
using Winstaller.Utilities;

namespace Winstaller.Modules;

/// <summary>
/// Module for mapping network drives
/// </summary>
public class NetworkDrivesModule : ModuleBase
{
    public NetworkDrivesModule(WinstallerConfig config) : base(config) { }

    public override string Name => "Network Drives";
    public override string Description => "Maps network drives and sets their labels";
    public override bool IsEnabled => Config.NetworkDrives.Enabled;

    public override async Task<bool> ExecuteAsync()
    {
        if (!IsEnabled)
        {
            ConsoleHelper.WriteWarning("Network Drives module is disabled in configuration.");
            return false;
        }

        ConsoleHelper.WriteHeader("Network Drives Setup");

        if (Config.NetworkDrives.Drives.Count == 0)
        {
            ConsoleHelper.WriteWarning("No drives configured.");
            return true;
        }

        var success = true;

        foreach (var drive in Config.NetworkDrives.Drives)
        {
            Console.WriteLine($"\nProcessing drive {drive.DriveLetter}: -> {drive.NetworkPath}");

            // Delete existing mapping if configured
            if (drive.DeleteFirst)
            {
                Console.WriteLine($"  Removing existing {drive.DriveLetter}: mapping...");
                await RunCmdAsync($"net use {drive.DriveLetter}: /delete", 5000);
            }

            // Wait before reconnecting
            await Task.Delay(Math.Max(0, Config.NetworkDrives.TimeoutSeconds) * 1000);

            // Create new mapping
            Console.WriteLine($"  Mapping {drive.DriveLetter}: to {drive.NetworkPath}...");
            var persistentFlag = drive.Persistent ? "/persistent:yes" : "/persistent:no";
            var credentialArgs = BuildCredentialArgs(drive);
            var result = await RunCmdAsync($"net use {drive.DriveLetter}: {drive.NetworkPath} {credentialArgs} {persistentFlag}", 30000);

            if (result != 0)
            {
                ConsoleHelper.WriteError($"Failed to map drive {drive.DriveLetter}:");
                success = false;
                continue;
            }

            // Set drive label
            if (!string.IsNullOrEmpty(drive.Label))
            {
                Console.WriteLine($"  Setting label to '{drive.Label}'...");
                var labelCmd = $"(New-Object -ComObject Shell.Application).NameSpace('{drive.DriveLetter}:\\').Self.Name = '{drive.Label}'";
                await RunPowerShellAsync(labelCmd, 10000);
            }

            ConsoleHelper.WriteSuccess($"Drive {drive.DriveLetter}: mapped successfully");
        }

        return success;
    }

    protected override List<MenuOption> GetMenuOptions()
    {
        return
        [
            new MenuOption("Map All Drives", ExecuteAsync),
            new MenuOption("List Configured Drives", ListConfiguredDrives),
            new MenuOption("Show Current Mappings", ShowCurrentMappings),
            new MenuOption("Map Single Drive", MapSingleDriveAsync),
            new MenuOption("Disconnect All Configured Drives", DisconnectAllAsync)
        ];
    }

    private Task ListConfiguredDrives()
    {
        ConsoleHelper.WriteSubHeader("Configured Network Drives");

        if (Config.NetworkDrives.Drives.Count == 0)
        {
            Console.WriteLine("No drives configured.");
            return Task.CompletedTask;
        }

        foreach (var drive in Config.NetworkDrives.Drives)
        {
            Console.WriteLine($"  {drive.DriveLetter}: -> {drive.NetworkPath}");
            Console.WriteLine($"       Label: {drive.Label}");
            Console.WriteLine($"       Persistent: {drive.Persistent}");
            Console.WriteLine($"       Delete First: {drive.DeleteFirst}");
            Console.WriteLine();
        }

        return Task.CompletedTask;
    }

    private async Task ShowCurrentMappings()
    {
        ConsoleHelper.WriteSubHeader("Current Network Mappings");
        await RunCmdAsync("net use", 10000);
    }

    private async Task MapSingleDriveAsync()
    {
        if (Config.NetworkDrives.Drives.Count == 0)
        {
            Console.WriteLine("No drives configured.");
            return;
        }

        Console.WriteLine("\nSelect a drive to map:");
        for (int i = 0; i < Config.NetworkDrives.Drives.Count; i++)
        {
            var drive = Config.NetworkDrives.Drives[i];
            Console.WriteLine($"  [{i + 1}] {drive.DriveLetter}: -> {drive.NetworkPath} ({drive.Label})");
        }

        Console.Write("\nChoice: ");
        var input = Console.ReadLine()?.Trim();

        if (int.TryParse(input, out int choice) && choice >= 1 && choice <= Config.NetworkDrives.Drives.Count)
        {
            var drive = Config.NetworkDrives.Drives[choice - 1];
            await MapDriveAsync(drive);
        }
    }

    private async Task MapDriveAsync(NetworkDriveMapping drive)
    {
        Console.WriteLine($"\nMapping {drive.DriveLetter}: -> {drive.NetworkPath}...");

        if (drive.DeleteFirst)
        {
            await RunCmdAsync($"net use {drive.DriveLetter}: /delete", 5000);
            await Task.Delay(1000);
        }

        var persistentFlag = drive.Persistent ? "/persistent:yes" : "/persistent:no";
        var credentialArgs = BuildCredentialArgs(drive);
        var result = await RunCmdAsync($"net use {drive.DriveLetter}: {drive.NetworkPath} {credentialArgs} {persistentFlag}", 30000);

        if (result == 0 && !string.IsNullOrEmpty(drive.Label))
        {
            var labelCmd = $"(New-Object -ComObject Shell.Application).NameSpace('{drive.DriveLetter}:\\').Self.Name = '{drive.Label}'";
            await RunPowerShellAsync(labelCmd, 10000);
            ConsoleHelper.WriteSuccess($"Drive {drive.DriveLetter}: mapped with label '{drive.Label}'");
        }
        else if (result == 0)
        {
            ConsoleHelper.WriteSuccess($"Drive {drive.DriveLetter}: mapped");
        }
        else
        {
            ConsoleHelper.WriteError($"Failed to map drive {drive.DriveLetter}:");
        }
    }

    private async Task DisconnectAllAsync()
    {
        ConsoleHelper.WriteSubHeader("Disconnecting All Configured Drives");

        foreach (var drive in Config.NetworkDrives.Drives)
        {
            Console.WriteLine($"Disconnecting {drive.DriveLetter}:...");
            await RunCmdAsync($"net use {drive.DriveLetter}: /delete", 5000);
        }

        ConsoleHelper.WriteSuccess("All configured drives disconnected");
    }

    private static string BuildCredentialArgs(NetworkDriveMapping drive)
    {
        if (string.IsNullOrWhiteSpace(drive.Username))
            return string.Empty;

        var password = string.IsNullOrWhiteSpace(drive.Password) ? "*" : $"\"{drive.Password}\"";
        return $"{password} /user:\"{drive.Username}\"";
    }
}
