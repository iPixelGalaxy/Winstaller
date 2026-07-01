using Winstaller.Configuration;
using Winstaller.Models;
using Winstaller.Utilities;

namespace Winstaller.Modules;

/// <summary>
/// Module for installing Spotify with Spicetify customizations
/// </summary>
public class SpotifyModule : ModuleBase
{
    public SpotifyModule(WinstallerConfig config) : base(config) { }

    public override string Name => "Spotify";
    public override string Description => "Installs Spotify with Spicetify customizations";
    public override bool IsEnabled => Config.Spotify.Enabled;

    public override async Task<bool> ExecuteAsync()
    {
        if (!IsEnabled)
        {
            ConsoleHelper.WriteWarning("Spotify module is disabled in configuration.");
            return false;
        }

        ConsoleHelper.WriteHeader("Spotify Setup");

        var success = true;

        // Spotify installation
        if (Config.Spotify.InstallSpotify)
        {
            if (!await InstallSpotifyAsync())
                success = false;
        }

        // Spicetify installation
        if (Config.Spotify.InstallSpicetify)
        {
            if (!await InstallSpicetifyAsync())
                success = false;
        }

        return success;
    }

    private async Task<bool> InstallSpotifyAsync()
    {
        ConsoleHelper.WriteSubHeader("Installing Spotify");

        Console.WriteLine("Installing Spotify via winget...");
        var result = await RunCmdAsync("winget install Spotify.Spotify", 300000);

        if (result != 0)
        {
            ConsoleHelper.WriteError("Failed to install Spotify");
            return false;
        }

        ConsoleHelper.WriteSuccess("Spotify installed successfully");
        return true;
    }

    private async Task<bool> InstallSpicetifyAsync()
    {
        ConsoleHelper.WriteSubHeader("Installing Spicetify");

        Console.WriteLine("Installing Spicetify...");

        // Download and run Spicetify installer
        var installCmd = "$ProgressPreference = 'SilentlyContinue'; iex ((New-Object System.Net.WebClient).DownloadString('https://raw.githubusercontent.com/spicetify/cli/main/install.ps1'))";
        var result = await RunPowerShellAsync(installCmd, 300000);

        if (result != 0)
        {
            ConsoleHelper.WriteError("Failed to install Spicetify");
            return false;
        }

        // Configure Spicetify
        var spicetifyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "spicetify",
            "spicetify"
        );

        if (File.Exists(spicetifyPath) || File.Exists(spicetifyPath + ".exe"))
        {
            Console.WriteLine("Configuring Spicetify...");

            if (Config.Spotify.BlockUpdates)
            {
                await RunProcessAsync(spicetifyPath, "spotify-updates block", 30000);
            }

            if (!string.IsNullOrEmpty(Config.Spotify.SidebarConfig))
            {
                await RunProcessAsync(spicetifyPath, $"config sidebar_config {Config.Spotify.SidebarConfig}", 30000);
            }

            foreach (var customApp in Config.Spotify.CustomApps)
            {
                await RunProcessAsync(spicetifyPath, $"config custom_apps {customApp}", 30000);
            }

            await RunProcessAsync(spicetifyPath, "apply", 60000);

            ConsoleHelper.WriteSuccess("Spicetify configured successfully");
        }
        else
        {
            ConsoleHelper.WriteWarning("Spicetify executable not found, skipping configuration");
        }

        return true;
    }

    protected override List<MenuOption> GetMenuOptions()
    {
        return
        [
            new MenuOption("Run Full Setup", ExecuteAsync),
            new MenuOption("Install Spotify Only", async () =>
            {
                await InstallSpotifyAsync();
            }),
            new MenuOption("Install Spicetify Only", async () =>
            {
                await InstallSpicetifyAsync();
            }),
            new MenuOption("Kill Spotify Processes", async () =>
            {
                Console.WriteLine("Killing Spotify processes...");
                await RunCmdAsync("taskkill /F /IM spotify.exe", 5000);
                ConsoleHelper.WriteSuccess("Spotify processes terminated");
            }),
            new MenuOption("Show Configuration", ShowConfiguration)
        ];
    }

    private Task ShowConfiguration()
    {
        ConsoleHelper.WriteSubHeader("Spotify Configuration");

        Console.WriteLine($"\n  Install Spotify: {Config.Spotify.InstallSpotify}");
        Console.WriteLine($"  Install Spicetify: {Config.Spotify.InstallSpicetify}");
        Console.WriteLine($"  Block Updates: {Config.Spotify.BlockUpdates}");
        Console.WriteLine($"  Sidebar Config: {Config.Spotify.SidebarConfig}");
        Console.WriteLine($"  Custom Apps: {string.Join(", ", Config.Spotify.CustomApps)}");

        return Task.CompletedTask;
    }
}
