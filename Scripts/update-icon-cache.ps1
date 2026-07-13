[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string[]]$PackageId,
    [Parameter(Mandatory)] [string]$SourcePath,
    [string]$SourceName,
    [switch]$Overwrite
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class WinstallerShellIcon {
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx; public int cy; public SIZE(int x, int y) { cx = x; cy = y; } }
    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr bitmap); }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)] public static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, out IShellItemImageFactory item);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr objectHandle);
    public static IntPtr GetImage(string path, int size) { var iid = new Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"); IShellItemImageFactory item; SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item); IntPtr bitmap; var result = item.GetImage(new SIZE(size, size), 0, out bitmap); if (result != 0) Marshal.ThrowExceptionForHR(result); return bitmap; }
}
"@

$root = Split-Path -Parent $PSScriptRoot
$cacheRoot = Join-Path $root 'Assets\IconCache'
$pngRoot = Join-Path $cacheRoot 'png'
$indexPath = Join-Path $cacheRoot 'index.json'
New-Item -ItemType Directory -Force -Path $pngRoot | Out-Null
$cleanPath = $SourcePath -replace ',\d+$', ''
$isShellPath = $cleanPath.StartsWith('shell:', [StringComparison]::OrdinalIgnoreCase)
if (-not $isShellPath -and -not (Test-Path -LiteralPath $cleanPath -PathType Leaf)) { throw "Source file not found: $cleanPath" }
$image = $null
$bitmapHandle = [IntPtr]::Zero
try {
    if (-not $isShellPath -and [IO.Path]::GetExtension($cleanPath).Equals('.png', [StringComparison]::OrdinalIgnoreCase)) { $image = [Drawing.Image]::FromFile($cleanPath) }
    else { $bitmapHandle = [WinstallerShellIcon]::GetImage($cleanPath, 256); $image = [Drawing.Image]::FromHbitmap($bitmapHandle) }
    $fileName = ([regex]::Replace($PackageId[0].ToLowerInvariant(), '[^a-z0-9]+', '-').Trim('-')) + '.png'
    $outputPath = Join-Path $pngRoot $fileName
    if ((Test-Path -LiteralPath $outputPath) -and -not $Overwrite) { throw "Icon already exists: $outputPath. Use -Overwrite to replace it." }
    $canvas = [Drawing.Bitmap]::new(256, 256, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($canvas)
    try {
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $scale = [Math]::Min(1.0, [Math]::Min(256.0 / $image.Width, 256.0 / $image.Height))
        $width = [Math]::Max(1, [int][Math]::Round($image.Width * $scale)); $height = [Math]::Max(1, [int][Math]::Round($image.Height * $scale))
        $graphics.DrawImage($image, [int]((256 - $width) / 2), [int]((256 - $height) / 2), $width, $height)
        $canvas.Save($outputPath, [Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $canvas.Dispose() }
} finally { if ($image) { $image.Dispose() }; if ($bitmapHandle -ne [IntPtr]::Zero) { [WinstallerShellIcon]::DeleteObject($bitmapHandle) | Out-Null } }
$checksum = (Get-FileHash -Algorithm SHA256 -LiteralPath $outputPath).Hash.ToLowerInvariant()
$manifest = if (Test-Path -LiteralPath $indexPath) { Get-Content -Raw $indexPath | ConvertFrom-Json } else { [pscustomobject]@{ schemaVersion = 1; icons = @() } }
$remaining = @($manifest.icons | Where-Object { $entry = $_; -not @($entry.packageIds | Where-Object { $_ -in $PackageId }).Count })
$entry = [pscustomobject]@{ packageIds = @($PackageId); file = "png/$fileName"; sha256 = $checksum; source = if ($SourceName) { $SourceName } else { [IO.Path]::GetFileName($cleanPath) } }
$manifest = [pscustomobject]@{ schemaVersion = 1; icons = @($remaining + $entry | Sort-Object { $_.packageIds[0] }) }
$manifest | ConvertTo-Json -Depth 5 | Set-Content -NoNewline -Encoding utf8 $indexPath
Write-Host "Updated $($entry.file) for $($PackageId -join ', ')"