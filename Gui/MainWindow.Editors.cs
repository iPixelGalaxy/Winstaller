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
private FrameworkElement BuildFontsContent(FontsConfig config)
    {
        var fontsDirectory = Environment.ExpandEnvironmentVariables(config.FontsDirectory)
            .Replace("{USERNAME}", Environment.UserName, StringComparison.OrdinalIgnoreCase);
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "Fonts to install",
            FontSize = 17,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 }
        });

        if (!Directory.Exists(fontsDirectory))
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"Fonts folder not found: {fontsDirectory}",
                Foreground = ResourceBrush("WinstallerSecondaryTextBrush"),
                TextWrapping = TextWrapping.Wrap
            });
            return Card(panel);
        }

        IReadOnlyList<string> fontFiles;
        try
        {
            fontFiles = Directory.GetFiles(fontsDirectory, "*.ttf")
                .Concat(Directory.GetFiles(fontsDirectory, "*.otf"))
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"Could not read fonts folder: {ex.Message}",
                Foreground = ResourceBrush("WinstallerSecondaryTextBrush"),
                TextWrapping = TextWrapping.Wrap
            });
            return Card(panel);
        }

        panel.Children.Add(new TextBlock
        {
            Text = fontFiles.Count == 0
                ? "No .ttf or .otf fonts found."
                : $"{fontFiles.Count} font{(fontFiles.Count == 1 ? string.Empty : "s")}",
            Foreground = ResourceBrush("WinstallerSecondaryTextBrush")
        });

        panel.Children.Add(new ItemsRepeater
        {
            ItemsSource = fontFiles,
            VerticalCacheLength = 1,
            Layout = new StackLayout { Spacing = 10 },
            ItemTemplate = new CallbackElementFactory(data => new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children =
                {
                    new FontIcon { Glyph = "\uE8D2", FontSize = 16, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = Path.GetFileName((string)data!), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }
                }
            })
        });

        return Card(panel);
    }


    private FrameworkElement BuildShellFoldersContent(ShellFoldersConfig config)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(BuildListSection(config, typeof(ShellFoldersConfig).GetProperty(nameof(ShellFoldersConfig.Folders))!, allowAdd: false));

        var presets = GetShellFolderPresets()
            .Where(preset => !config.Folders.Any(folder => folder.RegistryValue.Equals(preset.RegistryValue, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (presets.Count > 0)
        {
            var addButton = new Button { Content = "+ Add Folder", CornerRadius = new CornerRadius(4) };
            var flyout = new MenuFlyout();
            foreach (var preset in presets)
            {
                var item = new MenuFlyoutItem { Text = preset.Name };
                item.Click += (_, _) =>
                {
                    config.Folders.Add(new ShellFolderMapping { FolderName = preset.Name, RegistryValue = preset.RegistryValue, Path = preset.DefaultPath });
                    SaveConfiguration();
                    RenderModule(_modules.First(module => ReferenceEquals(module.Config, config)));
                };
                flyout.Items.Add(item);
            }
            addButton.Flyout = flyout;
            panel.Children.Add(addButton);
        }

        return panel;
    }


    private FrameworkElement BuildConfigEditor(object config, bool includeScalarSettings = true)
    {
        var panel = new StackPanel { Spacing = 12 };
        foreach (var property in config.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite || property.Name == "Enabled")
            {
                continue;
            }

            if (!includeScalarSettings && !IsSupportedList(property.PropertyType))
            {
                continue;
            }

            if (!includeScalarSettings && property.Name.Contains("Ignored", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            panel.Children.Add(BuildConfigSection(config, property));
        }

        return panel;
    }

    private FrameworkElement BuildConfigSection(object target, PropertyInfo property)
    {
        return IsSupportedList(property.PropertyType)
            ? BuildListSection(target, property)
            : BuildSettingRow(target, property);
    }

    private FrameworkElement BuildSettingRow(object target, PropertyInfo property)
    {
        var row = new StackPanel { Spacing = 10 };
        var header = new Grid { ColumnSpacing = 14 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        header.Children.Add(new FontIcon
        {
            Glyph = GetConfigGlyph(property, null),
            FontSize = 20,
            Width = 28,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0)
        });

        var label = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock
        {
            Text = SplitName(property.Name),
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 },
            TextWrapping = TextWrapping.WrapWholeWords
        });
        label.Children.Add(new TextBlock
        {
            Text = GetSettingDescription(property),
            Foreground = ResourceBrush("WinstallerSecondaryTextBrush"),
            FontSize = 12,
            TextWrapping = TextWrapping.WrapWholeWords
        });
        Grid.SetColumn(label, 1);
        header.Children.Add(label);
        row.Children.Add(header);

        var editor = BuildValueEditor(target, property);
        editor.VerticalAlignment = VerticalAlignment.Center;
        editor.HorizontalAlignment = HorizontalAlignment.Stretch;
        editor.Margin = new Thickness(42, 0, 0, 0);
        row.Children.Add(editor);

        var card = Card(row);
        card.MaxWidth = 760;
        card.HorizontalAlignment = HorizontalAlignment.Stretch;
        return card;
    }

    private FrameworkElement BuildPropertyEditor(object target, PropertyInfo property)
    {
        var row = new Grid { ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        row.Children.Add(new StackPanel
        {
            Children =
            {
                new TextBlock { Text = SplitName(property.Name), FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 } },
                new TextBlock { Text = property.PropertyType.Name, Opacity = 0.6, FontSize = 12 }
            }
        });

        var editor = IsSupportedList(property.PropertyType)
            ? BuildListEditor(target, property)
            : BuildValueEditor(target, property);

        Grid.SetColumn(editor, 1);
        row.Children.Add(editor);
        return row;
    }

    private FrameworkElement BuildValueEditor(object target, PropertyInfo property)
    {
        var value = property.GetValue(target);

        FrameworkElement editor;
        if (property.PropertyType == typeof(bool))
        {
            var toggle = new ToggleSwitch { IsOn = value is true };
            toggle.Toggled += (_, _) =>
            {
                if (_isLoadingUi)
                {
                    return;
                }

                property.SetValue(target, toggle.IsOn);
                SaveConfiguration();
            };
            editor = toggle;
        }
        else if (property.PropertyType == typeof(int))
        {
            var box = new NumberBox
            {
                Value = value is int intValue ? intValue : 0,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            box.ValueChanged += (_, args) =>
            {
                if (_isLoadingUi)
                {
                    return;
                }

                if (!double.IsNaN(args.NewValue))
                {
                    property.SetValue(target, Convert.ToInt32(args.NewValue));
                    SaveConfiguration();
                }
            };
            editor = box;
        }
        else if (property.PropertyType == typeof(string))
        {
            var isReadOnly = IsReadOnlySetting(target, property);
            if (isReadOnly)
            {
                editor = new Border
                {
                    Background = ResourceBrush("WinstallerCardBrush"),
                    BorderBrush = ResourceBrush("WinstallerCardStrokeBrush"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 8, 10, 8),
                    Opacity = 0.68,
                    Child = new TextBlock
                    {
                        Text = value?.ToString() ?? string.Empty,
                        Foreground = ResourceBrush("WinstallerSecondaryTextBrush"),
                        TextWrapping = TextWrapping.WrapWholeWords
                    }
                };
                return editor;
            }

            var box = new TextBox
            {
                Text = value?.ToString() ?? string.Empty,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                TextWrapping = TextWrapping.NoWrap
            };

            box.LostFocus += (_, _) =>
            {
                property.SetValue(target, box.Text);
                SaveConfiguration();
            };

            editor = box;
        }
        else
        {
            editor = new TextBlock
            {
                Text = "This setting type is not editable yet.",
                Opacity = 0.65,
                TextWrapping = TextWrapping.Wrap
            };
        }

        return editor;
    }

    private static bool IsReadOnlySetting(object target, PropertyInfo property)
    {
        return target is SymlinksConfig && property.Name == nameof(SymlinksConfig.BaseSymlinkDirectory);
    }

    private FrameworkElement BuildListSection(object target, PropertyInfo property, bool allowAdd = true)
    {
        return Card(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                BuildListHeader(target, property),
                BuildListEditor(target, property, allowAdd)
            }
        });
    }

    private FrameworkElement BuildCollapsibleListSection(object target, PropertyInfo property, string title, string description, bool allowAdd = true)
    {
        var list = (IList?)property.GetValue(target);
        var count = list?.Count ?? 0;
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        header.Children.Add(new FontIcon
        {
            Glyph = GetConfigGlyph(property, null),
            FontSize = 19,
            Width = 28,
            VerticalAlignment = VerticalAlignment.Center
        });

        var label = new StackPanel { Spacing = 2 };
        label.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 17,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 }
        });
        label.Children.Add(new TextBlock
        {
            Text = description,
            Foreground = ResourceBrush("WinstallerSecondaryTextBrush"),
            FontSize = 12
        });
        Grid.SetColumn(label, 1);
        header.Children.Add(label);

        var countText = new TextBlock
        {
            Text = $"{count} item{(count == 1 ? string.Empty : "s")}",
            Foreground = ResourceBrush("WinstallerSecondaryTextBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(countText, 2);
        header.Children.Add(countText);

        var expander = new Expander
        {
            Header = header,
            Content = BuildListEditor(target, property, allowAdd),
            IsExpanded = count <= 8
        };

        return Card(expander);
    }

    private FrameworkElement BuildListHeader(object target, PropertyInfo property)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new FontIcon
        {
            Glyph = GetConfigGlyph(property, null),
            FontSize = 20,
            Width = 28,
            VerticalAlignment = VerticalAlignment.Center
        });

        var title = new StackPanel { Spacing = 2 };
        title.Children.Add(new TextBlock
        {
            Text = SplitName(property.Name),
            FontSize = 18,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 }
        });
        title.Children.Add(new TextBlock
        {
            Text = GetSettingDescription(property),
            Foreground = ResourceBrush("WinstallerSecondaryTextBrush"),
            FontSize = 12
        });
        Grid.SetColumn(title, 1);
        row.Children.Add(title);

        return row;
    }

    private FrameworkElement BuildListEditor(object target, PropertyInfo property, bool allowAdd = true)
    {
        var list = (IList?)property.GetValue(target);
        if (list is null)
        {
            list = (IList)Activator.CreateInstance(property.PropertyType)!;
            property.SetValue(target, list);
        }

        var itemType = property.PropertyType.GetGenericArguments()[0];
        var panel = new StackPanel { Spacing = 8 };
        var emptyText = new TextBlock { Text = "No items configured.", Opacity = 0.65 };
        var items = new ItemsRepeater
        {
            VerticalCacheLength = 0.75,
            Layout = new StackLayout { Spacing = 8 }
        };
        items.ItemTemplate = new CallbackElementFactory(data =>
        {
            var item = (IndexedItem)data!;
            return BuildListItemEditor(list, itemType, item.Index, Refresh, property);
        });
        panel.Children.Add(emptyText);
        panel.Children.Add(items);

        void Refresh()
        {
            emptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            items.ItemsSource = list.Cast<object>()
                .Select((item, index) => new IndexedItem(item, index))
                .ToList();
        }

        if (allowAdd)
            panel.Children.Add(ActionButton($"+ Add {Singularize(SplitName(property.Name))}", () =>
            {
                list.Add(CreateDefaultItem(itemType));
                SaveConfiguration();
                Refresh();
            }));

        Refresh();
        return panel;
    }

    private FrameworkElement BuildListItemEditor(IList list, Type itemType, int index, Action refresh, PropertyInfo? listProperty = null)
    {
        var item = list[index]!;
        var header = GetItemTitle(item, itemType, index);
        if (listProperty?.Name.Contains("Ignored", StringComparison.OrdinalIgnoreCase) == true)
        {
            header += " (Ignored)";
        }

        var body = new StackPanel { Spacing = 8 };
        if (itemType == typeof(string))
        {
            var box = new TextBox
            {
                Text = item.ToString() ?? string.Empty,
                PlaceholderText = "Value",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            box.LostFocus += (_, _) =>
            {
                list[index] = box.Text;
                SaveConfiguration();
            };
            body.Children.Add(box);
        }
        else
        {
            foreach (var property in itemType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.CanRead && property.CanWrite)
                {
                    body.Children.Add(BuildInlineObjectPropertyEditor(item, property));
                }
            }
        }

        var removeButton = ActionButton("Remove", () =>
        {
            list.RemoveAt(index);
            SaveConfiguration();
            refresh();
        });

        var row = new Grid { ColumnSpacing = 14 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new FontIcon
        {
            Glyph = GetConfigGlyph(listProperty, item),
            FontSize = 20,
            Width = 28,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 0, 0)
        });

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = header,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 }
        });
        content.Children.Add(body);
        Grid.SetColumn(content, 1);
        row.Children.Add(content);

        Grid.SetColumn(removeButton, 2);
        row.Children.Add(removeButton);

        return Card(row);
    }


    private FrameworkElement BuildInlineObjectPropertyEditor(object target, PropertyInfo property)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        row.Children.Add(new TextBlock
        {
            Text = SplitName(property.Name),
            VerticalAlignment = VerticalAlignment.Center
        });

        FrameworkElement editor;
        var value = property.GetValue(target);
        var nullableType = Nullable.GetUnderlyingType(property.PropertyType);
        var effectiveType = nullableType ?? property.PropertyType;

        if (effectiveType == typeof(bool))
        {
            var toggle = new ToggleSwitch { IsOn = value is true };
            toggle.Toggled += (_, _) =>
            {
                if (_isLoadingUi)
                {
                    return;
                }

                property.SetValue(target, toggle.IsOn);
                SaveConfiguration();
            };
            editor = toggle;
        }
        else if (effectiveType == typeof(int))
        {
            var box = new NumberBox
            {
                Value = value is int intValue ? intValue : double.NaN,
                PlaceholderText = nullableType is null ? "0" : "Optional",
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
            };
            box.ValueChanged += (_, args) =>
            {
                if (_isLoadingUi)
                {
                    return;
                }

                if (double.IsNaN(args.NewValue) && nullableType is not null)
                {
                    property.SetValue(target, null);
                }
                else if (!double.IsNaN(args.NewValue))
                {
                    property.SetValue(target, Convert.ToInt32(args.NewValue));
                }
                SaveConfiguration();
            };
            editor = box;
        }
        else if (effectiveType.IsEnum)
        {
            var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var enumValue in Enum.GetValues(effectiveType)) combo.Items.Add(enumValue);
            combo.SelectedItem = value;
            combo.SelectionChanged += (_, _) =>
            {
                if (!_isLoadingUi && combo.SelectedItem is not null)
                    property.SetValue(target, combo.SelectedItem);
            };
            editor = combo;
        }
        else
        {
            var box = new TextBox
            {
                Text = value?.ToString() ?? string.Empty,
                PlaceholderText = SplitName(property.Name),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            box.LostFocus += (_, _) =>
            {
                property.SetValue(target, string.IsNullOrWhiteSpace(box.Text) && nullableType is not null ? null : box.Text);
                SaveConfiguration();
            };
            editor = box;
        }

        Grid.SetColumn(editor, 1);
        row.Children.Add(editor);
        return row;
    }

    private static bool IsSupportedList(Type type)
    {
        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>);
    }

    private static object CreateDefaultItem(Type itemType)
    {
        if (itemType == typeof(string))
        {
            return string.Empty;
        }

        return Activator.CreateInstance(itemType) ?? throw new InvalidOperationException($"Could not create {itemType.Name}");
    }

    private FrameworkElement SettingCard(string title, ToggleSwitch toggle, Action<ToggleSwitch> configure)
    {
        configure(toggle);
        return new SettingsCard
        {
            Header = title,
            Content = toggle,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
    }

    private StackPanel PageTitle(string title, string subtitle)
    {
        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = 28,
                    FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 }
                },
                new TextBlock
                {
                    Text = subtitle,
                    Foreground = ResourceBrush("WinstallerSecondaryTextBrush"),
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
    }

    private TextBlock SectionTitle(string title)
    {
        return new TextBlock
        {
            Text = title,
            FontSize = 18,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 }
        };
    }

    private Button ActionButton(string text, Action action, bool primary = false)
    {
        return ActionButton(text, () =>
        {
            action();
            return Task.CompletedTask;
        }, primary);
    }

    private Button ActionButton(string text, Func<Task> action, bool primary = false)
    {
        var button = new Button
        {
            Content = text,
            Style = primary ? (Style)Application.Current.Resources["AccentButtonStyle"] : null,
            CornerRadius = new CornerRadius(4),
            MinHeight = 32,
            Padding = new Thickness(12, 6, 12, 6)
        };
        button.Click += async (_, _) =>
        {
            if (!button.IsEnabled)
                return;

            button.IsEnabled = false;
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                RunLog.WriteException("UI", $"{text} failed", ex);
                await RunOnUiThreadAsync(() => AppendOutput($"{text} failed: {ex.Message}"));
            }
            finally
            {
                await RunOnUiThreadAsync(() => button.IsEnabled = true);
            }
        };
        return button;
    }

    private Button IconActionButton(string glyph, string label, Action action)
    {
        var button = ActionButton(label, action);
        button.Content = new FontIcon { Glyph = glyph, FontSize = 16 };
        button.Width = 32;
        button.Height = 32;
        button.MinWidth = 32;
        button.Padding = new Thickness(0);
        ToolTipService.SetToolTip(button, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        return button;
    }

    private Button IconActionButton(string glyph, string label, Func<Task> action)
    {
        var button = ActionButton(label, action);
        button.Content = new FontIcon { Glyph = glyph, FontSize = 16 };
        button.Width = 32;
        button.Height = 32;
        button.MinWidth = 32;
        button.Padding = new Thickness(0);
        ToolTipService.SetToolTip(button, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        return button;
    }
    private Button TopBarActionButton(Symbol icon, string text, Action action, bool primary = false)
    {
        return TopBarActionButton(icon, text, () =>
        {
            action();
            return Task.CompletedTask;
        }, primary);
    }

    private Button TopBarActionButton(Symbol icon, string text, Func<Task> action, bool primary = false)
    {
        var label = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center
        };
        _topBarActionLabels.Add(label);

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new SymbolIcon(icon),
                label
            }
        };

        var button = new Button
        {
            Content = content,
            Style = primary ? (Style)Application.Current.Resources["AccentButtonStyle"] : null,
            CornerRadius = new CornerRadius(4),
            MinHeight = 32,
            Padding = new Thickness(10, 5, 10, 5),
            VerticalAlignment = VerticalAlignment.Center
        };
        button.IsEnabled = !_isRunning;
        ToolTipService.SetToolTip(button, text);
        button.Click += async (_, _) =>
        {
            if (_isRunning)
            {
                AppendOutput("Operation already running.");
                return;
            }

            button.IsEnabled = false;
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                RunLog.WriteException("UI", $"{text} failed", ex);
                AppendOutput($"{text} failed: {ex.Message}");
            }
            finally
            {
                await RunOnUiThreadAsync(() => button.IsEnabled = true);
            }
        };
        return button;
    }

    private void UpdateTopBarActionLabelVisibility()
    {
        var iconOnly = (GetAppWindow()?.Size.Width ?? 0) < 1260;
        foreach (var label in _topBarActionLabels)
        {
            label.Visibility = iconOnly ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private Border Card(UIElement child)
    {
        return new Border
        {
            Background = ResourceBrush("WinstallerCardBrush"),
            BorderBrush = ResourceBrush("WinstallerCardStrokeBrush"),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Child = child,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
    }
}

