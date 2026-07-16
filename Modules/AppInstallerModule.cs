using Winstaller.Configuration;
using Winstaller.Models;
using Winstaller.Utilities;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Winstaller.Modules;

/// <summary>
/// Module for installing applications using winget and custom scripts
/// </summary>
public class AppInstallerModule : ModuleBase
{
    private static readonly HttpClient HttpClient = new();

    public AppInstallerModule(WinstallerConfig config) : base(config) { }

    public override string Name => "App Installer";
    public override string Description => "Installs configured applications with automatic per-app installers";
    public override bool IsEnabled => Config.AppInstaller.Enabled;

    public override async Task<bool> ExecuteAsync()
    {
        if (!IsEnabled)
        {
            ConsoleHelper.WriteWarning("App Installer module is disabled in configuration.");
            return false;
        }

        EnsureAdministrator();

        if (!await EnsurePackageServicesAsync())
            return false;

        ConsoleHelper.WriteHeader("Starting Package Installations");

        var successCount = 0;
        var totalCount = 0;

        foreach (var packageId in Config.AppInstaller.DefaultInstalls)
        {
            totalCount++;
            if (await InstallConfiguredPackageAsync(packageId))
                successCount++;
            Console.WriteLine();
        }

        if (Config.AppInstaller.CustomScripts.Count > 0)
        {
            ConsoleHelper.WriteSubHeader("Custom Installations");
            foreach (var script in Config.AppInstaller.CustomScripts)
            {
                totalCount++;
                if (await RunCustomInstallerAsync(script))
                    successCount++;
                Console.WriteLine();
            }
        }

        // Summary
        ConsoleHelper.WriteHeader("Installation Summary");
        Console.WriteLine($"Total packages processed: {totalCount}");
        Console.WriteLine($"Successful installations: {successCount}");
        Console.WriteLine($"Failed installations: {totalCount - successCount}");

        return successCount == totalCount;
    }

    private static async Task<bool> EnsurePackageServicesAsync()
    {
        ConsoleHelper.WriteSubHeader("Checking Microsoft Store and App Installer");
        if (!await IsStoreAvailableAsync())
        {
            ConsoleHelper.WriteWarning("Microsoft Store is unavailable. Running wsreset -i...");
            await RunProcessAsync("wsreset.exe", "-i", 120000, false);
            for (var attempt = 0; attempt < 60 && !await IsStoreAvailableAsync(); attempt++)
                await Task.Delay(TimeSpan.FromSeconds(5));
            if (!await IsStoreAvailableAsync())
            {
                ConsoleHelper.WriteError("Microsoft Store did not become available.");
                return false;
            }
        }

        if (!await IsWingetAvailableAsync())
        {
            await RunPowerShellAsync("Add-AppxPackage -RegisterByFamilyName -MainPackage Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", 60000);
            if (!await IsWingetAvailableAsync())
            {
                ConsoleHelper.WriteWarning("Repairing App Installer and WinGet...");
                var repair = "Install-PackageProvider -Name NuGet -Force | Out-Null; Install-Module -Name Microsoft.WinGet.Client -Force -AllowClobber -Repository PSGallery -Scope AllUsers | Out-Null; Import-Module Microsoft.WinGet.Client; Repair-WinGetPackageManager -Force -Latest -AllUsers";
                await RunPowerShellAsync(repair, 600000);
            }
        }

        if (!await IsWingetAvailableAsync())
        {
            ConsoleHelper.WriteError("WinGet is unavailable after App Installer repair.");
            return false;
        }

        await RunProcessAsync("winget", "upgrade Microsoft.AppInstaller --accept-source-agreements --accept-package-agreements --disable-interactivity --no-progress", 300000, false);
        if (await RunProcessAsync("winget", "source update --disable-interactivity --no-progress", 120000, false) != 0 ||
            await RunProcessAsync("winget", "show --id 9WZDNCRFHVN5 --exact --source msstore --accept-source-agreements --disable-interactivity --no-progress", 120000, false) != 0)
        {
            ConsoleHelper.WriteError("WinGet cannot access required sources.");
            return false;
        }

        return true;
    }

    private static Task<bool> IsStoreAvailableAsync()
    {
        return IsPowerShellConditionTrueAsync("Get-AppxPackage -Name Microsoft.WindowsStore -ErrorAction SilentlyContinue");
    }

    private static async Task<bool> IsWingetAvailableAsync()
    {
        return await RunProcessAsync("winget", "--version", 30000, false) == 0;
    }

    private static async Task<bool> IsPowerShellConditionTrueAsync(string expression)
    {
        return await RunPowerShellAsync($"if ({expression}) {{ exit 0 }} else {{ exit 1 }}", 30000) == 0;
    }

    private async Task<bool> InstallBundledHevcAsync()
    {
        const string identity = "Microsoft.HEVCVideoExtensions";
        var provisioned = await IsPowerShellConditionTrueAsync($"Get-AppxProvisionedPackage -Online | Where-Object DisplayName -eq '{identity}'");
        var installed = await IsPowerShellConditionTrueAsync($"Get-AppxPackage -Name {identity} -ErrorAction SilentlyContinue");
        var bundlePath = ExtractHevcBundle();
        if (bundlePath is null)
        {
            ConsoleHelper.WriteError("Bundled HEVC Video Extensions package is missing.");
            return false;
        }

        if (!provisioned && await RunProcessAsync("dism.exe", $"/Online /Add-ProvisionedAppxPackage /PackagePath:\"{bundlePath}\" /SkipLicense", 300000, true) != 0)
        {
            ConsoleHelper.WriteError("Failed provisioning HEVC Video Extensions.");
            return false;
        }
        if (!installed && await RunPowerShellAsync($"Add-AppxPackage -Path '{bundlePath.Replace("'", "''")}'", 300000) != 0)
        {
            ConsoleHelper.WriteError("Failed registering HEVC Video Extensions for current user.");
            return false;
        }

        var upgrade = await RunProcessAsync("winget", $"upgrade --id {RecommendedAppCatalog.HevcPackageId} --exact --source msstore --accept-source-agreements --accept-package-agreements --disable-interactivity --no-progress", Config.AppInstaller.DefaultTimeoutSeconds * 1000, true);
        return upgrade == 0;
    }

    private static string? ExtractHevcBundle()
    {
        const string resourceName = "Winstaller.Assets.HEVCVideoExtensions.appxbundle";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream is null || BootstrapManager.DataRoot is null)
            return null;
        var directory = Path.Combine(BootstrapManager.CacheDirectory, "packages");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "HEVC Video Extensions.appxbundle");
        if (File.Exists(path) && new FileInfo(path).Length == stream.Length)
            return path;
        using var output = File.Create(path);
        stream.CopyTo(output);
        return path;
    }
    private async Task<bool> InstallConfiguredPackageAsync(string packageId)
    {
        Config.AppInstaller.Behaviors.TryGetValue(packageId, out var behavior);
        behavior ??= new AppInstallBehavior();

        var success = behavior.InstallMode switch
        {
            AppInstallMode.Prepared => await InstallPreparedPackageAsync(packageId, behavior),
            AppInstallMode.Manual => await InstallManualPackageAsync(packageId, behavior),
            AppInstallMode.Winget => await InstallDefaultPackageAsync(packageId),
            _ when packageId.Equals("Microsoft.VisualStudioCode", StringComparison.OrdinalIgnoreCase) => await InstallVisualStudioCodeAsync(behavior),
            _ when packageId.Equals("Git.Git", StringComparison.OrdinalIgnoreCase) => await InstallGitAsync(behavior),
            _ => await InstallDefaultPackageAsync(packageId)
        };

        if (!success)
            return false;

        if (packageId.Equals("Discord.Discord", StringComparison.OrdinalIgnoreCase))
            return await ApplyDiscordOptionsAsync(behavior.Discord);

        if (packageId.Equals("Spotify.Spotify", StringComparison.OrdinalIgnoreCase))
            return await ApplySpotifyOptionsAsync(behavior.Spotify);

        return true;
    }

    private async Task<bool> ApplyDiscordOptionsAsync(DiscordInstallOptions options)
    {
        if (!options.InstallVencord)
            return true;

        await RunCmdAsync("taskkill /F /IM discord.exe", 5000);
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var installerPath = Path.Combine(tempDir, "VencordInstallerCli.exe");
            using var response = await HttpClient.GetAsync(options.VencordInstallerUrl);
            response.EnsureSuccessStatusCode();
            await using (var fileStream = new FileStream(installerPath, FileMode.Create))
            {
                await response.Content.CopyToAsync(fileStream);
            }

            var location = ExpandEnvironmentVariables(options.DiscordLocation);
            var result = await RunProcessAsync(installerPath, $"-install -location \"{location}\"", 120000);
            if (options.InstallOpenAsar)
                await RunProcessAsync(installerPath, $"-install-openasar -location \"{location}\"", 120000);
            return result == 0;
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Discord options failed: {ex.Message}");
            return false;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private async Task<bool> ApplySpotifyOptionsAsync(SpotifyInstallOptions options)
    {
        if (!options.InstallSpicetify)
            return true;

        var installCmd = "$ProgressPreference = 'SilentlyContinue'; iex ((New-Object System.Net.WebClient).DownloadString('https://raw.githubusercontent.com/spicetify/cli/main/install.ps1'))";
        if (await RunPowerShellAsync(installCmd, 300000) != 0)
            return false;

        var spicetifyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "spicetify", "spicetify");
        if (!File.Exists(spicetifyPath) && File.Exists(spicetifyPath + ".exe"))
            spicetifyPath += ".exe";
        if (!File.Exists(spicetifyPath))
            return true;

        if (options.BlockUpdates)
            await RunProcessAsync(spicetifyPath, "spotify-updates block", 30000);
        if (!string.IsNullOrWhiteSpace(options.SidebarConfig))
            await RunProcessAsync(spicetifyPath, $"config sidebar_config {options.SidebarConfig}", 30000);
        foreach (var customApp in options.CustomApps)
            await RunProcessAsync(spicetifyPath, $"config custom_apps {customApp}", 30000);
        await RunProcessAsync(spicetifyPath, "apply", 60000);
        return true;
    }

    private async Task<bool> InstallVisualStudioCodeAsync(AppInstallBehavior behavior)
    {
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "win32-arm64-user",
            Architecture.X86 => "win32-user",
            _ => "win32-x64-user"
        };
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var installer = Path.Combine(tempDir, "VSCodeUserSetup.exe");
            var version = behavior.LockVersion ? behavior.Version : "latest";
            var url = $"https://update.code.visualstudio.com/{version}/{architecture}/stable";
            Console.WriteLine("Downloading Visual Studio Code User Setup...");
            using var response = await HttpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            await using (var output = File.Create(installer))
                await response.Content.CopyToAsync(output);
            var arguments = "/VERYSILENT /NORESTART /SP- /MERGETASKS=\"addcontextmenufiles,addcontextmenufolders,addtopath,!runcode\"";
            var result = await RunProcessAsync(installer, arguments, Config.AppInstaller.InstallerTimeoutSeconds * 1000);
            if (result != 0) ConsoleHelper.WriteError($"VS Code installer failed (exit code: {result})");
            return result == 0;
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"VS Code installation failed: {ex.Message}");
            return false;
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    private async Task<bool> InstallGitAsync(AppInstallBehavior behavior)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            Console.WriteLine("Downloading Git for Windows...");
            var version = behavior.LockVersion ? $" --version \"{behavior.Version}\"" : string.Empty;
            var download = await RunProcessAsync("winget", $"download --id Git.Git --exact --source winget{version} --download-directory \"{tempDir}\" --accept-source-agreements --accept-package-agreements --disable-interactivity", Config.AppInstaller.DefaultTimeoutSeconds * 1000);
            if (download != 0) { ConsoleHelper.WriteError($"Git download failed (exit code: {download})"); return false; }
            var installer = FindInstaller(tempDir);
            if (installer is null) { ConsoleHelper.WriteError("Git installer was not downloaded."); return false; }
            var infPath = Path.Combine(tempDir, "git-install.inf");
            await File.WriteAllTextAsync(infPath, BuildGitInf(behavior.Git));
            var result = await RunProcessAsync(installer, $"/VERYSILENT /NORESTART /NOCANCEL /SP- /LOADINF=\"{infPath}\"", Config.AppInstaller.InstallerTimeoutSeconds * 1000);
            if (result != 0) ConsoleHelper.WriteError($"Git installer failed (exit code: {result})");
            return result == 0;
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Git installation failed: {ex.Message}");
            return false;
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    internal static string BuildGitInf(GitInstallOptions options)
    {
        static string Editor(GitEditor value) => value switch { GitEditor.Vim => "VIM", GitEditor.NotepadPlusPlus => "Notepad++", GitEditor.CustomEditor => "CustomEditor", _ => value.ToString() };
        static string Path(GitPath value) => value switch { GitPath.CmdTools => "CmdTools", _ => value.ToString() };
        static string Ssh(GitSsh value) => value switch { GitSsh.OpenSsh => "OpenSSH", GitSsh.ExternalOpenSsh => "ExternalOpenSSH", GitSsh.Plink => "Plink", _ => throw new ArgumentOutOfRangeException(nameof(value)) };
        static string Https(GitHttps value) => value == GitHttps.OpenSsl ? "OpenSSL" : "WinSSL";
        static string LineEndings(GitLineEndings value) => value.ToString();
        static string Terminal(GitTerminal value) => value == GitTerminal.MinTTY ? "MinTTY" : "ConHost";
        static string Pull(GitPullBehavior value) => value == GitPullBehavior.FFOnly ? "FFOnly" : value.ToString();
        static string Enabled(bool value) => value ? "Enabled" : "Disabled";
        static void RejectNewlines(string value, string name)
        {
            if (value.Contains('\r') || value.Contains('\n')) throw new ArgumentException($"{name} cannot contain newlines.");
        }
        RejectNewlines(options.CustomEditorPath, nameof(options.CustomEditorPath));
        RejectNewlines(options.PlinkPath, nameof(options.PlinkPath));
        var components = new List<string>();
        if (options.DesktopIcon) components.Add("icons\\desktop");
        if (options.GitBashHere) components.Add("ext\\shellhere");
        if (options.GitGuiHere) components.Add("ext\\guihere");
        if (options.GitLfs) components.Add("gitlfs");
        if (options.AssociateGitFiles) components.Add("assoc");
        if (options.AssociateShellFiles) components.Add("assoc_sh");
        if (options.WindowsTerminalProfile) components.Add("windowsterminal");
        if (options.Scalar) components.Add("scalar");
        if (options.CheckForUpdates) components.Add("autoupdate");
        var lines = new List<string> { "[Setup]", "Lang=default", $"Components={string.Join(',', components)}", $"EditorOption={Editor(options.Editor)}", $"PathOption={Path(options.Path)}", $"SSHOption={Ssh(options.Ssh)}", $"CURLOption={Https(options.Https)}", $"CRLFOption={LineEndings(options.LineEndings)}", $"BashTerminalOption={Terminal(options.Terminal)}", $"GitPullBehaviorOption={Pull(options.PullBehavior)}", $"UseCredentialManager={Enabled(options.CredentialManager)}", $"PerformanceTweaksFSCache={Enabled(options.FileSystemCache)}", $"EnableSymlinks={options.Symlinks}", $"AddmandatoryASLRsecurityexceptions={options.MandatoryAslr}", $"EnableBuiltinDifftool={options.BuiltinDifftool}", $"EnableBuiltinRebase={options.BuiltinRebase}", $"EnableBuiltinStash={options.BuiltinStash}", $"EnableBuiltinInteractiveAdd={options.BuiltinInteractiveAdd}", $"EnablePseudoConsoleSupport={options.PseudoConsole}", $"EnableFSMonitor={options.FileSystemMonitor}" };
        if (!string.IsNullOrWhiteSpace(options.DefaultBranch)) lines.Add($"DefaultBranchOption={options.DefaultBranch}");
        if (options.Editor == GitEditor.CustomEditor) lines.Add($"CustomEditorPath={options.CustomEditorPath}");
        if (options.Ssh == GitSsh.Plink) { lines.Add($"PlinkPath={options.PlinkPath}"); lines.Add($"TortoiseOption={options.UseTortoisePlink.ToString().ToLowerInvariant()}"); }
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private async Task<bool> InstallPreparedPackageAsync(string packageId, AppInstallBehavior behavior)
    {
        var infFile = Path.Combine(ExpandEnvironmentVariables(Config.AppInstaller.SetupInfoDirectory), packageId.Replace('.', '-') + ".inf");
        if (!File.Exists(infFile))
        {
            ConsoleHelper.WriteWarning($"INF file not found for {packageId}: {infFile}. Falling back to WinGet.");
            return await InstallDefaultPackageAsync(packageId);
        }
        var version = behavior.LockVersion ? $" --version \"{behavior.Version}\"" : string.Empty;
        var arguments = $"install --id \"{packageId}\" --exact --source winget{version} --override \"/LOADINF=\\\"{infFile}\\\" /SILENT /NOCANCEL\" --accept-source-agreements --accept-package-agreements --disable-interactivity";
        var result = await RunProcessAsync("winget", arguments, Config.AppInstaller.InstallerTimeoutSeconds * 1000);
        return result == 0;
    }

    private async Task<bool> InstallManualPackageAsync(string packageId, AppInstallBehavior behavior)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var version = behavior.LockVersion ? $" --version \"{behavior.Version}\"" : string.Empty;
            var download = await RunProcessAsync("winget", $"download --id \"{packageId}\" --exact --source winget{version} --download-directory \"{tempDir}\" --accept-source-agreements --accept-package-agreements --disable-interactivity", Config.AppInstaller.DefaultTimeoutSeconds * 1000);
            if (download != 0) return false;
            var installer = FindInstaller(tempDir);
            if (installer is null) { ConsoleHelper.WriteError($"No installer downloaded for {packageId}."); return false; }
            return await RunProcessAsync(installer, string.Empty, Config.AppInstaller.InstallerTimeoutSeconds * 1000) == 0;
        }
        catch (Exception ex) { ConsoleHelper.WriteError($"Manual installation failed for {packageId}: {ex.Message}"); return false; }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
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

            var result = await RunProcessAsync(filePath, "", Config.AppInstaller.InstallerTimeoutSeconds * 1000);

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
            var consoleCmd = $"title Installing {packageId} && {BuildWingetInstallCommand(packageId)}";
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

        Console.WriteLine($"Installing {Config.AppInstaller.DefaultInstalls.Count} default packages...");
        var success = true;
        foreach (var packageId in Config.AppInstaller.DefaultInstalls)
        {
            if (!await InstallDefaultPackageAsync(packageId))
                success = false;
        }

        return success;
    }

    private string BuildWingetInstallCommand(string packageId)
    {
        var source = RecommendedAppCatalog.IsMicrosoftStorePackage(packageId) ? "msstore" : "winget";
        var command = $"winget install --id \"{packageId}\" --exact --source {source} --accept-source-agreements --accept-package-agreements --disable-interactivity";
        if (Config.AppInstaller.Behaviors.TryGetValue(packageId, out var behavior) &&
            behavior.LockVersion &&
            !string.IsNullOrWhiteSpace(behavior.Version))
        {
            command += $" --version \"{behavior.Version}\"";
        }

        return command;
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
            new MenuOption("Install Custom Scripts Only", InstallCustomOnlyAsync),
            new MenuOption("Install Configured Packages", InstallDefaultOnlyAsync),
            new MenuOption("Install Single Package", InstallSinglePackageAsync),
            new MenuOption("List All Configured Packages", ListPackages)
        ];
    }

    private async Task InstallCustomOnlyAsync()
    {
        EnsureAdministrator();

        if (!await EnsurePackageServicesAsync())
            return;
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

        if (!await EnsurePackageServicesAsync())
            return;
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

        Console.WriteLine($"\nCustom Scripts ({Config.AppInstaller.CustomScripts.Count}):");
        foreach (var script in Config.AppInstaller.CustomScripts)
            Console.WriteLine($"  - {script.Name} ({script.Type})");

        Console.WriteLine($"\nDefault Installs ({Config.AppInstaller.DefaultInstalls.Count}):");
        foreach (var pkg in Config.AppInstaller.DefaultInstalls)
            Console.WriteLine($"  - {pkg}");

        return Task.CompletedTask;
    }
}
