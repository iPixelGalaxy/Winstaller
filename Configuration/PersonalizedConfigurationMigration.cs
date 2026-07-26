using Winstaller.Models;

namespace Winstaller.Configuration;

internal static class PersonalizedConfigurationMigration
{
    public static void Apply(WinstallerConfig config)
    {
        if (BootstrapManager.DataRoot is null || !BootstrapManager.DataRoot.Equals(@"D:\.winstaller", StringComparison.OrdinalIgnoreCase)) return;
        var data = BootstrapManager.DataDirectory;
        CopyFiles(@"D:\ReinstallFiles\Registry", Path.Combine(data, "Registry"), ["RightClickMenus.reg", "WinRAR.reg", "MonitorConfig.reg", "Remove ms-gamebar.reg"]);
        CopyFiles(@"D:\ReinstallFiles\Shortcuts", Path.Combine(data, "FilesAndShortcuts", "Shortcuts"), ["*.lnk"]);
        CopyFiles(@"D:\ReinstallFiles\Startup", Path.Combine(data, "FilesAndShortcuts", "Startup"), ["*.lnk"]);
        CopyFiles(@"D:\ReinstallFiles\Misc", Path.Combine(data, "FilesAndShortcuts", "Misc"), ["id_ed25519", "id_ecdsa", "known_hosts", "mpv.conf"]);
        config.Registry = new RegistryConfig { Enabled = true, FilesToImport = new[] { "RightClickMenus.reg", "WinRAR.reg", "MonitorConfig.reg", "Remove ms-gamebar.reg" }.Select(name => Path.Combine(data, "Registry", name)).ToList(), Modifications = [new() { Key = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", ValueName = "Microsoft Edge Update", Delete = true }] };
        config.FileCopy = new FileCopyConfig { Enabled = true, Operations = DefaultFileOperations(data) };
        config.Startup = new StartupConfig { Enabled = true, Programs = [new() { Name = "VRCX", Path = @"D:\Software\Programs\VRCX\VRCX.exe" }, new() { Name = "Windhawk", Path = @"D:\Software\Programs\Windhawk\windhawk.exe", Arguments = "-tray-only", MachineLevel = true }] };
        config.SystemSettings = new SystemSettingsConfig { Enabled = true, ComputerName = new() { Apply = true, Value = "iPixelGalaxy-PC" }, Transparency = new() { Apply = true, Value = true }, UncAsIntranet = new() { Apply = true, Value = 1 }, SaveZoneInformation = new() { Apply = true, Value = 1 }, ConsentPromptBehaviorAdmin = new() { Apply = true, Value = 5 }, PromptOnSecureDesktop = new() { Apply = true, Value = 0 }, EnableLua = new() { Apply = true, Value = 1 } };
        config.Firewall = new FirewallConfig { Enabled = true, BackupPath = Path.Combine(data, "Firewall", "firewall-rules.wfw"), RestoreBackup = true };
        config.SetupTasks = new SetupTasksConfig { Enabled = true };
    }
    private static List<FileCopyOperation> DefaultFileOperations(string data)
    {
        var source = Path.Combine(data, "FilesAndShortcuts");
        return [
            new() { Name = "Start menu shortcuts", Source = Path.Combine(source, "Shortcuts"), SearchPattern = "*.lnk", MatchingFiles = true, Destination = @"%APPDATA%\Microsoft\Windows\Start Menu\Programs", RewriteShortcutProfilePaths = true },
            new() { Name = "Startup shortcuts", Source = Path.Combine(source, "Startup"), SearchPattern = "*.lnk", MatchingFiles = true, Destination = @"%PROGRAMDATA%\Microsoft\Windows\Start Menu\Programs\Startup", RewriteShortcutProfilePaths = true },
            new() { Name = "SSH Ed25519 key", Source = Path.Combine(source, "Misc", "id_ed25519"), Destination = @"%USERPROFILE%\.ssh\id_ed25519", ProtectPrivateKeyAcl = true },
            new() { Name = "SSH ECDSA key", Source = Path.Combine(source, "Misc", "id_ecdsa"), Destination = @"%USERPROFILE%\.ssh\github_key", ProtectPrivateKeyAcl = true },
            new() { Name = "SSH known hosts", Source = Path.Combine(source, "Misc", "known_hosts"), Destination = @"%USERPROFILE%\.ssh\known_hosts" },
            new() { Name = "mpv config", Source = Path.Combine(source, "Misc", "mpv.conf"), Destination = @"%APPDATA%\mpv\mpv.conf" },
            new() { Name = "Jellyfin mpv config", Source = Path.Combine(source, "Misc", "mpv.conf"), Destination = @"%LOCALAPPDATA%\Jellyfin Media Player\mpv.conf" },
            new() { Name = "Plex mpv config", Source = Path.Combine(source, "Misc", "mpv.conf"), Destination = @"%LOCALAPPDATA%\Plex\mpv.conf" }
        ];
    }
    private static void CopyFiles(string source, string destination, IEnumerable<string> patterns)
    {
        if (!Directory.Exists(source)) return;
        Directory.CreateDirectory(destination);
        foreach (var pattern in patterns) foreach (var file in Directory.GetFiles(source, pattern)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
    }
}
