namespace Winstaller.Models;

/// <summary>
/// Configuration for startup programs
/// </summary>
public class StartupConfig
{
    public bool Enabled { get; set; } = true;
    public List<StartupProgram> Programs { get; set; } = [];
    public List<ProcessToRun> ProcessesToRun { get; set; } = [];
}
