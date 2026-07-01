namespace Winstaller.Configuration;

public sealed class GeneralConfig
{
    public string Version { get; set; } = "1";
    public string Theme { get; set; } = "system";
    public Dictionary<string, bool> Modules { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
