using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;

namespace Winstaller.Utilities;

/// <summary>
/// Self-updater utility for Winstaller
/// </summary>
public static class SelfUpdater
{
    private const string VersionUrl = "https://copyparty.arimodu.dev/winstaller/version.txt";
    private const string DownloadUrlTemplate = "https://copyparty.arimodu.dev/winstaller/{0}.zip";

    /// <summary>
    /// Gets the current version of the running application
    /// </summary>
    public static Version CurrentVersion
    {
        get
        {
            //return new Version(1, 0, 0); // Testing override
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version;
            return version ?? new Version(1, 0, 0);
        }
    }

    /// <summary>
    /// Check for updates and optionally install them
    /// </summary>
    /// <param name="autoInstall">If true, automatically install updates without prompting</param>
    /// <returns>True if update was installed, false otherwise</returns>
    public static async Task<bool> CheckForUpdatesAsync(bool autoInstall = false)
    {
        Logger.Debug("Starting update check...");
        Logger.Debug($"Current version: {CurrentVersion}");
        Logger.Debug($"Auto-install: {autoInstall}");

        Console.WriteLine($"Current version: {CurrentVersion}");
        Console.WriteLine($"Checking for updates...");

        try
        {
            // Get latest version from server
            Logger.Debug($"Fetching version from: {VersionUrl}");
            var latestVersion = await GetLatestVersionAsync();

            if (latestVersion == null)
            {
                Logger.Error("Failed to check for updates.");
                return false;
            }

            Logger.Debug($"Latest version: {latestVersion}");
            Console.WriteLine($"Latest version:  {latestVersion}");

            if (latestVersion <= CurrentVersion)
            {
                Logger.Success("You are running the latest version.");
                Logger.Debug("No update required");
                return false;
            }

            Console.WriteLine();
            Logger.Warning($"Update available: {CurrentVersion} -> {latestVersion}");
            Logger.Debug("Update is available and newer than current version");

            if (!autoInstall)
            {
                if (!Logger.Confirm("\nDo you want to install the update?"))
                {
                    Logger.Debug("User declined update");
                    Console.WriteLine("Update cancelled.");
                    return false;
                }
            }
            else
            {
                Logger.Debug("Auto-installing update (autoInstall=true)");
                Console.WriteLine("Auto-installing update...");
            }

            // Download and install update
            return await DownloadAndInstallUpdateAsync(latestVersion);
        }
        catch (Exception ex)
        {
            Logger.Exception(ex, "Update check failed");
            return false;
        }
    }

    /// <summary>
    /// Get the latest version number from the server
    /// </summary>
    private static async Task<Version?> GetLatestVersionAsync()
    {
        try
        {
            Logger.Debug("Creating HTTP client for version check...");
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            Logger.Debug($"Sending request to {VersionUrl}");
            var versionText = await client.GetStringAsync(VersionUrl);
            versionText = versionText.Trim();

            Logger.Debug($"Received version string: '{versionText}'");

            if (Version.TryParse(versionText, out var version))
            {
                Logger.Debug($"Parsed version: {version}");
                return version;
            }

            Logger.Warning($"Invalid version format: {versionText}");
            return null;
        }
        catch (HttpRequestException ex)
        {
            Logger.Error($"Network error: {ex.Message}");
            Logger.Debug($"HTTP exception: {ex.GetType().Name} - {ex.StatusCode}");
            return null;
        }
        catch (TaskCanceledException)
        {
            Logger.Error("Request timed out.");
            Logger.Debug("Version check timed out after 30 seconds");
            return null;
        }
    }

    /// <summary>
    /// Download and install the update
    /// </summary>
    private static async Task<bool> DownloadAndInstallUpdateAsync(Version version)
    {
        var downloadUrl = string.Format(DownloadUrlTemplate, version);
        var tempZipPath = Path.Combine(Path.GetTempPath(), $"Winstaller-{version}.zip");
        var tempExtractPath = Path.Combine(Path.GetTempPath(), $"Winstaller-{version}-Extracted");
        var currentExePath = Environment.ProcessPath!;
        var currentDir = AppContext.BaseDirectory;

        Logger.Debug($"Download URL: {downloadUrl}");
        Logger.Debug($"Temp ZIP path: {tempZipPath}");
        Logger.Debug($"Temp extract path: {tempExtractPath}");
        Logger.Debug($"Current exe path: {currentExePath}");
        Logger.Debug($"Current directory: {currentDir}");

        try
        {
            // Download the update
            Logger.Debug("Starting download...");
            Console.WriteLine($"Downloading from: {downloadUrl}");

            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(5);

            Logger.Debug("Sending download request...");
            var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            Logger.Debug($"Content length: {totalBytes} bytes");

            {
                await using var contentStream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None);

                var buffer = new byte[81920];
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                    totalRead += bytesRead;

                    if (totalBytes > 0)
                    {
                        var percent = (int)(totalRead * 100 / totalBytes);
                        Console.Write($"\rDownloading: {percent}% ({totalRead / 1024}KB / {totalBytes / 1024}KB)    ");
                    }
                    else
                    {
                        Console.Write($"\rDownloading: {totalRead / 1024}KB    ");
                    }
                }

                Console.WriteLine();
                Logger.Success("Download complete.");
                Logger.Debug($"Downloaded {totalRead} bytes to {tempZipPath}");
            }

            // Extract the update
            Logger.Debug("Extracting update...");
            Console.WriteLine("Extracting update...");

            if (Directory.Exists(tempExtractPath))
            {
                Logger.Debug($"Removing existing extract directory: {tempExtractPath}");
                Directory.Delete(tempExtractPath, true);
            }

            ZipFile.ExtractToDirectory(tempZipPath, tempExtractPath);
            Logger.Success("Extraction complete.");
            Logger.Debug($"Extracted to {tempExtractPath}");

            // Find the new executable
            Logger.Debug("Searching for executable in update package...");
            var newExePath = FindExecutable(tempExtractPath);
            if (newExePath == null)
            {
                Logger.Error("Could not find Winstaller.exe in update package.");
                Logger.Debug("Executable not found - aborting update");
                return false;
            }

            Logger.Debug($"Found executable: {newExePath}");

            // Create update batch script
            var batchPath = Path.Combine(Path.GetTempPath(), "winstaller-update.cmd");
            var newExeDir = Path.GetDirectoryName(newExePath)!;

            Logger.Debug($"Creating update batch script: {batchPath}");

            // The batch script waits for this process to exit, then replaces files
            var batchContent = $@"@echo off
echo Sleeping for 2 seconds...
sleep 2
echo Installing update...
xcopy /E /Y /I ""{newExeDir}\*"" ""{currentDir}\"" >nul
if errorlevel 1 (
    echo Update failed!
    pause
    exit /b 1
)
echo Update complete!
rd /s /q ""{tempExtractPath}"" 2>nul
del ""{tempZipPath}"" 2>nul
del ""%~f0"" 2>nul
pause
";

            await File.WriteAllTextAsync(batchPath, batchContent);
            Logger.Debug("Update batch script created");

            Console.WriteLine("Launching updater and exiting...");
            Logger.Debug($"Starting batch script: {batchPath}");
            Logger.Debug($"Current process ID: {Environment.ProcessId}");

            // Start the batch script and exit
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{batchPath}\"",
                UseShellExecute = true,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal
            });

            Logger.Debug("Batch script started - exiting application");

            // Exit the current process to allow the update
            Environment.Exit(0);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Exception(ex, "Update failed");

            // Cleanup on failure
            Logger.Debug("Cleaning up after failed update...");
            try { if (File.Exists(tempZipPath)) File.Delete(tempZipPath); } catch { }
            try { if (Directory.Exists(tempExtractPath)) Directory.Delete(tempExtractPath, true); } catch { }

            return false;
        }
    }

    /// <summary>
    /// Find the executable in the extracted update folder
    /// </summary>
    private static string? FindExecutable(string extractPath)
    {
        Logger.Debug($"Searching for executable in: {extractPath}");

        // Check root
        var exePath = Path.Combine(extractPath, "Winstaller.exe");
        if (File.Exists(exePath))
        {
            Logger.Debug($"Found in root: {exePath}");
            return exePath;
        }

        // Check subdirectories (in case ZIP has a root folder)
        foreach (var dir in Directory.GetDirectories(extractPath))
        {
            Logger.Debug($"Checking subdirectory: {dir}");
            exePath = Path.Combine(dir, "Winstaller.exe");
            if (File.Exists(exePath))
            {
                Logger.Debug($"Found in subdirectory: {exePath}");
                return exePath;
            }
        }

        Logger.Debug("Executable not found in any location");
        return null;
    }
}
