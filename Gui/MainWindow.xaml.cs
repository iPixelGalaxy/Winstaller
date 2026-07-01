using System.Diagnostics;
using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Management;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using CommunityToolkit.WinUI.Controls;
using Winstaller.Models;
using Winstaller.Configuration;
using Winstaller.Modules;
using Winstaller.Utilities;
using WinRT.Interop;
using Windows.Storage;

namespace Winstaller.Gui;

public sealed partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly NavigationView _navigation = new();
    private readonly ScrollViewer _contentScroll = new();
    private readonly StackPanel _content = new();
    private readonly TextBox _outputBox = new();
    private readonly ComboBox _themeBox = new();
    private readonly Button _paneButton = new();
    private readonly Grid _titleBar = new();
    private readonly StackPanel _topBarActions = new();
    private readonly List<TextBlock> _topBarActionLabels = [];

    private WinstallerConfig _config = null!;
    private List<ModuleDescriptor> _modules = [];
    private ElementTheme _requestedTheme = ElementTheme.Default;
    private bool _isRunning;
    private bool _isLoadingUi;

    public MainWindow()
    {
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        BuildShell();

        if (BootstrapManager.TryLoad())
        {
            LoadSavedTheme();
            BootstrapManager.ImportLegacyConfigIfNeeded();
            LoadConfiguration();
            RebuildNavigation();
            RenderDashboard();
        }
        else
        {
            ApplyTheme(ElementTheme.Default);
            RenderInitialSetup();
        }
    }

    private void BuildShell()
    {
        ExtendsContentIntoTitleBar = true;

        RootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        RootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _navigation.PaneDisplayMode = NavigationViewPaneDisplayMode.Auto;
        _navigation.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
        _navigation.IsSettingsVisible = false;
        _navigation.IsPaneToggleButtonVisible = false;
        _navigation.IsTitleBarAutoPaddingEnabled = false;
        _navigation.CompactModeThresholdWidth = 1007;
        _navigation.ExpandedModeThresholdWidth = 1007;
        _navigation.OpenPaneLength = 312;
        _navigation.Header = null;
        _navigation.SelectionChanged += NavigationSelectionChanged;
        _navigation.DisplayModeChanged += NavigationDisplayModeChanged;
        SizeChanged += (_, _) =>
        {
            UpdatePaneButtonVisibility();
            UpdateTopBarActionLabelVisibility();
        };

        _content.Spacing = 12;
        _content.Padding = new Thickness(28, 22, 28, 28);
        _contentScroll.Content = _content;
        _navigation.Content = _contentScroll;

        SetTitleBar(null);
        var titleBar = BuildTitleBar();
        RootGrid.Children.Add(titleBar);
        Grid.SetRow(_navigation, 1);
        RootGrid.Children.Add(_navigation);
    }

    private Grid BuildTitleBar()
    {
        _titleBar.Height = 48;
        _titleBar.Padding = new Thickness(4, 0, 152, 0);
        _titleBar.Background = ResourceBrush("WinstallerTitleBarBrush");
        _titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _paneButton.Content = new FontIcon { Glyph = "\uE700", FontSize = 16 };
        _paneButton.Width = 44;
        _paneButton.Height = 40;
        _paneButton.Padding = new Thickness(0);
        _paneButton.Visibility = Visibility.Collapsed;
        _paneButton.Click += (_, _) => _navigation.IsPaneOpen = !_navigation.IsPaneOpen;
        ToolTipService.SetToolTip(_paneButton, "Toggle menu");

        var title = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        title.Children.Add(new Image
        {
            Width = 18,
            Height = 18,
            Source = new BitmapImage(new Uri("ms-appx:///Assets/Winstaller-Icon3.png")),
            Stretch = Stretch.Uniform,
            HighContrastAdjustment = ElementHighContrastAdjustment.None
        });
        title.Children.Add(new TextBlock
        {
            Text = "Winstaller",
            FontSize = 16,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 },
            VerticalAlignment = VerticalAlignment.Center
        });

        _topBarActions.Orientation = Orientation.Horizontal;
        _topBarActions.Spacing = 8;
        _topBarActions.Margin = new Thickness(222, 0, 0, 0);
        _topBarActions.VerticalAlignment = VerticalAlignment.Center;

        var themePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        themePanel.Children.Add(new TextBlock
        {
            Text = "Theme:",
            VerticalAlignment = VerticalAlignment.Center
        });

        _themeBox.MinWidth = 128;
        _themeBox.VerticalAlignment = VerticalAlignment.Center;
        _themeBox.Items.Add("System");
        _themeBox.Items.Add("Light");
        _themeBox.Items.Add("Dark");
        _themeBox.SelectedIndex = 0;
        _themeBox.SelectionChanged += (_, _) =>
        {
            _requestedTheme = _themeBox.SelectedIndex switch
            {
                1 => ElementTheme.Light,
                2 => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
            BootstrapManager.SaveTheme(GetThemeName(_requestedTheme));
            ConfigurationManager.SaveTheme(GetThemeName(_requestedTheme));
            ApplyTheme(_requestedTheme);
        };
        themePanel.Children.Add(_themeBox);

        Grid.SetColumn(_paneButton, 0);
        Grid.SetColumn(title, 1);
        Grid.SetColumn(_topBarActions, 2);
        Grid.SetColumn(themePanel, 4);
        _titleBar.Children.Add(_paneButton);
        _titleBar.Children.Add(title);
        _titleBar.Children.Add(_topBarActions);
        _titleBar.Children.Add(themePanel);
        return _titleBar;
    }

    private void NavigationDisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
    {
        UpdatePaneButtonVisibility();
    }

    private void UpdatePaneButtonVisibility()
    {
        _paneButton.Visibility =
            _navigation.DisplayMode == NavigationViewDisplayMode.Compact ||
            _navigation.DisplayMode == NavigationViewDisplayMode.Minimal ||
            (GetAppWindow()?.Size.Width ?? 0) < _navigation.ExpandedModeThresholdWidth
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void LoadConfiguration()
    {
        _config = ConfigurationManager.LoadConfiguration();
        _modules =
        [
            new("Network Drives", "Map configured network drives", "\uE839", _config.NetworkDrives, () => new NetworkDrivesModule(_config), SystemInfoImportScope.NetworkDrives),
            new("Symlinks", "Restore configured profile symlinks", "\uE71B", _config.Symlinks, () => new SymlinksModule(_config), SystemInfoImportScope.Symlinks),
            new("App Installer", "Install configured applications", "\uE896", _config.AppInstaller, () => new AppInstallerModule(_config), SystemInfoImportScope.AppInstaller),
            new("Fonts", "Install configured fonts", "\uE8D2", _config.Fonts, () => new FontsModule(_config), null),
            new("Shell Folders", "Configure user shell folders", "\uE8B7", _config.ShellFolders, () => new ShellFoldersModule(_config), SystemInfoImportScope.ShellFolders),
            new("Registry", "Apply registry files and changes", "\uE7B8", _config.Registry, () => new RegistryModule(_config), null),
            new("File Copy", "Run configured copy operations", "\uE8C8", _config.FileCopy, () => new FileCopyModule(_config), null),
            new("Startup", "Configure startup programs and processes", "\uE768", _config.Startup, () => new StartupModule(_config), SystemInfoImportScope.Startup),
            new("Path", "Configure PATH additions", "\uE943", _config.Path, () => new PathModule(_config), SystemInfoImportScope.Path),
            new("Discord", "Install and patch Discord", "\uE716", _config.Discord, () => new DiscordModule(_config), null),
            new("Spotify", "Install and patch Spotify", "\uE768", _config.Spotify, () => new SpotifyModule(_config), null),
        ];
    }

    private void NavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is ModuleDescriptor module)
        {
            RenderModule(module);
            return;
        }

        RenderDashboard();
    }

    private void RenderInitialSetup()
    {
        ClearTopBarActions();
        _navigation.MenuItems.Clear();
        _content.Children.Clear();
        _content.Children.Add(PageTitle("Initial setup", "Choose the drive where Winstaller should store configs and managed data."));

        var internalDriveRoots = GetInternalFixedDriveRoots();
        var drives = DriveInfo.GetDrives()
            .Where(drive => drive.IsReady &&
                            drive.DriveType == DriveType.Fixed &&
                            internalDriveRoots.Contains(drive.Name, StringComparer.OrdinalIgnoreCase))
            .OrderBy(drive => drive.Name)
            .ToList();

        var list = new StackPanel { Spacing = 8 };
        foreach (var drive in drives)
        {
            list.Children.Add(DriveCard(drive));
        }

        _content.Children.Add(Card(list));
    }

    private FrameworkElement DriveCard(DriveInfo drive)
    {
        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new FontIcon
        {
            Glyph = "\uEDA2",
            FontSize = 22,
            Width = 32,
            VerticalAlignment = VerticalAlignment.Center
        });

        var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel;
        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(new TextBlock
        {
            Text = $"{drive.Name.TrimEnd('\\')} {label}",
            FontSize = 16,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 }
        });
        text.Children.Add(new TextBlock
        {
            Text = $"{FormatBytes(drive.AvailableFreeSpace)} free of {FormatBytes(drive.TotalSize)}",
            Foreground = ResourceBrush("WinstallerSecondaryTextBrush")
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var chevron = new FontIcon
        {
            Glyph = "\uE76C",
            Opacity = 0.72,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(chevron, 2);
        grid.Children.Add(chevron);

        var card = Card(grid);
        card.Tapped += (_, _) =>
        {
            BootstrapManager.Initialize(drive.Name);
            BootstrapManager.SaveTheme(GetThemeName(_requestedTheme));
            LoadConfiguration();
            RebuildNavigation();
            RenderDashboard();
        };
        return card;
    }

    private void RenderDashboard()
    {
        _isLoadingUi = true;
        SetDashboardTopBarActions();
        _content.Children.Clear();
        _content.Children.Add(PageTitle("Dashboard", "Choose what Winstaller should restore or install."));

        var list = new StackPanel { Spacing = 8 };
        foreach (var module in _modules)
        {
            list.Children.Add(ModuleCard(module));
        }
        _content.Children.Add(list);

        _content.Children.Add(OutputCard());
        _isLoadingUi = false;
    }

    private void RenderModule(ModuleDescriptor module)
    {
        _isLoadingUi = true;
        SetModuleTopBarActions(module);
        _content.Children.Clear();
        _content.Children.Add(ModulePageHeader(module));

        _content.Children.Add(BuildConfigEditor(module.Config));
        _content.Children.Add(OutputCard());
        _isLoadingUi = false;
    }

    private void SetDashboardTopBarActions()
    {
        _topBarActions.Children.Clear();
        _topBarActionLabels.Clear();
        _topBarActions.Children.Add(TopBarActionButton(Symbol.Play, "Run All Enabled", async () => await ConfirmAndRunModulesAsync(_modules.Where(m => m.IsEnabled).ToList()), primary: true));
        _topBarActions.Children.Add(TopBarActionButton(Symbol.Download, "Import System Info", async () => await ImportSystemInfoAsync(SystemInfoImportScope.All)));
        _topBarActions.Children.Add(TopBarActionButton(Symbol.Refresh, "Reload All Configs", () =>
        {
            LoadConfiguration();
            RebuildNavigation();
            RenderDashboard();
            AppendOutput("Configuration reloaded.");
        }));
        _topBarActions.Children.Add(TopBarActionButton(Symbol.Folder, "Open Config Directory", OpenConfig));
        UpdateTopBarActionLabelVisibility();
    }

    private void SetModuleTopBarActions(ModuleDescriptor module)
    {
        _topBarActions.Children.Clear();
        _topBarActionLabels.Clear();
        _topBarActions.Children.Add(TopBarActionButton(Symbol.Play, "Run This Module", async () => await ConfirmAndRunModulesAsync([module]), primary: true));
        if (module.ImportScope is { } importScope)
        {
            _topBarActions.Children.Add(TopBarActionButton(Symbol.Download, "Import System Info", async () => await ImportSystemInfoAsync(importScope, module)));
        }
        _topBarActions.Children.Add(TopBarActionButton(Symbol.Save, "Save Config", SaveConfiguration));
        _topBarActions.Children.Add(TopBarActionButton(Symbol.Folder, "Open Config Directory", OpenConfig));
        UpdateTopBarActionLabelVisibility();
    }

    private void ClearTopBarActions()
    {
        _topBarActions.Children.Clear();
        _topBarActionLabels.Clear();
    }

    private FrameworkElement ModulePageHeader(ModuleDescriptor module)
    {
        var panel = new StackPanel { Spacing = 4 };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center
        };

        row.Children.Add(new FontIcon
        {
            Glyph = module.IconGlyph,
            FontSize = 24,
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(new TextBlock
        {
            Text = module.Name,
            FontSize = 28,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 },
            VerticalAlignment = VerticalAlignment.Center
        });

        var toggle = new ToggleSwitch
        {
            IsOn = module.IsEnabled,
            OffContent = string.Empty,
            OnContent = string.Empty,
            VerticalAlignment = VerticalAlignment.Center
        };
        toggle.Toggled += (_, _) =>
        {
            if (_isLoadingUi)
            {
                return;
            }

            module.SetEnabled(toggle.IsOn);
            SaveConfiguration();
        };
        row.Children.Add(toggle);

        panel.Children.Add(row);
        panel.Children.Add(new TextBlock
        {
            Text = module.Description,
            Foreground = ResourceBrush("WinstallerSecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap
        });
        return panel;
    }

    private FrameworkElement ModuleCard(ModuleDescriptor module)
    {
        var action = new Grid { ColumnSpacing = 2 };
        action.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        action.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var toggle = new ToggleSwitch
        {
            IsOn = module.IsEnabled,
            OffContent = string.Empty,
            OnContent = string.Empty,
            VerticalAlignment = VerticalAlignment.Center
        };
        toggle.Toggled += (_, _) =>
        {
            if (_isLoadingUi)
            {
                return;
            }

            module.SetEnabled(toggle.IsOn);
            SaveConfiguration();
        };
        toggle.Tapped += (sender, args) => args.Handled = true;
        Grid.SetColumn(toggle, 0);
        action.Children.Add(toggle);

        var chevron = new FontIcon
        {
            Glyph = "\uE76C",
            FontSize = 13,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(chevron, 1);
        action.Children.Add(chevron);

        var card = new SettingsCard
        {
            Header = module.Name,
            Description = module.Description,
            HeaderIcon = new FontIcon { Glyph = module.IconGlyph },
            Content = action,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsClickEnabled = false,
            Background = ResourceBrush("WinstallerDashboardCardBrush"),
            BorderThickness = new Thickness(0)
        };
        card.PointerEntered += (_, _) =>
        {
            card.Background = ResourceBrush("WinstallerDashboardCardHoverBrush");
        };
        card.PointerExited += (_, _) =>
        {
            card.Background = ResourceBrush("WinstallerDashboardCardBrush");
        };
        card.Tapped += (_, _) => SelectModule(module);
        return card;
    }

    private FrameworkElement BuildConfigEditor(object config)
    {
        var panel = new StackPanel { Spacing = 10 };
        foreach (var property in config.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite || property.Name == "Enabled")
            {
                continue;
            }

            panel.Children.Add(BuildPropertyEditor(config, property));
        }

        return Card(new StackPanel
        {
            Spacing = 14,
            Children =
            {
                SectionTitle("Configuration"),
                panel
            }
        });
    }

    private FrameworkElement BuildPropertyEditor(object target, PropertyInfo property)
    {
        var value = property.GetValue(target);
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
            var box = new TextBox
            {
                Text = value?.ToString() ?? string.Empty,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            box.LostFocus += (_, _) =>
            {
                property.SetValue(target, box.Text);
                SaveConfiguration();
            };
            editor = box;
        }
        else if (IsSupportedList(property.PropertyType))
        {
            editor = BuildListEditor(target, property);
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

        Grid.SetColumn(editor, 1);
        row.Children.Add(editor);
        return row;
    }

    private FrameworkElement BuildListEditor(object target, PropertyInfo property)
    {
        var list = (IList?)property.GetValue(target);
        if (list is null)
        {
            list = (IList)Activator.CreateInstance(property.PropertyType)!;
            property.SetValue(target, list);
        }

        var itemType = property.PropertyType.GetGenericArguments()[0];
        var panel = new StackPanel { Spacing = 8 };

        void Refresh()
        {
            panel.Children.Clear();

            if (list.Count == 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "No items configured.",
                    Opacity = 0.65
                });
            }

            for (var index = 0; index < list.Count; index++)
            {
                panel.Children.Add(BuildListItemEditor(list, itemType, index, Refresh));
            }

            panel.Children.Add(ActionButton($"+ Add {Singularize(SplitName(property.Name))}", () =>
            {
                list.Add(CreateDefaultItem(itemType));
                SaveConfiguration();
                Refresh();
            }));
        }

        Refresh();
        return panel;
    }

    private FrameworkElement BuildListItemEditor(IList list, Type itemType, int index, Action refresh)
    {
        var item = list[index]!;
        var header = itemType == typeof(string)
            ? $"Item {index + 1}"
            : $"{SplitName(itemType.Name)} {index + 1}";

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

        body.Children.Add(ActionButton("Remove", () =>
        {
            list.RemoveAt(index);
            SaveConfiguration();
            refresh();
        }));

        return new SettingsExpander
        {
            Header = header,
            IsExpanded = true,
            Content = body
        };
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

    private FrameworkElement OutputCard()
    {
        _outputBox.AcceptsReturn = true;
        _outputBox.IsReadOnly = true;
        _outputBox.TextWrapping = TextWrapping.NoWrap;
        _outputBox.FontFamily = new FontFamily("Cascadia Mono");
        _outputBox.MinHeight = 180;
        _outputBox.MaxHeight = 320;
        return Card(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                SectionTitle("Output"),
                _outputBox
            }
        });
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
        button.Click += async (_, _) => await action();
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
        ToolTipService.SetToolTip(button, text);
        button.Click += async (_, _) => await action();
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

    private void RebuildNavigation()
    {
        _navigation.MenuItems.Clear();
        _navigation.MenuItems.Add(new NavigationViewItem
        {
            Content = "Dashboard",
            Icon = new SymbolIcon(Symbol.Home),
            Tag = "dashboard"
        });
        _navigation.MenuItems.Add(new NavigationViewItemSeparator());
        foreach (var module in _modules)
        {
            _navigation.MenuItems.Add(new NavigationViewItem
            {
                Content = module.Name,
                Icon = new FontIcon { Glyph = module.IconGlyph },
                Tag = module
            });
        }
    }

    private void SelectModule(ModuleDescriptor module)
    {
        foreach (var item in _navigation.MenuItems.OfType<NavigationViewItem>())
        {
            if (ReferenceEquals(item.Tag, module))
            {
                _navigation.SelectedItem = item;
                RenderModule(module);
                return;
            }
        }
    }

    private async Task ConfirmAndRunModulesAsync(IReadOnlyList<ModuleDescriptor> modules)
    {
        if (modules.Count == 0)
        {
            AppendOutput("No modules selected.");
            return;
        }

        var moduleNames = string.Join(Environment.NewLine, modules.Select(module => $"- {module.Name}"));
        var confirmed = await ConfirmAsync(
            modules.Count == 1 ? "Run module?" : "Run enabled modules?",
            modules.Count == 1
                ? $"This will run {modules[0].Name}."
                : $"This will run {modules.Count} module(s):{Environment.NewLine}{moduleNames}",
            "Run");

        if (confirmed)
        {
            await RunModulesAsync(modules);
        }
    }

    private async Task ImportSystemInfoAsync(SystemInfoImportScope scope, ModuleDescriptor? module = null)
    {
        AppendOutput(module is null ? "Scanning system info..." : $"Scanning system info for {module.Name}...");
        var candidates = await SystemInfoImportService.FindCandidatesAsync(_config, scope);
        if (candidates.Count == 0)
        {
            await ShowMessageAsync("Import System Info", "No new system info was found for this scope.");
            AppendOutput("No new system info found.");
            return;
        }

        var selected = await ShowImportReviewDialogAsync(candidates);
        if (selected.Count == 0)
        {
            AppendOutput("Import cancelled.");
            return;
        }

        var added = SystemInfoImportService.ApplyCandidates(_config, selected);
        SaveConfiguration();
        LoadConfiguration();
        RebuildNavigation();
        if (module is null)
        {
            RenderDashboard();
        }
        else
        {
            var refreshedModule = _modules.FirstOrDefault(candidate => candidate.Name == module.Name);
            RenderModule(refreshedModule ?? module);
        }

        AppendOutput($"Imported {added} item(s).");
    }

    private async Task<List<SystemInfoImportCandidate>> ShowImportReviewDialogAsync(IReadOnlyList<SystemInfoImportCandidate> candidates)
    {
        var selected = new List<SystemInfoImportCandidate>();
        var panel = new StackPanel { Spacing = 12 };

        foreach (var group in candidates.GroupBy(candidate => candidate.Scope))
        {
            panel.Children.Add(new TextBlock
            {
                Text = SplitName(group.Key.ToString()),
                FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 }
            });

            foreach (var candidate in group)
            {
                var checkBox = new CheckBox
                {
                    IsChecked = true,
                    Tag = candidate,
                    Content = new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            new TextBlock { Text = candidate.Title, TextWrapping = TextWrapping.Wrap },
                            new TextBlock
                            {
                                Text = candidate.Detail,
                                Foreground = ResourceBrush("WinstallerSecondaryTextBrush"),
                                FontSize = 12,
                                TextWrapping = TextWrapping.Wrap
                            }
                        }
                    }
                };
                panel.Children.Add(checkBox);
            }
        }

        var scroll = new ScrollViewer
        {
            Content = panel,
            MaxHeight = 520
        };

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = $"Import {candidates.Count} system item(s)?",
            Content = scroll,
            PrimaryButtonText = "Import Selected",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return selected;

        foreach (var checkBox in panel.Children.OfType<CheckBox>())
        {
            if (checkBox.IsChecked == true && checkBox.Tag is SystemInfoImportCandidate candidate)
            {
                selected.Add(candidate);
            }
        }

        return selected;
    }

    private async Task<bool> ConfirmAsync(string title, string message, string primaryText)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = primaryText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "OK"
        };

        await dialog.ShowAsync();
    }
    private async Task RunModulesAsync(IReadOnlyList<ModuleDescriptor> modules)
    {
        if (_isRunning)
        {
            return;
        }

        if (modules.Count == 0)
        {
            AppendOutput("No modules selected.");
            return;
        }

        _isRunning = true;
        AppendOutput($"Starting {modules.Count} module(s).");

        var originalOut = Console.Out;
        var originalError = Console.Error;
        var originalIn = Console.In;
        using var writer = new TextBoxWriter(AppendOutputText);
        using var reader = new StringReader(string.Join(Environment.NewLine, Enumerable.Repeat("n", 100)));

        try
        {
            Console.SetOut(writer);
            Console.SetError(writer);
            Console.SetIn(reader);

            foreach (var descriptor in modules)
            {
                AppendOutput("");
                AppendOutput($"== {descriptor.Name} ==");
                try
                {
                    var succeeded = await descriptor.CreateModule().ExecuteAsync();
                    AppendOutput(succeeded ? "Completed successfully." : "Completed with errors.");
                }
                catch (Exception ex)
                {
                    AppendOutput($"Failed: {ex.Message}");
                }
            }
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Console.SetIn(originalIn);
            _isRunning = false;
            AppendOutput("Run finished.");
        }
    }

    private void OpenConfig()
    {
        if (!Directory.Exists(ConfigurationManager.ConfigDirectory))
        {
            SaveConfiguration();
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = ConfigurationManager.ConfigDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppendOutput($"Failed to open configuration: {ex.Message}");
        }
    }

    private void SaveConfiguration()
    {
        ConfigurationManager.SaveConfiguration(_config);
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
        AppendOutputText(text + Environment.NewLine);
    }

    private void AppendOutputText(string text)
    {
        _outputBox.Text += text;
        _outputBox.SelectionStart = _outputBox.Text.Length;
    }

    private static string SplitName(string value)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (i > 0 && char.IsUpper(value[i]) && !char.IsUpper(value[i - 1]))
            {
                builder.Append(' ');
            }
            builder.Append(value[i]);
        }
        return builder.ToString();
    }

    private static string Singularize(string value)
    {
        return value.EndsWith('s') && value.Length > 1 ? value[..^1] : value;
    }

    private sealed record ModuleDescriptor(
        string Name,
        string Description,
        string IconGlyph,
        object Config,
        Func<IModule> CreateModule,
        SystemInfoImportScope? ImportScope)
    {
        public bool IsEnabled => Config.GetType().GetProperty("Enabled")?.GetValue(Config) is true;

        public void SetEnabled(bool enabled)
        {
            Config.GetType().GetProperty("Enabled")?.SetValue(Config, enabled);
        }
    }

    private sealed class TextBoxWriter(Action<string> write) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            write(value.ToString());
        }

        public override void Write(string? value)
        {
            if (value is not null)
            {
                write(value);
            }
        }

        public override void WriteLine(string? value)
        {
            write((value ?? string.Empty) + Environment.NewLine);
        }
    }
}
