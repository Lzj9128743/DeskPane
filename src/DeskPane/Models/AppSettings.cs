using System.Text.Json.Serialization;

namespace DeskPane.Models;

/// <summary>
/// Root settings object that is serialized to/from the JSON config file.
/// State lives in per-domain <c>*SettingsSlice</c> objects; the flat
/// properties below are the serialization facade and the legacy access
/// surface. Declaration order here defines the on-disk member order.
/// </summary>
/// <remarks>
/// Batch 51 adjudication (schema-equivalent narrowing): the 220 passthrough
/// properties below are the frozen settings.json wire contract, not removable
/// facade weight. Serializing slice members directly was evaluated and
/// rejected 鈥?the flat member order interleaves all 13 slices, and the one
/// legacy wire-attribute pair (<see cref="LegacyWidgetCapsuleModeEnabled"/>
/// JsonPropertyName + WhenWritingNull) lives here 鈥?so no byte-equivalent
/// path removes these properties. Adding or removing a passthrough is a
/// disk-schema change and requires a schema-versioned migration; runtime code
/// reads the slices (<c>Settings.&lt;Slice&gt;.&lt;Prop&gt;</c>) and the
/// SettingsSliceOwnershipContractTests access ratchet keeps facade references
/// shrinking toward that end state.
/// </remarks>
public class AppSettings
{
    /// <summary>
    /// Settings schema version for migration purposes.
    /// New or legacy settings without this field begin at version 1 and are
    /// advanced by <see cref="DeskPane.Services.SettingsMigrationPipeline"/>.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    // 鈹€鈹€鈹€ Ownership slices (never serialized directly) 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€

    [JsonIgnore]
    public CoreSettingsSlice Core { get; } = new();

    [JsonIgnore]
    public PerformanceSettingsSlice Performance { get; } = new();

    [JsonIgnore]
    public WidgetShellSettingsSlice WidgetShell { get; } = new();

    [JsonIgnore]
    public FileWidgetSettingsSlice FileWidget { get; } = new();

    [JsonIgnore]
    public BackupSettingsSlice Backup { get; } = new();

    [JsonIgnore]
    public CloudBackupSettingsSlice CloudBackup { get; } = new();

    [JsonIgnore]
    public DesktopOrganizationSettingsSlice DesktopOrganization { get; } = new();

    [JsonIgnore]
    public WidgetLayoutSettingsSlice WidgetLayout { get; } = new();

    // 鈹€鈹€鈹€ Serialization facade (wire contract, order-significant) 鈹€鈹€

    /// <inheritdoc cref="CoreSettingsSlice.Theme"/>
    public string Theme { get => Core.Theme; set => Core.Theme = value; }

    /// <inheritdoc cref="CoreSettingsSlice.TrayIconStyle"/>
    public string TrayIconStyle { get => Core.TrayIconStyle; set => Core.TrayIconStyle = value; }

    /// <inheritdoc cref="CoreSettingsSlice.Language"/>
    public string Language { get => Core.Language; set => Core.Language = value; }

    /// <inheritdoc cref="CoreSettingsSlice.AccentColorMode"/>
    public string AccentColorMode { get => Core.AccentColorMode; set => Core.AccentColorMode = value; }

    /// <inheritdoc cref="CoreSettingsSlice.CustomAccentColor"/>
    public string CustomAccentColor { get => Core.CustomAccentColor; set => Core.CustomAccentColor = value; }

    /// <inheritdoc cref="CoreSettingsSlice.AutoStart"/>
    public bool AutoStart { get => Core.AutoStart; set => Core.AutoStart = value; }

    /// <inheritdoc cref="CoreSettingsSlice.AutoStartDefaultApplied"/>
    public bool AutoStartDefaultApplied { get => Core.AutoStartDefaultApplied; set => Core.AutoStartDefaultApplied = value; }

    /// <inheritdoc cref="CoreSettingsSlice.AutoStartMode"/>
    public StartupMode? AutoStartMode { get => Core.AutoStartMode; set => Core.AutoStartMode = value; }

    /// <inheritdoc cref="CoreSettingsSlice.SilentStartup"/>
    public bool SilentStartup { get => Core.SilentStartup; set => Core.SilentStartup = value; }

    /// <inheritdoc cref="PerformanceSettingsSlice.PerformanceMode"/>
    public string PerformanceMode { get => Performance.PerformanceMode; set => Performance.PerformanceMode = value; }

    /// <inheritdoc cref="PerformanceSettingsSlice.HiddenCacheCleanupDelaySeconds"/>
    public int HiddenCacheCleanupDelaySeconds { get => Performance.HiddenCacheCleanupDelaySeconds; set => Performance.HiddenCacheCleanupDelaySeconds = value; }

    /// <inheritdoc cref="PerformanceSettingsSlice.HiddenCacheCleanupScope"/>
    public string HiddenCacheCleanupScope { get => Performance.HiddenCacheCleanupScope; set => Performance.HiddenCacheCleanupScope = value; }

    /// <inheritdoc cref="PerformanceSettingsSlice.VisibleIdleCacheCleanupDelaySeconds"/>
    public int VisibleIdleCacheCleanupDelaySeconds { get => Performance.VisibleIdleCacheCleanupDelaySeconds; set => Performance.VisibleIdleCacheCleanupDelaySeconds = value; }

    /// <inheritdoc cref="PerformanceSettingsSlice.IdleWorkingSetTrimEnabled"/>
    public bool IdleWorkingSetTrimEnabled { get => Performance.IdleWorkingSetTrimEnabled; set => Performance.IdleWorkingSetTrimEnabled = value; }

    /// <inheritdoc cref="PerformanceSettingsSlice.ImmediateHiddenWorkingSetTrimEnabled"/>
    public bool ImmediateHiddenWorkingSetTrimEnabled { get => Performance.ImmediateHiddenWorkingSetTrimEnabled; set => Performance.ImmediateHiddenWorkingSetTrimEnabled = value; }

    /// <inheritdoc cref="PerformanceSettingsSlice.QuiescenceWorkingSetTrimEnabled"/>
    public bool QuiescenceWorkingSetTrimEnabled { get => Performance.QuiescenceWorkingSetTrimEnabled; set => Performance.QuiescenceWorkingSetTrimEnabled = value; }

    /// <inheritdoc cref="PerformanceSettingsSlice.TransientWindowReleaseDelaySeconds"/>
    public int TransientWindowReleaseDelaySeconds { get => Performance.TransientWindowReleaseDelaySeconds; set => Performance.TransientWindowReleaseDelaySeconds = value; }

    /// <inheritdoc cref="PerformanceSettingsSlice.PerformanceCacheBudget"/>
    public string PerformanceCacheBudget { get => Performance.PerformanceCacheBudget; set => Performance.PerformanceCacheBudget = value; }

    /// <inheritdoc cref="PerformanceSettingsSlice.EnableContinuousDecorativeAnimations"/>
    public bool EnableContinuousDecorativeAnimations { get => Performance.EnableContinuousDecorativeAnimations; set => Performance.EnableContinuousDecorativeAnimations = value; }

    /// <inheritdoc cref="PerformanceSettingsSlice.EnableTextMarqueeAnimations"/>
    public bool EnableTextMarqueeAnimations { get => Performance.EnableTextMarqueeAnimations; set => Performance.EnableTextMarqueeAnimations = value; }

    /// <inheritdoc cref="PerformanceSettingsSlice.EnableCompactAmbientAnimations"/>
    public bool EnableCompactAmbientAnimations { get => Performance.EnableCompactAmbientAnimations; set => Performance.EnableCompactAmbientAnimations = value; }







    /// <inheritdoc cref="CoreSettingsSlice.GlobalHotkeyEnabled"/>
    public bool GlobalHotkeyEnabled { get => Core.GlobalHotkeyEnabled; set => Core.GlobalHotkeyEnabled = value; }

    /// <inheritdoc cref="CoreSettingsSlice.GlobalHotkeyActivationKind"/>
    public HotkeyActivationKind GlobalHotkeyActivationKind { get => Core.GlobalHotkeyActivationKind; set => Core.GlobalHotkeyActivationKind = value; }

    /// <inheritdoc cref="CoreSettingsSlice.GlobalHotkeyModifiers"/>
    public int GlobalHotkeyModifiers { get => Core.GlobalHotkeyModifiers; set => Core.GlobalHotkeyModifiers = value; }

    /// <inheritdoc cref="CoreSettingsSlice.GlobalHotkeyKey"/>
    public int GlobalHotkeyKey { get => Core.GlobalHotkeyKey; set => Core.GlobalHotkeyKey = value; }

    /// <inheritdoc cref="CoreSettingsSlice.DesktopDoubleClickEnabled"/>
    public bool DesktopDoubleClickEnabled { get => Core.DesktopDoubleClickEnabled; set => Core.DesktopDoubleClickEnabled = value; }

    /// <inheritdoc cref="CoreSettingsSlice.HasCompletedOnboarding"/>
    public bool HasCompletedOnboarding { get => Core.HasCompletedOnboarding; set => Core.HasCompletedOnboarding = value; }

    /// <inheritdoc cref="CoreSettingsSlice.OnboardingStepIndex"/>
    public int OnboardingStepIndex { get => Core.OnboardingStepIndex; set => Core.OnboardingStepIndex = value; }

    /// <inheritdoc cref="CoreSettingsSlice.CompletedOnboardingVersion"/>
    public int CompletedOnboardingVersion { get => Core.CompletedOnboardingVersion; set => Core.CompletedOnboardingVersion = value; }

    /// <inheritdoc cref="CoreSettingsSlice.HasResolvedInitialFileWidgetSetup"/>
    public bool HasResolvedInitialFileWidgetSetup { get => Core.HasResolvedInitialFileWidgetSetup; set => Core.HasResolvedInitialFileWidgetSetup = value; }

    /// <inheritdoc cref="CoreSettingsSlice.FullyElevatedNoticeAcknowledgedUtcTicks"/>
    public long? FullyElevatedNoticeAcknowledgedUtcTicks { get => Core.FullyElevatedNoticeAcknowledgedUtcTicks; set => Core.FullyElevatedNoticeAcknowledgedUtcTicks = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.DefaultWidgetWidth"/>
    public double DefaultWidgetWidth { get => WidgetShell.DefaultWidgetWidth; set => WidgetShell.DefaultWidgetWidth = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.DefaultWidgetHeight"/>
    public double DefaultWidgetHeight { get => WidgetShell.DefaultWidgetHeight; set => WidgetShell.DefaultWidgetHeight = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetOpacity"/>
    public double WidgetOpacity { get => WidgetShell.WidgetOpacity; set => WidgetShell.WidgetOpacity = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetMaterialType"/>
    public string WidgetMaterialType { get => WidgetShell.WidgetMaterialType; set => WidgetShell.WidgetMaterialType = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetMaterialIntensity"/>
    public double WidgetMaterialIntensity { get => WidgetShell.WidgetMaterialIntensity; set => WidgetShell.WidgetMaterialIntensity = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetForegroundMode"/>
    public string WidgetForegroundMode { get => WidgetShell.WidgetForegroundMode; set => WidgetShell.WidgetForegroundMode = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetForegroundColor"/>
    public string WidgetForegroundColor { get => WidgetShell.WidgetForegroundColor; set => WidgetShell.WidgetForegroundColor = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetBorderColorMode"/>
    public string WidgetBorderColorMode { get => WidgetShell.WidgetBorderColorMode; set => WidgetShell.WidgetBorderColorMode = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetBorderStyle"/>
    public string WidgetBorderStyle { get => WidgetShell.WidgetBorderStyle; set => WidgetShell.WidgetBorderStyle = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetBackgroundMode"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WidgetBackgroundMode { get => WidgetShell.WidgetBackgroundMode; set => WidgetShell.WidgetBackgroundMode = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetBackgroundUnifiedImage"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WidgetBackgroundUnifiedImage { get => WidgetShell.WidgetBackgroundUnifiedImage; set => WidgetShell.WidgetBackgroundUnifiedImage = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetBackgroundPanoramaImage"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WidgetBackgroundPanoramaImage { get => WidgetShell.WidgetBackgroundPanoramaImage; set => WidgetShell.WidgetBackgroundPanoramaImage = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetBackgroundDim"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? WidgetBackgroundDim { get => WidgetShell.WidgetBackgroundDim; set => WidgetShell.WidgetBackgroundDim = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetBackgroundUnifiedFit"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WidgetBackgroundUnifiedFit { get => WidgetShell.WidgetBackgroundUnifiedFit; set => WidgetShell.WidgetBackgroundUnifiedFit = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetTextShadowEnabled"/>
    public bool WidgetTextShadowEnabled { get => WidgetShell.WidgetTextShadowEnabled; set => WidgetShell.WidgetTextShadowEnabled = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCornerPreference"/>
    public string WidgetCornerPreference { get => WidgetShell.WidgetCornerPreference; set => WidgetShell.WidgetCornerPreference = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetAnimationEffect"/>
    public string WidgetAnimationEffect { get => WidgetShell.WidgetAnimationEffect; set => WidgetShell.WidgetAnimationEffect = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetAnimationSpeed"/>
    public string WidgetAnimationSpeed { get => WidgetShell.WidgetAnimationSpeed; set => WidgetShell.WidgetAnimationSpeed = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetAnimationSlideDirection"/>
    public string WidgetAnimationSlideDirection { get => WidgetShell.WidgetAnimationSlideDirection; set => WidgetShell.WidgetAnimationSlideDirection = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetAnimationEasingIntensity"/>
    public string WidgetAnimationEasingIntensity { get => WidgetShell.WidgetAnimationEasingIntensity; set => WidgetShell.WidgetAnimationEasingIntensity = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetAnimationStaggerEnabled"/>
    public bool WidgetAnimationStaggerEnabled { get => WidgetShell.WidgetAnimationStaggerEnabled; set => WidgetShell.WidgetAnimationStaggerEnabled = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetLayerMode"/>
    public string WidgetLayerMode { get => WidgetShell.WidgetLayerMode; set => WidgetShell.WidgetLayerMode = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.KeepWidgetsVisibleOnShowDesktop"/>
    public bool KeepWidgetsVisibleOnShowDesktop { get => WidgetShell.KeepWidgetsVisibleOnShowDesktop; set => WidgetShell.KeepWidgetsVisibleOnShowDesktop = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.DisplayWidgetChromeMode"/>
    public string DisplayWidgetChromeMode { get => WidgetShell.DisplayWidgetChromeMode; set => WidgetShell.DisplayWidgetChromeMode = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.InteractiveWidgetChromeMode"/>
    public string InteractiveWidgetChromeMode { get => WidgetShell.InteractiveWidgetChromeMode; set => WidgetShell.InteractiveWidgetChromeMode = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCollapseBehavior"/>
    public string WidgetCollapseBehavior { get => WidgetShell.WidgetCollapseBehavior; set => WidgetShell.WidgetCollapseBehavior = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.LegacyWidgetCapsuleModeEnabled"/>
    [JsonPropertyName("widgetCapsuleModeEnabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyWidgetCapsuleModeEnabled { get => WidgetShell.LegacyWidgetCapsuleModeEnabled; set => WidgetShell.LegacyWidgetCapsuleModeEnabled = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCompactWidthMode"/>
    public string WidgetCompactWidthMode { get => WidgetShell.WidgetCompactWidthMode; set => WidgetShell.WidgetCompactWidthMode = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCompactExpansionDirection"/>
    public string WidgetCompactExpansionDirection { get => WidgetShell.WidgetCompactExpansionDirection; set => WidgetShell.WidgetCompactExpansionDirection = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCapsuleArrangementMode"/>
    public string WidgetCapsuleArrangementMode { get => WidgetShell.WidgetCapsuleArrangementMode; set => WidgetShell.WidgetCapsuleArrangementMode = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCapsuleBarSpacing"/>
    public double WidgetCapsuleBarSpacing { get => WidgetShell.WidgetCapsuleBarSpacing; set => WidgetShell.WidgetCapsuleBarSpacing = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCapsuleBarPlacement"/>
    public string WidgetCapsuleBarPlacement { get => WidgetShell.WidgetCapsuleBarPlacement; set => WidgetShell.WidgetCapsuleBarPlacement = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCapsuleBarDirection"/>
    public string WidgetCapsuleBarDirection { get => WidgetShell.WidgetCapsuleBarDirection; set => WidgetShell.WidgetCapsuleBarDirection = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCapsuleBarOrder"/>
    public List<string> WidgetCapsuleBarOrder { get => WidgetShell.WidgetCapsuleBarOrder; set => WidgetShell.WidgetCapsuleBarOrder = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCapsuleFreePlacements"/>
    public Dictionary<string, WidgetCompactPlacement> WidgetCapsuleFreePlacements { get => WidgetShell.WidgetCapsuleFreePlacements; set => WidgetShell.WidgetCapsuleFreePlacements = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCollapsedStyle"/>
    public string WidgetCollapsedStyle { get => WidgetShell.WidgetCollapsedStyle; set => WidgetShell.WidgetCollapsedStyle = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCompactContentMode"/>
    public string WidgetCompactContentMode { get => WidgetShell.WidgetCompactContentMode; set => WidgetShell.WidgetCompactContentMode = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCompactHideSensitiveContent"/>
    public bool WidgetCompactHideSensitiveContent { get => WidgetShell.WidgetCompactHideSensitiveContent; set => WidgetShell.WidgetCompactHideSensitiveContent = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCompactSettingsVersion"/>
    public int WidgetCompactSettingsVersion { get => WidgetShell.WidgetCompactSettingsVersion; set => WidgetShell.WidgetCompactSettingsVersion = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCompactAnimationEffect"/>
    public string WidgetCompactAnimationEffect { get => WidgetShell.WidgetCompactAnimationEffect; set => WidgetShell.WidgetCompactAnimationEffect = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCompactAnimationDurationMs"/>
    public int WidgetCompactAnimationDurationMs { get => WidgetShell.WidgetCompactAnimationDurationMs; set => WidgetShell.WidgetCompactAnimationDurationMs = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCompactExpandDelayMs"/>
    public int WidgetCompactExpandDelayMs { get => WidgetShell.WidgetCompactExpandDelayMs; set => WidgetShell.WidgetCompactExpandDelayMs = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCompactCollapseDelayMs"/>
    public int WidgetCompactCollapseDelayMs { get => WidgetShell.WidgetCompactCollapseDelayMs; set => WidgetShell.WidgetCompactCollapseDelayMs = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCompactMediaCornerMode"/>
    public string WidgetCompactMediaCornerMode { get => WidgetShell.WidgetCompactMediaCornerMode; set => WidgetShell.WidgetCompactMediaCornerMode = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetTitleIconMode"/>
    public string WidgetTitleIconMode { get => WidgetShell.WidgetTitleIconMode; set => WidgetShell.WidgetTitleIconMode = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.DoubleClickToOpen"/>
    public bool DoubleClickToOpen { get => FileWidget.DoubleClickToOpen; set => FileWidget.DoubleClickToOpen = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.FileWidgetFolderOpenBehavior"/>
    public string FileWidgetFolderOpenBehavior { get => FileWidget.FileWidgetFolderOpenBehavior; set => FileWidget.FileWidgetFolderOpenBehavior = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.FileItemSystemContextMenuEnabled"/>
    public bool FileItemSystemContextMenuEnabled { get => FileWidget.FileItemSystemContextMenuEnabled; set => FileWidget.FileItemSystemContextMenuEnabled = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.HideShortcutArrowOverlay"/>
    public bool HideShortcutArrowOverlay { get => FileWidget.HideShortcutArrowOverlay; set => FileWidget.HideShortcutArrowOverlay = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.ShowImageFilesAsIcons"/>
    public bool ShowImageFilesAsIcons { get => FileWidget.ShowImageFilesAsIcons; set => FileWidget.ShowImageFilesAsIcons = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.ShowHoverButtons"/>
    public bool ShowHoverButtons { get => WidgetShell.ShowHoverButtons; set => WidgetShell.ShowHoverButtons = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetHoverButtonActions"/>
    public string WidgetHoverButtonActions { get => WidgetShell.WidgetHoverButtonActions; set => WidgetShell.WidgetHoverButtonActions = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.ResizeSnapEnabled"/>
    public bool ResizeSnapEnabled { get => WidgetShell.ResizeSnapEnabled; set => WidgetShell.ResizeSnapEnabled = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetSnapSpacing"/>
    public double WidgetSnapSpacing { get => WidgetShell.WidgetSnapSpacing; set => WidgetShell.WidgetSnapSpacing = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.ShowListItemDetails"/>
    public bool ShowListItemDetails { get => FileWidget.ShowListItemDetails; set => FileWidget.ShowListItemDetails = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.ShowFileItemPathTooltips"/>
    public bool ShowFileItemPathTooltips { get => FileWidget.ShowFileItemPathTooltips; set => FileWidget.ShowFileItemPathTooltips = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.FileStacksEnabled"/>
    public bool FileStacksEnabled { get => FileWidget.FileStacksEnabled; set => FileWidget.FileStacksEnabled = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.FileStackAutoStacking"/>
    public bool FileStackAutoStacking { get => FileWidget.FileStackAutoStacking; set => FileWidget.FileStackAutoStacking = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.FileStackGroupBy"/>
    public string FileStackGroupBy { get => FileWidget.FileStackGroupBy; set => FileWidget.FileStackGroupBy = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.FileStackThreshold"/>
    public int FileStackThreshold { get => FileWidget.FileStackThreshold; set => FileWidget.FileStackThreshold = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.FileStackOrderBy"/>
    public string FileStackOrderBy { get => FileWidget.FileStackOrderBy; set => FileWidget.FileStackOrderBy = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.FileStackOpenMode"/>
    public string FileStackOpenMode { get => FileWidget.FileStackOpenMode; set => FileWidget.FileStackOpenMode = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.FileStackPopoverLayout"/>
    public string FileStackPopoverLayout { get => FileWidget.FileStackPopoverLayout; set => FileWidget.FileStackPopoverLayout = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.FileStackPopoverStyle"/>
    public string FileStackPopoverStyle { get => FileWidget.FileStackPopoverStyle; set => FileWidget.FileStackPopoverStyle = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.FileStackCustomRules"/>
    public List<FileStackCustomRule> FileStackCustomRules { get => FileWidget.FileStackCustomRules; set => FileWidget.FileStackCustomRules = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.FileStackUnmatchedBehavior"/>
    public string FileStackUnmatchedBehavior { get => FileWidget.FileStackUnmatchedBehavior; set => FileWidget.FileStackUnmatchedBehavior = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.ManagedDropAction"/>
    public string ManagedDropAction { get => FileWidget.ManagedDropAction; set => FileWidget.ManagedDropAction = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.ManagedDragOutAction"/>
    public string ManagedDragOutAction { get => FileWidget.ManagedDragOutAction; set => FileWidget.ManagedDragOutAction = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.DragOutModifierTipEnabled"/>
    public bool DragOutModifierTipEnabled { get => FileWidget.DragOutModifierTipEnabled; set => FileWidget.DragOutModifierTipEnabled = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.DragOutResultHintEnabled"/>
    public bool DragOutResultHintEnabled { get => FileWidget.DragOutResultHintEnabled; set => FileWidget.DragOutResultHintEnabled = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.DefaultManagedStorageRootPath"/>
    public string DefaultManagedStorageRootPath { get => FileWidget.DefaultManagedStorageRootPath; set => FileWidget.DefaultManagedStorageRootPath = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.ManagedStorageDesktopShortcutEnabled"/>
    public bool ManagedStorageDesktopShortcutEnabled { get => FileWidget.ManagedStorageDesktopShortcutEnabled; set => FileWidget.ManagedStorageDesktopShortcutEnabled = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.ManagedStorageDesktopShortcutPath"/>
    public string ManagedStorageDesktopShortcutPath { get => FileWidget.ManagedStorageDesktopShortcutPath; set => FileWidget.ManagedStorageDesktopShortcutPath = value; }

    /// <inheritdoc cref="BackupSettingsSlice.AutomaticBackupEnabled"/>
    public bool AutomaticBackupEnabled { get => Backup.AutomaticBackupEnabled; set => Backup.AutomaticBackupEnabled = value; }

    /// <inheritdoc cref="BackupSettingsSlice.AutomaticBackupIntervalMinutes"/>
    public int AutomaticBackupIntervalMinutes { get => Backup.AutomaticBackupIntervalMinutes; set => Backup.AutomaticBackupIntervalMinutes = value; }

    /// <inheritdoc cref="BackupSettingsSlice.AutomaticBackupRetentionCount"/>
    public int AutomaticBackupRetentionCount { get => Backup.AutomaticBackupRetentionCount; set => Backup.AutomaticBackupRetentionCount = value; }

    /// <inheritdoc cref="BackupSettingsSlice.AutomaticBackupDirectory"/>
    public string AutomaticBackupDirectory { get => Backup.AutomaticBackupDirectory; set => Backup.AutomaticBackupDirectory = value; }

    /// <inheritdoc cref="DesktopOrganizationSettingsSlice.RecentOrganizationHistory"/>
    public List<OrganizationHistoryEntry> RecentOrganizationHistory { get => DesktopOrganization.RecentOrganizationHistory; set => DesktopOrganization.RecentOrganizationHistory = value; }

    /// <inheritdoc cref="DesktopOrganizationSettingsSlice.DesktopOrganizationRules"/>
    public List<DesktopOrganizationRule> DesktopOrganizationRules { get => DesktopOrganization.DesktopOrganizationRules; set => DesktopOrganization.DesktopOrganizationRules = value; }

    /// <inheritdoc cref="DesktopOrganizationSettingsSlice.DesktopAutoOrganizationEnabled"/>
    public bool DesktopAutoOrganizationEnabled { get => DesktopOrganization.DesktopAutoOrganizationEnabled; set => DesktopOrganization.DesktopAutoOrganizationEnabled = value; }

    /// <inheritdoc cref="DesktopOrganizationSettingsSlice.DesktopAutoOrganizationDelaySeconds"/>
    public int DesktopAutoOrganizationDelaySeconds { get => DesktopOrganization.DesktopAutoOrganizationDelaySeconds; set => DesktopOrganization.DesktopAutoOrganizationDelaySeconds = value; }

    /// <inheritdoc cref="DesktopOrganizationSettingsSlice.DesktopAutoOrganizationBaselineUtc"/>
    public DateTimeOffset? DesktopAutoOrganizationBaselineUtc { get => DesktopOrganization.DesktopAutoOrganizationBaselineUtc; set => DesktopOrganization.DesktopAutoOrganizationBaselineUtc = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.IconSize"/>
    public double IconSize { get => WidgetShell.IconSize; set => WidgetShell.IconSize = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.TextSize"/>
    public double TextSize { get => WidgetShell.TextSize; set => WidgetShell.TextSize = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.LayoutDensity"/>
    public string LayoutDensity { get => WidgetShell.LayoutDensity; set => WidgetShell.LayoutDensity = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.LayoutDensityScale"/>
    public double LayoutDensityScale { get => WidgetShell.LayoutDensityScale; set => WidgetShell.LayoutDensityScale = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.HorizontalSpacingScale"/>
    public double HorizontalSpacingScale { get => WidgetShell.HorizontalSpacingScale; set => WidgetShell.HorizontalSpacingScale = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.VerticalSpacingScale"/>
    public double VerticalSpacingScale { get => WidgetShell.VerticalSpacingScale; set => WidgetShell.VerticalSpacingScale = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.FileNameWidthScale"/>
    public double FileNameWidthScale { get => FileWidget.FileNameWidthScale; set => FileWidget.FileNameWidthScale = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.FileNameLineCount"/>
    public int FileNameLineCount { get => FileWidget.FileNameLineCount; set => FileWidget.FileNameLineCount = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.ShowFileExtensions"/>
    public bool ShowFileExtensions { get => FileWidget.ShowFileExtensions; set => FileWidget.ShowFileExtensions = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.HideShortcutExtensionWhenShowingFileExtensions"/>
    public bool HideShortcutExtensionWhenShowingFileExtensions { get => FileWidget.HideShortcutExtensionWhenShowingFileExtensions; set => FileWidget.HideShortcutExtensionWhenShowingFileExtensions = value; }

    /// <inheritdoc cref="WidgetLayoutSettingsSlice.Widgets"/>
    public List<WidgetConfig> Widgets { get => WidgetLayout.Widgets; set => WidgetLayout.Widgets = value; }

    /// <inheritdoc cref="WidgetLayoutSettingsSlice.WidgetDefaultBoundScreenId"/>
    public string? WidgetDefaultBoundScreenId
    {
        get => WidgetLayout.WidgetDefaultBoundScreenId;
        set => WidgetLayout.WidgetDefaultBoundScreenId = value;
    }

    /// <inheritdoc cref="WidgetLayoutSettingsSlice.WidgetNewPlacementTarget"/>
    public string WidgetNewPlacementTarget
    {
        get => WidgetLayout.WidgetNewPlacementTarget;
        set => WidgetLayout.WidgetNewPlacementTarget = value;
    }

    /// <inheritdoc cref="WidgetLayoutSettingsSlice.WidgetDisplayDisconnectBehavior"/>
    public string WidgetDisplayDisconnectBehavior
    {
        get => WidgetLayout.WidgetDisplayDisconnectBehavior;
        set => WidgetLayout.WidgetDisplayDisconnectBehavior = value;
    }

    /// <inheritdoc cref="WidgetLayoutSettingsSlice.WidgetGroups"/>
    public List<WidgetGroupConfig> WidgetGroups { get => WidgetLayout.WidgetGroups; set => WidgetLayout.WidgetGroups = value; }

    /// <inheritdoc cref="WidgetLayoutSettingsSlice.WidgetTopologyLayouts"/>
    public Dictionary<string, WidgetTopologyLayoutProfile> WidgetTopologyLayouts { get => WidgetLayout.WidgetTopologyLayouts; set => WidgetLayout.WidgetTopologyLayouts = value; }

    /// <inheritdoc cref="WidgetLayoutSettingsSlice.ActiveWidgetTopologyKey"/>
    public string? ActiveWidgetTopologyKey { get => WidgetLayout.ActiveWidgetTopologyKey; set => WidgetLayout.ActiveWidgetTopologyKey = value; }

    /// <inheritdoc cref="WidgetLayoutSettingsSlice.WidgetGroupsEnabled"/>
    public bool WidgetGroupsEnabled { get => WidgetLayout.WidgetGroupsEnabled; set => WidgetLayout.WidgetGroupsEnabled = value; }

    /// <inheritdoc cref="WidgetLayoutSettingsSlice.WidgetGroupDefaultNavigationStyle"/>
    public string WidgetGroupDefaultNavigationStyle { get => WidgetLayout.WidgetGroupDefaultNavigationStyle; set => WidgetLayout.WidgetGroupDefaultNavigationStyle = value; }

    /// <inheritdoc cref="WidgetLayoutSettingsSlice.WidgetGroupDefaultTitleDisplayMode"/>
    public string WidgetGroupDefaultTitleDisplayMode { get => WidgetLayout.WidgetGroupDefaultTitleDisplayMode; set => WidgetLayout.WidgetGroupDefaultTitleDisplayMode = value; }

    /// <inheritdoc cref="WidgetLayoutSettingsSlice.WidgetGroupWheelSwitchEnabled"/>
    public bool WidgetGroupWheelSwitchEnabled { get => WidgetLayout.WidgetGroupWheelSwitchEnabled; set => WidgetLayout.WidgetGroupWheelSwitchEnabled = value; }

    /// <inheritdoc cref="WidgetLayoutSettingsSlice.WidgetGroupHoverSwitchEnabled"/>
    public bool WidgetGroupHoverSwitchEnabled { get => WidgetLayout.WidgetGroupHoverSwitchEnabled; set => WidgetLayout.WidgetGroupHoverSwitchEnabled = value; }

    /// <inheritdoc cref="WidgetLayoutSettingsSlice.WidgetGroupSwitchAnimationStyle"/>
    public string WidgetGroupSwitchAnimationStyle { get => WidgetLayout.WidgetGroupSwitchAnimationStyle; set => WidgetLayout.WidgetGroupSwitchAnimationStyle = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.FocusClickedWidgetOnRaise"/>
    public bool FocusClickedWidgetOnRaise { get => WidgetShell.FocusClickedWidgetOnRaise; set => WidgetShell.FocusClickedWidgetOnRaise = value; }

    /// <inheritdoc cref="WidgetLayoutSettingsSlice.DeletedWidgetIds"/>
    public List<string> DeletedWidgetIds { get => WidgetLayout.DeletedWidgetIds; set => WidgetLayout.DeletedWidgetIds = value; }

    // 鈹€鈹€鈹€ Weather Widget Settings 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€

    // 鈹€鈹€鈹€ Search Settings 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€

    // 鈹€鈹€鈹€ Cloud Backup (roadmap 搂10 鈥?non-secret provider config only) 鈹€鈹€鈹€

    /// <inheritdoc cref="CloudBackupSettingsSlice.CloudBackupProvider"/>
    public string CloudBackupProvider { get => CloudBackup.CloudBackupProvider; set => CloudBackup.CloudBackupProvider = value; }

    /// <inheritdoc cref="CloudBackupSettingsSlice.CloudBackupServerUrl"/>
    public string CloudBackupServerUrl { get => CloudBackup.CloudBackupServerUrl; set => CloudBackup.CloudBackupServerUrl = value; }

    /// <inheritdoc cref="CloudBackupSettingsSlice.CloudBackupRemotePath"/>
    public string CloudBackupRemotePath { get => CloudBackup.CloudBackupRemotePath; set => CloudBackup.CloudBackupRemotePath = value; }

    /// <inheritdoc cref="CloudBackupSettingsSlice.CloudBackupUsername"/>
    public string CloudBackupUsername { get => CloudBackup.CloudBackupUsername; set => CloudBackup.CloudBackupUsername = value; }

    /// <inheritdoc cref="CloudBackupSettingsSlice.CloudBackupWidgetStyleEnabled"/>
    public bool CloudBackupWidgetStyleEnabled { get => CloudBackup.CloudBackupWidgetStyleEnabled; set => CloudBackup.CloudBackupWidgetStyleEnabled = value; }

    /// <inheritdoc cref="CloudBackupSettingsSlice.CloudBackupRetentionCount"/>
    public int CloudBackupRetentionCount { get => CloudBackup.CloudBackupRetentionCount; set => CloudBackup.CloudBackupRetentionCount = value; }

    /// <inheritdoc cref="CloudBackupSettingsSlice.CloudBackupIntervalMinutes"/>
    public int CloudBackupIntervalMinutes { get => CloudBackup.CloudBackupIntervalMinutes; set => CloudBackup.CloudBackupIntervalMinutes = value; }

    /// <inheritdoc cref="CloudBackupSettingsSlice.CloudBackupLastSuccessUtcTicks"/>
    public long CloudBackupLastSuccessUtcTicks { get => CloudBackup.CloudBackupLastSuccessUtcTicks; set => CloudBackup.CloudBackupLastSuccessUtcTicks = value; }

    /// <inheritdoc cref="CloudBackupSettingsSlice.CloudBackupLastFailureUtcTicks"/>
    public long CloudBackupLastFailureUtcTicks { get => CloudBackup.CloudBackupLastFailureUtcTicks; set => CloudBackup.CloudBackupLastFailureUtcTicks = value; }

    /// <inheritdoc cref="CloudBackupSettingsSlice.CloudBackupLastUnverifiedUtcTicks"/>
    public long CloudBackupLastUnverifiedUtcTicks { get => CloudBackup.CloudBackupLastUnverifiedUtcTicks; set => CloudBackup.CloudBackupLastUnverifiedUtcTicks = value; }
}
