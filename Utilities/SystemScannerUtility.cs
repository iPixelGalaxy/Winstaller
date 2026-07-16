using Winstaller.Configuration;
using Winstaller.Models;
using Microsoft.Win32;
using System.Diagnostics;

namespace Winstaller.Utilities;

/// <summary>
/// Utility for scanning current system settings and adding them to configuration
/// </summary>
public class SystemScannerUtility
{
    private readonly WinstallerConfig _config;
    private readonly string _configPath;

    public SystemScannerUtility(WinstallerConfig config, string configPath)
    {
        _config = config;
        _configPath = configPath;
    }

    public async Task ShowMenuAsync()
    {
        while (true)
        {
            Console.Clear();
            ConsoleHelper.WriteHeader("System Scanner Utility");
            Console.WriteLine("Scan current system settings and add them to configuration\n");

            Console.WriteLine("  [1] Scan PATH Variables");
            Console.WriteLine("  [2] Scan Network Drives");
            Console.WriteLine("  [3] Scan Shell Folders");
            Console.WriteLine("  [4] Scan Installed Apps (winget)");
            Console.WriteLine("  [5] Scan All");
            Console.WriteLine("\n  [0] Back to Main Menu");

            Console.Write("\nSelect option: ");
            var input = Console.ReadLine()?.Trim();

            switch (input)
            {
                case "1":
                    await ScanPathVariablesAsync();
                    break;
                case "2":
                    await ScanNetworkDrivesAsync();
                    break;
                case "3":
                    await ScanShellFoldersAsync();
                    break;
                case "4":
                    await ScanInstalledWingetAppsAsync();
                    break;
                case "5":
                    await ScanAllAsync();
                    break;
                case "0":
                case "":
                    return;
            }

            if (input != "0" && !string.IsNullOrEmpty(input))
            {
                Console.WriteLine("\nPress any key to continue...");
                Console.ReadKey(true);
            }
        }
    }

    private async Task ScanAllAsync()
    {
        await ScanPathVariablesAsync();
        Console.WriteLine();
        await ScanNetworkDrivesAsync();
        Console.WriteLine();
        await ScanShellFoldersAsync();
        Console.WriteLine();
        await ScanInstalledWingetAppsAsync();
    }

    #region PATH Scanner

    private async Task ScanPathVariablesAsync()
    {
        ConsoleHelper.WriteSubHeader("Scanning PATH Variables");

        Console.WriteLine("\n  [1] Scan System PATH");
        Console.WriteLine("  [2] Scan User PATH");
        Console.WriteLine("  [3] Scan Both");
        Console.Write("\nChoice: ");

        var choice = Console.ReadLine()?.Trim();
        var entries = new List<string>();

        if (choice == "1" || choice == "3")
        {
            var systemPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "";
            entries.AddRange(systemPath.Split(';', StringSplitOptions.RemoveEmptyEntries));
        }

        if (choice == "2" || choice == "3")
        {
            var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
            entries.AddRange(userPath.Split(';', StringSplitOptions.RemoveEmptyEntries));
        }

        // Remove duplicates and already configured entries
        var currentConfigured = _config.Path.Additions.Select(p => Environment.ExpandEnvironmentVariables(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newEntries = entries.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(e => !currentConfigured.Contains(e))
            .ToList();

        if (newEntries.Count == 0)
        {
            Console.WriteLine("\nNo new PATH entries found that aren't already in configuration.");
            return;
        }

        Console.WriteLine($"\nFound {newEntries.Count} PATH entries not in configuration:\n");

        for (int i = 0; i < newEntries.Count; i++)
        {
            var exists = Directory.Exists(newEntries[i]);
            var status = exists ? "[EXISTS]" : "[MISSING]";
            Console.WriteLine($"  [{i + 1,3}] {status} {newEntries[i]}");
        }

        Console.WriteLine("\nEnter numbers to add (comma-separated), 'all' for all, or 'q' to cancel:");
        Console.Write("Selection: ");

        var selection = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(selection) || selection == "q")
            return;

        List<string> selected;
        if (selection == "all")
        {
            selected = newEntries;
        }
        else
        {
            selected = [];
            var parts = selection.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                if (int.TryParse(part, out int idx) && idx >= 1 && idx <= newEntries.Count)
                {
                    selected.Add(newEntries[idx - 1]);
                }
            }
        }

        if (selected.Count == 0)
        {
            Console.WriteLine("No valid selections.");
            return;
        }

        _config.Path.Additions.AddRange(selected);
        ConfigurationManager.SaveConfiguration(_config, _configPath);
        ConsoleHelper.WriteSuccess($"Added {selected.Count} PATH entries to configuration");

        await Task.CompletedTask;
    }

    #endregion

    #region Winget App Scanner

    private async Task ScanInstalledWingetAppsAsync()
    {
        ConsoleHelper.WriteSubHeader("Scanning Installed Apps via winget");

        var result = await RunWingetListAsync();
        if (result.ExitCode != 0)
        {
            ConsoleHelper.WriteError("Failed to scan installed winget apps.");
            if (!string.IsNullOrWhiteSpace(result.Error))
                Console.WriteLine(result.Error.Trim());
            return;
        }

        var installedPackages = ParseWingetListOutput(result.Output);
        if (installedPackages.Count == 0)
        {
            Console.WriteLine("\nNo installed apps were found from the winget source.");
            return;
        }

        var configuredPackageIds = GetConfiguredPackageIds();
        var missingPackages = installedPackages
            .Where(package => IsInstallableWingetId(package.Id))
            .Where(package => !configuredPackageIds.Contains(package.Id))
            .OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missingPackages.Count == 0)
        {
            Console.WriteLine("\nAll installed winget apps are already represented in app installer configuration.");
            return;
        }

        Console.WriteLine($"\nFound {missingPackages.Count} installed winget app(s) not in configuration:\n");

        for (var i = 0; i < missingPackages.Count; i++)
        {
            var package = missingPackages[i];
            Console.WriteLine($"  [{i + 1,3}] {package.Name}");
            Console.WriteLine($"        Id: {package.Id}");
            Console.WriteLine($"   Version: {package.Version}");
        }

        Console.WriteLine("\nOptions:");
        Console.WriteLine("  - Enter numbers separated by commas to add package IDs (e.g., 1,3,5)");
        Console.WriteLine("  - Enter 'all' to add all package IDs");
        Console.WriteLine("  - Enter 'q' to quit without making changes");
        Console.Write("\nYour selection: ");

        var selection = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(selection) || selection == "q")
            return;

        List<WingetInstalledPackage> selected;
        if (selection == "all")
        {
            selected = missingPackages;
        }
        else
        {
            selected = [];
            var parts = selection.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                if (int.TryParse(part, out var index) && index >= 1 && index <= missingPackages.Count)
                {
                    selected.Add(missingPackages[index - 1]);
                }
            }
        }

        if (selected.Count == 0)
        {
            Console.WriteLine("No valid selections.");
            return;
        }

        foreach (var package in selected)
        {
            if (!_config.AppInstaller.DefaultInstalls.Contains(package.Id, StringComparer.OrdinalIgnoreCase))
                _config.AppInstaller.DefaultInstalls.Add(package.Id);
        }

        ConfigurationManager.SaveConfiguration(_config, _configPath);
        ConsoleHelper.WriteSuccess($"Added {selected.Count} package ID(s) to default installs");

        await Task.CompletedTask;
    }

    private HashSet<string> GetConfiguredPackageIds()
    {
        var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var packageId in _config.AppInstaller.DefaultInstalls)
            packageIds.Add(packageId);

        foreach (var script in _config.AppInstaller.CustomScripts)
        {
            if (!string.IsNullOrWhiteSpace(script.Name))
                packageIds.Add(script.Name);
        }

        return packageIds;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunWingetListAsync()
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = "list --source winget --accept-source-agreements",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();
            await Task.WhenAll(outputTask, errorTask);

            return (process.ExitCode, outputTask.Result, errorTask.Result);
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }

    private static List<WingetInstalledPackage> ParseWingetListOutput(string output)
    {
        var lines = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        var headerIndex = lines.FindIndex(line =>
            line.Contains("Name", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("Id", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("Version", StringComparison.OrdinalIgnoreCase));

        if (headerIndex < 0)
            return [];

        var header = lines[headerIndex];
        var idStart = header.IndexOf("Id", StringComparison.OrdinalIgnoreCase);
        var versionStart = header.IndexOf("Version", StringComparison.OrdinalIgnoreCase);
        var availableStart = header.IndexOf("Available", StringComparison.OrdinalIgnoreCase);
        var sourceStart = header.IndexOf("Source", StringComparison.OrdinalIgnoreCase);

        if (idStart < 0 || versionStart < 0)
            return [];

        var packages = new List<WingetInstalledPackage>();
        foreach (var line in lines.Skip(headerIndex + 1))
        {
            if (line.TrimStart().StartsWith("-", StringComparison.Ordinal))
                continue;

            var name = SliceColumn(line, 0, idStart);
            var id = SliceColumn(line, idStart, versionStart);
            int? versionEnd = availableStart > 0 ? availableStart : sourceStart > 0 ? sourceStart : null;
            var version = SliceColumn(line, versionStart, versionEnd);
            var source = sourceStart > 0 ? SliceColumn(line, sourceStart, null) : "winget";

            if (string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace(id) ||
                !source.Equals("winget", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            packages.Add(new WingetInstalledPackage(name, id, version));
        }

        return packages
            .GroupBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static bool IsInstallableWingetId(string id)
    {
        return !id.StartsWith("ARP\\", StringComparison.OrdinalIgnoreCase) &&
               !id.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase) &&
               !id.Contains('\\', StringComparison.Ordinal);
    }

    private static string SliceColumn(string line, int start, int? end)
    {
        if (start >= line.Length)
            return string.Empty;

        var length = end.HasValue ? Math.Min(end.Value, line.Length) - start : line.Length - start;
        if (length <= 0)
            return string.Empty;

        return line.Substring(start, length).Trim();
    }

    private sealed record WingetInstalledPackage(string Name, string Id, string Version);

    #endregion

    #region Network Drives Scanner

    private async Task ScanNetworkDrivesAsync()
    {
        ConsoleHelper.WriteSubHeader("Scanning Network Drives");

        var drives = new List<(string Letter, string Path, string Label)>();

        Console.WriteLine("\nScanning mounted drives...\n");

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType == DriveType.Network)
            {
                var letter = drive.Name.TrimEnd('\\', ':');
                var path = GetNetworkPath(letter);
                var label = drive.VolumeLabel ?? "";

                if (!string.IsNullOrEmpty(path))
                {
                    Console.WriteLine($"  Found: {letter}: -> {path} ({label})");
                    drives.Add((letter, path, label));
                }
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  Drive {drive.Name} is a {drive.DriveType} drive. Skipping...");
                Console.ForegroundColor = ConsoleColor.White;
            }
        }

        if (drives.Count == 0)
        {
            Console.WriteLine("  No network drives found.");
            return;
        }

        // Filter out already configured drives
        var configuredLetters = _config.NetworkDrives.Drives.Select(d => d.DriveLetter.ToUpperInvariant()).ToHashSet();
        var newDrives = drives.Where(d => !configuredLetters.Contains(d.Letter.ToUpperInvariant())).ToList();

        if (newDrives.Count == 0)
        {
            Console.WriteLine("\nAll found network drives are already in configuration.");
            return;
        }

        Console.WriteLine($"\n{newDrives.Count} drives not in configuration:");
        for (int i = 0; i < newDrives.Count; i++)
        {
            var (letter, path, label) = newDrives[i];
            Console.WriteLine($"  [{i + 1}] {letter}: -> {path} ({label})");
        }

        Console.WriteLine("\nEnter numbers to add (comma-separated), 'all' for all, or 'q' to cancel:");
        Console.Write("Selection: ");

        var selection = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(selection) || selection == "q")
            return;

        List<(string Letter, string Path, string Label)> selected;
        if (selection == "all")
        {
            selected = newDrives;
        }
        else
        {
            selected = [];
            var parts = selection.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                if (int.TryParse(part, out int idx) && idx >= 1 && idx <= newDrives.Count)
                {
                    selected.Add(newDrives[idx - 1]);
                }
            }
        }

        if (selected.Count == 0)
        {
            Console.WriteLine("No valid selections.");
            return;
        }

        foreach (var (letter, path, label) in selected)
        {
            _config.NetworkDrives.Drives.Add(new NetworkDriveMapping
            {
                DriveLetter = letter,
                NetworkPath = path,
                Label = label,
                Persistent = true,
                DeleteFirst = true
            });
        }

        ConfigurationManager.SaveConfiguration(_config, _configPath);
        ConsoleHelper.WriteSuccess($"Added {selected.Count} network drives to configuration");

        await Task.CompletedTask;
    }

    private static string GetNetworkPath(string driveLetter)
    {
        try
        {
            // Use WMI or registry to get the UNC path
            using var key = Registry.CurrentUser.OpenSubKey($@"Network\{driveLetter}");
            if (key != null)
            {
                return key.GetValue("RemotePath") as string ?? "";
            }
        }
        catch { }

        // Fallback: try to use net use command
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "net",
                Arguments = $"use {driveLetter}:",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            // Parse output for "Remote name"
            foreach (var line in output.Split('\n'))
            {
                if (line.Trim().StartsWith("Remote name", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3)
                    {
                        return parts[2].Trim();
                    }
                }
            }
        }
        catch { }

        return "";
    }

    #endregion

    #region Shell Folders Scanner

    private async Task ScanShellFoldersAsync()
    {
        ConsoleHelper.WriteSubHeader("Scanning Shell Folders");

        var knownFolders = new Dictionary<string, (string RegValue, string FriendlyName)>
        {
            { "Desktop", ("Desktop", "Desktop") },
            { "Downloads", ("{374DE290-123F-4565-9164-39C4925E467B}", "Downloads") },
            { "Documents", ("Personal", "Documents") },
            { "Pictures", ("My Pictures", "Pictures") },
            { "Music", ("My Music", "Music") },
            { "Videos", ("My Video", "Videos") }
        };

        var currentFolders = new List<(string Name, string RegValue, string Path)>();

        Console.WriteLine("\nScanning current shell folders...\n");

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            if (key == null)
            {
                ConsoleHelper.WriteError("Failed to read shell folders");
                return;
            }

            foreach (var (name, (regValue, friendlyName)) in knownFolders)
            {
                var value = key.GetValue(regValue) as string ?? "";
                if (!string.IsNullOrEmpty(value))
                {
                    Console.WriteLine($"  {friendlyName}: {value}");
                    currentFolders.Add((friendlyName, regValue, value));
                }
            }
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Error reading shell folders: {ex.Message}");
            return;
        }

        // Filter out already configured folders
        var configuredRegValues = _config.ShellFolders.Folders.Select(f => f.RegistryValue).ToHashSet();
        var newFolders = currentFolders.Where(f => !configuredRegValues.Contains(f.RegValue)).ToList();

        if (newFolders.Count == 0)
        {
            Console.WriteLine("\nAll found shell folders are already in configuration.");
            return;
        }

        Console.WriteLine($"\n{newFolders.Count} folders not in configuration:");
        for (int i = 0; i < newFolders.Count; i++)
        {
            var (name, _, path) = newFolders[i];
            Console.WriteLine($"  [{i + 1}] {name}: {path}");
        }

        Console.WriteLine("\nEnter numbers to add (comma-separated), 'all' for all, or 'q' to cancel:");
        Console.Write("Selection: ");

        var selection = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(selection) || selection == "q")
            return;

        List<(string Name, string RegValue, string Path)> selected;
        if (selection == "all")
        {
            selected = newFolders;
        }
        else
        {
            selected = [];
            var parts = selection.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                if (int.TryParse(part, out int idx) && idx >= 1 && idx <= newFolders.Count)
                {
                    selected.Add(newFolders[idx - 1]);
                }
            }
        }

        if (selected.Count == 0)
        {
            Console.WriteLine("No valid selections.");
            return;
        }

        foreach (var (name, regValue, path) in selected)
        {
            _config.ShellFolders.Folders.Add(new ShellFolderMapping
            {
                FolderName = name,
                RegistryValue = regValue,
                Path = path
            });
        }

        ConfigurationManager.SaveConfiguration(_config, _configPath);
        ConsoleHelper.WriteSuccess($"Added {selected.Count} shell folders to configuration");

        await Task.CompletedTask;
    }

    #endregion
}
