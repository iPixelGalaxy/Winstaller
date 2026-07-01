namespace Winstaller.Models;

/// <summary>
/// Configuration for file copy operations
/// </summary>
public class FileCopyConfig
{
    public bool Enabled { get; set; } = true;
    public List<FileCopyOperation> Operations { get; set; } = [];
}
