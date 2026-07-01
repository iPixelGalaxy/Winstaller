namespace Winstaller.Models;

/// <summary>
/// Configuration for PATH environment variable modifications
/// </summary>
public class PathConfig
{
    public bool Enabled { get; set; } = true;
    public List<string> Additions { get; set; } = [];
}
