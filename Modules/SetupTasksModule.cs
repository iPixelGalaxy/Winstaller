using System.Diagnostics;
using Winstaller.Configuration;
using Winstaller.Models;
using Winstaller.Utilities;

namespace Winstaller.Modules;

public class SetupTasksModule : ModuleBase
{
    private readonly SetupTaskStateStore _state = new();

    public SetupTasksModule(WinstallerConfig config) : base(config) { }

    public override string Name => "Setup Tasks";
    public override string Description => "Runs configured one-time app and script automations";
    public override bool IsEnabled => Config.SetupTasks.Enabled;

    public override async Task<bool> ExecuteAsync()
    {
        if (!IsEnabled)
            return false;

        var success = true;
        foreach (var workflow in Config.SetupTasks.Workflows.Where(workflow => workflow.Enabled && !_state.IsComplete(workflow.Id)))
            success &= await ExecuteWorkflowAsync(workflow);
        return success;
    }

    public async Task<bool> RunAgainAsync(string workflowId)
    {
        var workflow = Config.SetupTasks.Workflows.FirstOrDefault(item => item.Id.Equals(workflowId, StringComparison.OrdinalIgnoreCase));
        if (workflow is null)
            return false;

        _state.Clear(workflow.Id);
        return await ExecuteWorkflowAsync(workflow);
    }

    private async Task<bool> ExecuteWorkflowAsync(SetupWorkflow workflow)
    {
        if (workflow.Actions.Count == 0)
        {
            ConsoleHelper.WriteError($"{workflow.Name}: no actions configured.");
            return false;
        }

        ConsoleHelper.WriteSubHeader(workflow.Name);
        var started = new Dictionary<string, StartedApplication>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var action in workflow.Actions)
            {
                var succeeded = await ExecuteActionAsync(action, started);
                if (!succeeded)
                    return false;
            }

            _state.Complete(workflow.Id);
            ConsoleHelper.WriteSuccess($"Completed {workflow.Name}");
            return true;
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"{workflow.Name}: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> ExecuteActionAsync(SetupTaskAction action, Dictionary<string, StartedApplication> started)
    {
        switch (action)
        {
            case StartApplicationAction start:
                Console.WriteLine($"  Start: {start.Name}");
                started[start.Id] = StartApplication(start.Path, start.Arguments, start.WorkingDirectory);
                return true;

            case WaitAction wait:
                if (wait.Seconds < 0)
                    throw new InvalidOperationException("Wait time cannot be negative.");
                Console.WriteLine($"  Wait: {wait.Seconds}s");
                await Task.Delay(TimeSpan.FromSeconds(wait.Seconds));
                return true;

            case CloseApplicationAction close:
                Console.WriteLine("  Close application");
                CloseApplications(close, started);
                return true;

            case KillApplicationAction kill:
                Console.WriteLine("  Kill application");
                KillApplications(kill, started);
                return true;

            case RestartApplicationAction restart:
                Console.WriteLine("  Restart application");
                RestartApplication(restart, started);
                return true;

            case RunScriptAction script:
                Console.WriteLine($"  Run script: {script.Name}");
                return await RunScriptAsync(script);

            default:
                throw new InvalidOperationException($"Unsupported setup action: {action.GetType().Name}");
        }
    }

    private StartedApplication StartApplication(string configuredPath, string arguments, string workingDirectory)
    {
        var path = ExpandEnvironmentVariables(configuredPath);
        if (!File.Exists(path))
            throw new FileNotFoundException("Setup application missing.", path);

        var process = Process.Start(new ProcessStartInfo(path, ExpandEnvironmentVariables(arguments))
        {
            UseShellExecute = true,
            WorkingDirectory = ResolveWorkingDirectory(workingDirectory, path)
        }) ?? throw new InvalidOperationException($"Could not start {path}.");
        return new StartedApplication(process, configuredPath, arguments, workingDirectory);
    }

    private void CloseApplications(ProcessTargetAction action, IReadOnlyDictionary<string, StartedApplication> started)
    {
        foreach (var process in ResolveTargets(action, started))
        {
            try { if (!process.HasExited) process.CloseMainWindow(); }
            catch { }
        }
    }

    private void KillApplications(ProcessTargetAction action, IReadOnlyDictionary<string, StartedApplication> started)
    {
        foreach (var process in ResolveTargets(action, started))
        {
            try { if (!process.HasExited) process.Kill(); }
            catch { }
        }
    }

    private void RestartApplication(RestartApplicationAction action, Dictionary<string, StartedApplication> started)
    {
        if (action.TargetKind == SetupTaskTargetKind.StartedApplication)
        {
            if (!started.TryGetValue(action.StartedActionId, out var target))
                throw new InvalidOperationException("Restart target was not started earlier in this workflow.");

            TryKill(target.Process);
            try { target.Process.WaitForExit(5000); }
            catch { }
            started[action.StartedActionId] = StartApplication(target.Path, target.Arguments, target.WorkingDirectory);
            return;
        }

        KillApplications(action, started);
        if (string.IsNullOrWhiteSpace(action.Path))
            throw new InvalidOperationException("Restarting an existing process needs a relaunch path.");
        _ = StartApplication(action.Path, action.Arguments, action.WorkingDirectory);
    }

    private static IEnumerable<Process> ResolveTargets(ProcessTargetAction action, IReadOnlyDictionary<string, StartedApplication> started)
    {
        if (action.TargetKind == SetupTaskTargetKind.StartedApplication)
        {
            if (!started.TryGetValue(action.StartedActionId, out var target))
                throw new InvalidOperationException("Application target was not started earlier in this workflow.");
            return [target.Process];
        }

        var processName = action.ProcessName.Trim();
        if (string.IsNullOrWhiteSpace(processName))
            throw new InvalidOperationException("Existing process name is required.");
        return Process.GetProcessesByName(Path.GetFileNameWithoutExtension(processName));
    }

    private async Task<bool> RunScriptAsync(RunScriptAction action)
    {
        var path = ExpandEnvironmentVariables(action.Path);
        if (!File.Exists(path))
            throw new FileNotFoundException("Setup script missing.", path);

        using var process = Process.Start(CreateScriptStartInfo(action, path));
        if (process is null)
            return false;
        if (!action.WaitForExit)
            return true;

        try
        {
            if (action.TimeoutSeconds is > 0)
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(action.TimeoutSeconds.Value));
            else
                await process.WaitForExitAsync();
        }
        catch (TimeoutException)
        {
            TryKill(process);
            ConsoleHelper.WriteError($"{action.Name}: timed out.");
            return false;
        }

        if (process.ExitCode == 0)
            return true;
        ConsoleHelper.WriteError($"{action.Name}: exit code {process.ExitCode}.");
        return false;
    }

    private static ProcessStartInfo CreateScriptStartInfo(RunScriptAction action, string path)
    {
        var runner = action.Runner == SetupTaskScriptRunner.Auto
            ? Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".ps1" => SetupTaskScriptRunner.PowerShell,
                ".bat" or ".cmd" => SetupTaskScriptRunner.CommandPrompt,
                _ => SetupTaskScriptRunner.Direct
            }
            : action.Runner;
        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = ResolveWorkingDirectory(action.WorkingDirectory, path)
        };
        var arguments = ExpandEnvironmentVariables(action.Arguments);
        switch (runner)
        {
            case SetupTaskScriptRunner.PowerShell:
                info.FileName = "powershell.exe";
                info.Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{path}\" {arguments}".Trim();
                break;
            case SetupTaskScriptRunner.CommandPrompt:
                info.FileName = "cmd.exe";
                info.ArgumentList.Add("/d");
                info.ArgumentList.Add("/s");
                info.ArgumentList.Add("/c");
                info.ArgumentList.Add($"call \"{path}\" {arguments}".Trim());
                break;
            default:
                info.FileName = path;
                info.Arguments = arguments;
                break;
        }
        return info;
    }

    private static string ResolveWorkingDirectory(string configuredDirectory, string path)
    {
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
            return Environment.ExpandEnvironmentVariables(configuredDirectory);
        return Path.GetDirectoryName(path) ?? AppContext.BaseDirectory;
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(); }
        catch { }
    }

    private sealed record StartedApplication(Process Process, string Path, string Arguments, string WorkingDirectory);
}
