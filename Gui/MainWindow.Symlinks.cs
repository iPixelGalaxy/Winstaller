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
private FrameworkElement BuildSymlinksContent(SymlinksConfig config)
    {
        var grid = new Grid { ColumnSpacing = 18 };
        for (var i = 0; i < 4; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var roaming = BuildSymlinkColumn(config, typeof(SymlinksConfig).GetProperty(nameof(SymlinksConfig.RoamingDirectories))!, "Roaming", "Microsoft\\VisualStudio");
        var local = BuildSymlinkColumn(config, typeof(SymlinksConfig).GetProperty(nameof(SymlinksConfig.LocalDirectories))!, "Local", "Microsoft\\VisualStudio");
        var localLow = BuildSymlinkColumn(config, typeof(SymlinksConfig).GetProperty(nameof(SymlinksConfig.LocalLowDirectories))!, "LocalLow", "Company\\Game");
        var special = BuildSymlinkColumn(config, typeof(SymlinksConfig).GetProperty(nameof(SymlinksConfig.SpecialSymlinks))!, "Special", string.Empty);

        Grid.SetColumn(roaming, 0);
        Grid.SetColumn(local, 1);
        Grid.SetColumn(localLow, 2);
        Grid.SetColumn(special, 3);
        grid.Children.Add(roaming);
        grid.Children.Add(local);
        grid.Children.Add(localLow);
        grid.Children.Add(special);
        return grid;
    }

    private FrameworkElement BuildSymlinkColumn(SymlinksConfig config, PropertyInfo property, string title, string placeholder)
    {
        var list = (IList?)property.GetValue(config);
        if (list is null)
        {
            list = (IList)Activator.CreateInstance(property.PropertyType)!;
            property.SetValue(config, list);
        }

        var itemType = property.PropertyType.GetGenericArguments()[0];
        var panel = new StackPanel { Spacing = 8 };
        var countText = new TextBlock
        {
            Foreground = ResourceBrush("WinstallerSecondaryTextBrush"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        var emptyText = new TextBlock
        {
            Text = "No items configured.",
            Opacity = 0.65,
            FontSize = 12
        };
        var items = new ItemsRepeater
        {
            VerticalCacheLength = 0.25,
            Layout = new StackLayout { Spacing = 8 }
        };
        items.ItemTemplate = itemType == typeof(string)
            ? new RecyclableRowFactory(() => BuildCompactStringListItem(config, property, list, Refresh, placeholder))
            : itemType == typeof(SpecialSymlink)
                ? new RecyclableRowFactory(() => BuildCompactSpecialSymlinkItem(config, list, Refresh))
                : new CallbackElementFactory(data =>
                {
                    var item = (IndexedItem)data!;
                    return BuildListItemEditor(list, itemType, item.Index, Refresh, property);
                });
        panel.Children.Add(emptyText);
        panel.Children.Add(items);

        void Refresh()
        {
            countText.Text = $"{list.Count} item{(list.Count == 1 ? string.Empty : "s")}";
            emptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            items.ItemsSource = list.Cast<object>()
                .Select((item, index) => new IndexedItem(item, index))
                .ToList();
        }

        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(new FontIcon
        {
            Glyph = GetConfigGlyph(property, null),
            FontSize = 18,
            Width = 24,
            VerticalAlignment = VerticalAlignment.Center
        });
        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 17,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 }
        });
        text.Children.Add(countText);
        Grid.SetColumn(text, 1);
        header.Children.Add(text);

        panel.Children.Add(ActionButton($"+ Add {title}", () =>
        {
            list.Add(CreateDefaultItem(itemType));
            SaveConfiguration();
            Refresh();
        }));

        Refresh();
        return new StackPanel
        {
            Spacing = 10,
            Children = { header, panel }
        };
    }


    private ReusableSymlinkRow BuildCompactStringListItem(SymlinksConfig config, PropertyInfo property, IList list, Action refresh, string placeholder)
    {
        var row = new ReusableSymlinkRow { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var isBinding = false;
        var isDirty = false;
        Button? iconButton = null;
        TextBox? box = null;
        iconButton = SymlinkOpenButton(
            "\uE8B7",
            async () =>
            {
                var item = row.Item;
                if (item is null)
                    return;

                var currentValue = list[item.Index]?.ToString() ?? string.Empty;
                if (IsShiftDown())
                {
                    OpenFolder(GetManagedAppDataSymlinkTarget(config, property.Name, currentValue));
                    return;
                }

                var picked = await PickSymlinkPathAsync(GetAppDataRootForProperty(property.Name), currentValue);
                if (picked is null)
                    return;

                await RunOnUiThreadAsync(() =>
                {
                    if (!ReferenceEquals(row.Item, item))
                        return;
                    list[item.Index] = picked;
                    isBinding = true;
                    box!.Text = picked;
                    isBinding = false;
                    isDirty = false;
                    iconButton!.Content = new FontIcon { Glyph = "\uE8B7", FontSize = 16 };
                    SaveConfiguration();
                });
            });
        row.Children.Add(iconButton);

        box = new TextBox
        {
            PlaceholderText = placeholder,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 0
        };
        void SaveBox(bool defocus = true)
        {
            var item = row.Item;
            if (item is null || !isDirty)
                return;

            list[item.Index] = box!.Text.Trim();
            iconButton!.Content = new FontIcon { Glyph = "\uE8B7", FontSize = 16 };
            SaveConfiguration();
            isDirty = false;
            if (defocus)
                DefocusTextBox(box);
        }
        box.TextChanged += (_, _) => { if (!isBinding) isDirty = true; };
        box.LostFocus += (_, _) => SaveBox();
        box.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter)
            {
                SaveBox();
                args.Handled = true;
            }
        };
        Grid.SetColumn(box, 1);
        row.Children.Add(box);

        var removeButton = CompactRemoveButton(async () =>
        {
            var item = row.Item;
            if (item is null)
                return;

            var currentValue = list[item.Index]?.ToString() ?? string.Empty;
            if (!await ConfirmSymlinkRemovalAsync(currentValue, SplitName(property.Name)))
                return;
            list.RemoveAt(item.Index);
            SaveConfiguration();
            refresh();
        });
        Grid.SetColumn(removeButton, 2);
        row.Children.Add(removeButton);

        row.BindAction = item =>
        {
            isBinding = true;
            box.Text = item.Value?.ToString() ?? string.Empty;
            isBinding = false;
            isDirty = false;
            iconButton.Content = new FontIcon { Glyph = "\uE8B7", FontSize = 16 };
        };
        row.RecycleAction = () => SaveBox(false);
        return row;
    }

    private ReusableSymlinkRow BuildCompactSpecialSymlinkItem(SymlinksConfig config, IList list, Action refresh)
    {
        var outer = new ReusableSymlinkRow { ColumnSpacing = 8 };
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var isBinding = false;
        var sourceDirty = false;
        var targetDirty = false;
        TextBox? sourceBox = null;
        TextBox? targetBox = null;
        Button? sourceButton = null;
        Button? targetButton = null;
        SpecialSymlink? Current() => outer.Item?.Value as SpecialSymlink;
        sourceButton = SymlinkOpenButton(
            "\uE8B7",
            async () =>
            {
                var item = outer.Item;
                var symlink = Current();
                if (item is null || symlink is null)
                    return;
                if (IsShiftDown())
                {
                    OpenFolder(ExpandConfigPath(symlink.Source));
                    return;
                }

                var picked = await PickPathForTypeAsync(symlink.IsDirectory, symlink.Source, "Select symlink source");
                if (picked is null)
                    return;

                await RunOnUiThreadAsync(() =>
                {
                    if (!ReferenceEquals(outer.Item, item))
                        return;
                    symlink.Source = picked;
                    isBinding = true;
                    sourceBox!.Text = picked;
                    isBinding = false;
                    sourceDirty = false;
                    SetSpecialSymlinkGlyphs(symlink);
                    SaveConfiguration();
                });
            });
        targetButton = SymlinkOpenButton(
            "\uE8B7",
            async () =>
            {
                var item = outer.Item;
                var symlink = Current();
                if (item is null || symlink is null)
                    return;
                if (IsShiftDown())
                {
                    OpenFolder(GetSpecialSymlinkTarget(config, symlink));
                    return;
                }

                var picked = await PickPathForTypeAsync(symlink.IsDirectory, GetSpecialSymlinkTarget(config, symlink), "Select symlink target");
                if (picked is null)
                    return;

                await RunOnUiThreadAsync(() =>
                {
                    if (!ReferenceEquals(outer.Item, item))
                        return;
                    symlink.Target = picked;
                    isBinding = true;
                    targetBox!.Text = picked;
                    isBinding = false;
                    targetDirty = false;
                    SetSpecialSymlinkGlyphs(symlink);
                    SaveConfiguration();
                });
            });
        var fields = new StackPanel { Spacing = 6 };
        var sourceRow = new Grid { ColumnSpacing = 6 };
        sourceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sourceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sourceRow.Children.Add(sourceButton);
        sourceBox = CompactTextBox(string.Empty, "Source", value =>
        {
            var symlink = Current();
            if (symlink is null || !sourceDirty)
                return;
            symlink.Source = value;
            sourceDirty = false;
            SetSpecialSymlinkGlyphs(symlink);
            SaveConfiguration();
        });
        sourceBox.TextChanged += (_, _) => { if (!isBinding) sourceDirty = true; };
        Grid.SetColumn(sourceBox, 1);
        sourceRow.Children.Add(sourceBox);

        var targetRow = new Grid { ColumnSpacing = 6 };
        targetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        targetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        targetRow.Children.Add(targetButton);
        targetBox = CompactTextBox(string.Empty, "Target override", value =>
        {
            var symlink = Current();
            if (symlink is null || !targetDirty)
                return;
            symlink.Target = value;
            targetDirty = false;
            SetSpecialSymlinkGlyphs(symlink);
            SaveConfiguration();
        });
        targetBox.TextChanged += (_, _) => { if (!isBinding) targetDirty = true; };
        Grid.SetColumn(targetBox, 1);
        targetRow.Children.Add(targetBox);

        var typeToggle = new ToggleSwitch
        {
            IsOn = false,
            OnContent = "Directory",
            OffContent = "File",
            MinWidth = 0
        };
        typeToggle.Toggled += (_, _) =>
        {
            if (isBinding)
                return;
            var symlink = Current();
            if (symlink is null)
                return;
            symlink.IsDirectory = typeToggle.IsOn;
            SetSpecialSymlinkGlyphs(symlink);
            SaveConfiguration();
        };
        fields.Children.Add(sourceRow);
        fields.Children.Add(targetRow);
        fields.Children.Add(typeToggle);
        outer.Children.Add(fields);

        var removeButton = CompactRemoveButton(async () =>
        {
            var item = outer.Item;
            var symlink = Current();
            if (item is null || symlink is null)
                return;
            var name = GetItemTitle(symlink, typeof(SpecialSymlink), item.Index);
            if (!await ConfirmSymlinkRemovalAsync(name, "Special"))
                return;
            list.RemoveAt(item.Index);
            SaveConfiguration();
            refresh();
        });
        Grid.SetColumn(removeButton, 1);
        outer.Children.Add(removeButton);

        void SetSpecialSymlinkGlyphs(SpecialSymlink symlink)
        {
            sourceButton!.Content = new FontIcon { Glyph = GetSpecialSymlinkGlyph(symlink), FontSize = 16 };
            targetButton!.Content = new FontIcon { Glyph = GetSpecialSymlinkGlyph(symlink), FontSize = 16 };
        }

        outer.BindAction = item =>
        {
            var symlink = (SpecialSymlink)item.Value;
            isBinding = true;
            sourceBox.Text = symlink.Source;
            targetBox.Text = symlink.Target;
            typeToggle.IsOn = symlink.IsDirectory;
            isBinding = false;
            sourceDirty = false;
            targetDirty = false;
            SetSpecialSymlinkGlyphs(symlink);
        };
        outer.RecycleAction = () =>
        {
            var symlink = Current();
            if (symlink is null)
                return;
            if (sourceDirty)
            {
                symlink.Source = sourceBox.Text.Trim();
                sourceDirty = false;
                SaveConfiguration();
            }
            if (targetDirty)
            {
                symlink.Target = targetBox.Text.Trim();
                targetDirty = false;
                SaveConfiguration();
            }
        };
        return outer;
    }

    private TextBox CompactTextBox(string value, string placeholder, Action<string> save)
    {
        var box = new TextBox
        {
            Text = value,
            PlaceholderText = placeholder,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 0
        };
        box.LostFocus += (_, _) =>
        {
            save(box.Text.Trim());
            DefocusTextBox(box);
        };
        box.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter)
            {
                save(box.Text.Trim());
                DefocusTextBox(box);
                args.Handled = true;
            }
        };
        return box;
    }

    private Button SymlinkOpenButton(string glyph, Func<Task> open)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 16 },
            Width = 30,
            Height = 32,
            MinWidth = 30,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(button, "Select location. Shift-click opens location.");
        button.Click += async (_, _) =>
        {
            try
            {
                WriteDiagnosticLog("Symlink picker clicked.");
                await open();
                WriteDiagnosticLog("Symlink picker completed.");
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"Symlink picker failed:{Environment.NewLine}{ex}");
                await RunOnUiThreadAsync(async () =>
                {
                    AppendOutput($"Picker failed: {ex.Message}");
                    await ShowExceptionAsync("Picker failed", ex);
                });
            }
        };
        return button;
    }

    private Button CompactRemoveButton(Func<Task> remove)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = "\uE74D", FontSize = 13 },
            Width = 32,
            Height = 32,
            MinWidth = 32,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(button, "Remove");
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                await remove();
            }
            catch (Exception ex)
            {
                RunLog.WriteException("UI", "Remove failed", ex);
                AppendOutput($"Remove failed: {ex.Message}");
            }
            finally
            {
                button.IsEnabled = true;
            }
        };
        return button;
    }


    private void OpenFolder(string path)
    {
        var target = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        while (!string.IsNullOrWhiteSpace(target) && !Directory.Exists(target))
        {
            target = Path.GetDirectoryName(target);
        }

        if (string.IsNullOrWhiteSpace(target))
            return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendOutput($"Open folder failed: {ex.Message}");
        }
    }

    private async Task<string?> PickSymlinkPathAsync(string rootPath, string currentValue)
    {
        var expandedRoot = ExpandConfigPath(rootPath);
        var initialFolder = GetExistingFolder(Path.Combine(expandedRoot, currentValue), expandedRoot);
        var picked = await PickFolderPathAsync(initialFolder, "Select AppData folder");
        if (picked is null)
            return null;

        if (!picked.StartsWith(expandedRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) &&
            !picked.Equals(expandedRoot, StringComparison.OrdinalIgnoreCase))
        {
            await ShowMessageAsync("Invalid location", $"Pick a path under {expandedRoot}.");
            return null;
        }

        return Path.GetRelativePath(expandedRoot, picked);
    }

    private async Task<string?> PickPathForTypeAsync(bool isDirectory, string? currentPath, string title)
    {
        var initialFolder = GetExistingFolder(currentPath, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return isDirectory ? await PickFolderPathAsync(initialFolder, title) : await PickFilePathAsync(initialFolder, title);
    }

    private async Task<string?> PickFolderPathAsync(string? initialFolder, string title)
    {
        WriteDiagnosticLog("Folder picker create.");
        var hwnd = WindowNative.GetWindowHandle(this);
        WriteDiagnosticLog($"Native folder picker show hwnd={hwnd}; initialFolder={initialFolder}; title={title}.");
        var folder = await NativePathPicker.PickFolderAsync(hwnd, initialFolder, title);
        WriteDiagnosticLog(folder is null ? "Folder picker canceled." : $"Folder picker picked: {folder}");
        return folder;
    }

    private async Task<string?> PickFilePathAsync(string? initialFolder, string title)
    {
        WriteDiagnosticLog("File picker create.");
        var hwnd = WindowNative.GetWindowHandle(this);
        WriteDiagnosticLog($"Native file picker show hwnd={hwnd}; initialFolder={initialFolder}; title={title}.");
        var file = await NativePathPicker.PickFileAsync(hwnd, initialFolder, title);
        WriteDiagnosticLog(file is null ? "File picker canceled." : $"File picker picked: {file}");
        return file;
    }

    private static string GetExistingFolder(string? path, string fallback)
    {
        var expandedFallback = ExpandConfigPath(fallback);
        var expandedPath = string.IsNullOrWhiteSpace(path) ? expandedFallback : ExpandConfigPath(path);
        if (Directory.Exists(expandedPath))
            return expandedPath;

        var parent = Path.GetDirectoryName(expandedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
            return parent;

        return Directory.Exists(expandedFallback)
            ? expandedFallback
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static string GetAppDataRootForProperty(string propertyName)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return propertyName switch
        {
            nameof(SymlinksConfig.RoamingDirectories) => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            nameof(SymlinksConfig.LocalDirectories) => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            nameof(SymlinksConfig.LocalLowDirectories) => Path.Combine(profile, "AppData", "LocalLow"),
            _ => profile
        };
    }

    private static bool IsShiftDown()
    {
        return Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }

    private static void DefocusTextBox(TextBox box)
    {
        box.SelectionLength = 0;
        if (box.XamlRoot?.Content is Control control)
        {
            control.Focus(FocusState.Programmatic);
            return;
        }

        FocusManager.TryMoveFocus(FocusNavigationDirection.Next);
    }

    private static string GetManagedAppDataSymlinkTarget(SymlinksConfig config, string propertyName, string relativePath)
    {
        var section = propertyName switch
        {
            nameof(SymlinksConfig.RoamingDirectories) => "Roaming",
            nameof(SymlinksConfig.LocalDirectories) => "Local",
            nameof(SymlinksConfig.LocalLowDirectories) => "LocalLow",
            _ => string.Empty
        };
        var baseDir = ExpandConfigPath(config.BaseSymlinkDirectory);
        return Path.Combine(baseDir, "AppData", section, relativePath);
    }

    private static string GetSpecialSymlinkTarget(SymlinksConfig config, SpecialSymlink symlink)
    {
        if (!string.IsNullOrWhiteSpace(symlink.Target))
            return ExpandConfigPath(symlink.Target);

        var source = ExpandConfigPath(symlink.Source);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var localLow = Path.Combine(profile, "AppData", "LocalLow");
        var baseDir = ExpandConfigPath(config.BaseSymlinkDirectory);

        var relative = source.StartsWith(roaming, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine("AppData", "Roaming", Path.GetRelativePath(roaming, source))
            : source.StartsWith(local, StringComparison.OrdinalIgnoreCase)
                ? Path.Combine("AppData", "Local", Path.GetRelativePath(local, source))
                : source.StartsWith(localLow, StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine("AppData", "LocalLow", Path.GetRelativePath(localLow, source))
                    : Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        return Path.Combine(baseDir, relative);
    }

    private static string ExpandConfigPath(string path)
    {
        return Environment.ExpandEnvironmentVariables(path).Replace("{USERNAME}", Environment.UserName);
    }

    private static string GetSpecialSymlinkGlyph(SpecialSymlink symlink)
    {
        return symlink.IsDirectory ? "\uE8B7" : "\uE8A5";
    }
}

