namespace Winstaller.Models;

public class VRChatRegistryConfig
{
    public bool Enabled { get; set; }
    public string BackupPath { get; set; } = string.Empty;
    public bool RestoreSettings { get; set; } = true;
    public bool RestorePersonalData { get; set; } = true;
    public List<string> ExcludedValueIds { get; set; } = [];
}
