namespace Winstaller.Models;

public class AppliedSetting<T>
{
    public bool Apply { get; set; }
    public T Value { get; set; } = default!;
}

public class SystemSettingsConfig
{
    public bool Enabled { get; set; }
    public AppliedSetting<string> ComputerName { get; set; } = new() { Apply = true, Value = Environment.MachineName };
    public AppliedSetting<bool> Transparency { get; set; } = new() { Apply = true, Value = true };
    public AppliedSetting<int> UncAsIntranet { get; set; } = new() { Apply = true, Value = 1 };
    public AppliedSetting<int> SaveZoneInformation { get; set; } = new() { Apply = true, Value = 1 };
    public AppliedSetting<UacLevel> Uac { get; set; } = new() { Apply = true, Value = UacLevel.NotifyAppsWithoutSecureDesktop };
}

public enum UacLevel
{
    NeverNotify,
    NotifyAppsWithoutSecureDesktop,
    NotifyAppsWithSecureDesktop,
    AlwaysNotify
}
