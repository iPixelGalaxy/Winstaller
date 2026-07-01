using Winstaller.Configuration;
using Winstaller.Models;

namespace Winstaller.Utilities;

/// <summary>
/// Utility for managing AppData folder symlinks - allows converting regular folders to symlinks
/// </summary>
public class AppDataSymlinkUtility
{
    private readonly WinstallerConfig _config;
    private readonly string _configPath;

    private string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private string AppDataRoaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private string AppDataLocal => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private string AppDataLocalLow => Path.Combine(UserProfile, "AppData", "LocalLow");

    public AppDataSymlinkUtility(WinstallerConfig config, string configPath)
    {
        _config = config;
        _configPath = configPath;
    }

    public async Task ShowMenuAsync()
    {
        while (true)
        {
            Console.Clear();
            ConsoleHelper.WriteHeader("AppData Symlink Utility");
            Console.WriteLine("Convert regular AppData folders to symlinks\n");

            Console.WriteLine("  [1] Scan Roaming AppData");
            Console.WriteLine("  [2] Scan Local AppData");
            Console.WriteLine("  [3] Scan LocalLow AppData");
            Console.WriteLine("  [4] Scan All AppData Sections");
            Console.WriteLine("  [5] Scan Existing Symlinks (add missing to config)");
            Console.WriteLine("  [6] Show Current Configuration");
            Console.WriteLine("\n  [0] Back to Main Menu");

            Console.Write("\nSelect option: ");
            var input = Console.ReadLine()?.Trim();

            switch (input)
            {
                case "1":
                    await ScanAndProcessAsync("Roaming", AppDataRoaming, "Roaming");
                    break;
                case "2":
                    await ScanAndProcessAsync("Local", AppDataLocal, "Local");
                    break;
                case "3":
                    await ScanAndProcessAsync("LocalLow", AppDataLocalLow, "LocalLow");
                    break;
                case "4":
                    await ScanAllAsync();
                    break;
                case "5":
                    await ScanExistingSymlinksAsync();
                    break;
                case "6":
                    ShowConfiguration();
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
        var allDirs = new List<(string Section, string Name, string FullPath)>();

        // Scan all three sections
        allDirs.AddRange(ScanDirectory(AppDataRoaming, "Roaming"));
        allDirs.AddRange(ScanDirectory(AppDataLocal, "Local"));
        allDirs.AddRange(ScanDirectory(AppDataLocalLow, "LocalLow"));

        if (allDirs.Count == 0)
        {
            Console.WriteLine("\nNo non-symlinked directories found in any AppData section.");
            return;
        }

        await DisplayAndProcessDirectoriesAsync(allDirs);
    }

    private async Task ScanAndProcessAsync(string sectionName, string appDataPath, string subFolder)
    {
        ConsoleHelper.WriteSubHeader($"Scanning {sectionName} AppData");

        var directories = ScanDirectory(appDataPath, subFolder);

        if (directories.Count == 0)
        {
            Console.WriteLine($"\nNo non-symlinked directories found in {sectionName}.");
            return;
        }

        await DisplayAndProcessDirectoriesAsync(directories);
    }

    private List<(string Section, string Name, string FullPath)> ScanDirectory(string appDataPath, string section)
    {
        var results = new List<(string Section, string Name, string FullPath)>();

        if (!Directory.Exists(appDataPath))
            return results;

        try
        {
            var directories = Directory.GetDirectories(appDataPath);

            foreach (var dir in directories)
            {
                var dirName = Path.GetFileName(dir);

                // Skip excluded directories
                if (_config.AppDataUtility.ExcludedDirectories.Contains(dirName, StringComparer.OrdinalIgnoreCase))
                    continue;

                // Check if it's already a symlink
                var info = new DirectoryInfo(dir);
                if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    results.Add((section, dirName, dir));
                }
            }
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Error scanning {appDataPath}: {ex.Message}");
        }

        return results;
    }

    private async Task DisplayAndProcessDirectoriesAsync(List<(string Section, string Name, string FullPath)> directories)
    {
        Console.WriteLine($"\nFound {directories.Count} non-symlinked directories:\n");

        // Display directories with numbers
        for (int i = 0; i < directories.Count; i++)
        {
            var (section, name, fullPath) = directories[i];
            var size = GetDirectorySize(fullPath);
            Console.WriteLine($"  [{i + 1,3}] [{section,-8}] {name,-40} ({FormatSize(size)})");
        }

        Console.WriteLine("\nOptions:");
        Console.WriteLine("  - Enter numbers separated by commas to select directories (e.g., 1,3,5)");
        Console.WriteLine("  - Enter 'all' to select all directories");
        Console.WriteLine("  - Enter 'q' to quit without making changes");

        Console.Write("\nYour selection: ");
        var input = Console.ReadLine()?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(input) || input == "q")
            return;

        List<(string Section, string Name, string FullPath)> selected;

        if (input == "all")
        {
            selected = directories;
        }
        else
        {
            selected = [];
            var parts = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var part in parts)
            {
                if (int.TryParse(part, out int index) && index >= 1 && index <= directories.Count)
                {
                    selected.Add(directories[index - 1]);
                }
            }
        }

        if (selected.Count == 0)
        {
            Console.WriteLine("No valid selections made.");
            return;
        }

        Console.WriteLine($"\nYou have selected {selected.Count} directories to convert to symlinks:");
        foreach (var (section, name, _) in selected)
        {
            Console.WriteLine($"  - [{section}] {name}");
        }

        if (!ConsoleHelper.Confirm("\nProceed with conversion?"))
        {
            Console.WriteLine("Operation cancelled.");
            return;
        }

        // Ensure administrator privileges
        if (!AdminHelper.IsAdministrator())
        {
            ConsoleHelper.WriteError("This operation requires administrator privileges.");
            ConsoleHelper.WriteWarning("Please run this application as administrator.");
            return;
        }

        // Process each selected directory
        foreach (var (section, name, fullPath) in selected)
        {
            await ConvertToSymlinkAsync(section, name, fullPath);
        }
    }

    private async Task ConvertToSymlinkAsync(string section, string name, string sourcePath)
    {
        Console.WriteLine($"\nProcessing [{section}] {name}...");

        var symlinkBaseDir = ExpandEnvironmentVariables(_config.AppDataUtility.SymlinkBaseDirectory);
        var targetPath = Path.Combine(symlinkBaseDir, section, name);

        try
        {
            // Step 1: Ensure target parent directory exists
            var targetParent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetParent) && !Directory.Exists(targetParent))
            {
                Directory.CreateDirectory(targetParent);
                Console.WriteLine($"  Created target directory: {targetParent}");
            }

            // Step 2: Copy the directory to target location
            Console.WriteLine($"  Copying to {targetPath}...");

            if (Directory.Exists(targetPath))
            {
                if (!ConsoleHelper.Confirm($"  Target already exists. Overwrite?"))
                {
                    Console.WriteLine("  Skipped.");
                    return;
                }
                Directory.Delete(targetPath, true);
            }

            var copyResult = await CopyDirectoryAsync(sourcePath, targetPath);

            if (copyResult.Aborted)
            {
                Console.WriteLine("  Copy aborted by user. Cleaning up...");
                try { Directory.Delete(targetPath, true); } catch { }
                return;
            }

            if (copyResult.SkippedFiles.Count > 0)
            {
                ConsoleHelper.WriteWarning($"  {copyResult.SkippedFiles.Count} file(s) were skipped:");
                foreach (var skipped in copyResult.SkippedFiles.Take(5))
                {
                    Console.WriteLine($"    - {Path.GetFileName(skipped)}");
                }
                if (copyResult.SkippedFiles.Count > 5)
                {
                    Console.WriteLine($"    ... and {copyResult.SkippedFiles.Count - 5} more");
                }

                if (!ConsoleHelper.Confirm("  Continue with incomplete copy?"))
                {
                    Console.WriteLine("  Aborting. Cleaning up...");
                    try { Directory.Delete(targetPath, true); } catch { }
                    return;
                }
            }

            // Step 3: Verify the copy was successful
            Console.WriteLine("  Verifying copy...");
            if (!VerifyCopy(sourcePath, targetPath))
            {
                ConsoleHelper.WriteError("  Verification failed! Copy may be incomplete.");
                if (!ConsoleHelper.Confirm("  Continue anyway?"))
                {
                    Console.WriteLine("  Aborting.");
                    return;
                }
            }

            // Step 4: Delete the original directory
            Console.WriteLine("  Removing original directory...");
            try
            {
                Directory.Delete(sourcePath, true);
            }
            catch (IOException ex) when (IsFileLocked(ex))
            {
                ConsoleHelper.WriteError("  Cannot delete original directory - files still in use.");
                ConsoleHelper.WriteWarning("  Your data has been copied to the target location.");
                ConsoleHelper.WriteWarning("  Please close all applications using this folder and manually:");
                Console.WriteLine($"    1. Delete: {sourcePath}");
                Console.WriteLine($"    2. Run: mklink /D \"{sourcePath}\" \"{targetPath}\"");
                return;
            }

            // Step 5: Create symlink
            Console.WriteLine("  Creating symlink...");
            var cmdResult = await RunCmdAsync($"mklink /D \"{sourcePath}\" \"{targetPath}\"");

            if (cmdResult == 0)
            {
                ConsoleHelper.WriteSuccess($"  Successfully converted {name} to symlink");

                // Add to configuration and save
                AddToConfiguration(section, name);
            }
            else
            {
                ConsoleHelper.WriteError($"  Failed to create symlink for {name}");

                // Attempt recovery by moving the target back
                Console.WriteLine("  Attempting recovery...");
                try
                {
                    await CopyDirectoryAsync(targetPath, sourcePath);
                    Console.WriteLine("  Recovered original directory");
                }
                catch (Exception ex)
                {
                    ConsoleHelper.WriteError($"  Recovery failed: {ex.Message}");
                    ConsoleHelper.WriteError($"  Data is available at: {targetPath}");
                }
            }
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"  Error processing {name}: {ex.Message}");
        }
    }

    private static async Task<CopyResult> CopyDirectoryAsync(string source, string destination, List<string>? skippedFiles = null)
    {
        skippedFiles ??= [];
        var info = new DirectoryInfo(source);

        Directory.CreateDirectory(destination);

        // Copy files
        foreach (var file in info.GetFiles())
        {
            var targetFilePath = Path.Combine(destination, file.Name);
            var copySuccess = await CopyFileWithRetryAsync(file.FullName, targetFilePath, skippedFiles);
            if (!copySuccess)
            {
                // User chose to abort
                return new CopyResult { Success = false, Aborted = true, SkippedFiles = skippedFiles };
            }
        }

        // Copy subdirectories
        foreach (var subDir in info.GetDirectories())
        {
            var targetSubDir = Path.Combine(destination, subDir.Name);
            var result = await CopyDirectoryAsync(subDir.FullName, targetSubDir, skippedFiles);
            if (result.Aborted)
            {
                return result;
            }
        }

        return new CopyResult { Success = true, Aborted = false, SkippedFiles = skippedFiles };
    }

    private static async Task<bool> CopyFileWithRetryAsync(string source, string destination, List<string> skippedFiles)
    {
        while (true)
        {
            try
            {
                await Task.Run(() => File.Copy(source, destination, true));
                return true;
            }
            catch (IOException ex) when (IsFileLocked(ex))
            {
                Console.WriteLine();
                ConsoleHelper.WriteWarning($"    File in use: {Path.GetFileName(source)}");
                Console.WriteLine("    The file is being used by another application.");
                Console.WriteLine();
                Console.WriteLine("    Options:");
                Console.WriteLine("      [R] Retry (close the application first)");
                Console.WriteLine("      [S] Skip this file");
                Console.WriteLine("      [A] Abort entire operation");
                Console.Write("    Choice: ");

                var choice = Console.ReadLine()?.Trim().ToUpperInvariant();

                switch (choice)
                {
                    case "R":
                        Console.WriteLine("    Retrying...");
                        continue;
                    case "S":
                        Console.WriteLine($"    Skipping {Path.GetFileName(source)}");
                        skippedFiles.Add(source);
                        return true;
                    case "A":
                        Console.WriteLine("    Aborting operation...");
                        return false;
                    default:
                        Console.WriteLine("    Invalid choice, please try again.");
                        continue;
                }
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine();
                ConsoleHelper.WriteWarning($"    Access denied: {Path.GetFileName(source)}");
                Console.WriteLine("    Options:");
                Console.WriteLine("      [R] Retry");
                Console.WriteLine("      [S] Skip this file");
                Console.WriteLine("      [A] Abort entire operation");
                Console.Write("    Choice: ");

                var choice = Console.ReadLine()?.Trim().ToUpperInvariant();

                switch (choice)
                {
                    case "R":
                        continue;
                    case "S":
                        skippedFiles.Add(source);
                        return true;
                    case "A":
                        return false;
                    default:
                        continue;
                }
            }
        }
    }

    private static bool IsFileLocked(IOException ex)
    {
        // Check for common file-in-use HRESULT codes
        var errorCode = ex.HResult & 0xFFFF;
        return errorCode == 32 || errorCode == 33; // ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION
    }

    private record CopyResult
    {
        public bool Success { get; init; }
        public bool Aborted { get; init; }
        public List<string> SkippedFiles { get; init; } = [];
    }

    private static bool VerifyCopy(string source, string destination)
    {
        try
        {
            var sourceFiles = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
            var destFiles = Directory.GetFiles(destination, "*", SearchOption.AllDirectories);

            if (sourceFiles.Length != destFiles.Length)
                return false;

            var sourceDirs = Directory.GetDirectories(source, "*", SearchOption.AllDirectories);
            var destDirs = Directory.GetDirectories(destination, "*", SearchOption.AllDirectories);

            return sourceDirs.Length == destDirs.Length;
        }
        catch
        {
            return false;
        }
    }

    private static long GetDirectorySize(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch
        {
            return 0;
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        int order = 0;
        double size = bytes;

        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return $"{size:0.##} {sizes[order]}";
    }

    private void ShowConfiguration()
    {
        ConsoleHelper.WriteSubHeader("AppData Utility Configuration");

        Console.WriteLine($"\nSymlink Base Directory: {_config.AppDataUtility.SymlinkBaseDirectory}");

        Console.WriteLine($"\nExcluded Directories ({_config.AppDataUtility.ExcludedDirectories.Count}):");
        foreach (var dir in _config.AppDataUtility.ExcludedDirectories)
            Console.WriteLine($"  - {dir}");

        Console.WriteLine($"\nConfigured Symlinks:");
        Console.WriteLine($"  Roaming ({_config.Symlinks.RoamingDirectories.Count}):");
        foreach (var dir in _config.Symlinks.RoamingDirectories)
            Console.WriteLine($"    - {dir}");

        Console.WriteLine($"  Local ({_config.Symlinks.LocalDirectories.Count}):");
        foreach (var dir in _config.Symlinks.LocalDirectories)
            Console.WriteLine($"    - {dir}");

        Console.WriteLine($"  LocalLow ({_config.Symlinks.LocalLowDirectories.Count}):");
        foreach (var dir in _config.Symlinks.LocalLowDirectories)
            Console.WriteLine($"    - {dir}");

        Console.WriteLine($"\nAppData Locations:");
        Console.WriteLine($"  Roaming:  {AppDataRoaming}");
        Console.WriteLine($"  Local:    {AppDataLocal}");
        Console.WriteLine($"  LocalLow: {AppDataLocalLow}");
    }

    private async Task ScanExistingSymlinksAsync()
    {
        ConsoleHelper.WriteSubHeader("Scanning for Existing Symlinks Not in Config");

        var missingFromConfig = new List<(string Section, string Name, string FullPath, string Target)>();

        // Scan all three sections for symlinks
        ScanForExistingSymlinks(AppDataRoaming, "Roaming", _config.Symlinks.RoamingDirectories, missingFromConfig);
        ScanForExistingSymlinks(AppDataLocal, "Local", _config.Symlinks.LocalDirectories, missingFromConfig);
        ScanForExistingSymlinks(AppDataLocalLow, "LocalLow", _config.Symlinks.LocalLowDirectories, missingFromConfig);

        if (missingFromConfig.Count == 0)
        {
            Console.WriteLine("\nAll existing symlinks are already in the configuration.");
            return;
        }

        Console.WriteLine($"\nFound {missingFromConfig.Count} symlinks not in configuration:\n");

        for (int i = 0; i < missingFromConfig.Count; i++)
        {
            var (section, name, _, target) = missingFromConfig[i];
            Console.WriteLine($"  [{i + 1,3}] [{section,-8}] {name}");
            Console.WriteLine($"         -> {target}");
        }

        Console.WriteLine("\nOptions:");
        Console.WriteLine("  - Enter numbers separated by commas to select (e.g., 1,3,5)");
        Console.WriteLine("  - Enter 'all' to select all");
        Console.WriteLine("  - Enter 'q' to quit without making changes");

        Console.Write("\nYour selection: ");
        var input = Console.ReadLine()?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(input) || input == "q")
            return;

        List<(string Section, string Name, string FullPath, string Target)> selected;

        if (input == "all")
        {
            selected = missingFromConfig;
        }
        else
        {
            selected = [];
            var parts = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var part in parts)
            {
                if (int.TryParse(part, out int index) && index >= 1 && index <= missingFromConfig.Count)
                {
                    selected.Add(missingFromConfig[index - 1]);
                }
            }
        }

        if (selected.Count == 0)
        {
            Console.WriteLine("No valid selections made.");
            return;
        }

        // Add selected symlinks to config
        foreach (var (section, name, _, _) in selected)
        {
            AddToConfiguration(section, name);
        }

        Console.WriteLine($"\nAdded {selected.Count} symlinks to configuration.");
        await Task.CompletedTask;
    }

    private void ScanForExistingSymlinks(string appDataPath, string section, List<string> configuredList, List<(string Section, string Name, string FullPath, string Target)> results)
    {
        if (!Directory.Exists(appDataPath))
            return;

        try
        {
            var directories = Directory.GetDirectories(appDataPath);

            foreach (var dir in directories)
            {
                var dirName = Path.GetFileName(dir);

                // Skip excluded directories
                if (_config.AppDataUtility.ExcludedDirectories.Contains(dirName, StringComparer.OrdinalIgnoreCase))
                    continue;

                // Check if it's a symlink
                var info = new DirectoryInfo(dir);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    // It's a symlink - check if it's in the config
                    if (!configuredList.Contains(dirName, StringComparer.OrdinalIgnoreCase))
                    {
                        var target = GetSymlinkTarget(dir);
                        results.Add((section, dirName, dir, target));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Error scanning {appDataPath}: {ex.Message}");
        }
    }

    private static string GetSymlinkTarget(string symlinkPath)
    {
        try
        {
            var info = new DirectoryInfo(symlinkPath);
            if (info.LinkTarget != null)
            {
                return info.LinkTarget;
            }
        }
        catch { }

        return "(unknown target)";
    }

    private void AddToConfiguration(string section, string name)
    {
        var list = section switch
        {
            "Roaming" => _config.Symlinks.RoamingDirectories,
            "Local" => _config.Symlinks.LocalDirectories,
            "LocalLow" => _config.Symlinks.LocalLowDirectories,
            _ => null
        };

        if (list == null)
        {
            ConsoleHelper.WriteWarning($"  Unknown section: {section}");
            return;
        }

        if (!list.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(name);
            ConfigurationManager.SaveConfiguration(_config, _configPath);
            Console.WriteLine($"  Added '{name}' to {section} directories in config");
        }
        else
        {
            Console.WriteLine($"  '{name}' already in {section} config");
        }
    }

    private static string ExpandEnvironmentVariables(string path)
    {
        var result = Environment.ExpandEnvironmentVariables(path);
        result = result.Replace("{USERNAME}", Environment.UserName);
        return result;
    }

    private static async Task<int> RunCmdAsync(string command)
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            process.Start();

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            if (!string.IsNullOrWhiteSpace(output))
                Console.WriteLine($"    {output.Trim()}");

            return process.ExitCode;
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Error running command: {ex.Message}");
            return -1;
        }
    }
}
