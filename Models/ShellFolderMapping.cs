namespace Winstaller.Models;

/// <summary>
/// Represents a shell folder mapping
/// </summary>
public class ShellFolderMapping
{
    public string FolderName { get; set; } = string.Empty;
    public string RegistryValue { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}
