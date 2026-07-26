using System.Diagnostics;

namespace Winstaller.Utilities;

public sealed class RunSessionCoordinator : IAsyncDisposable
{
    private static readonly AsyncLocal<RunSessionCoordinator?> CurrentSession = new();
    public static RunSessionCoordinator? Current => CurrentSession.Value;
    private bool _restartExplorer;
    private bool _refreshPolicy;
    public bool RebootRequired { get; private set; }
    public void RequestExplorerRestart() => _restartExplorer = true;
    public void RequestPolicyRefresh() => _refreshPolicy = true;
    public void RequestReboot() => RebootRequired = true;
    public void Activate() => CurrentSession.Value = this;
    public async Task FlushAsync()
    {
        if (_refreshPolicy) await RunAsync("gpupdate.exe", "/force");
        if (_restartExplorer)
        {
            await RunAsync("taskkill.exe", "/IM explorer.exe /F");
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        }
        if (RebootRequired) ConsoleHelper.WriteWarning("Restart Windows to finish computer-name change.");
    }
    private static async Task RunAsync(string file, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false, CreateNoWindow = true });
        if (process is not null) await process.WaitForExitAsync();
    }
    public ValueTask DisposeAsync() { if (ReferenceEquals(CurrentSession.Value, this)) CurrentSession.Value = null; return ValueTask.CompletedTask; }
}
