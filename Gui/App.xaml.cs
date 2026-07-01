using Microsoft.UI.Xaml;
using Winstaller.Utilities;

namespace Winstaller.Gui;

public sealed partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            args.Handled = true;
            Logger.Error($"Unhandled UI exception: {args.Exception.Message}");
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
