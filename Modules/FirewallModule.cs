using System.Diagnostics;
using Winstaller.Configuration;
using Winstaller.Utilities;

namespace Winstaller.Modules;

public class FirewallModule : ModuleBase
{
    public FirewallModule(WinstallerConfig config) : base(config) { }
    public override string Name => "Firewall";
    public override string Description => "Captures and restores managed Windows Firewall policy";
    public override bool IsEnabled => Config.Firewall.Enabled;
    public override async Task<bool> ExecuteAsync()
    {
        if (!IsEnabled || !Config.Firewall.RestoreBackup) return !IsEnabled ? false : true;
        var backup = ExpandEnvironmentVariables(Config.Firewall.BackupPath);
        if (!File.Exists(backup)) { ConsoleHelper.WriteError("Firewall backup missing."); return false; }
        return await RunNetshAsync($"advfirewall import \"{backup}\"") == 0;
    }
    public async Task<bool> CaptureAsync()
    {
        var backup = ExpandEnvironmentVariables(Config.Firewall.BackupPath);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        var temporary = backup + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (await RunNetshAsync($"advfirewall export \"{temporary}\"") != 0 || !File.Exists(temporary) || new FileInfo(temporary).Length == 0) return false;
            if (File.Exists(backup)) File.Replace(temporary, backup, backup + ".bak", true); else File.Move(temporary, backup);
            return true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static async Task<int> RunNetshAsync(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("netsh.exe", arguments) { UseShellExecute = false, CreateNoWindow = true });
        if (process is null) return -1;
        await process.WaitForExitAsync(); return process.ExitCode;
    }
}
