namespace Winstaller.Models;

/// <summary>
/// Represents a startup program registration
/// </summary>
public class StartupProgram
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public bool MachineLevel { get; set; } = false;
}
