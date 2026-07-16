using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using CommunityToolkit.WinUI.Controls;
using Winstaller.Models;

namespace Winstaller.Gui;

public sealed partial class MainWindow
{
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
