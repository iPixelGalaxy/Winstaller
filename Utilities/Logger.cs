namespace Winstaller.Utilities;

/// <summary>
/// Log level enumeration
/// </summary>
public enum LogLevel
{
    Debug,
    Info,
    Success,
    Warning,
    Error,
    Critical
}

/// <summary>
/// Logger utility with colored console output
/// Colors: Green=Success, White=Info, DarkGray=Debug, Yellow=Warning, Red=Error, Magenta=Critical
/// </summary>
public static class Logger
{
    private static readonly object _lock = new();
    private static LogLevel _minimumLevel = LogLevel.Info;

    /// <summary>
    /// Gets or sets the minimum log level. Messages below this level will not be displayed.
    /// </summary>
    public static LogLevel MinimumLevel
    {
        get => _minimumLevel;
        set => _minimumLevel = value;
    }

    /// <summary>
    /// Enable debug logging
    /// </summary>
    public static void EnableDebug() => _minimumLevel = LogLevel.Debug;

    /// <summary>
    /// Disable debug logging (default)
    /// </summary>
    public static void DisableDebug() => _minimumLevel = LogLevel.Info;

    /// <summary>
    /// Log a debug message (dark gray)
    /// </summary>
    public static void Debug(string message) => Log(LogLevel.Debug, message);

    /// <summary>
    /// Log an info message (white)
    /// </summary>
    public static void Info(string message) => Log(LogLevel.Info, message);

    /// <summary>
    /// Log a success message (green)
    /// </summary>
    public static void Success(string message) => Log(LogLevel.Success, message);

    /// <summary>
    /// Log a warning message (yellow)
    /// </summary>
    public static void Warning(string message) => Log(LogLevel.Warning, message);

    /// <summary>
    /// Log an error message (red)
    /// </summary>
    public static void Error(string message) => Log(LogLevel.Error, message);

    /// <summary>
    /// Log a critical message (magenta/purple)
    /// </summary>
    public static void Critical(string message) => Log(LogLevel.Critical, message);

    /// <summary>
    /// Log a message with the specified level
    /// </summary>
    public static void Log(LogLevel level, string message)
    {
        if (level < _minimumLevel)
            return;

        lock (_lock)
        {
            var (color, prefix) = GetColorAndPrefix(level);

            Console.ForegroundColor = color;
            Console.WriteLine($"{prefix} {message}");
            Console.ForegroundColor = ConsoleColor.White;
        }
    }

    /// <summary>
    /// Log a message with the specified level, without a newline
    /// </summary>
    public static void LogInline(LogLevel level, string message)
    {
        if (level < _minimumLevel)
            return;

        lock (_lock)
        {
            var (color, _) = GetColorAndPrefix(level);

            Console.ForegroundColor = color;
            Console.Write(message);
            Console.ForegroundColor = ConsoleColor.White;
        }
    }

    /// <summary>
    /// Write a raw line without any formatting (resets to white after)
    /// </summary>
    public static void WriteLine(string message = "")
    {
        lock (_lock)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(message);
        }
    }

    /// <summary>
    /// Write raw text without newline (resets to white after)
    /// </summary>
    public static void Write(string message)
    {
        lock (_lock)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write(message);
        }
    }

    /// <summary>
    /// Log with a specific color (resets to white after)
    /// </summary>
    public static void WriteColored(ConsoleColor color, string message, bool newLine = true)
    {
        lock (_lock)
        {
            Console.ForegroundColor = color;
            if (newLine)
                Console.WriteLine(message);
            else
                Console.Write(message);
            Console.ForegroundColor = ConsoleColor.White;
        }
    }

    /// <summary>
    /// Write a header with decorative lines
    /// </summary>
    public static void WriteHeader(string text)
    {
        lock (_lock)
        {
            var line = new string('=', Math.Max(text.Length + 4, 50));
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(line);
            Console.WriteLine($"  {text}");
            Console.WriteLine(line);
        }
    }

    /// <summary>
    /// Write a subheader with decorative lines
    /// </summary>
    public static void WriteSubHeader(string text)
    {
        lock (_lock)
        {
            var line = new string('-', Math.Max(text.Length + 4, 40));
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(line);
            Console.WriteLine($"  {text}");
            Console.WriteLine(line);
        }
    }

    /// <summary>
    /// Display a confirmation prompt and return the result
    /// </summary>
    public static bool Confirm(string message)
    {
        lock (_lock)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"{message} (y/n): ");
            var response = Console.ReadLine()?.Trim().ToLowerInvariant();
            return response == "y" || response == "yes";
        }
    }

    /// <summary>
    /// Log an exception with full details at Error level
    /// </summary>
    public static void Exception(Exception ex, string? context = null)
    {
        if (!string.IsNullOrEmpty(context))
            Error($"{context}: {ex.Message}");
        else
            Error(ex.Message);

        Debug($"Exception type: {ex.GetType().FullName}");
        Debug($"Stack trace: {ex.StackTrace}");

        if (ex.InnerException != null)
        {
            Debug($"Inner exception: {ex.InnerException.Message}");
        }
    }

    /// <summary>
    /// Log the start of an operation (debug level)
    /// </summary>
    public static void OperationStart(string operation)
    {
        Debug($"Starting: {operation}");
    }

    /// <summary>
    /// Log the end of an operation (debug level)
    /// </summary>
    public static void OperationEnd(string operation, bool success = true)
    {
        if (success)
            Debug($"Completed: {operation}");
        else
            Debug($"Failed: {operation}");
    }

    private static (ConsoleColor Color, string Prefix) GetColorAndPrefix(LogLevel level)
    {
        return level switch
        {
            LogLevel.Debug => (ConsoleColor.DarkGray, "[DBG]"),
            LogLevel.Info => (ConsoleColor.White, "[INF]"),
            LogLevel.Success => (ConsoleColor.Green, "[OK ]"),
            LogLevel.Warning => (ConsoleColor.Yellow, "[WRN]"),
            LogLevel.Error => (ConsoleColor.Red, "[ERR]"),
            LogLevel.Critical => (ConsoleColor.Magenta, "[CRT]"),
            _ => (ConsoleColor.White, "[???]")
        };
    }
}
