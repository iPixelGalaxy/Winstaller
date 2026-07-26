using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Winstaller.Configuration;
using Winstaller.Utilities;

namespace Winstaller.Gui;

public sealed partial class MainWindow
{
    private FrameworkElement BuildPreReinstallChecklistPage()
    {
        var page = new StackPanel { Spacing = 12 };
        page.Children.Add(PageTitle("Pre-reinstall Checklist", "Review current Windows changes and add selected items to their Winstaller sections."));
        var scanStatus = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var spinner = new ProgressRing { IsActive = true, Width = 20, Height = 20, Visibility = Visibility.Collapsed };
        scanStatus.Children.Add(spinner);
        var status = new TextBlock { Text = "Scanning current Windows configuration…", Foreground = ResourceBrush("WinstallerSecondaryTextBrush"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        scanStatus.Children.Add(status);
        page.Children.Add(scanStatus);
        page.Children.Add(new TextBlock { Text = $"Scan log: {RunLog.Path}", FontSize = 12, Foreground = ResourceBrush("WinstallerSecondaryTextBrush"), TextWrapping = TextWrapping.Wrap });

        var findings = new StackPanel { Spacing = 8 };
        page.Children.Add(findings);
        var checks = new List<(SystemInfoImportCandidate Candidate, CheckBox Check)>();
        Button? scan = null;
        Button? run = null;

        async Task ScanAsync()
        {
            run!.IsEnabled = false;
            scan!.IsEnabled = false;
            spinner.Visibility = Visibility.Visible;
            status.Text = "Scanning current Windows configuration…";
            RunLog.Write("Pre-reinstall Checklist", "Scan requested.");
            findings.Children.Clear();
            checks.Clear();
            try
            {
                var candidates = (await Task.Run(async () => await SystemInfoImportService.FindCandidatesAsync(
                    _config,
                    SystemInfoImportScope.All,
                    includeUpdates: true,
                    progress: message => DispatcherQueue.TryEnqueue(() => status.Text = message))).ConfigureAwait(false))
                    .Where(candidate => candidate.Scope != SystemInfoImportScope.Firewall)
                    .Where(candidate => candidate.Group != "Ignored")
                    .ToList();
                await RunOnUiThreadAsync(() =>
                {
                    var findingCards = new List<FrameworkElement>();
                    foreach (var group in candidates.GroupBy(candidate => candidate.Group == "Changed" ? "Changed configuration" : $"New {SplitName(candidate.Scope.ToString())}"))
                    {
                        var groupPanel = new StackPanel { Spacing = 6 };
                        groupPanel.Children.Add(new TextBlock { Text = group.Key, FontSize = 16, FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 } });
                        foreach (var candidate in group)
                        {
                            var content = new StackPanel { Spacing = 2 };
                            content.Children.Add(new TextBlock { Text = candidate.Title, TextWrapping = TextWrapping.Wrap });
                            content.Children.Add(new TextBlock { Text = candidate.Detail, FontSize = 12, Foreground = ResourceBrush("WinstallerSecondaryTextBrush"), TextWrapping = TextWrapping.Wrap });
                            FrameworkElement checkContent = content;
                            if (candidate.Value is AppImportCandidate app)
                            {
                                var appContent = new Grid { ColumnSpacing = 8 };
                                appContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                                appContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                                appContent.Children.Add(CreateAppIconView(app.PackageId, 32, app.DisplayName).Host);
                                Grid.SetColumn(content, 1);
                                appContent.Children.Add(content);
                                checkContent = appContent;
                            }
                            var check = new CheckBox { Content = checkContent, IsChecked = candidate.Scope != SystemInfoImportScope.Symlinks && candidate.Group != "Ignored" };
                            checks.Add((candidate, check));
                            if (candidate.Value is AppImportCandidate)
                            {
                                var row = new Grid { ColumnSpacing = 8 };
                                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                                row.Children.Add(check);
                                var ignore = ActionButton("Ignore", () =>
                                {
                                    SystemInfoImportService.IgnoreCandidates(_config, [candidate]);
                                    ConfigurationManager.SaveConfiguration(_config);
                                    checks.RemoveAll(entry => entry.Candidate == candidate);
                                    groupPanel.Children.Remove(row);
                                    if (groupPanel.Children.Count == 1)
                                        groupPanel.Visibility = Visibility.Collapsed;
                                });
                                Grid.SetColumn(ignore, 1);
                                row.Children.Add(ignore);
                                groupPanel.Children.Add(row);
                            }
                            else
                                groupPanel.Children.Add(check);
                        }
                        findingCards.Add(Card(groupPanel));
                    }
                    var scannedScopes = new[]
                    {
                        SystemInfoImportScope.Path,
                        SystemInfoImportScope.NetworkDrives,
                        SystemInfoImportScope.ShellFolders,
                        SystemInfoImportScope.AppInstaller,
                        SystemInfoImportScope.Fonts,
                        SystemInfoImportScope.Startup,
                        SystemInfoImportScope.Symlinks
                    };
                    var unchangedScopes = scannedScopes.Where(scope => candidates.All(candidate => candidate.Scope != scope)).ToList();
                    if (unchangedScopes.Count > 0)
                    {
                        var unchanged = new StackPanel { Spacing = 6 };
                        unchanged.Children.Add(new TextBlock { Text = "No changes detected", FontSize = 16, FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 } });
                        foreach (var scope in unchangedScopes)
                            unchanged.Children.Add(new TextBlock { Text = SplitName(scope.ToString()) });
                        findingCards.Add(Card(unchanged));
                    }
                    var resultGrid = new ItemsRepeater
                    {
                        Layout = new UniformGridLayout
                        {
                            MinItemWidth = 360,
                            MinItemHeight = 224,
                            MinRowSpacing = 8,
                            MinColumnSpacing = 8,
                            ItemsStretch = UniformGridLayoutItemsStretch.Fill
                        },
                        ItemsSource = findingCards,
                        ItemTemplate = new CallbackElementFactory(data => (FrameworkElement)data!)
                    };
                    findings.Children.Add(resultGrid);
                    status.Text = candidates.Count == 0 ? "No new or changed system configuration found." : $"Found {candidates.Count} item(s). Review selections, then run checklist.";
                    RunLog.Write("Pre-reinstall Checklist", status.Text);
                    run!.IsEnabled = true;
                });
            }
            catch (Exception ex)
            {
                await RunOnUiThreadAsync(() =>
                {
                    status.Text = $"Scan failed: {ex.Message}";
                    RunLog.WriteException("Pre-reinstall Checklist", "Scan failed", ex);
                });
            }
            finally
            {
                await RunOnUiThreadAsync(() =>
                {
                    spinner.Visibility = Visibility.Collapsed;
                    scan!.IsEnabled = true;
                });
            }
        }

        async Task RunAsync()
        {
            var selected = checks.Where(entry => entry.Check.IsChecked == true).Select(entry => entry.Candidate).ToList();
            if (!await ConfirmAsync("Add selected items?", $"This adds {selected.Count} selected system configuration item(s) to Winstaller.", "Add"))
                return;

            run!.IsEnabled = false;
            scan!.IsEnabled = false;
            BeginLongOperation();
            status.Text = "Adding selected system configuration…";
            try
            {
                var messages = await Task.Run(() =>
                {
                    var output = new List<string>();
                    var applied = SystemInfoImportService.ApplyCandidatesWithResult(_config, selected, SymlinkImportMode.Copy, output.Add);
                    output.Add($"System configuration: {applied.Added} item(s) applied.");
                    output.AddRange(applied.SymlinkFailures.Select(failure => $"{failure.Title}: {failure.Message}"));
                    ConfigurationManager.SaveConfiguration(_config);
                    return output;
                }).ConfigureAwait(false);
                await RunOnUiThreadAsync(() =>
                {
                    foreach (var message in messages)
                        AppendOutput(message);
                    status.Text = "Selected items added. See activity log for details.";
                });
            }
            catch (Exception ex)
            {
                await RunOnUiThreadAsync(() =>
                {
                    status.Text = $"Checklist failed: {ex.Message}";
                    AppendOutput(status.Text);
                });
            }
            finally
            {
                await RunOnUiThreadAsync(() =>
                {
                    EndLongOperation();
                    scan!.IsEnabled = true;
                    run!.IsEnabled = true;
                });
            }
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        scan = ActionButton("Scan Again", async () => await ScanAsync());
        run = ActionButton("Run Selected", async () => await RunAsync(), primary: true);
        run.IsEnabled = false;
        actions.Children.Add(scan);
        actions.Children.Add(run);
        page.Children.Add(actions);
        _ = ScanAsync();
        return page;
    }
}
