// Copyright (c) DeskPane. All rights reserved.

using System.Text;
using System.Text.Json;
using DeskPane.Helpers;
using DeskPane.Models;

namespace DeskPane.Services;

/// <summary>
/// Manages same-volume staging folders for DeskPane virtual desktop groups:
/// - While DeskPane is running, files placed in a virtual desktop box live in a
///   hidden same-volume staging folder (%LocalAppData%\DeskPane\data\staging\&lt;widgetId&gt;\,
///   or &lt;DesktopDrive&gt;\.DeskPaneStaging\&lt;widgetId&gt;\ if Desktop is on another drive).
/// - Same-volume moves are instant NTFS metadata operations (0 ms, zero copy).
/// - On DeskPane exit (or when a box is deleted), all staged items are moved
///   straight back to their original desktop paths and empty staging folders are removed.
/// - A lightweight JSON ledger (staging-ledger.json) ensures crash/power-loss safety.
/// </summary>
public static class DesktopStagingService
{
    private static readonly object s_sync = new();
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private sealed class StagingLedgerEntry
    {
        public string WidgetId { get; set; } = string.Empty;
        public string StagedPath { get; set; } = string.Empty;
        public string OriginalDesktopPath { get; set; } = string.Empty;
    }

    private static string LedgerFilePath =>
        Path.Combine(DeskPaneDataPathService.Current.RootPath, "staging-ledger.json");

    public const string StagingRootFolderName = "DeskPane 桌面格子";

    /// <summary>
    /// Returns the staging root directory guaranteed to be on the same drive
    /// as the user's Desktop for instant zero-copy NTFS moves.
    /// Named "DeskPane 桌面格子" so when pinned to Explorer Quick Access it is
    /// immediately recognizable in any app's file upload dialog.
    /// </summary>
    public static string GetStagingRootPath()
    {
        string userDesktop = FileService.GetDesktopPaths().UserDesktop;
        string appDataRoot = DeskPaneDataPathService.Current.RootPath;

        string? desktopDrive = Path.GetPathRoot(userDesktop);
        string? appDataDrive = Path.GetPathRoot(appDataRoot);

        string root;
        if (!string.IsNullOrWhiteSpace(desktopDrive) &&
            string.Equals(desktopDrive, appDataDrive, StringComparison.OrdinalIgnoreCase))
        {
            root = Path.Combine(appDataRoot, StagingRootFolderName);
        }
        else if (!string.IsNullOrWhiteSpace(desktopDrive))
        {
            root = Path.Combine(desktopDrive, StagingRootFolderName);
        }
        else
        {
            root = Path.Combine(appDataRoot, StagingRootFolderName);
        }

        Directory.CreateDirectory(root);
        return root;
    }

    public static string GetWidgetStagingFolder(string widgetId)
    {
        string safeId = string.IsNullOrWhiteSpace(widgetId)
            ? "default"
            : FileService.SanitizeFileSystemName(widgetId);
        string folder = Path.Combine(GetStagingRootPath(), safeId);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>
    /// Returns a human-readable staging subfolder named after the widget (e.g. "AI", "工作文档")
    /// under "DeskPane 桌面格子", migrating any legacy GUID staging folder automatically.
    /// </summary>
    public static string GetWidgetStagingFolder(WidgetConfig widget)
    {
        string stagingRoot = GetStagingRootPath();
        string folderName = !string.IsNullOrWhiteSpace(widget.ManagedFolderName)
            ? FileService.SanitizeFileSystemName(widget.ManagedFolderName)
            : ResolveReadableFolderName(stagingRoot, widget.Name, widget.Id);

        if (string.IsNullOrWhiteSpace(folderName))
        {
            folderName = FileService.SanitizeFileSystemName(widget.Id);
        }

        string folder = Path.Combine(stagingRoot, folderName);
        Directory.CreateDirectory(folder);
        widget.ManagedFolderName = folderName;

        // Migrate any files from previous legacy GUID folder (%LocalAppData%\DeskPane\staging\<id>\)
        MigrateLegacyGuidStagingFolder(widget.Id, folder);
        return folder;
    }

    /// <summary>
    /// Renames a virtual widget's staging subfolder when the user renames the pane,
    /// keeping the Explorer Quick Access view in sync with the pane title.
    /// </summary>
    public static void RenameWidgetStagingFolder(WidgetConfig widget, string newName)
    {
        if (!widget.IsVirtualDesktopGroup)
        {
            return;
        }

        lock (s_sync)
        {
            string stagingRoot = GetStagingRootPath();
            string oldFolder = !string.IsNullOrWhiteSpace(widget.MappedFolderPath)
                ? widget.MappedFolderPath
                : GetWidgetStagingFolder(widget);

            string newFolderName = ResolveReadableFolderName(stagingRoot, newName, widget.Id, ignorePath: oldFolder);
            string newFolder = Path.Combine(stagingRoot, newFolderName);

            if (string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(oldFolder)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(newFolder)),
                    StringComparison.OrdinalIgnoreCase))
            {
                widget.ManagedFolderName = newFolderName;
                widget.MappedFolderPath = newFolder;
                return;
            }

            try
            {
                if (Directory.Exists(oldFolder))
                {
                    if (Directory.Exists(newFolder))
                    {
                        foreach (string entry in Directory.GetFileSystemEntries(oldFolder))
                        {
                            string dest = EnsureUniqueDestinationPath(Path.Combine(newFolder, Path.GetFileName(entry)));
                            TryMoveFileSystemEntry(entry, dest);
                        }
                        TryRemoveEmptyDirectory(oldFolder);
                    }
                    else
                    {
                        Directory.Move(oldFolder, newFolder);
                    }
                }
                else
                {
                    Directory.CreateDirectory(newFolder);
                }

                widget.ManagedFolderName = newFolderName;
                widget.MappedFolderPath = newFolder;

                // Update ledger paths to reflect the renamed subfolder
                List<StagingLedgerEntry> ledger = LoadLedgerLocked();
                bool ledgerChanged = false;
                string normalizedOld = Path.TrimEndingDirectorySeparator(Path.GetFullPath(oldFolder)) + Path.DirectorySeparatorChar;
                for (int i = 0; i < ledger.Count; i++)
                {
                    if (ledger[i].StagedPath.StartsWith(normalizedOld, StringComparison.OrdinalIgnoreCase))
                    {
                        string relative = ledger[i].StagedPath.Substring(normalizedOld.Length);
                        ledger[i].StagedPath = Path.Combine(newFolder, relative);
                        ledgerChanged = true;
                    }
                }
                if (ledgerChanged)
                {
                    SaveLedgerLocked(ledger);
                }
            }
            catch (Exception ex)
            {
                App.Log($"[DesktopStaging] Rename staging folder failed: {ex.Message}");
            }
        }
    }

    public static async Task PinStagingRootToQuickAccessAsync()
    {
        try
        {
            string root = GetStagingRootPath();
            var result = await ExplorerQuickAccessHelper.TryPinFolderToQuickAccessAsync(root);
            App.LogVerbose($"[DesktopStaging] QuickAccess pin '{root}' succeeded={result.Succeeded} error={result.Error}");
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[DesktopStaging] QuickAccess pin failed: {ex.Message}");
        }
    }

    public static void UnpinStagingRootFromQuickAccess()
    {
        try
        {
            string root = GetStagingRootPath();
            ExplorerQuickAccessHelper.TryUnpinFolderFromQuickAccess(root, out string? error);
            App.LogVerbose($"[DesktopStaging] QuickAccess unpin '{root}' error={error}");
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[DesktopStaging] QuickAccess unpin failed: {ex.Message}");
        }
    }

    private static string ResolveReadableFolderName(
        string stagingRoot,
        string? preferredName,
        string widgetId,
        string? ignorePath = null)
    {
        string baseName = FileService.SanitizeFileSystemName(preferredName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "DeskPane";
        }

        string candidate = Path.Combine(stagingRoot, baseName);
        if (!Directory.Exists(candidate) ||
            (!string.IsNullOrWhiteSpace(ignorePath) &&
             string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(ignorePath), StringComparison.OrdinalIgnoreCase)))
        {
            return baseName;
        }

        for (int suffix = 2; suffix < 100; suffix++)
        {
            string numbered = $"{baseName} ({suffix})";
            string numberedPath = Path.Combine(stagingRoot, numbered);
            if (!Directory.Exists(numberedPath) ||
                (!string.IsNullOrWhiteSpace(ignorePath) &&
                 string.Equals(Path.GetFullPath(numberedPath), Path.GetFullPath(ignorePath), StringComparison.OrdinalIgnoreCase)))
            {
                return numbered;
            }
        }

        return $"{baseName}_{widgetId[..Math.Min(6, widgetId.Length)]}";
    }

    private static void MigrateLegacyGuidStagingFolder(string widgetId, string targetFolder)
    {
        try
        {
            string legacyRoot = Path.Combine(DeskPaneDataPathService.Current.RootPath, "staging");
            string legacyFolder = Path.Combine(legacyRoot, FileService.SanitizeFileSystemName(widgetId));
            if (!Directory.Exists(legacyFolder))
            {
                return;
            }

            foreach (string entry in Directory.GetFileSystemEntries(legacyFolder))
            {
                string dest = EnsureUniqueDestinationPath(Path.Combine(targetFolder, Path.GetFileName(entry)));
                TryMoveFileSystemEntry(entry, dest);
            }

            TryRemoveEmptyDirectory(legacyFolder);
            TryRemoveEmptyDirectory(legacyRoot);
        }
        catch
        {
            // Best-effort migration from legacy GUID staging folder.
        }
    }

    /// <summary>
    /// Called during startup before widgets open: moves remembered desktop items
    /// (recorded in each virtual widget's Config.Items) from the real Desktop into
    /// their widget staging folders so the desktop stays clean while DeskPane runs.
    /// </summary>
    public static void StageVirtualWidgetsOnStartup(IReadOnlyList<WidgetConfig> widgets)
    {
        lock (s_sync)
        {
            List<StagingLedgerEntry> ledger = LoadLedgerLocked();
            bool ledgerChanged = false;

            for (int i = 0; i < widgets.Count; i++)
            {
                WidgetConfig widget = widgets[i];
                if (!widget.IsVirtualDesktopGroup || widget.IsDisabled)
                {
                    continue;
                }

                string stagingFolder = GetWidgetStagingFolder(widget);
                widget.MappedFolderPath = stagingFolder;
                widget.FollowsDefaultStoragePath = false;

                for (int j = 0; j < widget.Items.Count; j++)
                {
                    string originalPath = widget.Items[j].Path;
                    if (string.IsNullOrWhiteSpace(originalPath))
                    {
                        continue;
                    }

                    string fileName = Path.GetFileName(originalPath);
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        continue;
                    }

                    string stagedPath = Path.Combine(stagingFolder, fileName);
                    if (PathExists(stagedPath))
                    {
                        UpsertLedgerEntry(ledger, widget.Id, stagedPath, originalPath);
                        ledgerChanged = true;
                        continue;
                    }

                    if (!PathExists(originalPath))
                    {
                        continue;
                    }

                    if (TryMoveFileSystemEntry(originalPath, stagedPath))
                    {
                        UpsertLedgerEntry(ledger, widget.Id, stagedPath, originalPath);
                        ledgerChanged = true;
                    }
                }
            }

            if (ledgerChanged)
            {
                SaveLedgerLocked(ledger);
            }
        }
    }

    /// <summary>
    /// Moves a single desktop item into a virtual widget's staging folder and records
    /// its original desktop path in the ledger.
    /// </summary>
    public static string? StageItemToWidget(WidgetConfig targetWidget, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !PathExists(sourcePath))
        {
            return null;
        }

        lock (s_sync)
        {
            string stagingFolder = GetWidgetStagingFolder(targetWidget);
            string fileName = Path.GetFileName(sourcePath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            string destinationPath = Path.Combine(stagingFolder, fileName);
            if (string.Equals(
                    Path.GetFullPath(sourcePath),
                    Path.GetFullPath(destinationPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return destinationPath;
            }

            destinationPath = EnsureUniqueDestinationPath(destinationPath);
            if (!TryMoveFileSystemEntry(sourcePath, destinationPath))
            {
                return null;
            }

            List<StagingLedgerEntry> ledger = LoadLedgerLocked();
            string originalDesktopPath = ResolveOriginalDesktopPath(ledger, sourcePath, fileName);
            RemoveLedgerByStagedPath(ledger, sourcePath);
            UpsertLedgerEntry(ledger, targetWidget.Id, destinationPath, originalDesktopPath);
            SaveLedgerLocked(ledger);
            return destinationPath;
        }
    }

    /// <summary>
    /// Restores all staged items belonging to one deleted/removed virtual widget
    /// back to the real Desktop and deletes its empty staging folder.
    /// </summary>
    public static void RestoreWidgetStagedFilesToDesktop(WidgetConfig widget)
    {
        if (!widget.IsVirtualDesktopGroup)
        {
            return;
        }

        lock (s_sync)
        {
            List<StagingLedgerEntry> ledger = LoadLedgerLocked();
            string stagingFolder = GetWidgetStagingFolder(widget);
            RestoreFolderToDesktopLocked(widget.Id, stagingFolder, ledger, recordItemsOnWidget: null);
            SaveLedgerLocked(ledger);
        }
    }

    /// <summary>
    /// Called on DeskPane shutdown: snapshots each virtual widget's current staged
    /// members back into Config.Items (as original Desktop paths) and moves every
    /// staged file/folder back to the real Desktop.
    /// </summary>
    public static int RestoreAllStagedFilesToDesktop(IReadOnlyList<WidgetConfig> widgets)
    {
        lock (s_sync)
        {
            List<StagingLedgerEntry> ledger = LoadLedgerLocked();
            var handledWidgetIds = new HashSet<string>(StringComparer.Ordinal);
            int restoredCount = 0;

            for (int i = 0; i < widgets.Count; i++)
            {
                WidgetConfig widget = widgets[i];
                if (!widget.IsVirtualDesktopGroup)
                {
                    continue;
                }

                handledWidgetIds.Add(widget.Id);
                string stagingFolder = GetWidgetStagingFolder(widget);
                restoredCount += RestoreFolderToDesktopLocked(widget.Id, stagingFolder, ledger, recordItemsOnWidget: widget);
            }

            restoredCount += RestoreOrphanLedgerAndStagingFoldersLocked(ledger, handledWidgetIds);
            SaveLedgerLocked(ledger);
            TryRemoveEmptyDirectory(GetStagingRootPath());
            return restoredCount;
        }
    }

    /// <summary>
    /// User-initiated one-click restore from the tray menu: moves every staged file
    /// back to the real Desktop and clears virtual group item assignments so files
    /// stay on the desktop instead of being re-staged on next launch.
    /// </summary>
    public static int RestoreAllStagedFilesFromTray(IReadOnlyList<WidgetConfig> widgets)
    {
        lock (s_sync)
        {
            List<StagingLedgerEntry> ledger = LoadLedgerLocked();
            var handledWidgetIds = new HashSet<string>(StringComparer.Ordinal);
            int restoredCount = 0;

            for (int i = 0; i < widgets.Count; i++)
            {
                WidgetConfig widget = widgets[i];
                if (!widget.IsVirtualDesktopGroup)
                {
                    continue;
                }

                handledWidgetIds.Add(widget.Id);
                string stagingFolder = GetWidgetStagingFolder(widget);
                restoredCount += RestoreFolderToDesktopLocked(widget.Id, stagingFolder, ledger, recordItemsOnWidget: null);
                widget.Items.Clear();
            }

            restoredCount += RestoreOrphanLedgerAndStagingFoldersLocked(ledger, handledWidgetIds);
            SaveLedgerLocked(ledger);
            TryRemoveEmptyDirectory(GetStagingRootPath());
            return restoredCount;
        }
    }

    /// <summary>
    /// Crash / session-ending / CLI emergency restore: never throws, restores all
    /// staged files back to the real Desktop even if settings/widgets are unavailable.
    /// </summary>
    public static int EmergencyRestoreAllStagedFilesToDesktop(IReadOnlyList<WidgetConfig>? widgets = null)
    {
        try
        {
            if (widgets is not null)
            {
                return RestoreAllStagedFilesToDesktop(widgets);
            }

            lock (s_sync)
            {
                List<StagingLedgerEntry> ledger = LoadLedgerLocked();
                int restoredCount = RestoreOrphanLedgerAndStagingFoldersLocked(ledger, handledWidgetIds: null);
                SaveLedgerLocked(ledger);
                TryRemoveEmptyDirectory(GetStagingRootPath());
                return restoredCount;
            }
        }
        catch
        {
            return 0;
        }
    }

    private static int RestoreOrphanLedgerAndStagingFoldersLocked(
        List<StagingLedgerEntry> ledger,
        HashSet<string>? handledWidgetIds)
    {
        int restoredCount = 0;
        string userDesktop = FileService.GetDesktopPaths().UserDesktop;

        for (int i = ledger.Count - 1; i >= 0; i--)
        {
            StagingLedgerEntry entry = ledger[i];
            if (handledWidgetIds is not null && handledWidgetIds.Contains(entry.WidgetId))
            {
                continue;
            }

            if (!PathExists(entry.StagedPath))
            {
                ledger.RemoveAt(i);
                continue;
            }

            string target = string.IsNullOrWhiteSpace(entry.OriginalDesktopPath)
                ? Path.Combine(userDesktop, Path.GetFileName(entry.StagedPath))
                : entry.OriginalDesktopPath;
            string restoreTarget = PathExists(target) ? EnsureUniqueDestinationPath(target) : target;
            if (TryMoveFileSystemEntry(entry.StagedPath, restoreTarget))
            {
                ledger.RemoveAt(i);
                restoredCount++;
            }
        }

        // Sweep any remaining files in staging subdirectories even if the ledger was missing/corrupt.
        string stagingRoot = GetStagingRootPath();
        if (Directory.Exists(stagingRoot))
        {
            try
            {
                foreach (string subDir in Directory.GetDirectories(stagingRoot))
                {
                    foreach (string entryPath in Directory.GetFileSystemEntries(subDir))
                    {
                        string fileName = Path.GetFileName(entryPath);
                        if (string.IsNullOrWhiteSpace(fileName))
                        {
                            continue;
                        }

                        string target = Path.Combine(userDesktop, fileName);
                        string restoreTarget = PathExists(target) ? EnsureUniqueDestinationPath(target) : target;
                        if (TryMoveFileSystemEntry(entryPath, restoreTarget))
                        {
                            RemoveLedgerByStagedPath(ledger, entryPath);
                            restoredCount++;
                        }
                    }

                    TryRemoveEmptyDirectory(subDir);
                }
            }
            catch
            {
                // Best-effort sweep.
            }
        }

        return restoredCount;
    }

    /// <summary>
    /// Synchronizes ledger entries when files are moved into or out of a virtual
    /// widget's staging folder via normal drag-and-drop.
    /// </summary>
    public static void RecordStagedItems(WidgetConfig widget, IEnumerable<(string SourcePath, string StagedPath)> movedItems)
    {
        if (!widget.IsVirtualDesktopGroup)
        {
            return;
        }

        lock (s_sync)
        {
            List<StagingLedgerEntry> ledger = LoadLedgerLocked();
            bool changed = false;

            foreach (var (sourcePath, stagedPath) in movedItems)
            {
                if (string.IsNullOrWhiteSpace(stagedPath))
                {
                    continue;
                }

                string fileName = Path.GetFileName(stagedPath);
                string originalDesktopPath = ResolveOriginalDesktopPath(ledger, sourcePath, fileName);
                RemoveLedgerByStagedPath(ledger, sourcePath);
                UpsertLedgerEntry(ledger, widget.Id, stagedPath, originalDesktopPath);
                changed = true;
            }

            if (changed)
            {
                SaveLedgerLocked(ledger);
            }
        }
    }

    private static int RestoreFolderToDesktopLocked(
        string widgetId,
        string stagingFolder,
        List<StagingLedgerEntry> ledger,
        WidgetConfig? recordItemsOnWidget)
    {
        string userDesktop = FileService.GetDesktopPaths().UserDesktop;
        var rememberedDesktopPaths = new List<string>();
        int restoredCount = 0;

        if (Directory.Exists(stagingFolder))
        {
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(stagingFolder);
            }
            catch
            {
                entries = [];
            }

            foreach (string stagedPath in entries)
            {
                string fileName = Path.GetFileName(stagedPath);
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    continue;
                }

                string originalDesktopPath = ResolveOriginalDesktopPath(ledger, stagedPath, fileName);
                if (string.IsNullOrWhiteSpace(originalDesktopPath))
                {
                    originalDesktopPath = Path.Combine(userDesktop, fileName);
                }

                string restoreTarget = PathExists(originalDesktopPath)
                    ? EnsureUniqueDestinationPath(originalDesktopPath)
                    : originalDesktopPath;

                if (TryMoveFileSystemEntry(stagedPath, restoreTarget))
                {
                    rememberedDesktopPaths.Add(restoreTarget);
                    RemoveLedgerByStagedPath(ledger, stagedPath);
                    restoredCount++;
                }
                else
                {
                    // File might be locked by another process; keep it in ledger and remember it.
                    rememberedDesktopPaths.Add(originalDesktopPath);
                }
            }

            TryRemoveEmptyDirectory(stagingFolder);
        }

        if (recordItemsOnWidget is not null)
        {
            recordItemsOnWidget.Items.Clear();
            for (int i = 0; i < rememberedDesktopPaths.Count; i++)
            {
                recordItemsOnWidget.Items.Add(new WidgetItemConfig
                {
                    Path = rememberedDesktopPaths[i],
                    SortOrder = i
                });
            }
        }

        return restoredCount;
    }

    private static string ResolveOriginalDesktopPath(
        List<StagingLedgerEntry> ledger,
        string sourceOrStagedPath,
        string fileName)
    {
        for (int i = 0; i < ledger.Count; i++)
        {
            if (string.Equals(ledger[i].StagedPath, sourceOrStagedPath, StringComparison.OrdinalIgnoreCase))
            {
                return ledger[i].OriginalDesktopPath;
            }
        }

        if (!string.IsNullOrWhiteSpace(sourceOrStagedPath) &&
            VirtualDesktopGroupPolicy.IsDesktopItemPath(sourceOrStagedPath))
        {
            return Path.GetFullPath(sourceOrStagedPath);
        }

        return Path.Combine(FileService.GetDesktopPaths().UserDesktop, fileName);
    }

    private static void UpsertLedgerEntry(
        List<StagingLedgerEntry> ledger,
        string widgetId,
        string stagedPath,
        string originalDesktopPath)
    {
        for (int i = 0; i < ledger.Count; i++)
        {
            if (string.Equals(ledger[i].StagedPath, stagedPath, StringComparison.OrdinalIgnoreCase))
            {
                ledger[i].WidgetId = widgetId;
                ledger[i].OriginalDesktopPath = originalDesktopPath;
                return;
            }
        }

        ledger.Add(new StagingLedgerEntry
        {
            WidgetId = widgetId,
            StagedPath = stagedPath,
            OriginalDesktopPath = originalDesktopPath
        });
    }

    private static void RemoveLedgerByStagedPath(List<StagingLedgerEntry> ledger, string stagedPath)
    {
        if (string.IsNullOrWhiteSpace(stagedPath))
        {
            return;
        }

        for (int i = ledger.Count - 1; i >= 0; i--)
        {
            if (string.Equals(ledger[i].StagedPath, stagedPath, StringComparison.OrdinalIgnoreCase))
            {
                ledger.RemoveAt(i);
            }
        }
    }

    private static bool PathExists(string path) =>
        File.Exists(path) || Directory.Exists(path);

    private static string EnsureUniqueDestinationPath(string candidatePath)
    {
        if (!PathExists(candidatePath))
        {
            return candidatePath;
        }

        string dir = Path.GetDirectoryName(candidatePath) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(candidatePath);
        string ext = Path.GetExtension(candidatePath);

        for (int suffix = 2; suffix < 1000; suffix++)
        {
            string next = Path.Combine(dir, $"{name} ({suffix}){ext}");
            if (!PathExists(next))
            {
                return next;
            }
        }

        return Path.Combine(dir, $"{name}_{Guid.NewGuid():N}{ext}");
    }

    private static bool TryMoveFileSystemEntry(string sourcePath, string destinationPath)
    {
        try
        {
            string? destDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            if (File.Exists(sourcePath))
            {
                File.Move(sourcePath, destinationPath);
                return true;
            }

            if (Directory.Exists(sourcePath))
            {
                Directory.Move(sourcePath, destinationPath);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            App.Log($"[DesktopStaging] Failed to move '{sourcePath}' -> '{destinationPath}': {ex.Message}");
            return false;
        }
    }

    private static void TryRemoveEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path, recursive: false);
            }
        }
        catch
        {
            // Ignore cleanup errors on busy directories.
        }
    }

    private static List<StagingLedgerEntry> LoadLedgerLocked()
    {
        try
        {
            if (File.Exists(LedgerFilePath))
            {
                string json = File.ReadAllText(LedgerFilePath, Encoding.UTF8);
                var parsed = JsonSerializer.Deserialize<List<StagingLedgerEntry>>(json);
                if (parsed is not null)
                {
                    return parsed;
                }
            }
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[DesktopStaging] Failed to load ledger: {ex.Message}");
        }

        return [];
    }

    private static void SaveLedgerLocked(List<StagingLedgerEntry> ledger)
    {
        try
        {
            string? dir = Path.GetDirectoryName(LedgerFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (ledger.Count == 0)
            {
                if (File.Exists(LedgerFilePath))
                {
                    File.Delete(LedgerFilePath);
                }

                return;
            }

            string json = JsonSerializer.Serialize(ledger, s_jsonOptions);
            File.WriteAllText(LedgerFilePath, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[DesktopStaging] Failed to save ledger: {ex.Message}");
        }
    }
}
