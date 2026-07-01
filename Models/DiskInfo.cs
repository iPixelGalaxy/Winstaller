namespace Winstaller.Models;

/// <summary>
/// Media type enumeration for physical disks
/// </summary>
public enum MediaType
{
    Unknown,
    HDD,
    SSD,
    NVMe,
    USB
}

/// <summary>
/// Information about a physical disk
/// </summary>
public class DiskInfo
{
    public int DiskNumber { get; set; }
    public string Model { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string SizeFormatted => FormatSize(SizeBytes);
    public MediaType MediaType { get; set; } = MediaType.Unknown;
    public string PartitionStyle { get; set; } = "Unknown"; // GPT, MBR, RAW
    public bool IsInitialized { get; set; }
    public bool HasPartitions { get; set; }
    public List<PartitionInfo> Partitions { get; set; } = [];

    public bool IsUnformatted => !IsInitialized || !HasPartitions;

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

    public override string ToString()
    {
        var typeStr = MediaType switch
        {
            MediaType.NVMe => "NVMe",
            MediaType.SSD => "SSD",
            MediaType.HDD => "HDD",
            MediaType.USB => "USB",
            _ => "???"
        };

        var status = IsUnformatted ? "[UNFORMATTED]" : $"[{PartitionStyle}]";
        return $"Disk {DiskNumber}: {SizeFormatted,-10} {typeStr,-5} {status,-13} {Manufacturer} {Model}".Trim();
    }
}
