using Winstaller.Models;

namespace Winstaller.Utilities;

public sealed record PreReinstallFileBackupResult(int Updated, int Removed, IReadOnlyList<string> Warnings);

public static class PreReinstallChecklistService
{
    public static PreReinstallFileBackupResult RefreshFileBackups(FileCopyConfig config, Action<string>? log = null)
    {
        var warnings = new List<string>();
        var updated = 0;
        var removed = 0;
        var operations = config.Operations.Where(operation => !operation.SkipPreReinstallBackup).ToList();

        foreach (var group in operations.GroupBy(operation => Expand(operation.Source), StringComparer.OrdinalIgnoreCase))
        {
            var available = group.FirstOrDefault(operation => DestinationExists(operation));
            if (available is null)
            {
                warnings.Add($"No current destination found for {group.First().NameOrSource()}; kept managed backup.");
                continue;
            }

            if (group.Count() > 1)
                log?.Invoke($"Using {available.NameOrSource()} as backup source; skipped {group.Count() - 1} duplicate destination(s).");

            try
            {
                var result = available.MatchingFiles
                    ? BackupMatchingFiles(available, log)
                    : BackupSingleFile(available, log);
                updated += result.Updated;
                removed += result.Removed;
                warnings.AddRange(result.Warnings);
            }
            catch (Exception ex)
            {
                warnings.Add($"{available.NameOrSource()}: {ex.Message}");
            }
        }

        return new PreReinstallFileBackupResult(updated, removed, warnings);
    }

    private static PreReinstallFileBackupResult BackupSingleFile(FileCopyOperation operation, Action<string>? log)
    {
        var source = Expand(operation.Source);
        var destination = Expand(operation.Destination);
        if (!File.Exists(destination))
            return new(0, 0, [$"{operation.NameOrSource()}: destination file missing; kept managed backup."]);

        CopyAtomically(destination, source);
        log?.Invoke($"Updated {operation.NameOrSource()}.");
        return new(1, 0, []);
    }

    private static PreReinstallFileBackupResult BackupMatchingFiles(FileCopyOperation operation, Action<string>? log)
    {
        var managedDirectory = Expand(operation.Source);
        var destinationDirectory = Expand(operation.Destination);
        if (!Directory.Exists(destinationDirectory))
            return new(0, 0, [$"{operation.NameOrSource()}: destination folder missing; kept managed backup."]);

        var pattern = string.IsNullOrWhiteSpace(operation.SearchPattern) ? "*" : operation.SearchPattern;
        var currentFiles = Directory.GetFiles(destinationDirectory, pattern);
        Directory.CreateDirectory(managedDirectory);
        var copiedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var updated = 0;
        foreach (var currentFile in currentFiles)
        {
            var fileName = Path.GetFileName(currentFile);
            CopyAtomically(currentFile, Path.Combine(managedDirectory, fileName));
            copiedNames.Add(fileName);
            updated++;
        }

        var removed = 0;
        foreach (var managedFile in Directory.GetFiles(managedDirectory, pattern))
        {
            if (copiedNames.Contains(Path.GetFileName(managedFile)))
                continue;
            File.Delete(managedFile);
            removed++;
        }

        log?.Invoke($"Updated {updated} file(s) for {operation.NameOrSource()}; removed {removed} stale file(s).");
        return new(updated, removed, []);
    }

    private static bool DestinationExists(FileCopyOperation operation)
    {
        var destination = Expand(operation.Destination);
        return operation.MatchingFiles ? Directory.Exists(destination) : File.Exists(destination);
    }

    private static void CopyAtomically(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? throw new InvalidOperationException("Destination directory is missing."));
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary, true);
            if (File.Exists(destination))
                File.Replace(temporary, destination, destination + ".bak", true);
            else
                File.Move(temporary, destination);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static string Expand(string value) => Environment.ExpandEnvironmentVariables(value).Replace("{USERNAME}", Environment.UserName);
    private static string NameOrSource(this FileCopyOperation operation) => string.IsNullOrWhiteSpace(operation.Name) ? operation.Source : operation.Name;
}
