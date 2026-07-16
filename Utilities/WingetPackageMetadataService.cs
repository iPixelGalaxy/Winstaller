using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Winstaller.Utilities;

internal sealed record WingetPackageMetadata(
    string? Version,
    Uri? InstallerUrl,
    Uri? IconUrl,
    Uri? Homepage,
    Uri? PublisherUrl);

internal static class WingetPackageMetadataService
{
    private static readonly ConcurrentDictionary<string, Task<WingetPackageMetadata>> Requests = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim MetadataGate = new(4, 4);
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static Task<WingetPackageMetadata> GetAsync(string packageId)
    {
        var normalized = packageId.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? Task.FromResult(Empty())
            : Requests.GetOrAdd(normalized, LoadAsync);
    }

    private static async Task<WingetPackageMetadata> LoadAsync(string packageId)
    {
        var isMicrosoftStorePackage = RecommendedAppCatalog.IsMicrosoftStorePackage(packageId);
        await MetadataGate.WaitAsync();
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = $"show --id \"{EscapeWingetArgument(packageId)}\" --exact --source {(isMicrosoftStorePackage ? "msstore" : "winget")} --accept-source-agreements --disable-interactivity --no-progress",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            var metadata = Empty();
            if (process is not null)
            {
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                var output = await outputTask;
                var error = await errorTask;
                if (process.ExitCode != 0)
                    RunLog.Write("WingetMetadata", $"winget show failed for {packageId}: {string.Join(Environment.NewLine, new[] { output, error }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim()}");
                else
                    metadata = Parse(output);
            }

            if (isMicrosoftStorePackage && metadata.Version is null)
                metadata = metadata with { Version = await GetMicrosoftStoreVersionAsync(packageId) };

            return AddStoreProductUri(metadata, packageId, isMicrosoftStorePackage);
        }
        catch (Exception ex)
        {
            RunLog.WriteException("WingetMetadata", $"Failed reading metadata for {packageId}", ex);
            return CreateFallback(packageId, isMicrosoftStorePackage);
        }
        finally
        {
            MetadataGate.Release();
        }
    }

    private static WingetPackageMetadata Parse(string output)
    {
        string? version = null;
        Uri? installerUrl = null;
        Uri? iconUrl = null;
        Uri? homepage = null;
        Uri? publisherUrl = null;

        foreach (var line in output.Split('\n'))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(value)) continue;

            if (key.Equals("Version", StringComparison.OrdinalIgnoreCase) && !value.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) version ??= value;
            else if (key.Equals("Installer Url", StringComparison.OrdinalIgnoreCase)) installerUrl ??= ToHttpsUri(value);
            else if (key.Equals("Icon", StringComparison.OrdinalIgnoreCase)) iconUrl ??= ToHttpsUri(value);
            else if (key.Equals("Homepage", StringComparison.OrdinalIgnoreCase)) homepage ??= ToHttpsUri(value);
            else if (key.Equals("Publisher Url", StringComparison.OrdinalIgnoreCase)) publisherUrl ??= ToHttpsUri(value);
        }

        return new WingetPackageMetadata(version, installerUrl, iconUrl, homepage, publisherUrl);
    }

    private static Uri? ToHttpsUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;

    private static WingetPackageMetadata CreateFallback(string packageId, bool isMicrosoftStorePackage) =>
        AddStoreProductUri(Empty(), packageId, isMicrosoftStorePackage);

    private static WingetPackageMetadata AddStoreProductUri(WingetPackageMetadata metadata, string packageId, bool isMicrosoftStorePackage)
    {
        if (!isMicrosoftStorePackage)
            return metadata;

        var productUri = new Uri($"ms-windows-store://pdp/?ProductId={Uri.EscapeDataString(packageId)}");
        return metadata with { InstallerUrl = productUri };
    }

    private static async Task<string?> GetMicrosoftStoreVersionAsync(string productId)
    {
        try
        {
            var market = System.Globalization.RegionInfo.CurrentRegion.TwoLetterISORegionName;
            var language = System.Globalization.CultureInfo.CurrentUICulture.Name;
            if (market.Length != 2) market = "US";
            if (string.IsNullOrWhiteSpace(language)) language = "en-US";
            var uri = $"https://displaycatalog.mp.microsoft.com/v7.0/products/{Uri.EscapeDataString(productId)}?market={Uri.EscapeDataString(market)}&languages={Uri.EscapeDataString(language)}";
            using var response = await HttpClient.GetAsync(uri);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
            return GetCompatibleStorePackageVersion(document.RootElement);
        }
        catch (Exception ex)
        {
            RunLog.WriteException("WingetMetadata", $"Microsoft Store catalog lookup failed for {productId}", ex);
            return null;
        }
    }

    private static string? GetCompatibleStorePackageVersion(JsonElement root)
    {
        if (!root.TryGetProperty("Product", out var product) ||
            !product.TryGetProperty("DisplaySkuAvailabilities", out var skus) ||
            skus.ValueKind != JsonValueKind.Array)
            return null;

        var architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => "x64"
        };
        var currentWindowsVersion = PackWindowsVersion(Environment.OSVersion.Version);
        (int Rank, string PackageFullName, string? DependencyBlob)? best = null;

        foreach (var displaySku in skus.EnumerateArray())
        {
            if (!displaySku.TryGetProperty("Sku", out var sku) ||
                !sku.TryGetProperty("Properties", out var properties) ||
                !properties.TryGetProperty("Packages", out var packages) ||
                packages.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var package in packages.EnumerateArray())
            {
                if (!package.TryGetProperty("PackageFullName", out var fullNameElement) ||
                    fullNameElement.GetString() is not { Length: > 0 } fullName ||
                    !SupportsArchitecture(package, architecture) ||
                    !SupportsCurrentWindows(package, currentWindowsVersion))
                    continue;

                var rank = package.TryGetProperty("PackageRank", out var rankElement) && rankElement.TryGetInt32(out var parsedRank)
                    ? parsedRank
                    : 0;
                if (best is not null && best.Value.Rank >= rank) continue;
                best = (rank, fullName, package.TryGetProperty("PlatformDependencyXmlBlob", out var blob) ? blob.GetString() : null);
            }
        }

        if (best is null) return null;
        return GetBundledPackageVersion(best.Value.DependencyBlob, architecture) ?? ExtractPackageVersion(best.Value.PackageFullName);
    }

    private static bool SupportsArchitecture(JsonElement package, string architecture) =>
        package.TryGetProperty("Architectures", out var architectures) && architectures.ValueKind == JsonValueKind.Array &&
        architectures.EnumerateArray().Any(value => string.Equals(value.GetString(), architecture, StringComparison.OrdinalIgnoreCase) || string.Equals(value.GetString(), "neutral", StringComparison.OrdinalIgnoreCase));

    private static bool SupportsCurrentWindows(JsonElement package, ulong currentWindowsVersion)
    {
        if (!package.TryGetProperty("PlatformDependencies", out var dependencies) || dependencies.ValueKind != JsonValueKind.Array) return false;
        return dependencies.EnumerateArray().Any(dependency =>
        {
            if (!dependency.TryGetProperty("PlatformName", out var name) ||
                !("Windows.Desktop".Equals(name.GetString(), StringComparison.OrdinalIgnoreCase) || "Windows.Universal".Equals(name.GetString(), StringComparison.OrdinalIgnoreCase))) return false;
            return !dependency.TryGetProperty("MinVersion", out var minVersion) || minVersion.GetUInt64() <= currentWindowsVersion;
        });
    }

    private static string? GetBundledPackageVersion(string? blob, string architecture)
    {
        if (string.IsNullOrWhiteSpace(blob)) return null;
        try
        {
            using var document = JsonDocument.Parse(blob);
            if (!document.RootElement.TryGetProperty("content.bundledPackages", out var packages) || packages.ValueKind != JsonValueKind.Array) return null;
            var names = packages.EnumerateArray().Select(value => value.GetString()).Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
            var package = names.FirstOrDefault(value => value!.Contains($"_{architecture}__", StringComparison.OrdinalIgnoreCase)) ?? names.FirstOrDefault();
            return ExtractPackageVersion(package);
        }
        catch (JsonException) { return null; }
    }

    private static string? ExtractPackageVersion(string? packageFullName)
    {
        var match = Regex.Match(packageFullName ?? string.Empty, "^[^_]+_(?<version>\\d+(?:\\.\\d+){1,3})_");
        return match.Success ? match.Groups["version"].Value : null;
    }

    private static ulong PackWindowsVersion(Version version) =>
        ((ulong)(ushort)Math.Max(version.Major, 0) << 48) |
        ((ulong)(ushort)Math.Max(version.Minor, 0) << 32) |
        ((ulong)(ushort)Math.Max(version.Build, 0) << 16) |
        (ulong)(ushort)Math.Max(version.Revision, 0);

    private static string EscapeWingetArgument(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static WingetPackageMetadata Empty() => new(null, null, null, null, null);
}
