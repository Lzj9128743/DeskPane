# DeskPane（桌片）

**轻量、无感、不绑架文件的 Windows 10/11 桌面整理工具，支持亚克力与全透明格子。**

[English](README.md) | 简体中文

[![Windows 10/11](https://img.shields.io/badge/Windows-10%2F11-0078D4.svg)](#运行环境)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](#本地编译运行)
[![License: GPL v3](https://img.shields.io/badge/license-GPLv3-blue.svg)](LICENSE)

DeskPane 专注于纯粹的桌面文件归类与极低系统占用：砍掉一切非核心插件与臃肿后台，不注入 `explorer.exe`，笔记本离电后台几乎零耗电，退出软件后桌面文件自动完整归位。

---

## 核心特性

### 无感归类，退出自动归位
- **同盘隐藏暂存（0 毫秒瞬移）**：拖入格子的桌面文件/快捷方式通过 NTFS 同盘元数据瞬移至隐藏暂存目录（默认 `%LocalAppData%\DeskPane\data\staging\`；若桌面在其他盘，则自动建在同盘隐藏目录 `<Drive>:\.DeskPaneStaging\`），**不在你的桌面或文档目录新建任何可见文件夹**。
- **未入格图标原地保留**：只有拖进格子的图标会从原桌面隐身，其余桌面图标照常保留在原生 Windows 桌面上，不改变原有使用习惯。
- **退出 / 删格自动全部移回桌面**：退出 DeskPane 或删除格子时，所有暂存文件自动原样移回真实桌面；下次启动自动恢复整理好的格子格局。
- **3 道防丢安全防线**：
  1. **崩溃瞬间自动抢救**：未捕获异常退出前同步将暂存文件瞬移回桌面。
  2. **关机 / 注销自动归位**：系统会话结束或进程退出时自动还原。
  3. **随时手动与脱离软件恢复**：支持托盘右键 **恢复全部文件到桌面**、命令行 `DeskPane.exe --restore-desktop`，以及程序同目录下零依赖的 `一键恢复桌面文件.bat` 急救脚本（配合 `staging-ledger.json` 账本兜底）。

### 极致瘦身与低功耗
- **砍掉所有非核心模块**：移除待办、速记、搜索、天气、音乐、Glance、应用内更新器、启动欢迎页以及独立设置窗口进程。
- **语言包仅保留中英文**：仅内置 **简体中文 (`zh-CN`)** 与 **English (`en-US`)**，大幅缩减资源体积。
- **笔记本离电友好**：纯事件驱动、无多余轮询定时器、压缩图标缓存预算，空闲 CPU 占用 `0%`。

### 简洁质感与高效交互
- **两种背景质感**：砍掉冗余 Mica 材质，仅保留 **亚克力（20% / 40% / 60% / 80% / 100% 透明度）** 与 **全透明（无模糊）**；切换至全透明时，文字自动变为浅色（`#F7F7F7`）保证在壁纸上清晰可读。
- **图标 / 列表双视图**：文件格子支持在 **图标视图** 与 **列表视图** 之间一键切换，同时支持文件夹映射、分组标签、自动叠放与紧凑胶囊模式。
- **干净的悬停体验**：取消鼠标悬停图标时显示内部路径提示。
- **双击桌面空白处**：快捷切换隐藏/显示 Windows 原生桌面图标。
- **全托盘右键控制**：整理桌面、恢复全部文件到桌面、隐藏/显示桌面图标、新建格子、新建文件夹映射、浅/深色主题、背景质感与透明度、中/英语言切换、开机自启与退出，全部在系统托盘右键菜单一键完成。

---

## 运行环境

- Windows 10（19041 及以上）或 Windows 11（x64 / ARM64）
- 本地源码编译需安装 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## 本地编译运行

编译自包含 x64 版本（内含 .NET 10 与 Windows App SDK 运行时，可直接双击运行）：

```powershell
dotnet build .\src\DeskPane\DeskPane.csproj `
  -p:Platform=x64 `
  -p:RuntimeIdentifier=win-x64 `
  -p:SelfContained=true `
  -p:WindowsAppSDKSelfContained=true `
  -p:DeskPaneShellThumbnailProxy=false `
  -p:DeskPaneRustNative=false
```

编译产物位于：

```text
src\DeskPane\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\DeskPane.exe
```

## 开源协议与致谢

本项目采用 **GPL-3.0-only** 协议开源，详见 [LICENSE](LICENSE)。
基于开源 WinUI 3 桌面整理项目 DeskBox 精简重构。
