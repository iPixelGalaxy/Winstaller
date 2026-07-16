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
private static string GetSettingDescription(PropertyInfo property)
    {
        if (IsSupportedList(property.PropertyType))
        {
            return "Configured items";
        }

        return property.PropertyType == typeof(bool) ? "On or off" :
            property.PropertyType == typeof(int) ? "Number" :
            property.PropertyType == typeof(string) ? "Text" :
            "Setting";
    }

    private static bool IsBasicModule(string name)
    {
        return name is "Symlinks" or "App Installer" or "Fonts" or "Shell Folders" or "Path" or "Network Drives";
    }

    private static bool HasIgnoredItems(object config)
    {
        return config.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(property => property.Name.Contains("Ignored", StringComparison.OrdinalIgnoreCase) &&
                             IsSupportedList(property.PropertyType));
    }

    private static string GetEmptyImportMessage(SystemInfoImportScope scope)
    {
        return scope switch
        {
            SystemInfoImportScope.NetworkDrives => "No new network drives were found.",
            SystemInfoImportScope.AppInstaller => "No new installed apps were found.",
            SystemInfoImportScope.Fonts => "No new installed fonts were found.",
            SystemInfoImportScope.ShellFolders => "All standard shell folders are already configured.",
            SystemInfoImportScope.Symlinks => "No new symlink candidates were found.",
            SystemInfoImportScope.Path => "No new PATH entries were found.",
            SystemInfoImportScope.Startup => "No new startup items were found.",
            _ => "No new importable items were found."
        };
    }

    private static IReadOnlyList<ShellFolderPreset> GetShellFolderPresets()
    {
        return
        [
            new("Desktop", "Desktop", @"D:\{USERNAME}\Desktop"),
            new("Downloads", "{374DE290-123F-4565-9164-39C4925E467B}", @"D:\{USERNAME}\Downloads"),
            new("Documents", "Personal", @"D:\{USERNAME}\Documents"),
            new("Pictures", "My Pictures", @"D:\{USERNAME}\Pictures"),
            new("Music", "My Music", @"D:\{USERNAME}\Music"),
            new("Videos", "My Video", @"D:\{USERNAME}\Videos")
        ];
    }

    private static string GetImportLabel(ModuleDescriptor module)
    {
        return module.ImportScope switch
        {
            SystemInfoImportScope.AppInstaller => "Import Installed Apps",
            SystemInfoImportScope.Fonts => "Import Installed Fonts",
            SystemInfoImportScope.NetworkDrives => "Import Network Drives",
            SystemInfoImportScope.Symlinks => "Import Symlinks",
            SystemInfoImportScope.ShellFolders => "Import Shell Folders",
            SystemInfoImportScope.Path => "Import PATH Entries",
            SystemInfoImportScope.Startup => "Import Startup Items",
            _ => "Import System Info"
        };
    }

    private static string GetKnownPackageName(string packageId)
    {
        var knownName = RecommendedAppCatalog.GetKnownDisplayName(packageId);
        if (!string.IsNullOrWhiteSpace(knownName)) return knownName;
        return packageId.ToLowerInvariant() switch
        {
            "discord.discord" => "Discord",
            "spotify.spotify" => "Spotify",
            "git.git" => "Git",
            "github.gitlfs" => "Git LFS",
            "microsoft.visualstudiocode" => "Visual Studio Code",
            "" => "New app",
            _ => packageId
        };
    }

    private static bool KnownBehaviorPackage(string packageId)
    {
        return packageId.Equals("Discord.Discord", StringComparison.OrdinalIgnoreCase) ||
               packageId.Equals("Spotify.Spotify", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetItemTitle(object item, Type itemType, int index)
    {
        return item switch
        {
            string value when !string.IsNullOrWhiteSpace(value) => value,
            NetworkDriveMapping drive when !string.IsNullOrWhiteSpace(drive.DriveLetter) => $"{drive.DriveLetter}: {drive.NetworkPath}",
            ShellFolderMapping folder when !string.IsNullOrWhiteSpace(folder.FolderName) => folder.FolderName,
            StartupProgram program when !string.IsNullOrWhiteSpace(program.Name) => program.Name,
            ProcessToRun process when !string.IsNullOrWhiteSpace(process.Path) => Path.GetFileName(process.Path),
            FileCopyOperation copy when !string.IsNullOrWhiteSpace(copy.Source) => Path.GetFileName(copy.Source),
            RegistryModification modification when !string.IsNullOrWhiteSpace(modification.Key) => modification.Key,
            CustomInstaller installer when !string.IsNullOrWhiteSpace(installer.Name) => installer.Name,
            SpecialSymlink symlink when !string.IsNullOrWhiteSpace(symlink.Description) => symlink.Description,
            _ => itemType == typeof(string) ? "New entry" : $"New {SplitName(itemType.Name).ToLowerInvariant()}"
        };
    }

    private static string GetConfigGlyph(PropertyInfo? property, object? item)
    {
        if (item is ShellFolderMapping folder)
        {
            return folder.FolderName.ToLowerInvariant() switch
            {
                "desktop" => "\uE80F",
                "downloads" => "\uE896",
                "documents" => "\uE8A5",
                "pictures" => "\uEB9F",
                "music" => "\uEC4F",
                "videos" => "\uE8B2",
                _ => "\uE8B7"
            };
        }

        if (item is NetworkDriveMapping)
            return "\uE839";
        if (item is StartupProgram or ProcessToRun)
            return "\uE768";
        if (item is FileCopyOperation)
            return "\uE8C8";
        if (item is RegistryModification)
            return "\uE7B8";
        if (item is CustomInstaller)
            return "\uE896";
        if (item is SpecialSymlink)
            return "\uE71B";

        var name = property?.Name ?? string.Empty;
        return name switch
        {
            "Folders" => "\uE8B7",
            "Drives" => "\uE839",
            "Additions" => "\uE943",
            "Programs" or "ProcessesToRun" => "\uE768",
            "FilesToImport" or "Modifications" => "\uE7B8",
            "Operations" => "\uE8C8",
            "CustomScripts" or "DefaultInstalls" => "\uE896",
            "RoamingDirectories" => "\uE8B7",
            "LocalDirectories" => "\uE8B7",
            "LocalLowDirectories" => "\uE8B7",
            "SpecialSymlinks" => "\uE8A5",
            "FontsDirectory" => "\uE8D2",
            _ => "\uE8A5"
        };
    }

    private static string SplitName(string value)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (i > 0 && char.IsUpper(value[i]) && !char.IsUpper(value[i - 1]))
            {
                builder.Append(' ');
            }
            builder.Append(value[i]);
        }
        return builder.ToString();
    }

    private static string Singularize(string value)
    {
        return value.EndsWith("Directories", StringComparison.Ordinal) ? value[..^3] + "y" :
            value.EndsWith("ies", StringComparison.Ordinal) ? value[..^3] + "y" :
            value.EndsWith('s') && value.Length > 1 ? value[..^1] : value;
    }

    private sealed record AppIconView(Grid Host, Image Image, FontIcon Fallback, ProgressRing Spinner);
    private sealed record AppBehaviorDialogState(string PackageId, AppInstallBehavior Behavior);
    private sealed record AppGroupSection(RecommendedAppGroupInfo Group, List<string> PackageIds, VariableSizedWrapGrid Tiles, Action RefreshTiles, StackPanel Section);
    private sealed record IndexedItem(object Value, int Index);
    private sealed class ReusableSymlinkRow : Grid
    {
        public IndexedItem? Item { get; private set; }
        public Action<IndexedItem>? BindAction { get; set; }
        public Action? RecycleAction { get; set; }

        public void Bind(IndexedItem item)
        {
            Item = item;
            BindAction?.Invoke(item);
        }

        public void Recycle()
        {
            RecycleAction?.Invoke();
            Item = null;
        }
    }

    private sealed class RecyclableRowFactory(Func<ReusableSymlinkRow> create) : IElementFactory
    {
        private readonly Stack<ReusableSymlinkRow> _pool = new();

        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            var row = _pool.Count > 0 ? _pool.Pop() : create();
            row.Bind((IndexedItem)args.Data!);
            return row;
        }

        public void RecycleElement(ElementFactoryRecycleArgs args)
        {
            if (args.Element is not ReusableSymlinkRow row)
                return;

            row.Recycle();
            _pool.Push(row);
        }
    }

    private sealed class CallbackElementFactory(Func<object?, UIElement> create, Action<UIElement>? recycle = null) : IElementFactory
    {
        public UIElement GetElement(ElementFactoryGetArgs args) => create(args.Data);

        public void RecycleElement(ElementFactoryRecycleArgs args)
        {
            recycle?.Invoke(args.Element);
        }
    }
    private sealed record ShellFolderPreset(string Name, string RegistryValue, string DefaultPath);
    private sealed record SymlinkImportSelection(
        List<SystemInfoImportCandidate> Selected,
        List<SystemInfoImportCandidate> Ignored,
        SymlinkImportMode Mode);

    private sealed record ModuleDescriptor(
        string Name,
        string Description,
        string IconGlyph,
        object Config,
        Func<IModule> CreateModule,
        SystemInfoImportScope? ImportScope)
    {
        public bool IsEnabled => Config.GetType().GetProperty("Enabled")?.GetValue(Config) is true;

        public void SetEnabled(bool enabled)
        {
            Config.GetType().GetProperty("Enabled")?.SetValue(Config, enabled);
        }
    }

    private sealed class BufferedTextBoxWriter(Action<string> write, string logArea) : TextWriter
    {
        private readonly object _lock = new();
        private readonly StringBuilder _buffer = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_lock)
            {
                _buffer.Append(value);
                if (value == '\n')
                    FlushLocked();
            }
        }

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return;

            lock (_lock)
            {
                _buffer.Append(value);
                if (value.Contains('\n', StringComparison.Ordinal))
                    FlushLocked();
            }
        }

        public override void WriteLine(string? value)
        {
            var line = (value ?? string.Empty) + Environment.NewLine;
            RunLog.Write(logArea, value ?? string.Empty);
            write(line);
        }

        public override void Flush()
        {
            lock (_lock)
                FlushLocked();
        }

        private void FlushLocked()
        {
            if (_buffer.Length == 0)
                return;

            var text = _buffer.ToString();
            _buffer.Clear();
            RunLog.Write(logArea, text.TrimEnd());
            write(text);
        }
    }
}

