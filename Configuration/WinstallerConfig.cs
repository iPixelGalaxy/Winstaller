using Winstaller.Models;

namespace Winstaller.Configuration;

/// <summary>
/// Root configuration class for Winstaller
/// </summary>
public class WinstallerConfig
{
    public NetworkDrivesConfig NetworkDrives { get; set; } = new();
    public SymlinksConfig Symlinks { get; set; } = new();
    public AppInstallerConfig AppInstaller { get; set; } = new();

    // Personalized configurations split into separate modules
    public FontsConfig Fonts { get; set; } = new();
    public ShellFoldersConfig ShellFolders { get; set; } = new();
    public RegistryConfig Registry { get; set; } = new();
    public FileCopyConfig FileCopy { get; set; } = new();
    public StartupConfig Startup { get; set; } = new();
    public PathConfig Path { get; set; } = new();

    // Discord and Spotify as separate modules
    public DiscordConfig Discord { get; set; } = new();
    public SpotifyConfig Spotify { get; set; } = new();

    public AppDataUtilityConfig AppDataUtility { get; set; } = new();
}
