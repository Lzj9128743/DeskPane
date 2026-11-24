using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using DeskPane.Core.Persistence;
using DeskPane.Helpers;
using DeskPane.Models;
using DeskPane.Platform;
using DeskPane.Services;

namespace DeskPane;

public partial class App
{
    public DeskPaneDiagnosticsBundleService DiagnosticsBundleService { get; private set; } = null!;

    public DeskPaneDiagnosticSnapshot CreateDiagnosticSnapshot()
    {
        AppRuntimeHealthSnapshot? runtimeHealth = DiagnosticsService?.GetRuntimeHealthSnapshot(
            WidgetManager?.GetFolderWatcherHealthSnapshots());
        DeskPaneWidgetManagerDiagnostic widgetManager =
            WidgetManager?.CreateDiagnosticsSnapshot() ?? DeskPaneWidgetManagerDiagnostic.Empty;
        IReadOnlyList<DeskPaneDisplayDiagnostic> displays = GetDisplayDiagnostics();
        SettingsPersistenceFailure? saveFailure = SettingsService.LastPersistenceFailure;
        GlobalHotkeyGesture toggleGesture = GlobalHotkeyService?.CurrentGesture ??
            GlobalHotkeyService.NormalizeGesture(
                SettingsService.Settings.GlobalHotkeyModifiers,
                SettingsService.Settings.GlobalHotkeyKey);
        ShortcutNativeDiagnosticState shortcutNative =
            ShortcutNativeBackend.CaptureDiagnosticState();
        var startupLedger = new DeskPaneStartupLedgerDiagnostic(
            StormGuardActive: _startupStormAssessment?.SuppressFileMutatingStartup == true,
            AssessmentReason: _startupStormAssessment?.Reason,
            PreviousLaunchStartedAtUtc: _startupStormAssessment?.PreviousLaunchStartedAtUtc);
        var callbackRadarCounts = Platform.CallbackDepth.SnapshotWindowOperationCounts();
        var callbackRadar = new DeskPaneCallbackRadarDiagnostic(
            callbackRadarCounts.Values.Sum(),
            callbackRadarCounts,
            Platform.UiDispatch.PostAfterCallbackDroppedCount);
        RecoveryOfferMarker? recoveryOfferMarker = RecoveryOfferStore.TryRead();
        var recoveryOffer = new DeskPaneRecoveryOfferDiagnostic(
            recoveryOfferMarker is not null,
            recoveryOfferMarker?.DetectedAtUtc.ToUniversalTime(),
            recoveryOfferMarker?.SnapshotCreatedAtUtc.ToUniversalTime(),
            recoveryOfferMarker?.SnapshotAppVersion,
            recoveryOfferMarker?.SnapshotWidgetCount ?? 0,
            _lastRecoveryOfferOutcome.ToString());

        return new DeskPaneDiagnosticSnapshot(
            SchemaVersion: 9,
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            AppVersion: GetDiagnosticVersion(),
            DistributionChannel: DistributionService.ChannelName,
            IsPackaged: DistributionService.IsPackaged,
            OperatingSystem: RuntimeInformation.OSDescription,
            ProcessArchitecture: RuntimeInformation.ProcessArchitecture.ToString(),
            UiCulture: CultureInfo.CurrentUICulture.Name,
            Hotkeys: new DeskPaneHotkeyDiagnostic(
                SettingsService.Settings.GlobalHotkeyEnabled,
                GlobalHotkeyService?.IsRegistered == true,
                (int)toggleGesture.Modifiers,
                toggleGesture.VirtualKey,
                GlobalHotkeyService?.ReceivedCount ?? 0,
                GlobalHotkeyService?.InvocationCount ?? 0,
                GlobalHotkeyService?.DispatchFailureCount ?? 0,
                GlobalHotkeyService?.UsesReservedHook == true,
                GlobalHotkeyService?.ReservedHookThreadId ?? 0,
                GlobalHotkeyService?.ReservedHookLastErrorCode ?? 0,
                GlobalHotkeyService?.ReservedHookTriggerCount ?? 0,
                GlobalHotkeyService?.ReservedHookPostFailureCount ?? 0,
                GlobalHotkeyService?.ReservedHookInputFailureCount ?? 0,
                !string.IsNullOrWhiteSpace(GlobalHotkeyService?.LastError),
                false,
                false,
                0,
                0),
            Settings: new DeskPaneSettingsDiagnostic(
                SettingsService.LastLoadRecoveryState,
                SettingsService.HasPendingSave,
                saveFailure?.Operation,
                saveFailure?.OccurredAt.ToUniversalTime()),
            RecoveryOffer: recoveryOffer,
            ShortcutNative: new DeskPaneShortcutNativeDiagnostic(
                shortcutNative.SelectedBackend,
                shortcutNative.ModuleName,
                shortcutNative.ModuleExists,
                shortcutNative.ModuleArchitecture,
                shortcutNative.ModuleSha256,
                shortcutNative.LoadAttempted,
                shortcutNative.LoadState,
                shortcutNative.AbiVersion,
                shortcutNative.Capabilities),
            RuntimeHealth: runtimeHealth,
            WidgetManager: widgetManager,
            Displays: displays,
            StorageMigration: WidgetManager?.StorageMigrationDiagnostic,
            StartupLedger: startupLedger,
            CallbackRadar: callbackRadar);
    }

    private static IReadOnlyList<DeskPaneDisplayDiagnostic> GetDisplayDiagnostics()
    {
        try
        {
            return Win32Helper.GetMonitorWorkAreaInfos()
                .Select((display, index) => new DeskPaneDisplayDiagnostic(
                    index + 1,
                    display.IsPrimary,
                    display.DpiScale,
                    ToDiagnosticRect(display.Monitor),
                    ToDiagnosticRect(display.WorkArea)))
                .ToArray();
        }
        catch (Exception ex)
        {
            Log($"[DiagnosticsBundle] Display enumeration failed: {ex.Message}");
            return [];
        }
    }

    private static DeskPaneDiagnosticRect ToDiagnosticRect(Win32Helper.RECT bounds)
    {
        return new DeskPaneDiagnosticRect(
            bounds.Left,
            bounds.Top,
            bounds.Right - bounds.Left,
            bounds.Bottom - bounds.Top);
    }

    private static string GetDiagnosticVersion()
    {
        Assembly assembly = typeof(App).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                   ?.InformationalVersion
               ?? assembly.GetName().Version?.ToString()
               ?? "unknown";
    }
}
