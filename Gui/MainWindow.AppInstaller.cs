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
private FrameworkElement BuildAppInstallerTiles(AppInstallerConfig config)
    {
        var groupedSections = new List<AppGroupSection>();
        foreach (var group in RecommendedAppCatalog.Groups.Append(new RecommendedAppGroupInfo(RecommendedAppGroup.None, "Apps")))
        {
            var packageIds = new List<string>();
            var tiles = new VariableSizedWrapGrid
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            var tileByPackageId = new Dictionary<string, FrameworkElement>(StringComparer.OrdinalIgnoreCase);
            var isMaterialized = false;
            var chevron = new FontIcon
            {
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            var headerContent = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    chevron,
                    new TextBlock { Text = group.Title, FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 }, VerticalAlignment = VerticalAlignment.Center }
                }
            };
            var header = new Button
            {
                Content = headerContent,
                Padding = new Thickness(0),
                MinHeight = 32,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderBrush = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Left,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            header.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Colors.Transparent);
            header.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Colors.Transparent);
            header.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(Colors.Transparent);
            header.Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(Colors.Transparent);

            var body = new StackPanel { Spacing = 8, Children = { tiles } };
            var section = new StackPanel { Spacing = 8, Children = { header, body } };
            void RefreshTiles()
            {
                if (!isMaterialized)
                    return;

                var wanted = packageIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var packageId in tileByPackageId.Keys.Where(packageId => !wanted.Contains(packageId)).ToList())
                {
                    tiles.Children.Remove(tileByPackageId[packageId]);
                    tileByPackageId.Remove(packageId);
                }

                for (var index = 0; index < packageIds.Count; index++)
                {
                    var packageId = packageIds[index];
                    if (!tileByPackageId.TryGetValue(packageId, out var tile))
                    {
                        tile = BuildAppTile(packageId, config, Refresh);
                        tileByPackageId.Add(packageId, tile);
                    }

                    if (tiles.Children.IndexOf(tile) != index)
                    {
                        tiles.Children.Remove(tile);
                        tiles.Children.Insert(index, tile);
                    }
                }
            }
            void MaterializeTiles()
            {
                if (isMaterialized)
                    return;

                isMaterialized = true;
                RefreshTiles();
            }
            void SetExpanded(bool isExpanded)
            {
                _appInstallerGroupExpanded[group.Group] = isExpanded;
                chevron.Glyph = isExpanded ? "\uE70D" : "\uE76C";
                body.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;
                if (isExpanded)
                    MaterializeTiles();
            }

            var defaultExpanded = group.Group == RecommendedAppGroup.None;
            SetExpanded(_appInstallerGroupExpanded.TryGetValue(group.Group, out var isExpanded) ? isExpanded : defaultExpanded);
            header.Click += (_, _) =>
            {
                SetExpanded(!_appInstallerGroupExpanded[group.Group]);
                ConfigurationManager.SaveAppInstallerGroupExpanded(
                    _appInstallerGroupExpanded.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value));
            };
            groupedSections.Add(new AppGroupSection(group, packageIds, tiles, RefreshTiles, section));
        }

        void Refresh()
        {
            foreach (var section in groupedSections)
            {
                section.PackageIds.Clear();
                section.PackageIds.AddRange(config.DefaultInstalls
                    .Where(app => RecommendedAppCatalog.GetGroup(app) == section.Group.Group)
                    .OrderBy(RecommendedAppCatalog.GetGroupSortOrder)
                    .ThenBy(app => GetAppDisplayName(config, app), StringComparer.OrdinalIgnoreCase)
                    .ToList());
                section.Section.Visibility = section.PackageIds.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                if (_appInstallerGroupExpanded.TryGetValue(section.Group.Group, out var isExpanded) && isExpanded)
                    section.RefreshTiles();
            }
        }

        Refresh();
        var content = new StackPanel { Spacing = 12 };
        foreach (var section in groupedSections)
            content.Children.Add(section.Section);
        content.Children.Add(ActionButton("+ Add App", async () => await ShowAppBehaviorDialogAsync(config, null)));
        return content;
    }


    private FrameworkElement BuildAppTile(string packageId, AppInstallerConfig config, Action refresh)
    {
        var displayName = GetAppDisplayName(config, packageId);
        Uri? installerUrl = null;
        var download = IconActionButton("\uE896", "Direct download unavailable", () => OpenPackageUriAsync(installerUrl));
        download.IsEnabled = false;
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children =
            {
                IconActionButton("\uE74D", "Delete app", async () =>
                {
                    if (!await ConfirmAsync("Delete app?", $"Remove {displayName} from App Installer? This only removes its Winstaller configuration; it does not uninstall the app.", "Delete"))
                        return;
                    config.DefaultInstalls.RemoveAll(id => id.Equals(packageId, StringComparison.OrdinalIgnoreCase));
                    config.Behaviors.Remove(packageId);
                    SaveConfiguration();
                    refresh();
                }),
                download,
                IconActionButton("\uE713", "App settings", async () => await ShowAppBehaviorDialogAsync(config, packageId))
            }
        };

        var iconView = CreateAppIconView(packageId, 40, displayName, loadImmediately: false);
        var title = CreateAppTileTitle(displayName);
        var version = new TextBlock
        {
            Text = "Version loading…",
            FontSize = 11,
            Foreground = ResourceBrush("WinstallerSecondaryTextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var labels = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { title, version } };
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(iconView.Host);
        Grid.SetColumn(labels, 1);
        header.Children.Add(labels);

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(header);
        Grid.SetRow(footer, 1);
        content.Children.Add(footer);

        var tile = new Border
        {
            Width = 250,
            Height = 128,
            Margin = new Thickness(0, 0, 8, 8),
            Background = ResourceBrush("WinstallerCardBrush"),
            BorderBrush = ResourceBrush("WinstallerCardStrokeBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14),
            Child = content
        };
        var detailsLoaded = false;
        tile.EffectiveViewportChanged += (sender, args) =>
        {
            if (detailsLoaded || args.EffectiveViewport.IsEmpty)
                return;

            detailsLoaded = true;
            _ = LoadAppIconAsync(packageId, iconView.Image, iconView.Fallback, iconView.Spinner, displayName: displayName);
            _ = LoadAppMetadataAsync(packageId, version, download, url => installerUrl = url);
        };
        return tile;
    }

    private static TextBlock CreateAppTileTitle(string name)
    {
        const double availableWidth = 174;
        var title = new TextBlock
        {
            Text = name,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 },
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        title.FontSize = 14;
        title.Measure(new Size(availableWidth, double.PositiveInfinity));
        if (title.DesiredSize.Width <= availableWidth)
            return title;

        title.FontSize = 12.5;
        title.Measure(new Size(availableWidth, double.PositiveInfinity));
        if (title.DesiredSize.Width <= availableWidth * 1.12)
        {
            ToolTipService.SetToolTip(title, name);
            return title;
        }

        title.FontSize = 14;
        title.TextWrapping = TextWrapping.Wrap;
        title.MaxLines = 2;
        title.MaxHeight = 40;
        title.Measure(new Size(availableWidth, 40));
        if (title.DesiredSize.Height > 40)
            title.FontSize = 12.5;
        ToolTipService.SetToolTip(title, name);
        return title;
    }

    private AppIconView CreateAppIconView(string packageId, double size, string? displayName = null, Func<bool>? isCurrent = null, bool loadImmediately = true)
    {
        var host = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Left };
        var fallback = new FontIcon { Glyph = "\uE896", FontSize = size * 0.65, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        var spinner = new ProgressRing { Width = Math.Max(16, size * 0.55), Height = Math.Max(16, size * 0.55), IsActive = true, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var image = new Image { Width = size, Height = size, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
        host.Children.Add(fallback);
        host.Children.Add(spinner);
        host.Children.Add(image);
        if (loadImmediately)
            _ = LoadAppIconAsync(packageId, image, fallback, spinner, isCurrent, displayName);
        return new AppIconView(host, image, fallback, spinner);
    }

    private async Task LoadAppIconAsync(string packageId, Image icon, FontIcon fallback, ProgressRing spinner, Func<bool>? isCurrent = null, string? displayName = null)
    {
        try
        {
            var path = await AppIconService.GetIconPathAsync(packageId, displayName);
            if (isCurrent is not null && !isCurrent()) return;
            await RunOnUiThreadAsync(() =>
            {
                if (isCurrent is not null && !isCurrent()) return;
                void ShowFallback() { spinner.IsActive = false; spinner.Visibility = Visibility.Collapsed; fallback.Visibility = Visibility.Visible; }
                if (string.IsNullOrWhiteSpace(path)) { ShowFallback(); return; }
                void ShowIcon()
                {
                    if (isCurrent is not null && !isCurrent()) return;
                    spinner.IsActive = false; spinner.Visibility = Visibility.Collapsed;
                    icon.Visibility = Visibility.Visible; fallback.Visibility = Visibility.Collapsed;
                }
                void HandleFailure()
                {
                    if (isCurrent is not null && !isCurrent()) return;
                    AppIconService.Invalidate(packageId, path);
                    ShowFallback();
                    RunLog.Write("AppIcon", $"Image decode failed for {packageId}: {path}");
                }

                if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    var source = new SvgImageSource(new Uri(path)); source.Opened += (_, _) => ShowIcon(); source.OpenFailed += (_, _) => HandleFailure(); icon.Source = source;
                }
                else
                {
                    var bitmap = new BitmapImage { UriSource = new Uri(path), DecodePixelWidth = Math.Max(64, (int)Math.Ceiling(icon.Width)) };
                    bitmap.ImageOpened += (_, _) => ShowIcon(); bitmap.ImageFailed += (_, _) => HandleFailure(); icon.Source = bitmap;
                }
            });
        }
        catch (Exception ex)
        {
            RunLog.WriteException("AppIcon", $"Failed loading icon for {packageId}", ex);
            try
            {
                if (isCurrent is not null && !isCurrent()) return;
                await RunOnUiThreadAsync(() =>
                {
                    if (isCurrent is not null && !isCurrent()) return;
                    spinner.IsActive = false;
                    spinner.Visibility = Visibility.Collapsed;
                    fallback.Visibility = Visibility.Visible;
                });
            }
            catch (Exception uiException)
            {
                RunLog.WriteException("AppIcon", $"Failed showing fallback for {packageId}", uiException);
            }
        }
    }

    private async Task LoadAppMetadataAsync(string packageId, TextBlock version, Button download, Action<Uri?> setInstallerUrl, Func<bool>? isCurrent = null)
    {
        try
        {
            var metadata = await WingetPackageMetadataService.GetAsync(packageId);
            if (isCurrent is not null && !isCurrent()) return;
            await RunOnUiThreadAsync(() =>
            {
                if (isCurrent is not null && !isCurrent()) return;
                version.Text = string.IsNullOrWhiteSpace(metadata.Version) ? "Version unavailable" : $"Version {metadata.Version}";
                setInstallerUrl(metadata.InstallerUrl);
                download.IsEnabled = metadata.InstallerUrl is not null;
                var label = metadata.InstallerUrl is null
                    ? "Direct download unavailable"
                    : RecommendedAppCatalog.IsMicrosoftStorePackage(packageId)
                        ? "Open in Microsoft Store"
                        : "Open direct download";
                ToolTipService.SetToolTip(download, label);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(download, label);
            });
        }
        catch (Exception ex)
        {
            RunLog.WriteException("WingetMetadata", $"Failed showing metadata for {packageId}", ex);
            try
            {
                if (isCurrent is not null && !isCurrent()) return;
                await RunOnUiThreadAsync(() =>
                {
                    if (isCurrent is null || isCurrent())
                        version.Text = "Version unavailable";
                });
            }
            catch (Exception uiException)
            {
                RunLog.WriteException("WingetMetadata", $"Failed showing unavailable metadata for {packageId}", uiException);
            }
        }
    }

    private async Task OpenPackageUriAsync(Uri? uri)
    {
        if (uri is null || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != "ms-windows-store")) return;
        try
        {
            if (uri.Scheme == "ms-windows-store")
            {
                if (!await Windows.System.Launcher.LaunchUriAsync(uri))
                    throw new InvalidOperationException("Windows could not open Microsoft Store.");
                return;
            }

            Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            RunLog.WriteException("UI", $"Failed opening {uri}", ex);
            AppendOutput($"Failed to open download: {ex.Message}");
        }
    }

    private static string GetAppDisplayName(AppInstallerConfig config, string packageId)
    {
        var displayName = config.Behaviors.TryGetValue(packageId, out var behavior) && !string.IsNullOrWhiteSpace(behavior.DisplayName)
            ? behavior.DisplayName
            : GetKnownPackageName(packageId);
        return RecommendedAppCatalog.NormalizeExistingDisplayName(packageId, displayName);
    }

    private async Task ShowAppBehaviorDialogAsync(AppInstallerConfig config, string? packageId)
    {
        var isNew = string.IsNullOrWhiteSpace(packageId);
        var behavior = !isNew && config.Behaviors.TryGetValue(packageId!, out var existing)
            ? CloneAppBehavior(existing)
            : new AppInstallBehavior { DisplayName = isNew ? string.Empty : GetKnownPackageName(packageId!) };
        var name = new TextBox { Text = isNew ? behavior.DisplayName : GetAppDisplayName(config, packageId!), PlaceholderText = "App name" };
        var id = new TextBox { Text = packageId ?? string.Empty, PlaceholderText = "Winget package ID" };
        EnableAppSettingsTextCopy(name);
        EnableAppSettingsTextCopy(id);
        var iconPreviewActive = true;
        var iconPreview = CreateAppIconView(id.Text, 64, name.Text, () => iconPreviewActive);
        var iconGeneration = 0;
        CancellationTokenSource? iconPreviewCancellation = null;
        async Task RefreshIconPreviewAsync()
        {
            iconPreviewCancellation?.Cancel();
            iconPreviewCancellation?.Dispose();
            iconPreviewCancellation = new CancellationTokenSource();
            var cancellationToken = iconPreviewCancellation.Token;
            var generation = ++iconGeneration;
            if (!iconPreviewActive) return;
            iconPreview.Image.Visibility = Visibility.Collapsed;
            iconPreview.Fallback.Visibility = Visibility.Collapsed;
            iconPreview.Spinner.Visibility = Visibility.Visible;
            iconPreview.Spinner.IsActive = true;
            var typedId = id.Text.Trim();
            if (string.IsNullOrWhiteSpace(typedId))
            {
                iconPreview.Spinner.IsActive = false;
                iconPreview.Spinner.Visibility = Visibility.Collapsed;
                iconPreview.Fallback.Visibility = Visibility.Visible;
                return;
            }
            try
            {
                await Task.Delay(600, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (!iconPreviewActive || generation != iconGeneration) return;
            await LoadAppIconAsync(typedId, iconPreview.Image, iconPreview.Fallback, iconPreview.Spinner, () => iconPreviewActive && generation == iconGeneration, name.Text);
        }
        id.TextChanged += (_, _) => _ = RefreshIconPreviewAsync();
        var mode = new ComboBox { MinWidth = 260 };
        void RefreshModes()
        {
            var selected = behavior.InstallMode;
            mode.Items.Clear();
            mode.Items.Add($"Auto ({GetAutoInstallDescription(id.Text)})");
            mode.Items.Add("WinGet");
            mode.Items.Add("Prepared (saved INF)");
            mode.Items.Add("Manual (interactive installer)");
            mode.SelectedIndex = (int)selected;
        }
        RefreshModes();
        AppInstallMode SelectedMode() => (AppInstallMode)Math.Clamp(mode.SelectedIndex, 0, 3);
        var lockVersion = new CheckBox { Content = "Lock version", IsChecked = behavior.LockVersion };
        var version = new TextBox { Text = behavior.Version, PlaceholderText = "Version" };
        EnableAppSettingsTextCopy(version);
        void UpdateVersionState()
        {
            version.IsEnabled = lockVersion.IsChecked is true;
        }
        lockVersion.Checked += (_, _) => UpdateVersionState();
        lockVersion.Unchecked += (_, _) => UpdateVersionState();
        UpdateVersionState();

        var panel = new StackPanel { Spacing = 10 };
        var appNameRow = new Grid { ColumnSpacing = 12 };
        appNameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        appNameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        iconPreview.Host.VerticalAlignment = VerticalAlignment.Top;
        appNameRow.Children.Add(iconPreview.Host);
        var appNameEditor = new StackPanel { Spacing = 6 };
        appNameEditor.Children.Add(new TextBlock { Text = "App name" });
        appNameEditor.Children.Add(name);
        Grid.SetColumn(appNameEditor, 1);
        appNameRow.Children.Add(appNameEditor);
        panel.Children.Add(appNameRow);
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
            if (SelectedMode() != AppInstallMode.Auto)
            {
                customOptions.Children.Add(new TextBlock { Text = "Custom app settings apply only in Auto mode.", Foreground = ResourceBrush("WinstallerSecondaryTextBrush") });
                return;
            }
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
            else if (typedId.Equals("Microsoft.VisualStudioCode", StringComparison.OrdinalIgnoreCase))
            {
                customOptions.Children.Add(new TextBlock { Text = "Auto downloads official User Setup. Enables Open with Code menus and PATH. File associations stay unchanged.", TextWrapping = TextWrapping.Wrap, Foreground = ResourceBrush("WinstallerSecondaryTextBrush") });
            }
            else if (typedId.Equals("Git.Git", StringComparison.OrdinalIgnoreCase))
            {
                customOptions.Children.Add(BuildGitInstallOptions(behavior.Git));
            }
            EnableAppSettingsTextCopyInTree(customOptions);
        }
        id.TextChanged += (_, _) => { RefreshModes(); RefreshCustomOptions(); };
        mode.SelectionChanged += (_, _) => RefreshCustomOptions();
        panel.Children.Add(customOptions);
        RefreshCustomOptions();

        AppInstallBehavior BuildDraft()
        {
            var draft = CloneAppBehavior(behavior);
            draft.DisplayName = string.IsNullOrWhiteSpace(name.Text) ? GetKnownPackageName(id.Text.Trim()) : name.Text.Trim();
            draft.InstallMode = SelectedMode();
            draft.LockVersion = lockVersion.IsChecked is true;
            draft.Version = draft.LockVersion ? version.Text.Trim() : string.Empty;
            return draft;
        }

        string GetDraftState() => JsonSerializer.Serialize(new AppBehaviorDialogState(id.Text.Trim(), BuildDraft()), JsonOptions);
        var initialDraftState = GetDraftState();

        async Task<bool> SaveDraftAsync()
        {
            var newId = id.Text.Trim();
            if (string.IsNullOrWhiteSpace(newId))
            {
                await ShowMessageAsync("App Settings", "Winget package ID is required.");
                return false;
            }
            if (lockVersion.IsChecked is true && string.IsNullOrWhiteSpace(version.Text))
            {
                await ShowMessageAsync("App Settings", "Version is required when version locking is enabled.");
                return false;
            }
            if ((isNew || !newId.Equals(packageId, StringComparison.OrdinalIgnoreCase)) &&
                config.DefaultInstalls.Contains(newId, StringComparer.OrdinalIgnoreCase))
            {
                await ShowMessageAsync("App Settings", "That package ID is already configured.");
                return false;
            }

            var draft = BuildDraft();
            if (isNew)
                config.DefaultInstalls.Add(newId);
            else if (!newId.Equals(packageId, StringComparison.OrdinalIgnoreCase))
            {
                var index = config.DefaultInstalls.FindIndex(existingId => existingId.Equals(packageId, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                    config.DefaultInstalls[index] = newId;
                config.Behaviors.Remove(packageId!);
            }
            config.Behaviors[newId] = draft;
            SaveConfiguration();
            RenderModule(_modules.First(module => ReferenceEquals(module.Config, config)));
            return true;
        }

        try
        {
            while (true)
            {
                var saved = false;
                var dismissed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var width = Math.Min(720, Math.Max(360, RootGrid.ActualWidth - 32));
                var popup = new Popup
                {
                    XamlRoot = RootGrid.XamlRoot,
                    IsLightDismissEnabled = false
                };
                var popupBackground = new SolidColorBrush(RootGrid.ActualTheme == ElementTheme.Dark
                    ? ColorHelper.FromArgb(255, 38, 38, 38)
                    : ColorHelper.FromArgb(255, 250, 250, 250));
                var save = new Button
                {
                    Content = "Save",
                    MinWidth = 92,
                    Style = (Style)Application.Current.Resources["AccentButtonStyle"]
                };
                var close = new Button { Content = "Close", MinWidth = 92 };
                var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { close, save } };
                var popupContent = new StackPanel { Spacing = 14, Children = { new TextBlock { Text = isNew ? "Add App" : "App Settings", FontSize = 20, FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 } }, new ScrollViewer { Content = panel, MaxHeight = 540 }, actions } };
                var card = new Border { Width = width, MaxHeight = 680, Padding = new Thickness(20), Background = popupBackground, BorderBrush = ResourceBrush("WinstallerCardStrokeBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = popupContent };
                var overlay = new Grid { Width = RootGrid.ActualWidth, Height = RootGrid.ActualHeight, Background = ResourceBrush("WinstallerModalOverlayBrush"), Children = { card } };
                overlay.PointerPressed += (_, args) =>
                {
                    var point = args.GetCurrentPoint(card).Position;
                    if (point.X < 0 || point.Y < 0 || point.X > card.ActualWidth || point.Y > card.ActualHeight)
                    {
                        args.Handled = true;
                        popup.IsOpen = false;
                    }
                };
                popup.Child = overlay;
                close.Click += (_, _) => popup.IsOpen = false;
                save.Click += async (_, _) =>
                {
                    save.IsEnabled = false;
                    if (await SaveDraftAsync()) { saved = true; popup.IsOpen = false; }
                    save.IsEnabled = true;
                };
                popup.Closed += (_, _) => dismissed.TrySetResult();
                popup.IsOpen = true;
                await dismissed.Task;
                if (saved) return;

                if (GetDraftState().Equals(initialDraftState, StringComparison.Ordinal))
                    return;

                var closePrompt = new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot,
                    Title = "Save changes?",
                    Content = new TextBlock { Text = "Save changes to this app before closing?", TextWrapping = TextWrapping.Wrap },
                    PrimaryButtonText = "Save",
                    SecondaryButtonText = "Discard",
                    CloseButtonText = "Keep Editing"
                };
                var closeResult = await closePrompt.ShowAsync();
                if (closeResult == ContentDialogResult.Secondary)
                    return;
                if (closeResult == ContentDialogResult.Primary && await SaveDraftAsync())
                    return;
            }
        }
        finally
        {
            iconPreviewActive = false;
            ++iconGeneration;
            iconPreviewCancellation?.Cancel();
            iconPreviewCancellation?.Dispose();
        }
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
            },
            Git = CloneGitInstallOptions(source.Git)
        };
    }

    private static string GetAutoInstallDescription(string packageId) => packageId.Trim() switch
    {
        "Microsoft.VisualStudioCode" => "VS Code User Setup",
        "Git.Git" => "Git installer",
        _ => "WinGet"
    };


    private static GitInstallOptions CloneGitInstallOptions(GitInstallOptions source) => new()
    {
        DesktopIcon = source.DesktopIcon, GitBashHere = source.GitBashHere, GitGuiHere = source.GitGuiHere, GitLfs = source.GitLfs, AssociateGitFiles = source.AssociateGitFiles, AssociateShellFiles = source.AssociateShellFiles, WindowsTerminalProfile = source.WindowsTerminalProfile, Scalar = source.Scalar, CheckForUpdates = source.CheckForUpdates, Editor = source.Editor, CustomEditorPath = source.CustomEditorPath, DefaultBranch = source.DefaultBranch, Path = source.Path, Ssh = source.Ssh, PlinkPath = source.PlinkPath, UseTortoisePlink = source.UseTortoisePlink, Https = source.Https, LineEndings = source.LineEndings, Terminal = source.Terminal, PullBehavior = source.PullBehavior, CredentialManager = source.CredentialManager, FileSystemCache = source.FileSystemCache, Symlinks = source.Symlinks, MandatoryAslr = source.MandatoryAslr, BuiltinDifftool = source.BuiltinDifftool, BuiltinRebase = source.BuiltinRebase, BuiltinStash = source.BuiltinStash, BuiltinInteractiveAdd = source.BuiltinInteractiveAdd, PseudoConsole = source.PseudoConsole, FileSystemMonitor = source.FileSystemMonitor
    };

private FrameworkElement BuildGitInstallOptions(GitInstallOptions options)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "Components", FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 } });
        var components = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalSpacing = 18, VerticalSpacing = 8 };
        foreach (var (name, description) in new[]
        {
            (nameof(GitInstallOptions.DesktopIcon), "Create a desktop shortcut"), (nameof(GitInstallOptions.GitBashHere), "Add Git Bash Here to Explorer"), (nameof(GitInstallOptions.GitGuiHere), "Add Git GUI Here to Explorer"), (nameof(GitInstallOptions.GitLfs), "Install Git Large File Storage"), (nameof(GitInstallOptions.AssociateGitFiles), "Open .git* files in editor"), (nameof(GitInstallOptions.AssociateShellFiles), "Run .sh files with Git Bash"), (nameof(GitInstallOptions.WindowsTerminalProfile), "Add Git Bash to Windows Terminal"), (nameof(GitInstallOptions.Scalar), "Install Scalar for large repositories"), (nameof(GitInstallOptions.CheckForUpdates), "Check daily for Git updates")
        }) components.Children.Add(BuildGitCheckBox(options, name, SplitName(name), description));
        panel.Children.Add(components);

        panel.Children.Add(new TextBlock { Text = "Git behavior", FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 } });
        var behavior = new StackPanel { Spacing = 8 };
        behavior.Children.Add(BuildGitChoice(options, nameof(GitInstallOptions.Editor), "Default editor", "Editor opened for commit messages."));
        behavior.Children.Add(BuildGitText(options, nameof(GitInstallOptions.CustomEditorPath), "Custom editor command", "Used only for Custom editor."));
        behavior.Children.Add(BuildGitText(options, nameof(GitInstallOptions.DefaultBranch), "Initial branch name", "Leave blank for Git installer default."));
        behavior.Children.Add(BuildGitChoice(options, nameof(GitInstallOptions.Path), "PATH environment", "Where Git commands are available."));
        behavior.Children.Add(BuildGitChoice(options, nameof(GitInstallOptions.Ssh), "SSH client", "Used for SSH remotes."));
        behavior.Children.Add(BuildGitText(options, nameof(GitInstallOptions.PlinkPath), "Plink executable", "Used only when SSH client is Plink."));
        behavior.Children.Add(BuildGitCheckBox(options, nameof(GitInstallOptions.UseTortoisePlink), "Use TortoisePlink", "Used only with Plink."));
        behavior.Children.Add(BuildGitChoice(options, nameof(GitInstallOptions.Https), "HTTPS backend", "OpenSSL or Windows certificate store."));
        behavior.Children.Add(BuildGitChoice(options, nameof(GitInstallOptions.LineEndings), "Line endings", "How Git converts LF and CRLF."));
        behavior.Children.Add(BuildGitChoice(options, nameof(GitInstallOptions.Terminal), "Git Bash terminal", "MinTTY terminal or Windows console host."));
        behavior.Children.Add(BuildGitChoice(options, nameof(GitInstallOptions.PullBehavior), "git pull behavior", "Merge, rebase, or fast-forward only."));
        panel.Children.Add(behavior);

        panel.Children.Add(new TextBlock { Text = "Performance", FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 } });
        var performance = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalSpacing = 18, VerticalSpacing = 8 };
        performance.Children.Add(BuildGitCheckBox(options, nameof(GitInstallOptions.CredentialManager), "Git Credential Manager", "Store and refresh credentials."));
        performance.Children.Add(BuildGitCheckBox(options, nameof(GitInstallOptions.FileSystemCache), "Filesystem cache", "Cache filesystem metadata."));
        performance.Children.Add(BuildGitChoice(options, nameof(GitInstallOptions.Symlinks), "Symbolic links", "Auto uses installer default."));
        panel.Children.Add(performance);

        var advanced = new StackPanel { Spacing = 8 };
        foreach (var (name, label, description) in new[]
        {
            (nameof(GitInstallOptions.MandatoryAslr), "ASLR exceptions", "Compatibility exceptions for mandatory ASLR."), (nameof(GitInstallOptions.BuiltinDifftool), "Built-in difftool", "Use Git built-in difftool."), (nameof(GitInstallOptions.BuiltinRebase), "Built-in rebase", "Use Git built-in rebase."), (nameof(GitInstallOptions.BuiltinStash), "Built-in stash", "Use Git built-in stash."), (nameof(GitInstallOptions.BuiltinInteractiveAdd), "Built-in interactive add", "Use Git built-in interactive add."), (nameof(GitInstallOptions.PseudoConsole), "Pseudo console", "Use Windows pseudoconsole support."), (nameof(GitInstallOptions.FileSystemMonitor), "Filesystem monitor", "Watch repository changes.")
        }) advanced.Children.Add(BuildGitChoice(options, name, label, description));
        var chevron = new FontIcon { Glyph = "\uE76C", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var header = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { chevron, new TextBlock { Text = "Advanced", VerticalAlignment = VerticalAlignment.Center } } }, Padding = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent), BorderBrush = new SolidColorBrush(Colors.Transparent), HorizontalAlignment = HorizontalAlignment.Left };
        header.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Colors.Transparent);
        header.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Colors.Transparent);
        advanced.Visibility = Visibility.Collapsed;
        header.Click += (_, _) => { var expanded = advanced.Visibility != Visibility.Visible; advanced.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed; chevron.Glyph = expanded ? "\uE70D" : "\uE76C"; };
        panel.Children.Add(header);
        panel.Children.Add(advanced);
        return panel;
    }

    private FrameworkElement BuildGitCheckBox(GitInstallOptions options, string propertyName, string label, string description)
    {
        var property = typeof(GitInstallOptions).GetProperty(propertyName)!;
        var box = new CheckBox { Content = label, IsChecked = property.GetValue(options) is true, MinWidth = 225 };
        ToolTipService.SetToolTip(box, description);
        box.Checked += (_, _) => property.SetValue(options, true);
        box.Unchecked += (_, _) => property.SetValue(options, false);
        return box;
    }

    private FrameworkElement BuildGitChoice(GitInstallOptions options, string propertyName, string label, string description)
    {
        var property = typeof(GitInstallOptions).GetProperty(propertyName)!;
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var selectedValue = property.GetValue(options);
        foreach (var value in Enum.GetValues(property.PropertyType))
        {
            var item = new ComboBoxItem { Content = GetGitChoiceLabel((Enum)value), Tag = value };
            combo.Items.Add(item);
            if (Equals(value, selectedValue)) combo.SelectedItem = item;
        }
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ComboBoxItem { Tag: not null } item) property.SetValue(options, item.Tag); };
        return BuildGitRow(label, description, combo);
    }

    private FrameworkElement BuildGitText(GitInstallOptions options, string propertyName, string label, string description)
    {
        var property = typeof(GitInstallOptions).GetProperty(propertyName)!;
        var box = new TextBox { Text = property.GetValue(options)?.ToString() ?? string.Empty, HorizontalAlignment = HorizontalAlignment.Stretch };
        box.TextChanged += (_, _) => property.SetValue(options, box.Text);
        return BuildGitRow(label, description, box);
    }

    private FrameworkElement BuildGitRow(string label, string description, FrameworkElement editor)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new StackPanel { Spacing = 2, Children = { new TextBlock { Text = label }, new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = ResourceBrush("WinstallerSecondaryTextBrush") } } });
        Grid.SetColumn(editor, 1); grid.Children.Add(editor);
        return grid;
    }
    private static string GetGitChoiceLabel(Enum value) => value switch
    {
        GitEditor.Nano => "Nano",
        GitEditor.Vim => "Vim",
        GitEditor.NotepadPlusPlus => "Notepad++",
        GitEditor.VisualStudioCode => "Visual Studio Code",
        GitEditor.VisualStudioCodeInsiders => "Visual Studio Code Insiders",
        GitEditor.SublimeText => "Sublime Text",
        GitEditor.Atom => "Atom",
        GitEditor.VSCodium => "VSCodium",
        GitEditor.Notepad => "Notepad",
        GitEditor.Wordpad => "WordPad",
        GitEditor.MicrosoftEdit => "Microsoft Edit",
        GitEditor.CustomEditor => "Custom editor command",
        GitPath.BashOnly => "Git Bash only",
        GitPath.Cmd => "Git from Command Prompt and third-party software",
        GitPath.CmdTools => "Git and optional Unix tools from Command Prompt",
        GitSsh.OpenSsh => "Bundled OpenSSH",
        GitSsh.ExternalOpenSsh => "External OpenSSH",
        GitSsh.Plink => "Plink",
        GitHttps.OpenSsl => "OpenSSL",
        GitHttps.WinSsl => "Windows Secure Channel",
        GitLineEndings.LFOnly => "Checkout as-is, commit Unix-style LF",
        GitLineEndings.CRLFAlways => "Checkout Windows-style CRLF, commit Unix-style LF",
        GitLineEndings.CRLFCommitAsIs => "Checkout as-is, commit as-is",
        GitTerminal.MinTTY => "MinTTY",
        GitTerminal.ConHost => "Windows Console Host",
        GitPullBehavior.Merge => "Merge",
        GitPullBehavior.Rebase => "Rebase",
        GitPullBehavior.FFOnly => "Fast-forward only",
        GitTriState.Auto => "Auto (Git installer default)",
        GitTriState.Enabled => "Enabled",
        GitTriState.Disabled => "Disabled",
        _ => value.ToString()
    };
}
