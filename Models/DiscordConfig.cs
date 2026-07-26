namespace Winstaller.Models;

/// <summary>
/// Configuration for Discord installation and customization
/// </summary>
public class DiscordConfig
{
    public bool Enabled { get; set; } = false;
    public bool InstallDiscord { get; set; } = true;
    public bool InstallEquicord { get; set; } = true;
    public bool InstallOpenAsar { get; set; } = true;
    public string EquicordInstallerUrl { get; set; } = "https://github.com/Equicord/Equilotl/releases/latest/download/EquilotlCli.exe";
    public string DiscordLocation { get; set; } = @"%LOCALAPPDATA%\Discord";
}
