using System.Text.Json.Serialization;

namespace Winstaller.Models;

public class SetupTasksConfig
{
    public bool Enabled { get; set; }
    public List<SetupWorkflow> Workflows { get; set; } = [];
}

public sealed class SetupWorkflow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New workflow";
    public bool Enabled { get; set; } = true;
    public List<SetupTaskAction> Actions { get; set; } = [];
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StartApplicationAction), "startApplication")]
[JsonDerivedType(typeof(WaitAction), "wait")]
[JsonDerivedType(typeof(CloseApplicationAction), "closeApplication")]
[JsonDerivedType(typeof(KillApplicationAction), "killApplication")]
[JsonDerivedType(typeof(RestartApplicationAction), "restartApplication")]
[JsonDerivedType(typeof(RunScriptAction), "runScript")]
public abstract class SetupTaskAction
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
}

public sealed class StartApplicationAction : SetupTaskAction
{
    public string Name { get; set; } = "Application";
    public string Path { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
}

public sealed class WaitAction : SetupTaskAction
{
    public int Seconds { get; set; } = 1;
}

public enum SetupTaskTargetKind
{
    StartedApplication,
    ExistingProcess
}

public abstract class ProcessTargetAction : SetupTaskAction
{
    public SetupTaskTargetKind TargetKind { get; set; } = SetupTaskTargetKind.StartedApplication;
    public string StartedActionId { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
}

public sealed class CloseApplicationAction : ProcessTargetAction { }

public sealed class KillApplicationAction : ProcessTargetAction { }

public sealed class RestartApplicationAction : ProcessTargetAction
{
    public string Path { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
}

public enum SetupTaskScriptRunner
{
    Auto,
    PowerShell,
    CommandPrompt,
    Direct
}

public sealed class RunScriptAction : SetupTaskAction
{
    public string Name { get; set; } = "Script";
    public SetupTaskScriptRunner Runner { get; set; } = SetupTaskScriptRunner.Auto;
    public string Path { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public bool WaitForExit { get; set; } = true;
    public int? TimeoutSeconds { get; set; }
}

public static class SetupTasksDefaults
{
    public static SetupTasksConfig CreateLegacy(bool enabled, bool windhawkAndSteam, bool desktopPlusUiAccess, string windhawkPath, string steamPath, string desktopPlusBatchPath) => new()
    {
        Enabled = enabled,
        Workflows =
        [
            new SetupWorkflow
            {
                Id = "windhawk-steam",
                Name = "Windhawk and Steam initialization",
                Enabled = windhawkAndSteam,
                Actions =
                [
                    new StartApplicationAction { Id = "windhawk-start", Name = "Windhawk", Path = windhawkPath },
                    new StartApplicationAction { Id = "steam-start", Name = "Steam", Path = steamPath },
                    new WaitAction { Id = "wait-for-startup", Seconds = 3 },
                    new CloseApplicationAction { Id = "close-windhawk", TargetKind = SetupTaskTargetKind.ExistingProcess, ProcessName = "windhawk" },
                    new CloseApplicationAction { Id = "close-steam", TargetKind = SetupTaskTargetKind.ExistingProcess, ProcessName = "steam" },
                    new WaitAction { Id = "wait-for-close", Seconds = 2 },
                    new KillApplicationAction { Id = "kill-windhawk", TargetKind = SetupTaskTargetKind.ExistingProcess, ProcessName = "windhawk" },
                    new KillApplicationAction { Id = "kill-steam", TargetKind = SetupTaskTargetKind.ExistingProcess, ProcessName = "steam" }
                ]
            },
            new SetupWorkflow
            {
                Id = "desktop-plus-uiaccess",
                Name = "Desktop+ UIAccess",
                Enabled = desktopPlusUiAccess,
                Actions =
                [
                    new RunScriptAction { Id = "enable-uiaccess", Name = "Enable UIAccess", Runner = SetupTaskScriptRunner.CommandPrompt, Path = desktopPlusBatchPath }
                ]
            }
        ]
    };
}
