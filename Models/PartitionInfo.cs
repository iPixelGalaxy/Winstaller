namespace Winstaller.Models;

/// <summary>
/// Information about a disk partition
/// </summary>
public class PartitionInfo
{
    public int PartitionNumber { get; set; }
    public string Label { get; set; } = string.Empty;
    public string FileSystem { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public long UsedBytes { get; set; }
    public char? DriveLetter { get; set; }
    public string SizeFormatted => FormatSize(SizeBytes);
    public string UsedFormatted => FormatSize(UsedBytes);

    private static string FormatSize(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        int order = 0;
        double size = bytes;

        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return $"{size:0.##} {sizes[order]}";
    }
}
