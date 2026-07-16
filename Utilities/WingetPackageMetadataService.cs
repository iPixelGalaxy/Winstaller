using System.Collections.Concurrent;
using System.Diagnostics;

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
            if (process is null) return CreateFallback(packageId, isMicrosoftStorePackage);

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
            {
                RunLog.Write("WingetMetadata", $"winget show failed for {packageId}: {error.Trim()}");
                return CreateFallback(packageId, isMicrosoftStorePackage);
            }

            return AddStoreProductUri(Parse(output), packageId, isMicrosoftStorePackage);
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

    private static string EscapeWingetArgument(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static WingetPackageMetadata Empty() => new(null, null, null, null, null);
}
