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

        RunLog.Write("Import", $"Review selected {selected.Count} {SplitName(scope.ToString())} item(s).");

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
        if (!DispatcherQueue.HasThreadAccess)
        {
            await RunOnUiThreadAsync(() => ImportSelectedSystemInfoAsync(scope, module, candidates, selected, ignored, symlinkMode));
            return;
        }

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
        void FlushLog()
        {
            string chunk;
            lock (uiLogLock)
            {
                chunk = pendingLogText.ToString();
                pendingLogText.Clear();
                logFlushQueued = false;
            }

            if (!string.IsNullOrEmpty(chunk))
                AppendTextToOutputBox(outputBox, chunk);
        }
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

            if (!DispatcherQueue.TryEnqueue(FlushLog))
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
            if (selected.Count > 0 && result.Added == 0)
                Log("No selected items were added; they are already configured or were skipped.");
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
                FlushLog();
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
                    var iconView = CreateAppIconView(app.PackageId, 32, app.DisplayName);
                    var choiceContent = new Grid { ColumnSpacing = 8 };
                    choiceContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    choiceContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    choiceContent.Children.Add(iconView.Host);
                    var choiceText = new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            new TextBlock { Text = candidate.Title, TextWrapping = TextWrapping.Wrap },
                            new TextBlock { Text = candidate.Detail, FontSize = 12, Foreground = ResourceBrush("WinstallerSecondaryTextBrush"), TextWrapping = TextWrapping.Wrap }
                        }
                    };
                    Grid.SetColumn(choiceText, 1);
                    choiceContent.Children.Add(choiceText);
                    var choice = new CheckBox
                    {
                        IsChecked = recommendedSelected.Contains(app.PackageId),
                        IsEnabled = candidate.Group != "Ignored",
                        Content = choiceContent
                    };
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
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                if (candidate.Value is AppImportCandidate appCandidate)
                    row.Children.Add(CreateAppIconView(appCandidate.PackageId, 32, appCandidate.DisplayName).Host);
                var checkBox = new CheckBox { IsChecked = !isIgnored, Tag = candidate, VerticalAlignment = VerticalAlignment.Center };
                checkBox.Checked += (_, _) => UpdateTitle();
                checkBox.Unchecked += (_, _) => UpdateTitle();
                checkBoxes.Add(checkBox);
                Grid.SetColumn(checkBox, 1);
                row.Children.Add(checkBox);
                var detail = new StackPanel { Spacing = 2 };
                detail.Children.Add(new TextBlock { Text = candidate.Title, TextWrapping = TextWrapping.Wrap });
                detail.Children.Add(new TextBlock { Text = candidate.Detail, Foreground = ResourceBrush("WinstallerSecondaryTextBrush"), FontSize = 12, TextWrapping = TextWrapping.Wrap });
                Grid.SetColumn(detail, 2);
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
                    Grid.SetColumn(ignore, 3);
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
}

