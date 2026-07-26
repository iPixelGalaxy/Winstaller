using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Winstaller.Configuration;
using Winstaller.Utilities;

namespace Winstaller.Gui;

public sealed partial class App : Application
{
    private Window? _window;
    private readonly HashSet<TextBox> _clipboardTextBoxes = [];

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
        if (_window.Content is FrameworkElement root)
            root.LayoutUpdated += (_, _) => AttachTextBoxClipboardMenus(root);
        _window.Activate();
    }

    private static void WriteCrashLog(string context, Exception? exception)
    {
        RunLog.WriteException("Crash", context, exception);
    }

    private void AttachTextBoxClipboardMenus(DependencyObject root)
    {
        if (root is TextBox box)
            AttachTextBoxClipboardMenu(box);

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            AttachTextBoxClipboardMenus(VisualTreeHelper.GetChild(root, index));
    }

    private void AttachTextBoxClipboardMenu(TextBox box)
    {
        if (!_clipboardTextBoxes.Add(box) || box.ContextFlyout is not null)
            return;

        var menu = new MenuFlyout();
        var cut = new MenuFlyoutItem { Text = "Cut" };
        var copy = new MenuFlyoutItem { Text = "Copy" };
        var paste = new MenuFlyoutItem { Text = "Paste" };
        var selectAll = new MenuFlyoutItem { Text = "Select All" };
        menu.Opening += (_, _) =>
        {
            var canEdit = box.IsEnabled && !box.IsReadOnly;
            cut.IsEnabled = canEdit && box.SelectionLength > 0;
            copy.IsEnabled = box.IsEnabled && box.SelectionLength > 0;
            paste.IsEnabled = canEdit;
            selectAll.IsEnabled = box.IsEnabled && box.Text.Length > 0;
        };
        cut.Click += (_, _) => box.CutSelectionToClipboard();
        copy.Click += (_, _) => box.CopySelectionToClipboard();
        paste.Click += (_, _) => box.PasteFromClipboard();
        selectAll.Click += (_, _) => box.SelectAll();
        menu.Items.Add(cut);
        menu.Items.Add(copy);
        menu.Items.Add(paste);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(selectAll);
        box.ContextFlyout = menu;
    }
}
