using Winstaller.Configuration;
using Winstaller.Models;
using Winstaller.Utilities;
using Microsoft.Win32;

namespace Winstaller.Modules;

/// <summary>
/// Module for importing registry files and applying registry modifications
/// </summary>
public class RegistryModule : ModuleBase
{
    public RegistryModule(WinstallerConfig config) : base(config) { }

    public override string Name => "Registry";
    public override string Description => "Imports registry files and applies registry modifications";
    public override bool IsEnabled => Config.Registry.Enabled;

    public override async Task<bool> ExecuteAsync()
    {
        if (!IsEnabled)
        {
            ConsoleHelper.WriteWarning("Registry module is disabled in configuration.");
            return false;
        }

        EnsureAdministrator();

        ConsoleHelper.WriteHeader("Registry Configuration");

        var success = true;

        // Import registry files
        if (Config.Registry.FilesToImport.Count > 0)
        {
            ConsoleHelper.WriteSubHeader("Importing Registry Files");
            foreach (var regFile in Config.Registry.FilesToImport)
            {
                var path = ExpandEnvironmentVariables(regFile);
                Console.WriteLine($"  Importing {Path.GetFileName(path)}...");

                if (!File.Exists(path))
                {
                    ConsoleHelper.WriteWarning($"    Registry file not found: {path}");
                    continue;
                }

                var result = await RunCmdAsync($"regedit /s \"{path}\"", 30000);
                if (result == 0)
                {
                    ConsoleHelper.WriteSuccess($"    Imported {Path.GetFileName(path)}");
                }
                else
                {
                    ConsoleHelper.WriteError($"    Failed to import {Path.GetFileName(path)}");
                    success = false;
                }
            }
        }

        // Apply registry modifications
        if (Config.Registry.Modifications.Count > 0)
        {
            ConsoleHelper.WriteSubHeader("Applying Registry Modifications");
            foreach (var mod in Config.Registry.Modifications)
            {
                Console.WriteLine($"  Processing {mod.Key}\\{mod.ValueName}...");

                try
                {
                    if (mod.Delete)
                    {
                        var (root, subKey) = ParseRegistryKey(mod.Key);
                        using var key = root?.OpenSubKey(subKey, true);
                        key?.DeleteValue(mod.ValueName, false);
                        ConsoleHelper.WriteSuccess($"    Deleted {mod.ValueName}");
                    }
                    else
                    {
                        var (root, subKey) = ParseRegistryKey(mod.Key);
                        using var key = root?.CreateSubKey(subKey, true);

                        if (key == null)
                        {
                            ConsoleHelper.WriteError($"    Failed to open/create registry key");
                            success = false;
                            continue;
                        }

                        var valueKind = mod.ValueType.ToUpperInvariant() switch
                        {
                            "REG_DWORD" => RegistryValueKind.DWord,
                            "REG_QWORD" => RegistryValueKind.QWord,
                            "REG_EXPAND_SZ" => RegistryValueKind.ExpandString,
                            "REG_MULTI_SZ" => RegistryValueKind.MultiString,
                            "REG_BINARY" => RegistryValueKind.Binary,
                            _ => RegistryValueKind.String
                        };

                        object value = valueKind switch
                        {
                            RegistryValueKind.DWord => int.Parse(mod.Value),
                            RegistryValueKind.QWord => long.Parse(mod.Value),
                            _ => mod.Value
                        };

                        key.SetValue(mod.ValueName, value, valueKind);
                        ConsoleHelper.WriteSuccess($"    Set {mod.ValueName} = {mod.Value}");
                    }
                }
                catch (Exception ex)
                {
                    ConsoleHelper.WriteError($"    Failed: {ex.Message}");
                    success = false;
                }
            }
        }

        return success;
    }

    private static (RegistryKey? Root, string SubKey) ParseRegistryKey(string fullKey)
    {
        var parts = fullKey.Split('\\', 2);
        if (parts.Length < 2)
            return (null, "");

        var root = parts[0].ToUpperInvariant() switch
        {
            "HKEY_LOCAL_MACHINE" or "HKLM" => Registry.LocalMachine,
            "HKEY_CURRENT_USER" or "HKCU" => Registry.CurrentUser,
            "HKEY_CLASSES_ROOT" or "HKCR" => Registry.ClassesRoot,
            "HKEY_USERS" or "HKU" => Registry.Users,
            "HKEY_CURRENT_CONFIG" or "HKCC" => Registry.CurrentConfig,
            _ => null
        };

        return (root, parts[1]);
    }

    protected override List<MenuOption> GetMenuOptions()
    {
        return
        [
            new MenuOption("Apply All Registry Changes", ExecuteAsync),
            new MenuOption("Import Registry Files Only", ImportFilesOnly),
            new MenuOption("Apply Modifications Only", ApplyModificationsOnly),
            new MenuOption("List Configuration", ListConfiguration)
        ];
    }

    private async Task ImportFilesOnly()
    {
        EnsureAdministrator();
        ConsoleHelper.WriteSubHeader("Importing Registry Files");

        foreach (var regFile in Config.Registry.FilesToImport)
        {
            var path = ExpandEnvironmentVariables(regFile);
            Console.WriteLine($"  Importing {Path.GetFileName(path)}...");

            if (!File.Exists(path))
            {
                ConsoleHelper.WriteWarning($"    Not found: {path}");
                continue;
            }

            var result = await RunCmdAsync($"regedit /s \"{path}\"", 30000);
            if (result == 0)
                ConsoleHelper.WriteSuccess($"    Imported");
            else
                ConsoleHelper.WriteError($"    Failed");
        }
    }

    private Task ApplyModificationsOnly()
    {
        EnsureAdministrator();
        ConsoleHelper.WriteSubHeader("Applying Registry Modifications");

        foreach (var mod in Config.Registry.Modifications)
        {
            Console.WriteLine($"  {mod.Key}\\{mod.ValueName}...");

            try
            {
                if (mod.Delete)
                {
                    var (root, subKey) = ParseRegistryKey(mod.Key);
                    using var key = root?.OpenSubKey(subKey, true);
                    key?.DeleteValue(mod.ValueName, false);
                    ConsoleHelper.WriteSuccess($"    Deleted");
                }
                else
                {
                    var (root, subKey) = ParseRegistryKey(mod.Key);
                    using var key = root?.CreateSubKey(subKey, true);

                    if (key != null)
                    {
                        var valueKind = mod.ValueType.ToUpperInvariant() switch
                        {
                            "REG_DWORD" => RegistryValueKind.DWord,
                            "REG_QWORD" => RegistryValueKind.QWord,
                            _ => RegistryValueKind.String
                        };

                        object value = valueKind switch
                        {
                            RegistryValueKind.DWord => int.Parse(mod.Value),
                            RegistryValueKind.QWord => long.Parse(mod.Value),
                            _ => mod.Value
                        };

                        key.SetValue(mod.ValueName, value, valueKind);
                        ConsoleHelper.WriteSuccess($"    Set to {mod.Value}");
                    }
                }
            }
            catch (Exception ex)
            {
                ConsoleHelper.WriteError($"    Error: {ex.Message}");
            }
        }

        return Task.CompletedTask;
    }

    private Task ListConfiguration()
    {
        ConsoleHelper.WriteSubHeader("Registry Configuration");

        Console.WriteLine($"\nFiles to Import ({Config.Registry.FilesToImport.Count}):");
        foreach (var file in Config.Registry.FilesToImport)
            Console.WriteLine($"  - {file}");

        Console.WriteLine($"\nModifications ({Config.Registry.Modifications.Count}):");
        foreach (var mod in Config.Registry.Modifications)
        {
            var action = mod.Delete ? "[DELETE]" : $"= {mod.Value}";
            Console.WriteLine($"  - {mod.Key}\\{mod.ValueName} {action}");
        }

        return Task.CompletedTask;
    }
}
