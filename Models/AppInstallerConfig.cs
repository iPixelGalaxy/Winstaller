namespace Winstaller.Models;

/// <summary>
/// Configuration for application installation
/// </summary>
public class AppInstallerConfig
{
    public bool Enabled { get; set; } = true;
    public string SetupInfoDirectory { get; set; } = @"D:\ReinstallFiles\SetupInfo";
    public int DefaultTimeoutSeconds { get; set; } = 300;
    public int InstallerTimeoutSeconds { get; set; } = 1800;

    public List<CustomInstaller> CustomScripts { get; set; } = [];
    public List<string> DefaultInstalls { get; set; } = [];
    public List<string> IgnoredApps { get; set; } = [];
    public Dictionary<string, AppInstallBehavior> Behaviors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class AppInstallBehavior
{
    public string DisplayName { get; set; } = string.Empty;
    public bool LockVersion { get; set; }
    public string Version { get; set; } = string.Empty;
    public DiscordInstallOptions Discord { get; set; } = new();
    public SpotifyInstallOptions Spotify { get; set; } = new();
    public GitInstallOptions Git { get; set; } = new();
}

public enum GitEditor { Vim, Nano, Notepad, VisualStudioCode, Custom }
public enum GitDefaultBranch { InstallerDefault, Master, Main }
public enum GitPath { BashOnly, Cmd, CmdAndTools }
public enum GitSsh { OpenSsh, ExternalPlink }
public enum GitHttps { OpenSsl, WindowsSecureChannel }
public enum GitLineEndings { CRLFAlways, CRLFCommitAsIs, LFAlways }
public enum GitTerminal { MinTTY, WindowsConsole }
public enum GitPullBehavior { Merge, Rebase, FastForwardOnly }
public enum GitTriState { Auto, Enabled, Disabled }

public class GitInstallOptions
{
    public bool DesktopIcon { get; set; }
    public bool GitBashHere { get; set; } = true;
    public bool GitGuiHere { get; set; } = true;
    public bool GitLfs { get; set; } = true;
    public bool AssociateGitFiles { get; set; } = true;
    public bool AssociateShellFiles { get; set; } = true;
    public bool WindowsTerminalProfile { get; set; } = true;
    public bool Scalar { get; set; }
    public bool CheckForUpdates { get; set; } = true;
    public GitEditor Editor { get; set; } = GitEditor.Vim;
    public string CustomEditorPath { get; set; } = string.Empty;
    public GitDefaultBranch DefaultBranch { get; set; } = GitDefaultBranch.InstallerDefault;
    public GitPath Path { get; set; } = GitPath.Cmd;
    public GitSsh Ssh { get; set; } = GitSsh.OpenSsh;
    public string PlinkPath { get; set; } = string.Empty;
    public GitHttps Https { get; set; } = GitHttps.OpenSsl;
    public GitLineEndings LineEndings { get; set; } = GitLineEndings.CRLFAlways;
    public GitTerminal Terminal { get; set; } = GitTerminal.MinTTY;
    public GitPullBehavior PullBehavior { get; set; } = GitPullBehavior.Merge;
    public bool CredentialManager { get; set; } = true;
    public bool FileSystemCache { get; set; } = true;
    public bool Symlinks { get; set; }
    public GitTriState MandatoryAslr { get; set; } = GitTriState.Auto;
    public GitTriState BuiltinDifftool { get; set; } = GitTriState.Auto;
    public GitTriState BuiltinRebase { get; set; } = GitTriState.Auto;
    public GitTriState BuiltinStash { get; set; } = GitTriState.Auto;
    public GitTriState BuiltinInteractiveAdd { get; set; } = GitTriState.Auto;
    public GitTriState PseudoConsole { get; set; } = GitTriState.Auto;
    public GitTriState FileSystemMonitor { get; set; } = GitTriState.Auto;
}

public class DiscordInstallOptions
{
    public bool InstallDiscord { get; set; } = true;
    public bool InstallVencord { get; set; } = true;
    public bool InstallOpenAsar { get; set; } = true;
    public string VencordInstallerUrl { get; set; } = "https://github.com/Vencord/Installer/releases/latest/download/VencordInstallerCli.exe";
    public string DiscordLocation { get; set; } = @"%LOCALAPPDATA%\Discord";
}

public class SpotifyInstallOptions
{
    public bool InstallSpotify { get; set; } = true;
    public bool InstallSpicetify { get; set; } = true;
    public bool BlockUpdates { get; set; } = true;
    public string SidebarConfig { get; set; } = "0";
    public List<string> CustomApps { get; set; } = ["lyrics-plus"];
}
