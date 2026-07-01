using Winstaller.Configuration;
using Winstaller.Models;
using Winstaller.Utilities;
using Microsoft.Win32;

namespace Winstaller.Modules;

/// <summary>
/// Module for configuring Windows shell folders (Desktop, Documents, etc.)
/// </summary>
public class ShellFoldersModule : ModuleBase
{
    public ShellFoldersModule(WinstallerConfig config) : base(config) { }

    public override string Name => "Shell Folders";
    public override string Description => "Configures Windows shell folders (Desktop, Documents, Downloads, etc.)";
    public override bool IsEnabled => Config.ShellFolders.Enabled;

    public override async Task<bool> ExecuteAsync()
    {
        if (!IsEnabled)
        {
            ConsoleHelper.WriteWarning("Shell Folders module is disabled in configuration.");
            return false;
        }

        ConsoleHelper.WriteHeader("Configuring Shell Folders");

        if (Config.ShellFolders.Folders.Count == 0)
        {
            Console.WriteLine("No shell folders configured.");
            return true;
        }

        var success = true;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", true);
            if (key == null)
            {
                ConsoleHelper.WriteError("Failed to open User Shell Folders registry key");
                return false;
            }

            foreach (var folder in Config.ShellFolders.Folders)
            {
                var path = ExpandEnvironmentVariables(folder.Path);
                Console.WriteLine($"  Setting {folder.FolderName} -> {path}");

                try
                {
                    // Ensure the target directory exists
                    if (!Directory.Exists(path))
                    {
                        Directory.CreateDirectory(path);
                        Console.WriteLine($"    Created directory: {path}");
                    }

                    key.SetValue(folder.RegistryValue, path, RegistryValueKind.ExpandString);
                    ConsoleHelper.WriteSuccess($"    Configured {folder.FolderName}");
                }
                catch (Exception ex)
                {
                    ConsoleHelper.WriteError($"    Failed to configure {folder.FolderName}: {ex.Message}");
                    success = false;
                }
            }
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Failed to configure shell folders: {ex.Message}");
            success = false;
        }

        // Restart Explorer to apply changes
        if (success && ConsoleHelper.Confirm("\nRestart Explorer to apply changes?"))
        {
            Console.WriteLine("Restarting Explorer...");
            await RunCmdAsync("taskkill /IM explorer.exe /F", 5000);
            await Task.Delay(1000);
            await RunCmdAsync("start explorer.exe", 5000);
        }

        return success;
    }

    protected override List<MenuOption> GetMenuOptions()
    {
        return
        [
            new MenuOption("Apply Shell Folder Configuration", ExecuteAsync),
            new MenuOption("List Configured Folders", ListConfiguredFolders),
            new MenuOption("Show Current System Folders", ShowCurrentFolders),
            new MenuOption("Show Configuration", ShowConfiguration)
        ];
    }

    private Task ListConfiguredFolders()
    {
        ConsoleHelper.WriteSubHeader($"Configured Shell Folders ({Config.ShellFolders.Folders.Count})");

        if (Config.ShellFolders.Folders.Count == 0)
        {
            Console.WriteLine("No shell folders configured.");
            return Task.CompletedTask;
        }

        foreach (var folder in Config.ShellFolders.Folders)
        {
            Console.WriteLine($"  {folder.FolderName}:");
            Console.WriteLine($"    Registry: {folder.RegistryValue}");
            Console.WriteLine($"    Path: {folder.Path}");
            Console.WriteLine();
        }

        return Task.CompletedTask;
    }

    private Task ShowCurrentFolders()
    {
        ConsoleHelper.WriteSubHeader("Current System Shell Folders");

        var knownFolders = new Dictionary<string, string>
        {
            { "Desktop", "Desktop" },
            { "Downloads", "{374DE290-123F-4565-9164-39C4925E467B}" },
            { "Documents", "Personal" },
            { "Pictures", "My Pictures" },
            { "Music", "My Music" },
            { "Videos", "My Video" }
        };

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            if (key == null)
            {
                ConsoleHelper.WriteError("Failed to read shell folders");
                return Task.CompletedTask;
            }

            foreach (var (name, regValue) in knownFolders)
            {
                var value = key.GetValue(regValue) as string ?? "(not set)";
                Console.WriteLine($"  {name}: {value}");
            }
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Error reading shell folders: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private Task ShowConfiguration()
    {
        ConsoleHelper.WriteSubHeader("Shell Folders Configuration");
        Console.WriteLine($"\n  Enabled: {Config.ShellFolders.Enabled}");
        Console.WriteLine($"  Configured Folders: {Config.ShellFolders.Folders.Count}");
        return Task.CompletedTask;
    }
}
