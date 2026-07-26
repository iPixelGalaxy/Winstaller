namespace Winstaller.Models;

public class AppliedSetting<T>
{
    public bool Apply { get; set; }
    public T Value { get; set; } = default!;
}

public class SystemSettingsConfig
{
    public bool Enabled { get; set; }
    public AppliedSetting<string> ComputerName { get; set; } = new();
    public AppliedSetting<bool> Transparency { get; set; } = new();
    public AppliedSetting<int> UncAsIntranet { get; set; } = new();
    public AppliedSetting<int> SaveZoneInformation { get; set; } = new();
    public AppliedSetting<int> ConsentPromptBehaviorAdmin { get; set; } = new();
    public AppliedSetting<int> PromptOnSecureDesktop { get; set; } = new();
    public AppliedSetting<int> EnableLua { get; set; } = new();
}
