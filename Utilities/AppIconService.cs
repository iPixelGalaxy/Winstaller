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
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly ConcurrentDictionary<string, Task<string?>> Requests = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim NetworkGate = new(3, 3);

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
        var directory = Path.Combine(BootstrapManager.CacheDirectory, "app-icons-v2");
        Directory.CreateDirectory(directory);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(packageId))).ToLowerInvariant();
        var missPath = Path.Combine(directory, key + ".miss");
        var cachedPath = GetCachedIconPath(directory, key);
        if (cachedPath is not null) return cachedPath;
        if (File.Exists(missPath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(missPath) < TimeSpan.FromHours(24)) return null;

        try
        {
            await NetworkGate.WaitAsync();
            try
            {
                foreach (var source in await GetCandidateUrlsAsync(packageId))
                {
                    var bytes = await DownloadImageAsync(source);
                    if (bytes is null) continue;
                    var extension = GetImageExtension(bytes)!;
                    var iconPath = Path.Combine(directory, key + extension);
                    await File.WriteAllBytesAsync(iconPath, bytes);
                    if (File.Exists(missPath)) File.Delete(missPath);
                    return iconPath;
                }
            }
            finally { NetworkGate.Release(); }
        }
        catch { }

        try { File.WriteAllText(missPath, DateTime.UtcNow.ToString("O")); } catch { }
        return null;
    }

    private static async Task<List<Uri>> GetCandidateUrlsAsync(string packageId)
    {
        var metadata = await GetMetadataAsync(packageId);
        var candidates = new List<Uri>();
        AddUrl(candidates, metadata.IconUrl);
        if (RecommendedAppCatalog.IsMicrosoftStorePackage(packageId))
        {
            var storePage = new Uri($"https://apps.microsoft.com/detail/{packageId}");
            AddUrl(candidates, await DiscoverIconUrlAsync(storePage));
        }
        foreach (var page in new[] { metadata.Homepage, metadata.PublisherUrl })
        {
            AddUrl(candidates, await DiscoverIconUrlAsync(page));
            AddUrl(candidates, GetFaviconUrl(page));
        }
        return candidates.DistinctBy(uri => uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddUrl(List<Uri> candidates, Uri? uri)
    {
        if (uri?.Scheme == Uri.UriSchemeHttps) candidates.Add(uri);
    }

    private static async Task<Uri?> DiscoverIconUrlAsync(Uri? page)
    {
        if (page?.Scheme != Uri.UriSchemeHttps) return null;
        var html = await DownloadTextAsync(page);
        if (html is null) return null;
        foreach (Match tag in HtmlTagRegex().Matches(html))
        {
            var value = tag.Value;
            var href = ReadAttribute(value, "href") ?? ReadAttribute(value, "content");
            if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(page, href, out var icon) || icon.Scheme != Uri.UriSchemeHttps) continue;
            var rel = ReadAttribute(value, "rel");
            var property = ReadAttribute(value, "property");
            if ((rel?.Contains("icon", StringComparison.OrdinalIgnoreCase) ?? false) ||
                string.Equals(property, "og:image", StringComparison.OrdinalIgnoreCase)) return icon;
        }
        return null;
    }

    private static string? ReadAttribute(string tag, string name)
    {
        var match = Regex.Match(tag, $"\\b{name}\\s*=\\s*['\"](?<value>[^'\"]+)['\"]", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static async Task<string?> DownloadTextAsync(Uri uri)
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

    private static async Task<byte[]?> DownloadImageAsync(Uri uri)
    {
        using var response = await HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
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
        return LooksLikeImage(bytes) ? bytes : null;
    }

    private static bool LooksLikeImage(byte[] bytes) =>
        bytes.Length >= 4 &&
        ((bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) ||
         (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) ||
         (bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1 && bytes[3] == 0));

    private static string? GetCachedIconPath(string directory, string key)
    {
        foreach (var extension in new[] { ".png", ".jpg", ".ico" })
        {
            var path = Path.Combine(directory, key + extension);
            if (File.Exists(path)) return path;
        }

        var legacyPath = Path.Combine(directory, key + ".img");
        if (!File.Exists(legacyPath)) return null;
        try
        {
            var extension = GetImageExtension(File.ReadAllBytes(legacyPath));
            if (extension is null)
            {
                File.Delete(legacyPath);
                return null;
            }

            var migratedPath = Path.Combine(directory, key + extension);
            File.Move(legacyPath, migratedPath, true);
            return migratedPath;
        }
        catch { return null; }
    }

    private static string? GetImageExtension(byte[] bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return ".png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ".jpg";
        return bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1 && bytes[3] == 0 ? ".ico" : null;
    }

    private static async Task<(Uri? IconUrl, Uri? Homepage, Uri? PublisherUrl)> GetMetadataAsync(string packageId)
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

    private static Uri? GetFaviconUrl(Uri? site) => site?.Scheme == Uri.UriSchemeHttps
        ? new UriBuilder(site) { Path = "/favicon.ico", Query = string.Empty, Fragment = string.Empty }.Uri : null;

    [GeneratedRegex("<(?:link|meta)\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlTagRegex();
}