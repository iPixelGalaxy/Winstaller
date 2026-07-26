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
    private FrameworkElement BuildVRChatRegistryContent(VRChatRegistryConfig config)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "Stores a portable JSON backup of HKCU\\SOFTWARE\\VRChat\\VRChat. Restore writes selected values back to VRChat registry.",
            Foreground = ResourceBrush("WinstallerSecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap
        });
        var captureStatus = new TextBlock
        {
            Foreground = ResourceBrush("WinstallerSecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap
        };
        panel.Children.Add(ActionButton("Capture Current VRChat Registry", async () =>
        {
            captureStatus.Text = "Capturing current VRChat registry…";
            var module = new VRChatRegistryModule(_config);
            var captured = await Task.Run(async () => await module.CaptureAsync()).ConfigureAwait(false);
            await RunOnUiThreadAsync(() =>
            {
                if (!captured)
                {
                    captureStatus.Text = module.LastMessage;
                    captureStatus.Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
                    return;
                }

                InvalidateCachedPage("VRChat Registry");
                RenderModule(_modules.First(descriptor => ReferenceEquals(descriptor.Config, config)));
            });
        }, primary: true));
        panel.Children.Add(captureStatus);
        panel.Children.Add(ActionButton("Restore Selected Groups", async () => await new VRChatRegistryModule(_config).RestoreAsync()));

        var backupPath = Environment.ExpandEnvironmentVariables(config.BackupPath);
        panel.Children.Add(new TextBlock
        {
            Text = File.Exists(backupPath)
                ? $"Backup: {new FileInfo(backupPath).Length / 1024d:0.#} KB • {File.GetLastWriteTime(backupPath):g}"
                : "No backup captured yet.",
            Foreground = ResourceBrush("WinstallerSecondaryTextBrush")
        });
        panel.Children.Add(BuildVRChatRestoreGroup(config, nameof(VRChatRegistryConfig.RestoreSettings), "Settings", "Graphics, audio, input, safety, camera, accessibility, and other app settings."));
        panel.Children.Add(BuildVRChatRestoreGroup(config, nameof(VRChatRegistryConfig.RestorePersonalData), "Personal data", "Account-linked values, inventories, custom groups, histories, and session-related data."));
        var backupValues = new StackPanel { Spacing = 12 };
        backupValues.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { new ProgressRing { IsActive = true, Width = 20, Height = 20 }, new TextBlock { Text = "Loading saved VRChat values…", VerticalAlignment = VerticalAlignment.Center } }
        });
        panel.Children.Add(backupValues);
        _ = LoadVRChatBackupValuesAsync(config, backupValues);
        return panel;
    }

    private async Task LoadVRChatBackupValuesAsync(VRChatRegistryConfig config, StackPanel backupValues)
    {
        var backup = await (_vrchatBackupPreloadTask ?? Task.Run(() => new VRChatRegistryModule(_config).LoadBackup())).ConfigureAwait(false);
        await RunOnUiThreadAsync(() =>
        {
            backupValues.Children.Clear();
            if (backup is null)
                return;

            backupValues.Children.Add(BuildVRChatValueGroup(config, backup, backup.Values.Where(value => value.Group == VRChatRegistryGroup.Settings), "Settings"));
            backupValues.Children.Add(BuildVRChatValueGroup(config, backup, backup.Values.Where(value => value.Group == VRChatRegistryGroup.Personal), "Personal data"));
        });
    }

    private FrameworkElement BuildVRChatValueGroup(VRChatRegistryConfig config, VRChatRegistryBackup backup, IEnumerable<VRChatRegistryValue> source, string title)
    {
        var values = source.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var panel = new StackPanel { Spacing = 8 };
        if (values.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "No captured values.", Opacity = 0.65 });
        }
        else
        {
            foreach (var category in values.GroupBy(VRChatRegistryModule.GetCategory).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                panel.Children.Add(new TextBlock { Text = category.Key, FontSize = 16, FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 }, Margin = new Thickness(0, 8, 0, 0) });
                var tiles = BuildProgressiveTileGrid(category.ToList(), 272, 74, value => BuildVRChatValueTile(config, backup, value));
                panel.Children.Add(tiles);
            }
        }
        var section = new StackPanel { Spacing = 8 };
        section.Children.Add(new TextBlock { Text = $"{title} ({values.Count})", FontSize = 20, FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 }, Margin = new Thickness(0, 12, 0, 0) });
        section.Children.Add(panel);
        return section;
    }

    private FrameworkElement BuildVRChatValueTile(VRChatRegistryConfig config, VRChatRegistryBackup backup, VRChatRegistryValue value)
    {
        var editor = BuildVRChatValueEditor(backup, value);
        var editorWidth = editor is ToggleSwitch ? 56 : 82;
        var row = new Grid { ColumnSpacing = 10, HorizontalAlignment = HorizontalAlignment.Stretch };
        row.VerticalAlignment = VerticalAlignment.Center;
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(editorWidth) });
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = DescribeVRChatValue(value.Name), FontSize = 14, FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 }, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = GuessVRChatDescription(value), FontSize = 11, Foreground = ResourceBrush("WinstallerSecondaryTextBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        row.Children.Add(text);
        Grid.SetColumn(editor, 1);
        row.Children.Add(editor);
        var tile = new Border
        {
            Height = 74,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = ResourceBrush("WinstallerCardBrush"),
            BorderBrush = ResourceBrush("WinstallerCardStrokeBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(28),
            Padding = new Thickness(16, 8, 12, 8),
            Child = row
        };
        ToolTipService.SetToolTip(tile, $"Likely meaning. Registry value: {value.Name}");
        return tile;
    }

    private FrameworkElement BuildVRChatValueEditor(VRChatRegistryBackup backup, VRChatRegistryValue value)
    {
        void Save() => new VRChatRegistryModule(_config).SaveBackup(backup);
        if (value.Kind == Microsoft.Win32.RegistryValueKind.DWord && (value.Data == "0" || value.Data == "1"))
        {
            var toggle = new ToggleSwitch { IsOn = value.Data == "1", OffContent = string.Empty, OnContent = string.Empty, Width = 52, MinWidth = 52, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            toggle.Toggled += (_, _) => { value.Data = toggle.IsOn ? "1" : "0"; Save(); };
            return toggle;
        }
        if ((value.Kind is Microsoft.Win32.RegistryValueKind.DWord or Microsoft.Win32.RegistryValueKind.QWord) && ulong.TryParse(value.Data, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            var box = new TextBox { Text = value.Data, Width = 78, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
            box.LostFocus += (_, _) =>
            {
                if (ulong.TryParse(box.Text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number))
                {
                    value.Data = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    Save();
                }
                else box.Text = value.Data;
            };
            return box;
        }
        return new TextBlock { Text = value.Kind is Microsoft.Win32.RegistryValueKind.Binary ? "Saved binary" : "Saved value", Foreground = ResourceBrush("WinstallerSecondaryTextBrush"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    }

    private static string DescribeVRChatValue(string name)
    {
        var hash = name.LastIndexOf("_h", StringComparison.OrdinalIgnoreCase);
        var readable = hash > 0 ? name[..hash] : name;
        var knownNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AUDIO_MASTER_STEAMAUDIO"] = "Master Volume",
            ["AUDIO_UI_STEAMAUDIO"] = "UI Volume",
            ["AUDIO_GAME_SFX_STEAMAUDIO"] = "Sound Effects Volume",
            ["AUDIO_GAME_VOICE_STEAMAUDIO"] = "Voice Volume",
            ["AUDIO_GAME_AVATARS_STEAMAUDIO"] = "Avatar Volume",
            ["AUDIO_GAME_PROPS_STEAMAUDIO"] = "Prop Volume"
        };
        if (knownNames.TryGetValue(readable, out var knownName)) return knownName;
        readable = readable.Replace("CustomTrustLevel_", string.Empty).Replace("VRC_", string.Empty).Replace("_", " ");
        var result = new StringBuilder(readable.Length + 12);
        for (var index = 0; index < readable.Length; index++)
        {
            var current = readable[index];
            if (index > 0 && char.IsUpper(current) && char.IsLower(readable[index - 1])) result.Append(' ');
            result.Append(current);
        }
        var normalized = result.ToString();
        if (normalized.All(character => !char.IsLetter(character) || char.IsUpper(character)))
            normalized = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(normalized.ToLowerInvariant());
        return normalized.Replace("Trust Level", string.Empty).Replace("Can Use", "Can use").Replace("Can Speak", "Can speak").Replace("Ui", "UI").Replace("Fps", "FPS").Replace("Sfx", "SFX").Trim();
    }

    private static string GuessVRChatDescription(VRChatRegistryValue value)
    {
        var name = value.Name;
        if (name.StartsWith("CustomTrustLevel_", StringComparison.OrdinalIgnoreCase)) return "Likely custom Safety Shield rule for this trust rank.";
        if (name.StartsWith("Screenmanager", StringComparison.OrdinalIgnoreCase)) return "Likely saved display or fullscreen preference.";
        return VRChatRegistryModule.GetCategory(value) switch
        {
            "Audio & Voice" => "Likely audio, voice, microphone, or volume preference.",
            "Input & Movement" => "Likely input, comfort, turning, or movement preference.",
            "Safety & Avatars" => "Likely avatar visibility, safety, or performance preference.",
            "Camera & Mirrors" => "Likely camera, drone, mirror, or photo preference.",
            "User Interface & Accessibility" => "Likely menu, HUD, notification, or accessibility preference.",
            "Social & Privacy" => "Likely social, friend, group, or privacy preference.",
            "Account & Session" => "Likely account or session data.",
            "Saved Collections & History" => "Likely saved collection, favorite, or history data.",
            "Personal UI State" => "Likely personal UI state or dismissed prompt data.",
            _ => "Likely VRChat preference; exact meaning is undocumented."
        };
    }

    private FrameworkElement BuildVRChatRestoreGroup(VRChatRegistryConfig config, string propertyName, string title, string description)
    {
        var property = typeof(VRChatRegistryConfig).GetProperty(propertyName)!;
        var toggle = new ToggleSwitch { IsOn = (bool)property.GetValue(config)!, OffContent = string.Empty, OnContent = string.Empty, VerticalAlignment = VerticalAlignment.Center };
        toggle.Toggled += (_, _) => { property.SetValue(config, toggle.IsOn); SaveConfiguration(); };
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = title, FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 } });
        text.Children.Add(new TextBlock { Text = description, FontSize = 12, Foreground = ResourceBrush("WinstallerSecondaryTextBrush"), TextWrapping = TextWrapping.Wrap });
        row.Children.Add(text);
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);
        return Card(row);
    }

    private FrameworkElement BuildRegistryContent(RegistryConfig config)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "Add .reg files to Winstaller managed storage. Winstaller imports stored copies when Registry runs.",
            Foreground = ResourceBrush("WinstallerSecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(ActionButton("+ Import .reg File", async () => await ImportRegistryFileAsync(config), primary: true));
        panel.Children.Add(BuildManagedRegistryFiles(config));
        panel.Children.Add(BuildListSection(config, typeof(RegistryConfig).GetProperty(nameof(RegistryConfig.Modifications))!));
        return panel;
    }

    private FrameworkElement BuildManagedRegistryFiles(RegistryConfig config)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = config.FilesToImport.Count == 0
                ? "No .reg files imported."
                : $"{config.FilesToImport.Count} registry file{(config.FilesToImport.Count == 1 ? string.Empty : "s")}",
            Foreground = ResourceBrush("WinstallerSecondaryTextBrush")
        });
        if (config.FilesToImport.Count == 0)
            return panel;

        var tiles = new VariableSizedWrapGrid
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        foreach (var path in config.FilesToImport)
            tiles.Children.Add(BuildRegistryFileTile(path, config));
        panel.Children.Add(tiles);
        return panel;
    }

    private FrameworkElement BuildRegistryFileTile(string path, RegistryConfig config)
    {
        var size = File.Exists(path) ? new FileInfo(path).Length : 0;
        var labels = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(CreateAppTileTitle(Path.GetFileName(path)));
        labels.Children.Add(new TextBlock
        {
            Text = File.Exists(path) ? $"Registry file • {size / 1024d:0.#} KB" : "Registry file • missing",
            FontSize = 11,
            Foreground = ResourceBrush("WinstallerSecondaryTextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(new FontIcon { Glyph = "\uE7B8", FontSize = 40, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(labels, 1);
        header.Children.Add(labels);

        var remove = IconActionButton("\uE74D", "Remove from imports", () =>
        {
            config.FilesToImport.Remove(path);
            SaveConfiguration();
            InvalidateCachedPage("Registry");
            RenderModule(_modules.First(module => ReferenceEquals(module.Config, config)));
        });
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { remove } };
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(header);
        Grid.SetRow(footer, 1);
        content.Children.Add(footer);
        var tile = new Border
        {
            Width = 250,
            Height = 128,
            Margin = new Thickness(0, 0, 8, 8),
            Background = ResourceBrush("WinstallerCardBrush"),
            BorderBrush = ResourceBrush("WinstallerCardStrokeBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14),
            Child = content
        };
        ToolTipService.SetToolTip(tile, path);
        return tile;
    }

    private async Task ImportRegistryFileAsync(RegistryConfig config)
    {
        var source = await PickFilePathAsync(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Choose registry file (.reg)");
        if (source is null)
            return;
        if (!source.EndsWith(".reg", StringComparison.OrdinalIgnoreCase))
        {
            await ShowMessageAsync("Invalid file", "Choose a .reg file.");
            return;
        }

        try
        {
            var directory = Path.Combine(BootstrapManager.DataDirectory, "Registry");
            Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, Path.GetFileName(source));
            if (File.Exists(destination) && !Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            {
                var stem = Path.GetFileNameWithoutExtension(source);
                destination = Path.Combine(directory, $"{stem}-{DateTime.Now:yyyyMMdd-HHmmss}.reg");
            }

            File.Copy(source, destination, overwrite: true);
            if (!config.FilesToImport.Contains(destination, StringComparer.OrdinalIgnoreCase))
                config.FilesToImport.Add(destination);
            await RunOnUiThreadAsync(() =>
            {
                SaveConfiguration();
                InvalidateCachedPage("Registry");
                RenderModule(_modules.First(module => ReferenceEquals(module.Config, config)));
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowMessageAsync("Import registry file", $"Could not store {Path.GetFileName(source)}: {ex.Message}");
        }
    }

private FrameworkElement BuildFontsContent(FontsConfig config)
    {
        var fontsDirectory = Environment.ExpandEnvironmentVariables(config.FontsDirectory)
            .Replace("{USERNAME}", Environment.UserName, StringComparison.OrdinalIgnoreCase);
        var panel = new StackPanel { Spacing = 12 };

        if (!Directory.Exists(fontsDirectory))
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"Fonts folder not found: {fontsDirectory}",
                Foreground = ResourceBrush("WinstallerSecondaryTextBrush"),
                TextWrapping = TextWrapping.Wrap
            });
            return panel;
        }

        IReadOnlyList<string> fontFiles;
        if (_fontPreloadTask is { IsCompletedSuccessfully: true })
        {
            fontFiles = _fontPreloadTask.Result;
        }
        else try
        {
            fontFiles = Directory.GetFiles(fontsDirectory, "*.ttf")
                .Concat(Directory.GetFiles(fontsDirectory, "*.otf"))
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"Could not read fonts folder: {ex.Message}",
                Foreground = ResourceBrush("WinstallerSecondaryTextBrush"),
                TextWrapping = TextWrapping.Wrap
            });
            return panel;
        }

        panel.Children.Add(new TextBlock
        {
            Text = fontFiles.Count == 0
                ? "No .ttf or .otf fonts found."
                : $"{fontFiles.Count} font{(fontFiles.Count == 1 ? string.Empty : "s")}",
            Foreground = ResourceBrush("WinstallerSecondaryTextBrush")
        });

        var tiles = BuildProgressiveTileGrid(fontFiles, 250, 88, fontFile => BuildFontTile(fontFile, fontsDirectory, config));
        panel.Children.Add(tiles);

        return panel;
    }

    private FrameworkElement BuildFontTile(string fontFile, string fontsDirectory, FontsConfig config)
    {
        var fileName = Path.GetFileName(fontFile);
        var fontFamilyName = FontPreviewService.GetFontFamily(fontFile);
        var isOpenType = fontFile.EndsWith(".otf", StringComparison.OrdinalIgnoreCase);
        var type = isOpenType ? "OpenType Font" : "TrueType Font";
        var size = new FileInfo(fontFile).Length;
        var details = new TextBlock
        {
            Text = $"{type} • {size / 1024d:0.#} KB",
            FontSize = 11,
            Foreground = ResourceBrush("WinstallerSecondaryTextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var title = CreateAppTileTitle(fileName);
        var labels = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { title, details } };
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var preview = new TextBlock
        {
            Text = "Aa",
            FontSize = 32,
            Width = 50,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (!string.IsNullOrWhiteSpace(fontFamilyName))
            preview.FontFamily = new FontFamily(fontFamilyName);
        header.Children.Add(preview);
        Grid.SetColumn(labels, 1);
        header.Children.Add(labels);

        var remove = IconActionButton("\uE74D", "Delete font", async () =>
        {
            if (!await ConfirmAsync("Delete font?", $"Delete {fileName} from Winstaller's fonts folder? This permanently removes the font file; it does not uninstall an installed font.", "Delete"))
                return;

            try
            {
                var expectedDirectory = Path.GetFullPath(fontsDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var fileDirectory = Path.GetDirectoryName(Path.GetFullPath(fontFile))?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.Equals(expectedDirectory, fileDirectory, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Font file is outside Winstaller's fonts folder.");

                File.Delete(fontFile);
                RenderModule(_modules.First(module => ReferenceEquals(module.Config, config)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                await ShowMessageAsync("Delete font", $"Could not delete {fileName}: {ex.Message}");
            }
        });
        remove.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(remove, 2);
        header.Children.Add(remove);

        return new Border
        {
            Height = 88,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = ResourceBrush("WinstallerCardBrush"),
            BorderBrush = ResourceBrush("WinstallerCardStrokeBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14),
            Child = header
        };
    }


    private FrameworkElement BuildShellFoldersContent(ShellFoldersConfig config)
    {
        var panel = new StackPanel { Spacing = 12 };
        var folderTiles = new List<FrameworkElement>();
        void Refresh()
        {
            RenderModule(_modules.First(module => ReferenceEquals(module.Config, config)));
        }
        for (var index = 0; index < config.Folders.Count; index++)
        {
            var tile = BuildListItemEditor(config.Folders, typeof(ShellFolderMapping), index, Refresh);
            tile.HorizontalAlignment = HorizontalAlignment.Stretch;
            folderTiles.Add(tile);
        }
        panel.Children.Add(BuildResponsiveTileGrid(folderTiles));

        var presets = GetShellFolderPresets()
            .Where(preset => !config.Folders.Any(folder => folder.RegistryValue.Equals(preset.RegistryValue, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (presets.Count > 0)
        {
            var addButton = new Button { Content = "+ Add Folder", CornerRadius = new CornerRadius(4) };
            var flyout = new MenuFlyout();
            foreach (var preset in presets)
            {
                var item = new MenuFlyoutItem { Text = preset.Name };
                item.Click += (_, _) =>
                {
                    config.Folders.Add(new ShellFolderMapping { FolderName = preset.Name, RegistryValue = preset.RegistryValue, Path = preset.DefaultPath });
                    SaveConfiguration();
                    RenderModule(_modules.First(module => ReferenceEquals(module.Config, config)));
                };
                flyout.Items.Add(item);
            }
            addButton.Flyout = flyout;
            panel.Children.Add(addButton);
        }

        return panel;
    }

    private FrameworkElement BuildPathContent(PathConfig config)
    {
        var panel = new StackPanel { Spacing = 12 };
        var property = typeof(PathConfig).GetProperty(nameof(PathConfig.Additions))!;
        void Refresh()
        {
            RenderModule(_modules.First(module => ReferenceEquals(module.Config, config)));
        }

        var pathTiles = new List<FrameworkElement>();
        for (var index = 0; index < config.Additions.Count; index++)
        {
            var tile = BuildListItemEditor(config.Additions, typeof(string), index, Refresh, property);
            tile.HorizontalAlignment = HorizontalAlignment.Stretch;
            pathTiles.Add(tile);
        }
        panel.Children.Add(BuildResponsiveTileGrid(pathTiles));
        panel.Children.Add(ActionButton("+ Add Path", () =>
        {
            config.Additions.Add(string.Empty);
            SaveConfiguration();
            Refresh();
        }));
        return panel;
    }

    private FrameworkElement BuildNetworkDrivesContent(NetworkDrivesConfig config)
    {
        var panel = new StackPanel { Spacing = 12 };
        void Refresh()
        {
            RenderModule(_modules.First(module => ReferenceEquals(module.Config, config)));
        }

        var driveTiles = new List<FrameworkElement>();
        for (var index = 0; index < config.Drives.Count; index++)
        {
            var tile = BuildNetworkDriveTile(config, config.Drives[index], Refresh);
            tile.HorizontalAlignment = HorizontalAlignment.Stretch;
            driveTiles.Add(tile);
        }
        panel.Children.Add(BuildResponsiveTileGrid(driveTiles));
        panel.Children.Add(ActionButton("+ Add Drive", () =>
        {
            config.Drives.Add(new NetworkDriveMapping());
            SaveConfiguration();
            Refresh();
        }));
        return panel;
    }

    private FrameworkElement BuildNetworkDriveTile(NetworkDrivesConfig config, NetworkDriveMapping drive, Action refresh)
    {
        TextBox TextField(string label, string value, Action<string> save)
        {
            var box = new TextBox
            {
                Header = label,
                Text = value,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(12, 4, 12, 4)
            };
            box.LostFocus += (_, _) => { save(box.Text); SaveConfiguration(); };
            return box;
        }

        CheckBox CheckField(string label, bool value, Action<bool> save)
        {
            var checkBox = new CheckBox { Content = label, IsChecked = value, VerticalAlignment = VerticalAlignment.Center };
            checkBox.Checked += (_, _) => { save(true); SaveConfiguration(); };
            checkBox.Unchecked += (_, _) => { save(false); SaveConfiguration(); };
            return checkBox;
        }

        var driveLetter = new ComboBox { Header = "Drive Letter", Width = 92, HorizontalAlignment = HorizontalAlignment.Left };
        driveLetter.Items.Add(string.Empty);
        foreach (var letter in Enumerable.Range('A', 26).Select(value => ((char)value).ToString()))
            driveLetter.Items.Add(letter);
        driveLetter.SelectedItem = drive.DriveLetter.Trim().TrimEnd(':').ToUpperInvariant();
        driveLetter.SelectionChanged += (_, _) =>
        {
            drive.DriveLetter = driveLetter.SelectedItem?.ToString() ?? string.Empty;
            SaveConfiguration();
        };

        var password = new PasswordBox
        {
            Header = "Password",
            Password = drive.Password,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 4, 40, 4),
            PasswordRevealMode = PasswordRevealMode.Hidden
        };
        var revealed = false;
        var reveal = new Button
        {
            Content = new FontIcon { Glyph = "\uE890", FontSize = 16 },
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderBrush = new SolidColorBrush(Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 4, 2)
        };
        reveal.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Colors.Transparent);
        reveal.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Colors.Transparent);
        ToolTipService.SetToolTip(reveal, "Show password");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(reveal, "Show password");
        reveal.Click += (_, _) =>
        {
            revealed = !revealed;
            password.PasswordRevealMode = revealed ? PasswordRevealMode.Visible : PasswordRevealMode.Hidden;
            reveal.Content = new FontIcon { Glyph = revealed ? "\uE7B3" : "\uE890", FontSize = 16 };
            ToolTipService.SetToolTip(reveal, revealed ? "Hide password" : "Show password");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(reveal, revealed ? "Hide password" : "Show password");
        };
        password.LostFocus += (_, _) => { drive.Password = password.Password; SaveConfiguration(); };
        var passwordField = new Grid();
        passwordField.Children.Add(password);
        passwordField.Children.Add(reveal);

        string GetTitle() => !string.IsNullOrWhiteSpace(drive.Label)
            ? drive.Label
            : string.IsNullOrWhiteSpace(drive.DriveLetter) ? "New network drive" : $"{drive.DriveLetter}: {drive.NetworkPath}";
        var title = new TextBlock
        {
            Text = GetTitle(),
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 },
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var labelField = TextField("Label", drive.Label, value => drive.Label = value);
        labelField.TextChanged += (_, _) =>
        {
            drive.Label = labelField.Text;
            title.Text = GetTitle();
        };

        var inputRow = new Grid { ColumnSpacing = 12 };
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var inputFields = new FrameworkElement[]
        {
            driveLetter,
            TextField("Network Path", drive.NetworkPath, value => drive.NetworkPath = value),
            labelField,
            TextField("Username", drive.Username, value => drive.Username = value),
            passwordField
        };
        for (var index = 0; index < inputFields.Length; index++)
        {
            Grid.SetColumn(inputFields[index], index);
            inputRow.Children.Add(inputFields[index]);
        }
        var persistent = CheckField("Persistent", drive.Persistent, value => drive.Persistent = value);
        var deleteFirst = CheckField("Delete First", drive.DeleteFirst, value => drive.DeleteFirst = value);
        var remove = IconActionButton("\uE74D", "Remove drive", () =>
        {
            config.Drives.Remove(drive);
            SaveConfiguration();
            refresh();
        });
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var heading = new Grid { ColumnSpacing = 8 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.Children.Add(new FontIcon { Glyph = "\uE839", FontSize = 20, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(title, 1);
        heading.Children.Add(title);
        Grid.SetColumnSpan(heading, 2);
        header.Children.Add(heading);
        Grid.SetColumn(persistent, 2);
        header.Children.Add(persistent);
        Grid.SetColumn(deleteFirst, 3);
        header.Children.Add(deleteFirst);
        remove.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(remove, 4);
        header.Children.Add(remove);
        var fields = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                header,
                inputRow
            }
        };
        return Card(fields);
    }

    private Grid BuildResponsiveTileGrid(IReadOnlyList<FrameworkElement> tiles)
    {
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        var arrangedColumns = -1;
        void ArrangeTiles()
        {
            var columns = RootGrid.ActualWidth >= 1723 ? 2 : 1;
            if (arrangedColumns == columns && grid.Children.Count == tiles.Count)
                return;

            grid.ColumnDefinitions.Clear();
            grid.RowDefinitions.Clear();
            grid.Children.Clear();
            for (var column = 0; column < columns; column++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var row = 0; row < (int)Math.Ceiling(tiles.Count / (double)columns); row++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var index = 0; index < tiles.Count; index++)
            {
                Grid.SetRow(tiles[index], index / columns);
                Grid.SetColumn(tiles[index], index % columns);
                grid.Children.Add(tiles[index]);
            }
            arrangedColumns = columns;
        }
        grid.SizeChanged += (_, _) => ArrangeTiles();
        ArrangeTiles();
        return grid;
    }

    private FrameworkElement BuildStartupContent(StartupConfig config)
    {
        var panel = new StackPanel { Spacing = 12 };
        void Refresh() => RenderModule(_modules.First(module => ReferenceEquals(module.Config, config)));

        panel.Children.Add(new TextBlock
        {
            Text = "Startup Programs",
            FontSize = 18,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 }
        });
        panel.Children.Add(BuildResponsiveTileGrid(config.Programs
            .Select(program => BuildStartupProgramTile(config, program, Refresh))
            .Cast<FrameworkElement>()
            .ToList()));
        panel.Children.Add(ActionButton("+ Add Startup Program", () =>
        {
            config.Programs.Add(new StartupProgram());
            SaveConfiguration();
            Refresh();
        }));

        panel.Children.Add(new TextBlock
        {
            Text = "Processes To Run",
            FontSize = 18,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 },
            Margin = new Thickness(0, 12, 0, 0)
        });
        panel.Children.Add(BuildResponsiveTileGrid(config.ProcessesToRun
            .Select(process => BuildStartupProcessTile(config, process, Refresh))
            .Cast<FrameworkElement>()
            .ToList()));
        panel.Children.Add(ActionButton("+ Add Process", () =>
        {
            config.ProcessesToRun.Add(new ProcessToRun());
            SaveConfiguration();
            Refresh();
        }));

        return panel;
    }

    private FrameworkElement BuildStartupProgramTile(StartupConfig config, StartupProgram program, Action refresh)
    {
        TextBox TextField(string label, string value, Action<string> save)
        {
            var box = new TextBox { Header = label, Text = value, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12, 4, 12, 4) };
            box.LostFocus += (_, _) => { save(box.Text); SaveConfiguration(); };
            return box;
        }

        CheckBox CheckField(string label, bool value, Action<bool> save)
        {
            var checkBox = new CheckBox { Content = label, IsChecked = value, VerticalAlignment = VerticalAlignment.Center };
            checkBox.Checked += (_, _) => { save(true); SaveConfiguration(); };
            checkBox.Unchecked += (_, _) => { save(false); SaveConfiguration(); };
            return checkBox;
        }

        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(program.Name) ? "New startup program" : program.Name,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 },
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var name = TextField("Name", program.Name, value => program.Name = value);
        name.TextChanged += (_, _) => title.Text = string.IsNullOrWhiteSpace(name.Text) ? "New startup program" : name.Text;

        var remove = IconActionButton("\uE74D", "Remove startup program", () =>
        {
            config.Programs.Remove(program);
            SaveConfiguration();
            refresh();
        });
        remove.HorizontalAlignment = HorizontalAlignment.Right;

        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(title);
        var enabled = CheckField("Enabled", program.Enabled, value => program.Enabled = value);
        Grid.SetColumn(enabled, 1);
        header.Children.Add(enabled);
        var allUsers = CheckField("All Users", program.MachineLevel, value => program.MachineLevel = value);
        Grid.SetColumn(allUsers, 2);
        header.Children.Add(allUsers);
        Grid.SetColumn(remove, 3);
        header.Children.Add(remove);

        var fields = new Grid { ColumnSpacing = 12 };
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var inputs = new FrameworkElement[]
        {
            name,
            TextField("Executable Path", program.Path, value => program.Path = value),
            TextField("Arguments", program.Arguments, value => program.Arguments = value)
        };
        for (var index = 0; index < inputs.Length; index++)
        {
            Grid.SetColumn(inputs[index], index);
            fields.Children.Add(inputs[index]);
        }

        return Card(new StackPanel { Spacing = 10, Children = { header, fields } });
    }

    private FrameworkElement BuildStartupProcessTile(StartupConfig config, ProcessToRun process, Action refresh)
    {
        TextBox TextField(string label, string value, Action<string> save)
        {
            var box = new TextBox { Header = label, Text = value, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12, 4, 12, 4) };
            box.LostFocus += (_, _) => { save(box.Text); SaveConfiguration(); };
            return box;
        }

        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(process.Path) ? "New startup process" : Path.GetFileName(process.Path),
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 },
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var path = TextField("Executable Path", process.Path, value => process.Path = value);
        path.TextChanged += (_, _) => title.Text = string.IsNullOrWhiteSpace(path.Text) ? "New startup process" : Path.GetFileName(path.Text);

        var waitForExit = new CheckBox { Content = "Wait For Exit", IsChecked = process.WaitForExit, VerticalAlignment = VerticalAlignment.Center };
        waitForExit.Checked += (_, _) => { process.WaitForExit = true; SaveConfiguration(); };
        waitForExit.Unchecked += (_, _) => { process.WaitForExit = false; SaveConfiguration(); };
        var killAfter = new NumberBox
        {
            Header = "Kill After Seconds",
            Value = process.KillAfterSeconds ?? double.NaN,
            PlaceholderText = "Optional",
            Width = 150,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        killAfter.ValueChanged += (_, args) =>
        {
            process.KillAfterSeconds = double.IsNaN(args.NewValue) ? null : Convert.ToInt32(args.NewValue);
            SaveConfiguration();
        };
        var remove = IconActionButton("\uE74D", "Remove startup process", () =>
        {
            config.ProcessesToRun.Remove(process);
            SaveConfiguration();
            refresh();
        });
        remove.HorizontalAlignment = HorizontalAlignment.Right;

        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(title);
        Grid.SetColumn(waitForExit, 1);
        header.Children.Add(waitForExit);
        Grid.SetColumn(killAfter, 2);
        header.Children.Add(killAfter);
        Grid.SetColumn(remove, 3);
        header.Children.Add(remove);

        var fields = new Grid { ColumnSpacing = 12 };
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var inputs = new FrameworkElement[] { path, TextField("Arguments", process.Arguments, value => process.Arguments = value) };
        for (var index = 0; index < inputs.Length; index++)
        {
            Grid.SetColumn(inputs[index], index);
            fields.Children.Add(inputs[index]);
        }

        return Card(new StackPanel { Spacing = 10, Children = { header, fields } });
    }

    private FrameworkElement BuildFileCopyContent(FileCopyConfig config)
    {
        var panel = new StackPanel { Spacing = 12 };
        void Refresh() => RenderModule(_modules.First(module => ReferenceEquals(module.Config, config)));

        panel.Children.Add(new TextBlock
        {
            Text = "Restore saved files to their Windows locations. Add one restore operation per file or folder. Copy Matching Files copies a folder's matching files, such as *.lnk.",
            Foreground = ResourceBrush("WinstallerSecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(BuildResponsiveTileGrid(config.Operations
            .Select(operation => BuildFileCopyTile(config, operation, Refresh))
            .Cast<FrameworkElement>()
            .ToList()));
        panel.Children.Add(ActionButton("+ Add Restore Operation", () =>
        {
            config.Operations.Add(new FileCopyOperation());
            SaveConfiguration();
            Refresh();
        }));
        return panel;
    }

    private FrameworkElement BuildFileCopyTile(FileCopyConfig config, FileCopyOperation operation, Action refresh)
    {
        TextBox TextField(string label, string value, Action<string> save)
        {
            var box = new TextBox { Header = label, Text = value, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12, 4, 12, 4) };
            box.LostFocus += (_, _) => { save(box.Text); SaveConfiguration(); };
            return box;
        }

        CheckBox CheckField(string label, bool value, Action<bool> save)
        {
            var checkBox = new CheckBox { Content = label, IsChecked = value, VerticalAlignment = VerticalAlignment.Center };
            checkBox.Checked += (_, _) => { save(true); SaveConfiguration(); };
            checkBox.Unchecked += (_, _) => { save(false); SaveConfiguration(); };
            return checkBox;
        }

        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(operation.Name) ? "New restore operation" : operation.Name,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 },
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var name = TextField("Name", operation.Name, value => operation.Name = value);
        name.TextChanged += (_, _) => title.Text = string.IsNullOrWhiteSpace(name.Text) ? "New restore operation" : name.Text;
        var remove = IconActionButton("\uE74D", "Remove restore operation", () =>
        {
            config.Operations.Remove(operation);
            SaveConfiguration();
            refresh();
        });
        remove.HorizontalAlignment = HorizontalAlignment.Right;

        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(title);
        var copyMatching = CheckField("Copy Matching Files", operation.MatchingFiles, value => operation.MatchingFiles = value);
        Grid.SetColumn(copyMatching, 1);
        header.Children.Add(copyMatching);
        Grid.SetColumn(remove, 2);
        header.Children.Add(remove);

        var locations = new Grid { ColumnSpacing = 12 };
        locations.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        locations.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var inputs = new FrameworkElement[]
        {
            name,
            TextField("Source File Or Folder", operation.Source, value => operation.Source = value),
            TextField("Destination File Or Folder", operation.Destination, value => operation.Destination = value),
            TextField("Search Pattern", operation.SearchPattern, value => operation.SearchPattern = value)
        };

        var firstRow = new Grid { ColumnSpacing = 12 };
        firstRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        firstRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        Grid.SetColumn(inputs[0], 0);
        firstRow.Children.Add(inputs[0]);
        Grid.SetColumn(inputs[1], 1);
        firstRow.Children.Add(inputs[1]);

        Grid.SetColumn(inputs[2], 0);
        locations.Children.Add(inputs[2]);
        Grid.SetColumn(inputs[3], 1);
        locations.Children.Add(inputs[3]);

        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        options.Children.Add(CheckField("Replace Existing", operation.Overwrite, value => operation.Overwrite = value));
        options.Children.Add(CheckField("Rewrite Shortcut Profile Paths", operation.RewriteShortcutProfilePaths, value => operation.RewriteShortcutProfilePaths = value));
        options.Children.Add(CheckField("Protect Private Key", operation.ProtectPrivateKeyAcl, value => operation.ProtectPrivateKeyAcl = value));
        options.Children.Add(CheckField("Do Not Back Up", operation.SkipPreReinstallBackup, value => operation.SkipPreReinstallBackup = value));

        return Card(new StackPanel { Spacing = 10, Children = { header, firstRow, locations, options } });
    }


    private FrameworkElement BuildConfigEditor(object config, bool includeScalarSettings = true)
    {
        var panel = new StackPanel { Spacing = 12 };
        foreach (var property in config.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite || property.Name == "Enabled")
            {
                continue;
            }

            if (!includeScalarSettings && !IsSupportedList(property.PropertyType))
            {
                continue;
            }

            if (!includeScalarSettings && property.Name.Contains("Ignored", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            panel.Children.Add(BuildConfigSection(config, property));
        }

        return panel;
    }

    private FrameworkElement BuildSystemSettingsContent(SystemSettingsConfig settings)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(BuildUacSettingCard(settings.Uac));
        var compactSettings = new List<FrameworkElement>
        {
            BuildAppliedSettingCard("Computer name", "Rename this PC. Windows applies it after restart.", settings.ComputerName, new TextBox
        {
            Text = settings.ComputerName.Value,
            PlaceholderText = "Computer name",
            HorizontalAlignment = HorizontalAlignment.Stretch
        }, value => settings.ComputerName.Value = (string)value),
            BuildAppliedSettingCard("Transparency effects", "Show transparency in Windows surfaces.", settings.Transparency, new ToggleSwitch
        {
            IsOn = settings.Transparency.Value
        }, value => settings.Transparency.Value = (bool)value),
            BuildAppliedSettingCard("Treat UNC paths as intranet", "Apply Local Intranet zone rules to UNC network paths.", settings.UncAsIntranet, new ToggleSwitch
        {
            IsOn = settings.UncAsIntranet.Value != 0
        }, value => settings.UncAsIntranet.Value = (bool)value ? 1 : 0),
            BuildAppliedSettingCard("Do not preserve download zone information", "Stop Windows saving source-zone metadata for downloaded files.", settings.SaveZoneInformation, new ToggleSwitch
        {
            IsOn = settings.SaveZoneInformation.Value != 0
        }, value => settings.SaveZoneInformation.Value = (bool)value ? 1 : 0)
        };
        panel.Children.Add(BuildProgressiveTileGrid(compactSettings, 210, 188, setting => setting));
        return panel;
    }

    private FrameworkElement BuildAppliedSettingCard<T>(string title, string description, AppliedSetting<T> setting, Control valueEditor, Action<object> setValue)
    {
        var apply = new ToggleSwitch { Header = "Apply this setting", IsOn = setting.Apply };
        valueEditor.IsEnabled = setting.Apply;
        apply.Toggled += (_, _) =>
        {
            if (_isLoadingUi) return;
            setting.Apply = apply.IsOn;
            valueEditor.IsEnabled = apply.IsOn;
            SaveConfiguration();
        };

        switch (valueEditor)
        {
            case TextBox box:
                box.LostFocus += (_, _) =>
                {
                    setValue(box.Text);
                    SaveConfiguration();
                };
                break;
            case ToggleSwitch toggle:
                toggle.Toggled += (_, _) =>
                {
                    if (_isLoadingUi) return;
                    setValue(toggle.IsOn);
                    SaveConfiguration();
                };
                break;
        }

        return Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = title, FontSize = 17, FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 } },
                new TextBlock { Text = description, Foreground = ResourceBrush("WinstallerSecondaryTextBrush"), TextWrapping = TextWrapping.WrapWholeWords },
                apply,
                valueEditor
            }
        });
    }

    private FrameworkElement BuildUacSettingCard(AppliedSetting<UacLevel> setting)
    {
        var labels = new[]
        {
            "Never notify",
            "Notify when apps change Windows, without secure desktop",
            "Notify when apps change Windows",
            "Always notify"
        };
        var current = Math.Clamp((int)setting.Value, 0, labels.Length - 1);
        var selected = new TextBlock { Text = labels[current], FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 } };
        var slider = new Slider { Minimum = 0, Maximum = labels.Length - 1, StepFrequency = 1, TickFrequency = 1, Value = current, IsEnabled = setting.Apply };
        var apply = new ToggleSwitch { Header = "Apply UAC setting", IsOn = setting.Apply };
        apply.Toggled += (_, _) =>
        {
            if (_isLoadingUi) return;
            setting.Apply = apply.IsOn;
            slider.IsEnabled = apply.IsOn;
            SaveConfiguration();
        };
        slider.ValueChanged += (_, args) =>
        {
            if (_isLoadingUi) return;
            var value = Math.Clamp((int)Math.Round(args.NewValue), 0, labels.Length - 1);
            setting.Value = (UacLevel)value;
            selected.Text = labels[value];
            SaveConfiguration();
        };

        return Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "User Account Control", FontSize = 17, FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 } },
                new TextBlock { Text = "Choose when Windows asks permission before elevation.", Foreground = ResourceBrush("WinstallerSecondaryTextBrush"), TextWrapping = TextWrapping.WrapWholeWords },
                apply,
                selected,
                slider
            }
        });
    }

    private FrameworkElement BuildConfigSection(object target, PropertyInfo property)
    {
        return IsSupportedList(property.PropertyType)
            ? BuildListSection(target, property)
            : BuildSettingRow(target, property);
    }

    private FrameworkElement BuildSettingRow(object target, PropertyInfo property)
    {
        var row = new StackPanel { Spacing = 10 };
        var header = new Grid { ColumnSpacing = 14 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        header.Children.Add(new FontIcon
        {
            Glyph = GetConfigGlyph(property, null),
            FontSize = 20,
            Width = 28,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0)
        });

        var label = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock
        {
            Text = SplitName(property.Name),
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 },
            TextWrapping = TextWrapping.WrapWholeWords
        });
        label.Children.Add(new TextBlock
        {
            Text = GetSettingDescription(property),
            Foreground = ResourceBrush("WinstallerSecondaryTextBrush"),
            FontSize = 12,
            TextWrapping = TextWrapping.WrapWholeWords
        });
        Grid.SetColumn(label, 1);
        header.Children.Add(label);
        row.Children.Add(header);

        var editor = BuildValueEditor(target, property);
        editor.VerticalAlignment = VerticalAlignment.Center;
        editor.HorizontalAlignment = HorizontalAlignment.Stretch;
        editor.Margin = new Thickness(42, 0, 0, 0);
        row.Children.Add(editor);

        var card = Card(row);
        card.MaxWidth = 760;
        card.HorizontalAlignment = HorizontalAlignment.Stretch;
        return card;
    }

    private FrameworkElement BuildPropertyEditor(object target, PropertyInfo property)
    {
        var row = new Grid { ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        row.Children.Add(new StackPanel
        {
            Children =
            {
                new TextBlock { Text = SplitName(property.Name), FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 } },
                new TextBlock { Text = property.PropertyType.Name, Opacity = 0.6, FontSize = 12 }
            }
        });

        var editor = IsSupportedList(property.PropertyType)
            ? BuildListEditor(target, property)
            : BuildValueEditor(target, property);

        Grid.SetColumn(editor, 1);
        row.Children.Add(editor);
        return row;
    }

    private FrameworkElement BuildValueEditor(object target, PropertyInfo property)
    {
        var value = property.GetValue(target);

        FrameworkElement editor;
        if (property.PropertyType == typeof(bool))
        {
            var toggle = new ToggleSwitch { IsOn = value is true };
            toggle.Toggled += (_, _) =>
            {
                if (_isLoadingUi)
                {
                    return;
                }

                property.SetValue(target, toggle.IsOn);
                SaveConfiguration();
            };
            editor = toggle;
        }
        else if (property.PropertyType == typeof(int))
        {
            var box = new NumberBox
            {
                Value = value is int intValue ? intValue : 0,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            box.ValueChanged += (_, args) =>
            {
                if (_isLoadingUi)
                {
                    return;
                }

                if (!double.IsNaN(args.NewValue))
                {
                    property.SetValue(target, Convert.ToInt32(args.NewValue));
                    SaveConfiguration();
                }
            };
            editor = box;
        }
        else if (property.PropertyType == typeof(string))
        {
            var isReadOnly = IsReadOnlySetting(target, property);
            if (isReadOnly)
            {
                editor = new Border
                {
                    Background = ResourceBrush("WinstallerCardBrush"),
                    BorderBrush = ResourceBrush("WinstallerCardStrokeBrush"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 8, 10, 8),
                    Opacity = 0.68,
                    Child = new TextBlock
                    {
                        Text = value?.ToString() ?? string.Empty,
                        Foreground = ResourceBrush("WinstallerSecondaryTextBrush"),
                        TextWrapping = TextWrapping.WrapWholeWords
                    }
                };
                return editor;
            }

            var box = new TextBox
            {
                Text = value?.ToString() ?? string.Empty,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                TextWrapping = TextWrapping.NoWrap
            };

            box.LostFocus += (_, _) =>
            {
                property.SetValue(target, box.Text);
                SaveConfiguration();
            };

            editor = box;
        }
        else
        {
            editor = new TextBlock
            {
                Text = "This setting type is not editable yet.",
                Opacity = 0.65,
                TextWrapping = TextWrapping.Wrap
            };
        }

        return editor;
    }

    private static bool IsReadOnlySetting(object target, PropertyInfo property)
    {
        return target is SymlinksConfig && property.Name == nameof(SymlinksConfig.BaseSymlinkDirectory);
    }

    private FrameworkElement BuildListSection(object target, PropertyInfo property, bool allowAdd = true)
    {
        return Card(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                BuildListHeader(target, property),
                BuildListEditor(target, property, allowAdd)
            }
        });
    }

    private FrameworkElement BuildCollapsibleListSection(object target, PropertyInfo property, string title, string description, bool allowAdd = true)
    {
        var list = (IList?)property.GetValue(target);
        var count = list?.Count ?? 0;
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        header.Children.Add(new FontIcon
        {
            Glyph = GetConfigGlyph(property, null),
            FontSize = 19,
            Width = 28,
            VerticalAlignment = VerticalAlignment.Center
        });

        var label = new StackPanel { Spacing = 2 };
        label.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 17,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 }
        });
        label.Children.Add(new TextBlock
        {
            Text = description,
            Foreground = ResourceBrush("WinstallerSecondaryTextBrush"),
            FontSize = 12
        });
        Grid.SetColumn(label, 1);
        header.Children.Add(label);

        var countText = new TextBlock
        {
            Text = $"{count} item{(count == 1 ? string.Empty : "s")}",
            Foreground = ResourceBrush("WinstallerSecondaryTextBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(countText, 2);
        header.Children.Add(countText);

        var expander = new Expander
        {
            Header = header,
            Content = BuildListEditor(target, property, allowAdd),
            IsExpanded = count <= 8
        };

        return Card(expander);
    }

    private FrameworkElement BuildListHeader(object target, PropertyInfo property)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new FontIcon
        {
            Glyph = GetConfigGlyph(property, null),
            FontSize = 20,
            Width = 28,
            VerticalAlignment = VerticalAlignment.Center
        });

        var title = new StackPanel { Spacing = 2 };
        title.Children.Add(new TextBlock
        {
            Text = SplitName(property.Name),
            FontSize = 18,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 }
        });
        title.Children.Add(new TextBlock
        {
            Text = GetSettingDescription(property),
            Foreground = ResourceBrush("WinstallerSecondaryTextBrush"),
            FontSize = 12
        });
        Grid.SetColumn(title, 1);
        row.Children.Add(title);

        return row;
    }

    private FrameworkElement BuildListEditor(object target, PropertyInfo property, bool allowAdd = true)
    {
        var list = (IList?)property.GetValue(target);
        if (list is null)
        {
            list = (IList)Activator.CreateInstance(property.PropertyType)!;
            property.SetValue(target, list);
        }

        var itemType = property.PropertyType.GetGenericArguments()[0];
        var panel = new StackPanel { Spacing = 8 };
        var emptyText = new TextBlock { Text = "No items configured.", Opacity = 0.65 };
        var items = new ItemsRepeater
        {
            VerticalCacheLength = 0.75,
            Layout = new StackLayout { Spacing = 8 }
        };
        items.ItemTemplate = new CallbackElementFactory(data =>
        {
            var item = (IndexedItem)data!;
            return BuildListItemEditor(list, itemType, item.Index, Refresh, property);
        });
        panel.Children.Add(emptyText);
        panel.Children.Add(items);

        void Refresh()
        {
            emptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            items.ItemsSource = list.Cast<object>()
                .Select((item, index) => new IndexedItem(item, index))
                .ToList();
        }

        if (allowAdd)
            panel.Children.Add(ActionButton($"+ Add {Singularize(SplitName(property.Name))}", () =>
            {
                list.Add(CreateDefaultItem(itemType));
                SaveConfiguration();
                Refresh();
            }));

        Refresh();
        return panel;
    }

    private FrameworkElement BuildListItemEditor(IList list, Type itemType, int index, Action refresh, PropertyInfo? listProperty = null)
    {
        var item = list[index]!;
        var header = GetItemTitle(item, itemType, index);
        if (listProperty?.Name.Contains("Ignored", StringComparison.OrdinalIgnoreCase) == true)
        {
            header += " (Ignored)";
        }

        var body = new StackPanel { Spacing = 8 };
        if (itemType == typeof(string))
        {
            var box = new TextBox
            {
                Text = item.ToString() ?? string.Empty,
                PlaceholderText = "Value",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            box.LostFocus += (_, _) =>
            {
                list[index] = box.Text;
                SaveConfiguration();
            };
            body.Children.Add(box);
        }
        else
        {
            foreach (var property in itemType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.CanRead && property.CanWrite)
                {
                    body.Children.Add(BuildInlineObjectPropertyEditor(item, property));
                }
            }
        }

        var removeButton = IconActionButton("\uE74D", "Remove", () =>
        {
            list.RemoveAt(index);
            SaveConfiguration();
            refresh();
        });

        var row = new Grid { ColumnSpacing = 14 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new FontIcon
        {
            Glyph = GetConfigGlyph(listProperty, item),
            FontSize = 20,
            Width = 28,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 0, 0)
        });

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = header,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 }
        });
        content.Children.Add(body);
        Grid.SetColumn(content, 1);
        row.Children.Add(content);

        Grid.SetColumn(removeButton, 2);
        row.Children.Add(removeButton);

        return Card(row);
    }


    private FrameworkElement BuildInlineObjectPropertyEditor(object target, PropertyInfo property)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        row.Children.Add(new TextBlock
        {
            Text = GetInlinePropertyName(target, property),
            VerticalAlignment = VerticalAlignment.Center
        });

        FrameworkElement editor;
        var value = property.GetValue(target);
        var nullableType = Nullable.GetUnderlyingType(property.PropertyType);
        var effectiveType = nullableType ?? property.PropertyType;

        if (effectiveType == typeof(bool))
        {
            var toggle = new ToggleSwitch { IsOn = value is true };
            toggle.Toggled += (_, _) =>
            {
                if (_isLoadingUi)
                {
                    return;
                }

                property.SetValue(target, toggle.IsOn);
                SaveConfiguration();
            };
            editor = toggle;
        }
        else if (effectiveType == typeof(int))
        {
            var box = new NumberBox
            {
                Value = value is int intValue ? intValue : double.NaN,
                PlaceholderText = nullableType is null ? "0" : "Optional",
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
            };
            box.ValueChanged += (_, args) =>
            {
                if (_isLoadingUi)
                {
                    return;
                }

                if (double.IsNaN(args.NewValue) && nullableType is not null)
                {
                    property.SetValue(target, null);
                }
                else if (!double.IsNaN(args.NewValue))
                {
                    property.SetValue(target, Convert.ToInt32(args.NewValue));
                }
                SaveConfiguration();
            };
            editor = box;
        }
        else if (effectiveType.IsEnum)
        {
            var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var enumValue in Enum.GetValues(effectiveType)) combo.Items.Add(enumValue);
            combo.SelectedItem = value;
            combo.SelectionChanged += (_, _) =>
            {
                if (!_isLoadingUi && combo.SelectedItem is not null)
                    property.SetValue(target, combo.SelectedItem);
            };
            editor = combo;
        }
        else
        {
            var box = new TextBox
            {
                Text = value?.ToString() ?? string.Empty,
                PlaceholderText = SplitName(property.Name),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            box.LostFocus += (_, _) =>
            {
                property.SetValue(target, string.IsNullOrWhiteSpace(box.Text) && nullableType is not null ? null : box.Text);
                SaveConfiguration();
            };
            editor = box;
        }

        Grid.SetColumn(editor, 1);
        row.Children.Add(editor);
        return row;
    }

    private static string GetInlinePropertyName(object target, PropertyInfo property)
    {
        if (target is StartupProgram)
        {
            return property.Name switch
            {
                "MachineLevel" => "Add To All Users Registry",
                "Enabled" => "Start Enabled",
                _ => SplitName(property.Name)
            };
        }

        return SplitName(property.Name);
    }

    private static bool IsSupportedList(Type type)
    {
        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>);
    }

    private static object CreateDefaultItem(Type itemType)
    {
        if (itemType == typeof(string))
        {
            return string.Empty;
        }

        return Activator.CreateInstance(itemType) ?? throw new InvalidOperationException($"Could not create {itemType.Name}");
    }

    private FrameworkElement SettingCard(string title, ToggleSwitch toggle, Action<ToggleSwitch> configure)
    {
        configure(toggle);
        return new SettingsCard
        {
            Header = title,
            Content = toggle,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
    }

    private StackPanel PageTitle(string title, string subtitle)
    {
        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = 28,
                    FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 }
                },
                new TextBlock
                {
                    Text = subtitle,
                    Foreground = ResourceBrush("WinstallerSecondaryTextBrush"),
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
    }

    private TextBlock SectionTitle(string title)
    {
        return new TextBlock
        {
            Text = title,
            FontSize = 18,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 }
        };
    }

    private Button ActionButton(string text, Action action, bool primary = false)
    {
        return ActionButton(text, () =>
        {
            action();
            return Task.CompletedTask;
        }, primary);
    }

    private Button ActionButton(string text, Func<Task> action, bool primary = false)
    {
        var button = new Button
        {
            Content = text,
            Style = primary ? (Style)Application.Current.Resources["AccentButtonStyle"] : null,
            CornerRadius = new CornerRadius(4),
            MinHeight = 32,
            Padding = new Thickness(12, 6, 12, 6)
        };
        button.Click += async (_, _) =>
        {
            if (!button.IsEnabled)
                return;

            button.IsEnabled = false;
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                RunLog.WriteException("UI", $"{text} failed", ex);
                await RunOnUiThreadAsync(() => AppendOutput($"{text} failed: {ex.Message}"));
            }
            finally
            {
                await RunOnUiThreadAsync(() => button.IsEnabled = true);
            }
        };
        return button;
    }

    private Button IconActionButton(string glyph, string label, Action action)
    {
        var button = ActionButton(label, action);
        button.Content = new FontIcon { Glyph = glyph, FontSize = 16 };
        button.Width = 32;
        button.Height = 32;
        button.MinWidth = 32;
        button.Padding = new Thickness(0);
        ToolTipService.SetToolTip(button, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        return button;
    }

    private Button IconActionButton(string glyph, string label, Func<Task> action)
    {
        var button = ActionButton(label, action);
        button.Content = new FontIcon { Glyph = glyph, FontSize = 16 };
        button.Width = 32;
        button.Height = 32;
        button.MinWidth = 32;
        button.Padding = new Thickness(0);
        ToolTipService.SetToolTip(button, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        return button;
    }
    private Button TopBarActionButton(Symbol icon, string text, Action action, bool primary = false)
    {
        return TopBarActionButton(icon, text, () =>
        {
            action();
            return Task.CompletedTask;
        }, primary);
    }

    private Button TopBarActionButton(Symbol icon, string text, Func<Task> action, bool primary = false)
    {
        var label = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center
        };
        _topBarActionLabels.Add(label);

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new SymbolIcon(icon),
                label
            }
        };

        var button = new Button
        {
            Content = content,
            Style = primary ? (Style)Application.Current.Resources["AccentButtonStyle"] : null,
            CornerRadius = new CornerRadius(4),
            MinHeight = 32,
            Padding = new Thickness(10, 5, 10, 5),
            VerticalAlignment = VerticalAlignment.Center
        };
        button.IsEnabled = !_isRunning;
        ToolTipService.SetToolTip(button, text);
        button.Click += async (_, _) =>
        {
            if (_isRunning)
            {
                AppendOutput("Operation already running.");
                return;
            }

            button.IsEnabled = false;
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                RunLog.WriteException("UI", $"{text} failed", ex);
                AppendOutput($"{text} failed: {ex.Message}");
            }
            finally
            {
                await RunOnUiThreadAsync(() => button.IsEnabled = true);
            }
        };
        return button;
    }

    private void UpdateTopBarActionLabelVisibility()
    {
        var iconOnly = (GetAppWindow()?.Size.Width ?? 0) < 1260;
        foreach (var label in _topBarActionLabels)
        {
            label.Visibility = iconOnly ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private Border Card(UIElement child)
    {
        return new Border
        {
            Background = ResourceBrush("WinstallerCardBrush"),
            BorderBrush = ResourceBrush("WinstallerCardStrokeBrush"),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Child = child,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
    }
}

