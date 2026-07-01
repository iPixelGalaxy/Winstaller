namespace Winstaller.Utilities;

/// <summary>
/// Console helper utilities for user interaction
/// Uses Logger internally for consistent colored output
/// </summary>
public static class ConsoleHelper
{
    public static void WriteHeader(string text) => Logger.WriteHeader(text);

    public static void WriteSubHeader(string text) => Logger.WriteSubHeader(text);

    public static void WriteSuccess(string message) => Logger.Success(message);

    public static void WriteError(string message) => Logger.Error(message);

    public static void WriteWarning(string message) => Logger.Warning(message);

    public static void WriteInfo(string message) => Logger.Info(message);

    public static void WriteDebug(string message) => Logger.Debug(message);

    public static void WriteCritical(string message) => Logger.Critical(message);

    public static bool Confirm(string message) => Logger.Confirm(message);
}
