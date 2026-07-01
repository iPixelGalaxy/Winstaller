namespace Winstaller.Models;

/// <summary>
/// Configuration for Discord installation and customization
/// </summary>
public class DiscordConfig
{
    public bool Enabled { get; set; } = false;
    public bool InstallDiscord { get; set; } = true;
    public bool InstallVencord { get; set; } = true;
    public bool InstallOpenAsar { get; set; } = true;
    public string VencordInstallerUrl { get; set; } = "https://github.com/Vencord/Installer/releases/latest/download/VencordInstallerCli.exe";
    public string DiscordLocation { get; set; } = @"%LOCALAPPDATA%\Discord";
}
