namespace Winstaller.Models;

/// <summary>
/// Configuration for symlink creation
/// </summary>
public class SymlinksConfig
{
    public bool Enabled { get; set; } = true;
    public string BaseSymlinkDirectory { get; set; } = @"D:\ReinstallFiles\Symlinks";
    public List<string> RoamingDirectories { get; set; } = [];
    public List<string> LocalDirectories { get; set; } = [];
    public List<string> LocalLowDirectories { get; set; } = [];
    public List<string> IgnoredRoamingDirectories { get; set; } = [];
    public List<string> IgnoredLocalDirectories { get; set; } = [];
    public List<string> IgnoredLocalLowDirectories { get; set; } = [];
    public List<SpecialSymlink> SpecialSymlinks { get; set; } = [];
}
