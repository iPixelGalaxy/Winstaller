using Winstaller.Configuration;
using Winstaller.Models;
using Winstaller.Utilities;
using Microsoft.Win32;

namespace Winstaller.Modules;

/// <summary>
/// Module for configuring startup programs and running initial processes
/// </summary>
public class StartupModule : ModuleBase
{
    public StartupModule(WinstallerConfig config) : base(config) { }

    public override string Name => "Startup";
    public override string Description => "Configures startup programs and runs initial setup processes";
    public override bool IsEnabled => Config.Startup.Enabled;

    public override async Task<bool> ExecuteAsync()
    {
        if (!IsEnabled)
        {
            ConsoleHelper.WriteWarning("Startup module is disabled in configuration.");
            return false;
        }

        EnsureAdministrator();

        ConsoleHelper.WriteHeader("Startup Configuration");

        var success = true;

        // Configure startup programs
        if (Config.Startup.Programs.Count > 0)
        {
            ConsoleHelper.WriteSubHeader("Configuring Startup Programs");
            foreach (var startup in Config.Startup.Programs)
            {
                Console.WriteLine($"  Adding {startup.Name} to startup...");

                try
                {
                    var keyPath = startup.MachineLevel
                        ? @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"
                        : @"Software\Microsoft\Windows\CurrentVersion\Run";

                    var root = startup.MachineLevel ? Registry.LocalMachine : Registry.CurrentUser;

                    using var key = root.OpenSubKey(keyPath, true);
                    if (key == null)
                    {
                        ConsoleHelper.WriteError($"    Failed to open Run registry key");
                        success = false;
                        continue;
                    }

                    var value = string.IsNullOrEmpty(startup.Arguments)
                        ? $"\"{ExpandEnvironmentVariables(startup.Path)}\""
                        : $"\"{ExpandEnvironmentVariables(startup.Path)}\" {startup.Arguments}";

                    key.SetValue(startup.Name, value);
                    ConsoleHelper.WriteSuccess($"    Added {startup.Name}");
                }
                catch (Exception ex)
                {
                    ConsoleHelper.WriteError($"    Failed to add {startup.Name}: {ex.Message}");
                    success = false;
                }
            }
        }

        // Run processes
        if (Config.Startup.ProcessesToRun.Count > 0)
        {
            ConsoleHelper.WriteSubHeader("Running Startup Processes");
            foreach (var proc in Config.Startup.ProcessesToRun)
            {
                var path = ExpandEnvironmentVariables(proc.Path);
                Console.WriteLine($"  Running {Path.GetFileName(path)}...");

                try
                {
                    var processInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = path,
                        Arguments = proc.Arguments,
                        UseShellExecute = true
                    };

                    var process = System.Diagnostics.Process.Start(processInfo);

                    if (proc.WaitForExit && process != null)
                    {
                        await process.WaitForExitAsync();
                    }
                    else if (proc.KillAfterSeconds.HasValue && process != null)
                    {
                        await Task.Delay(proc.KillAfterSeconds.Value * 1000);
                        try
                        {
                            process.Kill();
                            Console.WriteLine($"    Killed after {proc.KillAfterSeconds.Value} seconds");
                        }
                        catch { }
                    }

                    ConsoleHelper.WriteSuccess($"    Completed {Path.GetFileName(path)}");
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

    protected override List<MenuOption> GetMenuOptions()
    {
        return
        [
            new MenuOption("Apply All Startup Configuration", ExecuteAsync),
            new MenuOption("Configure Startup Programs Only", ConfigureStartupProgramsOnly),
            new MenuOption("Run Processes Only", RunProcessesOnly),
            new MenuOption("List Current Startup Programs", ListCurrentStartup),
            new MenuOption("List Configuration", ListConfiguration)
        ];
    }

    private Task ConfigureStartupProgramsOnly()
    {
        EnsureAdministrator();
        ConsoleHelper.WriteSubHeader("Configuring Startup Programs");

        foreach (var startup in Config.Startup.Programs)
        {
            Console.WriteLine($"  Adding {startup.Name}...");

            try
            {
                var keyPath = startup.MachineLevel
                    ? @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"
                    : @"Software\Microsoft\Windows\CurrentVersion\Run";

                var root = startup.MachineLevel ? Registry.LocalMachine : Registry.CurrentUser;

                using var key = root.OpenSubKey(keyPath, true);
                if (key != null)
                {
                    var value = string.IsNullOrEmpty(startup.Arguments)
                        ? $"\"{ExpandEnvironmentVariables(startup.Path)}\""
                        : $"\"{ExpandEnvironmentVariables(startup.Path)}\" {startup.Arguments}";

                    key.SetValue(startup.Name, value);
                    ConsoleHelper.WriteSuccess($"    Added");
                }
            }
            catch (Exception ex)
            {
                ConsoleHelper.WriteError($"    Error: {ex.Message}");
            }
        }

        return Task.CompletedTask;
    }

    private async Task RunProcessesOnly()
    {
        ConsoleHelper.WriteSubHeader("Running Processes");

        foreach (var proc in Config.Startup.ProcessesToRun)
        {
            var path = ExpandEnvironmentVariables(proc.Path);
            Console.WriteLine($"  Running {Path.GetFileName(path)}...");

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    Arguments = proc.Arguments,
                    UseShellExecute = true
                };

                var process = System.Diagnostics.Process.Start(psi);

                if (proc.WaitForExit && process != null)
                {
                    await process.WaitForExitAsync();
                }
                else if (proc.KillAfterSeconds.HasValue && process != null)
                {
                    await Task.Delay(proc.KillAfterSeconds.Value * 1000);
                    try { process.Kill(); } catch { }
                }

                ConsoleHelper.WriteSuccess($"    Done");
            }
            catch (Exception ex)
            {
                ConsoleHelper.WriteError($"    Error: {ex.Message}");
            }
        }
    }

    private Task ListCurrentStartup()
    {
        ConsoleHelper.WriteSubHeader("Current Startup Programs");

        Console.WriteLine("\nUser Startup:");
        try
        {
            using var userKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (userKey != null)
            {
                foreach (var name in userKey.GetValueNames())
                {
                    var value = userKey.GetValue(name) as string;
                    Console.WriteLine($"  {name}: {value}");
                }
            }
        }
        catch { }

        Console.WriteLine("\nMachine Startup:");
        try
        {
            using var machineKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
            if (machineKey != null)
            {
                foreach (var name in machineKey.GetValueNames())
                {
                    var value = machineKey.GetValue(name) as string;
                    Console.WriteLine($"  {name}: {value}");
                }
            }
        }
        catch { }

        return Task.CompletedTask;
    }

    private Task ListConfiguration()
    {
        ConsoleHelper.WriteSubHeader("Startup Configuration");

        Console.WriteLine($"\nStartup Programs ({Config.Startup.Programs.Count}):");
        foreach (var prog in Config.Startup.Programs)
        {
            var level = prog.MachineLevel ? "[Machine]" : "[User]";
            Console.WriteLine($"  {level} {prog.Name}: {prog.Path}");
        }

        Console.WriteLine($"\nProcesses to Run ({Config.Startup.ProcessesToRun.Count}):");
        foreach (var proc in Config.Startup.ProcessesToRun)
        {
            var mode = proc.WaitForExit ? "[Wait]" : (proc.KillAfterSeconds.HasValue ? $"[Kill after {proc.KillAfterSeconds}s]" : "[Fire & Forget]");
            Console.WriteLine($"  {mode} {proc.Path}");
        }

        return Task.CompletedTask;
    }
}
