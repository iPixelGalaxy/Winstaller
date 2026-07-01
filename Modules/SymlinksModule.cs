using Winstaller.Configuration;
using Winstaller.Models;
using Winstaller.Utilities;

namespace Winstaller.Modules;

/// <summary>
/// Module for creating symlinks for AppData directories and special files
/// </summary>
public class SymlinksModule : ModuleBase
{
    public SymlinksModule(WinstallerConfig config) : base(config) { }

    public override string Name => "Symlinks";
    public override string Description => "Creates symlinks for AppData directories, git config, SSH keys, etc.";
    public override bool IsEnabled => Config.Symlinks.Enabled;

    private string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private string AppDataRoaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private string AppDataLocal => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private string AppDataLocalLow => Path.Combine(UserProfile, "AppData", "LocalLow");

    public override async Task<bool> ExecuteAsync()
    {
        if (!IsEnabled)
        {
            ConsoleHelper.WriteWarning("Symlinks module is disabled in configuration.");
            return false;
        }

        EnsureAdministrator();

        ConsoleHelper.WriteHeader("Symlinks Setup");

        var success = true;
        var baseDir = ExpandEnvironmentVariables(Config.Symlinks.BaseSymlinkDirectory);

        // Process Roaming directories
        if (Config.Symlinks.RoamingDirectories.Count > 0)
        {
            ConsoleHelper.WriteSubHeader("Roaming AppData Symlinks");
            foreach (var dir in Config.Symlinks.RoamingDirectories.Where(dir => !string.IsNullOrWhiteSpace(dir)))
            {
                var result = await CreateAppDataSymlink("Roaming", dir, AppDataRoaming, Path.Combine(baseDir, "AppData", "Roaming"));
                if (!result) success = false;
            }
        }

        // Process Local directories
        if (Config.Symlinks.LocalDirectories.Count > 0)
        {
            ConsoleHelper.WriteSubHeader("Local AppData Symlinks");
            foreach (var dir in Config.Symlinks.LocalDirectories.Where(dir => !string.IsNullOrWhiteSpace(dir)))
            {
                var result = await CreateAppDataSymlink("Local", dir, AppDataLocal, Path.Combine(baseDir, "AppData", "Local"));
                if (!result) success = false;
            }
        }

        // Process LocalLow directories
        if (Config.Symlinks.LocalLowDirectories.Count > 0)
        {
            ConsoleHelper.WriteSubHeader("LocalLow AppData Symlinks");
            foreach (var dir in Config.Symlinks.LocalLowDirectories.Where(dir => !string.IsNullOrWhiteSpace(dir)))
            {
                var result = await CreateAppDataSymlink("LocalLow", dir, AppDataLocalLow, Path.Combine(baseDir, "AppData", "LocalLow"));
                if (!result) success = false;
            }
        }

        // Process special symlinks
        if (Config.Symlinks.SpecialSymlinks.Count > 0)
        {
            ConsoleHelper.WriteSubHeader("Special Symlinks");
            foreach (var symlink in Config.Symlinks.SpecialSymlinks)
            {
                Console.WriteLine($"\nProcessing: {symlink.Description}");
                var source = ExpandEnvironmentVariables(symlink.Source);
                var result = await CreateSymlink(
                    source,
                    ResolveSpecialTarget(baseDir, source, symlink.Target),
                    symlink.IsDirectory
                );
                if (!result) success = false;
            }
        }

        return success;
    }

    private async Task<bool> CreateAppDataSymlink(string category, string relativePath, string appDataPath, string symlinkBasePath)
    {
        var sourcePath = Path.Combine(appDataPath, relativePath);
        var targetPath = Path.Combine(symlinkBasePath, relativePath);

        Console.WriteLine($"  Symlinking {category}\\{relativePath}...");

        return await CreateSymlink(sourcePath, targetPath, true);
    }

    private async Task<bool> CreateSymlink(string sourcePath, string targetPath, bool isDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(targetPath))
            {
                ConsoleHelper.WriteWarning("    Skipped blank symlink path.");
                return false;
            }

            var targetDir = isDirectory ? targetPath : Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
                Console.WriteLine($"    Created target directory: {targetDir}");
            }

            if (isDirectory && Directory.Exists(sourcePath))
            {
                var info = new DirectoryInfo(sourcePath);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return VerifySymlink(sourcePath, targetPath, true);
                }

                ConsoleHelper.WriteWarning($"    Source exists and is not a symlink, skipping to avoid deleting app data: {sourcePath}");
                return false;
            }

            if (!isDirectory && File.Exists(sourcePath))
            {
                var info = new FileInfo(sourcePath);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return VerifySymlink(sourcePath, targetPath, false);
                }

                ConsoleHelper.WriteWarning($"    Source exists and is not a symlink, skipping to avoid deleting app data: {sourcePath}");
                return false;
            }

            var sourceDir = Path.GetDirectoryName(sourcePath);
            if (!string.IsNullOrEmpty(sourceDir) && !Directory.Exists(sourceDir))
            {
                Directory.CreateDirectory(sourceDir);
            }

            var linkType = isDirectory ? "/D" : "";
            var result = await RunCmdAsync($"mklink {linkType} \"{sourcePath}\" \"{targetPath}\"", 10000);

            if (result == 0 && VerifySymlink(sourcePath, targetPath, isDirectory))
            {
                ConsoleHelper.WriteSuccess($"    Symlink created: {sourcePath} -> {targetPath}");
                return true;
            }

            ConsoleHelper.WriteError($"    Failed to create symlink");
            return false;
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"    Error: {ex.Message}");
            return false;
        }
    }

    private static bool VerifySymlink(string sourcePath, string targetPath, bool isDirectory)
    {
        FileSystemInfo info = isDirectory ? new DirectoryInfo(sourcePath) : new FileInfo(sourcePath);
        if (!info.Exists)
        {
            ConsoleHelper.WriteError($"    Symlink missing after creation: {sourcePath}");
            return false;
        }

        if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            ConsoleHelper.WriteError($"    Source is not a symlink: {sourcePath}");
            return false;
        }

        if (isDirectory && !Directory.Exists(targetPath))
        {
            ConsoleHelper.WriteError($"    Target is missing: {targetPath}");
            return false;
        }

        if (!isDirectory && !File.Exists(targetPath))
        {
            ConsoleHelper.WriteError($"    Target is missing: {targetPath}");
            return false;
        }

        var linkTarget = info.LinkTarget;
        if (string.IsNullOrWhiteSpace(linkTarget))
        {
            ConsoleHelper.WriteError($"    Could not read symlink target: {sourcePath}");
            return false;
        }

        var normalizedLinkTarget = Path.GetFullPath(linkTarget, Path.GetDirectoryName(sourcePath) ?? Environment.CurrentDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedTarget = Path.GetFullPath(targetPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!normalizedLinkTarget.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))
        {
            ConsoleHelper.WriteError($"    Symlink target mismatch: {sourcePath} -> {linkTarget}, expected {targetPath}");
            return false;
        }

        Console.WriteLine($"    Existing symlink verified: {sourcePath} -> {targetPath}");
        return true;
    }
    protected override List<MenuOption> GetMenuOptions()
    {
        return
        [
            new MenuOption("Create All Symlinks", ExecuteAsync),
            new MenuOption("Create Roaming Symlinks Only", CreateRoamingSymlinksAsync),
            new MenuOption("Create Local Symlinks Only", CreateLocalSymlinksAsync),
            new MenuOption("Create LocalLow Symlinks Only", CreateLocalLowSymlinksAsync),
            new MenuOption("Create Special Symlinks Only", CreateSpecialSymlinksAsync),
            new MenuOption("List All Configured Symlinks", ListConfiguredSymlinks),
            new MenuOption("Verify Existing Symlinks", VerifySymlinksAsync)
        ];
    }

    private async Task CreateRoamingSymlinksAsync()
    {
        EnsureAdministrator();
        var baseDir = ExpandEnvironmentVariables(Config.Symlinks.BaseSymlinkDirectory);

        ConsoleHelper.WriteSubHeader("Roaming AppData Symlinks");
        foreach (var dir in Config.Symlinks.RoamingDirectories.Where(dir => !string.IsNullOrWhiteSpace(dir)))
        {
            await CreateAppDataSymlink("Roaming", dir, AppDataRoaming, Path.Combine(baseDir, "AppData", "Roaming"));
        }
    }

    private async Task CreateLocalSymlinksAsync()
    {
        EnsureAdministrator();
        var baseDir = ExpandEnvironmentVariables(Config.Symlinks.BaseSymlinkDirectory);

        ConsoleHelper.WriteSubHeader("Local AppData Symlinks");
        foreach (var dir in Config.Symlinks.LocalDirectories.Where(dir => !string.IsNullOrWhiteSpace(dir)))
        {
            await CreateAppDataSymlink("Local", dir, AppDataLocal, Path.Combine(baseDir, "AppData", "Local"));
        }
    }

    private async Task CreateLocalLowSymlinksAsync()
    {
        EnsureAdministrator();
        var baseDir = ExpandEnvironmentVariables(Config.Symlinks.BaseSymlinkDirectory);

        ConsoleHelper.WriteSubHeader("LocalLow AppData Symlinks");
        foreach (var dir in Config.Symlinks.LocalLowDirectories.Where(dir => !string.IsNullOrWhiteSpace(dir)))
        {
            await CreateAppDataSymlink("LocalLow", dir, AppDataLocalLow, Path.Combine(baseDir, "AppData", "LocalLow"));
        }
    }

    private async Task CreateSpecialSymlinksAsync()
    {
        EnsureAdministrator();

        ConsoleHelper.WriteSubHeader("Special Symlinks");
        var baseDir = ExpandEnvironmentVariables(Config.Symlinks.BaseSymlinkDirectory);
        foreach (var symlink in Config.Symlinks.SpecialSymlinks)
        {
            Console.WriteLine($"\nProcessing: {symlink.Description}");
            await CreateSymlink(
                ExpandEnvironmentVariables(symlink.Source),
                ResolveSpecialTarget(baseDir, ExpandEnvironmentVariables(symlink.Source), symlink.Target),
                symlink.IsDirectory
            );
        }
    }

    private string ResolveSpecialTarget(string baseDir, string source, string configuredTarget)
    {
        if (!string.IsNullOrWhiteSpace(configuredTarget))
            return ExpandEnvironmentVariables(configuredTarget);

        var relative = source.StartsWith(AppDataRoaming, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine("AppData", "Roaming", Path.GetRelativePath(AppDataRoaming, source))
            : source.StartsWith(AppDataLocal, StringComparison.OrdinalIgnoreCase)
                ? Path.Combine("AppData", "Local", Path.GetRelativePath(AppDataLocal, source))
                : source.StartsWith(AppDataLocalLow, StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine("AppData", "LocalLow", Path.GetRelativePath(AppDataLocalLow, source))
                    : Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        return Path.Combine(baseDir, relative);
    }

    private Task ListConfiguredSymlinks()
    {
        ConsoleHelper.WriteSubHeader("Configured Symlinks");

        Console.WriteLine("\nRoaming AppData:");
        foreach (var dir in Config.Symlinks.RoamingDirectories.Where(dir => !string.IsNullOrWhiteSpace(dir)))
            Console.WriteLine($"  - {dir}");

        Console.WriteLine("\nLocal AppData:");
        foreach (var dir in Config.Symlinks.LocalDirectories.Where(dir => !string.IsNullOrWhiteSpace(dir)))
            Console.WriteLine($"  - {dir}");

        Console.WriteLine("\nLocalLow AppData:");
        foreach (var dir in Config.Symlinks.LocalLowDirectories.Where(dir => !string.IsNullOrWhiteSpace(dir)))
            Console.WriteLine($"  - {dir}");

        Console.WriteLine("\nSpecial Symlinks:");
        foreach (var s in Config.Symlinks.SpecialSymlinks)
            Console.WriteLine($"  - {s.Description}: {s.Source} -> {s.Target}");

        return Task.CompletedTask;
    }

    private Task VerifySymlinksAsync()
    {
        ConsoleHelper.WriteSubHeader("Verifying Symlinks");

        var baseDir = ExpandEnvironmentVariables(Config.Symlinks.BaseSymlinkDirectory);
        var valid = 0;
        var invalid = 0;
        var missing = 0;

        // Check Roaming
        foreach (var dir in Config.Symlinks.RoamingDirectories.Where(dir => !string.IsNullOrWhiteSpace(dir)))
        {
            var path = Path.Combine(AppDataRoaming, dir);
            VerifyPath(path, ref valid, ref invalid, ref missing);
        }

        // Check Local
        foreach (var dir in Config.Symlinks.LocalDirectories.Where(dir => !string.IsNullOrWhiteSpace(dir)))
        {
            var path = Path.Combine(AppDataLocal, dir);
            VerifyPath(path, ref valid, ref invalid, ref missing);
        }

        // Check LocalLow
        foreach (var dir in Config.Symlinks.LocalLowDirectories.Where(dir => !string.IsNullOrWhiteSpace(dir)))
        {
            var path = Path.Combine(AppDataLocalLow, dir);
            VerifyPath(path, ref valid, ref invalid, ref missing);
        }

        // Check Special
        foreach (var symlink in Config.Symlinks.SpecialSymlinks)
        {
            var path = ExpandEnvironmentVariables(symlink.Source);
            VerifyPath(path, ref valid, ref invalid, ref missing, symlink.IsDirectory);
        }

        Console.WriteLine($"\nSummary:");
        Console.WriteLine($"  Valid symlinks: {valid}");
        Console.WriteLine($"  Invalid (not symlinks): {invalid}");
        Console.WriteLine($"  Missing: {missing}");

        return Task.CompletedTask;
    }

    private static void VerifyPath(string path, ref int valid, ref int invalid, ref int missing, bool isDirectory = true)
    {
        try
        {
            if (isDirectory)
            {
                if (!Directory.Exists(path))
                {
                    Console.WriteLine($"  [MISSING] {path}");
                    missing++;
                }
                else
                {
                    var info = new DirectoryInfo(path);
                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        Console.WriteLine($"  [OK] {path}");
                        valid++;
                    }
                    else
                    {
                        Console.WriteLine($"  [NOT SYMLINK] {path}");
                        invalid++;
                    }
                }
            }
            else
            {
                if (!File.Exists(path))
                {
                    Console.WriteLine($"  [MISSING] {path}");
                    missing++;
                }
                else
                {
                    var info = new FileInfo(path);
                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        Console.WriteLine($"  [OK] {path}");
                        valid++;
                    }
                    else
                    {
                        Console.WriteLine($"  [NOT SYMLINK] {path}");
                        invalid++;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [ERROR] {path}: {ex.Message}");
            invalid++;
        }
    }
}
