namespace Winstaller.Models;

/// <summary>
/// Configuration for network drive mappings
/// </summary>
public class NetworkDrivesConfig
{
    public bool Enabled { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 10;
    public List<NetworkDriveMapping> Drives { get; set; } = [];
}
