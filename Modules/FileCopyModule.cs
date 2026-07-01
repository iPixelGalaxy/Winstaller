using Winstaller.Configuration;
using Winstaller.Models;
using Winstaller.Utilities;

namespace Winstaller.Modules;

/// <summary>
/// Module for copying files to specific destinations
/// </summary>
public class FileCopyModule : ModuleBase
{
    public FileCopyModule(WinstallerConfig config) : base(config) { }

    public override string Name => "File Copy";
    public override string Description => "Copies configured files to their destinations";
    public override bool IsEnabled => Config.FileCopy.Enabled;

    public override Task<bool> ExecuteAsync()
    {
        if (!IsEnabled)
        {
            ConsoleHelper.WriteWarning("File Copy module is disabled in configuration.");
            return Task.FromResult(false);
        }

        ConsoleHelper.WriteHeader("Copying Files");

        if (Config.FileCopy.Operations.Count == 0)
        {
            Console.WriteLine("No file copy operations configured.");
            return Task.FromResult(true);
        }

        var success = true;
        var copied = 0;

        foreach (var copyOp in Config.FileCopy.Operations)
        {
            var source = ExpandEnvironmentVariables(copyOp.Source);
            var dest = ExpandEnvironmentVariables(copyOp.Destination);

            Console.WriteLine($"  Copying to {dest}...");

            try
            {
                // Create empty file first if configured
                if (copyOp.CreateEmptyFirst)
                {
                    var destDir = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    {
                        Directory.CreateDirectory(destDir);
                    }
                    File.WriteAllText(dest, "");
                }

                // Copy the file
                if (File.Exists(source))
                {
                    File.Copy(source, dest, true);
                    ConsoleHelper.WriteSuccess($"    Copied {Path.GetFileName(source)}");
                    copied++;
                }
                else
                {
                    ConsoleHelper.WriteWarning($"    Source file not found: {source}");
                }
            }
            catch (Exception ex)
            {
                ConsoleHelper.WriteError($"    Failed to copy: {ex.Message}");
                success = false;
            }
        }

        Console.WriteLine($"\nCopied {copied}/{Config.FileCopy.Operations.Count} files");
        return Task.FromResult(success);
    }

    protected override List<MenuOption> GetMenuOptions()
    {
        return
        [
            new MenuOption("Copy All Files", ExecuteAsync),
            new MenuOption("List Configured Operations", ListOperations),
            new MenuOption("Verify Source Files Exist", VerifySourceFiles)
        ];
    }

    private Task ListOperations()
    {
        ConsoleHelper.WriteSubHeader($"File Copy Operations ({Config.FileCopy.Operations.Count})");

        if (Config.FileCopy.Operations.Count == 0)
        {
            Console.WriteLine("No operations configured.");
            return Task.CompletedTask;
        }

        foreach (var op in Config.FileCopy.Operations)
        {
            Console.WriteLine($"\n  Source: {op.Source}");
            Console.WriteLine($"  Dest:   {op.Destination}");
            Console.WriteLine($"  Create Empty First: {op.CreateEmptyFirst}");
        }

        return Task.CompletedTask;
    }

    private Task VerifySourceFiles()
    {
        ConsoleHelper.WriteSubHeader("Verifying Source Files");

        var found = 0;
        var missing = 0;

        foreach (var op in Config.FileCopy.Operations)
        {
            var source = ExpandEnvironmentVariables(op.Source);
            if (File.Exists(source))
            {
                Console.WriteLine($"  [OK] {Path.GetFileName(source)}");
                found++;
            }
            else
            {
                ConsoleHelper.WriteWarning($"  [MISSING] {source}");
                missing++;
            }
        }

        Console.WriteLine($"\nFound: {found}, Missing: {missing}");
        return Task.CompletedTask;
    }
}
