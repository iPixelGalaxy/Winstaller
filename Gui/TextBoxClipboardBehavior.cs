using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace Winstaller.Gui;

public static class TextBoxClipboardBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(TextBoxClipboardBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not TextBox box || args.NewValue is not true)
            return;

        box.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
        if (box.ContextFlyout is null)
            box.ContextFlyout = CreateContextMenu(box);
    }

    private static void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (sender is not TextBox box ||
            !InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down))
            return;

        switch (args.Key)
        {
            case VirtualKey.A:
                box.SelectAll();
                args.Handled = true;
                break;
            case VirtualKey.C:
                box.CopySelectionToClipboard();
                args.Handled = true;
                break;
            case VirtualKey.X when !box.IsReadOnly:
                box.CutSelectionToClipboard();
                args.Handled = true;
                break;
            case VirtualKey.V when !box.IsReadOnly:
                box.PasteFromClipboard();
                args.Handled = true;
                break;
        }
    }

    private static MenuFlyout CreateContextMenu(TextBox box)
    {
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
        return menu;
    }
}
