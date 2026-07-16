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
    private readonly Dictionary<RecommendedAppGroup, bool> _appInstallerGroupExpanded = [];
    private readonly Dictionary<string, FrameworkElement> _pageCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _pageScrollOffsets = new(StringComparer.Ordinal);

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
    private string? _currentPageKey;

    private const string DashboardPageKey = "Dashboard";

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
        SetTitleBar(titleBar);
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


    private void LoadConfiguration()
    {
        _pageCache.Clear();
        _pageScrollOffsets.Clear();
        _currentPageKey = null;
        _config = ConfigurationManager.LoadConfiguration();
        _appInstallerGroupExpanded.Clear();
        foreach (var (groupName, isExpanded) in ConfigurationManager.LoadAppInstallerGroupExpanded())
        {
            if (Enum.TryParse<RecommendedAppGroup>(groupName, ignoreCase: true, out var group))
                _appInstallerGroupExpanded[group] = isExpanded;
        }
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
        _pageCache.Clear();
        _pageScrollOffsets.Clear();
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
}

