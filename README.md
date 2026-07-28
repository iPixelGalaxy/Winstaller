# Winstaller

> **Alpha software:** Winstaller is under active development. Features can be incomplete or change without notice. Review every configured action before running it on a PC you rely on.
>
> **AI usage:** This project uses AI-assisted development. Maintainers review and test changes before release, but AI-generated code can contain mistakes.

<img width="1600" height="812" alt="image" src="https://github.com/user-attachments/assets/753d42fa-ee27-442a-95f2-2ae0d3924b47" />

Winstaller captures parts of an existing Windows setup, stores a reusable configuration, then applies selected parts of that setup to a new or reinstalled PC. It has a WinUI desktop interface and a console interface.

## Typical workflow

1. Run Winstaller and choose an internal drive for its managed storage.
2. Use the Pre-reinstall Checklist or guided setup to scan current Windows configuration.
3. Review detected items, select what to keep, and configure each module.
4. After reinstalling Windows, run selected modules to restore apps, files, settings, and links.

## Features

| Area | What Winstaller does |
| --- | --- |
| Pre-reinstall scan | Finds installed winget apps, fonts, startup items, `PATH` entries, network drives, shell folders, and symlink candidates. Selected findings become module configuration. |
| App Installer | Installs configured winget and Microsoft Store packages, runs custom installers, and supports app-specific options for Git, Discord, and Spotify. |
| Startup and system settings | Creates startup entries, runs configured processes, sets computer name, transparency, UNC handling, attachment policy, and UAC level. |
| User data layout | Installs fonts; moves Desktop, Downloads, Documents, Pictures, Music, and Videos; maps network drives; adds system `PATH` entries. |
| Files and links | Copies saved files, shortcuts, and private keys. Creates AppData and special symlinks, including Git configuration and SSH keys. |
| Registry and firewall | Imports `.reg` files, applies registry values, exports managed Windows Firewall policy, and restores saved firewall policy. |
| Setup Tasks | Runs ordered workflows with application start, wait, close, kill, restart, and script actions. Completed workflows stay skipped until run again. |
| VRChat | Backs up and restores VRChat registry settings and personal data. |

### Application handling

App Installer uses winget by default. It can repair App Installer and winget sources when needed, install Microsoft Store packages, and run custom download or script installers.

Discord entries can install Equicord and OpenAsar. Spotify entries can install Spicetify and its configured customizations. Git entries expose installer choices such as editor, `PATH`, SSH, HTTPS backend, line endings, terminal, pull behavior, Git LFS, file associations, and context-menu entries.

## Windows installation

Winstaller can install Windows from `install.wim` or `install.esd`. WinPE mode searches attached drives for installation media and `autounattend.xml`, then can apply an image with DISM and create boot files.

**Warning:** Windows installation mode partitions and formats the disk you select. Back up needed data and verify disk model, size, and number before continuing.

## Getting started

Download a release archive from the [Winstaller release directory](https://copyparty.arimodu.dev/winstaller/), extract it, then run `Winstaller.exe` as administrator. The executable requests administrator permission because modules can create symlinks, write registry values, install software, and change system settings.

First launch asks for an internal fixed drive. Winstaller creates hidden storage at `X:\.winstaller`:

| Path | Contents |
| --- | --- |
| `config` | `general.json` and per-module JSON settings in `config\modules`. |
| `data` | Managed files, backups, fonts, and other module data. |
| `logs` | Run and scan logs. |
| `cache` | Cached application metadata and icons. |

`%LocalAppData%\Winstaller\bootstrap.json` records the selected storage drive. Internet access is required for winget, application downloads, and update checks.

## Command line

Run `Winstaller.exe` without arguments for the graphical interface. Console commands use `winstaller` below.

| Command | Result |
| --- | --- |
| `winstaller --console` | Opens interactive console menu. |
| `winstaller --run-all` | Runs every enabled module. |
| `winstaller --run <module-name>` | Runs one module, such as `winstaller --run app-installer`. |
| `winstaller --list` | Lists modules and their status. |
| `winstaller --config <path>` | Loads a monolithic configuration file from a custom path. |
| `winstaller --generate-config` | Writes default configuration. An optional path writes a monolithic file there. |
| `winstaller --install` | Opens interactive WIM or ESD installation utility. |
| `winstaller --winpe` | Runs automated Windows installation from WinPE. |
| `winstaller --winpe --no-unformatted` | Requires manual disk selection in WinPE. |
| `winstaller --update` | Checks for an update and asks before installing it. |
| `winstaller --update --auto` | Checks for and installs an update without prompting. |
| `winstaller --version` | Prints version number. |
| `winstaller --help` | Prints command help. |
| `winstaller --debug <command>` | Adds debug logging to any command. |

## Build from source

Requirements: Windows 10 or 11, .NET 10 SDK, and an x64 machine.

Compile without running the project-specific publish-and-copy target:

```powershell
dotnet build Winstaller.csproj -p:IsPublishSingleExeAfterBuild=true
```

Publish a self-contained, single-file release executable to `publish\Winstaller.exe`:

```powershell
dotnet publish Winstaller.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IsPublishSingleExeAfterBuild=true -o publish
```
