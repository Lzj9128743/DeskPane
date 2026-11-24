namespace DeskPane.Contracts;

using System.Threading;
using DeskPane.Models;
using DeskPane.Services;

/// <summary>
/// Outcome of a desktop-organization undo (or undo-abandon) run, shaped as a
/// record so a remote settings host can carry it across the process
/// boundary — the exception types the in-process coordinator throws do not
/// survive serialization.
/// </summary>
public enum DesktopOrganizationUndoOutcome
{
    Completed,
    Incomplete,
    Failed,
}

public sealed record DesktopOrganizationUndoResult(
    DesktopOrganizationUndoOutcome Outcome,
    int RestoredCount = 0,
    int RemainingCount = 0,
    string? Error = null);

/// <summary>
/// Settings-side surface for widget-host operations, which live in the main
/// process once the settings UI runs in its own process: feature-widget
/// enable state, widget group editing, orphan managed-folder cleanup,
/// managed-storage root migration, glance instance management, display
/// moves, and folder-watcher health. Availability degrades exactly like the
/// previous inline null checks: <see cref="IsHostAvailable"/> is false in
/// headless hosts and value-returning members fall back to settings-only
/// behavior where the old call sites did.
/// </summary>
public interface ISettingsWidgetActions
{
    /// <summary>False when no widget manager exists (headless host).</summary>
    bool IsHostAvailable { get; }

    /// <summary>
    /// Applies a settings-UI group-presentation edit (or reset) to the
    /// authoritative layout: assigns the six presentation fields on the
    /// group, persists, and notifies the host presentation. Null fields are
    /// left untouched, so a reset sends explicit defaults for all six.
    /// </summary>
    Task ApplyGroupPresentation(GroupPresentationChange change);

    /// <summary>Applies the four group-navigation defaults (full snapshot).</summary>
    Task ApplyGroupNavigationDefaults(GroupNavigationDefaultsChange change);

    /// <summary>Applies the new-widget placement preferences (full snapshot).</summary>
    Task ApplyLayoutPreferences(LayoutPreferencesChange change);

    /// <summary>
    /// Runs a capsule-override reset on the authoritative layout (the reset
    /// logic itself lives in CapsuleOverrideResetter so both processes run
    /// identical code).
    /// </summary>
    Task ResetCapsuleOverrides(CapsuleOverrideReset reset);

    /// <summary>
    /// Clears every per-widget background override in the authoritative
    /// layout (metadata + stored asset files). The overrides live in the
    /// layout domain, which snapshots never carry — the appearance page's
    /// batch clear must ride this command (batch R8).
    /// </summary>
    Task ClearPerWidgetBackgrounds();

    void RefreshVisibleWidgetDesktopLayers(string reason);

    void NotifyWidgetGroupPresentationSettingsChanged();

    // ── Widget groups ───────────────────────────────────────────────

    Task<bool> ReorderWidgetGroupMemberAsync(string sourceWidgetId, string targetWidgetId);

    Task<bool> RemoveWidgetFromGroupAsync(string widgetId);

    Task<bool> DissolveWidgetGroupContainingAsync(string widgetId);

    // ── Orphan managed folders ──────────────────────────────────────

    /// <summary>Candidates, or null when no manager exists.</summary>
    IReadOnlyList<ManagedStorageFolderCleanupCandidate>? GetOrphanManagedStorageFolders();

    Task<int> RestoreOrphanManagedStorageFoldersAsync(IReadOnlyList<string> folderPaths);

    Task MoveOrphanManagedStorageFolderContentsToDesktopAsync(string folderPath);

    Task DeleteOrphanManagedStorageFolderAsync(string folderPath, nint ownerWindowHandle);

    // ── Managed-storage root migration ──────────────────────────────

    int GetDefaultManagedStorageWidgetCount();

    Task<ManagedStorageMigrationPreview> PreviewManagedStorageMigrationAsync(
        string newRootPath,
        CancellationToken token);

    Task<ManagedStorageMigrationResult> UpdateDefaultManagedStorageRootAsync(
        string newRootPath,
        ManagedStorageMigrationOptions? options);

    Task RenameWidgetAsync(string widgetId, string newName);

    // ── Displays ────────────────────────────────────────────────────

    Task<(int MovedCount, WidgetMoveAllUndoToken Token)> MoveAllWidgetSurfacesToDisplayAsync(
        string displayStableId);

    Task<bool> UndoMoveAllWidgetSurfacesAsync(WidgetMoveAllUndoToken token);

    Task ApplyScreenBindingAsync(
        string widgetId,
        WidgetScreenBindingMode mode,
        string? boundScreenId,
        bool persistImmediately = true);

    // ── Diagnostics ─────────────────────────────────────────────────

    // GetFolderWatcherHealthSnapshots removed (deep review R18, A-#27): the
    // host never called it — the runtime-health diagnostics path goes through
    // ISettingsAppActions.GetRuntimeHealthSnapshot, which reads the watcher
    // snapshots directly on the main process. The widget-facade wire, router
    // case, and both implementations went with it.

    // ── Desktop organization undo ───────────────────────────────────

    /// <summary>Latest undoable organization history entry, or null.</summary>
    OrganizationHistoryEntry? GetLatestUndoableDesktopOrganizationEntry();

    /// <summary>One organization history entry by id, or null.</summary>
    OrganizationHistoryEntry? GetDesktopOrganizationHistoryEntry(string historyId);

    Task<DesktopOrganizationUndoResult> UndoDesktopOrganizationAsync(
        string historyId,
        nint ownerWindowHandle);

    Task<DesktopOrganizationUndoResult> AbandonDesktopOrganizationUndoAsync(string historyId);
}
