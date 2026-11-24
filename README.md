# DeskPane

**A lightweight, non-destructive Windows 10/11 desktop organizer with acrylic & transparent panes.**

English | [简体中文](README.zh-CN.md)

[![Windows 10/11](https://img.shields.io/badge/Windows-10%2F11-0078D4.svg)](#requirements)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](#build-from-source)
[![License: GPL v3](https://img.shields.io/badge/license-GPLv3-blue.svg)](LICENSE)

DeskPane is a streamlined, low-footprint Windows desktop organizer focused strictly on clean file grouping and instant desktop restoration—no bloated extras, no Explorer DLL injection, and near-zero background battery drain on laptops.

---

## Core Design & Features

### Non-Destructive Desktop Grouping (Auto-Restore on Exit)
- **Instant same-volume staging**: Files and shortcuts dragged into a desktop pane are moved via instant 0 ms NTFS metadata moves into a hidden same-volume staging folder (`%LocalAppData%\DeskPane\data\staging\` or `<Drive>:\.DeskPaneStaging\` if Desktop is on another drive). No visible folders are created on your Desktop or Documents.
- **Unorganized items stay untouched**: Desktop icons you do not put into panes remain naturally on the native Windows desktop.
- **Automatic restore on exit & delete**: Exiting DeskPane or deleting a pane automatically moves all staged files straight back to the real Desktop. Relaunching DeskPane restores your organized panes.
- **3-Layer Crash & Emergency Safety**:
  1. **Crash auto-rescue**: Unhandled exceptions trigger an immediate synchronous restore to Desktop before process termination.
  2. **Shutdown / logoff auto-restore**: Process exit restores staged files automatically so your desktop stays intact across reboots.
  3. **One-click & standalone recovery**: Tray menu **Restore All to Desktop**, CLI flag `DeskPane.exe --restore-desktop`, and a standalone zero-dependency `一键恢复桌面文件.bat` script next to the executable backed by `staging-ledger.json`.

### Minimalist, Low-Resource Footprint
- **Stripped to essentials**: Removed non-core modules (Todo, Quick Capture, Search, Weather, Music, Glance), in-app Updater, Onboarding welcome screen, and the separate Settings window / SettingsHost process.
- **Bilingual only**: Ships only **Simplified Chinese (`zh-CN`)** and **English (`en-US`)** localization packs.
- **Battery-friendly**: Event-driven architecture with reduced background timers and compressed icon caches—0% idle CPU and minimal iGPU impact on battery power.

### Clean Visuals & Controls
- **Two background styles**: **Acrylic** (20% / 40% / 60% / 80% / 100% opacity) and **Transparent (No Blur)** with automatic light text (`#F7F7F7`) for crisp legibility over wallpapers.
- **Icon & List views**: Switch any file pane between **Icon View** and **List View** directly from the pane menu, with drag-and-drop, manual/rule sorting, stacking, and compact capsule collapse.
- **Clean hover experience**: Internal file paths are hidden from icon hover tooltips.
- **Double-click blank desktop**: Quickly toggle native Windows desktop icons on/off with negligible overhead.
- **All settings in the tray menu**: Organize Desktop, Restore All to Desktop, Hide/Show Desktop Icons, New Box, Background & Opacity, Language (`简体中文` / `English`), Auto-start, and Exit.

---

## Requirements

- Windows 10 (build 19041+) or Windows 11 (x64 / ARM64)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (for building from source)

## Build from Source

Build a self-contained x64 binary (bundles .NET 10 and Windows App SDK runtime so `DeskPane.exe` can be launched directly):

```powershell
dotnet build .\src\DeskPane\DeskPane.csproj `
  -p:Platform=x64 `
  -p:RuntimeIdentifier=win-x64 `
  -p:SelfContained=true `
  -p:WindowsAppSDKSelfContained=true `
  -p:DeskPaneShellThumbnailProxy=false `
  -p:DeskPaneRustNative=false
```

Output executable:

```text
src\DeskPane\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\DeskPane.exe
```

## License & Attribution

Licensed under **GPL-3.0-only**. See [LICENSE](LICENSE) for details.
Refactored and streamlined from the open-source WinUI 3 desktop organizer project DeskBox.
