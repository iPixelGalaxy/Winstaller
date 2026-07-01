namespace Winstaller.Models;

/// <summary>
/// Configuration for application installation
/// </summary>
public class AppInstallerConfig
{
    public bool Enabled { get; set; } = true;
    public string SetupInfoDirectory { get; set; } = @"D:\ReinstallFiles\SetupInfo";
    public int DefaultTimeoutSeconds { get; set; } = 300;
    public int BulkTimeoutSeconds { get; set; } = 3600;
    public int ManualTimeoutSeconds { get; set; } = 1800;

    public List<string> PreparedInstallers { get; set; } = [];
    public List<string> ManualInstalls { get; set; } = [];
    public List<CustomInstaller> CustomScripts { get; set; } = [];
    public List<string> DefaultInstalls { get; set; } = [];
    public Dictionary<string, AppInstallBehavior> Behaviors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class AppInstallBehavior
{
    public string InstallMode { get; set; } = "Default";
    public DiscordInstallOptions Discord { get; set; } = new();
    public SpotifyInstallOptions Spotify { get; set; } = new();
}

public class DiscordInstallOptions
{
    public bool InstallDiscord { get; set; } = true;
    public bool InstallVencord { get; set; } = true;
    public bool InstallOpenAsar { get; set; } = true;
    public string VencordInstallerUrl { get; set; } = "https://github.com/Vencord/Installer/releases/latest/download/VencordInstallerCli.exe";
    public string DiscordLocation { get; set; } = @"%LOCALAPPDATA%\Discord";
}

public class SpotifyInstallOptions
{
    public bool InstallSpotify { get; set; } = true;
    public bool InstallSpicetify { get; set; } = true;
    public bool BlockUpdates { get; set; } = true;
    public string SidebarConfig { get; set; } = "0";
    public List<string> CustomApps { get; set; } = ["lyrics-plus"];
}
