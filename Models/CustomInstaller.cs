namespace Winstaller.Models;

/// <summary>
/// Represents a custom installer script
/// </summary>
public class CustomInstaller
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // "office", "allthenticate", "download"
    public string? ConfigFile { get; set; }
    public string? DownloadUrl { get; set; }
    public bool Silent { get; set; } = false;
}
