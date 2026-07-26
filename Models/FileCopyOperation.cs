namespace Winstaller.Models;

/// <summary>
/// Represents a file copy operation
/// </summary>
public class FileCopyOperation
{
    public string Name { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public string SearchPattern { get; set; } = string.Empty;
    public bool MatchingFiles { get; set; }
    public bool Overwrite { get; set; } = true;
    public bool RewriteShortcutProfilePaths { get; set; }
    public bool ProtectPrivateKeyAcl { get; set; }
    // Retained for file-copy.json compatibility. Empty destination files are no longer created.
    public bool CreateEmptyFirst { get; set; }
}
