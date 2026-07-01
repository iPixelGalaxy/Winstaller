namespace Winstaller.Models;

/// <summary>
/// Configuration for registry modifications
/// </summary>
public class RegistryConfig
{
    public bool Enabled { get; set; } = true;
    public List<string> FilesToImport { get; set; } = [];
    public List<RegistryModification> Modifications { get; set; } = [];
}
