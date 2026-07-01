namespace Winstaller.Models;

/// <summary>
/// Configuration for shell folder mappings
/// </summary>
public class ShellFoldersConfig
{
    public bool Enabled { get; set; } = true;
    public List<ShellFolderMapping> Folders { get; set; } = [];
}
