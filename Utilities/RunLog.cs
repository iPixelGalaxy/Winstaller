using Winstaller.Configuration;

namespace Winstaller.Utilities;

public static class RunLog
{
    private static readonly object LockObject = new();
    private static string? _path;

    public static string Path
    {
        get
        {
            lock (LockObject)
            {
                return _path ??= CreatePath();
            }
        }
    }

    public static void Write(string area, string message)
    {
        try
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{area}] {message}";
            lock (LockObject)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path) ?? AppContext.BaseDirectory);
                File.AppendAllText(Path, line + Environment.NewLine);
            }
        }
        catch
        {
        }
    }

    public static void WriteException(string area, string context, Exception? exception)
    {
        Write(area, exception is null ? context : $"{context}{Environment.NewLine}{exception}");
    }

    private static string CreatePath()
    {
        var logDirectory = BootstrapManager.DataRoot is null
            ? System.IO.Path.Combine(BootstrapManager.BootstrapDirectory, "logs")
            : BootstrapManager.LogsDirectory;
        Directory.CreateDirectory(logDirectory);
        return System.IO.Path.Combine(logDirectory, $"log-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    }
}

