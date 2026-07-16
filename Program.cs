using Winstaller.Configuration;
using Winstaller.Gui;
using Winstaller.Modules;
using Winstaller.Utilities;
using System.Runtime.InteropServices;

namespace Winstaller;

internal class Program
{
    private static WinstallerConfig _config = null!;
    private static List<IModule> _modules = null!;
    private static AppDataSymlinkUtility _appDataUtility = null!;
    private static SystemScannerUtility _systemScanner = null!;
    private static WindowsInstallerUtility _windowsInstaller = null!;

    [STAThread]
    static async Task Main(string[] args)
    {
        if (args.Length == 0)
        {
            Microsoft.UI.Xaml.Application.Start(_ =>
            {
                var app = new App();
            });
            return;
        }

        NativeConsole.EnsureAvailable();
        Console.Title = "Winstaller - Windows Setup Utility";

        // Check for --debug flag (can be combined with any other flags)
        if (args.Any(a => a.Equals("--debug", StringComparison.OrdinalIgnoreCase) ||
                         a.Equals("-d", StringComparison.OrdinalIgnoreCase)))
        {
            Logger.EnableDebug();
            Logger.Debug("Debug logging enabled");
        }

        // Load configuration
        _config = ConfigurationManager.LoadConfiguration();

        // Initialize modules
        InitializeModules();

        // Initialize utilities
        _appDataUtility = new AppDataSymlinkUtility(_config, ConfigurationManager.DefaultConfigPath);
        _systemScanner = new SystemScannerUtility(_config, ConfigurationManager.DefaultConfigPath);
        _windowsInstaller = new WindowsInstallerUtility(_config);

        // Handle command line arguments (filter out --debug/-d flags)
        var filteredArgs = args.Where(a => !a.Equals("--debug", StringComparison.OrdinalIgnoreCase) &&
                                           !a.Equals("-d", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (filteredArgs.Length > 0)
        {
            await HandleCommandLineAsync(filteredArgs);
            return;
        }

        // Show interactive console menu when explicitly requested.
        await ShowMainMenuAsync();
    }

    private static void InitializeModules()
    {
        _modules =
        [
            // Core setup modules
            new NetworkDrivesModule(_config),
            new SymlinksModule(_config),
            new AppInstallerModule(_config),

            // Personalized configuration modules (split)
            new FontsModule(_config),
            new ShellFoldersModule(_config),
            new RegistryModule(_config),
            new FileCopyModule(_config),
            new StartupModule(_config),
            new PathModule(_config),

            // Application-specific modules
            new DiscordModule(_config),
            new SpotifyModule(_config)
        ];
    }

    private static async Task HandleCommandLineAsync(string[] args)
    {
        // Check for --winpe mode first (can be combined with other flags)
        if (args.Any(a => a.Equals("--winpe", StringComparison.OrdinalIgnoreCase)))
        {
            var noUnformatted = args.Any(a => a.Equals("--no-unformatted", StringComparison.OrdinalIgnoreCase));
            await _windowsInstaller.RunWinPEFlowAsync(noUnformatted);
            return;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "--run-all":
            case "-a":
                await RunAllModulesAsync();
                break;

            case "--run":
            case "-r":
                if (args.Length > 1)
                {
                    await RunModuleByNameAsync(args[1]);
                }
                else
                {
                    Console.WriteLine("Usage: winstaller --run <module-name>");
                    ListModules();
                }
                break;

            case "--list":
            case "-l":
                ListModules();
                break;

            case "--config":
            case "-c":
                if (args.Length > 1)
                {
                    _config = ConfigurationManager.LoadConfiguration(args[1]);
                    Console.WriteLine($"Loaded configuration from {args[1]}");
                }
                else
                {
                    Console.WriteLine($"Configuration path: {ConfigurationManager.DefaultConfigPath}");
                }
                break;

            case "--generate-config":
            case "-g":
                var path = args.Length > 1 ? args[1] : ConfigurationManager.DefaultConfigPath;
                ConfigurationManager.SaveConfiguration(ConfigurationManager.CreateDefaultConfiguration(), path);
                break;

            case "--install":
            case "-i":
                await _windowsInstaller.ShowMenuAsync();
                break;

            case "--update":
            case "-u":
                var autoUpdate = args.Any(a => a.Equals("--auto", StringComparison.OrdinalIgnoreCase));
                await SelfUpdater.CheckForUpdatesAsync(autoUpdate);
                break;

            case "--version":
            case "-v":
                Console.WriteLine($"Winstaller v{SelfUpdater.CurrentVersion}");
                break;

            case "--help":
            case "-h":
            case "/?":
                ShowHelp();
                break;

            case "--console":
                await ShowMainMenuAsync();
                break;

            default:
                Console.WriteLine($"Unknown argument: {args[0]}");
                ShowHelp();
                break;
        }
    }

    private static async Task ShowMainMenuAsync()
    {
        while (true)
        {
            Console.Clear();
            ConsoleHelper.WriteHeader("Winstaller - Windows Setup Utility");

            Console.WriteLine("\n  MODULES:");
            for (int i = 0; i < _modules.Count; i++)
            {
                var module = _modules[i];
                var status = module.IsEnabled ? "+" : "-";
                Console.WriteLine($"    [{i + 1,2}] [{status}] {module.Name}");
            }

            Console.WriteLine("\n  ACTIONS:");
            Console.WriteLine($"    [A] Run All Enabled Modules");
            Console.WriteLine($"    [S] Select Modules to Run");

            Console.WriteLine("\n  UTILITIES:");
            Console.WriteLine($"    [U] AppData Symlink Manager");
            Console.WriteLine($"    [X] System Scanner (PATH/Drives/Folders/Apps)");
            Console.WriteLine($"    [W] Windows Installer (WIM/ESD)");

            Console.WriteLine("\n  SETTINGS:");
            Console.WriteLine($"    [C] View/Edit Configuration");
            Console.WriteLine($"    [R] Reload Configuration");
            Console.WriteLine($"    [G] Generate Default Config");

            Console.WriteLine("\n  [Q] Quit");

            Console.Write("\nSelect option: ");
            var input = Console.ReadLine()?.Trim().ToUpperInvariant();

            switch (input)
            {
                case "A":
                    await RunAllModulesAsync();
                    PressAnyKey();
                    break;

                case "S":
                    await SelectAndRunModulesAsync();
                    PressAnyKey();
                    break;

                case "U":
                    await _appDataUtility.ShowMenuAsync();
                    break;

                case "X":
                    await _systemScanner.ShowMenuAsync();
                    break;

                case "W":
                    await _windowsInstaller.ShowMenuAsync();
                    break;

                case "C":
                    await ShowConfigurationMenuAsync();
                    break;

                case "R":
                    _config = ConfigurationManager.LoadConfiguration();
                    InitializeModules();
                    _appDataUtility = new AppDataSymlinkUtility(_config, ConfigurationManager.DefaultConfigPath);
                    _systemScanner = new SystemScannerUtility(_config, ConfigurationManager.DefaultConfigPath);
                    _windowsInstaller = new WindowsInstallerUtility(_config);
                    Console.WriteLine("Configuration reloaded.");
                    PressAnyKey();
                    break;

                case "G":
                    ConfigurationManager.SaveConfiguration(ConfigurationManager.CreateDefaultConfiguration());
                    Console.WriteLine("Default configuration generated.");
                    PressAnyKey();
                    break;

                case "Q":
                case "":
                    Console.WriteLine("Goodbye!");
                    return;

                default:
                    if (int.TryParse(input, out int moduleIndex) && moduleIndex >= 1 && moduleIndex <= _modules.Count)
                    {
                        await _modules[moduleIndex - 1].ShowMenuAsync();
                    }
                    break;
            }
        }
    }

    private static async Task RunAllModulesAsync()
    {
        Console.Clear();
        ConsoleHelper.WriteHeader("Running All Enabled Modules");

        var enabledModules = _modules.Where(m => m.IsEnabled).ToList();

        if (enabledModules.Count == 0)
        {
            Console.WriteLine("No modules are enabled.");
            return;
        }

        Console.WriteLine($"\nWill run {enabledModules.Count} modules:");
        foreach (var module in enabledModules)
        {
            Console.WriteLine($"  - {module.Name}");
        }

        if (!ConsoleHelper.Confirm("\nProceed?"))
        {
            Console.WriteLine("Cancelled.");
            return;
        }

        var successCount = 0;
        var failCount = 0;

        foreach (var module in enabledModules)
        {
            Console.WriteLine($"\n{'=',-60}");
            Console.WriteLine($"Running: {module.Name}");
            Console.WriteLine($"{'=',-60}\n");

            try
            {
                if (await module.ExecuteAsync())
                {
                    successCount++;
                    ConsoleHelper.WriteSuccess($"\n{module.Name} completed successfully");
                }
                else
                {
                    failCount++;
                    ConsoleHelper.WriteError($"\n{module.Name} completed with errors");
                }
            }
            catch (Exception ex)
            {
                failCount++;
                ConsoleHelper.WriteError($"\n{module.Name} failed: {ex.Message}");
            }
        }

        Console.WriteLine($"\n{'=',-60}");
        ConsoleHelper.WriteHeader("Summary");
        Console.WriteLine($"Successful: {successCount}");
        Console.WriteLine($"Failed: {failCount}");
    }

    private static async Task SelectAndRunModulesAsync()
    {
        Console.Clear();
        ConsoleHelper.WriteHeader("Select Modules to Run");

        Console.WriteLine("\nAvailable modules:");
        for (int i = 0; i < _modules.Count; i++)
        {
            var module = _modules[i];
            var status = module.IsEnabled ? "Enabled" : "Disabled";
            Console.WriteLine($"  [{i + 1,2}] {module.Name} ({status})");
        }

        Console.WriteLine("\nEnter module numbers separated by commas (e.g., 1,3,5):");
        Console.WriteLine("Or enter 'all' to run all modules regardless of enabled status.");
        Console.Write("\nSelection: ");

        var input = Console.ReadLine()?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(input))
            return;

        List<IModule> selectedModules;

        if (input == "all")
        {
            selectedModules = _modules.ToList();
        }
        else
        {
            selectedModules = [];
            var parts = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var part in parts)
            {
                if (int.TryParse(part, out int index) && index >= 1 && index <= _modules.Count)
                {
                    selectedModules.Add(_modules[index - 1]);
                }
            }
        }

        if (selectedModules.Count == 0)
        {
            Console.WriteLine("No valid modules selected.");
            return;
        }

        Console.WriteLine($"\nWill run {selectedModules.Count} modules:");
        foreach (var module in selectedModules)
        {
            Console.WriteLine($"  - {module.Name}");
        }

        if (!ConsoleHelper.Confirm("\nProceed?"))
        {
            Console.WriteLine("Cancelled.");
            return;
        }

        foreach (var module in selectedModules)
        {
            Console.WriteLine($"\n{'=',-60}");
            Console.WriteLine($"Running: {module.Name}");
            Console.WriteLine($"{'=',-60}\n");

            try
            {
                await module.ExecuteAsync();
            }
            catch (Exception ex)
            {
                ConsoleHelper.WriteError($"Error: {ex.Message}");
            }
        }
    }

    private static async Task ShowConfigurationMenuAsync()
    {
        while (true)
        {
            Console.Clear();
            ConsoleHelper.WriteHeader("Configuration");

            Console.WriteLine($"\nConfiguration file: {ConfigurationManager.DefaultConfigPath}");
            Console.WriteLine($"File exists: {File.Exists(ConfigurationManager.DefaultConfigPath)}");

            Console.WriteLine("\n  [1] View Current Configuration");
            Console.WriteLine("  [2] Open Configuration in Editor");
            Console.WriteLine("  [3] Generate Default Configuration");
            Console.WriteLine("  [4] Reload Configuration");
            Console.WriteLine("\n  [0] Back");

            Console.Write("\nSelect option: ");
            var input = Console.ReadLine()?.Trim();

            switch (input)
            {
                case "1":
                    ViewConfiguration();
                    PressAnyKey();
                    break;

                case "2":
                    await OpenConfigInEditorAsync();
                    PressAnyKey();
                    break;

                case "3":
                    ConfigurationManager.SaveConfiguration(ConfigurationManager.CreateDefaultConfiguration());
                    Console.WriteLine("Default configuration saved.");
                    PressAnyKey();
                    break;

                case "4":
                    _config = ConfigurationManager.LoadConfiguration();
                    InitializeModules();
                    Console.WriteLine("Configuration reloaded.");
                    PressAnyKey();
                    break;

                case "0":
                case "":
                    return;
            }
        }
    }

    private static void ViewConfiguration()
    {
        Console.Clear();
        ConsoleHelper.WriteHeader("Current Configuration");

        Console.WriteLine("\nMODULE STATUS:");
        foreach (var module in _modules)
        {
            var status = module.IsEnabled ? "Enabled" : "Disabled";
            Console.WriteLine($"  {module.Name}: {status}");
        }

        Console.WriteLine($"\nNETWORK DRIVES:");
        Console.WriteLine($"  Configured drives: {_config.NetworkDrives.Drives.Count}");

        Console.WriteLine($"\nSYMLINKS:");
        Console.WriteLine($"  Roaming directories: {_config.Symlinks.RoamingDirectories.Count}");
        Console.WriteLine($"  Local directories: {_config.Symlinks.LocalDirectories.Count}");
        Console.WriteLine($"  LocalLow directories: {_config.Symlinks.LocalLowDirectories.Count}");
        Console.WriteLine($"  Special symlinks: {_config.Symlinks.SpecialSymlinks.Count}");

        Console.WriteLine($"\nAPP INSTALLER:");
        Console.WriteLine($"  Custom scripts: {_config.AppInstaller.CustomScripts.Count}");
        Console.WriteLine($"  Default installs: {_config.AppInstaller.DefaultInstalls.Count}");

        Console.WriteLine($"\nFONTS:");
        Console.WriteLine($"  Enabled: {_config.Fonts.Enabled}");
        Console.WriteLine($"  Directory: {_config.Fonts.FontsDirectory}");

        Console.WriteLine($"\nSHELL FOLDERS:");
        Console.WriteLine($"  Enabled: {_config.ShellFolders.Enabled}");
        Console.WriteLine($"  Configured: {_config.ShellFolders.Folders.Count}");

        Console.WriteLine($"\nREGISTRY:");
        Console.WriteLine($"  Enabled: {_config.Registry.Enabled}");
        Console.WriteLine($"  Files to import: {_config.Registry.FilesToImport.Count}");
        Console.WriteLine($"  Modifications: {_config.Registry.Modifications.Count}");

        Console.WriteLine($"\nFILE COPY:");
        Console.WriteLine($"  Enabled: {_config.FileCopy.Enabled}");
        Console.WriteLine($"  Operations: {_config.FileCopy.Operations.Count}");

        Console.WriteLine($"\nSTARTUP:");
        Console.WriteLine($"  Enabled: {_config.Startup.Enabled}");
        Console.WriteLine($"  Programs: {_config.Startup.Programs.Count}");
        Console.WriteLine($"  Processes to run: {_config.Startup.ProcessesToRun.Count}");

        Console.WriteLine($"\nPATH:");
        Console.WriteLine($"  Enabled: {_config.Path.Enabled}");
        Console.WriteLine($"  Additions: {_config.Path.Additions.Count}");

        Console.WriteLine($"\nDISCORD:");
        Console.WriteLine($"  Enabled: {_config.Discord.Enabled}");

        Console.WriteLine($"\nSPOTIFY:");
        Console.WriteLine($"  Enabled: {_config.Spotify.Enabled}");

        Console.WriteLine($"\nAPPDATA UTILITY:");
        Console.WriteLine($"  Symlink base: {_config.AppDataUtility.SymlinkBaseDirectory}");
    }

    private static async Task OpenConfigInEditorAsync()
    {
        var configPath = ConfigurationManager.DefaultConfigPath;

        if (!File.Exists(configPath))
        {
            Console.WriteLine("Configuration file doesn't exist. Generating default...");
            ConfigurationManager.SaveConfiguration(ConfigurationManager.CreateDefaultConfiguration());
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = configPath,
                UseShellExecute = true
            };

            System.Diagnostics.Process.Start(psi);
            Console.WriteLine($"Opened {configPath} in default editor.");
            Console.WriteLine("Remember to reload the configuration after making changes.");
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Failed to open editor: {ex.Message}");
            Console.WriteLine($"\nConfiguration file is at: {configPath}");
        }

        await Task.CompletedTask;
    }

    private static void ListModules()
    {
        Console.WriteLine("\nAvailable modules:");
        foreach (var module in _modules)
        {
            var status = module.IsEnabled ? "[+]" : "[-]";
            Console.WriteLine($"  {status} {module.Name.ToLowerInvariant().Replace(" ", "-")}: {module.Description}");
        }
    }

    private static async Task RunModuleByNameAsync(string name)
    {
        var normalizedName = name.ToLowerInvariant().Replace("-", " ").Replace("_", " ");
        var module = _modules.FirstOrDefault(m =>
            m.Name.ToLowerInvariant() == normalizedName ||
            m.Name.ToLowerInvariant().Replace(" ", "-") == name.ToLowerInvariant() ||
            m.Name.ToLowerInvariant().Replace(" ", "_") == name.ToLowerInvariant()
        );

        if (module == null)
        {
            Console.WriteLine($"Module not found: {name}");
            ListModules();
            return;
        }

        await module.ExecuteAsync();
    }

    private static void ShowHelp()
    {
        Console.WriteLine($@"
Winstaller v{SelfUpdater.CurrentVersion} - Windows Setup Utility

Usage:
  winstaller                    Launch GUI
  winstaller --console          Interactive console mode
  winstaller --run-all          Run all enabled modules
  winstaller --run <module>     Run a specific module
  winstaller --list             List available modules
  winstaller --config <path>    Use custom configuration file
  winstaller --generate-config  Generate default configuration
  winstaller --install          Launch Windows installer utility
  winstaller --update           Check for and install updates
  winstaller --update --auto    Auto-install updates without prompting
  winstaller --version          Show version number
  winstaller --help             Show this help

Global Flags:
  --debug, -d                   Enable debug logging (can combine with any command)

WinPE Mode:
  winstaller --winpe            Automated Windows installation from WinPE
                                - Searches all drives for install.wim/esd
                                - Auto-detects autounattend.xml
                                - Auto-selects single unformatted drive (5s timeout)

  winstaller --winpe --no-unformatted
                                Force manual disk selection even if single
                                unformatted drive is available

  winstaller --winpe --debug    Run WinPE mode with debug logging

Modules:");
        ListModules();
    }

    private static void PressAnyKey()
    {
        Console.WriteLine("\nPress any key to continue...");
        Console.ReadKey(true);
    }
}

// Extension for admin check accessible from utilities
public static class AdminHelper
{
    public static bool IsAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}

internal static class NativeConsole
{
    private const int AttachParentProcess = -1;

    public static void EnsureAvailable()
    {
        if (!AttachConsole(AttachParentProcess))
        {
            AllocConsole();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();
}
