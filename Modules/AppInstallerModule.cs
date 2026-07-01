using Winstaller.Configuration;
using Winstaller.Models;
using Winstaller.Utilities;
using System.Net.Http;

namespace Winstaller.Modules;

/// <summary>
/// Module for installing applications using winget and custom scripts
/// </summary>
public class AppInstallerModule : ModuleBase
{
    private static readonly HttpClient HttpClient = new();

    public AppInstallerModule(WinstallerConfig config) : base(config) { }

    public override string Name => "App Installer";
    public override string Description => "Installs applications using winget with prepared configurations, manual installers, and bulk installs";
    public override bool IsEnabled => Config.AppInstaller.Enabled;

    public override async Task<bool> ExecuteAsync()
    {
        if (!IsEnabled)
        {
            ConsoleHelper.WriteWarning("App Installer module is disabled in configuration.");
            return false;
        }

        EnsureAdministrator();

        ConsoleHelper.WriteHeader("Starting Package Installations");

        var successCount = 0;
        var totalCount = 0;

        // Phase 1: Prepared Installers (with INF files)
        if (Config.AppInstaller.PreparedInstallers.Count > 0)
        {
            ConsoleHelper.WriteSubHeader("Phase 1: Prepared Installers");
            foreach (var packageId in Config.AppInstaller.PreparedInstallers)
            {
                totalCount++;
                if (await InstallPreparedPackageAsync(packageId))
                    successCount++;
                Console.WriteLine();
            }
        }

        // Phase 2: Manual Installs
        if (Config.AppInstaller.ManualInstalls.Count > 0)
        {
            ConsoleHelper.WriteSubHeader("Phase 2: Manual Installations");
            foreach (var packageId in Config.AppInstaller.ManualInstalls)
            {
                totalCount++;
                if (await InstallManualPackageAsync(packageId))
                    successCount++;
                Console.WriteLine();
            }
        }

        // Phase 3: Custom Scripts
        if (Config.AppInstaller.CustomScripts.Count > 0)
        {
            ConsoleHelper.WriteSubHeader("Phase 3: Custom Installations");
            foreach (var script in Config.AppInstaller.CustomScripts)
            {
                totalCount++;
                if (await RunCustomInstallerAsync(script))
                    successCount++;
                Console.WriteLine();
            }
        }

        // Phase 4: Default Installs (bulk)
        if (Config.AppInstaller.DefaultInstalls.Count > 0)
        {
            ConsoleHelper.WriteSubHeader("Phase 4: Default Installations (Bulk)");
            totalCount += Config.AppInstaller.DefaultInstalls.Count;
            if (await InstallDefaultPackagesBulkAsync())
                successCount += Config.AppInstaller.DefaultInstalls.Count;
        }

        // Summary
        ConsoleHelper.WriteHeader("Installation Summary");
        Console.WriteLine($"Total packages processed: {totalCount}");
        Console.WriteLine($"Successful installations: {successCount}");
        Console.WriteLine($"Failed installations: {totalCount - successCount}");

        return successCount == totalCount;
    }

    private async Task<bool> InstallPreparedPackageAsync(string packageId)
    {
        var setupInfoDir = ExpandEnvironmentVariables(Config.AppInstaller.SetupInfoDirectory);
        var infFileName = packageId.Replace(".", "-") + ".inf";
        var infFile = Path.Combine(setupInfoDir, infFileName);

        if (!File.Exists(infFile))
        {
            ConsoleHelper.WriteWarning($"INF file not found for {packageId}: {infFile}");
            Console.WriteLine("Falling back to default installation...");
            return await InstallDefaultPackageAsync(packageId);
        }

        Console.WriteLine($"Installing {packageId} with INF file...");
        Console.WriteLine("Opening new console window for winget output...");

        var overrideArgs = $"/LOADINF=\"{infFile}\" /SILENT /NOCANCEL";
        var consoleCmd = $"winget install {packageId} --override '{overrideArgs}'";

        try
        {
            var result = await RunCmdAsync(
                $"start /wait powershell /c \"{consoleCmd}\"",
                Config.AppInstaller.DefaultTimeoutSeconds * 1000
            );

            if (result == 0)
            {
                ConsoleHelper.WriteSuccess($"Successfully installed {packageId}");
                return true;
            }
            else
            {
                ConsoleHelper.WriteError($"Installation failed for {packageId} (exit code: {result})");
                return false;
            }
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Error installing {packageId}: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> InstallManualPackageAsync(string packageId)
    {
        Console.WriteLine($"Manual installation for {packageId}");
        Console.WriteLine("Downloading package...");

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            // Download using winget
            var downloadResult = await RunCmdAsync(
                $"winget download {packageId} -d \"{tempDir}\"",
                Config.AppInstaller.DefaultTimeoutSeconds * 1000
            );

            if (downloadResult != 0)
            {
                ConsoleHelper.WriteError($"Failed to download {packageId}");
                return false;
            }

            // Find the downloaded executable
            var installerPath = FindInstaller(tempDir);
            if (installerPath == null)
            {
                ConsoleHelper.WriteError($"No executable found for {packageId}");
                return false;
            }

            Console.WriteLine($"Running installer: {Path.GetFileName(installerPath)}");
            Console.WriteLine("Please complete the installation manually...");

            var result = await RunProcessAsync(installerPath, "", Config.AppInstaller.ManualTimeoutSeconds * 1000);

            ConsoleHelper.WriteSuccess($"Manual installation process finished for {packageId}");
            return true;
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Error during manual installation of {packageId}: {ex.Message}");
            return false;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private async Task<bool> RunCustomInstallerAsync(CustomInstaller script)
    {
        Console.WriteLine($"Running custom installer: {script.Name}");

        return script.Type.ToLowerInvariant() switch
        {
            "office" => await InstallOfficeAsync(script),
            "download" => await InstallFromDownloadAsync(script),
            _ => await InstallDefaultPackageAsync(script.Name)
        };
    }

    private async Task<bool> InstallOfficeAsync(CustomInstaller script)
    {
        var setupInfoDir = ExpandEnvironmentVariables(Config.AppInstaller.SetupInfoDirectory);
        var configFile = Path.Combine(setupInfoDir, script.ConfigFile ?? "Configuration.xml");

        if (!File.Exists(configFile))
        {
            ConsoleHelper.WriteWarning($"Configuration.xml file not found at {configFile}");
            return false;
        }

        Console.WriteLine("Installing Office with Configuration.xml file...");
        Console.WriteLine("Opening new console window for winget output...");

        var overrideArgs = $"/configure \"{configFile}\"";
        var consoleCmd = $"winget install Microsoft.Office --override '{overrideArgs}'";

        try
        {
            var result = await RunCmdAsync(
                $"start /wait powershell /c \"{consoleCmd}\"",
                Config.AppInstaller.DefaultTimeoutSeconds * 1000
            );

            if (result == 0)
            {
                ConsoleHelper.WriteSuccess("Successfully installed Microsoft Office");
                return true;
            }
            else
            {
                ConsoleHelper.WriteError($"Installation failed for Microsoft Office (exit code: {result})");
                return false;
            }
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Error installing Microsoft Office: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> InstallFromDownloadAsync(CustomInstaller script)
    {
        if (string.IsNullOrEmpty(script.DownloadUrl))
        {
            ConsoleHelper.WriteError($"No download URL specified for {script.Name}");
            return false;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            var fileName = Path.GetFileName(new Uri(script.DownloadUrl).LocalPath);
            var filePath = Path.Combine(tempDir, fileName);

            Console.WriteLine($"Downloading {script.DownloadUrl}...");

            using var response = await HttpClient.GetAsync(script.DownloadUrl);
            response.EnsureSuccessStatusCode();

            await using var fileStream = new FileStream(filePath, FileMode.Create);
            await response.Content.CopyToAsync(fileStream);
            await fileStream.FlushAsync();
            fileStream.Close();

            Console.WriteLine($"Running installer: {filePath}");
            Console.WriteLine("Please complete the installation manually...");

            var result = await RunProcessAsync(filePath, "", Config.AppInstaller.ManualTimeoutSeconds * 1000);

            ConsoleHelper.WriteSuccess($"Installation process finished for {script.Name}");
            return true;
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Error installing {script.Name}: {ex.Message}");
            return false;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private async Task<bool> InstallDefaultPackageAsync(string packageId)
    {
        Console.WriteLine($"Installing {packageId} in separate console...");

        try
        {
            var consoleCmd = $"title Installing {packageId} && winget install {packageId}";
            var result = await RunCmdAsync(
                $"start /wait cmd /c \"{consoleCmd}\"",
                Config.AppInstaller.DefaultTimeoutSeconds * 1000
            );

            if (result == 0)
            {
                ConsoleHelper.WriteSuccess($"Successfully installed {packageId}");
                return true;
            }
            else
            {
                ConsoleHelper.WriteError($"Installation failed for {packageId} (exit code: {result})");
                return false;
            }
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Error installing {packageId}: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> InstallDefaultPackagesBulkAsync()
    {
        if (Config.AppInstaller.DefaultInstalls.Count == 0)
        {
            Console.WriteLine("No default packages to install");
            return true;
        }

        Console.WriteLine($"Installing {Config.AppInstaller.DefaultInstalls.Count} default packages in bulk...");
        Console.WriteLine("Opening new console window for bulk installation...");

        var packagesStr = string.Join(" ", Config.AppInstaller.DefaultInstalls);
        var consoleCmd = $"title Bulk Installation - {Config.AppInstaller.DefaultInstalls.Count} packages && winget install {packagesStr}";

        Console.WriteLine("Command that will run:");
        Console.WriteLine($"winget install {packagesStr}");

        try
        {
            Console.WriteLine("Starting bulk installation... This may take a while.");

            var result = await RunCmdAsync(
                $"start /wait cmd /c \"{consoleCmd}\"",
                Config.AppInstaller.BulkTimeoutSeconds * 1000
            );

            if (result == 0)
            {
                ConsoleHelper.WriteSuccess("Bulk installation completed successfully");
                return true;
            }
            else
            {
                ConsoleHelper.WriteError($"Bulk installation completed with errors (exit code: {result})");
                return false;
            }
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Error during bulk installation: {ex.Message}");
            return false;
        }
    }

    private static string? FindInstaller(string directory)
    {
        var extensions = new[] { ".exe", ".msi" };
        foreach (var file in Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories))
        {
            if (extensions.Any(ext => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                return file;
        }
        return null;
    }

    protected override List<MenuOption> GetMenuOptions()
    {
        return
        [
            new MenuOption("Run Full Installation", ExecuteAsync),
            new MenuOption("Install Prepared Packages Only", InstallPreparedOnlyAsync),
            new MenuOption("Install Manual Packages Only", InstallManualOnlyAsync),
            new MenuOption("Install Custom Scripts Only", InstallCustomOnlyAsync),
            new MenuOption("Install Default Packages (Bulk)", InstallDefaultOnlyAsync),
            new MenuOption("Install Single Package", InstallSinglePackageAsync),
            new MenuOption("List All Configured Packages", ListPackages)
        ];
    }

    private async Task InstallPreparedOnlyAsync()
    {
        EnsureAdministrator();
        ConsoleHelper.WriteSubHeader("Prepared Installers");
        foreach (var packageId in Config.AppInstaller.PreparedInstallers)
        {
            await InstallPreparedPackageAsync(packageId);
            Console.WriteLine();
        }
    }

    private async Task InstallManualOnlyAsync()
    {
        EnsureAdministrator();
        ConsoleHelper.WriteSubHeader("Manual Installations");
        foreach (var packageId in Config.AppInstaller.ManualInstalls)
        {
            await InstallManualPackageAsync(packageId);
            Console.WriteLine();
        }
    }

    private async Task InstallCustomOnlyAsync()
    {
        EnsureAdministrator();
        ConsoleHelper.WriteSubHeader("Custom Installations");
        foreach (var script in Config.AppInstaller.CustomScripts)
        {
            await RunCustomInstallerAsync(script);
            Console.WriteLine();
        }
    }

    private async Task InstallDefaultOnlyAsync()
    {
        EnsureAdministrator();
        await InstallDefaultPackagesBulkAsync();
    }

    private async Task InstallSinglePackageAsync()
    {
        Console.Write("Enter package ID (winget ID): ");
        var packageId = Console.ReadLine()?.Trim();

        if (string.IsNullOrEmpty(packageId))
        {
            Console.WriteLine("No package ID entered.");
            return;
        }

        await InstallDefaultPackageAsync(packageId);
    }

    private Task ListPackages()
    {
        ConsoleHelper.WriteSubHeader("Configured Packages");

        Console.WriteLine($"\nPrepared Installers ({Config.AppInstaller.PreparedInstallers.Count}):");
        foreach (var pkg in Config.AppInstaller.PreparedInstallers)
            Console.WriteLine($"  - {pkg}");

        Console.WriteLine($"\nManual Installs ({Config.AppInstaller.ManualInstalls.Count}):");
        foreach (var pkg in Config.AppInstaller.ManualInstalls)
            Console.WriteLine($"  - {pkg}");

        Console.WriteLine($"\nCustom Scripts ({Config.AppInstaller.CustomScripts.Count}):");
        foreach (var script in Config.AppInstaller.CustomScripts)
            Console.WriteLine($"  - {script.Name} ({script.Type})");

        Console.WriteLine($"\nDefault Installs ({Config.AppInstaller.DefaultInstalls.Count}):");
        foreach (var pkg in Config.AppInstaller.DefaultInstalls)
            Console.WriteLine($"  - {pkg}");

        return Task.CompletedTask;
    }
}
