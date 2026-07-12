using Winstaller.Configuration;
using Winstaller.Utilities;

namespace Winstaller.Modules;

/// <summary>
/// Base class for all Winstaller modules providing common functionality
/// </summary>
public abstract class ModuleBase : IModule
{
    protected readonly WinstallerConfig Config;

    protected ModuleBase(WinstallerConfig config)
    {
        Config = config;
    }

    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract bool IsEnabled { get; }

    public abstract Task<bool> ExecuteAsync();

    public virtual async Task ShowMenuAsync()
    {
        while (true)
        {
            Console.Clear();
            ConsoleHelper.WriteHeader($"{Name} Module");
            Console.WriteLine($"{Description}\n");
            Console.WriteLine($"Status: {(IsEnabled ? "Enabled" : "Disabled")}\n");

            var options = GetMenuOptions();
            for (int i = 0; i < options.Count; i++)
            {
                Console.WriteLine($"  [{i + 1}] {options[i].Label}");
            }
            Console.WriteLine($"\n  [0] Back to Main Menu");

            Console.Write("\nSelect option: ");
            var input = Console.ReadLine()?.Trim();

            if (input == "0" || string.IsNullOrEmpty(input))
                return;

            if (int.TryParse(input, out int choice) && choice >= 1 && choice <= options.Count)
            {
                await options[choice - 1].Action();
                Console.WriteLine("\nPress any key to continue...");
                Console.ReadKey(true);
            }
        }
    }

    protected virtual List<MenuOption> GetMenuOptions()
    {
        return
        [
            new MenuOption("Run Module", ExecuteAsync)
        ];
    }

    protected record MenuOption(string Label, Func<Task> Action);

    #region Helper Methods

    protected static string ExpandEnvironmentVariables(string path)
    {
        var result = Environment.ExpandEnvironmentVariables(path);
        result = result.Replace("{USERNAME}", Environment.UserName);
        return result;
    }

    protected static bool IsAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    protected static void EnsureAdministrator()
    {
        if (!IsAdministrator())
        {
            ConsoleHelper.WriteWarning("This operation requires administrator privileges.");
            ConsoleHelper.WriteWarning("Please run this application as administrator.");
        }
    }

    protected static async Task<int> RunProcessAsync(string fileName, string arguments, int timeoutMs = 300000, bool showOutput = true)
    {
        try
        {
            Logger.Debug($"Running process: {fileName} {arguments}");

            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = showOutput,
                RedirectStandardError = showOutput,
                CreateNoWindow = !showOutput
            };

            process.Start();

            var outputTask = showOutput ? process.StandardOutput.ReadToEndAsync() : Task.FromResult(string.Empty);
            var errorTask = showOutput ? process.StandardError.ReadToEndAsync() : Task.FromResult(string.Empty);

            try
            {
                using var cts = new CancellationTokenSource(Math.Max(1, timeoutMs));
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }

                await Task.WhenAll(outputTask, errorTask);
                Logger.Error($"Process timed out after {timeoutMs / 1000} seconds");
                return -1;
            }

            await Task.WhenAll(outputTask, errorTask);

            if (!string.IsNullOrWhiteSpace(outputTask.Result))
                Console.WriteLine(outputTask.Result);
            if (!string.IsNullOrWhiteSpace(errorTask.Result))
                Console.Error.WriteLine(errorTask.Result);

            Logger.Debug($"Process exited with code: {process.ExitCode}");
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            Logger.Error($"Process timed out after {timeoutMs / 1000} seconds");
            return -1;
        }
        catch (Exception ex)
        {
            Logger.Exception(ex, "Error running process");
            return -1;
        }
    }

    protected static async Task<int> RunCmdAsync(string command, int timeoutMs = 300000)
    {
        return await RunProcessAsync("cmd.exe", $"/c {command}", timeoutMs);
    }

    protected static async Task<int> RunPowerShellAsync(string command, int timeoutMs = 300000)
    {
        return await RunProcessAsync("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"", timeoutMs);
    }

    #endregion
}
