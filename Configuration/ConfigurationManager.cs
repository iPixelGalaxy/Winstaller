using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Winstaller.Models;
using Winstaller.Utilities;

namespace Winstaller.Configuration;

public static class ConfigurationManager
{
    private const string LegacyConfigFileName = "winstaller-config.json";
    private const string GeneralConfigFileName = "general.json";
    private const string ModuleDirectoryName = "modules";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly ModuleConfigDescriptor[] ModuleConfigs =
    [
        new("networkDrives", "network-drives.json", nameof(WinstallerConfig.NetworkDrives)),
        new("symlinks", "symlinks.json", nameof(WinstallerConfig.Symlinks)),
        new("appInstaller", "app-installer.json", nameof(WinstallerConfig.AppInstaller)),
        new("fonts", "fonts.json", nameof(WinstallerConfig.Fonts)),
        new("shellFolders", "shell-folders.json", nameof(WinstallerConfig.ShellFolders)),
        new("registry", "registry.json", nameof(WinstallerConfig.Registry)),
        new("fileCopy", "file-copy.json", nameof(WinstallerConfig.FileCopy)),
        new("startup", "startup.json", nameof(WinstallerConfig.Startup)),
        new("path", "path.json", nameof(WinstallerConfig.Path)),
        new("discord", "discord.json", nameof(WinstallerConfig.Discord)),
        new("spotify", "spotify.json", nameof(WinstallerConfig.Spotify)),
        new("appDataUtility", "app-data-utility.json", nameof(WinstallerConfig.AppDataUtility)),
    ];

    public static string ConfigDirectory =>
        BootstrapManager.DataRoot is null
            ? AppContext.BaseDirectory
            : BootstrapManager.ConfigDirectory;

    public static string ModuleConfigDirectory => Path.Combine(ConfigDirectory, ModuleDirectoryName);

    public static string DefaultConfigPath => Path.Combine(ConfigDirectory, GeneralConfigFileName);

    public static WinstallerConfig LoadConfiguration(string? path = null)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            return LoadMonolithicConfiguration(path);
        }

        EnsureSplitConfiguration();
        var config = CreateDefaultConfiguration();
        var general = LoadJson(DefaultConfigPath, CreateDefaultGeneralConfig(config));

        foreach (var descriptor in ModuleConfigs)
        {
            var property = descriptor.GetProperty();
            var modulePath = Path.Combine(ModuleConfigDirectory, descriptor.FileName);
            var value = LoadModuleJson(modulePath, property.PropertyType, property.GetValue(config)!);
            property.SetValue(config, value);

            if (general.Modules.TryGetValue(descriptor.Id, out var enabled))
            {
                SetEnabled(value, enabled);
            }
        }

        SanitizeSymlinkConfig(config.Symlinks);
        return config;
    }

    public static void SaveConfiguration(WinstallerConfig config, string? path = null)
    {
        SanitizeSymlinkConfig(config.Symlinks);

        if (!string.IsNullOrWhiteSpace(path))
        {
            SaveMonolithicConfiguration(config, path);
            return;
        }

        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(ModuleConfigDirectory);

        var general = LoadJson(DefaultConfigPath, CreateDefaultGeneralConfig(config));
        foreach (var descriptor in ModuleConfigs)
        {
            var value = descriptor.GetProperty().GetValue(config)!;
            general.Modules[descriptor.Id] = GetEnabled(value);

            SaveJson(Path.Combine(ModuleConfigDirectory, descriptor.FileName), ToJsonWithoutEnabled(value));
        }

        SaveJson(DefaultConfigPath, general);
    }

    public static void SaveTheme(string theme)
    {
        Directory.CreateDirectory(ConfigDirectory);
        var general = LoadJson(DefaultConfigPath, new GeneralConfig());
        general.Theme = theme;
        SaveJson(DefaultConfigPath, general);
    }

    public static Dictionary<string, bool> LoadAppInstallerGroupExpanded()
    {
        var general = LoadJson(DefaultConfigPath, new GeneralConfig());
        return new Dictionary<string, bool>(general.AppInstallerGroupExpanded, StringComparer.OrdinalIgnoreCase);
    }

    public static void SaveAppInstallerGroupExpanded(IReadOnlyDictionary<string, bool> expanded)
    {
        Directory.CreateDirectory(ConfigDirectory);
        var general = LoadJson(DefaultConfigPath, new GeneralConfig());
        general.AppInstallerGroupExpanded = new Dictionary<string, bool>(expanded, StringComparer.OrdinalIgnoreCase);
        SaveJson(DefaultConfigPath, general);
    }

    public static WinstallerConfig CreateDefaultConfiguration()
    {
        return new WinstallerConfig
        {
            NetworkDrives = new NetworkDrivesConfig
            {
                Enabled = false,
                TimeoutSeconds = 10,
                Drives = []
            },
            Symlinks = new SymlinksConfig
            {
                Enabled = false,
                BaseSymlinkDirectory = BootstrapManager.DataRoot is null ? @"<Placeholder>" : Path.Combine(BootstrapManager.DataDirectory, "Symlinks"),
                ForceKillApps = false,
                Resymlink = true,
                CreateBackupFolders = true,
                RoamingDirectories = [],
                LocalDirectories = [],
                LocalLowDirectories = [],
                IgnoredRoamingDirectories = ["Microsoft", "Temp", "cache", "Cache", "CrashDumps", "D3DSCache", "Packages", "NVIDIA"],
                IgnoredLocalDirectories = ["Microsoft", "Temp", "cache", "Cache", "CrashDumps", "D3DSCache", "Packages", "NVIDIA"],
                IgnoredLocalLowDirectories = ["Microsoft", "Temp", "cache", "Cache", "CrashDumps", "D3DSCache", "Packages", "NVIDIA"],
                SpecialSymlinks = []
            },
            AppInstaller = new AppInstallerConfig
            {
                Enabled = false,
                SetupInfoDirectory = BootstrapManager.DataRoot is null ? @"<Placeholder>" : Path.Combine(BootstrapManager.DataDirectory, "SetupInfo"),
                DefaultTimeoutSeconds = 300,
                InstallerTimeoutSeconds = 1800,
                CustomScripts = [],
                Behaviors = new(StringComparer.OrdinalIgnoreCase),
                DefaultInstalls = []
            },
            Fonts = new FontsConfig
            {
                Enabled = false,
                FontsDirectory = BootstrapManager.DataRoot is null ? @"<Placeholder>" : Path.Combine(BootstrapManager.DataDirectory, "Fonts"),
                IgnoredFonts = []
            },
            ShellFolders = new ShellFoldersConfig
            {
                Enabled = false,
                Folders =
                [
                    new ShellFolderMapping { FolderName = "Desktop", RegistryValue = "Desktop", Path = @"D:\{USERNAME}\Desktop" },
                    new ShellFolderMapping { FolderName = "Downloads", RegistryValue = "{374DE290-123F-4565-9164-39C4925E467B}", Path = @"D:\{USERNAME}\Downloads" },
                    new ShellFolderMapping { FolderName = "Documents", RegistryValue = "Personal", Path = @"D:\{USERNAME}\Documents" },
                    new ShellFolderMapping { FolderName = "Pictures", RegistryValue = "My Pictures", Path = @"D:\{USERNAME}\Pictures" },
                    new ShellFolderMapping { FolderName = "Music", RegistryValue = "My Music", Path = @"Z:\Arimodu\Music" },
                    new ShellFolderMapping { FolderName = "Videos", RegistryValue = "My Video", Path = @"Z:\Videos" }
                ]
            },
            Registry = new RegistryConfig
            {
                Enabled = false,
                FilesToImport = [],
                Modifications = []
            },
            FileCopy = new FileCopyConfig
            {
                Enabled = false,
                Operations = []
            },
            Startup = new StartupConfig
            {
                Enabled = false,
                Programs = [],
                ProcessesToRun = []
            },
            Path = new PathConfig
            {
                Enabled = false,
                Additions = []
            },
            Discord = new DiscordConfig
            {
                Enabled = false,
                InstallDiscord = true,
                InstallVencord = true,
                InstallOpenAsar = true,
                VencordInstallerUrl = "https://github.com/Vencord/Installer/releases/latest/download/VencordInstallerCli.exe",
                DiscordLocation = @"%LOCALAPPDATA%\Discord"
            },
            Spotify = new SpotifyConfig
            {
                Enabled = false,
                InstallSpotify = true,
                InstallSpicetify = true,
                BlockUpdates = true,
                SidebarConfig = "0",
                CustomApps = ["lyrics-plus"]
            },
            AppDataUtility = new AppDataUtilityConfig
            {
                SymlinkBaseDirectory = BootstrapManager.DataRoot is null ? @"<Placeholder>" : Path.Combine(BootstrapManager.DataDirectory, "AppDataSymlinks"),
                ExcludedDirectories = ["Microsoft", "Temp", "cache", "Cache", "CrashDumps", "D3DSCache", "Packages", "NVIDIA"]
            }
        };
    }

    private static void SanitizeSymlinkConfig(SymlinksConfig config)
    {
        RemoveBlankEntries(config.RoamingDirectories);
        RemoveBlankEntries(config.LocalDirectories);
        RemoveBlankEntries(config.LocalLowDirectories);
        RemoveBlankEntries(config.IgnoredRoamingDirectories);
        RemoveBlankEntries(config.IgnoredLocalDirectories);
        RemoveBlankEntries(config.IgnoredLocalLowDirectories);
        SanitizeAppDataList("Roaming", config.RoamingDirectories, config.IgnoredRoamingDirectories);
        SanitizeAppDataList("Local", config.LocalDirectories, config.IgnoredLocalDirectories);
        SanitizeAppDataList("LocalLow", config.LocalLowDirectories, config.IgnoredLocalLowDirectories);
    }

    private static void RemoveBlankEntries(List<string> values)
    {
        values.RemoveAll(string.IsNullOrWhiteSpace);
    }

    private static void SanitizeAppDataList(string section, List<string> active, List<string> ignored)
    {
        for (var i = active.Count - 1; i >= 0; i--)
        {
            var normalized = SymlinkSafetyPolicy.NormalizeRelativePath(active[i]);
            if (!SymlinkSafetyPolicy.IsSafeAppDataRelativePath(section, normalized, out _))
            {
                active.RemoveAt(i);
                if (!ignored.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                    ignored.Add(normalized);
                continue;
            }

            active[i] = normalized;
        }

        for (var i = ignored.Count - 1; i >= 0; i--)
        {
            var normalized = SymlinkSafetyPolicy.NormalizeRelativePath(ignored[i]);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                ignored.RemoveAt(i);
                continue;
            }

            ignored[i] = normalized;
        }
    }
    private static void EnsureSplitConfiguration()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(ModuleConfigDirectory);

        if (File.Exists(DefaultConfigPath))
        {
            return;
        }

        var legacyPath = Path.Combine(ConfigDirectory, LegacyConfigFileName);
        if (File.Exists(legacyPath))
        {
            var migrated = LoadMonolithicConfiguration(legacyPath);
            SaveConfiguration(migrated);
            File.Move(legacyPath, legacyPath + ".bak", overwrite: true);
            return;
        }

        SaveConfiguration(CreateDefaultConfiguration());
    }

    private static WinstallerConfig LoadMonolithicConfiguration(string path)
    {
        if (!File.Exists(path))
        {
            var defaultConfig = CreateDefaultConfiguration();
            SaveMonolithicConfiguration(defaultConfig, path);
            return defaultConfig;
        }

        try
        {
            var json = File.ReadAllText(path);
            var root = JsonNode.Parse(json)?.AsObject();
            if (root?["appInstaller"] is JsonObject appInstaller)
                MigrateAppInstallerJson(appInstaller);
            return JsonSerializer.Deserialize<WinstallerConfig>(root?.ToJsonString() ?? json, JsonOptions) ?? CreateDefaultConfiguration();
        }
        catch
        {
            return CreateDefaultConfiguration();
        }
    }

    private static void SaveMonolithicConfiguration(WinstallerConfig config, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
        SaveJson(path, config);
    }

    private static GeneralConfig CreateDefaultGeneralConfig(WinstallerConfig config)
    {
        var general = new GeneralConfig();
        foreach (var descriptor in ModuleConfigs)
        {
            general.Modules[descriptor.Id] = GetEnabled(descriptor.GetProperty().GetValue(config)!);
        }

        general.Theme = BootstrapManager.Theme;
        return general;
    }

    private static T LoadJson<T>(string path, T fallback)
    {
        if (!File.Exists(path))
        {
            SaveJson(path, fallback);
            return fallback;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static object LoadModuleJson(string path, Type type, object fallback)
    {
        if (!File.Exists(path))
        {
            SaveJson(path, ToJsonWithoutEnabled(fallback));
            return fallback;
        }

        try
        {
            var json = File.ReadAllText(path);
            if (type == typeof(AppInstallerConfig) && JsonNode.Parse(json) is JsonObject appInstaller)
            {
                MigrateAppInstallerJson(appInstaller);
                json = appInstaller.ToJsonString();
            }
            return JsonSerializer.Deserialize(json, type, JsonOptions) ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static void SaveJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);

        var json = JsonSerializer.Serialize(value, JsonOptions);
        try
        {
            if (File.Exists(path) && string.Equals(File.ReadAllText(path), json, StringComparison.Ordinal))
            {
                return;
            }
        }
        catch
        {
        }

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, json);
            if (File.Exists(path))
                File.Replace(temporaryPath, path, $"{path}.bak", ignoreMetadataErrors: true);
            else
                File.Move(temporaryPath, path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static void MigrateAppInstallerJson(JsonObject appInstaller)
    {
        if (appInstaller["manualTimeoutSeconds"] is JsonNode timeout && appInstaller["installerTimeoutSeconds"] is null)
            appInstaller["installerTimeoutSeconds"] = timeout.DeepClone();

        var defaults = appInstaller["defaultInstalls"] as JsonArray ?? [];
        var behaviors = appInstaller["behaviors"] as JsonObject ?? new JsonObject();
        appInstaller["behaviors"] = behaviors;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new JsonArray();
        foreach (var (source, mode) in new[]
        {
            (defaults, (string?)null),
            (appInstaller["preparedInstallers"] as JsonArray, "Prepared"),
            (appInstaller["manualInstalls"] as JsonArray, "Manual")
        })
        {
            if (source is null) continue;
            foreach (var entry in source)
            {
                var id = entry?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
                    merged.Add(id);
                if (!string.IsNullOrWhiteSpace(id) && mode is not null && behaviors[id] is null)
                    behaviors[id] = new JsonObject { ["installMode"] = mode };
            }
        }
        appInstaller["defaultInstalls"] = merged;
        appInstaller.Remove("preparedInstallers");
        appInstaller.Remove("manualInstalls");
        appInstaller.Remove("manualTimeoutSeconds");
        appInstaller.Remove("bulkTimeoutSeconds");
        foreach (var behavior in behaviors.Select(pair => pair.Value).OfType<JsonObject>())
        {
            var mode = behavior["installMode"]?.GetValue<string>();
            behavior["installMode"] = mode?.Equals("Default", StringComparison.OrdinalIgnoreCase) is true ? "Winget" :
                string.IsNullOrWhiteSpace(mode) ? "Auto" : mode;
            if (behavior["git"] is not JsonObject git)
                continue;
            MapLegacyGitValue(git, "editor", "Custom", "CustomEditor");
            MapLegacyGitValue(git, "path", "CmdAndTools", "CmdTools");
            MapLegacyGitValue(git, "ssh", "ExternalPlink", "Plink");
            MapLegacyGitValue(git, "https", "WindowsSecureChannel", "WinSsl");
            MapLegacyGitValue(git, "lineEndings", "LFAlways", "LFOnly");
            MapLegacyGitValue(git, "terminal", "WindowsConsole", "ConHost");
            MapLegacyGitValue(git, "pullBehavior", "FastForwardOnly", "FFOnly");
            if (git["symlinks"] is JsonValue symlinks && symlinks.TryGetValue<bool>(out var enabled))
                git["symlinks"] = enabled ? "Enabled" : "Disabled";
            if (git["defaultBranch"]?.GetValue<string>()?.Equals("InstallerDefault", StringComparison.OrdinalIgnoreCase) is true)
                git["defaultBranch"] = string.Empty;
        }
    }

    private static void MapLegacyGitValue(JsonObject git, string key, string oldValue, string newValue)
    {
        if (git[key]?.GetValue<string>()?.Equals(oldValue, StringComparison.OrdinalIgnoreCase) is true)
            git[key] = newValue;
    }

    private static bool GetEnabled(object config)
    {
        return config.GetType().GetProperty("Enabled")?.GetValue(config) is true;
    }

    private static void SetEnabled(object config, bool enabled)
    {
        config.GetType().GetProperty("Enabled")?.SetValue(config, enabled);
    }

    private static JsonNode ToJsonWithoutEnabled(object config)
    {
        var node = JsonSerializer.SerializeToNode(config, config.GetType(), JsonOptions)
            ?? new JsonObject();

        if (node is JsonObject obj)
        {
            obj.Remove("enabled");
        }

        return node;
    }

    private sealed record ModuleConfigDescriptor(string Id, string FileName, string PropertyName)
    {
        public PropertyInfo GetProperty()
        {
            return typeof(WinstallerConfig).GetProperty(PropertyName)
                ?? throw new InvalidOperationException($"Missing config property {PropertyName}");
        }
    }
}

