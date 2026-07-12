using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Winstaller.Configuration;

namespace Winstaller.Utilities;

internal static partial class AppIconService
{
    private const int MaxIconBytes = 2 * 1024 * 1024;
    private const int MaxHtmlBytes = 512 * 1024;
    private const int MinimumIconPixels = 64;
    private const int PreferredIconPixels = 128;
    private const double MinIconRatio = 0.75;
    private const double MaxIconRatio = 1.33;
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly ConcurrentDictionary<string, Task<string?>> Requests = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim MetadataGate = new(4, 4);
    private static readonly SemaphoreSlim HttpGate = new(8, 8);

    private sealed record IconCandidate(Uri Uri, int Priority, string Source);
    private sealed record IconAsset(byte[] Bytes, string Extension, int Width, int Height, IconCandidate Candidate);

    public static Task<string?> GetIconPathAsync(string packageId)
    {
        var normalized = packageId.Trim();
        return string.IsNullOrWhiteSpace(normalized) || BootstrapManager.DataRoot is null
            ? Task.FromResult<string?>(null)
            : Requests.GetOrAdd(normalized, FetchAndCacheAsync);
    }

    public static void Invalidate(string packageId, string path)
    {
        Requests.TryRemove(packageId.Trim(), out _);
        try { File.Delete(path); } catch { }
    }

    private static async Task<string?> FetchAndCacheAsync(string packageId)
    {
        var directory = Path.Combine(BootstrapManager.CacheDirectory, "app-icons");
        Directory.CreateDirectory(directory);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(packageId))).ToLowerInvariant();
        var missPath = Path.Combine(directory, key + ".miss-v2");
        var cachedPath = GetCachedIconPath(directory, key);
        if (cachedPath is not null) return cachedPath;
        if (TryMigrateV2Icon(directory, key, out var migratedPath)) return migratedPath;
        if (File.Exists(missPath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(missPath) < TimeSpan.FromHours(1)) return null;

        try
        {
            var candidates = await GetCandidateUrlsAsync(packageId);
            var assets = await Task.WhenAll(candidates.Select(DownloadIconAsync));
            IconAsset? selected = null;
            foreach (var asset in assets)
            {
                if (asset is null) continue;
                if (!IsUsableIcon(asset))
                {
                    RunLog.Write("AppIcon", $"Rejected {packageId} {asset.Candidate.Source}: {asset.Width}x{asset.Height}");
                    continue;
                }

                if (selected is null || IsBetter(asset, selected)) selected = asset;
            }

            if (selected is not null)
            {
                var iconPath = Path.Combine(directory, key + selected.Extension);
                await File.WriteAllBytesAsync(iconPath, selected.Bytes);
                if (File.Exists(missPath)) File.Delete(missPath);
                RunLog.Write("AppIcon", $"Selected {packageId} {selected.Candidate.Source}: {selected.Width}x{selected.Height}");
                return iconPath;
            }
        }
        catch (Exception ex)
        {
            RunLog.WriteException("AppIcon", $"Lookup failed for {packageId}", ex);
        }

        try { File.WriteAllText(missPath, DateTime.UtcNow.ToString("O")); } catch { }
        RunLog.Write("AppIcon", $"No usable official icon for {packageId}");
        return null;
    }

    private static bool IsBetter(IconAsset candidate, IconAsset current)
    {
        var candidatePreferred = IsPreferredQuality(candidate);
        var currentPreferred = IsPreferredQuality(current);
        if (candidatePreferred != currentPreferred) return candidatePreferred;
        var candidateArea = candidate.Width * candidate.Height;
        var currentArea = current.Width * current.Height;
        if (candidateArea != currentArea) return candidateArea > currentArea;
        return candidate.Candidate.Priority < current.Candidate.Priority;
    }

    private static bool IsPreferredQuality(IconAsset asset) =>
        asset.Extension == ".svg" || (asset.Width >= PreferredIconPixels && asset.Height >= PreferredIconPixels);

    private static bool IsUsableIcon(IconAsset asset)
    {
        if (asset.Extension == ".svg")
            return asset.Width == 0 || asset.Height == 0 || IsSquare(asset.Width, asset.Height);
        return asset.Width >= MinimumIconPixels && asset.Height >= MinimumIconPixels && IsSquare(asset.Width, asset.Height);
    }

    private static bool IsSquare(int width, int height)
    {
        if (width == 0 || height == 0) return false;
        var ratio = width / (double)height;
        return ratio >= MinIconRatio && ratio <= MaxIconRatio;
    }

    private static async Task<List<IconCandidate>> GetCandidateUrlsAsync(string packageId)
    {
        var metadata = await GetMetadataAsync(packageId);
        var candidates = new List<IconCandidate>();
        AddCandidate(candidates, metadata.IconUrl, 0, "winget icon");
        var pages = new List<(Uri? Page, string Source)>
        {
            (metadata.Homepage, metadata.Homepage?.Host ?? "site"),
            (metadata.PublisherUrl, metadata.PublisherUrl?.Host ?? "site")
        };
        if (RecommendedAppCatalog.IsMicrosoftStorePackage(packageId))
            pages.Add((new Uri($"https://apps.microsoft.com/detail/{packageId}"), "Microsoft Store"));
        var discoveries = await Task.WhenAll(pages.Select(page => DiscoverIconCandidatesAsync(page.Page, page.Source)));
        foreach (var (page, source) in pages)
            AddCandidate(candidates, GetFaviconUrl(page), 5, "site favicon");
        candidates.AddRange(discoveries.SelectMany(discovery => discovery));

        return candidates
            .GroupBy(candidate => candidate.Uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(candidate => candidate.Priority).First())
            .OrderBy(candidate => candidate.Priority)
            .ToList();
    }

    private static void AddCandidate(List<IconCandidate> candidates, Uri? uri, int priority, string source)
    {
        if (uri?.Scheme == Uri.UriSchemeHttps) candidates.Add(new IconCandidate(uri, priority, source));
    }

    private static async Task<IEnumerable<IconCandidate>> DiscoverIconCandidatesAsync(Uri? page, string source)
    {
        if (page?.Scheme != Uri.UriSchemeHttps) return [];
        var html = await DownloadTextAsync(page);
        if (html is null) return [];

        var candidates = new List<IconCandidate>();
        var manifests = new List<Uri>();
        foreach (Match tag in HtmlTagRegex().Matches(html))
        {
            var value = tag.Value;
            var rel = ReadAttribute(value, "rel");
            var property = ReadAttribute(value, "property");
            var href = ReadAttribute(value, "href") ?? ReadAttribute(value, "content");
            if (rel?.Contains("manifest", StringComparison.OrdinalIgnoreCase) == true && Uri.TryCreate(page, href, out var manifest) && manifest.Scheme == Uri.UriSchemeHttps)
            {
                manifests.Add(manifest);
                continue;
            }
            var sourceUri = href;
            if (value.StartsWith("<img", StringComparison.OrdinalIgnoreCase))
            {
                var descriptor = $"{ReadAttribute(value, "alt")} {ReadAttribute(value, "class")} {ReadAttribute(value, "id")} {ReadAttribute(value, "src")}";
                if (descriptor.Contains("icon", StringComparison.OrdinalIgnoreCase) || descriptor.Contains("logo", StringComparison.OrdinalIgnoreCase)) sourceUri = ReadAttribute(value, "src");
                else continue;
            }
            if (string.IsNullOrWhiteSpace(sourceUri) || !Uri.TryCreate(page, sourceUri, out var uri) || uri.Scheme != Uri.UriSchemeHttps) continue;
            if (rel?.Contains("icon", StringComparison.OrdinalIgnoreCase) == true)
                AddCandidate(candidates, uri, rel.Contains("apple", StringComparison.OrdinalIgnoreCase) ? 3 : 2, $"{source} {rel}");
            else if (value.StartsWith("<img", StringComparison.OrdinalIgnoreCase))
                AddCandidate(candidates, uri, 3, $"{source} logo image");
            else if (string.Equals(property, "og:image", StringComparison.OrdinalIgnoreCase))
                AddCandidate(candidates, uri, 4, $"{source} Open Graph");
        }
        var manifestCandidates = await Task.WhenAll(manifests.Distinct().Select(manifest => DiscoverManifestCandidatesAsync(manifest, source)));
        candidates.AddRange(manifestCandidates.SelectMany(result => result));
        return candidates;
    }

    private static async Task<IEnumerable<IconCandidate>> DiscoverManifestCandidatesAsync(Uri manifest, string source)
    {
        var text = await DownloadTextAsync(manifest);
        if (text is null) return [];
        var candidates = new List<IconCandidate>();
        foreach (Match match in ManifestSourceRegex().Matches(text))
        {
            if (Uri.TryCreate(manifest, match.Groups["value"].Value, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                AddCandidate(candidates, uri, 2, $"{source} web manifest");
        }
        return candidates;
    }
    private static string? ReadAttribute(string tag, string name)
    {
        var match = Regex.Match(tag, $"\\b{name}\\s*=\\s*['\"](?<value>[^'\"]+)['\"]", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static async Task<string?> DownloadTextAsync(Uri uri)
    {
        await HttpGate.WaitAsync();
        try
        {
            using var response = await HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps || response.Content.Headers.ContentLength is > MaxHtmlBytes) return null;
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 81920, false);
            var buffer = new char[81920];
            var builder = new StringBuilder();
            while (builder.Length < MaxHtmlBytes)
            {
                var read = await reader.ReadAsync(buffer);
                if (read == 0) break;
                builder.Append(buffer, 0, read);
            }
            return builder.ToString();
        }
        catch { return null; }
        finally { HttpGate.Release(); }
    }
    private static async Task<IconAsset?> DownloadIconAsync(IconCandidate candidate)
    {
        await HttpGate.WaitAsync();
        try
        {
            using var response = await HttpClient.GetAsync(candidate.Uri, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps || response.Content.Headers.ContentLength is > MaxIconBytes) return null;
            await using var input = await response.Content.ReadAsStreamAsync();
            await using var output = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                var read = await input.ReadAsync(buffer);
                if (read == 0) break;
                if (output.Length + read > MaxIconBytes) return null;
                await output.WriteAsync(buffer.AsMemory(0, read));
            }
            var bytes = output.ToArray();
            if (!TryGetImageInfo(bytes, out var extension, out var width, out var height)) return null;
            return new IconAsset(bytes, extension, width, height, candidate);
        }
        catch { return null; }
        finally { HttpGate.Release(); }
    }
    private static bool TryGetImageInfo(byte[] bytes, out string extension, out int width, out int height)
    {
        extension = string.Empty;
        width = height = 0;
        if (bytes.Length >= 24 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            extension = ".png";
            width = ReadBigEndian(bytes, 16);
            height = ReadBigEndian(bytes, 20);
            return true;
        }
        if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1 && bytes[3] == 0)
        {
            extension = ".ico";
            for (var index = 6; index + 15 < bytes.Length; index += 16)
            {
                var iconWidth = bytes[index] == 0 ? 256 : bytes[index];
                var iconHeight = bytes[index + 1] == 0 ? 256 : bytes[index + 1];
                if (iconWidth * iconHeight > width * height) { width = iconWidth; height = iconHeight; }
            }
            return width > 0;
        }
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            extension = ".jpg";
            return TryGetJpegDimensions(bytes, out width, out height);
        }
        var text = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 8192));
        if (!text.TrimStart().StartsWith("<svg", StringComparison.OrdinalIgnoreCase) && !text.Contains("<svg", StringComparison.OrdinalIgnoreCase)) return false;
        extension = ".svg";
        var match = SvgViewBoxRegex().Match(text);
        if (match.Success)
        {
            width = (int)double.Parse(match.Groups["width"].Value, System.Globalization.CultureInfo.InvariantCulture);
            height = (int)double.Parse(match.Groups["height"].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        return true;
    }

    private static int ReadBigEndian(byte[] bytes, int offset) => (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];

    private static bool TryGetJpegDimensions(byte[] bytes, out int width, out int height)
    {
        width = height = 0;
        for (var index = 2; index + 8 < bytes.Length; index++)
        {
            if (bytes[index] != 0xFF || bytes[index + 1] == 0xFF) continue;
            var marker = bytes[index + 1];
            if (marker is not (>= 0xC0 and <= 0xC3 or >= 0xC5 and <= 0xC7 or >= 0xC9 and <= 0xCB or >= 0xCD and <= 0xCF)) continue;
            height = (bytes[index + 5] << 8) | bytes[index + 6];
            width = (bytes[index + 7] << 8) | bytes[index + 8];
            return width > 0 && height > 0;
        }
        return false;
    }

    private static string? GetCachedIconPath(string directory, string key)
    {
        foreach (var extension in new[] { ".png", ".jpg", ".ico", ".svg" })
        {
            var path = Path.Combine(directory, key + extension);
            if (!File.Exists(path)) continue;
            try
            {
                var bytes = File.ReadAllBytes(path);
                if (TryGetImageInfo(bytes, out var detectedExtension, out var width, out var height) &&
                    IsUsableIcon(new IconAsset(bytes, detectedExtension, width, height, new IconCandidate(new Uri("https://localhost"), 0, "cache")))) return path;
                File.Delete(path);
            }
            catch { }
        }
        return null;
    }

    private static bool TryMigrateV2Icon(string destinationDirectory, string key, out string? migratedPath)
    {
        migratedPath = null;
        var sourceDirectory = Path.Combine(BootstrapManager.CacheDirectory, "app-icons-v2");
        foreach (var extension in new[] { ".png", ".jpg", ".ico", ".img" })
        {
            var sourcePath = Path.Combine(sourceDirectory, key + extension);
            if (!File.Exists(sourcePath)) continue;
            try
            {
                var bytes = File.ReadAllBytes(sourcePath);
                if (!TryGetImageInfo(bytes, out var detectedExtension, out var width, out var height) || !IsUsableIcon(new IconAsset(bytes, detectedExtension, width, height, new IconCandidate(new Uri("https://localhost"), 0, "v2 cache")))) continue;
                migratedPath = Path.Combine(destinationDirectory, key + detectedExtension);
                File.WriteAllBytes(migratedPath, bytes);
                return true;
            }
            catch { }
        }
        return false;
    }

    private static async Task<(Uri? IconUrl, Uri? Homepage, Uri? PublisherUrl)> GetMetadataAsync(string packageId)
    {
        await MetadataGate.WaitAsync();
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = $"show --id \"{packageId}\" --exact --source {(RecommendedAppCatalog.IsMicrosoftStorePackage(packageId) ? "msstore" : "winget")} --accept-source-agreements --disable-interactivity --no-progress",
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            });
            if (process is null) return default;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); return default; }
            await Task.WhenAll(output, error);
            if (process.ExitCode != 0) return default;
            Uri? icon = null; Uri? homepage = null; Uri? publisher = null;
            foreach (var line in output.Result.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = line.IndexOf(':');
                if (separator <= 0) continue;
                var key = line[..separator].Trim(); var value = line[(separator + 1)..].Trim();
                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) continue;
                if (key.Equals("Icon", StringComparison.OrdinalIgnoreCase)) icon ??= uri;
                else if (key.Equals("Homepage", StringComparison.OrdinalIgnoreCase)) homepage ??= uri;
                else if (key.Equals("Publisher Url", StringComparison.OrdinalIgnoreCase)) publisher ??= uri;
            }
            return (icon, homepage, publisher);
        }
        catch { return default; }
        finally { MetadataGate.Release(); }
    }
    private static Uri? GetFaviconUrl(Uri? site) => site?.Scheme == Uri.UriSchemeHttps
        ? new UriBuilder(site) { Path = "/favicon.ico", Query = string.Empty, Fragment = string.Empty }.Uri : null;

    [GeneratedRegex("<(?:link|meta|img)\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex("[\"']src[\"']\\s*:\\s*[\"'](?<value>[^\"']+)[\"']", RegexOptions.IgnoreCase)]
    private static partial Regex ManifestSourceRegex();
    [GeneratedRegex("viewBox\\s*=\\s*['\"][^'\"]*?\\s+(?<width>[0-9.]+)\\s+(?<height>[0-9.]+)['\"]", RegexOptions.IgnoreCase)]
    private static partial Regex SvgViewBoxRegex();
}