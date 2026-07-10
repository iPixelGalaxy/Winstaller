using System.Diagnostics;
using Microsoft.Win32;
using Winstaller.Configuration;
using Winstaller.Models;

namespace Winstaller.Utilities;

public enum SystemInfoImportScope
{
    All,
    Path,
    NetworkDrives,
    ShellFolders,
    AppInstaller,
    Fonts,
    Startup,
    Symlinks
}

public sealed record SystemInfoImportCandidate(
    SystemInfoImportScope Scope,
    string Title,
    string Detail,
    object Value,
    string Group = "");

public enum SymlinkImportMode
{
    Copy,
    Move
}

public sealed record SystemInfoImportResult(int Added, IReadOnlyList<SymlinkImportFailure> SymlinkFailures);
public sealed record SymlinkImportFailure(SystemInfoImportCandidate Candidate, string Title, IReadOnlyList<string> FailedPaths, string Message);

public static class SystemInfoImportService
{
    private static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string AppDataRoaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string AppDataLocal => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string AppDataLocalLow => Path.Combine(UserProfile, "AppData", "LocalLow");
    private const string BackupSuffix = ".winstaller-backup";

    public static async Task<List<SystemInfoImportCandidate>> FindCandidatesAsync(WinstallerConfig config, SystemInfoImportScope scope)
    {
        var candidates = new List<SystemInfoImportCandidate>();

        if (scope is SystemInfoImportScope.All or SystemInfoImportScope.Path)
            candidates.AddRange(FindPathCandidates(config));

        if (scope is SystemInfoImportScope.All or SystemInfoImportScope.NetworkDrives)
            candidates.AddRange(FindNetworkDriveCandidates(config));

        if (scope is SystemInfoImportScope.All or SystemInfoImportScope.ShellFolders)
            candidates.AddRange(FindShellFolderCandidates(config));

        if (scope is SystemInfoImportScope.All or SystemInfoImportScope.AppInstaller)
            candidates.AddRange(await FindWingetCandidatesAsync(config));

        if (scope is SystemInfoImportScope.All or SystemInfoImportScope.Fonts)
            candidates.AddRange(FindFontCandidates(config));

        if (scope is SystemInfoImportScope.All or SystemInfoImportScope.Startup)
            candidates.AddRange(FindStartupCandidates(config));

        if (scope is SystemInfoImportScope.All or SystemInfoImportScope.Symlinks)
            candidates.AddRange(FindSymlinkCandidates(config));

        return candidates;
    }

    public static int ApplyCandidates(
        WinstallerConfig config,
        IEnumerable<SystemInfoImportCandidate> candidates,
        SymlinkImportMode symlinkMode = SymlinkImportMode.Copy,
        Action<string>? log = null,
        Action<SystemInfoImportCandidate>? applied = null)
    {
        return ApplyCandidatesWithResult(config, candidates, symlinkMode, log, applied).Added;
    }

    public static SystemInfoImportResult ApplyCandidatesWithResult(
        WinstallerConfig config,
        IEnumerable<SystemInfoImportCandidate> candidates,
        SymlinkImportMode symlinkMode = SymlinkImportMode.Copy,
        Action<string>? log = null,
        Action<SystemInfoImportCandidate>? applied = null)
    {
        var added = 0;
        var failures = new List<SymlinkImportFailure>();
        foreach (var candidate in candidates)
        {
            log?.Invoke($"Applying {candidate.Scope}: {candidate.Title}");
            switch (candidate.Value)
            {
                case string path when candidate.Scope == SystemInfoImportScope.Path:
                    if (!config.Path.Additions.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        config.Path.Additions.Add(path);
                        added++;
                    }
                    break;

                case NetworkDriveMapping drive:
                    if (!config.NetworkDrives.Drives.Any(existing =>
                            existing.DriveLetter.Equals(drive.DriveLetter, StringComparison.OrdinalIgnoreCase)))
                    {
                        config.NetworkDrives.Drives.Add(drive);
                        added++;
                    }
                    break;

                case ShellFolderMapping folder:
                    if (!config.ShellFolders.Folders.Any(existing =>
                            existing.RegistryValue.Equals(folder.RegistryValue, StringComparison.OrdinalIgnoreCase)))
                    {
                        config.ShellFolders.Folders.Add(folder);
                        added++;
                    }
                    break;

                case string packageId when candidate.Scope == SystemInfoImportScope.AppInstaller:
                    if (!GetConfiguredPackageIds(config).Contains(packageId))
                    {
                        config.AppInstaller.DefaultInstalls.Add(packageId);
                        added++;
                    }
                    break;

                case string fontFile when candidate.Scope == SystemInfoImportScope.Fonts:
                    if (CopyFontToBackup(config, fontFile))
                        added++;
                    break;

                case StartupProgram startup:
                    if (!config.Startup.Programs.Any(existing =>
                            existing.Name.Equals(startup.Name, StringComparison.OrdinalIgnoreCase) &&
                            existing.MachineLevel == startup.MachineLevel))
                    {
                        config.Startup.Programs.Add(startup);
                        added++;
                    }
                    break;

                case SymlinkImport symlink:
                    var list = symlink.Section switch
                    {
                        "Roaming" => config.Symlinks.RoamingDirectories,
                        "Local" => config.Symlinks.LocalDirectories,
                        "LocalLow" => config.Symlinks.LocalLowDirectories,
                        _ => null
                    };

                    if (string.IsNullOrWhiteSpace(symlink.Name))
                    {
                        log?.Invoke("Skipped blank symlink entry.");
                        break;
                    }

                    if (!SymlinkSafetyPolicy.IsSafeAppDataRelativePath(symlink.Section, symlink.Name, out var unsafeReason))
                    {
                        log?.Invoke($"Blocked unsafe symlink path: [{symlink.Section}] {symlink.Name} ({unsafeReason})");
                        failures.Add(new SymlinkImportFailure(
                            candidate,
                            candidate.Title,
                            [symlink.SourcePath],
                            $"Blocked unsafe symlink path: {unsafeReason}"));
                        break;
                    }

                    if (list is not null &&
                        (!list.Contains(symlink.Name, StringComparer.OrdinalIgnoreCase) ||
                         symlink.Kind is SymlinkImportKind.BackupRecovery or SymlinkImportKind.BackupRollback ||
                         symlink.Kind is SymlinkImportKind.MissingConfiguredSymlink))
                    {
                        var result = MigrateSymlinkData(config, symlink, symlinkMode, log);
                        if (result.Success)
                        {
                            if (result.AddToConfig && !list.Contains(symlink.Name, StringComparer.OrdinalIgnoreCase))
                                list.Add(symlink.Name);
                            added++;
                            applied?.Invoke(candidate);
                        }
                        else
                        {
                            failures.Add(new SymlinkImportFailure(
                                candidate,
                                candidate.Title,
                                result.FailedPaths.Count == 0 ? [symlink.SourcePath] : result.FailedPaths,
                                result.Message));
                        }
                    }
                    break;
            }
        }

        return new SystemInfoImportResult(added, failures);
    }

    public static void IgnoreCandidates(WinstallerConfig config, IEnumerable<SystemInfoImportCandidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            switch (candidate.Value)
            {
                case string fontFile when candidate.Scope == SystemInfoImportScope.Fonts:
                    var fontName = Path.GetFileName(fontFile);
                    if (!config.Fonts.IgnoredFonts.Contains(fontName, StringComparer.OrdinalIgnoreCase))
                        config.Fonts.IgnoredFonts.Add(fontName);
                    break;

                case SymlinkImport symlink:
                    var ignored = symlink.Section switch
                    {
                        "Roaming" => config.Symlinks.IgnoredRoamingDirectories,
                        "Local" => config.Symlinks.IgnoredLocalDirectories,
                        "LocalLow" => config.Symlinks.IgnoredLocalLowDirectories,
                        _ => null
                    };

                    if (ignored is not null && !ignored.Contains(symlink.Name, StringComparer.OrdinalIgnoreCase))
                        ignored.Add(symlink.Name);
                    break;
            }
        }
    }

    public static void UnignoreCandidates(WinstallerConfig config, IEnumerable<SystemInfoImportCandidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (candidate.Value is not SymlinkImport symlink)
                continue;

            var ignored = symlink.Section switch
            {
                "Roaming" => config.Symlinks.IgnoredRoamingDirectories,
                "Local" => config.Symlinks.IgnoredLocalDirectories,
                "LocalLow" => config.Symlinks.IgnoredLocalLowDirectories,
                _ => null
            };

            ignored?.RemoveAll(item => item.Equals(symlink.Name, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static IEnumerable<SystemInfoImportCandidate> FindPathCandidates(WinstallerConfig config)
    {
        var entries = new List<string>();
        entries.AddRange((Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        entries.AddRange((Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var configured = config.Path.Additions
            .Select(Environment.ExpandEnvironmentVariables)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return entries
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(entry => !configured.Contains(entry))
            .Select(entry => new SystemInfoImportCandidate(
                SystemInfoImportScope.Path,
                entry,
                Directory.Exists(entry) ? "Folder exists" : "Folder missing",
                entry));
    }

    private static IEnumerable<SystemInfoImportCandidate> FindNetworkDriveCandidates(WinstallerConfig config)
    {
        var configured = config.NetworkDrives.Drives
            .Select(drive => drive.DriveLetter)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, NetworkDriveMapping>(StringComparer.OrdinalIgnoreCase);

        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Network))
        {
            var letter = drive.Name.TrimEnd('\\', ':');
            if (configured.Contains(letter))
                continue;

            var path = GetNetworkPath(letter);
            if (string.IsNullOrWhiteSpace(path))
                continue;

            found[letter] = new NetworkDriveMapping
            {
                DriveLetter = letter,
                NetworkPath = path,
                Label = drive.VolumeLabel ?? string.Empty,
                Persistent = true,
                DeleteFirst = true
            };
        }

        foreach (var mapping in GetNetUseMappings())
        {
            if (!configured.Contains(mapping.DriveLetter))
                found.TryAdd(mapping.DriveLetter, mapping);
        }

        foreach (var mapping in found.Values.OrderBy(drive => drive.DriveLetter, StringComparer.OrdinalIgnoreCase))
        {
            yield return new SystemInfoImportCandidate(
                SystemInfoImportScope.NetworkDrives,
                $"{mapping.DriveLetter}: -> {mapping.NetworkPath}",
                string.IsNullOrWhiteSpace(mapping.Label) ? "Network drive" : mapping.Label,
                mapping);
        }
    }

    private static IEnumerable<SystemInfoImportCandidate> FindShellFolderCandidates(WinstallerConfig config)
    {
        var knownFolders = new Dictionary<string, (string RegValue, string FriendlyName)>
        {
            { "Desktop", ("Desktop", "Desktop") },
            { "Downloads", ("{374DE290-123F-4565-9164-39C4925E467B}", "Downloads") },
            { "Documents", ("Personal", "Documents") },
            { "Pictures", ("My Pictures", "Pictures") },
            { "Music", ("My Music", "Music") },
            { "Videos", ("My Video", "Videos") }
        };

        var configured = config.ShellFolders.Folders
            .Select(folder => folder.RegistryValue)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
        if (key is null)
            yield break;

        foreach (var (_, (regValue, friendlyName)) in knownFolders)
        {
            if (configured.Contains(regValue))
                continue;

            var path = key.GetValue(regValue) as string ?? string.Empty;
            if (string.IsNullOrWhiteSpace(path))
                continue;

            var mapping = new ShellFolderMapping
            {
                FolderName = friendlyName,
                RegistryValue = regValue,
                Path = path
            };

            yield return new SystemInfoImportCandidate(
                SystemInfoImportScope.ShellFolders,
                friendlyName,
                path,
                mapping);
        }
    }

    private static async Task<IEnumerable<SystemInfoImportCandidate>> FindWingetCandidatesAsync(WinstallerConfig config)
    {
        var result = await RunWingetListAsync();
        if (result.ExitCode != 0)
            return [];

        var configured = GetConfiguredPackageIds(config);
        return ParseWingetListOutput(result.Output)
            .Where(package => IsInstallableWingetId(package.Id))
            .Where(package => !configured.Contains(package.Id))
            .OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
            .Select(package => new SystemInfoImportCandidate(
                SystemInfoImportScope.AppInstaller,
                package.Name,
                $"{package.Id} {package.Version}".Trim(),
                package.Id))
            .ToList();
    }

    public static IEnumerable<SystemInfoImportCandidate> GetRecommendedAppCandidates(WinstallerConfig config)
    {
        string[] recommended =
        [
            "Git.Git",
            "Discord.Discord",
            "Spotify.Spotify"
        ];
        var configured = GetConfiguredPackageIds(config);
        return recommended
            .Where(id => !configured.Contains(id))
            .Select(id => new SystemInfoImportCandidate(
                SystemInfoImportScope.AppInstaller,
                id,
                "Recommended app",
                id,
                "Recommended"));
    }

    private static IEnumerable<SystemInfoImportCandidate> FindFontCandidates(WinstallerConfig config)
    {
        var windowsFonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        if (!Directory.Exists(windowsFonts))
            yield break;

        var backupDir = Environment.ExpandEnvironmentVariables(config.Fonts.FontsDirectory)
            .Replace("{USERNAME}", Environment.UserName);
        var backedUp = Directory.Exists(backupDir)
            ? Directory.GetFiles(backupDir, "*.*")
                .Where(IsFontFile)
                .Select(Path.GetFileName)
                .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
                .Select(fileName => fileName!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var font in Directory.GetFiles(windowsFonts, "*.*").Where(IsFontFile))
        {
            var fileName = Path.GetFileName(font);
            if (backedUp.Contains(fileName) ||
                config.Fonts.IgnoredFonts.Contains(fileName, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return new SystemInfoImportCandidate(
                SystemInfoImportScope.Fonts,
                Path.GetFileNameWithoutExtension(font),
                fileName,
                font);
        }
    }

    private static IEnumerable<SystemInfoImportCandidate> FindStartupCandidates(WinstallerConfig config)
    {
        foreach (var candidate in ReadStartupKey(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", false))
        {
            if (!HasStartupProgram(config, candidate.Name, candidate.MachineLevel))
            {
                yield return new SystemInfoImportCandidate(
                    SystemInfoImportScope.Startup,
                    candidate.Name,
                    candidate.Path,
                    candidate);
            }
        }

        foreach (var candidate in ReadStartupKey(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
        {
            if (!HasStartupProgram(config, candidate.Name, candidate.MachineLevel))
            {
                yield return new SystemInfoImportCandidate(
                    SystemInfoImportScope.Startup,
                    candidate.Name,
                    candidate.Path,
                    candidate);
            }
        }
    }

    private static IEnumerable<SystemInfoImportCandidate> FindSymlinkCandidates(WinstallerConfig config)
    {
        foreach (var symlink in FindSymlinks(AppDataRoaming, "Roaming", config.Symlinks.RoamingDirectories, config.Symlinks.IgnoredRoamingDirectories, config))
            yield return symlink;

        foreach (var symlink in FindSymlinks(AppDataLocal, "Local", config.Symlinks.LocalDirectories, config.Symlinks.IgnoredLocalDirectories, config))
            yield return symlink;

        foreach (var symlink in FindSymlinks(AppDataLocalLow, "LocalLow", config.Symlinks.LocalLowDirectories, config.Symlinks.IgnoredLocalLowDirectories, config))
            yield return symlink;
    }

    private static IEnumerable<SystemInfoImportCandidate> FindSymlinks(
        string appDataPath,
        string section,
        List<string> configured,
        List<string> ignored,
        WinstallerConfig config)
    {
        if (!Directory.Exists(appDataPath))
            yield break;

        foreach (var candidate in FindSymlinksRecursive(appDataPath, appDataPath, section, configured, ignored, config, 0))
            yield return candidate;
    }

    private static IEnumerable<SystemInfoImportCandidate> FindSymlinksRecursive(
        string rootPath,
        string currentPath,
        string section,
        List<string> configured,
        List<string> ignored,
        WinstallerConfig config,
        int depth)
    {
        IEnumerable<string> directories;
        try
        {
            directories = Directory.GetDirectories(currentPath);
        }
        catch
        {
            yield break;
        }

        foreach (var dir in directories)
        {
            var leafName = Path.GetFileName(dir);
            var relativePath = SymlinkSafetyPolicy.NormalizeRelativePath(Path.GetRelativePath(rootPath, dir));
            var info = new DirectoryInfo(dir);
            if (SymlinkSafetyPolicy.IsWindowsShellJunction(info) ||
                !SymlinkSafetyPolicy.IsSafeAppDataRelativePath(section, relativePath, out _))
                continue;

            if (leafName.EndsWith(BackupSuffix, StringComparison.OrdinalIgnoreCase))
            {
                var originalName = leafName[..^BackupSuffix.Length];
                var parentRelative = Path.GetDirectoryName(relativePath);
                var originalRelativePath = string.IsNullOrWhiteSpace(parentRelative)
                    ? originalName
                    : Path.Combine(parentRelative, originalName);
                var originalPath = Path.Combine(rootPath, originalRelativePath);
                var destination = GetSymlinkDestination(config, section, originalRelativePath);
                var hasDestination = Directory.Exists(destination);

                yield return new SystemInfoImportCandidate(
                    SystemInfoImportScope.Symlinks,
                    $"[{section}] {originalRelativePath}",
                    hasDestination
                        ? $"Recover missing symlink from {destination}"
                        : $"Restore original folder from {dir}",
                    new SymlinkImport(
                        section,
                        originalRelativePath,
                        originalPath,
                        false,
                        dir,
                        hasDestination ? SymlinkImportKind.BackupRecovery : SymlinkImportKind.BackupRollback),
                    "Recovery Needed");
                continue;
            }

            var existingSymlink = info.Attributes.HasFlag(FileAttributes.ReparsePoint);
            var target = existingSymlink ? info.LinkTarget ?? "(unknown target)" : dir;
            var isConfigured = configured.Contains(relativePath, StringComparer.OrdinalIgnoreCase);
            var isIgnored = ignored.Contains(relativePath, StringComparer.OrdinalIgnoreCase);
            var isExcluded = config.AppDataUtility.ExcludedDirectories.Contains(leafName, StringComparer.OrdinalIgnoreCase);

            if (existingSymlink && !Path.IsPathRooted(target) && target != "(unknown target)")
            {
                target = Path.GetFullPath(target, Path.GetDirectoryName(dir) ?? currentPath);
            }

            if (!isConfigured && (existingSymlink || (depth == 0 && !isExcluded)))
            {
                yield return new SystemInfoImportCandidate(
                    SystemInfoImportScope.Symlinks,
                    $"[{section}] {relativePath}",
                    target,
                    new SymlinkImport(section, relativePath, dir, existingSymlink, target, existingSymlink ? SymlinkImportKind.ExistingSymlink : SymlinkImportKind.Normal),
                    isIgnored ? "Ignored" : existingSymlink ? "Existing Symlinks" : "Folders Not Yet Symlinked");
            }

            if (!existingSymlink)
            {
                foreach (var child in FindSymlinksRecursive(rootPath, dir, section, configured, ignored, config, depth + 1))
                    yield return child;
            }
        }

        foreach (var configuredName in configured.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var safeName = SymlinkSafetyPolicy.NormalizeRelativePath(configuredName);
            if (!SymlinkSafetyPolicy.IsSafeAppDataRelativePath(section, safeName, out _))
                continue;

            var sourcePath = Path.Combine(rootPath, safeName);
            if (Directory.Exists(sourcePath))
                continue;

            var destination = GetSymlinkDestination(config, section, safeName);
            if (!Directory.Exists(destination))
                continue;

            yield return new SystemInfoImportCandidate(
                SystemInfoImportScope.Symlinks,
                $"[{section}] {safeName}",
                $"Repair missing configured symlink to {destination}",
                new SymlinkImport(
                    section,
                    safeName,
                    sourcePath,
                    false,
                    destination,
                    SymlinkImportKind.MissingConfiguredSymlink),
                "Recovery Needed");
        }
    }

    private static bool CopyFontToBackup(WinstallerConfig config, string fontFile)
    {
        if (!File.Exists(fontFile))
            return false;

        var backupDir = Environment.ExpandEnvironmentVariables(config.Fonts.FontsDirectory)
            .Replace("{USERNAME}", Environment.UserName);
        Directory.CreateDirectory(backupDir);
        File.Copy(fontFile, Path.Combine(backupDir, Path.GetFileName(fontFile)), overwrite: true);
        return true;
    }

    private static bool IsFontFile(string path)
    {
        return path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<LockingProcessInfo> FindLockingProcesses(IEnumerable<string> paths) => SymlinkLockUtility.FindLockingProcesses(paths);

    private static SymlinkMigrationResult MigrateSymlinkData(WinstallerConfig config, SymlinkImport symlink, SymlinkImportMode mode, Action<string>? log)
    {
        if (!SymlinkSafetyPolicy.IsSafeAppDataRelativePath(symlink.Section, symlink.Name, out var unsafeReason))
        {
            log?.Invoke($"Blocked unsafe symlink path: [{symlink.Section}] {symlink.Name} ({unsafeReason})");
            return SymlinkMigrationResult.Fail($"Blocked unsafe symlink path: {unsafeReason}", [symlink.SourcePath]);
        }

        if (symlink.Kind is SymlinkImportKind.BackupRecovery or SymlinkImportKind.BackupRollback)
            return RecoverBackupSymlink(config, symlink, log);

        var dataSource = symlink.IsExistingSymlink && Directory.Exists(symlink.ExistingTargetPath)
            ? symlink.ExistingTargetPath
            : symlink.SourcePath;
        if (symlink.Kind is SymlinkImportKind.MissingConfiguredSymlink)
        {
            var repairDestination = GetSymlinkDestination(config, symlink.Section, symlink.Name);
            if (!Directory.Exists(repairDestination))
            {
                log?.Invoke($"Skipped missing symlink repair because target is missing: {repairDestination}");
                return SymlinkMigrationResult.Fail($"Target missing: {repairDestination}", [repairDestination]);
            }

            try
            {
                log?.Invoke($"Repairing missing symlink: {symlink.SourcePath} -> {repairDestination}");
                CreateDirectorySymlink(symlink.SourcePath, repairDestination, log);
                return SymlinkMigrationResult.Ok(addToConfig: false);
            }
            catch (Exception ex)
            {
                log?.Invoke($"Failed repairing missing symlink: {symlink.SourcePath} ({ex.Message})");
                return SymlinkMigrationResult.Fail($"Symlink repair failed: {ex.Message}", [symlink.SourcePath, repairDestination]);
            }
        }

        if (!Directory.Exists(dataSource))
        {
            log?.Invoke($"Skipped missing source: {dataSource}");
            return SymlinkMigrationResult.Fail($"Missing source: {dataSource}", [dataSource]);
        }

        var rootPath = symlink.Section switch
        {
            "Roaming" => AppDataRoaming,
            "Local" => AppDataLocal,
            "LocalLow" => AppDataLocalLow,
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(rootPath) ||
            !SymlinkSafetyPolicy.IsSafeAppDataPath(rootPath, symlink.SourcePath, symlink.Section, out unsafeReason))
        {
            log?.Invoke($"Blocked unsafe symlink source: {symlink.SourcePath} ({unsafeReason})");
            return SymlinkMigrationResult.Fail($"Blocked unsafe symlink source: {unsafeReason}", [symlink.SourcePath]);
        }

        var destination = GetSymlinkDestination(config, symlink.Section, symlink.Name);
        var backupPath = symlink.SourcePath + BackupSuffix;
        if (!PreflightSymlinkPaths(config, log, dataSource, symlink.SourcePath, destination, backupPath))
            return SymlinkMigrationResult.Fail("Locked by running app.", [dataSource, symlink.SourcePath, destination]);

        log?.Invoke($"{mode} {dataSource} -> {destination}");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? destination);

            if (Directory.Exists(destination))
            {
                log?.Invoke($"Removing existing destination: {destination}");
                Directory.Delete(destination, true);
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Failed preparing destination: {destination} ({ex.Message})");
            return SymlinkMigrationResult.Fail($"Failed preparing destination: {ex.Message}", [destination]);
        }

        if (mode == SymlinkImportMode.Move)
        {
            try
            {
                Directory.Move(dataSource, destination);
            }
            catch (Exception ex)
            {
                log?.Invoke($"Move failed, falling back to copy/delete. ({ex.Message})");
                var copyResult = CopyDirectory(dataSource, destination, log);
                if (!copyResult.Success)
                {
                    log?.Invoke($"Skipped symlink item because copy was incomplete: {symlink.Name}");
                    CleanupFailedDestination(destination, log);
                    return SymlinkMigrationResult.Fail($"Copy incomplete: {copyResult.Message}", copyResult.FailedPaths);
                }

                try
                {
                    Directory.Delete(dataSource, true);
                }
                catch (Exception deleteEx)
                {
                    log?.Invoke($"Skipped symlink item because source delete failed: {dataSource} ({deleteEx.Message})");
                    CleanupFailedDestination(destination, log);
                    return SymlinkMigrationResult.Fail($"Source delete failed: {deleteEx.Message}", [dataSource]);
                }
            }
        }
        else
        {
            var copyResult = CopyDirectory(dataSource, destination, log);
            if (!copyResult.Success)
            {
                log?.Invoke($"Skipped symlink item because copy was incomplete: {symlink.Name}");
                CleanupFailedDestination(destination, log);
                return SymlinkMigrationResult.Fail($"Copy incomplete: {copyResult.Message}", copyResult.FailedPaths);
            }
        }

        try
        {
            if (Directory.Exists(symlink.SourcePath))
            {
                if (symlink.IsExistingSymlink)
                {
                    Directory.Delete(symlink.SourcePath);
                }
                else if (mode == SymlinkImportMode.Copy && config.Symlinks.CreateBackupFolders)
                {
                    if (Directory.Exists(backupPath))
                        Directory.Delete(backupPath, true);
                    log?.Invoke($"Moving original to backup: {backupPath}");
                    Directory.Move(symlink.SourcePath, backupPath);
                }
                else if (mode == SymlinkImportMode.Copy)
                {
                    log?.Invoke($"Deleting original after successful copy: {symlink.SourcePath}");
                    Directory.Delete(symlink.SourcePath, true);
                }
            }

            log?.Invoke($"Creating symlink: {symlink.SourcePath} -> {destination}");
            CreateDirectorySymlink(symlink.SourcePath, destination, log);
            return SymlinkMigrationResult.Ok();
        }
        catch (Exception ex)
        {
            log?.Invoke($"Failed finalizing symlink item: {symlink.Name} ({ex.Message})");
            CleanupFailedDestination(destination, log);
            return SymlinkMigrationResult.Fail($"Finalize failed: {ex.Message}", [symlink.SourcePath]);
        }
    }

    private static bool PreflightSymlinkPaths(WinstallerConfig config, Action<string>? log, params string[] paths)
    {
        return SymlinkLockUtility.ClearLocks(
            paths,
            config.Symlinks.ForceKillApps,
            message => log?.Invoke(message));
    }

    private static SymlinkMigrationResult RecoverBackupSymlink(WinstallerConfig config, SymlinkImport symlink, Action<string>? log)
    {
        var backupPath = symlink.ExistingTargetPath;
        var destination = GetSymlinkDestination(config, symlink.Section, symlink.Name);

        if (Directory.Exists(symlink.SourcePath))
        {
            log?.Invoke($"Skipped backup recovery because original path already exists: {symlink.SourcePath}");
            return SymlinkMigrationResult.Fail($"Original path already exists: {symlink.SourcePath}", [symlink.SourcePath, backupPath]);
        }

        if (!Directory.Exists(backupPath))
        {
            log?.Invoke($"Skipped backup recovery because backup is missing: {backupPath}");
            return SymlinkMigrationResult.Fail($"Backup missing: {backupPath}", [backupPath]);
        }

        if (!Directory.Exists(destination))
        {
            try
            {
                log?.Invoke($"Restoring original folder from backup: {backupPath} -> {symlink.SourcePath}");
                Directory.Move(backupPath, symlink.SourcePath);
                return SymlinkMigrationResult.Ok(addToConfig: false);
            }
            catch (Exception ex)
            {
                log?.Invoke($"Failed restoring original folder from backup: {backupPath} ({ex.Message})");
                return SymlinkMigrationResult.Fail($"Restore failed: {ex.Message}", [backupPath, symlink.SourcePath]);
            }
        }

        try
        {
            log?.Invoke($"Recovering missing symlink: {symlink.SourcePath} -> {destination}");
            CreateDirectorySymlink(symlink.SourcePath, destination, log);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Failed recovering symlink: {symlink.SourcePath} ({ex.Message})");
            return SymlinkMigrationResult.Fail($"Symlink recovery failed: {ex.Message}", [symlink.SourcePath, destination]);
        }

        try
        {
            log?.Invoke($"Removing recovered backup folder: {backupPath}");
            Directory.Delete(backupPath, true);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Recovered symlink but failed to remove backup folder: {backupPath} ({ex.Message})");
        }

        return SymlinkMigrationResult.Ok();
    }

    private static string GetSymlinkDestination(WinstallerConfig config, string section, string name)
    {
        var baseDir = Environment.ExpandEnvironmentVariables(config.Symlinks.BaseSymlinkDirectory)
            .Replace("{USERNAME}", Environment.UserName);
        return Path.Combine(baseDir, "AppData", section, name);
    }

    private static CopyDirectoryResult CopyDirectory(string source, string destination, Action<string>? log)
    {
        var failedPaths = new List<string>();
        if (IsReparseDirectory(source))
        {
            log?.Invoke($"Skipped reparse directory: {source}");
            return CopyDirectoryResult.Fail([source], "Source is a reparse directory.");
        }

        try
        {
            Directory.CreateDirectory(destination);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Failed creating directory: {destination} ({ex.Message})");
            return CopyDirectoryResult.Fail([destination], ex.Message);
        }

        try
        {
            foreach (var file in Directory.GetFiles(source))
            {
                try
                {
                    File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
                }
                catch (Exception ex)
                {
                    failedPaths.Add(file);
                    log?.Invoke($"Skipped locked or unavailable file: {file} ({ex.Message})");
                }
            }
        }
        catch (Exception ex)
        {
            failedPaths.Add(source);
            log?.Invoke($"Skipped locked or unavailable directory: {source} ({ex.Message})");
        }

        try
        {
            foreach (var directory in Directory.GetDirectories(source))
            {
                if (IsReparseDirectory(directory))
                {
                    log?.Invoke($"Skipped reparse directory: {directory}");
                    continue;
                }

                var target = Path.Combine(destination, Path.GetFileName(directory));
                failedPaths.AddRange(CopyDirectory(directory, target, log).FailedPaths);
            }
        }
        catch (Exception ex)
        {
            failedPaths.Add(source);
            log?.Invoke($"Skipped locked or unavailable directory children: {source} ({ex.Message})");
        }

        return failedPaths.Count == 0
            ? CopyDirectoryResult.Ok()
            : CopyDirectoryResult.Fail(failedPaths, $"{failedPaths.Count} path(s) failed.");
    }

    private static void CleanupFailedDestination(string destination, Action<string>? log)
    {
        try
        {
            if (Directory.Exists(destination))
            {
                log?.Invoke($"Cleaning incomplete destination: {destination}");
                Directory.Delete(destination, true);
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Failed cleaning incomplete destination: {destination} ({ex.Message})");
        }
    }


    private static bool IsReparseDirectory(string path)
    {
        try
        {
            return new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return true;
        }
    }

    private static void CreateDirectorySymlink(string source, string target, Action<string>? log)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(source) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /D \"{source}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        if (process is null)
            throw new InvalidOperationException("Failed to start mklink.");

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (!string.IsNullOrWhiteSpace(output))
            log?.Invoke($"mklink output: {output.Trim()}");
        if (!string.IsNullOrWhiteSpace(error))
            log?.Invoke($"mklink error: {error.Trim()}");
        if (process.ExitCode != 0)
            throw new IOException($"mklink failed with exit code {process.ExitCode}.");

        VerifyDirectorySymlink(source, target);
    }

    private static void VerifyDirectorySymlink(string source, string target)
    {
        var info = new DirectoryInfo(source);
        if (!info.Exists)
            throw new IOException($"Symlink source was not created: {source}");

        if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException($"Symlink source is not a reparse point: {source}");

        if (!Directory.Exists(target))
            throw new IOException($"Symlink target is missing: {target}");

        var linkTarget = info.LinkTarget;
        if (string.IsNullOrWhiteSpace(linkTarget))
            throw new IOException($"Could not read symlink target for: {source}");

        var normalizedLinkTarget = Path.GetFullPath(linkTarget, Path.GetDirectoryName(source) ?? Environment.CurrentDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedTarget = Path.GetFullPath(target)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!normalizedLinkTarget.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Symlink target mismatch: {source} -> {linkTarget}, expected {target}");
    }

    private static IEnumerable<StartupProgram> ReadStartupKey(RegistryKey root, string path, bool machineLevel)
    {
        using var key = root.OpenSubKey(path);
        if (key is null)
            yield break;

        foreach (var name in key.GetValueNames())
        {
            var value = key.GetValue(name)?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(value))
                continue;

            yield return new StartupProgram
            {
                Name = name,
                Path = value,
                Arguments = string.Empty,
                MachineLevel = machineLevel
            };
        }
    }

    private static bool HasStartupProgram(WinstallerConfig config, string name, bool machineLevel)
    {
        return config.Startup.Programs.Any(existing =>
            existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
            existing.MachineLevel == machineLevel);
    }

    private static HashSet<string> GetConfiguredPackageIds(WinstallerConfig config)
    {
        var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var packageId in config.AppInstaller.PreparedInstallers)
            packageIds.Add(packageId);
        foreach (var packageId in config.AppInstaller.ManualInstalls)
            packageIds.Add(packageId);
        foreach (var packageId in config.AppInstaller.DefaultInstalls)
            packageIds.Add(packageId);
        foreach (var script in config.AppInstaller.CustomScripts)
            if (!string.IsNullOrWhiteSpace(script.Name))
                packageIds.Add(script.Name);

        return packageIds;
    }

    private static string GetNetworkPath(string driveLetter)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Network\{driveLetter}");
            if (key is not null)
                return key.GetValue("RemotePath") as string ?? string.Empty;
        }
        catch
        {
        }

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
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

            foreach (var line in output.Split('\n'))
            {
                if (!line.Trim().StartsWith("Remote name", StringComparison.OrdinalIgnoreCase))
                    continue;

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                    return parts[2].Trim();
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static IEnumerable<NetworkDriveMapping> GetNetUseMappings()
    {
        string output;
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "net",
                Arguments = "use",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            process.Start();
            output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
        }
        catch
        {
            yield break;
        }

        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var drive = parts.FirstOrDefault(part => part.Length == 2 && part[1] == ':');
            var remote = parts.FirstOrDefault(part => part.StartsWith(@"\\", StringComparison.Ordinal));
            if (drive is null || remote is null)
                continue;

            yield return new NetworkDriveMapping
            {
                DriveLetter = drive.TrimEnd(':'),
                NetworkPath = remote,
                Persistent = true,
                DeleteFirst = true
            };
        }
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
    private sealed record SymlinkImport(string Section, string Name, string SourcePath, bool IsExistingSymlink, string ExistingTargetPath, SymlinkImportKind Kind);
    private enum SymlinkImportKind
    {
        Normal,
        ExistingSymlink,
        BackupRecovery,
        BackupRollback,
        MissingConfiguredSymlink
    }

    private sealed record SymlinkMigrationResult(bool Success, IReadOnlyList<string> FailedPaths, string Message, bool AddToConfig)
    {
        public static SymlinkMigrationResult Ok(bool addToConfig = true) => new(true, [], string.Empty, addToConfig);
        public static SymlinkMigrationResult Fail(IReadOnlyList<string> failedPaths, string message) => new(false, failedPaths, message, false);
        public static SymlinkMigrationResult Fail(string message, IReadOnlyList<string> failedPaths) => new(false, failedPaths, message, false);
    }

    private sealed record CopyDirectoryResult(bool Success, IReadOnlyList<string> FailedPaths, string Message)
    {
        public static CopyDirectoryResult Ok() => new(true, [], string.Empty);
        public static CopyDirectoryResult Fail(IReadOnlyList<string> failedPaths, string message) => new(false, failedPaths, message);
    }
}
