namespace Winstaller.Models;

/// <summary>
/// Represents a single network drive mapping
/// </summary>
public class NetworkDriveMapping
{
    public string DriveLetter { get; set; } = string.Empty;
    public string NetworkPath { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public bool Persistent { get; set; } = true;
    public bool DeleteFirst { get; set; } = true;
}
