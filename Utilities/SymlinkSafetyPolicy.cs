using System.Text.RegularExpressions;

namespace Winstaller.Utilities;

public static partial class SymlinkSafetyPolicy
{
    private static readonly string[] WindowsShellJunctionNames =
    [
        "Application Data",
        "Cookies",
        "History",
        "Local Settings",
        "My Documents",
        "NetHood",
        "PrintHood",
        "Recent",
        "SendTo",
        "Start Menu",
        "Templates",
        "Temporary Internet Files"
    ];

    public static bool IsSafeAppDataRelativePath(string section, string relativePath, out string reason)
    {
        reason = string.Empty;
        var normalized = NormalizeRelativePath(relativePath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            reason = "Blank path.";
            return false;
        }

        if (Path.IsPathRooted(normalized))
        {
            reason = "Rooted paths are not allowed.";
            return false;
        }

        var parts = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Any(part => part is "." or ".."))
        {
            reason = "Relative traversal is not allowed.";
            return false;
        }

        if (parts.Any(part => WindowsShellJunctionNames.Contains(part, StringComparer.OrdinalIgnoreCase)))
        {
            reason = "Windows profile shell junction.";
            return false;
        }

        if (IsCriticalProfilePath(section, parts))
        {
            reason = "Windows profile infrastructure.";
            return false;
        }

        return true;
    }

    public static bool IsSafeAppDataPath(string rootPath, string path, string section, out string reason)
    {
        reason = string.Empty;
        try
        {
            var fullRoot = Path.GetFullPath(rootPath);
            var fullPath = Path.GetFullPath(path);
            var relativePath = Path.GetRelativePath(fullRoot, fullPath);

            if (relativePath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
            {
                reason = "Path is outside AppData root.";
                return false;
            }

            return IsSafeAppDataRelativePath(section, relativePath, out reason);
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    public static bool IsWindowsShellJunction(DirectoryInfo info)
    {
        return WindowsShellJunctionNames.Contains(info.Name, StringComparer.OrdinalIgnoreCase) &&
               info.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    public static string NormalizeRelativePath(string value)
    {
        return (value ?? string.Empty)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Trim()
            .Trim(Path.DirectorySeparatorChar);
    }

    private static bool IsCriticalProfilePath(string section, IReadOnlyList<string> parts)
    {
        if (parts.Count == 0)
            return true;

        if (section.Equals("Roaming", StringComparison.OrdinalIgnoreCase))
        {
            return StartsWith(parts, "Microsoft", "Windows", "Start Menu") ||
                   StartsWith(parts, "Microsoft", "Internet Explorer", "Quick Launch");
        }

        if (section.Equals("Local", StringComparison.OrdinalIgnoreCase))
        {
            return StartsWith(parts, "Microsoft", "Windows") ||
                   StartsWith(parts, "Microsoft", "WindowsApps") ||
                   IsCriticalLocalPackage(parts);
        }

        return false;
    }

    private static bool IsCriticalLocalPackage(IReadOnlyList<string> parts)
    {
        if (parts.Count < 2 || !parts[0].Equals("Packages", StringComparison.OrdinalIgnoreCase))
            return false;

        return CriticalPackageRegex().IsMatch(parts[1]);
    }

    private static bool StartsWith(IReadOnlyList<string> parts, params string[] prefix)
    {
        if (parts.Count < prefix.Length)
            return false;

        for (var i = 0; i < prefix.Length; i++)
        {
            if (!parts[i].Equals(prefix[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    [GeneratedRegex(@"^(Microsoft\.Windows\.StartMenuExperienceHost_|Microsoft\.Windows\.ShellExperienceHost_|MicrosoftWindows\.Client\.CBS_|Microsoft\.Windows\.Search_|Microsoft\.DesktopAppInstaller_|Microsoft\.WindowsStore_)", RegexOptions.IgnoreCase)]
    private static partial Regex CriticalPackageRegex();
}
