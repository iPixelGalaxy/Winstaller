using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Winstaller.Configuration;
using Winstaller.Modules;
using Winstaller.Utilities;

namespace Winstaller.Gui;

public sealed partial class MainWindow
{
    private FrameworkElement PreReinstallChecklistCard()
    {
        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new FontIcon { Glyph = "\uE8B7", FontSize = 22, Width = 28, VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = "Pre-reinstall Checklist", FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 } });
        text.Children.Add(new TextBlock { Text = "Refresh backups, review detected changes, and save selected system configuration.", Foreground = ResourceBrush("WinstallerSecondaryTextBrush") });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var button = ActionButton("Open", RenderPreReinstallChecklist, primary: true);
        Grid.SetColumn(button, 2);
        grid.Children.Add(button);
        return Card(grid);
    }

    private FrameworkElement BuildPreReinstallChecklistPage()
    {
        var page = new StackPanel { Spacing = 12 };
        page.Children.Add(PageTitle("Pre-reinstall Checklist", "Refresh managed backups, then review current Windows changes before reinstalling."));
        var scanStatus = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var spinner = new ProgressRing { IsActive = true, Width = 20, Height = 20, Visibility = Visibility.Collapsed };
        scanStatus.Children.Add(spinner);
        var status = new TextBlock { Text = "Scanning current Windows configuration…", Foreground = ResourceBrush("WinstallerSecondaryTextBrush"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        scanStatus.Children.Add(status);
        page.Children.Add(scanStatus);
        page.Children.Add(new TextBlock { Text = $"Scan log: {RunLog.Path}", FontSize = 12, Foreground = ResourceBrush("WinstallerSecondaryTextBrush"), TextWrapping = TextWrapping.Wrap });

        var backupTasks = new StackPanel { Spacing = 6 };
        page.Children.Add(Card(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Managed backups", FontSize = 18, FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 } },
                backupTasks
            }
        }));

        var findings = new StackPanel { Spacing = 8 };
        page.Children.Add(findings);
        var checks = new List<(SystemInfoImportCandidate Candidate, CheckBox Check)>();
        Button? scan = null;
        Button? run = null;

        void ShowBackupTasks()
        {
            backupTasks.Children.Clear();
            AddTask("Files & Shortcuts", _config.FileCopy.Enabled, _config.FileCopy.Operations.Count(operation => !operation.SkipPreReinstallBackup));
            AddTask("Windows Firewall", _config.Firewall.Enabled, null);
            AddTask("VRChat Registry", _config.VRChatRegistry.Enabled, null);
        }

        void AddTask(string name, bool enabled, int? count)
        {
            var detail = enabled
                ? count is null ? "Will refresh managed backup" : $"Will refresh {count} restore backup(s)"
                : "Skipped because module is disabled";
            backupTasks.Children.Add(new TextBlock { Text = $"{name}: {detail}", Foreground = enabled ? null : ResourceBrush("WinstallerSecondaryTextBrush") });
        }

        async Task ScanAsync()
        {
            run!.IsEnabled = false;
            scan!.IsEnabled = false;
            spinner.Visibility = Visibility.Visible;
            status.Text = "Scanning current Windows configuration…";
            RunLog.Write("Pre-reinstall Checklist", "Scan requested.");
            findings.Children.Clear();
            checks.Clear();
            ShowBackupTasks();
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
                    foreach (var group in candidates.GroupBy(candidate => candidate.Group == "Changed" ? "Changed configuration" : $"New {SplitName(candidate.Scope.ToString())}"))
                    {
                        var groupPanel = new StackPanel { Spacing = 6 };
                        groupPanel.Children.Add(new TextBlock { Text = group.Key, FontSize = 16, FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 } });
                        foreach (var candidate in group)
                        {
                            var content = new StackPanel { Spacing = 2 };
                            content.Children.Add(new TextBlock { Text = candidate.Title, TextWrapping = TextWrapping.Wrap });
                            content.Children.Add(new TextBlock { Text = candidate.Detail, FontSize = 12, Foreground = ResourceBrush("WinstallerSecondaryTextBrush"), TextWrapping = TextWrapping.Wrap });
                            var check = new CheckBox { Content = content, IsChecked = candidate.Scope != SystemInfoImportScope.Symlinks && candidate.Group != "Ignored" };
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
                        findings.Children.Add(Card(groupPanel));
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
                        findings.Children.Add(Card(unchanged));
                    }
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
            if (!await ConfirmAsync("Run pre-reinstall checklist?", $"This refreshes enabled backups and applies {selected.Count} selected system configuration item(s).", "Run"))
                return;

            run!.IsEnabled = false;
            scan!.IsEnabled = false;
            BeginLongOperation();
            status.Text = "Refreshing backups and applying selected configuration…";
            try
            {
                var messages = await Task.Run(async () =>
                {
                    var output = new List<string>();
                    if (_config.FileCopy.Enabled)
                    {
                        var result = PreReinstallChecklistService.RefreshFileBackups(_config.FileCopy, output.Add);
                        output.Add($"Files & Shortcuts: {result.Updated} updated, {result.Removed} stale file(s) removed.");
                        output.AddRange(result.Warnings);
                    }
                    if (_config.Firewall.Enabled)
                        output.Add((await new FirewallModule(_config).CaptureAsync()) ? "Firewall backup refreshed." : "Firewall backup failed.");
                    if (_config.VRChatRegistry.Enabled)
                        output.Add((await new VRChatRegistryModule(_config).CaptureAsync()) ? "VRChat registry backup refreshed." : "VRChat registry backup failed.");

                    var applied = SystemInfoImportService.ApplyCandidatesWithResult(_config, selected, SymlinkImportMode.Copy, output.Add);
                    output.Add($"System configuration: {applied.Added} item(s) applied.");
                    output.AddRange(applied.SymlinkFailures.Select(failure => $"{failure.Title}: {failure.Message}"));
                    ConfigurationManager.SaveConfiguration(_config);
                    return output;
                });
                foreach (var message in messages)
                    AppendOutput(message);
                status.Text = "Checklist finished. See activity log for details.";
            }
            catch (Exception ex)
            {
                status.Text = $"Checklist failed: {ex.Message}";
                AppendOutput(status.Text);
            }
            finally
            {
                EndLongOperation();
                scan!.IsEnabled = true;
                run!.IsEnabled = true;
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
