using System.Runtime.InteropServices;
using System.Text;

namespace Winstaller.Utilities;

internal static class FontPreviewService
{
    private const uint FrPrivate = 0x10;
    private const uint GfriDescription = 1;
    private static readonly HashSet<string> LoadedFonts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Sync = new();

    public static string? GetFontFamily(string fontFile)
    {
        try
        {
            lock (Sync)
            {
                if (!LoadedFonts.Contains(fontFile) && AddFontResourceEx(fontFile, FrPrivate, IntPtr.Zero) == 0)
                    return null;

                LoadedFonts.Add(fontFile);
            }

            uint length = 0;
            GetFontResourceInfo(fontFile, ref length, IntPtr.Zero, GfriDescription);
            if (length == 0)
                return null;

            var buffer = new StringBuilder((int)length);
            return GetFontResourceInfo(fontFile, ref length, buffer, GfriDescription)
                ? buffer.ToString()
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or ExternalException)
        {
            RunLog.WriteException("Fonts", $"Could not load preview font: {fontFile}", ex);
            return null;
        }
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int AddFontResourceEx(string fontFile, uint flags, IntPtr reserved);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetFontResourceInfo(string fontFile, ref uint bufferLength, IntPtr buffer, uint queryType);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetFontResourceInfo(string fontFile, ref uint bufferLength, StringBuilder buffer, uint queryType);
}
