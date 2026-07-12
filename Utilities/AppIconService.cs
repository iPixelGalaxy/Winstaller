using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Winstaller.Configuration;

namespace Winstaller.Utilities;

internal static class AppIconService
{
    private const int MaxIconBytes = 2 * 1024 * 1024;
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly ConcurrentDictionary<string, Task<string?>> Requests = new(StringComparer.OrdinalIgnoreCase);

    public static Task<string?> GetIconPathAsync(string packageId)
    {
        var normalized = packageId.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || BootstrapManager.DataRoot is null)
            return Task.FromResult<string?>(null);

        return Requests.GetOrAdd(normalized, FetchAndCacheAsync);
    }

    private static async Task<string?> FetchAndCacheAsync(string packageId)
    {
        var directory = Path.Combine(BootstrapManager.CacheDirectory, "app-icons");
        Directory.CreateDirectory(directory);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(packageId))).ToLowerInvariant();
        var iconPath = Path.Combine(directory, key + ".img");
        var missPath = Path.Combine(directory, key + ".miss");
        if (File.Exists(iconPath))
            return iconPath;
        if (File.Exists(missPath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(missPath) < TimeSpan.FromHours(24))
            return null;

        try
        {
            var metadata = await GetMetadataAsync(packageId);
            var source = metadata.IconUrl ?? GetFaviconUrl(metadata.Homepage) ?? GetFaviconUrl(metadata.PublisherUrl);
            if (source is null)
                return MarkMiss(missPath);

            var bytes = await DownloadAsync(source);
            if (bytes is null)
                return MarkMiss(missPath);

            await File.WriteAllBytesAsync(iconPath, bytes);
            if (File.Exists(missPath))
                File.Delete(missPath);
            return iconPath;
        }
        catch
        {
            return MarkMiss(missPath);
        }
    }

    private static string? MarkMiss(string missPath)
    {
        File.WriteAllText(missPath, DateTime.UtcNow.ToString("O"));
        return null;
    }

    private static async Task<(Uri? IconUrl, Uri? Homepage, Uri? PublisherUrl)> GetMetadataAsync(string packageId)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "winget",
            Arguments = $"show --id \"{packageId}\" --exact --source winget --accept-source-agreements --disable-interactivity --no-progress",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        });
        if (process is null)
            return default;

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            return default;
        }
        await Task.WhenAll(output, error);
        if (process.ExitCode != 0)
            return default;

        Uri? icon = null;
        Uri? homepage = null;
        Uri? publisher = null;
        foreach (var line in output.Result.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
                continue;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                continue;
            if (key.Equals("Icon", StringComparison.OrdinalIgnoreCase)) icon ??= uri;
            else if (key.Equals("Homepage", StringComparison.OrdinalIgnoreCase)) homepage ??= uri;
            else if (key.Equals("Publisher Url", StringComparison.OrdinalIgnoreCase)) publisher ??= uri;
        }
        return (icon, homepage, publisher);
    }

    private static Uri? GetFaviconUrl(Uri? site)
    {
        if (site is null || site.Scheme != Uri.UriSchemeHttps)
            return null;
        var builder = new UriBuilder(site) { Path = "/favicon.ico", Query = string.Empty, Fragment = string.Empty };
        return builder.Uri;
    }

    private static async Task<byte[]?> DownloadAsync(Uri uri)
    {
        using var response = await HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
            return null;
        if (response.Content.Headers.ContentLength is > MaxIconBytes)
            return null;

        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer);
            if (read == 0)
                break;
            if (output.Length + read > MaxIconBytes)
                return null;
            await output.WriteAsync(buffer.AsMemory(0, read));
        }
        return output.Length == 0 ? null : output.ToArray();
    }
}