using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace Winstaller.Utilities;

public sealed record LockingProcessInfo(int ProcessId, string ProcessName, string Path);
public sealed record RestartableApplication(string ExecutablePath, string Arguments, string ProcessName);

public static class SymlinkLockUtility
{
    public static IReadOnlyList<LockingProcessInfo> FindExactLockingProcesses(IEnumerable<string> paths) =>
        FindExactLockingProcessDetails(paths).Select(process => new LockingProcessInfo(process.ProcessId, process.ProcessName, process.Path)).ToList();

    public static bool StopExactUserAppLocks(IEnumerable<string> paths, ICollection<RestartableApplication> stoppedApplications, Action<string>? log)
    {
        var lockingProcesses = FindExactLockingProcessDetails(paths);
        if (lockingProcesses.Count == 0)
            return false;

        var stoppedAny = false;
        foreach (var processInfo in lockingProcesses)
        {
            log?.Invoke($"Locked by {processInfo.ProcessName} (PID {processInfo.ProcessId}): {processInfo.Path}");
            if (!IsSafeToStop(processInfo))
            {
                log?.Invoke($"Leaving protected or non-user process running: {processInfo.ProcessName} (PID {processInfo.ProcessId})");
                continue;
            }

            try
            {
                using var process = Process.GetProcessById(processInfo.ProcessId);
                var restart = GetRestartableApplication(process);
                log?.Invoke($"Stopping {processInfo.ProcessName} (PID {processInfo.ProcessId})");
                process.Kill(true);
                process.WaitForExit(5000);
                if (restart is not null)
                    stoppedApplications.Add(restart);
                stoppedAny = true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"Could not stop {processInfo.ProcessName} (PID {processInfo.ProcessId}): {ex.Message}");
            }
        }

        return stoppedAny;
    }

    public static void RestartApplications(IEnumerable<RestartableApplication> applications, Action<string>? log)
    {
        foreach (var application in applications
            .GroupBy(application => $"{application.ExecutablePath}\0{application.Arguments}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()))
        {
            try
            {
                Process.Start(new ProcessStartInfo(application.ExecutablePath, application.Arguments) { UseShellExecute = true });
                log?.Invoke($"Restarted {application.ProcessName}");
            }
            catch (Exception ex)
            {
                log?.Invoke($"Could not restart {application.ProcessName}: {ex.Message}");
            }
        }
    }

    public static IReadOnlyList<LockingProcessInfo> FindLockingProcesses(IEnumerable<string> paths)
    {
        var found = new Dictionary<int, LockingProcessInfo>();
        foreach (var path in ExpandLockCheckPaths(paths).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var process in FindLockingProcesses(path))
                found.TryAdd(process.ProcessId, process);
        }

        return found.Values.OrderBy(process => process.ProcessName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool ClearLocks(IEnumerable<string> paths, bool forceKill, Action<string>? log)
    {
        var lockingProcesses = FindLockingProcesses(paths);
        if (lockingProcesses.Count == 0)
            return true;

        foreach (var process in lockingProcesses)
            log?.Invoke($"Locked by {process.ProcessName} (PID {process.ProcessId}): {process.Path}");

        if (!forceKill)
            return false;

        foreach (var processInfo in lockingProcesses)
        {
            try
            {
                var process = Process.GetProcessById(processInfo.ProcessId);
                log?.Invoke($"Killing {processInfo.ProcessName} (PID {processInfo.ProcessId})");
                process.Kill(true);
                process.WaitForExit(10000);
            }
            catch (Exception ex)
            {
                log?.Invoke($"Failed to kill {processInfo.ProcessName} (PID {processInfo.ProcessId}): {ex.Message}");
                return false;
            }
        }

        return FindLockingProcesses(paths).Count == 0;
    }

    private static IEnumerable<string> ExpandLockCheckPaths(IEnumerable<string> paths)
    {
        const int maxDirectoryEntries = 300;
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return path;

            if (!Directory.Exists(path))
                continue;

            List<string> entries;
            try
            {
                entries = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Take(maxDirectoryEntries)
                    .ToList();
            }
            catch
            {
                continue;
            }

            foreach (var entry in entries)
                yield return entry;
        }
    }

    private static IReadOnlyList<ExactLockingProcessInfo> FindExactLockingProcessDetails(IEnumerable<string> paths)
    {
        var resources = paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (resources.Length == 0)
            return [];

        var sessionHandle = 0u;
        if (RmStartSession(out sessionHandle, 0, Guid.NewGuid().ToString("N")) != 0)
            return [];

        try
        {
            if (RmRegisterResources(sessionHandle, (uint)resources.Length, resources, 0, null, 0, null) != 0)
                return [];

            uint needed = 0;
            uint count = 0;
            uint reason;
            if (RmGetList(sessionHandle, out needed, ref count, null, out reason) != ErrorMoreData || needed == 0)
                return [];

            var processInfo = new RmProcessInfo[needed];
            count = needed;
            if (RmGetList(sessionHandle, out needed, ref count, processInfo, out reason) != 0)
                return [];

            return processInfo.Take((int)count)
                .GroupBy(info => info.Process.dwProcessId)
                .Select(group => group.First())
                .Select(info => new ExactLockingProcessInfo(info.Process.dwProcessId,
                    string.IsNullOrWhiteSpace(info.strAppName) ? $"PID {info.Process.dwProcessId}" : info.strAppName,
                    resources[0], info.ApplicationType, info.TSSessionId))
                .ToList();
        }
        finally
        {
            RmEndSession(sessionHandle);
        }
    }

    private static bool IsSafeToStop(ExactLockingProcessInfo processInfo)
    {
        const uint RmMainWindow = 1;
        const uint RmOtherWindow = 2;
        const uint RmConsole = 5;
        if (processInfo.ProcessId <= 4 || processInfo.ProcessId == Environment.ProcessId ||
            processInfo.SessionId == 0 || processInfo.ApplicationType is not (RmMainWindow or RmOtherWindow or RmConsole))
            return false;
        return !processInfo.ProcessName.Equals("Winstaller", StringComparison.OrdinalIgnoreCase) &&
               !processInfo.ProcessName.Equals("Windows Explorer", StringComparison.OrdinalIgnoreCase);
    }

    private static RestartableApplication? GetRestartableApplication(Process process)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT ExecutablePath, CommandLine FROM Win32_Process WHERE ProcessId = {process.Id}");
            var managementProcess = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
            var executablePath = managementProcess?["ExecutablePath"]?.ToString() ?? process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executablePath))
                return null;

            var commandLine = managementProcess?["CommandLine"]?.ToString() ?? executablePath;
            return new RestartableApplication(executablePath, GetArguments(commandLine, executablePath), process.ProcessName);
        }
        catch
        {
            return null;
        }
    }

    private static string GetArguments(string commandLine, string executablePath)
    {
        if (commandLine.StartsWith('"'))
        {
            var closingQuote = commandLine.IndexOf('"', 1);
            return closingQuote >= 0 ? commandLine[(closingQuote + 1)..].TrimStart() : string.Empty;
        }

        return commandLine.StartsWith(executablePath, StringComparison.OrdinalIgnoreCase)
            ? commandLine[executablePath.Length..].TrimStart()
            : string.Empty;
    }

    private sealed record ExactLockingProcessInfo(int ProcessId, string ProcessName, string Path, uint ApplicationType, uint SessionId);

    private static IReadOnlyList<LockingProcessInfo> FindLockingProcesses(string path)
    {
        var sessionHandle = 0u;
        var sessionKey = Guid.NewGuid().ToString("N");
        if (RmStartSession(out sessionHandle, 0, sessionKey) != 0)
            return [];

        try
        {
            var resources = new[] { path };
            if (RmRegisterResources(sessionHandle, (uint)resources.Length, resources, 0, null, 0, null) != 0)
                return [];

            uint needed = 0;
            uint count = 0;
            uint reason;
            var result = RmGetList(sessionHandle, out needed, ref count, null, out reason);
            if (result != ErrorMoreData || needed == 0)
                return [];

            var processInfo = new RmProcessInfo[needed];
            count = needed;
            result = RmGetList(sessionHandle, out needed, ref count, processInfo, out reason);
            if (result != 0)
                return [];

            return processInfo
                .Take((int)count)
                .Select(info => new LockingProcessInfo(
                    info.Process.dwProcessId,
                    string.IsNullOrWhiteSpace(info.strAppName) ? $"PID {info.Process.dwProcessId}" : info.strAppName,
                    path))
                .ToList();
        }
        finally
        {
            RmEndSession(sessionHandle);
        }
    }

    private const int ErrorMoreData = 234;

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint pSessionHandle,
        uint nFiles,
        string[]? rgsFilenames,
        uint nApplications,
        RmUniqueProcess[]? rgApplications,
        uint nServices,
        string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint dwSessionHandle,
        out uint pnProcInfoNeeded,
        ref uint pnProcInfo,
        [In, Out] RmProcessInfo[]? rgAffectedApps,
        out uint lpdwRebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string strServiceShortName;
        public uint ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }
}

