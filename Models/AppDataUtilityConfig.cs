namespace Winstaller.Models;

/// <summary>
/// Configuration for AppData symlink utility
/// </summary>
public class AppDataUtilityConfig
{
    public string SymlinkBaseDirectory { get; set; } = @"D:\ReinstallFiles\Symlinks\AppData";
    public List<string> ExcludedDirectories { get; set; } = ["Microsoft", "Temp", "cache", "Cache"];
}
