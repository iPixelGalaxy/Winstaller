using System.Diagnostics;
using Winstaller.Configuration;
using Winstaller.Utilities;

namespace Winstaller.Modules;

public class SetupTasksModule : ModuleBase
{
    private const string WindhawkSteam = "windhawk-steam";
    private const string DesktopPlus = "desktop-plus-uiaccess";
    private readonly SetupTaskStateStore _state = new();
    public SetupTasksModule(WinstallerConfig config) : base(config) { }
    public override string Name => "Setup Tasks";
    public override string Description => "Runs one-time Windhawk, Steam, and Desktop+ initialization";
    public override bool IsEnabled => Config.SetupTasks.Enabled;
    public override async Task<bool> ExecuteAsync()
    {
        if (!IsEnabled) return false;
        var success = true;
        if (Config.SetupTasks.WindhawkAndSteam && !_state.IsComplete(WindhawkSteam)) success &= await RunWindhawkSteamAsync();
        if (Config.SetupTasks.DesktopPlusUiAccess && !_state.IsComplete(DesktopPlus)) success &= await RunDesktopPlusAsync();
        return success;
    }
    public async Task<bool> RunAgainAsync(string task)
    {
        _state.Clear(task);
        return task == WindhawkSteam ? await RunWindhawkSteamAsync() : task == DesktopPlus ? await RunDesktopPlusAsync() : false;
    }
    private async Task<bool> RunWindhawkSteamAsync()
    {
        try
        {
            Start(Config.SetupTasks.WindhawkPath); Start(Config.SetupTasks.SteamPath);
            await Task.Delay(TimeSpan.FromSeconds(3));
            foreach (var process in Matching("windhawk", "steam")) TryClose(process);
            await Task.Delay(TimeSpan.FromSeconds(2));
            foreach (var process in Matching("windhawk", "steam")) { try { process.Kill(); } catch { } }
            _state.Complete(WindhawkSteam); return true;
        }
        catch (Exception ex) { ConsoleHelper.WriteError(ex.Message); return false; }
    }
    private async Task<bool> RunDesktopPlusAsync()
    {
        var path = ExpandEnvironmentVariables(Config.SetupTasks.DesktopPlusBatchPath);
        if (!File.Exists(path)) { ConsoleHelper.WriteError("Desktop+ UIAccess script missing."); return false; }
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{path}\"") { UseShellExecute = false, CreateNoWindow = true });
        if (process is null) return false;
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) return false;
        _state.Complete(DesktopPlus); return true;
    }
    private static void Start(string path) { if (!File.Exists(Environment.ExpandEnvironmentVariables(path))) throw new FileNotFoundException("Setup executable missing", path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
    private static IEnumerable<Process> Matching(params string[] names) => Process.GetProcesses().Where(p => names.Any(name => p.ProcessName.Contains(name, StringComparison.OrdinalIgnoreCase)));
    private static void TryClose(Process process) { try { process.CloseMainWindow(); } catch { } }
}
