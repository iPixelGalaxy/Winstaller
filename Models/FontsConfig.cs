namespace Winstaller.Models;

/// <summary>
/// Configuration for font installation
/// </summary>
public class FontsConfig
{
    public bool Enabled { get; set; } = true;
    public string FontsDirectory { get; set; } = @"D:\ReinstallFiles\Fonts";
}
