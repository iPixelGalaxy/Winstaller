using Microsoft.Win32;
using System.Management;
using Winstaller.Configuration;
using Winstaller.Models;
using Winstaller.Utilities;

namespace Winstaller.Modules;

public class SystemSettingsModule : ModuleBase
{
    private readonly RunSessionCoordinator? _session;
    public SystemSettingsModule(WinstallerConfig config, RunSessionCoordinator? session = null) : base(config) => _session = session ?? RunSessionCoordinator.Current;
    public override string Name => "System Settings";
    public override string Description => "Applies selected computer, appearance, network, attachment, and UAC settings";
    public override bool IsEnabled => Config.SystemSettings.Enabled;
    public override Task<bool> ExecuteAsync()
    {
        if (!IsEnabled) return Task.FromResult(false);
        var settings = Config.SystemSettings;
        var changedPolicy = false;
        try
        {
            if (settings.ComputerName.Apply && !Environment.MachineName.Equals(settings.ComputerName.Value, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(settings.ComputerName.Value) || settings.ComputerName.Value.Length > 15) throw new ArgumentException("Computer name must be 1-15 characters.");
                using var computer = new ManagementObject($"Win32_ComputerSystem.Name='{Environment.MachineName}'");
                computer.InvokeMethod("Rename", [settings.ComputerName.Value]);
                _session?.RequestReboot();
            }
            if (settings.Transparency.Apply && SetDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", settings.Transparency.Value ? 1 : 0)) _session?.RequestExplorerRestart();
            changedPolicy |= SetApplied(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Internet Settings\ZoneMap", "UNCAsIntranet", settings.UncAsIntranet);
            changedPolicy |= SetApplied(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Policies\Attachments", "SaveZoneInformation", settings.SaveZoneInformation);
            if (settings.Uac.Apply)
            {
                var uac = settings.Uac.Value switch
                {
                    UacLevel.NeverNotify => (ConsentPromptBehaviorAdmin: 0, PromptOnSecureDesktop: 0, EnableLua: 1),
                    UacLevel.NotifyAppsWithoutSecureDesktop => (ConsentPromptBehaviorAdmin: 5, PromptOnSecureDesktop: 0, EnableLua: 1),
                    UacLevel.NotifyAppsWithSecureDesktop => (ConsentPromptBehaviorAdmin: 5, PromptOnSecureDesktop: 1, EnableLua: 1),
                    UacLevel.AlwaysNotify => (ConsentPromptBehaviorAdmin: 2, PromptOnSecureDesktop: 1, EnableLua: 1),
                    _ => throw new ArgumentOutOfRangeException(nameof(settings.Uac.Value))
                };
                const string systemPolicy = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
                changedPolicy |= SetDword(Registry.LocalMachine, systemPolicy, "ConsentPromptBehaviorAdmin", uac.ConsentPromptBehaviorAdmin);
                changedPolicy |= SetDword(Registry.LocalMachine, systemPolicy, "PromptOnSecureDesktop", uac.PromptOnSecureDesktop);
                changedPolicy |= SetDword(Registry.LocalMachine, systemPolicy, "EnableLUA", uac.EnableLua);
            }
            if (changedPolicy) _session?.RequestPolicyRefresh();
            return Task.FromResult(true);
        }
        catch (Exception ex) { ConsoleHelper.WriteError(ex.Message); return Task.FromResult(false); }
    }
    private static bool SetApplied(RegistryKey root, string key, string name, AppliedSetting<int> setting) => setting.Apply && SetDword(root, key, name, setting.Value);
    private static bool SetDword(RegistryKey root, string key, string name, int value)
    {
        using var subkey = root.CreateSubKey(key, true)!;
        if (Convert.ToInt32(subkey.GetValue(name, int.MinValue)) == value) return false;
        subkey.SetValue(name, value, RegistryValueKind.DWord); return true;
    }
}
