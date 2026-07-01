using Winstaller.Configuration;
using Winstaller.Models;
using Winstaller.Utilities;
using System.Net.Http;

namespace Winstaller.Modules;

/// <summary>
/// Module for installing Discord with Vencord and OpenAsar
/// </summary>
public class DiscordModule : ModuleBase
{
    private static readonly HttpClient HttpClient = new();

    public DiscordModule(WinstallerConfig config) : base(config) { }

    public override string Name => "Discord";
    public override string Description => "Installs Discord with Vencord and OpenAsar customizations";
    public override bool IsEnabled => Config.Discord.Enabled;

    public override async Task<bool> ExecuteAsync()
    {
        if (!IsEnabled)
        {
            ConsoleHelper.WriteWarning("Discord module is disabled in configuration.");
            return false;
        }

        ConsoleHelper.WriteHeader("Discord Setup");

        var success = true;

        // Discord installation
        if (Config.Discord.InstallDiscord)
        {
            if (!await InstallDiscordAsync())
                success = false;
        }

        // Vencord installation
        if (Config.Discord.InstallVencord)
        {
            if (!await InstallVencordAsync())
                success = false;
        }

        return success;
    }

    private async Task<bool> InstallDiscordAsync()
    {
        ConsoleHelper.WriteSubHeader("Installing Discord");

        Console.WriteLine("Installing Discord via winget...");
        var result = await RunCmdAsync("winget install discord.discord", 300000);

        if (result != 0)
        {
            ConsoleHelper.WriteError("Failed to install Discord");
            return false;
        }

        // Kill Discord if running
        Console.WriteLine("Terminating Discord processes...");
        await RunCmdAsync("taskkill /F /IM discord.exe", 5000);
        await Task.Delay(2000);

        ConsoleHelper.WriteSuccess("Discord installed successfully");
        return true;
    }

    private async Task<bool> InstallVencordAsync()
    {
        ConsoleHelper.WriteSubHeader("Installing Vencord");

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            var installerPath = Path.Combine(tempDir, "VencordInstallerCli.exe");

            // Download Vencord Installer
            Console.WriteLine("Downloading Vencord Installer...");
            using var response = await HttpClient.GetAsync(Config.Discord.VencordInstallerUrl);
            response.EnsureSuccessStatusCode();

            await using var fileStream = new FileStream(installerPath, FileMode.Create);
            await response.Content.CopyToAsync(fileStream);
            await fileStream.FlushAsync();
            fileStream.Close();

            if (!File.Exists(installerPath))
            {
                ConsoleHelper.WriteError("Download failed. Check the URL or network connection.");
                return false;
            }

            var discordLocation = ExpandEnvironmentVariables(Config.Discord.DiscordLocation);

            // Run Vencord installer
            Console.WriteLine("Running Vencord installer...");
            var result = await RunProcessAsync(installerPath, $"-install -location \"{discordLocation}\"", 120000);

            if (result != 0)
            {
                ConsoleHelper.WriteWarning($"Vencord installation returned non-zero exit code: {result}");
            }
            else
            {
                ConsoleHelper.WriteSuccess("Vencord installed successfully");
            }

            // Install OpenAsar if configured
            if (Config.Discord.InstallOpenAsar)
            {
                Console.WriteLine("Installing OpenAsar...");
                var openAsarResult = await RunProcessAsync(installerPath, $"-install-openasar -location \"{discordLocation}\"", 120000);

                if (openAsarResult != 0)
                {
                    ConsoleHelper.WriteWarning($"OpenAsar installation returned non-zero exit code: {openAsarResult}");
                }
                else
                {
                    ConsoleHelper.WriteSuccess("OpenAsar installed successfully");
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Error installing Vencord: {ex.Message}");
            return false;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    protected override List<MenuOption> GetMenuOptions()
    {
        return
        [
            new MenuOption("Run Full Setup", ExecuteAsync),
            new MenuOption("Install Discord Only", async () =>
            {
                await InstallDiscordAsync();
            }),
            new MenuOption("Install Vencord/OpenAsar Only", async () =>
            {
                await InstallVencordAsync();
            }),
            new MenuOption("Kill Discord Processes", async () =>
            {
                Console.WriteLine("Killing Discord processes...");
                await RunCmdAsync("taskkill /F /IM discord.exe", 5000);
                ConsoleHelper.WriteSuccess("Discord processes terminated");
            }),
            new MenuOption("Show Configuration", ShowConfiguration)
        ];
    }

    private Task ShowConfiguration()
    {
        ConsoleHelper.WriteSubHeader("Discord Configuration");

        Console.WriteLine($"\n  Install Discord: {Config.Discord.InstallDiscord}");
        Console.WriteLine($"  Install Vencord: {Config.Discord.InstallVencord}");
        Console.WriteLine($"  Install OpenAsar: {Config.Discord.InstallOpenAsar}");
        Console.WriteLine($"  Discord Location: {Config.Discord.DiscordLocation}");
        Console.WriteLine($"  Vencord URL: {Config.Discord.VencordInstallerUrl}");

        return Task.CompletedTask;
    }
}
