namespace Winstaller.Models;

/// <summary>
/// Configuration for application installation
/// </summary>
public class AppInstallerConfig
{
    public bool Enabled { get; set; } = true;
    public string SetupInfoDirectory { get; set; } = @"D:\ReinstallFiles\SetupInfo";
    public int DefaultTimeoutSeconds { get; set; } = 300;
    public int BulkTimeoutSeconds { get; set; } = 3600;
    public int ManualTimeoutSeconds { get; set; } = 1800;

    public List<string> PreparedInstallers { get; set; } = [];
    public List<string> ManualInstalls { get; set; } = [];
    public List<CustomInstaller> CustomScripts { get; set; } = [];
    public List<string> DefaultInstalls { get; set; } = [];
}
