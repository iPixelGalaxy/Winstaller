namespace Winstaller.Utilities;

internal sealed record RecommendedApp(string PackageId, string Name, string Description, bool IsStore = false, bool IsBundledHevc = false);

internal static class RecommendedAppCatalog
{
    public const string HevcPackageId = "9NMZLZ57R3T7";

    public static readonly IReadOnlyList<RecommendedApp> Apps =
    [
        new("Git.Git", "Git", "Distributed version control"),
        new("Discord.Discord", "Discord", "Chat and voice"),
        new("Spotify.Spotify", "Spotify", "Music streaming"),
        new("Nilesoft.Shell", "Nilesoft Shell", "Custom Windows context menu"),
        new("Ditto.Ditto", "Ditto", "Clipboard manager"),
        new("9NBLGGH4Z1JC", "Speedtest", "Internet speed testing", true),
        new("9WZDNCRFHVN5", "Calculator", "Microsoft Calculator", true),
        new("9MZ95KL8MR0L", "Snipping Tool", "Screen capture", true),
        new("9MSMLRH6LZF3", "Notepad", "Microsoft Notepad", true),
        new("9PCFS5B6T72H", "Paint", "Microsoft Paint", true),
        new(HevcPackageId, "HEVC Video Extensions", "Bundled installer, then Microsoft Store upgrade", true, true),
        new("9N95Q1ZZPMH4", "MPEG-2 Video Extensions", "Microsoft Store codec", true),
        new("9MVZQVXJBQ9V", "AV1 Video Extension", "Microsoft Store codec", true),
        new("9N4D0MSMP0PT", "VP9 Video Extensions", "Microsoft Store codec", true),
        new("9PB0TRCNRHFX", "AVC Encoder Video Extension", "Microsoft Store codec", true),
        new("9PG2DK419DRG", "WebP Image Extensions", "Microsoft Store codec", true),
        new("9PMMSR1CGPWG", "HEIF Image Extensions", "Microsoft Store codec", true),
        new("Microsoft.WindowsTerminal", "Windows Terminal", "Modern command-line terminal"),
        new("JanDeDobbeleer.OhMyPosh", "Oh My Posh", "Shell prompt theme engine"),
        new("File-New-Project.EarTrumpet", "EarTrumpet", "Per-app volume control"),
        new("voidtools.Everything.Alpha", "Everything Alpha", "Fast file search"),
        new("Microsoft.DotNet.DesktopRuntime.6", "Runtime 6", "Windows desktop runtime"),
        new("Microsoft.DotNet.DesktopRuntime.7", "Runtime 7", "Windows desktop runtime"),
        new("Microsoft.DotNet.DesktopRuntime.8", "Runtime 8", "Windows desktop runtime"),
        new("Microsoft.DotNet.DesktopRuntime.9", "Runtime 9", "Windows desktop runtime"),
        new("Microsoft.DotNet.DesktopRuntime.10", "Runtime 10", "Windows desktop runtime"),
        new("Microsoft.VCRedist.2005.x86", "Visual C++ 2005 x86", "Microsoft runtime"),
        new("Microsoft.VCRedist.2005.x64", "Visual C++ 2005 x64", "Microsoft runtime"),
        new("Microsoft.VCRedist.2008.x86", "Visual C++ 2008 x86", "Microsoft runtime"),
        new("Microsoft.VCRedist.2008.x64", "Visual C++ 2008 x64", "Microsoft runtime"),
        new("Microsoft.VCRedist.2010.x86", "Visual C++ 2010 x86", "Microsoft runtime"),
        new("Microsoft.VCRedist.2010.x64", "Visual C++ 2010 x64", "Microsoft runtime"),
        new("Microsoft.VCRedist.2012.x86", "Visual C++ 2012 x86", "Microsoft runtime"),
        new("Microsoft.VCRedist.2012.x64", "Visual C++ 2012 x64", "Microsoft runtime"),
        new("Microsoft.VCRedist.2013.x86", "Visual C++ 2013 x86", "Microsoft runtime"),
        new("Microsoft.VCRedist.2013.x64", "Visual C++ 2013 x64", "Microsoft runtime"),
        new("Microsoft.VCRedist.2015+.x86", "Visual C++ 2015-2022 x86", "Microsoft runtime"),
        new("Microsoft.VCRedist.2015+.x64", "Visual C++ 2015-2022 x64", "Microsoft runtime"),
        new("RARLab.WinRAR", "WinRAR", "Archive manager"),
        new("Microsoft.PowerShell", "PowerShell", "Cross-platform shell"),
        new("GitHub.GitHubDesktop", "GitHub Desktop", "Git desktop client"),
        new("Microsoft.VisualStudioCode", "Visual Studio Code", "Code editor"),
        new("Notepad++.Notepad++", "Notepad++", "Text editor"),
        new("Microsoft.PowerToys", "PowerToys", "Windows productivity tools"),
        new("VideoLAN.VLC", "VLC media player", "Media player"),
        new("Unity.UnityHub", "Unity Hub", "Unity project manager"),
        new("KeePassXCTeam.KeePassXC", "KeePassXC", "Password manager"),
        new("7zip.7zip", "7-Zip", "Archive utility"),
        new("aria2.aria2", "aria2", "Download utility"),
        new("DuongDieuPhap.ImageGlass", "ImageGlass", "Image viewer"),
        new("ThioJoe.SvgThumbnailExtension", "SVG Thumbnail Extension", "Explorer SVG previews"),
        new("WinFsp.WinFsp", "WinFsp", "File-system proxy framework")
    ];

    public static bool IsMicrosoftStorePackage(string packageId) =>
        Apps.FirstOrDefault(app => app.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase))?.IsStore == true;

    public static string GetImportDisplayName(string packageId, string detectedName) =>
        TryGetRuntimeDisplayName(packageId, out var runtimeName) ? runtimeName : detectedName;

    public static string NormalizeExistingDisplayName(string packageId, string displayName)
    {
        if (!TryGetRuntimeDisplayName(packageId, out var runtimeName)) return displayName;
        var normalized = displayName.Trim();
        return normalized.Equals(packageId, StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("desktop runtime", StringComparison.OrdinalIgnoreCase)
            ? runtimeName
            : displayName;
    }

    public static string? GetKnownDisplayName(string packageId)
    {
        if (TryGetRuntimeDisplayName(packageId, out var runtimeName)) return runtimeName;
        return Apps.FirstOrDefault(app => app.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase))?.Name;
    }

    private static bool TryGetRuntimeDisplayName(string packageId, out string displayName)
    {
        const string prefix = "Microsoft.DotNet.DesktopRuntime.";
        if (packageId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(packageId[prefix.Length..], out var major))
        {
            displayName = $"Runtime {major}";
            return true;
        }
        displayName = string.Empty;
        return false;
    }
}