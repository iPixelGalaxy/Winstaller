using System.Text.Json;

namespace Winstaller.Configuration;

public static class BootstrapManager
{
    private const string DataDirectoryName = ".winstaller";
    private const string ConfigFileName = "winstaller-config.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static BootstrapSettings? _settings;

    public static string BootstrapDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Winstaller");

    public static string BootstrapPath => Path.Combine(BootstrapDirectory, "bootstrap.json");

    public static string? DataRoot => _settings?.DataRoot;
    public static string Theme => _settings?.Theme ?? "system";

    public static string ConfigDirectory => Path.Combine(RequireDataRoot(), "config");
    public static string DataDirectory => Path.Combine(RequireDataRoot(), "data");
    public static string LogsDirectory => Path.Combine(RequireDataRoot(), "logs");
    public static string CacheDirectory => Path.Combine(RequireDataRoot(), "cache");
    public static string ConfigPath => Path.Combine(ConfigDirectory, ConfigFileName);

    public static bool TryLoad()
    {
        _settings = null;

        if (!File.Exists(BootstrapPath))
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(BootstrapPath);
            var settings = JsonSerializer.Deserialize<BootstrapSettings>(json, JsonOptions);
            if (settings is null || string.IsNullOrWhiteSpace(settings.DataRoot))
            {
                return false;
            }

            settings.DataRoot = NormalizeDataRoot(settings.DataRoot);
            if (!Directory.Exists(settings.DataRoot))
            {
                return false;
            }

            _settings = settings;
            EnsureLayout(settings.DataRoot);
            Save();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static BootstrapSettings Initialize(string driveRoot)
    {
        var root = Path.Combine(Path.GetPathRoot(driveRoot) ?? driveRoot, DataDirectoryName);
        EnsureLayout(root);

        Directory.CreateDirectory(BootstrapDirectory);
        _settings = new BootstrapSettings { DataRoot = root };
        File.WriteAllText(BootstrapPath, JsonSerializer.Serialize(_settings, JsonOptions));

        ImportLegacyConfigIfNeeded();
        return _settings;
    }

    public static void SaveTheme(string theme)
    {
        if (_settings is null)
        {
            return;
        }

        _settings.Theme = theme;
        Save();
    }

    public static void ImportLegacyConfigIfNeeded()
    {
        var legacyPath = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
        if (!File.Exists(ConfigPath) && File.Exists(legacyPath))
        {
            File.Copy(legacyPath, ConfigPath, overwrite: false);
        }
    }

    private static void EnsureLayout(string root)
    {
        Directory.CreateDirectory(root);
        File.SetAttributes(root, File.GetAttributes(root) | FileAttributes.Hidden);
        Directory.CreateDirectory(Path.Combine(root, "config"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        Directory.CreateDirectory(Path.Combine(root, "cache"));
    }

    private static string NormalizeDataRoot(string dataRoot)
    {
        var root = Path.GetPathRoot(dataRoot);
        if (string.IsNullOrWhiteSpace(root))
        {
            return dataRoot;
        }

        return Path.Combine(root, DataDirectoryName);
    }

    private static void Save()
    {
        Directory.CreateDirectory(BootstrapDirectory);
        File.WriteAllText(BootstrapPath, JsonSerializer.Serialize(_settings, JsonOptions));
    }

    private static string RequireDataRoot()
    {
        if (_settings is null || string.IsNullOrWhiteSpace(_settings.DataRoot))
        {
            throw new InvalidOperationException("Winstaller storage is not initialized.");
        }

        return _settings.DataRoot;
    }
}
