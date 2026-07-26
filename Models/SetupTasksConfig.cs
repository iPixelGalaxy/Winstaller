namespace Winstaller.Models;

public class SetupTasksConfig
{
    public bool Enabled { get; set; }
    public bool WindhawkAndSteam { get; set; } = true;
    public bool DesktopPlusUiAccess { get; set; } = true;
    public string WindhawkPath { get; set; } = @"D:\Software\Programs\Windhawk\windhawk.exe";
    public string SteamPath { get; set; } = @"D:\Steam\steam.exe";
    public string DesktopPlusBatchPath { get; set; } = @"D:\Steam\steamapps\common\DesktopPlus\misc\EnableUIAccessNoUser.bat";
}
