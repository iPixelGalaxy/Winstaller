using Winstaller.Configuration;
using Winstaller.Models;
using Winstaller.Utilities;
using Microsoft.Win32;

namespace Winstaller.Modules;

/// <summary>
/// Module for installing fonts
/// </summary>
public class FontsModule : ModuleBase
{
    public FontsModule(WinstallerConfig config) : base(config) { }

    public override string Name => "Fonts";
    public override string Description => "Installs TrueType fonts from the configured fonts directory";
    public override bool IsEnabled => Config.Fonts.Enabled;

    public override Task<bool> ExecuteAsync()
    {
        if (!IsEnabled)
        {
            ConsoleHelper.WriteWarning("Fonts module is disabled in configuration.");
            return Task.FromResult(false);
        }

        EnsureAdministrator();

        ConsoleHelper.WriteHeader("Installing Fonts");

        var fontsDir = ExpandEnvironmentVariables(Config.Fonts.FontsDirectory);

        if (!Directory.Exists(fontsDir))
        {
            ConsoleHelper.WriteWarning($"Fonts directory not found: {fontsDir}");
            return Task.FromResult(true);
        }

        var fontFiles = Directory.GetFiles(fontsDir, "*.ttf")
            .Concat(Directory.GetFiles(fontsDir, "*.otf"))
            .ToArray();

        if (fontFiles.Length == 0)
        {
            Console.WriteLine("No fonts found to install.");
            return Task.FromResult(true);
        }

        var windowsFontsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        var success = true;
        var installed = 0;

        foreach (var fontFile in fontFiles)
        {
            var fileName = Path.GetFileName(fontFile);
            var fontName = Path.GetFileNameWithoutExtension(fontFile);
            var destPath = Path.Combine(windowsFontsDir, fileName);
            var isOtf = fontFile.EndsWith(".otf", StringComparison.OrdinalIgnoreCase);

            try
            {
                Console.WriteLine($"  Installing {fontName}...");

                // Copy font file
                File.Copy(fontFile, destPath, true);

                // Register in registry
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts", true);
                var fontType = isOtf ? "(OpenType)" : "(TrueType)";
                key?.SetValue($"{fontName} {fontType}", fileName);

                ConsoleHelper.WriteSuccess($"    Installed {fontName}");
                installed++;
            }
            catch (Exception ex)
            {
                ConsoleHelper.WriteError($"    Failed to install {fontName}: {ex.Message}");
                success = false;
            }
        }

        Console.WriteLine($"\nInstalled {installed}/{fontFiles.Length} fonts");
        return Task.FromResult(success);
    }

    protected override List<MenuOption> GetMenuOptions()
    {
        return
        [
            new MenuOption("Install All Fonts", ExecuteAsync),
            new MenuOption("List Available Fonts", ListFonts),
            new MenuOption("Show Configuration", ShowConfiguration)
        ];
    }

    private Task ListFonts()
    {
        var fontsDir = ExpandEnvironmentVariables(Config.Fonts.FontsDirectory);

        if (!Directory.Exists(fontsDir))
        {
            ConsoleHelper.WriteWarning($"Fonts directory not found: {fontsDir}");
            return Task.CompletedTask;
        }

        var fontFiles = Directory.GetFiles(fontsDir, "*.ttf")
            .Concat(Directory.GetFiles(fontsDir, "*.otf"))
            .ToArray();

        ConsoleHelper.WriteSubHeader($"Available Fonts ({fontFiles.Length})");

        foreach (var font in fontFiles)
        {
            Console.WriteLine($"  - {Path.GetFileName(font)}");
        }

        return Task.CompletedTask;
    }

    private Task ShowConfiguration()
    {
        ConsoleHelper.WriteSubHeader("Fonts Configuration");
        Console.WriteLine($"\n  Enabled: {Config.Fonts.Enabled}");
        Console.WriteLine($"  Fonts Directory: {Config.Fonts.FontsDirectory}");
        return Task.CompletedTask;
    }
}
