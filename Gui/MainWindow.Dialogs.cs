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
private Task<bool> ConfirmSymlinkRemovalAsync(string name, string category)
    {
        var label = string.IsNullOrWhiteSpace(name) ? "this entry" : $"\"{name}\"";
        return ConfirmAsync(
            "Remove symlink?",
            $"Remove {label} from {category} symlink configuration? Existing files and symlinks stay untouched.",
            "Remove");
    }

    private async Task<bool> ConfirmAsync(string title, string message, string primaryText)
    {
        if (!DispatcherQueue.HasThreadAccess)
            return await RunOnUiThreadAsync(() => ConfirmAsync(title, message, primaryText));

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = primaryText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            await RunOnUiThreadAsync(() => ShowMessageAsync(title, message));
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "OK"
        };

        await dialog.ShowAsync();
    }

    private async Task ShowExceptionAsync(string title, Exception exception)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            await RunOnUiThreadAsync(() => ShowExceptionAsync(title, exception));
            return;
        }

        var details = exception.ToString();
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = exception.Message,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBox
        {
            Text = details,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MinHeight = 180,
            MaxHeight = 320,
            HorizontalAlignment = HorizontalAlignment.Stretch
        });

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = "Copy Details",
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            CopyText(details);
            AppendOutput("Picker error copied.");
        }
    }

    private async Task ShowModuleSettingsAsync(ModuleDescriptor module)
    {
        var panel = new StackPanel { Spacing = 12 };
        foreach (var property in module.Config.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead ||
                !property.CanWrite ||
                property.Name == "Enabled" ||
                (module.Config is AppInstallerConfig && (property.Name.Contains("Timeout", StringComparison.OrdinalIgnoreCase) || property.Name == nameof(AppInstallerConfig.SetupInfoDirectory))) ||
                IsSupportedList(property.PropertyType))
            {
                continue;
            }

            panel.Children.Add(BuildSettingRow(module.Config, property));
        }

        if (module.Config is AppInstallerConfig)
        {
            panel.Children.Add(new TextBlock { Text = "Setup info is managed automatically. Installation time limits use built-in defaults.", TextWrapping = TextWrapping.Wrap, Foreground = ResourceBrush("WinstallerSecondaryTextBrush") });
            var clearStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = ResourceBrush("WinstallerSecondaryTextBrush") };
            panel.Children.Add(ActionButton("Clear Icon Cache", async () =>
            {
                var result = await AppIconService.ClearCacheAsync();
                await RunOnUiThreadAsync(() =>
                {
                    clearStatus.Text = result.Error is null
                        ? $"Cleared {result.DeletedFileCount} cached icon files."
                        : $"Could not clear icon cache: {result.Error}";
                    if (result.Error is not null) return;
                    InvalidateCachedPage(module.Name);
                    RenderModule(module);
                });
            }));
            panel.Children.Add(clearStatus);
        }
        if (panel.Children.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "No module settings available." });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = $"{module.Name} Settings",
            Content = new ScrollViewer
            {
                Content = panel,
                MaxHeight = 540,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            },
            CloseButtonText = "Done"
        };
        dialog.Resources["ContentDialogMinWidth"] = 680;
        dialog.Resources["ContentDialogMaxWidth"] = 840;

        await dialog.ShowAsync();
        RenderModule(module);
    }


    private async Task ShowIgnoredItemsAsync(ModuleDescriptor module)
    {
        var panel = new StackPanel { Spacing = 12 };
        foreach (var property in module.Config.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.Name.Contains("Ignored", StringComparison.OrdinalIgnoreCase) && IsSupportedList(property.PropertyType))
            {
                panel.Children.Add(BuildListSection(module.Config, property));
            }
        }

        if (panel.Children.Count == 0)
            panel.Children.Add(new TextBlock { Text = "No ignored items." });

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = $"{module.Name} Ignored Items",
            Content = new ScrollViewer { Content = panel, MaxHeight = 540 },
            CloseButtonText = "Done"
        };

        await dialog.ShowAsync();
        RenderModule(module);
    }

    private async Task ShowGuidedSetupPromptAsync()
    {
        if (await ConfirmAsync("Start guided setup?", "Walk through Symlinks, App Installer, Fonts, Shell Folders, PATH, and Network Drives if detected.", "Start"))
        {
            await RunGuidedSetupAsync();
        }
    }

    private async Task RunGuidedSetupAsync()
    {
        var standard = new[] { "Symlinks", "App Installer", "Fonts", "Shell Folders", "Path" }
            .Select(name => _modules.First(module => module.Name == name))
            .ToList();

        if ((await SystemInfoImportService.FindCandidatesAsync(_config, SystemInfoImportScope.NetworkDrives)).Count > 0)
            standard.Add(_modules.First(module => module.Name == "Network Drives"));

        foreach (var module in standard)
        {
            var runStep = await ConfirmAsync($"Guided setup: {module.Name}", "Import detected system information for this module?", "Import");
            if (runStep && module.ImportScope is { } scope)
            {
                await ImportSystemInfoAsync(scope, module);
            }
        }
    }
}

