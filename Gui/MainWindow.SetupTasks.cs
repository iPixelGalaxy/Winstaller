using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Winstaller.Configuration;
using Winstaller.Models;
using Winstaller.Modules;
using Winstaller.Utilities;

namespace Winstaller.Gui;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<string, TextBlock> _setupTaskStatusTexts = new(StringComparer.OrdinalIgnoreCase);

    private FrameworkElement BuildSetupTasksContent(SetupTasksConfig config)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "Build one-time setup automations from ordered app and script blocks. Completed workflows stay skipped until changed or run again.",
            Foreground = ResourceBrush("WinstallerSecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap
        });

        var workflowGrid = new Grid { ColumnSpacing = 10, RowSpacing = 10, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var workflow in config.Workflows)
            workflowGrid.Children.Add(BuildSetupWorkflowCard(config, workflow));
        workflowGrid.SizeChanged += (_, _) => ArrangeSetupWorkflowCards(workflowGrid);
        ArrangeSetupWorkflowCards(workflowGrid);
        panel.Children.Add(workflowGrid);

        panel.Children.Add(ActionButton("+ Add workflow", () =>
        {
            config.Workflows.Add(new SetupWorkflow());
            SaveSetupTasks(config);
        }, primary: true));
        return panel;
    }

    private FrameworkElement BuildSetupWorkflowCard(SetupTasksConfig config, SetupWorkflow workflow)
    {
        var card = new StackPanel { Spacing = 8 };
        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBox { Text = workflow.Name, PlaceholderText = "Workflow name", FontSize = 18 };
        title.LostFocus += (_, _) =>
        {
            workflow.Name = string.IsNullOrWhiteSpace(title.Text) ? "Untitled workflow" : title.Text.Trim();
            SaveSetupTasks(config, workflow, behaviorChanged: false, rerender: false);
        };
        header.Children.Add(title);

        var enabled = new ToggleSwitch { IsOn = workflow.Enabled, OnContent = "Enabled", OffContent = "Disabled", VerticalAlignment = VerticalAlignment.Center };
        enabled.Toggled += (_, _) =>
        {
            if (_isLoadingUi) return;
            workflow.Enabled = enabled.IsOn;
            SaveSetupTasks(config, workflow, behaviorChanged: false, rerender: false);
        };
        Grid.SetColumn(enabled, 1);
        header.Children.Add(enabled);
        card.Children.Add(header);

        var completedAt = new SetupTaskStateStore().GetCompletedAt(workflow.Id);
        var status = new TextBlock
        {
            Text = completedAt is null ? "Not run yet" : $"Completed {completedAt.Value.LocalDateTime:g}",
            Foreground = ResourceBrush("WinstallerSecondaryTextBrush")
        };
        _setupTaskStatusTexts[workflow.Id] = status;
        card.Children.Add(status);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(ActionButton(completedAt is null ? "Run" : "Run Again", async () => await RunSetupWorkflowWithOutputDialogAsync(workflow), primary: true));
        buttons.Children.Add(IconActionButton("\uE76B", "Move up", () => MoveSetupWorkflow(config, workflow, -1)));
        buttons.Children.Add(IconActionButton("\uE76C", "Move down", () => MoveSetupWorkflow(config, workflow, 1)));
        buttons.Children.Add(IconActionButton("\uE74D", "Delete workflow", async () =>
        {
            if (!await ConfirmAsync("Delete workflow?", $"Delete {workflow.Name} and its actions?", "Delete")) return;
            config.Workflows.Remove(workflow);
            new SetupTaskStateStore().Clear(workflow.Id);
            SaveSetupTasks(config);
        }));
        card.Children.Add(buttons);

        var blocks = new StackPanel { Spacing = 8 };
        for (var index = 0; index < workflow.Actions.Count; index++)
            blocks.Children.Add(BuildSetupActionBlock(config, workflow, workflow.Actions[index], index));
        card.Children.Add(blocks);
        card.Children.Add(ActionButton("+ Add action", () =>
        {
            workflow.Actions.Add(new StartApplicationAction());
            SaveSetupTasks(config, workflow);
        }));
        return Card(card);
    }

    private FrameworkElement BuildSetupActionBlock(SetupTasksConfig config, SetupWorkflow workflow, SetupTaskAction action, int index)
    {
        var content = new StackPanel { Spacing = 8 };
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = $"{index + 1}", Width = 22, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.65 });

        var type = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var kind in Enum.GetValues<SetupActionKind>())
            type.Items.Add(new ComboBoxItem { Content = GetSetupActionKindLabel(kind), Tag = kind });
        type.SelectedItem = type.Items.OfType<ComboBoxItem>().First(item => Equals(item.Tag, GetSetupActionKind(action)));
        type.SelectionChanged += (_, _) =>
        {
            if (_isLoadingUi || type.SelectedItem is not ComboBoxItem { Tag: SetupActionKind next } || next == GetSetupActionKind(action)) return;
            var replacement = CreateSetupAction(next);
            replacement.Id = action.Id;
            workflow.Actions[index] = replacement;
            SaveSetupTasks(config, workflow);
        };
        Grid.SetColumn(type, 1);
        header.Children.Add(type);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        controls.Children.Add(IconActionButton("\uE76B", "Move action up", () => MoveSetupAction(config, workflow, index, -1)));
        controls.Children.Add(IconActionButton("\uE76C", "Move action down", () => MoveSetupAction(config, workflow, index, 1)));
        controls.Children.Add(IconActionButton("\uE8C8", "Duplicate action", () =>
        {
            workflow.Actions.Insert(index + 1, CloneSetupAction(action));
            SaveSetupTasks(config, workflow);
        }));
        controls.Children.Add(IconActionButton("\uE74D", "Delete action", () =>
        {
            workflow.Actions.RemoveAt(index);
            SaveSetupTasks(config, workflow);
        }));
        Grid.SetColumn(controls, 2);
        header.Children.Add(controls);
        content.Children.Add(header);

        switch (action)
        {
            case StartApplicationAction start:
                content.Children.Add(SetupTextField("Name", start.Name, value => start.Name = value, config, workflow));
                content.Children.Add(SetupTextField("Application path", start.Path, value => start.Path = value, config, workflow));
                content.Children.Add(SetupTextField("Arguments", start.Arguments, value => start.Arguments = value, config, workflow));
                content.Children.Add(SetupTextField("Working directory", start.WorkingDirectory, value => start.WorkingDirectory = value, config, workflow));
                break;
            case WaitAction wait:
                content.Children.Add(SetupNumberField("Seconds", wait.Seconds, value => wait.Seconds = Math.Max(0, value), config, workflow, minimum: 0));
                break;
            case ProcessTargetAction target:
                BuildProcessTargetFields(content, config, workflow, target);
                if (target is RestartApplicationAction restart && target.TargetKind == SetupTaskTargetKind.ExistingProcess)
                {
                    content.Children.Add(SetupTextField("Relaunch path", restart.Path, value => restart.Path = value, config, workflow));
                    content.Children.Add(SetupTextField("Relaunch arguments", restart.Arguments, value => restart.Arguments = value, config, workflow));
                    content.Children.Add(SetupTextField("Working directory", restart.WorkingDirectory, value => restart.WorkingDirectory = value, config, workflow));
                }
                break;
            case RunScriptAction script:
                content.Children.Add(SetupTextField("Name", script.Name, value => script.Name = value, config, workflow));
                content.Children.Add(SetupEnumField("Runner", script.Runner, value => script.Runner = value, config, workflow));
                content.Children.Add(SetupTextField("Script path", script.Path, value => script.Path = value, config, workflow));
                content.Children.Add(SetupTextField("Arguments", script.Arguments, value => script.Arguments = value, config, workflow));
                content.Children.Add(SetupTextField("Working directory", script.WorkingDirectory, value => script.WorkingDirectory = value, config, workflow));
                content.Children.Add(SetupToggleField("Wait for exit", script.WaitForExit, value => script.WaitForExit = value, config, workflow));
                content.Children.Add(SetupNumberField("Timeout seconds", script.TimeoutSeconds ?? 0, value => script.TimeoutSeconds = value > 0 ? value : null, config, workflow, minimum: 0));
                break;
        }

        return new Border
        {
            Background = ResourceBrush("WinstallerDashboardCardBrush"),
            BorderBrush = ResourceBrush("WinstallerCardStrokeBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Child = content
        };
    }

    private void BuildProcessTargetFields(StackPanel content, SetupTasksConfig config, SetupWorkflow workflow, ProcessTargetAction action)
    {
        content.Children.Add(SetupEnumField("Target", action.TargetKind, value => action.TargetKind = value, config, workflow, rerender: true));
        if (action.TargetKind == SetupTaskTargetKind.ExistingProcess)
        {
            content.Children.Add(SetupTextField("Process name", action.ProcessName, value => action.ProcessName = value, config, workflow));
            return;
        }

        var starts = workflow.Actions.OfType<StartApplicationAction>().Where(start => start.Id != action.Id).ToList();
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Select earlier Start Application block" };
        foreach (var start in starts)
            combo.Items.Add(new ComboBoxItem { Content = start.Name, Tag = start.Id });
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals(item.Tag as string, action.StartedActionId, StringComparison.OrdinalIgnoreCase));
        combo.SelectionChanged += (_, _) =>
        {
            if (_isLoadingUi || combo.SelectedItem is not ComboBoxItem selected) return;
            action.StartedActionId = selected.Tag as string ?? string.Empty;
            SaveSetupTasks(config, workflow, rerender: false);
        };
        content.Children.Add(SetupFieldRow("Started application", combo));
    }

    private FrameworkElement SetupTextField(string label, string value, Action<string> setValue, SetupTasksConfig config, SetupWorkflow workflow)
    {
        var box = new TextBox { Text = value, PlaceholderText = label, HorizontalAlignment = HorizontalAlignment.Stretch };
        box.LostFocus += (_, _) =>
        {
            setValue(box.Text.Trim());
            SaveSetupTasks(config, workflow, rerender: false);
        };
        return SetupFieldRow(label, box);
    }

    private FrameworkElement SetupNumberField(string label, int value, Action<int> setValue, SetupTasksConfig config, SetupWorkflow workflow, int minimum)
    {
        var box = new NumberBox { Value = value, Minimum = minimum, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        box.ValueChanged += (_, args) =>
        {
            if (_isLoadingUi || double.IsNaN(args.NewValue)) return;
            setValue(Math.Max(minimum, Convert.ToInt32(args.NewValue)));
            SaveSetupTasks(config, workflow, rerender: false);
        };
        return SetupFieldRow(label, box);
    }

    private FrameworkElement SetupToggleField(string label, bool value, Action<bool> setValue, SetupTasksConfig config, SetupWorkflow workflow)
    {
        var toggle = new ToggleSwitch { IsOn = value, OnContent = string.Empty, OffContent = string.Empty };
        toggle.Toggled += (_, _) =>
        {
            if (_isLoadingUi) return;
            setValue(toggle.IsOn);
            SaveSetupTasks(config, workflow, rerender: false);
        };
        return SetupFieldRow(label, toggle);
    }

    private FrameworkElement SetupEnumField<T>(string label, T value, Action<T> setValue, SetupTasksConfig config, SetupWorkflow workflow, bool rerender = false) where T : struct, Enum
    {
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var item in Enum.GetValues<T>())
            combo.Items.Add(new ComboBoxItem { Content = item.ToString(), Tag = item });
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().First(item => Equals(item.Tag, value));
        combo.SelectionChanged += (_, _) =>
        {
            if (_isLoadingUi || combo.SelectedItem is not ComboBoxItem { Tag: T selected }) return;
            setValue(selected);
            SaveSetupTasks(config, workflow, rerender: rerender);
        };
        return SetupFieldRow(label, combo);
    }

    private FrameworkElement SetupFieldRow(string label, FrameworkElement editor)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(editor, 1);
        row.Children.Add(editor);
        return row;
    }

    private static void ArrangeSetupWorkflowCards(Grid grid)
    {
        const double minimumCardWidth = 520;
        var columns = Math.Max(1, (int)Math.Floor(grid.ActualWidth / minimumCardWidth));
        if (grid.ColumnDefinitions.Count == columns) return;

        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        for (var index = 0; index < columns; index++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var rows = (int)Math.Ceiling(grid.Children.Count / (double)columns);
        for (var index = 0; index < rows; index++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var index = 0; index < grid.Children.Count; index++)
        {
            var card = (FrameworkElement)grid.Children[index];
            Grid.SetRow(card, index / columns);
            Grid.SetColumn(card, index % columns);
        }
    }

    private void MoveSetupWorkflow(SetupTasksConfig config, SetupWorkflow workflow, int delta)
    {
        var index = config.Workflows.IndexOf(workflow);
        var destination = index + delta;
        if (index < 0 || destination < 0 || destination >= config.Workflows.Count) return;
        config.Workflows.RemoveAt(index);
        config.Workflows.Insert(destination, workflow);
        SaveSetupTasks(config, workflow);
    }

    private void MoveSetupAction(SetupTasksConfig config, SetupWorkflow workflow, int index, int delta)
    {
        var destination = index + delta;
        if (destination < 0 || destination >= workflow.Actions.Count) return;
        var action = workflow.Actions[index];
        workflow.Actions.RemoveAt(index);
        workflow.Actions.Insert(destination, action);
        SaveSetupTasks(config, workflow);
    }

    private void SaveSetupTasks(SetupTasksConfig config, SetupWorkflow? workflow = null, bool behaviorChanged = true, bool rerender = true)
    {
        if (behaviorChanged && workflow is not null)
        {
            new SetupTaskStateStore().Clear(workflow.Id);
            if (_setupTaskStatusTexts.TryGetValue(workflow.Id, out var status))
                status.Text = "Not run yet";
        }
        SaveConfiguration();
        var module = _modules.First(descriptor => ReferenceEquals(descriptor.Config, config));
        if (rerender)
            RenderModule(module);
    }

    private static SetupActionKind GetSetupActionKind(SetupTaskAction action) => action switch
    {
        StartApplicationAction => SetupActionKind.StartApplication,
        WaitAction => SetupActionKind.Wait,
        CloseApplicationAction => SetupActionKind.CloseApplication,
        KillApplicationAction => SetupActionKind.KillApplication,
        RestartApplicationAction => SetupActionKind.RestartApplication,
        RunScriptAction => SetupActionKind.RunScript,
        _ => throw new InvalidOperationException($"Unknown setup action: {action.GetType().Name}")
    };

    private static string GetSetupActionKindLabel(SetupActionKind kind) => kind switch
    {
        SetupActionKind.StartApplication => "Start Application",
        SetupActionKind.Wait => "Wait",
        SetupActionKind.CloseApplication => "Close Application",
        SetupActionKind.KillApplication => "Kill Application",
        SetupActionKind.RestartApplication => "Restart Application",
        SetupActionKind.RunScript => "Run Script",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static SetupTaskAction CreateSetupAction(SetupActionKind kind) => kind switch
    {
        SetupActionKind.StartApplication => new StartApplicationAction(),
        SetupActionKind.Wait => new WaitAction(),
        SetupActionKind.CloseApplication => new CloseApplicationAction(),
        SetupActionKind.KillApplication => new KillApplicationAction(),
        SetupActionKind.RestartApplication => new RestartApplicationAction(),
        SetupActionKind.RunScript => new RunScriptAction(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static SetupTaskAction CloneSetupAction(SetupTaskAction action)
    {
        SetupTaskAction copy = action switch
        {
            StartApplicationAction start => new StartApplicationAction { Name = start.Name, Path = start.Path, Arguments = start.Arguments, WorkingDirectory = start.WorkingDirectory },
            WaitAction wait => new WaitAction { Seconds = wait.Seconds },
            CloseApplicationAction close => new CloseApplicationAction { TargetKind = close.TargetKind, StartedActionId = close.StartedActionId, ProcessName = close.ProcessName },
            KillApplicationAction kill => new KillApplicationAction { TargetKind = kill.TargetKind, StartedActionId = kill.StartedActionId, ProcessName = kill.ProcessName },
            RestartApplicationAction restart => new RestartApplicationAction { TargetKind = restart.TargetKind, StartedActionId = restart.StartedActionId, ProcessName = restart.ProcessName, Path = restart.Path, Arguments = restart.Arguments, WorkingDirectory = restart.WorkingDirectory },
            RunScriptAction script => new RunScriptAction { Name = script.Name, Runner = script.Runner, Path = script.Path, Arguments = script.Arguments, WorkingDirectory = script.WorkingDirectory, WaitForExit = script.WaitForExit, TimeoutSeconds = script.TimeoutSeconds },
            _ => throw new InvalidOperationException($"Unknown setup action: {action.GetType().Name}")
        };
        return copy;
    }

    private async Task RunSetupWorkflowWithOutputDialogAsync(SetupWorkflow workflow)
    {
        var width = GetLogDialogWidth();
        var outputView = CreateLogOutputView(width, out var output);
        var copy = ActionButton("Copy Full Log", () => CopyTextFromFile(RunLog.Path));
        copy.IsEnabled = false;
        var folder = ActionButton("Open Log Folder", () => OpenFolder(Path.GetDirectoryName(RunLog.Path) ?? BootstrapManager.LogsDirectory));
        ContentDialog dialog = null!;
        var done = ActionButton("Done", () =>
        {
            dialog.Hide();
            return Task.CompletedTask;
        }, primary: true);
        done.MinWidth = 96;
        done.Visibility = Visibility.Collapsed;
        var footerButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { folder, copy, done } };
        var footer = new Grid { Width = width, Children = { footerButtons } };
        dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = $"Running {workflow.Name}",
            Content = new StackPanel { Spacing = 12, Width = width, Children = { outputView, footer } },
            DefaultButton = ContentDialogButton.None
        };
        dialog.Resources["ContentDialogMinWidth"] = width;
        dialog.Resources["ContentDialogMaxWidth"] = width + 80;
        dialog.Resources["ContentDialogSeparatorThickness"] = new Thickness(0);

        _activeOutputBox = output;
        var dialogTask = dialog.ShowAsync().AsTask();
        try
        {
            await RunSetupWorkflowAsync(workflow);
        }
        finally
        {
            await RunOnUiThreadAsync(() =>
            {
                FlushOutputText(output);
                _activeOutputBox = null;
                copy.IsEnabled = true;
                done.Visibility = Visibility.Visible;
            });
        }
        await dialogTask;
        InvalidateCachedPage("Setup Tasks");
        RenderModule(_modules.First(module => module.Config is SetupTasksConfig));
    }

    private async Task RunSetupWorkflowAsync(SetupWorkflow workflow)
    {
        if (_isRunning)
        {
            AppendOutput("Operation already running.");
            return;
        }

        BeginLongOperation();
        AppendOutput($"Starting {workflow.Name}. Log: {RunLog.Path}");
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
                    await using var session = new RunSessionCoordinator();
                    session.Activate();
                    var succeeded = await new SetupTasksModule(_config).RunAgainAsync(workflow.Id).ConfigureAwait(false);
                    var result = succeeded ? "Completed successfully." : "Completed with errors.";
                    RunLog.Write("Run", $"{workflow.Name}: {result}");
                    AppendOutput(result);
                    await session.FlushAsync().ConfigureAwait(false);
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

    private enum SetupActionKind
    {
        StartApplication,
        Wait,
        CloseApplication,
        KillApplication,
        RestartApplication,
        RunScript
    }
}
