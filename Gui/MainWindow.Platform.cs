using System.Diagnostics;
using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using CommunityToolkit.WinUI.Controls;
using Winstaller.Models;
using Winstaller.Configuration;
using Winstaller.Modules;
using Winstaller.Utilities;
using Windows.Foundation;
using WinRT.Interop;

namespace Winstaller.Gui;

public sealed partial class MainWindow : Window
{
private static void WriteDiagnosticLog(string message)
    {
        RunLog.Write("Debug", message);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.#} {units[index]}";
    }

    private static HashSet<string> GetInternalFixedDriveRoots()
    {
        var roots = DriveInfo.GetDrives()
            .Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed)
            .Select(drive => drive.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var diskSearcher = new ManagementObjectSearcher("SELECT DeviceID, InterfaceType, MediaType FROM Win32_DiskDrive");
            foreach (ManagementObject disk in diskSearcher.Get())
            {
                var interfaceType = disk["InterfaceType"]?.ToString() ?? string.Empty;
                var mediaType = disk["MediaType"]?.ToString() ?? string.Empty;
                var isExternal = interfaceType.Equals("USB", StringComparison.OrdinalIgnoreCase) ||
                                 mediaType.Contains("external", StringComparison.OrdinalIgnoreCase) ||
                                 mediaType.Contains("removable", StringComparison.OrdinalIgnoreCase);

                if (!isExternal)
                {
                    continue;
                }

                foreach (var root in GetDriveRootsForDisk(disk))
                {
                    roots.Remove(root);
                }
            }
        }
        catch
        {
            return roots;
        }

        return roots;
    }

    private static IEnumerable<string> GetDriveRootsForDisk(ManagementObject disk)
    {
        foreach (ManagementObject partition in disk.GetRelated("Win32_DiskPartition"))
        {
            foreach (ManagementObject logicalDisk in partition.GetRelated("Win32_LogicalDisk"))
            {
                var deviceId = logicalDisk["DeviceID"]?.ToString();
                if (!string.IsNullOrWhiteSpace(deviceId))
                {
                    yield return deviceId + "\\";
                }
            }
        }
    }

    private void ApplyTheme(ElementTheme theme)
    {
        if (RootGrid.XamlRoot?.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme;
        }
        _navigation.RequestedTheme = theme;
        _content.RequestedTheme = theme;
        _contentScroll.RequestedTheme = theme;
        _titleBar.RequestedTheme = theme;
        RootGrid.Background = new SolidColorBrush(Colors.Transparent);
        _titleBar.Background = ResourceBrush("WinstallerTitleBarBrush");
        _navigation.Background = ResourceBrush("WinstallerPaneBrush");
        _contentScroll.Background = ResourceBrush("WinstallerPageBrush");
        SetTitleBarTheme(theme);
    }

    private void LoadSavedTheme()
    {
        _requestedTheme = BootstrapManager.Theme.ToLowerInvariant() switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        _themeBox.SelectedIndex = _requestedTheme switch
        {
            ElementTheme.Light => 1,
            ElementTheme.Dark => 2,
            _ => 0
        };
        ApplyTheme(_requestedTheme);
    }

    private static string GetThemeName(ElementTheme theme)
    {
        return theme switch
        {
            ElementTheme.Light => "light",
            ElementTheme.Dark => "dark",
            _ => "system"
        };
    }

    private void SetTitleBarTheme(ElementTheme theme)
    {
        var appWindow = GetAppWindow();
        if (appWindow?.TitleBar is null)
        {
            return;
        }

        var dark = theme == ElementTheme.Dark ||
                   (theme == ElementTheme.Default && IsSystemDarkTheme());
        var titleBar = appWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.BackgroundColor = Colors.Transparent;
        titleBar.InactiveBackgroundColor = Colors.Transparent;
        titleBar.ForegroundColor = dark ? Colors.White : Colors.Black;
        titleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        titleBar.ButtonHoverBackgroundColor = dark ? ColorHelper.FromArgb(255, 55, 55, 55) : ColorHelper.FromArgb(255, 232, 232, 232);
    }

    private AppWindow? GetAppWindow()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var id = Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(id);
    }

    private void RegisterCloseGuard()
    {
        var appWindow = GetAppWindow();
        if (appWindow is not null)
            appWindow.Closing += AppWindowClosing;
    }

    private void AppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_isRunning)
            return;

        args.Cancel = true;
        AppendOutput("A task is still running. Wait for it to finish before closing Winstaller.");
    }

    private void BeginLongOperation()
    {
        _busyDepth++;
        _isRunning = true;
        _busyBar.Visibility = Visibility.Visible;
        _navigation.IsEnabled = false;
        SetTopBarActionsEnabled(false);
    }

    private void EndLongOperation()
    {
        if (_busyDepth > 0)
            _busyDepth--;

        if (_busyDepth > 0)
            return;

        _isRunning = false;
        _busyBar.Visibility = Visibility.Collapsed;
        _navigation.IsEnabled = true;
        SetTopBarActionsEnabled(true);
    }

    private async Task PaintBusyIndicatorAsync()
    {
        await Task.Yield();
        await Task.Delay(50);
    }

    private void SetTopBarActionsEnabled(bool enabled)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => SetTopBarActionsEnabled(enabled));
            return;
        }

        foreach (var child in _topBarActions.Children.OfType<Control>())
            child.IsEnabled = enabled;
    }

    private static Brush ResourceBrush(string key)
    {
        return (Brush)Application.Current.Resources[key];
    }

    private static bool IsSystemDarkTheme()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        var value = key?.GetValue("AppsUseLightTheme");
        return value is int intValue && intValue == 0;
    }

    private void AppendOutput(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
            RunLog.Write("UI", text);
        AppendOutputText(text + Environment.NewLine);
    }

    private void AppendOutputText(string text)
    {
        lock (_outputLock)
        {
            _pendingOutputText.Append(text);
            if (_outputFlushQueued)
                return;
            _outputFlushQueued = true;
        }

        if (!DispatcherQueue.TryEnqueue(FlushOutputText))
        {
            lock (_outputLock)
                _outputFlushQueued = false;
        }
    }

    private void FlushOutputText()
    {
        FlushOutputText(_activeOutputBox);
    }

    private void FlushOutputText(TextBox? outputBox)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(FlushOutputText);
            return;
        }

        string chunk;
        lock (_outputLock)
        {
            chunk = _pendingOutputText.ToString();
            _pendingOutputText.Clear();
            _outputFlushQueued = false;
        }

        if (outputBox is null || string.IsNullOrEmpty(chunk))
            return;

        AppendTextToOutputBox(outputBox, chunk);
    }

    private static void AppendTextToOutputBox(TextBox outputBox, string text)
    {
        const int maxVisibleCharacters = 200_000;
        var selectionStart = outputBox.SelectionStart;
        var selectionLength = outputBox.SelectionLength;
        var wasAtEnd = selectionStart + selectionLength >= outputBox.Text.Length;

        var combined = outputBox.Text + text;
        var trimOffset = 0;
        if (combined.Length > maxVisibleCharacters)
        {
            trimOffset = combined.Length - maxVisibleCharacters;
            combined = combined[trimOffset..];
        }

        outputBox.Text = combined;
        if (wasAtEnd)
        {
            outputBox.SelectionStart = outputBox.Text.Length;
            outputBox.SelectionLength = 0;
            return;
        }

        outputBox.SelectionStart = Math.Clamp(selectionStart - trimOffset, 0, outputBox.Text.Length);
        outputBox.SelectionLength = Math.Clamp(selectionLength, 0, outputBox.Text.Length - outputBox.SelectionStart);
    }

    private double GetLogDialogWidth()
    {
        var rootWidth = RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : 1900;
        return Math.Min(1900, Math.Max(1100, rootWidth - 96));
    }

    private void EnableAppSettingsTextCopy(TextBox box)
    {
        box.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.C &&
                Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                CopyText(box.SelectedText);
                args.Handled = true;
            }
        };

        var menu = new MenuFlyout();
        var copy = new MenuFlyoutItem { Text = "Copy" };
        copy.Click += (_, _) => CopyText(box.SelectedText);
        var selectAll = new MenuFlyoutItem { Text = "Select All" };
        selectAll.Click += (_, _) => box.SelectAll();
        menu.Items.Add(copy);
        menu.Items.Add(selectAll);
        box.ContextFlyout = menu;
    }

    private void EnableAppSettingsTextCopyInTree(DependencyObject root)
    {
        if (root is TextBox box)
            EnableAppSettingsTextCopy(box);

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            EnableAppSettingsTextCopyInTree(VisualTreeHelper.GetChild(root, index));
    }
    private TextBox CreateLogOutputBox(double width)
    {
        var outputBox = new TextBox
        {
            AcceptsReturn = true,
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono"),
            MinWidth = width,
            Width = width,
            MinHeight = 520,
            MaxHeight = 720
        };

        outputBox.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.C &&
                Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                CopyText(string.IsNullOrEmpty(outputBox.SelectedText) ? outputBox.Text : outputBox.SelectedText);
                args.Handled = true;
            }
        };

        var logMenu = new MenuFlyout();
        var copySelectionItem = new MenuFlyoutItem { Text = "Copy Selection" };
        copySelectionItem.Click += (_, _) => CopyText(outputBox.SelectedText);
        var copyAllItem = new MenuFlyoutItem { Text = "Copy All Visible Log" };
        copyAllItem.Click += (_, _) => CopyText(outputBox.Text);
        logMenu.Items.Add(copySelectionItem);
        logMenu.Items.Add(copyAllItem);
        outputBox.ContextFlyout = logMenu;
        return outputBox;
    }

    private void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        try
        {
            var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dataPackage.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
        }
        catch (Exception clipboardException)
        {
            try
            {
                SetClipboardText(WindowNative.GetWindowHandle(this), text);
            }
            catch (Exception fallbackException)
            {
                RunLog.WriteException("UI", "Clipboard copy failed", new AggregateException(clipboardException, fallbackException));
                AppendOutput($"Copy failed: {fallbackException.Message}");
            }
        }
    }

    private void CopyTextFromFile(string path)
    {
        try
        {
            if (File.Exists(path))
                CopyText(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            RunLog.WriteException("UI", $"Failed reading copy source: {path}", ex);
            AppendOutput($"Copy failed: {ex.Message}");
        }
    }

    private static void SetClipboardText(nint hwnd, string text)
    {
        var opened = false;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(hwnd))
            {
                opened = true;
                break;
            }

            Thread.Sleep(20);
        }

        if (!opened)
            throw new InvalidOperationException("Clipboard is unavailable.");

        var handle = nint.Zero;
        try
        {
            if (!EmptyClipboard())
                throw new InvalidOperationException("Could not clear clipboard.");
            var bytes = (text.Length + 1) * 2;
            handle = GlobalAlloc(GmemMoveable, (nuint)bytes);
            if (handle == nint.Zero)
                throw new InvalidOperationException("Could not allocate clipboard memory.");

            var target = GlobalLock(handle);
            if (target == nint.Zero)
                throw new InvalidOperationException("Could not lock clipboard memory.");

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                Marshal.WriteInt16(target, text.Length * 2, 0);
            }
            finally
            {
                GlobalUnlock(handle);
            }

            if (SetClipboardData(CfUnicodeText, handle) == nint.Zero)
                throw new InvalidOperationException("Could not set clipboard data.");

            handle = nint.Zero;
        }
        finally
        {
            CloseClipboard();
            if (handle != nint.Zero)
                GlobalFree(handle);
        }
    }

    private const uint GmemMoveable = 0x0002;
    private const uint CfUnicodeText = 13;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint uFormat, nint hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalFree(nint hMem);
}

