namespace Winstaller.Models;

/// <summary>
/// Represents a file copy operation
/// </summary>
public class FileCopyOperation
{
    public string Source { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public bool CreateEmptyFirst { get; set; } = true;
}
