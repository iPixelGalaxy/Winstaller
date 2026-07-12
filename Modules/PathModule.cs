using Winstaller.Configuration;
using Winstaller.Models;
using Winstaller.Utilities;
using System.Runtime.InteropServices;

namespace Winstaller.Modules;

/// <summary>
/// Module for managing PATH environment variable additions
/// </summary>
public class PathModule : ModuleBase
{
    public PathModule(WinstallerConfig config) : base(config) { }

    public override string Name => "PATH";
    public override string Description => "Adds configured directories to the system PATH environment variable";
    public override bool IsEnabled => Config.Path.Enabled;

    public override async Task<bool> ExecuteAsync()
    {
        if (!IsEnabled)
        {
            ConsoleHelper.WriteWarning("PATH module is disabled in configuration.");
            return false;
        }

        EnsureAdministrator();

        ConsoleHelper.WriteHeader("Updating PATH Environment Variable");

        if (Config.Path.Additions.Count == 0)
        {
            Console.WriteLine("No PATH additions configured.");
            return true;
        }

        try
        {
            var currentPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "";
            var pathList = currentPath.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();

            var added = 0;
            var alreadyPresent = 0;

            foreach (var addition in Config.Path.Additions)
            {
                var expandedPath = ExpandEnvironmentVariables(addition);
                if (!pathList.Contains(expandedPath, StringComparer.OrdinalIgnoreCase))
                {
                    pathList.Add(expandedPath);
                    Console.WriteLine($"  Adding: {expandedPath}");
                    added++;
                }
                else
                {
                    Console.WriteLine($"  Already present: {expandedPath}");
                    alreadyPresent++;
                }
            }

            if (added > 0)
            {
                var newPath = string.Join(";", pathList);

                Environment.SetEnvironmentVariable("PATH", newPath, EnvironmentVariableTarget.Machine);
                UpdateCurrentProcessPath(Config.Path.Additions);
                BroadcastEnvironmentChange();

                ConsoleHelper.WriteSuccess($"Added {added} entries to PATH");
                Console.WriteLine($"Already present: {alreadyPresent}");
                return true;
            }
            else
            {
                Console.WriteLine($"\nAll {alreadyPresent} paths already present in PATH");
                return true;
            }
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Failed to update PATH: {ex.Message}");
            return false;
        }
    }

    protected override List<MenuOption> GetMenuOptions()
    {
        return
        [
            new MenuOption("Add All Configured Paths", ExecuteAsync),
            new MenuOption("Show Current System PATH", ShowCurrentPath),
            new MenuOption("Show Current User PATH", ShowUserPath),
            new MenuOption("List Configured Additions", ListConfiguredAdditions),
            new MenuOption("Check Which Paths Are Missing", CheckMissingPaths)
        ];
    }

    private Task ShowCurrentPath()
    {
        ConsoleHelper.WriteSubHeader("Current System PATH");

        var path = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "";
        var entries = path.Split(';', StringSplitOptions.RemoveEmptyEntries);

        Console.WriteLine($"\n{entries.Length} entries:\n");
        foreach (var entry in entries)
        {
            var exists = Directory.Exists(entry);
            var status = exists ? "[OK]" : "[MISSING]";
            Console.WriteLine($"  {status} {entry}");
        }

        return Task.CompletedTask;
    }

    private Task ShowUserPath()
    {
        ConsoleHelper.WriteSubHeader("Current User PATH");

        var path = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        var entries = path.Split(';', StringSplitOptions.RemoveEmptyEntries);

        Console.WriteLine($"\n{entries.Length} entries:\n");
        foreach (var entry in entries)
        {
            var exists = Directory.Exists(entry);
            var status = exists ? "[OK]" : "[MISSING]";
            Console.WriteLine($"  {status} {entry}");
        }

        return Task.CompletedTask;
    }

    private Task ListConfiguredAdditions()
    {
        ConsoleHelper.WriteSubHeader($"Configured PATH Additions ({Config.Path.Additions.Count})");

        foreach (var path in Config.Path.Additions)
        {
            var expanded = ExpandEnvironmentVariables(path);
            var exists = Directory.Exists(expanded);
            var status = exists ? "[EXISTS]" : "[MISSING]";
            Console.WriteLine($"  {status} {path}");
            if (path != expanded)
            {
                Console.WriteLine($"         -> {expanded}");
            }
        }

        return Task.CompletedTask;
    }

    private Task CheckMissingPaths()
    {
        ConsoleHelper.WriteSubHeader("Checking Which Paths Are Missing from System PATH");

        var currentPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "";
        var pathList = currentPath.Split(';', StringSplitOptions.RemoveEmptyEntries);

        var missing = 0;
        var present = 0;

        foreach (var addition in Config.Path.Additions)
        {
            var expanded = ExpandEnvironmentVariables(addition);
            if (pathList.Contains(expanded, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  [PRESENT] {expanded}");
                present++;
            }
            else
            {
                ConsoleHelper.WriteWarning($"  [MISSING] {expanded}");
                missing++;
            }
        }

        Console.WriteLine($"\nPresent: {present}, Missing: {missing}");
        return Task.CompletedTask;
    }

    private static void UpdateCurrentProcessPath(IEnumerable<string> additions)
    {
        var currentPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process) ?? string.Empty;
        var paths = currentPath.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();

        foreach (var addition in additions.Select(ExpandEnvironmentVariables))
        {
            if (!paths.Contains(addition, StringComparer.OrdinalIgnoreCase))
                paths.Add(addition);
        }

        Environment.SetEnvironmentVariable("PATH", string.Join(";", paths), EnvironmentVariableTarget.Process);
    }

    private static void BroadcastEnvironmentChange()
    {
        const nint HwndBroadcast = 0xffff;
        const uint WmSettingChange = 0x001a;
        const uint SmtoAbortIfHung = 0x0002;
        SendMessageTimeout(HwndBroadcast, WmSettingChange, IntPtr.Zero, "Environment", SmtoAbortIfHung, 5000, out _);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeout(
        nint hWnd,
        uint msg,
        nint wParam,
        string lParam,
        uint flags,
        uint timeout,
        out nint result);}
