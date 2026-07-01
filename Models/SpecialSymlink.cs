namespace Winstaller.Models;

/// <summary>
/// Represents a special symlink with custom source and target
/// </summary>
public class SpecialSymlink
{
    public string Source { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public bool IsDirectory { get; set; } = true;
    public string Description { get; set; } = string.Empty;
}
