using Microsoft.UI.Xaml;
using Winstaller.Configuration;
using Winstaller.Utilities;

namespace Winstaller.Gui;

public sealed partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            WriteCrashLog("AppDomain unhandled exception", args.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
        UnhandledException += (_, args) =>
        {
            args.Handled = true;
            WriteCrashLog("Unhandled UI exception", args.Exception);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }

    private static void WriteCrashLog(string context, Exception? exception)
    {
        RunLog.WriteException("Crash", context, exception);
    }
}
