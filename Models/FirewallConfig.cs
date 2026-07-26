namespace Winstaller.Models;

public class FirewallConfig
{
    public bool Enabled { get; set; }
    public string BackupPath { get; set; } = string.Empty;
    public bool RestoreBackup { get; set; } = true;
}
