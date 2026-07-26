using System.Globalization;
using System.Text.Json;
using Microsoft.Win32;
using Winstaller.Configuration;
using Winstaller.Models;
using Winstaller.Utilities;

namespace Winstaller.Modules;

public class VRChatRegistryModule : ModuleBase
{
    private const string RootPath = @"SOFTWARE\VRChat\VRChat";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public VRChatRegistryModule(WinstallerConfig config) : base(config) { }
    public override string Name => "VRChat Registry";
    public override string Description => "Backs up and restores VRChat settings and personal data";
    public override bool IsEnabled => Config.VRChatRegistry.Enabled;
    public string LastMessage { get; private set; } = string.Empty;

    public override async Task<bool> ExecuteAsync()
    {
        if (!IsEnabled) return false;
        return await RestoreAsync();
    }

    public Task<bool> CaptureAsync()
    {
        try
        {
            RunLog.Write("VRChat Registry", "Capture started.");
            using var root = Registry.CurrentUser.OpenSubKey(RootPath);
            if (root is null) { LastMessage = "VRChat registry key not found."; RunLog.Write("VRChat Registry", LastMessage); ConsoleHelper.WriteError(LastMessage); return Task.FromResult(false); }
            var backup = new VRChatRegistryBackup { CapturedAt = DateTimeOffset.UtcNow };
            ReadKey(root, string.Empty, backup.Values);
            SaveBackup(backup);
            LastMessage = $"Captured {backup.Values.Count} VRChat registry values.";
            RunLog.Write("VRChat Registry", LastMessage);
            ConsoleHelper.WriteSuccess(LastMessage);
            return Task.FromResult(true);
        }
        catch (Exception ex) { LastMessage = $"VRChat capture failed: {ex.Message}"; RunLog.WriteException("VRChat Registry", "Capture failed", ex); ConsoleHelper.WriteError(LastMessage); return Task.FromResult(false); }
    }

    public Task<bool> RestoreAsync()
    {
        try
        {
            var path = ExpandEnvironmentVariables(Config.VRChatRegistry.BackupPath);
            if (!File.Exists(path)) { ConsoleHelper.WriteError("VRChat registry backup missing."); return Task.FromResult(false); }
            var backup = JsonSerializer.Deserialize<VRChatRegistryBackup>(File.ReadAllText(path), JsonOptions);
            if (backup is null) return Task.FromResult(false);
            var restored = 0;
            foreach (var value in backup.Values)
            {
                if (value.Group == VRChatRegistryGroup.Settings && !Config.VRChatRegistry.RestoreSettings) continue;
                if (value.Group == VRChatRegistryGroup.Personal && !Config.VRChatRegistry.RestorePersonalData) continue;
                if (Config.VRChatRegistry.ExcludedValueIds.Contains(GetValueId(value), StringComparer.OrdinalIgnoreCase)) continue;
                using var key = Registry.CurrentUser.CreateSubKey(string.IsNullOrWhiteSpace(value.SubKey) ? RootPath : $"{RootPath}\\{value.SubKey}", true);
                key!.SetValue(value.Name, Decode(value), value.Kind);
                restored++;
            }
            ConsoleHelper.WriteSuccess($"Restored {restored} VRChat registry values.");
            return Task.FromResult(true);
        }
        catch (Exception ex) { ConsoleHelper.WriteError($"VRChat restore failed: {ex.Message}"); return Task.FromResult(false); }
    }

    private void SaveBackup(VRChatRegistryBackup backup)
    {
        var path = ExpandEnvironmentVariables(Config.VRChatRegistry.BackupPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(backup, JsonOptions));
        if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", true); else File.Move(temporary, path);
    }

    public VRChatRegistryBackup? LoadBackup()
    {
        try
        {
            var path = ExpandEnvironmentVariables(Config.VRChatRegistry.BackupPath);
            return File.Exists(path) ? JsonSerializer.Deserialize<VRChatRegistryBackup>(File.ReadAllText(path), JsonOptions) : null;
        }
        catch { return null; }
    }

    public static string GetValueId(VRChatRegistryValue value) => $"{value.SubKey}\\{value.Name}";

    private static void ReadKey(RegistryKey key, string relativePath, List<VRChatRegistryValue> values)
    {
        foreach (var name in key.GetValueNames())
        {
            var kind = key.GetValueKind(name);
            values.Add(new VRChatRegistryValue { SubKey = relativePath, Name = name, Kind = kind, Data = Encode(key.GetValue(name), kind), Group = Classify(name) });
        }
        foreach (var child in key.GetSubKeyNames()) using (var childKey = key.OpenSubKey(child)) if (childKey is not null) ReadKey(childKey, string.IsNullOrWhiteSpace(relativePath) ? child : $"{relativePath}\\{child}", values);
    }

    private static string Encode(object? value, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.Binary => Convert.ToBase64String((byte[])(value ?? Array.Empty<byte>())),
        RegistryValueKind.MultiString => JsonSerializer.Serialize((string[])(value ?? Array.Empty<string>())),
        RegistryValueKind.DWord => ToUInt32(value).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => ToUInt64(value).ToString(CultureInfo.InvariantCulture),
        _ => value?.ToString() ?? string.Empty
    };
    private static object Decode(VRChatRegistryValue value) => value.Kind switch
    {
        RegistryValueKind.Binary => Convert.FromBase64String(value.Data),
        RegistryValueKind.MultiString => JsonSerializer.Deserialize<string[]>(value.Data) ?? [],
        RegistryValueKind.DWord => unchecked((int)uint.Parse(value.Data, CultureInfo.InvariantCulture)),
        RegistryValueKind.QWord => unchecked((long)ulong.Parse(value.Data, CultureInfo.InvariantCulture)),
        _ => value.Data
    };
    private static uint ToUInt32(object? value) => value switch
    {
        int number => unchecked((uint)number),
        uint number => number,
        long number => unchecked((uint)number),
        ulong number => unchecked((uint)number),
        _ => unchecked((uint)Convert.ToInt64(value, CultureInfo.InvariantCulture))
    };
    private static ulong ToUInt64(object? value) => value switch { long number => unchecked((ulong)number), ulong number => number, _ => Convert.ToUInt64(value, CultureInfo.InvariantCulture) };
    private static VRChatRegistryGroup Classify(string name) => name.StartsWith("usr_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("unity.", StringComparison.OrdinalIgnoreCase) || name.Contains("Session", StringComparison.OrdinalIgnoreCase) || name.Contains("Inventory", StringComparison.OrdinalIgnoreCase) || name.Contains("CustomGroup", StringComparison.OrdinalIgnoreCase) || name.Contains("History", StringComparison.OrdinalIgnoreCase) || name.StartsWith("HasSeen", StringComparison.OrdinalIgnoreCase) || name.StartsWith("FirstTime", StringComparison.OrdinalIgnoreCase) ? VRChatRegistryGroup.Personal : VRChatRegistryGroup.Settings;
}

public sealed class VRChatRegistryBackup { public DateTimeOffset CapturedAt { get; set; } public List<VRChatRegistryValue> Values { get; set; } = []; }
public sealed class VRChatRegistryValue { public string SubKey { get; set; } = string.Empty; public string Name { get; set; } = string.Empty; public RegistryValueKind Kind { get; set; } public string Data { get; set; } = string.Empty; public VRChatRegistryGroup Group { get; set; } }
public enum VRChatRegistryGroup { Settings, Personal }
