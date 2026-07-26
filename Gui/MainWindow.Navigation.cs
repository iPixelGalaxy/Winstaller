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


    private void NavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is ModuleDescriptor module)
        {
            RenderModule(module);
            return;
        }

        if (args.SelectedItemContainer?.Tag is string page && page == PreReinstallChecklistPageKey)
        {
            RenderPreReinstallChecklist();
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


    private void RenderDashboard()
    {
        _pageRenderVersion++;
        _isLoadingUi = true;
        SetDashboardTopBarActions();
        ShowCachedPage(DashboardPageKey, () =>
        {
            var page = new StackPanel { Spacing = 12 };
            page.Children.Add(PageTitle("Dashboard", "Choose what Winstaller should restore or install."));
            if (!ConfigurationManager.IsGuidedSetupReminderHidden())
                page.Children.Add(GuidedSetupCard());

            var list = new StackPanel { Spacing = 8 };
            foreach (var module in _modules)
                list.Children.Add(ModuleCard(module));
            page.Children.Add(list);
            return page;
        });

        _isLoadingUi = false;
    }

    private void RenderModule(ModuleDescriptor module)
    {
        var renderVersion = ++_pageRenderVersion;
        _isLoadingUi = true;
        SetModuleTopBarActions(module);
        if (_pageCache.TryGetValue(module.Name, out var cachedPage))
        {
            ShowPage(module.Name, cachedPage);
            _isLoadingUi = false;
            return;
        }

        ShowPage(module.Name, ModuleLoadingPage(module));
        DispatcherQueue.TryEnqueue(() =>
        {
            if (renderVersion != _pageRenderVersion)
                return;

            try
            {
                ShowCachedPage(module.Name, () => new StackPanel
                {
                    Spacing = 12,
                    Children = { ModulePageHeader(module), BuildModuleContent(module) }
                });
            }
            finally
            {
                _isLoadingUi = false;
            }
        });
    }

    private void RenderPreReinstallChecklist()
    {
        _pageRenderVersion++;
        _isLoadingUi = true;
        _topBarActions.Children.Clear();
        _topBarActionLabels.Clear();
        _topBarActions.Children.Add(TopBarActionButton(Symbol.Back, "Dashboard", RenderDashboard));
        UpdateTopBarActionLabelVisibility();
        ShowCachedPage(PreReinstallChecklistPageKey, BuildPreReinstallChecklistPage);
        _isLoadingUi = false;
    }

    private void ShowCachedPage(string key, Func<FrameworkElement> createPage)
    {
        if (_currentPageKey is not null)
            _pageScrollOffsets[_currentPageKey] = _contentScroll.VerticalOffset;

        if (!_pageCache.TryGetValue(key, out var page))
        {
            page = createPage();
            _pageCache[key] = page;
        }

        ShowPage(key, page);
    }

    private void ShowPage(string key, FrameworkElement page)
    {
        if (_currentPageKey is not null)
            _pageScrollOffsets[_currentPageKey] = _contentScroll.VerticalOffset;

        if (_content.Children.Count == 1 && ReferenceEquals(_content.Children[0], page))
            return;

        _content.Children.Clear();
        _content.Children.Add(page);
        _currentPageKey = key;
        var verticalOffset = _pageScrollOffsets.GetValueOrDefault(key);
        DispatcherQueue.TryEnqueue(() => _contentScroll.ChangeView(null, verticalOffset, null, disableAnimation: true));
    }

    private FrameworkElement ModuleLoadingPage(ModuleDescriptor module)
    {
        return new StackPanel
        {
            Spacing = 16,
            VerticalAlignment = VerticalAlignment.Top,
            Children =
            {
                ModulePageHeader(module),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children =
                    {
                        new ProgressRing { IsActive = true, Width = 24, Height = 24 },
                        new TextBlock { Text = $"Loading {module.Name}…", VerticalAlignment = VerticalAlignment.Center }
                    }
                }
            }
        };
    }

    private void InvalidateCachedPage(string key)
    {
        _pageCache.Remove(key);
        _pageScrollOffsets.Remove(key);
    }

    private void SetDashboardTopBarActions()
    {
        _topBarActions.Children.Clear();
        _topBarActionLabels.Clear();
        _topBarActions.Children.Add(TopBarActionButton(Symbol.Play, "Run All Enabled", async () => await ConfirmAndRunModulesAsync(_modules.Where(m => m.IsEnabled).ToList()), primary: true));
        _topBarActions.Children.Add(TopBarActionButton(Symbol.Help, "Guided Setup", async () => await RunGuidedSetupAsync()));
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
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(ActionButton("Start", async () => await RunGuidedSetupAsync(), primary: true));
        actions.Children.Add(ActionButton("Hide Reminder", async () =>
        {
            if (!await ConfirmAsync("Hide guided setup reminder?", "This removes it from Dashboard. Guided Setup remains in Dashboard top bar.", "Hide"))
                return;

            ConfigurationManager.SetGuidedSetupReminderHidden(true);
            InvalidateCachedPage(DashboardPageKey);
            RenderDashboard();
        }));
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);
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
            InvalidateCachedPage(DashboardPageKey);
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
            InvalidateCachedPage(module.Name);
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
        if (module.Config is FontsConfig fonts)
        {
            return BuildFontsContent(fonts);
        }
        if (module.Config is ShellFoldersConfig shellFolders)
        {
            return BuildShellFoldersContent(shellFolders);
        }
        if (module.Config is PathConfig path)
        {
            return BuildPathContent(path);
        }
        if (module.Config is NetworkDrivesConfig networkDrives)
        {
            return BuildNetworkDrivesContent(networkDrives);
        }
        if (module.Config is StartupConfig startup)
        {
            return BuildStartupContent(startup);
        }
        if (module.Config is FileCopyConfig fileCopy)
        {
            return BuildFileCopyContent(fileCopy);
        }
        if (module.Config is SymlinksConfig symlinks)
        {
            return BuildSymlinksContent(symlinks);
        }
        if (module.Config is RegistryConfig registry)
        {
            return BuildRegistryContent(registry);
        }
        if (module.Config is VRChatRegistryConfig vrchatRegistry)
        {
            return BuildVRChatRegistryContent(vrchatRegistry);
        }

        if (module.Config is SystemSettingsConfig systemSettings)
        {
            return BuildSystemSettingsContent(systemSettings);
        }

        if (module.Config is FirewallConfig or SetupTasksConfig)
        {
            return BuildConfigEditor(module.Config, includeScalarSettings: true);
        }

        return BuildConfigEditor(module.Config, includeScalarSettings: false);
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
        _navigation.MenuItems.Add(new NavigationViewItem
        {
            Content = "Pre-reinstall Checklist",
            Icon = new SymbolIcon(Symbol.Accept),
            Tag = PreReinstallChecklistPageKey
        });
        _navigation.MenuItems.Add(new NavigationViewItemSeparator());
        _navigation.MenuItems.Add(new NavigationViewItemHeader { Content = "Basic" });
        foreach (var moduleName in BasicModuleOrder)
        {
            var module = _modules.FirstOrDefault(module => module.Name == moduleName);
            if (module is null)
                continue;

            _navigation.MenuItems.Add(new NavigationViewItem
            {
                Content = module.Name,
                Icon = new FontIcon { Glyph = module.IconGlyph },
                Tag = module
            });
        }
        _navigation.MenuItems.Add(new NavigationViewItemSeparator());
        _navigation.MenuItems.Add(new NavigationViewItemHeader { Content = "Advanced" });
        foreach (var moduleName in AdvancedModuleOrder)
        {
            var module = _modules.FirstOrDefault(module => module.Name == moduleName);
            if (module is null)
                continue;

            _navigation.MenuItems.Add(new NavigationViewItem
            {
                Content = module.Name,
                Icon = new FontIcon { Glyph = module.IconGlyph },
                Tag = module
            });
        }
        _navigation.MenuItems.Add(new NavigationViewItemSeparator());
        _navigation.MenuItems.Add(new NavigationViewItemHeader { Content = "Niche" });
        foreach (var module in _modules.Where(module => IsNicheModule(module.Name)))
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
                if (ReferenceEquals(_navigation.SelectedItem, item))
                    RenderModule(module);
                else
                    _navigation.SelectedItem = item;
                return;
            }
        }
    }
}

