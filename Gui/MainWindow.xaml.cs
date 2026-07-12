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
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using CommunityToolkit.WinUI.Controls;
using Winstaller.Models;
using Winstaller.Configuration;
using Winstaller.Modules;
using Winstaller.Utilities;
using WinRT.Interop;

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
    private readonly ComboBox _themeBox = new();
    private readonly Button _paneButton = new();
    private readonly Grid _titleBar = new();
    private readonly ProgressBar _busyBar = new();
    private readonly StackPanel _topBarActions = new();
    private readonly List<TextBlock> _topBarActionLabels = [];

    private WinstallerConfig _config = null!;
    private List<ModuleDescriptor> _modules = [];
    private ElementTheme _requestedTheme = ElementTheme.Default;
    private TextBox? _activeOutputBox;
    private readonly object _outputLock = new();
    private readonly StringBuilder _pendingOutputText = new();
    private bool _outputFlushQueued;
    private int _busyDepth;
    private bool _isRunning;
    private bool _isLoadingUi;

    public MainWindow()
    {
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        BuildShell();
        RegisterCloseGuard();

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
        RootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });
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

        _busyBar.IsIndeterminate = true;
        _busyBar.Visibility = Visibility.Collapsed;
        _busyBar.MinHeight = 4;
        _busyBar.VerticalAlignment = VerticalAlignment.Stretch;
        Grid.SetRow(_busyBar, 1);
        RootGrid.Children.Add(_busyBar);

        Grid.SetRow(_navigation, 2);
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
        _topBarActions.Margin = new Thickness(198, 0, 0, 0);
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
            new("Symlinks", "Restore configured profile symlinks", "\uE71B", _config.Symlinks, () => new SymlinksModule(_config), SystemInfoImportScope.Symlinks),
            new("App Installer", "Install configured applications", "\uE896", _config.AppInstaller, () => new AppInstallerModule(_config), SystemInfoImportScope.AppInstaller),
            new("Fonts", "Install configured fonts", "\uE8D2", _config.Fonts, () => new FontsModule(_config), SystemInfoImportScope.Fonts),
            new("Shell Folders", "Configure user shell folders", "\uE8B7", _config.ShellFolders, () => new ShellFoldersModule(_config), SystemInfoImportScope.ShellFolders),
            new("Path", "Configure PATH additions", "\uE943", _config.Path, () => new PathModule(_config), SystemInfoImportScope.Path),
            new("Network Drives", "Map configured network drives", "\uE839", _config.NetworkDrives, () => new NetworkDrivesModule(_config), SystemInfoImportScope.NetworkDrives),
            new("Registry", "Apply registry files and changes", "\uE7B8", _config.Registry, () => new RegistryModule(_config), null),
            new("File Copy", "Run configured copy operations", "\uE8C8", _config.FileCopy, () => new FileCopyModule(_config), null),
            new("Startup", "Configure startup programs and processes", "\uE768", _config.Startup, () => new StartupModule(_config), SystemInfoImportScope.Startup),
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
            _ = ShowGuidedSetupPromptAsync();
        };
        return card;
    }

    private void RenderDashboard()
    {
        _isLoadingUi = true;
        SetDashboardTopBarActions();
        _content.Children.Clear();
        _content.Children.Add(PageTitle("Dashboard", "Choose what Winstaller should restore or install."));
        _content.Children.Add(GuidedSetupCard());

        var list = new StackPanel { Spacing = 8 };
        foreach (var module in _modules)
        {
            list.Children.Add(ModuleCard(module));
        }
        _content.Children.Add(list);

        _isLoadingUi = false;
    }

    private void RenderModule(ModuleDescriptor module)
    {
        _isLoadingUi = true;
        SetModuleTopBarActions(module);
        _content.Children.Clear();
        _content.Children.Add(ModulePageHeader(module));

        _content.Children.Add(BuildModuleContent(module));
        _isLoadingUi = false;
    }

    private void SetDashboardTopBarActions()
    {
        _topBarActions.Children.Clear();
        _topBarActionLabels.Clear();
        _topBarActions.Children.Add(TopBarActionButton(Symbol.Play, "Run All Enabled", async () => await ConfirmAndRunModulesAsync(_modules.Where(m => m.IsEnabled).ToList()), primary: true));
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
            _topBarActions.Children.Add(TopBarActionButton(Symbol.Download, GetImportLabel(module), async () => await ImportSystemInfoAsync(importScope, module)));
        }
        if (HasIgnoredItems(module.Config))
        {
            _topBarActions.Children.Add(TopBarActionButton(Symbol.List, "Ignored Items", async () => await ShowIgnoredItemsAsync(module)));
        }
        _topBarActions.Children.Add(TopBarActionButton(Symbol.Setting, "Module Settings", async () => await ShowModuleSettingsAsync(module)));
        _topBarActions.Children.Add(TopBarActionButton(Symbol.Folder, "Open Config Directory", OpenConfig));
        UpdateTopBarActionLabelVisibility();
    }

    private FrameworkElement GuidedSetupCard()
    {
        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new FontIcon { Glyph = "\uE9D9", FontSize = 22, Width = 28, VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = "Guided setup", FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 } });
        text.Children.Add(new TextBlock { Text = "Walk through standard restore setup.", Foreground = ResourceBrush("WinstallerSecondaryTextBrush") });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var button = ActionButton("Start", async () => await RunGuidedSetupAsync(), primary: true);
        Grid.SetColumn(button, 2);
        grid.Children.Add(button);
        return Card(grid);
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

    private FrameworkElement BuildModuleContent(ModuleDescriptor module)
    {
        if (module.Config is AppInstallerConfig appInstaller)
        {
            return BuildAppInstallerTiles(appInstaller);
        }
        if (module.Config is ShellFoldersConfig shellFolders)
        {
            return BuildShellFoldersContent(shellFolders);
        }
        if (module.Config is SymlinksConfig symlinks)
        {
            return BuildSymlinksContent(symlinks);
        }

        return BuildConfigEditor(module.Config, includeScalarSettings: false);
    }

    private FrameworkElement BuildAppInstallerTiles(AppInstallerConfig config)
    {
        var grid = new VariableSizedWrapGrid
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left
        };

        void Refresh()
        {
            grid.Children.Clear();
            foreach (var app in config.DefaultInstalls.OrderBy(app => GetAppDisplayName(config, app), StringComparer.OrdinalIgnoreCase))
                grid.Children.Add(BuildAppTile(app, config, Refresh));
        }

        Refresh();
        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                grid,
                ActionButton("+ Add App", async () => await ShowAppBehaviorDialogAsync(config, null))
            }
        };
    }

    private FrameworkElement BuildAppTile(string packageId, AppInstallerConfig config, Action refresh)
    {
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children =
            {
                IconActionButton("\uE74D", "Delete app", () =>
                {
                    config.DefaultInstalls.RemoveAll(id => id.Equals(packageId, StringComparison.OrdinalIgnoreCase));
                    config.Behaviors.Remove(packageId);
                    SaveConfiguration();
                    refresh();
                }),
                IconActionButton("\uE713", "App settings", async () => await ShowAppBehaviorDialogAsync(config, packageId))
            }
        };

        var iconHost = new Grid { Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Left };
        var fallback = new FontIcon { Glyph = "\uE896", FontSize = 26, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var icon = new Image { Width = 40, Height = 40, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
        iconHost.Children.Add(fallback);
        iconHost.Children.Add(icon);
        _ = LoadAppIconAsync(packageId, icon, fallback);

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new StackPanel { Spacing = 8 };
        header.Children.Add(iconHost);
        header.Children.Add(new TextBlock
        {
            Text = GetAppDisplayName(config, packageId),
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 },
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(header);
        Grid.SetRow(footer, 1);
        content.Children.Add(footer);

        return new Border
        {
            Width = 250,
            MinHeight = 118,
            Margin = new Thickness(0, 0, 8, 8),
            Background = ResourceBrush("WinstallerCardBrush"),
            BorderBrush = ResourceBrush("WinstallerCardStrokeBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14),
            Child = content
        };
    }

    private async Task LoadAppIconAsync(string packageId, Image icon, FontIcon fallback)
    {
        try
        {
            var path = await AppIconService.GetIconPathAsync(packageId);
            if (string.IsNullOrWhiteSpace(path))
                return;
            await RunOnUiThreadAsync(() =>
            {
                icon.Source = new BitmapImage(new Uri(path));
                icon.Visibility = Visibility.Visible;
                fallback.Visibility = Visibility.Collapsed;
            });
        }
        catch (Exception ex)
        {
            RunLog.WriteException("AppIcon", $"Failed loading icon for {packageId}", ex);
        }
    }

    private static string GetAppDisplayName(AppInstallerConfig config, string packageId)
    {
        return config.Behaviors.TryGetValue(packageId, out var behavior) && !string.IsNullOrWhiteSpace(behavior.DisplayName)
            ? behavior.DisplayName
            : GetKnownPackageName(packageId);
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

    private FrameworkElement BuildSymlinksContent(SymlinksConfig config)
    {
        var grid = new Grid { ColumnSpacing = 18 };
        for (var i = 0; i < 4; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var roaming = BuildSymlinkColumn(config, typeof(SymlinksConfig).GetProperty(nameof(SymlinksConfig.RoamingDirectories))!, "Roaming", "Microsoft\\VisualStudio");
        var local = BuildSymlinkColumn(config, typeof(SymlinksConfig).GetProperty(nameof(SymlinksConfig.LocalDirectories))!, "Local", "Microsoft\\VisualStudio");
        var localLow = BuildSymlinkColumn(config, typeof(SymlinksConfig).GetProperty(nameof(SymlinksConfig.LocalLowDirectories))!, "LocalLow", "Company\\Game");
        var special = BuildSymlinkColumn(config, typeof(SymlinksConfig).GetProperty(nameof(SymlinksConfig.SpecialSymlinks))!, "Special", string.Empty);

        Grid.SetColumn(roaming, 0);
        Grid.SetColumn(local, 1);
        Grid.SetColumn(localLow, 2);
        Grid.SetColumn(special, 3);
        grid.Children.Add(roaming);
        grid.Children.Add(local);
        grid.Children.Add(localLow);
        grid.Children.Add(special);
        return grid;
    }

    private FrameworkElement BuildSymlinkColumn(SymlinksConfig config, PropertyInfo property, string title, string placeholder)
    {
        var list = (IList?)property.GetValue(config);
        if (list is null)
        {
            list = (IList)Activator.CreateInstance(property.PropertyType)!;
            property.SetValue(config, list);
        }

        var itemType = property.PropertyType.GetGenericArguments()[0];
        var panel = new StackPanel { Spacing = 8 };
        var countText = new TextBlock
        {
            Foreground = ResourceBrush("WinstallerSecondaryTextBrush"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };

        void Refresh()
        {
            panel.Children.Clear();
            countText.Text = $"{list.Count} item{(list.Count == 1 ? string.Empty : "s")}";

            if (list.Count == 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "No items configured.",
                    Opacity = 0.65,
                    FontSize = 12
                });
            }

            for (var index = 0; index < list.Count; index++)
            {
                panel.Children.Add(itemType == typeof(string)
                    ? BuildCompactStringListItem(config, property, list, index, Refresh, placeholder)
                    : itemType == typeof(SpecialSymlink)
                        ? BuildCompactSpecialSymlinkItem(config, list, index, Refresh)
                        : BuildListItemEditor(list, itemType, index, Refresh, property));
            }

            panel.Children.Add(ActionButton($"+ Add {title}", () =>
            {
                list.Add(CreateDefaultItem(itemType));
                SaveConfiguration();
                Refresh();
            }));
        }

        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(new FontIcon
        {
            Glyph = GetConfigGlyph(property, null),
            FontSize = 18,
            Width = 24,
            VerticalAlignment = VerticalAlignment.Center
        });
        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 17,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 }
        });
        text.Children.Add(countText);
        Grid.SetColumn(text, 1);
        header.Children.Add(text);

        Refresh();
        return new StackPanel
        {
            Spacing = 10,
            Children = { header, panel }
        };
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
                panel.Children.Add(BuildListItemEditor(list, itemType, index, Refresh, property));
            }

            if (allowAdd)
            {
                panel.Children.Add(ActionButton($"+ Add {Singularize(SplitName(property.Name))}", () =>
                {
                    list.Add(CreateDefaultItem(itemType));
                    SaveConfiguration();
                    Refresh();
                }));
            }
        }

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

    private FrameworkElement BuildCompactStringListItem(SymlinksConfig config, PropertyInfo property, IList list, int index, Action refresh, string placeholder)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var value = list[index]?.ToString() ?? string.Empty;
        Button? iconButton = null;
        TextBox? box = null;
        iconButton = SymlinkOpenButton(
            "\uE8B7",
            async () =>
            {
                var currentValue = list[index]?.ToString() ?? string.Empty;
                if (IsShiftDown())
                {
                    OpenFolder(GetManagedAppDataSymlinkTarget(config, property.Name, currentValue));
                    return;
                }

                var picked = await PickSymlinkPathAsync(GetAppDataRootForProperty(property.Name), currentValue);
                if (picked is null)
                    return;

                await RunOnUiThreadAsync(() =>
                {
                    list[index] = picked;
                    if (box is not null)
                        box.Text = picked;
                    if (iconButton is not null)
                        iconButton.Content = new FontIcon { Glyph = "\uE8B7", FontSize = 16 };
                    SaveConfiguration();
                });
            });
        row.Children.Add(iconButton);

        box = new TextBox
        {
            Text = value,
            PlaceholderText = placeholder,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 0
        };
        void SaveBox()
        {
            list[index] = box.Text.Trim();
            iconButton.Content = new FontIcon { Glyph = "\uE8B7", FontSize = 16 };
            SaveConfiguration();
            DefocusTextBox(box);
        }
        box.LostFocus += (_, _) => SaveBox();
        box.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter)
            {
                SaveBox();
                args.Handled = true;
            }
        };
        Grid.SetColumn(box, 1);
        row.Children.Add(box);

        var removeButton = CompactRemoveButton(() =>
        {
            list.RemoveAt(index);
            SaveConfiguration();
            refresh();
        });
        Grid.SetColumn(removeButton, 2);
        row.Children.Add(removeButton);

        return row;
    }

    private FrameworkElement BuildCompactSpecialSymlinkItem(SymlinksConfig config, IList list, int index, Action refresh)
    {
        var symlink = (SpecialSymlink)list[index]!;
        var outer = new Grid { ColumnSpacing = 8 };
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Button? sourceButton = null;
        Button? targetButton = null;
        TextBox? sourceBox = null;
        TextBox? targetBox = null;
        ToggleSwitch? typeToggle = null;

        sourceButton = SymlinkOpenButton(
            GetSpecialSymlinkGlyph(symlink),
            async () =>
            {
                if (IsShiftDown())
                {
                    OpenFolder(ExpandConfigPath(symlink.Source));
                    return;
                }

                var picked = await PickPathForTypeAsync(symlink.IsDirectory, symlink.Source, "Select symlink source");
                if (picked is null)
                    return;

                await RunOnUiThreadAsync(() =>
                {
                    symlink.Source = picked;
                    if (sourceBox is not null)
                        sourceBox.Text = picked;
                    if (sourceButton is not null)
                        sourceButton.Content = new FontIcon { Glyph = GetSpecialSymlinkGlyph(symlink), FontSize = 16 };
                    if (targetButton is not null)
                        targetButton.Content = new FontIcon { Glyph = GetSpecialSymlinkGlyph(symlink), FontSize = 16 };
                    if (typeToggle is not null)
                        typeToggle.IsOn = symlink.IsDirectory;
                    SaveConfiguration();
                });
            });
        targetButton = SymlinkOpenButton(
            GetSpecialSymlinkGlyph(symlink),
            async () =>
            {
                if (IsShiftDown())
                {
                    OpenFolder(GetSpecialSymlinkTarget(config, symlink));
                    return;
                }

                var picked = await PickPathForTypeAsync(symlink.IsDirectory, GetSpecialSymlinkTarget(config, symlink), "Select symlink target");
                if (picked is null)
                    return;

                await RunOnUiThreadAsync(() =>
                {
                    symlink.Target = picked;
                    if (targetBox is not null)
                        targetBox.Text = picked;
                    if (targetButton is not null)
                        targetButton.Content = new FontIcon { Glyph = GetSpecialSymlinkGlyph(symlink), FontSize = 16 };
                    SaveConfiguration();
                });
            });
        var fields = new StackPanel { Spacing = 6 };
        var sourceRow = new Grid { ColumnSpacing = 6 };
        sourceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sourceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sourceRow.Children.Add(sourceButton);
        sourceBox = CompactTextBox(symlink.Source, "Source", value =>
        {
            symlink.Source = value;
            sourceButton.Content = new FontIcon { Glyph = GetSpecialSymlinkGlyph(symlink), FontSize = 16 };
            targetButton.Content = new FontIcon { Glyph = GetSpecialSymlinkGlyph(symlink), FontSize = 16 };
            SaveConfiguration();
        });
        Grid.SetColumn(sourceBox, 1);
        sourceRow.Children.Add(sourceBox);

        var targetRow = new Grid { ColumnSpacing = 6 };
        targetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        targetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        targetRow.Children.Add(targetButton);
        targetBox = CompactTextBox(symlink.Target, "Target override", value =>
        {
            symlink.Target = value;
            targetButton.Content = new FontIcon { Glyph = GetSpecialSymlinkGlyph(symlink), FontSize = 16 };
            SaveConfiguration();
        });
        Grid.SetColumn(targetBox, 1);
        targetRow.Children.Add(targetBox);

        typeToggle = new ToggleSwitch
        {
            IsOn = symlink.IsDirectory,
            OnContent = "Directory",
            OffContent = "File",
            MinWidth = 0
        };
        typeToggle.Toggled += (_, _) =>
        {
            symlink.IsDirectory = typeToggle.IsOn;
            sourceButton.Content = new FontIcon { Glyph = GetSpecialSymlinkGlyph(symlink), FontSize = 16 };
            targetButton.Content = new FontIcon { Glyph = GetSpecialSymlinkGlyph(symlink), FontSize = 16 };
            SaveConfiguration();
        };
        fields.Children.Add(sourceRow);
        fields.Children.Add(targetRow);
        fields.Children.Add(typeToggle);
        outer.Children.Add(fields);

        var removeButton = CompactRemoveButton(() =>
        {
            list.RemoveAt(index);
            SaveConfiguration();
            refresh();
        });
        Grid.SetColumn(removeButton, 1);
        outer.Children.Add(removeButton);

        return outer;
    }

    private TextBox CompactTextBox(string value, string placeholder, Action<string> save)
    {
        var box = new TextBox
        {
            Text = value,
            PlaceholderText = placeholder,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 0
        };
        box.LostFocus += (_, _) =>
        {
            save(box.Text.Trim());
            DefocusTextBox(box);
        };
        box.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter)
            {
                save(box.Text.Trim());
                DefocusTextBox(box);
                args.Handled = true;
            }
        };
        return box;
    }

    private Button SymlinkOpenButton(string glyph, Func<Task> open)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 16 },
            Width = 30,
            Height = 32,
            MinWidth = 30,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(button, "Select location. Shift-click opens location.");
        button.Click += async (_, _) =>
        {
            try
            {
                WriteDiagnosticLog("Symlink picker clicked.");
                await open();
                WriteDiagnosticLog("Symlink picker completed.");
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog($"Symlink picker failed:{Environment.NewLine}{ex}");
                await RunOnUiThreadAsync(async () =>
                {
                    AppendOutput($"Picker failed: {ex.Message}");
                    await ShowExceptionAsync("Picker failed", ex);
                });
            }
        };
        return button;
    }

    private Button CompactRemoveButton(Action remove)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = "\uE74D", FontSize = 13 },
            Width = 32,
            Height = 32,
            MinWidth = 32,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(button, "Remove");
        button.Click += (_, _) => remove();
        return button;
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
                AppendOutput($"{text} failed: {ex.Message}");
            }
            finally
            {
                button.IsEnabled = true;
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
        _navigation.MenuItems.Add(new NavigationViewItemHeader { Content = "Basic" });
        foreach (var module in _modules.Where(module => IsBasicModule(module.Name)))
        {
            _navigation.MenuItems.Add(new NavigationViewItem
            {
                Content = module.Name,
                Icon = new FontIcon { Glyph = module.IconGlyph },
                Tag = module
            });
        }
        _navigation.MenuItems.Add(new NavigationViewItemSeparator());
        _navigation.MenuItems.Add(new NavigationViewItemHeader { Content = "Advanced" });
        foreach (var module in _modules.Where(module => !IsBasicModule(module.Name)))
        {
            _navigation.MenuItems.Add(new NavigationViewItem
            {
                Content = module.Name,
                Icon = new FontIcon { Glyph = module.IconGlyph },
                Tag = module
            });
        }
    }

    private void RefreshAfterConfigurationReload(ModuleDescriptor? preferredModule)
    {
        var selectedModuleName = preferredModule?.Name ?? GetSelectedModuleName();
        SaveConfiguration();
        LoadConfiguration();
        RebuildNavigation();

        if (selectedModuleName is null)
        {
            RenderDashboard();
            return;
        }

        var refreshedModule = _modules.FirstOrDefault(module => module.Name == selectedModuleName);
        if (refreshedModule is null)
        {
            RenderDashboard();
            return;
        }

        SelectModule(refreshedModule);
    }

    private string? GetSelectedModuleName()
    {
        return (_navigation.SelectedItem as NavigationViewItem)?.Tag is ModuleDescriptor module
            ? module.Name
            : null;
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
            await RunModulesWithOutputDialogAsync(modules);
        }
    }

    private async Task ImportSystemInfoAsync(SystemInfoImportScope scope, ModuleDescriptor? module = null)
    {
        AppendOutput(module is null ? "Scanning system info..." : $"Scanning system info for {module.Name}...");
        BeginLongOperation();
        List<SystemInfoImportCandidate> candidates;
        try
        {
            await PaintBusyIndicatorAsync();
            candidates = await Task.Run(async () => await SystemInfoImportService.FindCandidatesAsync(_config, scope));
        }
        finally
        {
            await RunOnUiThreadAsync(EndLongOperation);
        }

        AppendOutput("Scan complete.");

        if (candidates.Count == 0 &&
            (scope != SystemInfoImportScope.AppInstaller || !SystemInfoImportService.GetRecommendedAppCandidates(_config).Any()))
        {
            var message = GetEmptyImportMessage(scope);
            await ShowMessageAsync("Import", message);
            AppendOutput(message);
            return;
        }

        List<SystemInfoImportCandidate> selected;
        var symlinkMode = SymlinkImportMode.Copy;
        if (scope == SystemInfoImportScope.Symlinks)
        {
            AppendOutput("Opening import review.");
            var result = await ShowSymlinkImportReviewDialogAsync(candidates);
            AppendOutput("Import review closed.");
            selected = result.Selected;
            symlinkMode = result.Mode;
            if (selected.Count == 0)
            {
                AppendOutput("No symlink items selected.");
                return;
            }

            await ImportSelectedSystemInfoAsync(scope, module, candidates, selected, result.Ignored, symlinkMode);
            return;
        }
        else
        {
            AppendOutput("Opening import review.");
            selected = await ShowImportReviewDialogAsync(candidates, scope == SystemInfoImportScope.AppInstaller);
            AppendOutput("Import review closed.");
        }
        if (selected.Count == 0)
        {
            AppendOutput("Import cancelled.");
            return;
        }

        if (scope == SystemInfoImportScope.NetworkDrives)
        {
            await FillNetworkDriveCredentialsAsync(selected);
        }

        await ImportSelectedSystemInfoAsync(scope, module, candidates, selected, [], symlinkMode);
    }

    private async Task ImportSelectedSystemInfoAsync(
        SystemInfoImportScope scope,
        ModuleDescriptor? module,
        IReadOnlyList<SystemInfoImportCandidate> candidates,
        IReadOnlyList<SystemInfoImportCandidate> selected,
        IReadOnlyList<SystemInfoImportCandidate> ignored,
        SymlinkImportMode symlinkMode)
    {
        var logDialogWidth = GetLogDialogWidth();
        var outputBox = CreateLogOutputBox(logDialogWidth);

        var progress = new ProgressBar
        {
            IsIndeterminate = true,
            MinWidth = logDialogWidth
        };
        var copyLogButton = ActionButton("Copy Full Log", () =>
        {
            CopyTextFromFile(RunLog.Path);
            AppendOutput("Import log copied.");
        });
        copyLogButton.HorizontalAlignment = HorizontalAlignment.Right;
        copyLogButton.IsEnabled = false;
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Width = logDialogWidth,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { copyLogButton }
        };
        var content = new StackPanel
        {
            Spacing = 12,
            Width = logDialogWidth,
            Children = { progress, outputBox, footer }
        };
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = $"Importing {SplitName(scope.ToString())}",
            Content = content,
            CloseButtonText = string.Empty,
            DefaultButton = ContentDialogButton.None
        };
        dialog.Resources["ContentDialogMinWidth"] = logDialogWidth;
        dialog.Resources["ContentDialogMaxWidth"] = logDialogWidth + 80;

        var uiLogLock = new object();
        var pendingLogText = new StringBuilder();
        var logFlushQueued = false;
        void Log(string message)
        {
            RunLog.Write("Import", message);
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}" + Environment.NewLine;
            lock (uiLogLock)
            {
                pendingLogText.Append(line);
                if (logFlushQueued)
                    return;
                logFlushQueued = true;
            }

            if (!DispatcherQueue.TryEnqueue(() =>
            {
                string chunk;
                lock (uiLogLock)
                {
                    chunk = pendingLogText.ToString();
                    pendingLogText.Clear();
                    logFlushQueued = false;
                }

                AppendTextToOutputBox(outputBox, chunk);
            }))
            {
                lock (uiLogLock)
                    logFlushQueued = false;
            }
        }

        var dialogTask = dialog.ShowAsync().AsTask();
        BeginLongOperation();
        try
        {
            Log($"Found {candidates.Count} candidate(s).");
            Log($"Selected {selected.Count} candidate(s).");
            Log($"Ignored {ignored.Count} candidate(s).");
            if (scope == SystemInfoImportScope.Symlinks)
                Log($"Symlink mode: {symlinkMode}");
            foreach (var candidate in selected)
                Log($"{candidate.Title}: {candidate.Detail}");
            foreach (var candidate in ignored)
                Log($"Ignored: {candidate.Title}: {candidate.Detail}");

            SystemInfoImportResult ApplyImport(IReadOnlyList<SystemInfoImportCandidate> importSelection)
            {
                SystemInfoImportService.IgnoreCandidates(_config, ignored);
                return SystemInfoImportService.ApplyCandidatesWithResult(
                    _config,
                    importSelection,
                    symlinkMode,
                    Log,
                    scope == SystemInfoImportScope.Symlinks
                        ? candidate =>
                        {
                            ConfigurationManager.SaveConfiguration(_config);
                            Log($"Saved successful symlink config: {candidate.Title}");
                        }
                        : null);
            }

            var result = await Task.Run(() => ApplyImport(selected));

            await RunOnUiThreadAsync(() =>
            {
                RefreshAfterConfigurationReload(module);
            });

            Log($"Imported {result.Added} item(s).");
            Log($"Skipped or failed {Math.Max(0, selected.Count - result.Added)} selected item(s).");
            if (result.SymlinkFailures.Count > 0)
            {
                Log("Failed symlink folders:");
                foreach (var failure in result.SymlinkFailures)
                {
                    Log($"- {failure.Title}: {failure.Message}");
                    foreach (var path in failure.FailedPaths.Take(8))
                        Log($"  {path}");
                }

                var lockingProcesses = SystemInfoImportService.FindLockingProcesses(
                    result.SymlinkFailures.SelectMany(failure => failure.FailedPaths));
                if (lockingProcesses.Count > 0)
                {
                    Log("Apps possibly causing copy issues:");
                    foreach (var process in lockingProcesses)
                        Log($"- {process.ProcessName} (PID {process.ProcessId}) locking {process.Path}");

                    await RunOnUiThreadAsync(() =>
                    {
                        var retryConfirmArmed = false;
                        Button? button = null;
                        button = ActionButton("Kill Apps and Retry", async () =>
                        {
                            if (button is null)
                                return;

                            if (!retryConfirmArmed)
                            {
                                retryConfirmArmed = true;
                                button.Content = "Confirm Kill and Retry";
                                Log($"Confirm kill and retry: this will kill {lockingProcesses.Count} detected app process(es), then retry {result.SymlinkFailures.Count} failed symlink item(s).");
                                return;
                            }

                            BeginLongOperation();
                            try
                            {
                                await RunOnUiThreadAsync(() =>
                                {
                                    button.IsEnabled = false;
                                    progress.IsIndeterminate = true;
                                });
                                Log("Killing detected locking apps...");
                                foreach (var processInfo in lockingProcesses)
                                {
                                    try
                                    {
                                        var process = Process.GetProcessById(processInfo.ProcessId);
                                        Log($"Killing {processInfo.ProcessName} (PID {processInfo.ProcessId})");
                                        process.Kill(true);
                                        await process.WaitForExitAsync();
                                    }
                                    catch (Exception ex)
                                    {
                                        Log($"Failed to kill {processInfo.ProcessName} (PID {processInfo.ProcessId}): {ex.Message}");
                                    }
                                }

                                Log("Retrying failed symlink item(s)...");
                                var retrySelection = result.SymlinkFailures.Select(failure => failure.Candidate).ToList();
                                var retryResult = await Task.Run(() => ApplyImport(retrySelection));
                                await RunOnUiThreadAsync(() =>
                                {
                                    RefreshAfterConfigurationReload(module);
                                });

                                Log($"Retry imported {retryResult.Added} item(s).");
                                if (retryResult.SymlinkFailures.Count > 0)
                                {
                                    Log($"Retry still failed {retryResult.SymlinkFailures.Count} symlink item(s).");
                                    foreach (var failure in retryResult.SymlinkFailures)
                                        Log($"- {failure.Title}: {failure.Message}");
                                }
                                else
                                {
                                    Log("Retry completed without symlink copy failures.");
                                }
                            }
                            finally
                            {
                                await RunOnUiThreadAsync(() =>
                                {
                                    progress.IsIndeterminate = false;
                                    EndLongOperation();
                                });
                            }
                        }, primary: true);
                        footer.Children.Insert(0, button);
                    });
                }
                else
                {
                    Log("No locking app process could be detected. Close related apps and retry import.");
                }
            }
            Log($"Log path: {RunLog.Path}");
            AppendOutput($"Imported {result.Added} item(s). Log: {RunLog.Path}");
        }
        catch (Exception ex)
        {
            RunLog.WriteException("Import", "Import failed", ex);
            Log($"Failed: {ex}");
            AppendOutput($"Import failed: {ex.Message}");
        }
        finally
        {
            await RunOnUiThreadAsync(() =>
            {
                progress.IsIndeterminate = false;
                copyLogButton.IsEnabled = true;
                dialog.CloseButtonText = "Done";
                EndLongOperation();
            });
        }

        await dialogTask;
    }

    private async Task FillNetworkDriveCredentialsAsync(IEnumerable<SystemInfoImportCandidate> selected)
    {
        foreach (var candidate in selected)
        {
            if (candidate.Value is not NetworkDriveMapping drive)
            {
                continue;
            }

            var username = new TextBox { PlaceholderText = "Username", Text = drive.Username };
            var password = new PasswordBox { PlaceholderText = "Password" };
            var panel = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = $"{drive.DriveLetter}: {drive.NetworkPath}", TextWrapping = TextWrapping.Wrap },
                    username,
                    password
                }
            };

            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Network drive credentials",
                Content = panel,
                PrimaryButtonText = "Save",
                SecondaryButtonText = "Skip",
                CloseButtonText = "Cancel"
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                drive.Username = username.Text.Trim();
                drive.Password = password.Password;
            }
            else if (result == ContentDialogResult.None)
            {
                break;
            }
        }
    }

    private async Task<List<SystemInfoImportCandidate>> ShowImportReviewDialogAsync(IReadOnlyList<SystemInfoImportCandidate> candidates, bool includeRecommendedApps = false)
    {
        if (!DispatcherQueue.HasThreadAccess)
            return await RunOnUiThreadAsync(() => ShowImportReviewDialogAsync(candidates, includeRecommendedApps));

        var selected = new List<SystemInfoImportCandidate>();
        var panel = new StackPanel { Spacing = 12 };
        var checkBoxes = new List<CheckBox>();
        var ignoredCandidates = new HashSet<SystemInfoImportCandidate>(candidates.Where(candidate => candidate.Group == "Ignored"));
        ContentDialog? dialog = null;
        void UpdateTitle()
        {
            if (dialog is not null)
                dialog.Title = $"Import {checkBoxes.Count(box => box.IsChecked == true && box.Tag is SystemInfoImportCandidate candidate && !ignoredCandidates.Contains(candidate))} system item(s)?";
        }

        if (includeRecommendedApps)
        {
            var recommendedCandidates = SystemInfoImportService.GetRecommendedAppCandidates(_config).ToList();
            var recommendedSelected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var recommendedToggle = new CheckBox { Content = "Add Recommended Apps", IsThreeState = true, IsChecked = false };
            Button? chooseRecommended = null;
            chooseRecommended = ActionButton("Choose Apps", () =>
            {
                var choices = new StackPanel { Spacing = 8, Padding = new Thickness(12) };
                void UpdateRecommendedState()
                {
                    var available = recommendedCandidates.Where(candidate => candidate.Group != "Ignored").ToList();
                    recommendedToggle.IsChecked = recommendedSelected.Count == 0 ? false :
                        recommendedSelected.Count == available.Count ? true : null;
                }
                foreach (var candidate in recommendedCandidates)
                {
                    var app = (AppImportCandidate)candidate.Value;
                    var choice = new CheckBox { Content = candidate.Title, IsChecked = recommendedSelected.Contains(app.PackageId), IsEnabled = candidate.Group != "Ignored" };
                    choice.Checked += (_, _) => { recommendedSelected.Add(app.PackageId); UpdateRecommendedState(); };
                    choice.Unchecked += (_, _) => { recommendedSelected.Remove(app.PackageId); UpdateRecommendedState(); };
                    choices.Children.Add(choice);
                }
                var flyout = new Flyout { Content = new ScrollViewer { Content = choices, MaxHeight = 360 } };
                flyout.ShowAt(chooseRecommended);
            });
            recommendedToggle.Click += (_, _) =>
            {
                var available = recommendedCandidates.Where(candidate => candidate.Group != "Ignored").Cast<SystemInfoImportCandidate>().ToList();
                if (recommendedToggle.IsChecked == true)
                    foreach (var candidate in available)
                        recommendedSelected.Add(((AppImportCandidate)candidate.Value).PackageId);
                else
                    recommendedSelected.Clear();
            };
            var recommendedRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            recommendedRow.Children.Add(recommendedToggle);
            recommendedRow.Children.Add(chooseRecommended);
            panel.Children.Add(recommendedRow);
            panel.Tag = (recommendedCandidates, recommendedSelected);
        }

        foreach (var group in candidates.GroupBy(candidate => string.IsNullOrWhiteSpace(candidate.Group) ? SplitName(candidate.Scope.ToString()) : candidate.Group))
        {
            panel.Children.Add(new TextBlock { Text = group.Key, FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 } });
            foreach (var candidate in group)
            {
                var isIgnored = ignoredCandidates.Contains(candidate);
                var row = new Grid { ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var checkBox = new CheckBox { IsChecked = !isIgnored, Tag = candidate, VerticalAlignment = VerticalAlignment.Center };
                checkBox.Checked += (_, _) => UpdateTitle();
                checkBox.Unchecked += (_, _) => UpdateTitle();
                checkBoxes.Add(checkBox);
                row.Children.Add(checkBox);
                var detail = new StackPanel { Spacing = 2 };
                detail.Children.Add(new TextBlock { Text = candidate.Title, TextWrapping = TextWrapping.Wrap });
                detail.Children.Add(new TextBlock { Text = candidate.Detail, Foreground = ResourceBrush("WinstallerSecondaryTextBrush"), FontSize = 12, TextWrapping = TextWrapping.Wrap });
                Grid.SetColumn(detail, 1);
                row.Children.Add(detail);
                if (candidate.Value is AppImportCandidate)
                {
                    Button? ignore = null;
                    ignore = ActionButton(isIgnored ? "Ignored" : "Ignore", () =>
                    {
                        if (ignoredCandidates.Remove(candidate))
                        {
                            ignore!.Content = "Ignore";
                            SystemInfoImportService.UnignoreCandidates(_config, [candidate]);
                        }
                        else
                        {
                            ignoredCandidates.Add(candidate);
                            ignore!.Content = "Ignored";
                            checkBox.IsChecked = false;
                            SystemInfoImportService.IgnoreCandidates(_config, [candidate]);
                        }
                        SaveConfiguration();
                        UpdateTitle();
                    });
                    Grid.SetColumn(ignore, 2);
                    row.Children.Add(ignore);
                }
                panel.Children.Add(new Border { Background = ResourceBrush("WinstallerCardBrush"), CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Child = row });
            }
        }

        var scroll = new ScrollViewer { Content = panel, MaxHeight = 520 };
        dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = $"Import {candidates.Count} system item(s)?",
            Content = scroll,
            PrimaryButtonText = "Import Selected",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        UpdateTitle();
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return selected;

        foreach (var checkBox in checkBoxes)
            if (checkBox.IsChecked == true && checkBox.Tag is SystemInfoImportCandidate candidate && !ignoredCandidates.Contains(candidate))
                selected.Add(candidate);
        if (panel.Tag is ValueTuple<List<SystemInfoImportCandidate>, HashSet<string>> recommendations)
            selected.AddRange(recommendations.Item1.Where(candidate => recommendations.Item2.Contains(((AppImportCandidate)candidate.Value).PackageId)));
        return selected;
    }
    private async Task<SymlinkImportSelection> ShowSymlinkImportReviewDialogAsync(IReadOnlyList<SystemInfoImportCandidate> candidates)
    {
        if (!DispatcherQueue.HasThreadAccess)
            return await RunOnUiThreadAsync(() => ShowSymlinkImportReviewDialogAsync(candidates));

        var resultMode = SymlinkImportMode.Copy;
        var actionName = "Copy and Symlink";
        var accepted = false;
        ContentDialog? dialog = null;
        var dialogWidth = Math.Min(1080, Math.Max(720, (RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : 1180) - 160));
        var scrollbarGutter = 24;
        var cardWidth = dialogWidth - scrollbarGutter;
        var innerWidth = cardWidth - 24;
        var panel = new StackPanel
        {
            Spacing = 12,
            Width = cardWidth,
            MaxWidth = cardWidth,
            Margin = new Thickness(0, 0, scrollbarGutter, 0)
        };
        var textWidth = Math.Max(360, innerWidth - 316);

        var checkBoxes = new List<CheckBox>();
        var ignoredCandidates = new HashSet<SystemInfoImportCandidate>();
        void UpdateSelectedCount()
        {
            if (dialog is not null)
            {
                dialog.Title = $"Import {checkBoxes.Count(checkBox => checkBox.IsChecked == true && checkBox.Tag is SystemInfoImportCandidate candidate && !ignoredCandidates.Contains(candidate))} symlink item(s)?";
            }
        }

        var orderedGroups = candidates
            .GroupBy(candidate => string.IsNullOrWhiteSpace(candidate.Group) ? "Folders Not Yet Symlinked" : candidate.Group)
            .OrderBy(group => group.Key == "Existing Symlinks" ? 0 : group.Key == "Ignored" ? 2 : 1);

        foreach (var group in orderedGroups)
        {
            panel.Children.Add(new TextBlock
            {
                Text = group.Key,
                FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 }
            });
            foreach (var candidate in group)
            {
                var isIgnored = group.Key == "Ignored";
                if (isIgnored)
                {
                    ignoredCandidates.Add(candidate);
                }

                var itemGrid = new Grid
                {
                    ColumnSpacing = 10,
                    Width = innerWidth
                };
                itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(248) });

                var checkBox = new CheckBox
                {
                    IsChecked = !isIgnored,
                    Tag = candidate,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Width = 20,
                    Height = 20,
                    MinWidth = 0,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0)
                };
                checkBox.Checked += (_, _) => UpdateSelectedCount();
                checkBox.Unchecked += (_, _) => UpdateSelectedCount();
                checkBoxes.Add(checkBox);
                itemGrid.Children.Add(checkBox);

                var textPanel = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = candidate.Title, MaxWidth = textWidth, TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = candidate.Detail, MaxWidth = textWidth, Foreground = ResourceBrush("WinstallerSecondaryTextBrush"), FontSize = 12, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis }
                    }
                };
                ToolTipService.SetToolTip(textPanel, candidate.Detail);
                Grid.SetColumn(textPanel, 1);
                itemGrid.Children.Add(textPanel);

                var actionPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };

                Button? ignoreButton = null;
                ignoreButton = ActionButton("Ignore", () =>
                {
                    if (ignoredCandidates.Remove(candidate))
                    {
                        ignoreButton!.Content = "Ignore";
                        SystemInfoImportService.UnignoreCandidates(_config, [candidate]);
                        SaveConfiguration();
                        AppendOutput($"Unignored {candidate.Title}.");
                    }
                    else
                    {
                        ignoredCandidates.Add(candidate);
                        ignoreButton!.Content = "Ignored";
                        checkBox.IsChecked = false;
                        SystemInfoImportService.IgnoreCandidates(_config, [candidate]);
                        SaveConfiguration();
                        AppendOutput($"Ignored {candidate.Title}.");
                    }

                    UpdateSelectedCount();
                });
                ignoreButton.Content = isIgnored ? "Ignored" : "Ignore";
                ignoreButton.MinWidth = 84;
                actionPanel.Children.Add(ignoreButton);

                var openButton = ActionButton("Open Folder", () => OpenFolder(candidate.Detail));
                openButton.HorizontalAlignment = HorizontalAlignment.Right;
                openButton.VerticalAlignment = VerticalAlignment.Center;
                openButton.MinWidth = 108;
                actionPanel.Children.Add(openButton);
                Grid.SetColumn(actionPanel, 2);
                itemGrid.Children.Add(actionPanel);

                panel.Children.Add(new Border
                {
                    Background = ResourceBrush("WinstallerCardBrush"),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12),
                    Width = cardWidth,
                    Child = itemGrid
                });
            }
        }

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Width = cardWidth
        };
        var dialogContent = new StackPanel
        {
            Spacing = 10,
            Width = dialogWidth,
            MaxWidth = dialogWidth,
            Children =
            {
                new ScrollViewer
                {
                    Content = panel,
                    MaxHeight = 600,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                },
                new StackPanel
                {
                    Width = cardWidth,
                    Margin = new Thickness(0, 0, scrollbarGutter, 0),
                    Children = { footer }
                }
            }
        };

        dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Import 0 symlink item(s)?",
            FullSizeDesired = true,
            Content = dialogContent,
            DefaultButton = ContentDialogButton.None
        };
        dialog.Resources["ContentDialogMinWidth"] = dialogWidth;
        dialog.Resources["ContentDialogMaxWidth"] = dialogWidth + 80;
        UpdateSelectedCount();

        footer.Children.Add(ActionButton("Copy and Symlink (Safe, but slower)", () =>
        {
            actionName = "Copy and Symlink";
            resultMode = SymlinkImportMode.Copy;
            accepted = true;
            dialog.Hide();
        }, primary: true));
        footer.Children.Add(ActionButton("Move and Symlink (Faster, but riskier)", () =>
        {
            actionName = "Move and Symlink";
            resultMode = SymlinkImportMode.Move;
            accepted = true;
            dialog.Hide();
        }));
        footer.Children.Add(ActionButton("Cancel", () =>
        {
            accepted = false;
            dialog.Hide();
        }));

        await dialog.ShowAsync();
        if (!accepted)
            return new([], [], SymlinkImportMode.Copy);

        if (!await ConfirmAsync(
                $"Continue with {actionName}?",
                $"Are you sure you want to continue with {actionName}?",
                actionName))
            return new([], [], SymlinkImportMode.Copy);

        var selected = checkBoxes
            .Where(checkBox => checkBox.IsChecked == true && checkBox.Tag is SystemInfoImportCandidate candidate && !ignoredCandidates.Contains(candidate))
            .Select(checkBox => checkBox.Tag)
            .OfType<SystemInfoImportCandidate>()
            .ToList();
        return new(selected, ignoredCandidates.ToList(), resultMode);
    }

    private void OpenFolder(string path)
    {
        var target = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        while (!string.IsNullOrWhiteSpace(target) && !Directory.Exists(target))
        {
            target = Path.GetDirectoryName(target);
        }

        if (string.IsNullOrWhiteSpace(target))
            return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendOutput($"Open folder failed: {ex.Message}");
        }
    }

    private async Task<string?> PickSymlinkPathAsync(string rootPath, string currentValue)
    {
        var expandedRoot = ExpandConfigPath(rootPath);
        var initialFolder = GetExistingFolder(Path.Combine(expandedRoot, currentValue), expandedRoot);
        var picked = await PickFolderPathAsync(initialFolder, "Select AppData folder");
        if (picked is null)
            return null;

        if (!picked.StartsWith(expandedRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) &&
            !picked.Equals(expandedRoot, StringComparison.OrdinalIgnoreCase))
        {
            await ShowMessageAsync("Invalid location", $"Pick a path under {expandedRoot}.");
            return null;
        }

        return Path.GetRelativePath(expandedRoot, picked);
    }

    private async Task<string?> PickPathForTypeAsync(bool isDirectory, string? currentPath, string title)
    {
        var initialFolder = GetExistingFolder(currentPath, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return isDirectory ? await PickFolderPathAsync(initialFolder, title) : await PickFilePathAsync(initialFolder, title);
    }

    private async Task<string?> PickFolderPathAsync(string? initialFolder, string title)
    {
        WriteDiagnosticLog("Folder picker create.");
        var hwnd = WindowNative.GetWindowHandle(this);
        WriteDiagnosticLog($"Native folder picker show hwnd={hwnd}; initialFolder={initialFolder}; title={title}.");
        var folder = await NativePathPicker.PickFolderAsync(hwnd, initialFolder, title);
        WriteDiagnosticLog(folder is null ? "Folder picker canceled." : $"Folder picker picked: {folder}");
        return folder;
    }

    private async Task<string?> PickFilePathAsync(string? initialFolder, string title)
    {
        WriteDiagnosticLog("File picker create.");
        var hwnd = WindowNative.GetWindowHandle(this);
        WriteDiagnosticLog($"Native file picker show hwnd={hwnd}; initialFolder={initialFolder}; title={title}.");
        var file = await NativePathPicker.PickFileAsync(hwnd, initialFolder, title);
        WriteDiagnosticLog(file is null ? "File picker canceled." : $"File picker picked: {file}");
        return file;
    }

    private static string GetExistingFolder(string? path, string fallback)
    {
        var expandedFallback = ExpandConfigPath(fallback);
        var expandedPath = string.IsNullOrWhiteSpace(path) ? expandedFallback : ExpandConfigPath(path);
        if (Directory.Exists(expandedPath))
            return expandedPath;

        var parent = Path.GetDirectoryName(expandedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
            return parent;

        return Directory.Exists(expandedFallback)
            ? expandedFallback
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static string GetAppDataRootForProperty(string propertyName)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return propertyName switch
        {
            nameof(SymlinksConfig.RoamingDirectories) => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            nameof(SymlinksConfig.LocalDirectories) => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            nameof(SymlinksConfig.LocalLowDirectories) => Path.Combine(profile, "AppData", "LocalLow"),
            _ => profile
        };
    }

    private static bool IsShiftDown()
    {
        return Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }

    private static void DefocusTextBox(TextBox box)
    {
        box.SelectionLength = 0;
        if (box.XamlRoot?.Content is Control control)
        {
            control.Focus(FocusState.Programmatic);
            return;
        }

        FocusManager.TryMoveFocus(FocusNavigationDirection.Next);
    }

    private static string GetManagedAppDataSymlinkTarget(SymlinksConfig config, string propertyName, string relativePath)
    {
        var section = propertyName switch
        {
            nameof(SymlinksConfig.RoamingDirectories) => "Roaming",
            nameof(SymlinksConfig.LocalDirectories) => "Local",
            nameof(SymlinksConfig.LocalLowDirectories) => "LocalLow",
            _ => string.Empty
        };
        var baseDir = ExpandConfigPath(config.BaseSymlinkDirectory);
        return Path.Combine(baseDir, "AppData", section, relativePath);
    }

    private static string GetSpecialSymlinkTarget(SymlinksConfig config, SpecialSymlink symlink)
    {
        if (!string.IsNullOrWhiteSpace(symlink.Target))
            return ExpandConfigPath(symlink.Target);

        var source = ExpandConfigPath(symlink.Source);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var localLow = Path.Combine(profile, "AppData", "LocalLow");
        var baseDir = ExpandConfigPath(config.BaseSymlinkDirectory);

        var relative = source.StartsWith(roaming, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine("AppData", "Roaming", Path.GetRelativePath(roaming, source))
            : source.StartsWith(local, StringComparison.OrdinalIgnoreCase)
                ? Path.Combine("AppData", "Local", Path.GetRelativePath(local, source))
                : source.StartsWith(localLow, StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine("AppData", "LocalLow", Path.GetRelativePath(localLow, source))
                    : Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        return Path.Combine(baseDir, relative);
    }

    private static string ExpandConfigPath(string path)
    {
        return Environment.ExpandEnvironmentVariables(path).Replace("{USERNAME}", Environment.UserName);
    }

    private static string GetSpecialSymlinkGlyph(SpecialSymlink symlink)
    {
        return symlink.IsDirectory ? "\uE8B7" : "\uE8A5";
    }

    private async Task<bool> ConfirmAsync(string title, string message, string primaryText)
    {
        if (!DispatcherQueue.HasThreadAccess)
            return await RunOnUiThreadAsync(() => ConfirmAsync(title, message, primaryText));

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
        if (!DispatcherQueue.HasThreadAccess)
        {
            await RunOnUiThreadAsync(() => ShowMessageAsync(title, message));
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "OK"
        };

        await dialog.ShowAsync();
    }

    private async Task ShowExceptionAsync(string title, Exception exception)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            await RunOnUiThreadAsync(() => ShowExceptionAsync(title, exception));
            return;
        }

        var details = exception.ToString();
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = exception.Message,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBox
        {
            Text = details,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MinHeight = 180,
            MaxHeight = 320,
            HorizontalAlignment = HorizontalAlignment.Stretch
        });

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = "Copy Details",
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            CopyText(details);
            AppendOutput("Picker error copied.");
        }
    }

    private async Task ShowModuleSettingsAsync(ModuleDescriptor module)
    {
        var panel = new StackPanel { Spacing = 12 };
        foreach (var property in module.Config.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead ||
                !property.CanWrite ||
                property.Name == "Enabled" ||
                IsSupportedList(property.PropertyType))
            {
                continue;
            }

            panel.Children.Add(BuildSettingRow(module.Config, property));
        }

        if (panel.Children.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "No module settings available." });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = $"{module.Name} Settings",
            Content = new ScrollViewer
            {
                Content = panel,
                MaxHeight = 540,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            },
            CloseButtonText = "Done"
        };
        dialog.Resources["ContentDialogMinWidth"] = 680;
        dialog.Resources["ContentDialogMaxWidth"] = 840;

        await dialog.ShowAsync();
        RenderModule(module);
    }

    private async Task ShowAppBehaviorDialogAsync(AppInstallerConfig config, string? packageId)
    {
        var isNew = string.IsNullOrWhiteSpace(packageId);
        var behavior = !isNew && config.Behaviors.TryGetValue(packageId!, out var existing)
            ? CloneAppBehavior(existing)
            : new AppInstallBehavior { DisplayName = isNew ? string.Empty : GetKnownPackageName(packageId!) };
        var name = new TextBox { Text = behavior.DisplayName, PlaceholderText = "App name" };
        var id = new TextBox { Text = packageId ?? string.Empty, PlaceholderText = "Winget package ID" };
        var mode = new ComboBox { MinWidth = 180 };
        mode.Items.Add("Default");
        mode.Items.Add("Prepared");
        mode.Items.Add("Manual");
        mode.SelectedItem = string.IsNullOrWhiteSpace(behavior.InstallMode) ? "Default" : behavior.InstallMode;
        var lockVersion = new ToggleSwitch { Header = "Lock version", IsOn = behavior.LockVersion };
        var version = new TextBox { Text = behavior.Version, PlaceholderText = "Version" };
        void UpdateVersionState()
        {
            version.IsEnabled = lockVersion.IsOn && string.Equals(mode.SelectedItem?.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
        }
        lockVersion.Toggled += (_, _) => UpdateVersionState();
        mode.SelectionChanged += (_, _) => UpdateVersionState();
        UpdateVersionState();

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "App name" });
        panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = "Winget package ID" });
        panel.Children.Add(id);
        panel.Children.Add(new TextBlock { Text = "Install mode" });
        panel.Children.Add(mode);
        panel.Children.Add(lockVersion);
        panel.Children.Add(version);

        var customOptions = new StackPanel { Spacing = 10 };
        void RefreshCustomOptions()
        {
            customOptions.Children.Clear();
            var typedId = id.Text.Trim();
            if (typedId.Equals("Discord.Discord", StringComparison.OrdinalIgnoreCase))
            {
                customOptions.Children.Add(BuildInlineObjectPropertyEditor(behavior.Discord, typeof(DiscordInstallOptions).GetProperty(nameof(DiscordInstallOptions.InstallVencord))!));
                customOptions.Children.Add(BuildInlineObjectPropertyEditor(behavior.Discord, typeof(DiscordInstallOptions).GetProperty(nameof(DiscordInstallOptions.InstallOpenAsar))!));
                customOptions.Children.Add(BuildInlineObjectPropertyEditor(behavior.Discord, typeof(DiscordInstallOptions).GetProperty(nameof(DiscordInstallOptions.DiscordLocation))!));
                customOptions.Children.Add(BuildInlineObjectPropertyEditor(behavior.Discord, typeof(DiscordInstallOptions).GetProperty(nameof(DiscordInstallOptions.VencordInstallerUrl))!));
            }
            else if (typedId.Equals("Spotify.Spotify", StringComparison.OrdinalIgnoreCase))
            {
                customOptions.Children.Add(BuildInlineObjectPropertyEditor(behavior.Spotify, typeof(SpotifyInstallOptions).GetProperty(nameof(SpotifyInstallOptions.InstallSpicetify))!));
                customOptions.Children.Add(BuildInlineObjectPropertyEditor(behavior.Spotify, typeof(SpotifyInstallOptions).GetProperty(nameof(SpotifyInstallOptions.BlockUpdates))!));
                customOptions.Children.Add(BuildInlineObjectPropertyEditor(behavior.Spotify, typeof(SpotifyInstallOptions).GetProperty(nameof(SpotifyInstallOptions.SidebarConfig))!));
                customOptions.Children.Add(BuildListSection(behavior.Spotify, typeof(SpotifyInstallOptions).GetProperty(nameof(SpotifyInstallOptions.CustomApps))!));
            }
        }
        id.TextChanged += (_, _) => RefreshCustomOptions();
        panel.Children.Add(customOptions);
        RefreshCustomOptions();
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = isNew ? "Add App" : "App Settings",
            Content = new ScrollViewer { Content = panel, MaxHeight = 540 },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel"
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var newId = id.Text.Trim();
        if (string.IsNullOrWhiteSpace(newId))
        {
            await ShowMessageAsync("App Settings", "Winget package ID is required.");
            return;
        }
        if (lockVersion.IsOn && string.IsNullOrWhiteSpace(version.Text))
        {
            await ShowMessageAsync("App Settings", "Version is required when version locking is enabled.");
            return;
        }
        if ((isNew || !newId.Equals(packageId, StringComparison.OrdinalIgnoreCase)) &&
            config.DefaultInstalls.Contains(newId, StringComparer.OrdinalIgnoreCase))
        {
            await ShowMessageAsync("App Settings", "That package ID is already configured.");
            return;
        }

        behavior.DisplayName = string.IsNullOrWhiteSpace(name.Text) ? GetKnownPackageName(newId) : name.Text.Trim();
        behavior.InstallMode = mode.SelectedItem?.ToString() ?? "Default";
        behavior.LockVersion = lockVersion.IsOn && behavior.InstallMode.Equals("Default", StringComparison.OrdinalIgnoreCase);
        behavior.Version = behavior.LockVersion ? version.Text.Trim() : string.Empty;
        if (isNew)
            config.DefaultInstalls.Add(newId);
        else if (!newId.Equals(packageId, StringComparison.OrdinalIgnoreCase))
        {
            var index = config.DefaultInstalls.FindIndex(existingId => existingId.Equals(packageId, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                config.DefaultInstalls[index] = newId;
            config.Behaviors.Remove(packageId!);
        }
        config.Behaviors[newId] = behavior;
        SaveConfiguration();
        RenderModule(_modules.First(module => ReferenceEquals(module.Config, config)));
    }

    private static AppInstallBehavior CloneAppBehavior(AppInstallBehavior source)
    {
        return new AppInstallBehavior
        {
            DisplayName = source.DisplayName,
            InstallMode = source.InstallMode,
            LockVersion = source.LockVersion,
            Version = source.Version,
            Discord = new DiscordInstallOptions
            {
                InstallDiscord = source.Discord.InstallDiscord,
                InstallVencord = source.Discord.InstallVencord,
                InstallOpenAsar = source.Discord.InstallOpenAsar,
                VencordInstallerUrl = source.Discord.VencordInstallerUrl,
                DiscordLocation = source.Discord.DiscordLocation
            },
            Spotify = new SpotifyInstallOptions
            {
                InstallSpotify = source.Spotify.InstallSpotify,
                InstallSpicetify = source.Spotify.InstallSpicetify,
                BlockUpdates = source.Spotify.BlockUpdates,
                SidebarConfig = source.Spotify.SidebarConfig,
                CustomApps = [.. source.Spotify.CustomApps]
            }
        };
    }
    private async Task ShowIgnoredItemsAsync(ModuleDescriptor module)
    {
        var panel = new StackPanel { Spacing = 12 };
        foreach (var property in module.Config.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.Name.Contains("Ignored", StringComparison.OrdinalIgnoreCase) && IsSupportedList(property.PropertyType))
            {
                panel.Children.Add(BuildListSection(module.Config, property));
            }
        }

        if (panel.Children.Count == 0)
            panel.Children.Add(new TextBlock { Text = "No ignored items." });

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = $"{module.Name} Ignored Items",
            Content = new ScrollViewer { Content = panel, MaxHeight = 540 },
            CloseButtonText = "Done"
        };

        await dialog.ShowAsync();
        RenderModule(module);
    }

    private async Task ShowGuidedSetupPromptAsync()
    {
        if (await ConfirmAsync("Start guided setup?", "Walk through Symlinks, App Installer, Fonts, Shell Folders, PATH, and Network Drives if detected.", "Start"))
        {
            await RunGuidedSetupAsync();
        }
    }

    private async Task RunGuidedSetupAsync()
    {
        var standard = new[] { "Symlinks", "App Installer", "Fonts", "Shell Folders", "Path" }
            .Select(name => _modules.First(module => module.Name == name))
            .ToList();

        if ((await SystemInfoImportService.FindCandidatesAsync(_config, SystemInfoImportScope.NetworkDrives)).Count > 0)
            standard.Add(_modules.First(module => module.Name == "Network Drives"));

        foreach (var module in standard)
        {
            var runStep = await ConfirmAsync($"Guided setup: {module.Name}", "Import detected system information for this module?", "Import");
            if (runStep && module.ImportScope is { } scope)
            {
                await ImportSystemInfoAsync(scope, module);
            }
        }
    }

    private async Task RunModulesWithOutputDialogAsync(IReadOnlyList<ModuleDescriptor> modules)
    {
        var logDialogWidth = GetLogDialogWidth();
        var outputBox = CreateLogOutputBox(logDialogWidth);

        var progress = new ProgressBar
        {
            IsIndeterminate = true,
            MinWidth = logDialogWidth
        };
        var copyLogButton = ActionButton("Copy Full Log", () => CopyTextFromFile(RunLog.Path));
        copyLogButton.IsEnabled = false;
        var openFolderButton = ActionButton("Open Log Folder", () => OpenFolder(Path.GetDirectoryName(RunLog.Path) ?? BootstrapManager.LogsDirectory));
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Width = logDialogWidth,
            Children = { openFolderButton, copyLogButton }
        };
        var content = new StackPanel
        {
            Spacing = 12,
            Width = logDialogWidth,
            Children = { progress, outputBox, footer }
        };

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = modules.Count == 1 ? $"Running {modules[0].Name}" : $"Running {modules.Count} modules",
            Content = content,
            CloseButtonText = string.Empty,
            DefaultButton = ContentDialogButton.None
        };
        dialog.Resources["ContentDialogMinWidth"] = logDialogWidth;
        dialog.Resources["ContentDialogMaxWidth"] = logDialogWidth + 80;

        _activeOutputBox = outputBox;
        var dialogTask = dialog.ShowAsync().AsTask();
        try
        {
            await RunModulesAsync(modules);
        }
        finally
        {
            _activeOutputBox = null;
            await RunOnUiThreadAsync(() =>
            {
                progress.IsIndeterminate = false;
                copyLogButton.IsEnabled = true;
                dialog.CloseButtonText = "Done";
            });
        }

        await dialogTask;
    }

    private async Task RunModulesAsync(IReadOnlyList<ModuleDescriptor> modules)
    {
        if (_isRunning)
        {
            AppendOutput("Operation already running.");
            return;
        }

        if (modules.Count == 0)
        {
            AppendOutput("No modules selected.");
            return;
        }

        BeginLongOperation();
        AppendOutput($"Starting {modules.Count} module(s). Log: {RunLog.Path}");

        try
        {
            await PaintBusyIndicatorAsync();
            await Task.Run(async () =>
            {
                var originalOut = Console.Out;
                var originalError = Console.Error;
                var originalIn = Console.In;
                using var writer = new BufferedTextBoxWriter(AppendOutputText, "Run");
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
                        RunLog.Write("Run", $"Starting module: {descriptor.Name}");
                        try
                        {
                            var succeeded = await descriptor.CreateModule().ExecuteAsync().ConfigureAwait(false);
                            var result = succeeded ? "Completed successfully." : "Completed with errors.";
                            RunLog.Write("Run", $"{descriptor.Name}: {result}");
                            AppendOutput(result);
                        }
                        catch (Exception ex)
                        {
                            RunLog.WriteException("Run", $"{descriptor.Name} failed", ex);
                            AppendOutput($"Failed: {ex.Message}");
                        }
                    }
                }
                finally
                {
                    writer.Flush();
                    Console.SetOut(originalOut);
                    Console.SetError(originalError);
                    Console.SetIn(originalIn);
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            await RunOnUiThreadAsync(EndLongOperation);
            AppendOutput($"Run finished. Log: {RunLog.Path}");
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

    private Task RunOnUiThreadAsync(Action action)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }))
        {
            completion.SetException(new InvalidOperationException("Failed to enqueue UI work."));
        }

        return completion.Task;
    }

    private Task<T> RunOnUiThreadAsync<T>(Func<Task<T>> action)
    {
        if (DispatcherQueue.HasThreadAccess)
            return action();

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                completion.SetResult(await action());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }))
        {
            completion.SetException(new InvalidOperationException("Failed to enqueue UI work."));
        }

        return completion.Task;
    }

    private Task RunOnUiThreadAsync(Func<Task> action)
    {
        if (DispatcherQueue.HasThreadAccess)
            return action();

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }))
        {
            completion.SetException(new InvalidOperationException("Failed to enqueue UI work."));
        }

        return completion.Task;
    }

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

        if (_activeOutputBox is null || string.IsNullOrEmpty(chunk))
            return;

        AppendTextToOutputBox(_activeOutputBox, chunk);
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
            SetClipboardText(WindowNative.GetWindowHandle(this), text);
        }
        catch (Exception ex)
        {
            RunLog.WriteException("UI", "Clipboard copy failed", ex);
            AppendOutput($"Copy failed: {ex.Message}");
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
        if (!OpenClipboard(hwnd))
            throw new InvalidOperationException("Clipboard is unavailable.");

        var handle = nint.Zero;
        try
        {
            EmptyClipboard();
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

    private static string GetSettingDescription(PropertyInfo property)
    {
        if (IsSupportedList(property.PropertyType))
        {
            return "Configured items";
        }

        return property.PropertyType == typeof(bool) ? "On or off" :
            property.PropertyType == typeof(int) ? "Number" :
            property.PropertyType == typeof(string) ? "Text" :
            "Setting";
    }

    private static bool IsBasicModule(string name)
    {
        return name is "Symlinks" or "App Installer" or "Fonts" or "Shell Folders" or "Path" or "Network Drives";
    }

    private static bool HasIgnoredItems(object config)
    {
        return config.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(property => property.Name.Contains("Ignored", StringComparison.OrdinalIgnoreCase) &&
                             IsSupportedList(property.PropertyType));
    }

    private static string GetEmptyImportMessage(SystemInfoImportScope scope)
    {
        return scope switch
        {
            SystemInfoImportScope.NetworkDrives => "No new network drives were found.",
            SystemInfoImportScope.AppInstaller => "No new installed apps were found.",
            SystemInfoImportScope.Fonts => "No new installed fonts were found.",
            SystemInfoImportScope.ShellFolders => "All standard shell folders are already configured.",
            SystemInfoImportScope.Symlinks => "No new symlink candidates were found.",
            SystemInfoImportScope.Path => "No new PATH entries were found.",
            SystemInfoImportScope.Startup => "No new startup items were found.",
            _ => "No new importable items were found."
        };
    }

    private static IReadOnlyList<ShellFolderPreset> GetShellFolderPresets()
    {
        return
        [
            new("Desktop", "Desktop", @"D:\{USERNAME}\Desktop"),
            new("Downloads", "{374DE290-123F-4565-9164-39C4925E467B}", @"D:\{USERNAME}\Downloads"),
            new("Documents", "Personal", @"D:\{USERNAME}\Documents"),
            new("Pictures", "My Pictures", @"D:\{USERNAME}\Pictures"),
            new("Music", "My Music", @"D:\{USERNAME}\Music"),
            new("Videos", "My Video", @"D:\{USERNAME}\Videos")
        ];
    }

    private static string GetImportLabel(ModuleDescriptor module)
    {
        return module.ImportScope switch
        {
            SystemInfoImportScope.AppInstaller => "Import Installed Apps",
            SystemInfoImportScope.Fonts => "Import Installed Fonts",
            SystemInfoImportScope.NetworkDrives => "Import Network Drives",
            SystemInfoImportScope.Symlinks => "Import Symlinks",
            SystemInfoImportScope.ShellFolders => "Import Shell Folders",
            SystemInfoImportScope.Path => "Import PATH Entries",
            SystemInfoImportScope.Startup => "Import Startup Items",
            _ => "Import System Info"
        };
    }

    private static string GetKnownPackageName(string packageId)
    {
        return packageId.ToLowerInvariant() switch
        {
            "discord.discord" => "Discord",
            "spotify.spotify" => "Spotify",
            "git.git" => "Git",
            "github.gitlfs" => "Git LFS",
            "microsoft.visualstudiocode" => "Visual Studio Code",
            "" => "New app",
            _ => packageId
        };
    }

    private static bool KnownBehaviorPackage(string packageId)
    {
        return packageId.Equals("Discord.Discord", StringComparison.OrdinalIgnoreCase) ||
               packageId.Equals("Spotify.Spotify", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetItemTitle(object item, Type itemType, int index)
    {
        return item switch
        {
            string value when !string.IsNullOrWhiteSpace(value) => value,
            NetworkDriveMapping drive when !string.IsNullOrWhiteSpace(drive.DriveLetter) => $"{drive.DriveLetter}: {drive.NetworkPath}",
            ShellFolderMapping folder when !string.IsNullOrWhiteSpace(folder.FolderName) => folder.FolderName,
            StartupProgram program when !string.IsNullOrWhiteSpace(program.Name) => program.Name,
            ProcessToRun process when !string.IsNullOrWhiteSpace(process.Path) => Path.GetFileName(process.Path),
            FileCopyOperation copy when !string.IsNullOrWhiteSpace(copy.Source) => Path.GetFileName(copy.Source),
            RegistryModification modification when !string.IsNullOrWhiteSpace(modification.Key) => modification.Key,
            CustomInstaller installer when !string.IsNullOrWhiteSpace(installer.Name) => installer.Name,
            SpecialSymlink symlink when !string.IsNullOrWhiteSpace(symlink.Description) => symlink.Description,
            _ => itemType == typeof(string) ? "New entry" : $"New {SplitName(itemType.Name).ToLowerInvariant()}"
        };
    }

    private static string GetConfigGlyph(PropertyInfo? property, object? item)
    {
        if (item is ShellFolderMapping folder)
        {
            return folder.FolderName.ToLowerInvariant() switch
            {
                "desktop" => "\uE80F",
                "downloads" => "\uE896",
                "documents" => "\uE8A5",
                "pictures" => "\uEB9F",
                "music" => "\uEC4F",
                "videos" => "\uE8B2",
                _ => "\uE8B7"
            };
        }

        if (item is NetworkDriveMapping)
            return "\uE839";
        if (item is StartupProgram or ProcessToRun)
            return "\uE768";
        if (item is FileCopyOperation)
            return "\uE8C8";
        if (item is RegistryModification)
            return "\uE7B8";
        if (item is CustomInstaller)
            return "\uE896";
        if (item is SpecialSymlink)
            return "\uE71B";

        var name = property?.Name ?? string.Empty;
        return name switch
        {
            "Folders" => "\uE8B7",
            "Drives" => "\uE839",
            "Additions" => "\uE943",
            "Programs" or "ProcessesToRun" => "\uE768",
            "FilesToImport" or "Modifications" => "\uE7B8",
            "Operations" => "\uE8C8",
            "PreparedInstallers" or "ManualInstalls" or "CustomScripts" or "DefaultInstalls" => "\uE896",
            "RoamingDirectories" => "\uE8B7",
            "LocalDirectories" => "\uE8B7",
            "LocalLowDirectories" => "\uE8B7",
            "SpecialSymlinks" => "\uE8A5",
            "FontsDirectory" => "\uE8D2",
            _ => "\uE8A5"
        };
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
        return value.EndsWith("Directories", StringComparison.Ordinal) ? value[..^3] + "y" :
            value.EndsWith("ies", StringComparison.Ordinal) ? value[..^3] + "y" :
            value.EndsWith('s') && value.Length > 1 ? value[..^1] : value;
    }

    private sealed record ShellFolderPreset(string Name, string RegistryValue, string DefaultPath);
    private sealed record SymlinkImportSelection(
        List<SystemInfoImportCandidate> Selected,
        List<SystemInfoImportCandidate> Ignored,
        SymlinkImportMode Mode);

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

    private sealed class BufferedTextBoxWriter(Action<string> write, string logArea) : TextWriter
    {
        private readonly object _lock = new();
        private readonly StringBuilder _buffer = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_lock)
            {
                _buffer.Append(value);
                if (value == '\n')
                    FlushLocked();
            }
        }

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return;

            lock (_lock)
            {
                _buffer.Append(value);
                if (value.Contains('\n', StringComparison.Ordinal))
                    FlushLocked();
            }
        }

        public override void WriteLine(string? value)
        {
            var line = (value ?? string.Empty) + Environment.NewLine;
            RunLog.Write(logArea, value ?? string.Empty);
            write(line);
        }

        public override void Flush()
        {
            lock (_lock)
                FlushLocked();
        }

        private void FlushLocked()
        {
            if (_buffer.Length == 0)
                return;

            var text = _buffer.ToString();
            _buffer.Clear();
            RunLog.Write(logArea, text.TrimEnd());
            write(text);
        }
    }
}

