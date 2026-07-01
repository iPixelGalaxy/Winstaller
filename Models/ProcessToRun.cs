namespace Winstaller.Models;

/// <summary>
/// Represents a process to run during startup
/// </summary>
public class ProcessToRun
{
    public string Path { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public bool WaitForExit { get; set; } = true;
    public int? KillAfterSeconds { get; set; }
}
