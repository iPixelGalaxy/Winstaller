namespace Winstaller.Models;

/// <summary>
/// Configuration for Spotify installation and customization
/// </summary>
public class SpotifyConfig
{
    public bool Enabled { get; set; } = false;
    public bool InstallSpotify { get; set; } = true;
    public bool InstallSpicetify { get; set; } = true;
    public bool BlockUpdates { get; set; } = true;
    public string SidebarConfig { get; set; } = "0";
    public List<string> CustomApps { get; set; } = ["lyrics-plus"];
}
