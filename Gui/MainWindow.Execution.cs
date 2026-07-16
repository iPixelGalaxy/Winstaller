using System.Diagnostics;
using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using CommunityToolkit.WinUI.Controls;
using Winstaller.Models;
using Winstaller.Configuration;
using Winstaller.Modules;
using Winstaller.Utilities;
using Windows.Foundation;
using WinRT.Interop;

namespace Winstaller.Gui;

public sealed partial class MainWindow : Window
{
private async Task ConfirmAndRunModulesAsync(IReadOnlyList<ModuleDescriptor> modules)
    {
        if (modules.Count == 0)
        {
            AppendOutput("No modules selected.");
            return;
        }

        var moduleNames = string.Join(Environment.NewLine, modules.Select(module => $"- {module.Name}"));
        var confirmed = await ConfirmAsync(
            modules.Count == 1 ? "Run module?" : "Run enabled modules?",
            modules.Count == 1
                ? $"This will run {modules[0].Name}."
                : $"This will run {modules.Count} module(s):{Environment.NewLine}{moduleNames}",
            "Run");

        if (confirmed)
        {
            await RunModulesWithOutputDialogAsync(modules);
        }
    }


    private async Task RunModulesWithOutputDialogAsync(IReadOnlyList<ModuleDescriptor> modules)
    {
        var logDialogWidth = GetLogDialogWidth();
        var outputBox = CreateLogOutputBox(logDialogWidth);

        var progress = new ProgressBar
        {
            IsIndeterminate = true,
            MinWidth = logDialogWidth
        };
        var copyLogButton = ActionButton("Copy Full Log", () => CopyTextFromFile(RunLog.Path));
        copyLogButton.IsEnabled = false;
        var openFolderButton = ActionButton("Open Log Folder", () => OpenFolder(Path.GetDirectoryName(RunLog.Path) ?? BootstrapManager.LogsDirectory));
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Width = logDialogWidth,
            Children = { openFolderButton, copyLogButton }
        };
        var content = new StackPanel
        {
            Spacing = 12,
            Width = logDialogWidth,
            Children = { progress, outputBox, footer }
        };

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = modules.Count == 1 ? $"Running {modules[0].Name}" : $"Running {modules.Count} modules",
            Content = content,
            CloseButtonText = string.Empty,
            DefaultButton = ContentDialogButton.None
        };
        dialog.Resources["ContentDialogMinWidth"] = logDialogWidth;
        dialog.Resources["ContentDialogMaxWidth"] = logDialogWidth + 80;

        _activeOutputBox = outputBox;
        var dialogTask = dialog.ShowAsync().AsTask();
        try
        {
            await RunModulesAsync(modules);
        }
        finally
        {
            await RunOnUiThreadAsync(() =>
            {
                FlushOutputText(outputBox);
                _activeOutputBox = null;
                progress.IsIndeterminate = false;
                copyLogButton.IsEnabled = true;
                dialog.CloseButtonText = "Done";
            });
        }

        await dialogTask;
    }

    private async Task RunModulesAsync(IReadOnlyList<ModuleDescriptor> modules)
    {
        if (_isRunning)
        {
            AppendOutput("Operation already running.");
            return;
        }

        if (modules.Count == 0)
        {
            AppendOutput("No modules selected.");
            return;
        }

        BeginLongOperation();
        AppendOutput($"Starting {modules.Count} module(s). Log: {RunLog.Path}");

        try
        {
            await PaintBusyIndicatorAsync();
            await Task.Run(async () =>
            {
                var originalOut = Console.Out;
                var originalError = Console.Error;
                var originalIn = Console.In;
                using var writer = new BufferedTextBoxWriter(AppendOutputText, "Run");
                using var reader = new StringReader(string.Join(Environment.NewLine, Enumerable.Repeat("n", 100)));

                try
                {
                    Console.SetOut(writer);
                    Console.SetError(writer);
                    Console.SetIn(reader);

                    foreach (var descriptor in modules)
                    {
                        AppendOutput("");
                        AppendOutput($"== {descriptor.Name} ==");
                        RunLog.Write("Run", $"Starting module: {descriptor.Name}");
                        try
                        {
                            var succeeded = await descriptor.CreateModule().ExecuteAsync().ConfigureAwait(false);
                            var result = succeeded ? "Completed successfully." : "Completed with errors.";
                            RunLog.Write("Run", $"{descriptor.Name}: {result}");
                            AppendOutput(result);
                        }
                        catch (Exception ex)
                        {
                            RunLog.WriteException("Run", $"{descriptor.Name} failed", ex);
                            AppendOutput($"Failed: {ex.Message}");
                        }
                    }
                }
                finally
                {
                    writer.Flush();
                    Console.SetOut(originalOut);
                    Console.SetError(originalError);
                    Console.SetIn(originalIn);
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            await RunOnUiThreadAsync(EndLongOperation);
            AppendOutput($"Run finished. Log: {RunLog.Path}");
        }
    }
}

