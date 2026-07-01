using System.Diagnostics;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using Winstaller.Configuration;
using Winstaller.Models;

namespace Winstaller.Utilities;

/// <summary>
/// Utility for installing Windows from WIM/ESD images
/// </summary>
public class WindowsInstallerUtility(WinstallerConfig config)
{
    private readonly WinstallerConfig _config = config;
    private readonly bool _isWinPE = DetectWinPE();

    // WinPE known paths
    private const string WinPE_WimPath = @"%:\sources\install.wim";
    private const string WinPE_EsdPath = @"%:\sources\install.esd";
    private const string WinPE_AutounattendPath = @"%:\autounattend.xml";

    /// <summary>
    /// Detect if running in WinPE environment (WMI is not available)
    /// </summary>
    private static bool DetectWinPE()
    {
        Logger.Debug("Detecting WinPE environment...");

        // WinPE runs from X: drive and has minimal Windows installation
        // Check for WinPE-specific indicators
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? "";
        Logger.Debug($"SystemRoot: {systemRoot}");

        // WinPE typically runs from X:\Windows
        if (systemRoot.StartsWith("X:", StringComparison.OrdinalIgnoreCase))
        {
            Logger.Debug("WinPE detected: SystemRoot starts with X:");
            return true;
        }

        // Check for WinPE registry marker or missing WMI
        if (File.Exists(@"X:\Windows\System32\winpeshl.ini"))
        {
            Logger.Debug("WinPE detected: winpeshl.ini found");
            return true;
        }

        // Check if System.Management can initialize (quick test)
        try
        {
            Logger.Debug("Testing WMI availability...");
            // This will throw TypeInitializationException if WMI is not available
            _ = new ManagementObjectSearcher("SELECT * FROM Win32_OperatingSystem");
            Logger.Debug("WMI available - not in WinPE");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Debug($"WMI not available: {ex.Message}");
            return true; // WMI not available, treat as WinPE-like environment
        }
    }

    /// <summary>
    /// Run the interactive Windows installer (normal mode)
    /// </summary>
    public async Task ShowMenuAsync()
    {
        while (true)
        {
            Console.Clear();
            ConsoleHelper.WriteHeader("Windows Installer Utility");
            Console.WriteLine("Install Windows from WIM/ESD image\n");

            Console.WriteLine("  [1] Full Installation (Interactive)");
            Console.WriteLine("  [2] List Available Disks");
            Console.WriteLine("  [3] View Disk Details");
            Console.WriteLine("\n  [0] Back to Main Menu");

            Console.Write("\nSelect option: ");
            var input = Console.ReadLine()?.Trim();

            switch (input)
            {
                case "1":
                    await RunInteractiveInstallAsync();
                    break;
                case "2":
                    await ListDisksAsync();
                    Console.WriteLine("\nPress any key to continue...");
                    Console.ReadKey(true);
                    break;
                case "3":
                    await ViewDiskDetailsAsync();
                    break;
                case "0":
                case "":
                    return;
            }
        }
    }

    /// <summary>
    /// Run the WinPE automated flow
    /// </summary>
    public async Task RunWinPEFlowAsync(bool noUnformatted = false)
    {
        Logger.Debug("Starting WinPE flow");
        Logger.Debug($"noUnformatted flag: {noUnformatted}");
        Logger.Debug($"IsWinPE detected: {_isWinPE}");

        Console.Clear();
        Logger.WriteHeader("WinPE Windows Installation");
        Console.WriteLine("Automated installation from Windows PE environment\n");

        // Step 1: Find the WIM file
        Logger.Debug("Step 1: Searching for WIM/ESD image...");
        var wimPath = FindWinPEImage();
        if (string.IsNullOrEmpty(wimPath))
        {
            Logger.Error("Could not find install.wim or install.esd in X:\\sources\\");
            Console.WriteLine("Please ensure you're running from WinPE with the installation media mounted.");
            return;
        }

        Logger.Success($"Found image: {wimPath}");
        Logger.Debug($"Image path: {wimPath}");

        // Step 2: Check for autounattend.xml
        Logger.Debug("Step 2: Checking for autounattend.xml...");
        string? autounattendPath = FindAutounattend();
        if (!string.IsNullOrEmpty(autounattendPath))
        {
            Logger.Success($"Found autounattend: {autounattendPath}");
        }
        else
        {
            Logger.Warning("No autounattend.xml found.");
        }

        Console.WriteLine();

        // Step 3: Get disk information
        Logger.Debug("Step 3: Scanning disks...");
        Console.WriteLine("Scanning disks...\n");
        var drives = await GetDriveInfoAsync();

        Logger.Debug($"Found {drives.Count} disk(s)");
        foreach (var d in drives)
        {
            Logger.Debug($"  Disk {d.DiskNumber}: {d.Model} ({d.SizeFormatted}) - {d.PartitionStyle}");
        }

        if (drives.Count == 0)
        {
            Logger.Error("No disks found!");
            return;
        }

        // Step 4: Select target disk
        Logger.Debug("Step 4: Selecting target disk...");
        int selectedDisk = await SelectDiskWithTimeoutAsync(drives, noUnformatted);

        if (selectedDisk < 0)
        {
            Logger.Error("No disk selected. Installation cancelled.");
            return;
        }

        var targetDrive = drives.FirstOrDefault(d => d.DiskNumber == selectedDisk);
        Logger.Debug($"Selected disk: {selectedDisk}");
        Console.WriteLine($"\nSelected: {targetDrive}");

        // Step 5: Confirm installation
        Console.WriteLine();
        Logger.Warning($"WARNING: ALL DATA ON DISK {selectedDisk} WILL BE PERMANENTLY DELETED!");

        Console.Write("\nType 'YES' to proceed: ");
        var confirm = Console.ReadLine()?.Trim();

        if (confirm != "YES")
        {
            Logger.Debug("User cancelled installation");
            Console.WriteLine("\nInstallation cancelled.");
            return;
        }

        // Step 6: Perform installation
        Logger.Debug("Step 6: Starting installation...");
        await PerformInstallationAsync(selectedDisk, wimPath, autounattendPath);
    }

    private static string? FindAutounattend()
    {
        Logger.Debug("Searching for Autounattend...");

        foreach (var letter in Enumerable.Range('D', 'Z' - 'D' + 1).Select(i => (char)i).ToArray())
        {
            Logger.Debug($"Checking drive {letter}:");

            var unattendPath = WinPE_AutounattendPath.Replace('%', letter);
            if (File.Exists(unattendPath))
            {
                Logger.Debug($"Found Autounattend: {unattendPath}");
                return unattendPath;
            }
        }

        Logger.Debug("No Autounattend found on any drive");
        return null;
    }

    private static string? FindWinPEImage()
    {
        Logger.Debug("Searching for Windows installation image...");

        foreach (var letter in Enumerable.Range('D', 'Z' - 'D' + 1).Select(i => (char)i).ToArray())
        {
            Logger.Debug($"Checking drive {letter}:");

            var wimPath = WinPE_WimPath.Replace('%', letter);
            if (File.Exists(wimPath))
            {
                Logger.Debug($"Found WIM: {wimPath}");
                return wimPath;
            }

            var esdPath = WinPE_EsdPath.Replace('%', letter);
            if (File.Exists(esdPath))
            {
                Logger.Debug($"Found ESD: {esdPath}");
                return esdPath;
            }

            // Also check for split WIM
            var swmPath = $@"{letter}:\sources\install.swm";
            if (File.Exists(swmPath))
            {
                Logger.Debug($"Found split WIM: {swmPath}");
                return swmPath;
            }
        }

        Logger.Debug("No installation image found on any drive");
        return null;
    }

    private async Task<int> SelectDiskWithTimeoutAsync(List<DiskInfo> drives, bool noUnformatted)
    {
        // Find unformatted drives
        var unformattedDrives = drives.Where(d => d.IsUnformatted).ToList();

        // Auto-select single unformatted drive if conditions are met
        if (!noUnformatted && unformattedDrives.Count == 1)
        {
            var autoDrive = unformattedDrives[0];
            Console.WriteLine($"Found single unformatted drive: {autoDrive}");
            Console.WriteLine();

            // 5-second timeout - any key cancels auto-select
            Console.WriteLine("Auto-selecting in 5 seconds... Press any key to choose manually.");

            var autoSelected = await WaitWithTimeoutAsync(5000);

            if (autoSelected)
            {
                Console.WriteLine($"\nAuto-selected Disk {autoDrive.DiskNumber}");
                return autoDrive.DiskNumber;
            }
        }

        // Manual selection
        return await SelectDiskManuallyAsync(drives);
    }

    private async Task<bool> WaitWithTimeoutAsync(int milliseconds)
    {
        var cts = new CancellationTokenSource();
        var startTime = DateTime.Now;

        var keyTask = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                if (Console.KeyAvailable)
                {
                    Console.ReadKey(true);
                    return false;
                }
                Thread.Sleep(50);
            }
            return true;
        }, cts.Token);

        var delayTask = Task.Delay(milliseconds, cts.Token);

        // Show countdown
        for (int i = milliseconds / 1000; i > 0; i--)
        {
            if (keyTask.IsCompleted)
                break;

            Console.Write($"\r{i}... ");
            await Task.Delay(1000);
        }

        await cts.CancelAsync();

        try
        {
            await keyTask;
        }
        catch (OperationCanceledException) { }

        Console.WriteLine();

        // If key was pressed, keyTask returns false
        return keyTask.IsCompletedSuccessfully && keyTask.Result;
    }

    private Task<int> SelectDiskManuallyAsync(List<DiskInfo> drives)
    {
        Console.WriteLine("\nAvailable disks:\n");

        foreach (var drive in drives)
        {
            var prefix = drive.IsUnformatted ? "* " : "  ";
            Console.WriteLine($"{prefix}{drive}");

            if (drive.Partitions.Count > 0)
            {
                foreach (var part in drive.Partitions.Take(3))
                {
                    var letter = part.DriveLetter.HasValue ? $"({part.DriveLetter}:)" : "    ";
                    Console.WriteLine($"      Part {part.PartitionNumber}: {part.SizeFormatted,-10} {part.FileSystem,-6} {letter} {part.Label}");
                }
                if (drive.Partitions.Count > 3)
                {
                    Console.WriteLine($"      ... and {drive.Partitions.Count - 3} more partitions");
                }
            }
        }

        Console.WriteLine("\n* = Unformatted/Uninitialized drive");

        while (true)
        {
            Console.Write("\nEnter disk number (or 'q' to cancel): ");
            var input = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(input) || input.ToLower() == "q")
                return Task.FromResult(-1);

            if (int.TryParse(input, out int diskNum))
            {
                if (drives.Any(d => d.DiskNumber == diskNum))
                    return Task.FromResult(diskNum);

                Console.WriteLine($"Disk {diskNum} not found.");
            }
            else
            {
                Console.WriteLine("Invalid input. Please enter a disk number.");
            }
        }
    }

    private async Task RunInteractiveInstallAsync()
    {
        Console.Clear();
        ConsoleHelper.WriteHeader("Interactive Windows Installation");

        // Check for admin
        if (!AdminHelper.IsAdministrator())
        {
            ConsoleHelper.WriteError("This operation requires administrator privileges.");
            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey(true);
            return;
        }

        // Step 1: Get image path
        Console.WriteLine("\nSTEP 1: Locate Windows Installation Image");
        Console.WriteLine("=========================================");
        Console.WriteLine("Enter the full path to your Windows installation image.");
        Console.WriteLine("Supported formats: install.wim, install.esd, install.swm\n");

        string? imagePath = null;
        while (string.IsNullOrEmpty(imagePath))
        {
            Console.Write("Image path (or 'q' to cancel): ");
            var input = Console.ReadLine()?.Trim().Trim('"');

            if (input?.ToLower() == "q")
                return;

            if (!string.IsNullOrEmpty(input) && File.Exists(input))
            {
                imagePath = input;
            }
            else
            {
                ConsoleHelper.WriteError("File not found. Please check the path.");
            }
        }

        ConsoleHelper.WriteSuccess($"Found: {imagePath}");

        // Step 2: Get autounattend path (optional)
        Console.WriteLine("\n\nSTEP 2: Locate autounattend.xml (Optional)");
        Console.WriteLine("==========================================");
        Console.WriteLine("Enter path to autounattend.xml, or press Enter to skip.\n");

        Console.Write("Autounattend path: ");
        var xmlInput = Console.ReadLine()?.Trim().Trim('"');
        string? autounattendPath = null;

        if (!string.IsNullOrEmpty(xmlInput))
        {
            if (File.Exists(xmlInput))
            {
                autounattendPath = xmlInput;
                ConsoleHelper.WriteSuccess($"Found: {autounattendPath}");
            }
            else
            {
                ConsoleHelper.WriteWarning("File not found. Continuing without autounattend.xml");
            }
        }
        else
        {
            Console.WriteLine("Skipping autounattend.xml");
        }

        // Step 3: Select disk
        Console.WriteLine("\n\nSTEP 3: Select Target Disk");
        Console.WriteLine("==========================\n");

        var drives = await GetDriveInfoAsync();
        int selectedDisk = await SelectDiskManuallyAsync(drives);

        if (selectedDisk < 0)
        {
            Console.WriteLine("\nInstallation cancelled.");
            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey(true);
            return;
        }

        // Step 4: Final confirmation
        Console.Clear();
        ConsoleHelper.WriteHeader("Final Confirmation");

        var targetDrive = drives.FirstOrDefault(d => d.DiskNumber == selectedDisk);
        Console.WriteLine($"\nImage:        {imagePath}");
        Console.WriteLine($"Autounattend: {autounattendPath ?? "[NONE]"}");
        Console.WriteLine($"Target Disk:  {targetDrive}");

        Console.WriteLine();
        ConsoleHelper.WriteWarning("WARNING: ALL DATA ON THE SELECTED DISK WILL BE PERMANENTLY DELETED!");

        Console.Write("\nType 'YES' to proceed: ");
        var confirm = Console.ReadLine()?.Trim();

        if (confirm != "YES")
        {
            Console.WriteLine("\nInstallation cancelled.");
            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey(true);
            return;
        }

        await PerformInstallationAsync(selectedDisk, imagePath, autounattendPath);

        Console.WriteLine("\nPress any key to continue...");
        Console.ReadKey(true);
    }

    private async Task PerformInstallationAsync(int diskNumber, string imagePath, string? autounattendPath)
    {
        Console.Clear();
        ConsoleHelper.WriteHeader("Installing Windows");

        try
        {
            // Phase 1: Partition disk
            Console.WriteLine("\n[PHASE 1] Partitioning disk...");
            var partitionResult = await PartitionDiskAsync(diskNumber);
            if (!partitionResult)
            {
                ConsoleHelper.WriteError("Partitioning failed!");
                return;
            }
            ConsoleHelper.WriteSuccess("Disk partitioned (S: = EFI, W: = Windows)");

            // Phase 2: Apply image
            Console.WriteLine("\n[PHASE 2] Applying Windows image...");
            Console.WriteLine("This may take 10-30 minutes depending on disk speed.\n");
            var applyResult = await ApplyImageAsync(imagePath);
            if (!applyResult)
            {
                ConsoleHelper.WriteError("Image application failed!");
                return;
            }
            ConsoleHelper.WriteSuccess("Windows image applied");

            // Phase 3: Configure boot loader
            Console.WriteLine("\n[PHASE 3] Configuring boot loader...");
            var bootResult = await ConfigureBootLoaderAsync();
            if (!bootResult)
            {
                ConsoleHelper.WriteError("Boot loader configuration failed!");
                return;
            }
            ConsoleHelper.WriteSuccess("Boot loader configured");

            // Phase 4: Copy autounattend if provided
            if (!string.IsNullOrEmpty(autounattendPath))
            {
                Console.WriteLine("\n[PHASE 4] Setting up autounattend.xml...");
                await SetupAutounattendAsync(autounattendPath);
            }

            // Phase 5: Optimize filesystem
            Console.WriteLine("\n[PHASE 5] Optimizing filesystem...");
            await OptimizeFilesystemAsync();
            ConsoleHelper.WriteSuccess("Filesystem optimized");

            // Phase 6: Set regional configuration
            Console.WriteLine("\n[PHASE 6] Setting device region...");
            await SetDeviceRegionAsync();
            ConsoleHelper.WriteSuccess("Device region configured");

            // Phase 7: Cleanup - remove drive letters
            Console.WriteLine("\n[PHASE 7] Cleaning up...");
            await CleanupDriveLettersAsync(diskNumber);
            ConsoleHelper.WriteSuccess("Cleanup complete");

            // Done!
            Console.WriteLine();
            ConsoleHelper.WriteHeader("Installation Complete!");

            Console.WriteLine("\nNext steps:");
            Console.WriteLine("  1. Remove any installation media");
            Console.WriteLine("  2. Set boot order in BIOS/UEFI if needed");
            Console.WriteLine("  3. Reboot to complete Windows setup");

            if (!string.IsNullOrEmpty(autounattendPath))
            {
                Console.WriteLine("\n  Your autounattend.xml will run automatically on first boot.");
            }
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Installation failed: {ex.Message}");
        }
    }

    public async Task<List<DiskInfo>> GetDriveInfoAsync()
    {
        var drives = new List<DiskInfo>();

        Logger.Debug("Getting drive information...");
        Logger.Debug($"WinPE mode: {_isWinPE}");

        // In WinPE, WMI is not available - use diskpart directly
        if (_isWinPE)
        {
            Logger.Debug("Using diskpart fallback (WinPE mode)");
            return await GetDriveInfoFromDiskpartAsync();
        }

        try
        {
            Logger.Debug("Querying WMI for disk information...");
            // Use WMI to get disk information
            using var diskSearcher = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive");

            foreach (ManagementObject disk in diskSearcher.Get())
            {
                var driveInfo = new DiskInfo
                {
                    DiskNumber = ExtractDiskNumber(disk["DeviceID"]?.ToString() ?? ""),
                    Model = disk["Model"]?.ToString()?.Trim() ?? "Unknown",
                    Manufacturer = disk["Manufacturer"]?.ToString()?.Trim() ?? "",
                    SerialNumber = disk["SerialNumber"]?.ToString()?.Trim() ?? "",
                    SizeBytes = Convert.ToInt64(disk["Size"] ?? 0),
                    MediaType = DetermineMediaType(disk)
                };

                Logger.Debug($"Found disk {driveInfo.DiskNumber}: {driveInfo.Model}");

                // Clean up manufacturer (often redundant with model)
                if (string.IsNullOrEmpty(driveInfo.Manufacturer) ||
                    driveInfo.Manufacturer.Equals("(Standard disk drives)", StringComparison.OrdinalIgnoreCase))
                {
                    driveInfo.Manufacturer = ExtractManufacturer(driveInfo.Model);
                }

                // Get partition information
                await GetPartitionInfoAsync(driveInfo);

                drives.Add(driveInfo);
            }

            Logger.Debug($"WMI query complete. Found {drives.Count} disk(s)");
        }
        catch (Exception ex)
        {
            Logger.Warning($"WMI query failed: {ex.Message}");
            Logger.Debug($"WMI exception details: {ex.GetType().Name}");

            // Fallback to diskpart
            Logger.Debug("Falling back to diskpart...");
            drives = await GetDriveInfoFromDiskpartAsync();
        }

        return drives.OrderBy(d => d.DiskNumber).ToList();
    }

    private int ExtractDiskNumber(string deviceId)
    {
        // DeviceID is like \\.\PHYSICALDRIVE0
        var match = Regex.Match(deviceId, @"\d+$");
        return match.Success ? int.Parse(match.Value) : -1;
    }

    private MediaType DetermineMediaType(ManagementObject disk)
    {
        var diskMediaType = disk["MediaType"]?.ToString() ?? "";
        var model = disk["Model"]?.ToString()?.ToUpperInvariant() ?? "";
        var interfaceType = disk["InterfaceType"]?.ToString()?.ToUpperInvariant() ?? "";

        // Check for NVMe
        if (model.Contains("NVME") || interfaceType.Contains("NVME"))
            return MediaType.NVMe;

        // Check for USB
        if (interfaceType.Contains("USB"))
            return MediaType.USB;

        // Check for SSD indicators in model name
        if (model.Contains("SSD") || model.Contains("SOLID STATE"))
            return MediaType.SSD;

        // Try to determine from media type or other properties
        if (diskMediaType.Contains("SSD") || diskMediaType.Contains("Solid"))
            return MediaType.SSD;

        // Try WMI MSFT_PhysicalDisk for more accurate info
        try
        {
            using var physicalDiskSearcher = new ManagementObjectSearcher(
                @"\\.\root\microsoft\windows\storage",
                $"SELECT MediaType FROM MSFT_PhysicalDisk WHERE DeviceId = '{ExtractDiskNumber(disk["DeviceID"]?.ToString() ?? "")}'");

            foreach (ManagementObject physicalDisk in physicalDiskSearcher.Get())
            {
                var msftMediaType = Convert.ToInt32(physicalDisk["MediaType"]);
                // 3 = HDD, 4 = SSD, 5 = SCM
                return msftMediaType switch
                {
                    3 => MediaType.HDD,
                    4 => MediaType.SSD,
                    5 => MediaType.SSD, // SCM is similar to SSD
                    _ => MediaType.Unknown
                };
            }
        }
        catch { }

        // Default to HDD for spinning disks
        return MediaType.HDD;
    }

    private string ExtractManufacturer(string model)
    {
        // Common manufacturer prefixes
        string[] manufacturers = ["Samsung", "WDC", "Western Digital", "Seagate", "Toshiba",
            "Kingston", "SanDisk", "Crucial", "Intel", "Micron", "SK hynix", "ADATA",
            "PNY", "Corsair", "Transcend", "HGST", "Hitachi", "Maxtor", "LaCie"];

        foreach (var mfr in manufacturers)
        {
            if (model.StartsWith(mfr, StringComparison.OrdinalIgnoreCase))
                return mfr;
        }

        // Try first word
        var firstWord = model.Split(' ', '_', '-')[0];
        if (firstWord.Length >= 2 && firstWord.Length <= 15)
            return firstWord;

        return "";
    }

    private async Task GetPartitionInfoAsync(DiskInfo drive)
    {
        try
        {
            // Get partition style (GPT/MBR)
            using var partStyleSearcher = new ManagementObjectSearcher(
                $"SELECT * FROM Win32_DiskPartition WHERE DiskIndex = {drive.DiskNumber}");

            var partitions = partStyleSearcher.Get().Cast<ManagementObject>().ToList();

            if (partitions.Count == 0)
            {
                drive.HasPartitions = false;
                drive.IsInitialized = false;
                drive.PartitionStyle = "RAW";
                return;
            }

            drive.HasPartitions = true;
            drive.IsInitialized = true;

            // Determine partition style from first partition type
            var firstPartType = partitions[0]["Type"]?.ToString() ?? "";
            drive.PartitionStyle = firstPartType.Contains("GPT") ? "GPT" : "MBR";

            // Get partition details
            foreach (ManagementObject partition in partitions)
            {
                var partInfo = new PartitionInfo
                {
                    PartitionNumber = Convert.ToInt32(partition["Index"]) + 1,
                    SizeBytes = Convert.ToInt64(partition["Size"])
                };

                // Get associated logical disk (if any)
                var partPath = partition["DeviceID"]?.ToString();
                if (!string.IsNullOrEmpty(partPath))
                {
                    using var logicalSearcher = new ManagementObjectSearcher(
                        $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partPath}'}} WHERE AssocClass=Win32_LogicalDiskToPartition");

                    foreach (ManagementObject logical in logicalSearcher.Get())
                    {
                        partInfo.DriveLetter = logical["DeviceID"]?.ToString()?[0];
                        partInfo.FileSystem = logical["FileSystem"]?.ToString() ?? "";
                        partInfo.Label = logical["VolumeName"]?.ToString() ?? "";
                        partInfo.UsedBytes = Convert.ToInt64(logical["Size"] ?? 0) -
                                            Convert.ToInt64(logical["FreeSpace"] ?? 0);
                    }
                }

                drive.Partitions.Add(partInfo);
            }
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteWarning($"Failed to get partition info for disk {drive.DiskNumber}: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    private async Task<List<DiskInfo>> GetDriveInfoFromDiskpartAsync()
    {
        Logger.Debug("Getting drive info from diskpart...");
        var drives = new List<DiskInfo>();
        var scriptPath = Path.Combine(Path.GetTempPath(), "diskpart_script.txt");

        try
        {
            // Step 1: Get basic disk list with GPT info
            Logger.Debug("Running diskpart 'list disk' command...");
            await File.WriteAllTextAsync(scriptPath, "list disk");
            var listOutput = await RunProcessAsync("diskpart", $"/s \"{scriptPath}\"");
            Logger.Debug($"Diskpart output length: {listOutput.Length} chars");

            // Parse diskpart "list disk" output
            // Format: "  Disk 0    Online       238 GB  1024 KB        *"
            //         Columns: Disk#, Status, Size, Free, Dyn, Gpt
            var lines = listOutput.Split('\n');
            foreach (var line in lines)
            {
                // Match disk line: captures disk number, size, unit, and checks for GPT marker
                var match = Regex.Match(line, @"Disk\s+(\d+)\s+\w+\s+(\d+)\s*(GB|MB|TB|KB)\s+(\d+\s*(?:GB|MB|TB|KB|B))?\s*(\*)?\s*(\*)?");
                if (match.Success)
                {
                    var diskNum = int.Parse(match.Groups[1].Value);
                    var size = long.Parse(match.Groups[2].Value);
                    var unit = match.Groups[3].Value;
                    // Groups 5 and 6 are the Dyn and Gpt columns (marked with *)
                    var hasGptMarker = line.TrimEnd().EndsWith("*");

                    Logger.Debug($"Parsed disk {diskNum}: {size} {unit}, GPT={hasGptMarker}");

                    size *= unit switch
                    {
                        "TB" => 1024L * 1024 * 1024 * 1024,
                        "GB" => 1024L * 1024 * 1024,
                        "MB" => 1024L * 1024,
                        "KB" => 1024L,
                        _ => 1
                    };

                    drives.Add(new DiskInfo
                    {
                        DiskNumber = diskNum,
                        SizeBytes = size,
                        PartitionStyle = hasGptMarker ? "GPT" : "MBR",
                        IsInitialized = true // Will be updated below if no partitions
                    });
                }
            }

            Logger.Debug($"Found {drives.Count} disk(s) from diskpart");

            // Step 2: Get detailed info for each disk (model, partitions)
            foreach (var drive in drives)
            {
                Logger.Debug($"Getting details for disk {drive.DiskNumber}...");
                await GetDiskDetailFromDiskpartAsync(drive, scriptPath);
            }
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }

        Logger.Debug("Diskpart query complete");
        return drives.OrderBy(d => d.DiskNumber).ToList();
    }

    private async Task GetDiskDetailFromDiskpartAsync(DiskInfo drive, string scriptPath)
    {
        try
        {
            // Get disk detail (model, serial, etc.) and partition list
            var script = $@"select disk {drive.DiskNumber}
detail disk
list partition";
            await File.WriteAllTextAsync(scriptPath, script);
            var output = await RunProcessAsync("diskpart", $"/s \"{scriptPath}\"");

            // Parse model from detail disk output
            // Look for lines like "Samsung SSD 980 PRO 1TB" or the disk ID line
            var lines = output.Split('\n');
            var inDetailSection = false;
            var foundPartitions = false;

            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();

                // Detect when we're in the detail disk section (after "Disk ID:")
                if (trimmedLine.StartsWith("Disk ID:", StringComparison.OrdinalIgnoreCase))
                {
                    inDetailSection = true;
                    continue;
                }

                // The model/product name appears on the line right after "Type   :" in detail disk
                // Or we can look for known patterns
                if (inDetailSection && !string.IsNullOrEmpty(trimmedLine) &&
                    !trimmedLine.StartsWith("Type", StringComparison.OrdinalIgnoreCase) &&
                    !trimmedLine.StartsWith("Status", StringComparison.OrdinalIgnoreCase) &&
                    !trimmedLine.StartsWith("Path", StringComparison.OrdinalIgnoreCase) &&
                    !trimmedLine.StartsWith("Target", StringComparison.OrdinalIgnoreCase) &&
                    !trimmedLine.StartsWith("LUN ID", StringComparison.OrdinalIgnoreCase) &&
                    !trimmedLine.StartsWith("Location", StringComparison.OrdinalIgnoreCase) &&
                    !trimmedLine.StartsWith("Current Read-only", StringComparison.OrdinalIgnoreCase) &&
                    !trimmedLine.StartsWith("Read-only", StringComparison.OrdinalIgnoreCase) &&
                    !trimmedLine.StartsWith("Boot Disk", StringComparison.OrdinalIgnoreCase) &&
                    !trimmedLine.StartsWith("Pagefile", StringComparison.OrdinalIgnoreCase) &&
                    !trimmedLine.StartsWith("Hibernation", StringComparison.OrdinalIgnoreCase) &&
                    !trimmedLine.StartsWith("Crashdump", StringComparison.OrdinalIgnoreCase) &&
                    !trimmedLine.StartsWith("Clustered", StringComparison.OrdinalIgnoreCase) &&
                    !trimmedLine.StartsWith("Volume", StringComparison.OrdinalIgnoreCase) &&
                    !trimmedLine.StartsWith("Partition", StringComparison.OrdinalIgnoreCase) &&
                    !trimmedLine.StartsWith("---", StringComparison.OrdinalIgnoreCase) &&
                    !trimmedLine.StartsWith("There are no", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrEmpty(drive.Model))
                {
                    // This is likely the disk model name
                    drive.Model = trimmedLine;
                    drive.Manufacturer = ExtractManufacturer(trimmedLine);
                    drive.MediaType = DetermineMediaTypeFromModel(trimmedLine);
                    inDetailSection = false;
                }

                // Parse partition list
                // Format: "  Partition 1    Primary           100 MB"
                var partMatch = Regex.Match(trimmedLine, @"Partition\s+(\d+)\s+(\w+)\s+(\d+)\s*(GB|MB|TB|KB)");
                if (partMatch.Success)
                {
                    foundPartitions = true;
                    var partSize = long.Parse(partMatch.Groups[3].Value);
                    var partUnit = partMatch.Groups[4].Value;
                    partSize *= partUnit switch
                    {
                        "TB" => 1024L * 1024 * 1024 * 1024,
                        "GB" => 1024L * 1024 * 1024,
                        "MB" => 1024L * 1024,
                        "KB" => 1024L,
                        _ => 1
                    };

                    drive.Partitions.Add(new PartitionInfo
                    {
                        PartitionNumber = int.Parse(partMatch.Groups[1].Value),
                        SizeBytes = partSize
                    });
                }

                // Check for "There are no partitions on this disk"
                if (trimmedLine.Contains("no partitions", StringComparison.OrdinalIgnoreCase))
                {
                    drive.HasPartitions = false;
                    drive.IsInitialized = false;
                    drive.PartitionStyle = "RAW";
                }
            }

            drive.HasPartitions = foundPartitions || drive.Partitions.Count > 0;
            if (!drive.HasPartitions)
            {
                drive.IsInitialized = false;
                drive.PartitionStyle = "RAW";
            }

            // Set default model if not found
            if (string.IsNullOrEmpty(drive.Model))
            {
                drive.Model = $"Disk {drive.DiskNumber}";
            }
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteWarning($"Failed to get details for disk {drive.DiskNumber}: {ex.Message}");
            drive.Model = $"Disk {drive.DiskNumber}";
        }
    }

    private MediaType DetermineMediaTypeFromModel(string model)
    {
        var upperModel = model.ToUpperInvariant();

        if (upperModel.Contains("NVME") || upperModel.Contains("NVM"))
            return MediaType.NVMe;

        if (upperModel.Contains("SSD") || upperModel.Contains("SOLID STATE"))
            return MediaType.SSD;

        if (upperModel.Contains("USB") || upperModel.Contains("FLASH"))
            return MediaType.USB;

        // Default to HDD
        return MediaType.HDD;
    }

    private async Task ListDisksAsync()
    {
        Console.WriteLine("\nScanning disks...\n");

        var drives = await GetDriveInfoAsync();

        Console.WriteLine("Available Disks:");
        Console.WriteLine(new string('=', 80));

        foreach (var drive in drives)
        {
            var prefix = drive.IsUnformatted ? "* " : "  ";
            Console.WriteLine($"{prefix}{drive}");

            if (drive.Partitions.Count > 0)
            {
                foreach (var part in drive.Partitions)
                {
                    var letter = part.DriveLetter.HasValue ? $"({part.DriveLetter}:)" : "    ";
                    var used = part.UsedBytes > 0 ? $" Used: {part.UsedFormatted}" : "";
                    Console.WriteLine($"      Part {part.PartitionNumber}: {part.SizeFormatted,-10} {part.FileSystem,-8} {letter} {part.Label}{used}");
                }
            }
            Console.WriteLine();
        }

        Console.WriteLine(new string('=', 80));
        Console.WriteLine($"Total: {drives.Count} disk(s)");
        Console.WriteLine("\n* = Unformatted/Uninitialized drive");
    }

    private async Task ViewDiskDetailsAsync()
    {
        var drives = await GetDriveInfoAsync();

        Console.Write("\nEnter disk number to view details: ");
        var input = Console.ReadLine()?.Trim();

        if (!int.TryParse(input, out int diskNum))
        {
            Console.WriteLine("Invalid input.");
            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey(true);
            return;
        }

        var drive = drives.FirstOrDefault(d => d.DiskNumber == diskNum);
        if (drive == null)
        {
            Console.WriteLine($"Disk {diskNum} not found.");
            Console.WriteLine("\nPress any key to continue...");
            Console.ReadKey(true);
            return;
        }

        Console.Clear();
        ConsoleHelper.WriteHeader($"Disk {diskNum} Details");

        Console.WriteLine($"\n  Disk Number:     {drive.DiskNumber}");
        Console.WriteLine($"  Model:           {drive.Model}");
        Console.WriteLine($"  Manufacturer:    {drive.Manufacturer}");
        Console.WriteLine($"  Serial Number:   {drive.SerialNumber}");
        Console.WriteLine($"  Size:            {drive.SizeFormatted} ({drive.SizeBytes:N0} bytes)");
        Console.WriteLine($"  Drive Type:      {drive.MediaType}");
        Console.WriteLine($"  Partition Style: {drive.PartitionStyle}");
        Console.WriteLine($"  Initialized:     {drive.IsInitialized}");
        Console.WriteLine($"  Has Partitions:  {drive.HasPartitions}");

        if (drive.Partitions.Count > 0)
        {
            Console.WriteLine($"\n  Partitions ({drive.Partitions.Count}):");
            Console.WriteLine("  " + new string('-', 70));

            foreach (var part in drive.Partitions)
            {
                var letter = part.DriveLetter.HasValue ? $"({part.DriveLetter}:)" : "    ";
                Console.WriteLine($"    Part {part.PartitionNumber}: {part.SizeFormatted,-10} {part.FileSystem,-8} {letter} {part.Label}");
                if (part.UsedBytes > 0)
                {
                    Console.WriteLine($"           Used: {part.UsedFormatted}");
                }
            }
        }

        Console.WriteLine("\nPress any key to continue...");
        Console.ReadKey(true);
    }

    #region Installation Steps

    private async Task<bool> PartitionDiskAsync(int diskNumber)
    {
        var script = $@"SELECT DISK {diskNumber}
CLEAN
CONVERT GPT
CREATE PARTITION EFI SIZE=100
FORMAT QUICK FS=FAT32 LABEL=""EFI""
ASSIGN LETTER=S
CREATE PARTITION PRIMARY
FORMAT QUICK FS=NTFS LABEL=""Windows""
ASSIGN LETTER=W";

        var scriptPath = Path.Combine(Path.GetTempPath(), "diskpart_script.txt");
        await File.WriteAllTextAsync(scriptPath, script);

        try
        {
            var result = await RunProcessAsync("diskpart", $"/s \"{scriptPath}\"");
            Console.WriteLine(result);
            return !result.Contains("error", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }

    private async Task<bool> ApplyImageAsync(string imagePath)
    {
        var swmParam = "";
        if (imagePath.EndsWith(".swm", StringComparison.OrdinalIgnoreCase))
        {
            var basePath = imagePath[..^4]; // Remove .swm
            swmParam = $"/SWMFile:\"{basePath}*.swm\"";
        }

        var args = $"/Apply-Image /ImageFile:\"{imagePath}\" {swmParam} /Index:1 /ApplyDir:W:\\";
        var result = await RunProcessWithOutputAsync("dism.exe", args.Trim());

        return result.ExitCode == 0;
    }

    private async Task<bool> ConfigureBootLoaderAsync()
    {
        var result = await RunProcessAsync("bcdboot.exe", @"W:\Windows /s S:");
        return !result.Contains("error", StringComparison.OrdinalIgnoreCase);
    }

    private async Task SetupAutounattendAsync(string autounattendPath)
    {
        try
        {
            // Create Panther directory
            Directory.CreateDirectory(@"W:\Windows\Panther");

            // Copy autounattend.xml
            File.Copy(autounattendPath, @"W:\Windows\Panther\unattend.xml", true);
            ConsoleHelper.WriteSuccess("Copied autounattend.xml");

            // Register with Windows Setup via registry
            //var loadResult = await RunProcessAsync("reg.exe", @"load HKLM\WIM_SYSTEM W:\Windows\System32\config\SYSTEM");
            //if (!loadResult.Contains("error", StringComparison.OrdinalIgnoreCase))
            //{
            //    await RunProcessAsync("reg.exe", @"add ""HKLM\WIM_SYSTEM\Setup"" /v UnattendFile /t REG_SZ /d ""C:\Windows\Panther\unattend.xml"" /f");
            //    await RunProcessAsync("reg.exe", @"unload HKLM\WIM_SYSTEM");
            //    ConsoleHelper.WriteSuccess("Registered unattend.xml");
            //}
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteWarning($"Failed to setup autounattend: {ex.Message}");
        }
    }

    private async Task OptimizeFilesystemAsync()
    {
        await RunProcessAsync("fsutil.exe", "8dot3name set W: 1");
        await RunProcessAsync("fsutil.exe", @"8dot3name strip /s /f W:\");
    }

    private async Task SetDeviceRegionAsync()
    {
        try
        {
            var loadResult = await RunProcessAsync("reg.exe", @"load HKLM\WIM_SOFTWARE W:\Windows\System32\config\SOFTWARE");
            if (!loadResult.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                await RunProcessAsync("reg.exe", @"add ""HKLM\WIM_SOFTWARE\Microsoft\Windows\CurrentVersion\Control Panel\DeviceRegion"" /v DeviceRegion /t REG_DWORD /d 75 /f");
                await RunProcessAsync("reg.exe", @"unload HKLM\WIM_SOFTWARE");
            }
        }
        catch { }
    }

    private async Task CleanupDriveLettersAsync(int diskNumber)
    {
        var script = $@"SELECT DISK {diskNumber}
SELECT PART 1
REMOVE LETTER=S NOERR
SELECT PART 2
REMOVE LETTER=W NOERR";

        var scriptPath = Path.Combine(Path.GetTempPath(), "cleanup_script.txt");
        await File.WriteAllTextAsync(scriptPath, script);

        try
        {
            await RunProcessAsync("diskpart", $"/s \"{scriptPath}\"");
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }

    #endregion

    #region Process Helpers

    private async Task<string> RunProcessAsync(string fileName, string arguments)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            process.Start();

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            return output + error;
        }
        catch (Exception ex)
        {
            return $"error: {ex.Message}";
        }
    }

    private async Task<(int ExitCode, string Output)> RunProcessWithOutputAsync(string fileName, string arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var outputBuilder = new StringBuilder();

        process.OutputDataReceived += (s, e) =>
        {
            if (e.Data != null)
            {
                Console.WriteLine(e.Data);
                outputBuilder.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (s, e) =>
        {
            if (e.Data != null)
            {
                Console.WriteLine(e.Data);
                outputBuilder.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync();

        return (process.ExitCode, outputBuilder.ToString());
    }

    #endregion
}
