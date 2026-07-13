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
    [StructLayout(LayoutKind.Sequential)] private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public short bmPlanes, bmBitsPixel; public IntPtr bmBits; }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public int bmiColors; }
    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr bitmap); }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)] public static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, out IShellItemImageFactory item);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr objectHandle);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int GetObject(IntPtr handle, int size, out BITMAP bitmap);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int GetDIBits(IntPtr hdc, IntPtr bitmap, uint start, uint lines, IntPtr bits, ref BITMAPINFO info, uint usage);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool DeleteDC(IntPtr hdc);
    public static IntPtr GetImage(string path, int size) { var iid = new Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"); IShellItemImageFactory item; SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item); IntPtr bitmap; var result = item.GetImage(new SIZE(size, size), 5, out bitmap); if (result != 0) Marshal.ThrowExceptionForHR(result); return bitmap; }
    public static int GetWidth(IntPtr handle) { BITMAP bitmap; if (GetObject(handle, Marshal.SizeOf<BITMAP>(), out bitmap) == 0) throw new InvalidOperationException("Could not inspect shell bitmap."); return bitmap.bmWidth; }
    public static int GetHeight(IntPtr handle) { BITMAP bitmap; if (GetObject(handle, Marshal.SizeOf<BITMAP>(), out bitmap) == 0) throw new InvalidOperationException("Could not inspect shell bitmap."); return bitmap.bmHeight; }
    public static void CopyPixels(IntPtr handle, int width, int height, IntPtr destination) { var info = new BITMAPINFO { bmiHeader = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 } }; var dc = CreateCompatibleDC(IntPtr.Zero); try { if (dc == IntPtr.Zero || GetDIBits(dc, handle, 0, (uint)height, destination, ref info, 0) == 0) throw new InvalidOperationException("Could not read shell bitmap pixels."); } finally { if (dc != IntPtr.Zero) DeleteDC(dc); } }
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
    else { $bitmapHandle = [WinstallerShellIcon]::GetImage($cleanPath, 256); $image = [Drawing.Bitmap]::new([WinstallerShellIcon]::GetWidth($bitmapHandle), [WinstallerShellIcon]::GetHeight($bitmapHandle), [Drawing.Imaging.PixelFormat]::Format32bppArgb); $pixels = $image.LockBits([Drawing.Rectangle]::new(0, 0, $image.Width, $image.Height), [Drawing.Imaging.ImageLockMode]::WriteOnly, [Drawing.Imaging.PixelFormat]::Format32bppArgb); try { [WinstallerShellIcon]::CopyPixels($bitmapHandle, $image.Width, $image.Height, $pixels.Scan0) } finally { $image.UnlockBits($pixels) } }
    $fileName = ([regex]::Replace($PackageId[0].ToLowerInvariant(), '[^a-z0-9]+', '-').Trim('-')) + '.png'
    $outputPath = Join-Path $pngRoot $fileName
    if ((Test-Path -LiteralPath $outputPath) -and -not $Overwrite) { throw "Icon already exists: $outputPath. Use -Overwrite to replace it." }
    $canvas = [Drawing.Bitmap]::new(256, 256, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($canvas)
    try {
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $left = 0; $top = 0; $right = $image.Width - 1; $bottom = $image.Height - 1
        if ($image -is [Drawing.Bitmap]) {
            $bitmap = [Drawing.Bitmap]$image; $left = $image.Width; $top = $image.Height; $right = -1; $bottom = -1
            for ($y = 0; $y -lt $image.Height; $y++) { for ($x = 0; $x -lt $image.Width; $x++) { if ($bitmap.GetPixel($x, $y).A -gt 8) { $left = [Math]::Min($left, $x); $top = [Math]::Min($top, $y); $right = [Math]::Max($right, $x); $bottom = [Math]::Max($bottom, $y) } } }
            if ($right -lt $left) { $left = 0; $top = 0; $right = $image.Width - 1; $bottom = $image.Height - 1 }
        }
        $sourceWidth = $right - $left + 1; $sourceHeight = $bottom - $top + 1
        $scale = [Math]::Min(224.0 / $sourceWidth, 224.0 / $sourceHeight)
        $width = [Math]::Max(1, [int][Math]::Round($sourceWidth * $scale)); $height = [Math]::Max(1, [int][Math]::Round($sourceHeight * $scale))
        $destination = [Drawing.Rectangle]::new([int]((256 - $width) / 2), [int]((256 - $height) / 2), $width, $height)
        $graphics.DrawImage($image, $destination, $left, $top, $sourceWidth, $sourceHeight, [Drawing.GraphicsUnit]::Pixel)
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