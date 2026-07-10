using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Winstaller.Utilities;

public sealed record LockingProcessInfo(int ProcessId, string ProcessName, string Path);

public static class SymlinkLockUtility
{
    public static IReadOnlyList<LockingProcessInfo> FindLockingProcesses(IEnumerable<string> paths)
    {
        var found = new Dictionary<int, LockingProcessInfo>();
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
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