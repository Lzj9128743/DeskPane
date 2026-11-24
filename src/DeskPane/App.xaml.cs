// Copyright (c) DeskPane. All rights reserved.

using CommunityToolkit.Mvvm.Input;
using DeskPane.Controls.WidgetContents;
using DeskPane.Core.Persistence;
using DeskPane.Helpers;
using DeskPane.Models;
using DeskPane.Platform;
using DeskPane.Services;
using DeskPane.Views;
using System.Diagnostics;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using DrawingPoint = System.Drawing.Point;
using WinRT.Interop;

namespace DeskPane;

/// <summary>
/// Application bootstrap, tray menu, and widget lifecycle.
/// </summary>
public partial class App : Application
{
    private const double TrayMenuItemWidth = 200;
    private const int TrayContextMenuFallbackOffsetPixels = 24;
    private const int TrayContextMenuEstimatedWidth = (int)TrayMenuItemWidth + 24;
    private const int VisibleIdleMemoryCheckIntervalSeconds = 15;
    private const int VisibleIdleMemoryMinimumCooldownSeconds = 60;
    private const int BackgroundMemoryCleanupRetryDelaySeconds = 5;
    private const int BackgroundMemoryCleanupMaximumRetryDelaySeconds = 30;
    private const string AwaitParentExitArgument = "--await-parent-exit";
    // The unelevated relaunch waits out the elevated instance's graceful
    // shutdown (which releases the single-instance mutex); the timeout only
    // guards a shutdown that wedges, after which the launch continues into
    // the normal secondary-instance path. The graceful shutdown sequence has
    // five bounded 15s steps plus teardown margins (worst case ~50s), so the
    // wait must outlast that budget or a slow-but-healthy shutdown reads as
    // wedged and the relaunch gives up.
    private const int AwaitParentExitTimeoutMs = 60_000;
    private const int MaxQueuedLogLines = 4096;
    private const long MaxLogFileSizeBytes = 5 * 1024 * 1024; // 5 MB before rotation
    private const string DesktopOrganizationNotificationGroup = "desktop-organization";
    private const string DesktopOrganizationTargetUnavailableNotificationTag = "desktop-organization-target-unavailable";
    private const string FolderMappingNotificationGroup = "folder-mapping";
    private const string FolderMappingConflictNotificationTag = "folder-mapping-conflict";
    private const string PendingJumpListArgumentFileName = "pending-jumplist-arg.txt";
    private const string VerboseLoggingEnvironmentVariable = "DESKPANE_VERBOSE_LOG";
    private static readonly bool EnableVerboseLogging = IsEnabledEnvironmentValue(
        Environment.GetEnvironmentVariable(VerboseLoggingEnvironmentVariable));

    private static readonly string LogPath = DeskPaneDataPathService.Current.LogFilePath;
    private static readonly NativeNotificationActivationEnvelopeStore
        PendingNativeNotificationActivationStore = new(
            DeskPaneDataPathService.Current.RootPath);
    private static readonly string PendingJumpListArgumentPath = Path.Combine(
        DeskPaneDataPathService.Current.RootPath,
        PendingJumpListArgumentFileName);
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> s_logQueue = new();
    private static readonly SemaphoreSlim s_logSignal = new(0);
    private static int s_logWorkerStarted;
    private static int s_pendingLogLineCount;
    private static int s_logDirectoryEnsured;

    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _activationEvent;
    private static RegisteredWaitHandle? _activationRegistration;

    // Written by the primary instance only; secondary activations never touch
    // the file. See StartupSessionLedger for the lifecycle contract.
    private readonly StartupSessionLedger? _startupSessionLedger;
    private readonly StartupStormAssessment? _startupStormAssessment;

    private bool SuppressFileMutatingStartup =>
        _startupStormAssessment?.SuppressFileMutatingStartup == true;

    private TaskbarIcon? _trayIcon;
    private Window? _trayWindow;
    private Window? _folderPickerHelperWindow;
    private MenuFlyout? _trayContextMenu;
    private bool _traySecondWindowSyncLogged;
    private MenuFlyoutItem? _trayOrganizeDesktopItem;
    private MenuFlyoutItem? _trayToggleDesktopIconsItem;
    private MenuFlyoutItem? _trayMapFolderItem;
    private MenuFlyoutItem? _trayToggleThemeItem;
    private MenuFlyoutSubItem? _trayOpacityMenu;
    private MenuFlyoutItem? _trayToggleLanguageItem;
    private MenuFlyoutItem? _trayAutoStartItem;
    private readonly Dictionary<WidgetKind, MenuFlyoutItem> _trayCreateWidgetItems = [];
    private MenuFlyoutItem? _trayExitItem;
    private ElevationNoticeWindow? _elevationNoticeWindow;
    private RecoveryOfferWindow? _recoveryOfferWindow;
    private RecoveryOfferStore? _recoveryOfferStore;
    private bool _recoveryOfferShownThisSession;
    private bool _recoveryOfferInFlight;
    private DateTimeOffset? _lastSnapshotPausedReminderAtUtc;
    private RecoveryOfferOutcome _lastRecoveryOfferOutcome = RecoveryOfferOutcome.NotOffered;
    private readonly DateTimeOffset _startupProcessStartUtc =
        new DateTimeOffset(Process.GetCurrentProcess().StartTime).ToUniversalTime();

    internal RecoveryOfferStore RecoveryOfferStore =>
        _recoveryOfferStore ??= new RecoveryOfferStore();

    /// <summary>
    /// Wipe-recovery detection, offer and snapshot suppression stay off
    /// development data roots: smoke harnesses and portable runs deliberately
    /// start without settings and their snapshots are test fixtures.
    /// </summary>
    internal bool IsWipeRecoveryEligible =>
        !DeskPaneDataPathService.Current.IsDevelopmentRoot;
    private NativeAppNotificationService? _nativeNotificationService;
    private NativeNotificationActivationBootstrap? _nativeNotificationBootstrap;
    private bool _initialNotificationHandled;
    private readonly ShutdownSequence _shutdownSequence = new(Log);
    private static int s_shutdownRequested;
    internal static bool IsShuttingDown => Volatile.Read(ref s_shutdownRequested) != 0;
    private static readonly TimeSpan ShutdownHardTerminateGrace = TimeSpan.FromSeconds(3);
    private DisplayAreaWatcherService? _displayAreaWatcher;
    private DisplayTopologyTransitionCoordinator? _displayTopologyTransitionCoordinator;
    private AppLifecycleRecoveryWatcher? _lifecycleRecoveryWatcher;
    private HookHealthWatchdog? _hookHealthWatchdog;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _visibleIdleMemoryMaintenanceTimer;
    private int _visibleIdleMemoryMaintenanceRunning;
    private readonly VisibleIdleMemoryTracker _visibleIdleMemoryTracker = new(
        TimeSpan.FromSeconds(
            PerformanceSettingsPolicy.DefaultVisibleIdleCacheCleanupDelaySeconds),
        TimeSpan.FromSeconds(
            PerformanceSettingsPolicy.DefaultVisibleIdleCacheCleanupDelaySeconds));
    private bool _widgetsRaisedFromTray;
    private DateTimeOffset _lastSettingsPersistenceNotificationAt = DateTimeOffset.MinValue;
    private int _externalStateRecoveryScheduled;
    private bool _externalActivationReady;
    private bool _externalActivationRequestedWhileBusy;
    private bool _externalActivationHandling;
    private DateTimeOffset? _lastBareExternalActivationAtUtc;
    private readonly bool _processStartupLaunchDetected;
    private readonly StartupElevationAssessment _startupElevationAssessment = new(false, false, false, false);

    /// <summary>
    /// Cached launch-time elevation snapshot. Same-level fully-elevated
    /// sessions read <see cref="ShellIntegrityMismatch.None"/> here; surface
    /// code uses the mismatch leg to tell "drag-in is blocked" (app above
    /// shell) apart from same-level sessions where drag-in works.
    /// </summary>
    internal StartupElevationAssessment StartupElevation => _startupElevationAssessment;
    private bool _cloudBackupUnverifiedToastShown;

    public static new App Current => (App)Application.Current;

    public static Microsoft.UI.Dispatching.DispatcherQueue UiDispatcherQueue { get; private set; } = null!;

    /// <summary>
    /// Raised after an external display topology change (hot-plug, resolution
    /// change) has been processed. Always invoked on the UI thread via
    /// <see cref="UiDispatcherQueue"/>; settings sections subscribe to drop
    /// stale arrangement previews.
    /// </summary>
    public event Action? DisplayTopologyChanged;

    /// <summary>
    /// Raised (UI thread) when a widget's screen home changes anywhere —
    /// drag commits, context-menu moves, move-all/undo — so open UI surfaces
    /// such as the settings display preview refresh promptly instead of
    /// waiting for the page to be reopened.
    /// </summary>
    public event Action? ScreenHomeChanged;

    internal void NotifyScreenHomeChanged()
    {
        UiDispatcherQueue?.TryEnqueue(() => ScreenHomeChanged?.Invoke());
    }

    public bool IsStartupMode { get; set; }

    public AppDistributionService DistributionService { get; } = AppDistributionService.Current;
    public ServiceProvider Services { get; private set; } = null!;
    public SettingsService SettingsService { get; private set; } = null!;
    public DeskPaneDataBackupService DataBackupService { get; private set; } = null!;
    internal CloudBackupService CloudBackupService { get; private set; } = null!;
    public FileService FileService { get; private set; } = null!;
    public OrganizerService OrganizerService { get; private set; } = null!;
    public ManagedStorageDesktopShortcutService ManagedStorageDesktopShortcutService { get; private set; } = null!;
    public LocalizationService LocalizationService { get; private set; } = null!;
    public ThemeService ThemeService { get; private set; } = null!;
    public GlobalHotkeyService? GlobalHotkeyService { get; private set; }
    public DesktopDoubleClickActivationService? DesktopDoubleClickActivationService { get; private set; }
    public AppDiagnosticsService? DiagnosticsService => _diagnosticsService;
    public WidgetManager? WidgetManager { get; private set; }
    public ResizeGuideOverlayService ResizeGuideOverlay { get; private set; } = null!;
    public NativeAppNotificationService? NativeNotificationService => _nativeNotificationService;
    public DisplayAreaWatcherService? DisplayAreaWatcher => _displayAreaWatcher;
    public DesktopAutoOrganizationWatcher? DesktopAutoOrganizationWatcher { get; private set; }

    public static bool IsVerboseLoggingEnabled => EnableVerboseLogging;

    public App()
    {
        DeskPane.Platform.UiDispatch.MarkUiOriginThread();
        WaitForRelaunchParentExit(Environment.GetCommandLineArgs());

        // Register AUMID early so the taskbar button and Jump List work
        // for both packaged (MSIX) and unpackaged (Direct) distributions.
        JumpListService.RegisterAppUserModelId();

        Log("App() constructor start");
        _processStartupLaunchDetected = StartupLaunchPolicy.IsStartupLaunch(
            Environment.GetCommandLineArgs(),
            isStartupTaskActivation: IsStartupTaskActivation());
        _activationEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            DeskPaneDataPathService.Current.ActivationEventName);
        _singleInstanceMutex = new Mutex(
            true,
            DeskPaneDataPathService.Current.SingleInstanceMutexName,
            out bool createdNew);
        if (createdNew)
        {
            // The launch breadcrumb is written before anything can exit so a
            // death anywhere in this launch is attributable on the next one.
            _startupSessionLedger = new StartupSessionLedger(
                Path.Combine(DeskPaneDataPathService.Current.RootPath, "startup-ledger.json"));
            _startupStormAssessment = _startupSessionLedger.BeginPrimaryLaunch();
            if (_startupStormAssessment.SuppressFileMutatingStartup)
            {
                Log(
                    $"[Startup] Storm guard active ({_startupStormAssessment.Reason}): " +
                    "desktop file operations stay deferred this launch.");
            }
        }

        bool notificationLaunch = NativeNotificationActivationBootstrap.IsNotificationLaunch(
            Environment.GetCommandLineArgs());
        if (createdNew || notificationLaunch)
        {
            s_startupLaunchQuietExit = _processStartupLaunchDetected || notificationLaunch;
            // Cover constructor/COM initialization as well as OnLaunched.
            StartStartupWatchdog();
            EnsureNativeNotificationService();
        }
        NativeAppNotificationActivation? nativeNotificationActivation =
            TryGetCurrentNativeNotificationActivation();
        if (!createdNew)
        {
            string? jumpListArg = nativeNotificationActivation is null &&
                !_processStartupLaunchDetected
                    ? JumpListService.TryGetJumpListArgument(
                        string.Join(' ', Environment.GetCommandLineArgs()))
                    : null;
            string activationKind = nativeNotificationActivation is not null
                ? "notification"
                : _processStartupLaunchDetected
                    ? "startup"
                    : jumpListArg is not null
                        ? $"jump-list:{jumpListArg}"
                        : "bare";
            Log(
                $"[Activation] Secondary instance kind={activationKind} " +
                $"argumentCount={Math.Max(0, Environment.GetCommandLineArgs().Length - 1)} " +
                $"{GetParentProcessReport()}");

            if (nativeNotificationActivation is not null)
            {
                NativeNotificationActivationEnvelopeWriteResult writeResult =
                    PendingNativeNotificationActivationStore.Store(nativeNotificationActivation);
                Log(
                    $"[Notification] Forwarded typed activation envelope " +
                    $"disposition={writeResult.Disposition} " +
                    $"envelope={writeResult.Envelope?.EnvelopeId ?? "none"} " +
                    $"userInput={writeResult.Envelope?.UserInput.Count ?? 0} " +
                    $"error={writeResult.Error ?? "none"}");
            }
            else if (notificationLaunch)
            {
                Log("[Notification] Secondary notification activation unavailable; leaving the primary instance running");
                _nativeNotificationService?.Dispose();
                DrainLogQueue();
                Environment.Exit(1);
            }
            else if (_processStartupLaunchDetected)
            {
                Log("Another instance running; startup launch exiting silently");
                Environment.Exit(0);
            }
            else
            {
                if (jumpListArg is not null)
                {
                    StorePendingJumpListArgument(jumpListArg);
                }
            }

            Log("Another instance running, signaling existing instance");
            try
            {
                _activationEvent.Set();
            }
            catch (Exception ex)
            {
                Log($"Failed to signal existing instance: {ex}");
            }

            _nativeNotificationService?.Dispose();
            DrainLogQueue();
            Environment.Exit(0);
        }

        // Elevation gate: an elevated DeskPane cannot receive OLE drops from
        // Explorer (UIPI) and cannot start XAML drags either, so an elevated
        // launch either relaunches itself unelevated or surfaces a notice.
        // The mutex stays owned by the kernel on these exits: the replacement
        // instance waits for this process via --await-parent-exit.
        _startupElevationAssessment = DragDropPermissionService.AssessStartupElevation();
        if (_startupElevationAssessment.IsCurrentProcessElevated)
        {
            Log(
                $"[Elevation] Elevated launch detected " +
                $"uacDisabled={_startupElevationAssessment.IsUacDisabled} " +
                $"explorerElevated={_startupElevationAssessment.IsExplorerElevated} " +
                $"canRelaunch={_startupElevationAssessment.CanRelaunchUnelevated} " +
                $"mismatch={_startupElevationAssessment.Mismatch}");
            if (_processStartupLaunchDetected)
            {
                // Nobody is watching an unattended launch: fix the cause and
                // relaunch unelevated instead of parking a dialog. A failed
                // relaunch keeps this instance running (drag-drop degraded).
                _ = DragDropPermissionService.ClearElevationCompatibilityFlags();

                // Two loop breakers: only relaunch when Explorer really offers
                // a standard-user context, and never twice in a row — a
                // relaunch that lands elevated again (elevated Explorer, or
                // the ShellExecute fallback spawning an elevated child) must
                // not restart forever on an unattended desktop.
                bool alreadyRelaunchedForElevation =
                    TryGetAwaitParentExitProcessId(Environment.GetCommandLineArgs()) is not null;
                if (_startupElevationAssessment.CanRelaunchUnelevated &&
                    !alreadyRelaunchedForElevation)
                {
                    string relaunchArguments =
                        $"{AwaitParentExitArgument} {Environment.ProcessId} --startup";
                    if (DragDropPermissionService.TryRelaunchAsExplorerUser(relaunchArguments))
                    {
                        Log("[Elevation] Unattended launch relaunched unelevated; exiting elevated instance");
                        _startupSessionLedger?.MarkCleanExit();
                        _nativeNotificationService?.Dispose();
                        DrainLogQueue();
                        Environment.Exit(0);
                    }

                    Log("[Elevation] Unattended relaunch failed; continuing elevated");
                }
                else
                {
                    Log(
                        "[Elevation] Unattended relaunch skipped " +
                        $"(canRelaunch={_startupElevationAssessment.CanRelaunchUnelevated}, " +
                        $"alreadyRelaunched={alreadyRelaunchedForElevation}); continuing elevated");
                }
            }
            // Interactive launches defer the notice to OnLaunched, once the
            // tray exists and the startup pipeline is no longer watching.
        }

        InitializeComponent();

        // Build DI container and resolve core services
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddDeskPaneServices();
        Services = serviceCollection.BuildServiceProvider();

        SettingsService = Services.GetRequiredService<SettingsService>();
        SettingsService.PersistenceFailed += OnSettingsPersistenceFailed;
        SettingsService.ExternalSnapshotApplied += OnExternalSettingsSnapshotApplied;
        DataBackupService = Services.GetRequiredService<DeskPaneDataBackupService>();
        DataBackupService.AutomaticSnapshotFallbackDetected += OnAutomaticBackupFallbackDetected;
        // While a wipe-recovery decision is pending, scheduled automatic
        // snapshots stay paused so the fresh-default profile cannot evict the
        // pre-wipe snapshots the offer restores from.
        DataBackupService.SetRecoveryOfferPauseProbe(
            () => RecoveryOfferStore.TryRead() is not null);
        // The pending offer's referenced snapshot survives retention pruning
        // (without consuming a slot) so manual backups during the pending
        // window cannot evict the archive the offer restores from.
        DataBackupService.SetRecoverySnapshotRetentionPin(
            () => RecoveryOfferStore.TryRead()?.SnapshotPath);
        CloudBackupService = Services.GetRequiredService<CloudBackupService>();
        CloudBackupService.BackupRunCompleted += OnCloudBackupRunCompleted;
        DiagnosticsBundleService = Services.GetRequiredService<DeskPaneDiagnosticsBundleService>();
        FileService = Services.GetRequiredService<FileService>();
        OrganizerService = Services.GetRequiredService<OrganizerService>();
        ManagedStorageDesktopShortcutService =
            Services.GetRequiredService<ManagedStorageDesktopShortcutService>();
        ResizeGuideOverlay = Services.GetRequiredService<ResizeGuideOverlayService>();

        StartupService.Configure(StartupServiceFactory.Create(DistributionService, SettingsService));
        UnhandledException += OnUnhandledException;
        Log($"Distribution channel={DistributionService.ChannelName} packaged={DistributionService.IsPackaged}");
    }

    private static string GetProcessIntegrityReport()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return $"isAdminRole={principal.IsInRole(WindowsBuiltInRole.Administrator)} {GetProcessTokenReport(ProcessDiagnosticsNativeMethods.GetCurrentProcess())}";
        }
        catch (Exception ex)
        {
            return $"unknown error={ex.Message}";
        }
    }

    private static string GetAppCompatReport()
    {
        string exePath = Path.Combine(AppContext.BaseDirectory, "DeskPane.exe");
        string? currentUser = GetAppCompatLayerValue(Registry.CurrentUser, exePath);
        string? localMachine = GetAppCompatLayerValue(Registry.LocalMachine, exePath);

        return $"exe='{exePath}' hkcu={(string.IsNullOrWhiteSpace(currentUser) ? "none" : currentUser)} " +
               $"hklm={(string.IsNullOrWhiteSpace(localMachine) ? "none" : localMachine)}";
    }

    private static string GetParentProcessReport()
    {
        try
        {
            if (!TryGetParentProcessId(Environment.ProcessId, out uint parentProcessId) || parentProcessId == 0)
            {
                return "unknown";
            }

            string parentName = "unknown";
            try
            {
                parentName = Process.GetProcessById((int)parentProcessId).ProcessName;
            }
            catch
            {
            }

            string parentTokenReport = GetProcessTokenReport(parentProcessId);
            return $"ppid={parentProcessId} parent={parentName} {parentTokenReport}";
        }
        catch (Exception ex)
        {
            return $"unknown error={ex.Message}";
        }
    }

    private static string GetProcessTokenReport(uint processId)
    {
        IntPtr processHandle = ProcessDiagnosticsNativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (processHandle == IntPtr.Zero)
        {
            return $"token=unavailable error={Marshal.GetLastWin32Error()}";
        }

        try
        {
            return GetProcessTokenReport(processHandle);
        }
        finally
        {
            ProcessDiagnosticsNativeMethods.CloseHandle(processHandle);
        }
    }

    private static string GetProcessTokenReport(IntPtr processHandle)
    {
        string tokenElevated = TryGetTokenElevation(processHandle, out bool isTokenElevated)
            ? isTokenElevated.ToString()
            : "unknown";
        string integrityLevel = TryGetIntegrityLevel(processHandle, out string level)
            ? level
            : "unknown";

        return $"tokenElevated={tokenElevated} integrity={integrityLevel}";
    }

    private static string GetUacPolicyReport()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\System");
            object? enableLua = key?.GetValue("EnableLUA");
            object? consentPrompt = key?.GetValue("ConsentPromptBehaviorAdmin");
            object? promptOnSecureDesktop = key?.GetValue("PromptOnSecureDesktop");

            return $"EnableLUA={FormatRegistryValue(enableLua)} " +
                   $"ConsentPromptBehaviorAdmin={FormatRegistryValue(consentPrompt)} " +
                   $"PromptOnSecureDesktop={FormatRegistryValue(promptOnSecureDesktop)}";
        }
        catch (Exception ex)
        {
            return $"unknown error={ex.Message}";
        }
    }

    private static string FormatRegistryValue(object? value)
    {
        return value is null ? "missing" : value.ToString() ?? "unknown";
    }

    private static bool TryGetParentProcessId(int processId, out uint parentProcessId)
    {
        parentProcessId = 0;
        const uint Th32csSnapProcess = 0x00000002;

        IntPtr snapshot = ProcessDiagnosticsNativeMethods.CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return false;
        }

        try
        {
            var entry = new ProcessDiagnosticsNativeMethods.ProcessEntry32
            {
                dwSize = (uint)Marshal.SizeOf<ProcessDiagnosticsNativeMethods.ProcessEntry32>()
            };

            if (!ProcessDiagnosticsNativeMethods.Process32First(snapshot, ref entry))
            {
                return false;
            }

            do
            {
                if (entry.th32ProcessID == (uint)processId)
                {
                    parentProcessId = entry.th32ParentProcessID;
                    return true;
                }
            }
            while (ProcessDiagnosticsNativeMethods.Process32Next(snapshot, ref entry));

            return false;
        }
        finally
        {
            ProcessDiagnosticsNativeMethods.CloseHandle(snapshot);
        }
    }

    private static string? GetAppCompatLayerValue(RegistryKey? rootKey, string exePath)
    {
        if (rootKey is null)
        {
            return null;
        }

        try
        {
            using var key = rootKey.OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers");
            if (key?.GetValue(exePath) is string value && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        catch (Exception ex)
        {
            return $"error:{ex.Message}";
        }

        return null;
    }

    private static bool TryGetTokenElevation(IntPtr processHandle, out bool isElevated)
    {
        isElevated = false;
        if (!ProcessDiagnosticsNativeMethods.OpenProcessToken(processHandle, TokenQuery, out IntPtr tokenHandle))
        {
            return false;
        }

        try
        {
            int length = Marshal.SizeOf<ProcessDiagnosticsNativeMethods.TokenElevation>();
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!ProcessDiagnosticsNativeMethods.GetTokenInformation(tokenHandle, ProcessDiagnosticsNativeMethods.TokenInformationClass.TokenElevation, buffer, length, out _))
                {
                    return false;
                }

                var elevation = Marshal.PtrToStructure<ProcessDiagnosticsNativeMethods.TokenElevation>(buffer);
                isElevated = elevation.TokenIsElevated != 0;
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            ProcessDiagnosticsNativeMethods.CloseHandle(tokenHandle);
        }
    }

    private static bool TryGetIntegrityLevel(IntPtr processHandle, out string level)
    {
        level = string.Empty;
        if (!ProcessDiagnosticsNativeMethods.OpenProcessToken(processHandle, TokenQuery, out IntPtr tokenHandle))
        {
            return false;
        }

        try
        {
            _ = ProcessDiagnosticsNativeMethods.GetTokenInformation(tokenHandle, ProcessDiagnosticsNativeMethods.TokenInformationClass.TokenIntegrityLevel, IntPtr.Zero, 0, out int length);
            if (length <= 0)
            {
                return false;
            }

            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!ProcessDiagnosticsNativeMethods.GetTokenInformation(tokenHandle, ProcessDiagnosticsNativeMethods.TokenInformationClass.TokenIntegrityLevel, buffer, length, out _))
                {
                    return false;
                }

                var label = Marshal.PtrToStructure<ProcessDiagnosticsNativeMethods.TokenMandatoryLabel>(buffer);
                IntPtr subAuthorityCount = ProcessDiagnosticsNativeMethods.GetSidSubAuthorityCount(label.Label.Sid);
                if (subAuthorityCount == IntPtr.Zero)
                {
                    return false;
                }

                byte count = Marshal.ReadByte(subAuthorityCount);
                if (count == 0)
                {
                    return false;
                }

                IntPtr integrityRidPointer = ProcessDiagnosticsNativeMethods.GetSidSubAuthority(label.Label.Sid, (uint)(count - 1));
                int integrityRid = Marshal.ReadInt32(integrityRidPointer);
                level = FormatIntegrityLevel(integrityRid);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            ProcessDiagnosticsNativeMethods.CloseHandle(tokenHandle);
        }
    }

    private static string FormatIntegrityLevel(int integrityRid)
    {
        return integrityRid switch
        {
            < SecurityMandatoryLowRid => $"Untrusted(0x{integrityRid:X})",
            < SecurityMandatoryMediumRid => $"Low(0x{integrityRid:X})",
            < SecurityMandatoryHighRid => $"Medium(0x{integrityRid:X})",
            < SecurityMandatorySystemRid => $"High(0x{integrityRid:X})",
            < SecurityMandatoryProtectedProcessRid => $"System(0x{integrityRid:X})",
            _ => $"Protected(0x{integrityRid:X})"
        };
    }

    private const uint TokenQuery = 0x0008;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int SecurityMandatoryLowRid = 0x1000;
    private const int SecurityMandatoryMediumRid = 0x2000;
    private const int SecurityMandatoryHighRid = 0x3000;
    private const int SecurityMandatorySystemRid = 0x4000;
    private const int SecurityMandatoryProtectedProcessRid = 0x5000;

    // Process/token diagnostics entry points and their marshaling
    // structures live in DeskPane.Platform.ProcessDiagnosticsNativeMethods.

    public bool IsDeskPaneWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        Win32Helper.GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == (uint)Environment.ProcessId)
        {
            return true;
        }

        IntPtr rootHwnd = Win32Helper.GetAncestor(hwnd, Win32Helper.GA_ROOT);
        if (rootHwnd == IntPtr.Zero)
        {
            rootHwnd = hwnd;
        }

        if (_trayWindow is not null && rootHwnd == WindowNative.GetWindowHandle(_trayWindow))
        {
            return true;
        }

        return WidgetManager?.IsWidgetWindow(rootHwnd) == true;
    }

    public static void Log(string msg)
    {
        try
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}";
            if (Interlocked.Increment(ref s_pendingLogLineCount) > MaxQueuedLogLines)
            {
                Interlocked.Decrement(ref s_pendingLogLineCount);
                return;
            }

            s_logQueue.Enqueue(line);
            EnsureLogWorkerStarted();
            s_logSignal.Release();
        }
        catch
        {
        }
    }

    public static void LogVerbose(string msg)
    {
        if (!EnableVerboseLogging)
        {
            return;
        }

        Log(msg);
    }

    /// <summary>
    /// Safely execute an async action from an event handler, catching and logging any exceptions.
    /// Use this instead of async void to prevent unhandled exceptions from crashing the app.
    /// </summary>
    public static async void SafeFireAndForget(Func<Task> action, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Log($"[SafeFireAndForget] Unhandled exception in {caller}: {ex}");
        }
    }

    private static void EnsureLogWorkerStarted()
    {
        if (Interlocked.CompareExchange(ref s_logWorkerStarted, 1, 0) == 0)
        {
            _ = Task.Run(ProcessLogQueueAsync);
        }
    }

    private static async Task ProcessLogQueueAsync()
    {
        while (true)
        {
            await s_logSignal.WaitAsync().ConfigureAwait(false);
            DrainLogQueue();
        }
    }

    private static void DrainLogQueue()
    {
        var builder = new System.Text.StringBuilder();
        while (s_logQueue.TryDequeue(out string? line))
        {
            Interlocked.Decrement(ref s_pendingLogLineCount);
            builder.Append(line);
        }

        if (builder.Length == 0)
        {
            return;
        }

        try
        {
            EnsureLogDirectory();
            TryRotateLogFileIfNeeded();
            File.AppendAllText(LogPath, builder.ToString());
        }
        catch
        {
        }
    }

    private static void TryRotateLogFileIfNeeded()
    {
        try
        {
            if (!File.Exists(LogPath))
            {
                return;
            }

            var info = new FileInfo(LogPath);
            if (info.Length < MaxLogFileSizeBytes)
            {
                return;
            }

            // Rotate: current → .1, old .1 is deleted
            string rotatedPath = LogPath + ".1";
            if (File.Exists(rotatedPath))
            {
                File.Delete(rotatedPath);
            }

            File.Move(LogPath, rotatedPath);
        }
        catch
        {
            // If rotation fails (e.g., file locked), continue appending to the current file.
        }
    }

    private static void EnsureLogDirectory()
    {
        if (Volatile.Read(ref s_logDirectoryEnsured) != 0)
        {
            return;
        }

        string? dir = Path.GetDirectoryName(LogPath);
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        Volatile.Write(ref s_logDirectoryEnsured, 1);
    }

    private static bool IsEnabledEnvironmentValue(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Trim() is "1" or "true" or "TRUE" or "yes" or "YES" or "on" or "ON";
    }

    private static bool IsStartupTaskActivation()
    {
        if (!AppDistributionService.Current.IsPackaged)
            return false;
        try
        {
            // The platform API does not enter Windows App SDK notification
            // deserialization. Startup detection must also work if notifications
            // cannot be registered on this machine.
            return Windows.ApplicationModel.AppInstance.GetActivatedEventArgs()?.Kind ==
                   Windows.ApplicationModel.Activation.ActivationKind.StartupTask;
        }
        catch (Exception ex)
        {
            Log($"Failed to inspect startup-task activation: {ex.Message}");
            return false;
        }
    }

    internal static int? TryGetAwaitParentExitProcessId(IReadOnlyList<string> arguments)
    {
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index].Trim().Trim('"');
            if (string.Equals(argument, AwaitParentExitArgument, StringComparison.OrdinalIgnoreCase) &&
                index + 1 < arguments.Count &&
                int.TryParse(arguments[index + 1].Trim().Trim('"'), out int processId) &&
                processId > 0)
            {
                return processId;
            }

            string prefix = AwaitParentExitArgument + "=";
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(argument[prefix.Length..], out int inlineProcessId) &&
                inlineProcessId > 0)
            {
                return inlineProcessId;
            }
        }

        return null;
    }

    private static void WaitForRelaunchParentExit(IReadOnlyList<string> arguments)
    {
        if (TryGetAwaitParentExitProcessId(arguments) is not int parentProcessId)
        {
            return;
        }

        try
        {
            using Process parent = Process.GetProcessById(parentProcessId);
            Log($"[Elevation] Relaunched unelevated; waiting for elevated pid={parentProcessId} to exit");
            if (!parent.WaitForExit(AwaitParentExitTimeoutMs))
            {
                Log($"[Elevation] Timed out waiting for pid={parentProcessId}; continuing startup");
            }
            else
            {
                Log($"[Elevation] Elevated pid={parentProcessId} exited; proceeding with single-instance claim");
            }
        }
        catch (ArgumentException)
        {
            // The elevated instance already exited before we could look at it.
            Log($"[Elevation] Elevated pid={parentProcessId} already exited");
        }
        catch (Exception ex)
        {
            Log($"[Elevation] Parent wait failed for pid={parentProcessId}: {ex.Message}");
        }
    }

    private bool _isLaunched;

    private void ScheduleShellContextMenuPrewarm()
    {
        if (!SettingsService.Settings.FileItemSystemContextMenuEnabled)
        {
            return;
        }

        // Warm the native context-menu server in the background once startup
        // work settles, so the first right-click in a widget does not pay the
        // cold Shell handler-loading cost.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                ShellContextMenuProxy.Prewarm();
            }
            catch (Exception ex)
            {
                Log($"[ShellContextMenu] Prewarm failed: {ex.Message}");
            }
        });
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (_isLaunched)
        {
            Log("OnLaunched skipped: already launched");
            return;
        }
        _isLaunched = true;

        bool startupTaskActivation = IsStartupTaskActivation();
        bool isStartupLaunch = StartupLaunchPolicy.IsStartupLaunch(
            Environment.GetCommandLineArgs(),
            args.Arguments,
            startupTaskActivation);
        // Set before the watchdog arms: a task-triggered launch has no user
        // watching, so its fatal path must exit quietly and let the task's
        // restart policy retry instead of parking a modal dialog on an
        // unattended desktop while still holding the single-instance mutex.
        s_startupLaunchQuietExit = isStartupLaunch;
        using var perfScope = PerformanceLogger.Measure(
            "App.OnLaunched",
            $"startup={isStartupLaunch}");
        Log("OnLaunched start");
        // Armed before the try: a startup that stalls (rather than throwing)
        // would otherwise hold the single-instance mutex with no UI at all.
        StartStartupWatchdog();

        try
        {
            IsStartupMode = _processStartupLaunchDetected || isStartupLaunch;
            RunCriticalStartupStep("dispatcher-init", () =>
            {
                UiDispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                DeskPane.Platform.UiDispatch.MarkReady(UiDispatcherQueue);
                WidgetSegmentedLayoutHelper.Initialize(UiDispatcherQueue);
            });

            // The diagnostic report walks a snapshot of every process on the
            // machine plus several registry hives; only run it on elevated
            // launches or when verbose logging is explicitly enabled.
            if (IsVerboseLoggingEnabled || _startupElevationAssessment.IsCurrentProcessElevated)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        Log($"Process integrity {GetProcessIntegrityReport()} pid={Environment.ProcessId} processPath={Environment.ProcessPath ?? "unknown"} baseDir={AppContext.BaseDirectory}");
                        Log($"Process parent {GetParentProcessReport()} commandLine={Environment.CommandLine}");
                        Log($"UAC {GetUacPolicyReport()}");
                        Log($"AppCompat {GetAppCompatReport()}");
                    }
                    catch (Exception ex)
                    {
                        Log($"Startup diagnostics failed: {ex.Message}");
                    }
                });
            }

            // A prepared restore is applied before any service reads or normalizes app data.
            // From here to the widget-restoration phase every step is a feature,
            // not a lifeline: a failure must leave a usable app instead of
            // blocking startup entirely.
            DeskPaneRestoreApplyResult restoreResult = DeskPaneRestoreApplyResult.NoPendingRestore;
            await RunOptionalStartupStepAsync(
                "apply-pending-restore",
                async () => restoreResult = await DataBackupService.ApplyPendingRestoreAsync());
            if (restoreResult.Kind == DeskPaneRestoreApplyKind.Full && restoreResult.Succeeded)
            {
                // A completed restore answers the wipe-recovery question by
                // definition; a marker carried in through an archive written
                // before the exclusion existed must not re-offer it.
                await RecoveryOfferStore.ClearAsync();
            }
            bool hadSettingsBeforeStartup = File.Exists(Path.Combine(
                DeskPaneDataPathService.Current.DataDirectory,
                "settings.json"));

            // Capture the previous session's data before any startup normalization
            // writes. Settings are not loaded yet, so the backup schedule and folder
            // are read from the raw settings file; unreadable values fall back to
            // the defaults, matching the pre-setting behavior.
            RunOptionalStartupStep("read-backup-options", () =>
                DataBackupService.UpdateAutomaticBackupOptions(
                    DataBackupSettingsPolicy.ReadStartupOptions(
                        Path.Combine(DeskPaneDataPathService.Current.DataDirectory, "settings.json"))));
            MarkStartupProgress();
            Task settingsLoadTask = RunCriticalStartupStepAsync(
                "settings-load",
                () => SettingsService.LoadAsync());
            Task startupAutomaticSnapshotTask = Task.CompletedTask;
            await settingsLoadTask;
            PostRestoreRecoverySummary postRestoreRecovery = PostRestoreRecoverySummary.None;
            await RunOptionalStartupStepAsync(
                "post-restore-recovery",
                async () => postRestoreRecovery = await RecoverMachineLocalStateAfterRestoreAsync(restoreResult));
            SettingsService.SettingsChanged += OnMaterialCapabilitySettingsChanged;
            string requestedCornerPreference = SettingsService.Settings.WidgetCornerPreference;
            string effectiveCornerPreference =
                WindowsCompatibilityService.ResolveEffectiveWidgetCornerPreference(
                    requestedCornerPreference);
            Log(
                $"[Appearance] OS build={WindowsCompatibilityService.OsBuild}, " +
                $"requested corners={requestedCornerPreference}, " +
                $"effective corners={effectiveCornerPreference}");

            // Sync widget move/resize snap settings.
            RunOptionalStartupStep("resize-guide-settings", () =>
            {
                ResizeGuideOverlay.IsSnapEnabled = SettingsService.Settings.ResizeSnapEnabled;
                ResizeGuideOverlay.SnapSpacingDips = SettingsService.Settings.WidgetSnapSpacing;
            });

            // Phase 2: Initialize services that depend on settings (parallel)
            RunCriticalStartupStep("core-services", () =>
            {
                ThemeService = Services.GetRequiredService<ThemeService>();
                LocalizationService = Services.GetRequiredService<LocalizationService>();
                LocalizationService.LanguageChanged += OnLanguageChanged;
            });

            var themeService = ThemeService;
            var localizationService = LocalizationService;

            // Parallel: theme refresh only. Clipboard event subscription must stay on the UI thread.
            // RunAsync keeps the refresh on the UI thread (inline while the
            // dispatcher is idle here, enqueued once startup starts awaiting);
            // the old Task.Run hop made ThemeService's off-thread tripwire
            // fire on every single launch.
            Task themeTask = RunOptionalStartupStepAsync(
                "theme-refresh",
                () => DeskPane.Platform.UiDispatch.RunAsync(() =>
                {
                    themeService.RefreshAppearance();
                    return Task.CompletedTask;
                }));

            // Parallel: independent UI setup. The tray is the lifeline: once its
            // icon is up the user can act on the process again.
            MarkStartupProgress();
            RunCriticalStartupStep("tray-icon", CreateTrayIcon);
            RunOptionalStartupStep("lifecycle-recovery-watcher", InitializeLifecycleRecoveryWatcher);

            await themeTask;

            RunCriticalStartupStep("widget-manager", () =>
            {
                WidgetManager = new WidgetManager(SettingsService, FileService, OrganizerService,
                    themeService, localizationService);
                WidgetManager.TrayLayerStateChanged += UpdateTrayLayerStateText;
                // Lets a quick-reveal raise promote already-open DeskPane surfaces
                // (settings, desktop organization) above the raised
                // widget group; the reverse order is handled per-window at show.
                WidgetManager.AuxiliaryWindowProvider = GetRaisedBandAuxiliaryWindowHandles;
            });
            MarkStartupProgress();
            // Non-null past this point: the critical step above rethrows on
            // failure, so the launch never reaches here without a manager.
            WidgetManager widgetManager = WidgetManager!;
            RunOptionalStartupStep("desktop-double-click-activation", () =>
            {
                SettingsService.Settings.DesktopDoubleClickEnabled = true;
                DesktopDoubleClickActivationService = new DesktopDoubleClickActivationService(
                    SettingsService,
                    ToggleWidgetsFromDesktopDoubleClickAsync);
                DesktopDoubleClickActivationService.RefreshRegistration();
            });
            RunOptionalStartupStep("display-topology-coordinator", () =>
            {
                _displayTopologyTransitionCoordinator = new DisplayTopologyTransitionCoordinator(
                    UiDispatcherQueue,
                    DisplayAreaWatcherService.CaptureCurrentSignature,
                    async (generation, reasons) =>
                        WidgetManager is null ||
                        await WidgetManager.RestoreWidgetPositionsAsync(generation, reasons));
                StartTopologyGatePolling();
                if (WidgetManager is not null)
                {
                    WidgetManager.EndRemovalGraceByUserAction = EndDisplayRemovalGraceByUserAction;
                    WidgetManager.NotifyScreenHomeChangedAction = NotifyScreenHomeChanged;
                    WidgetManager.SessionManager.InteractionActiveChanged += () =>
                    {
                        var g = _displayTopologyTransitionCoordinator?.Gate;
                        if (g is null)
                        {
                            return;
                        }

                        if (WidgetManager.SessionManager.IsInteractionActive)
                        {
                            g.Close(DisplayTopologyGateReason.UserInteraction);
                        }
                        else
                        {
                            g.Open(DisplayTopologyGateReason.UserInteraction);
                        }
                    };
                }
            });

            // Startup settling (spec 5.6): bounded wait for missing displays
            // before the widget restore phase runs.
            await RunOptionalStartupStepAsync(
                "display-startup-settling",
                WaitForStartupDisplaySettlingAsync);

            // Phase 3: Restore widgets (the startup snapshot must finish
            // before the restoration phase starts writing normalized state).
            await startupAutomaticSnapshotTask;
            if (IsShuttingDown) return;
            int recoveredDesktopItems = 0;
            if (SuppressFileMutatingStartup)
            {
                // The interrupted-transaction journal is durable; the next
                // healthy launch replays it. Replaying desktop file moves
                // into a launch storm is how interrupted operations pile up.
                Log(
                    "[Startup] Storm guard: deferring desktop-organization recovery " +
                    "until a launch that follows a clean exit.");
            }
            else
            {
                await RunOptionalStartupStepAsync(
                    "desktop-organization-recovery",
                    async () => recoveredDesktopItems = await new DesktopOrganizationTransaction(
                        SettingsService,
                        FileService,
                        widgetManager: WidgetManager).RecoverPendingAsync());
                if (recoveredDesktopItems > 0)
                {
                    Log($"[DesktopOrganization] Recovered {recoveredDesktopItems} items from an interrupted transaction.");
                }
            }
            // A detached storage drive must not abort widget restoration.
            bool managedStorageRootUnavailable = false;
            RunOptionalStartupStep("storage-folder-entries", () =>
                managedStorageRootUnavailable = !widgetManager.SyncStorageFolderEntries());
            Task<bool>? startupDesktopLayerReadinessTask = null;
            if (IsStartupMode)
            {
                RunOptionalStartupStep("desktop-layer-deferral", () =>
                {
                    WidgetLayerService.BeginStartupDesktopLayerAttachmentDeferral();
                    startupDesktopLayerReadinessTask =
                        WidgetLayerService.WaitForDesktopIconViewReadyAsync();
                });
            }

            try
            {
                await widgetManager.RestoreWidgetsAsync();
            }
            catch (Exception ex)
            {
                // Restoration is not the lifeline: the tray icon still gives the
                // user a way back in, so a failure here degrades instead of
                // blocking startup.
                Log($"[Startup] Optional step 'restore-widgets' failed: {ex}");
                RecordStartupDegradation("restore-widgets", ex.ToString());
                if (startupDesktopLayerReadinessTask is not null)
                {
                    startupDesktopLayerReadinessTask = null;
                    WidgetLayerService.EndStartupDesktopLayerAttachmentDeferral();
                }
            }

            if (startupDesktopLayerReadinessTask is not null)
            {
                SafeFireAndForget(
                    () => CompleteStartupDesktopLayerInitializationAsync(
                        startupDesktopLayerReadinessTask,
                        widgetManager),
                    "startup-desktop-layer");
            }

            RunOptionalStartupStep("global-hotkey-service", () => InitializeGlobalHotkeyService(localizationService));
            RunOptionalStartupStep("shell-context-menu-prewarm", ScheduleShellContextMenuPrewarm);

            RunOptionalStartupStep("native-notifications", StartNativeNotificationService);
            await RunOptionalStartupStepAsync(
                "external-activation-initialization",
                CompleteExternalActivationInitializationAsync);
            RunOptionalStartupStep("restore-result-notification", () =>
                ShowDataRestoreResultNotification(restoreResult, postRestoreRecovery));
            RunOptionalStartupStep("settings-recovery-notification", ShowSettingsLoadRecoveryNotification);
            if (managedStorageRootUnavailable)
            {
                RunOptionalStartupStep("storage-unavailable-notification", ShowManagedStorageUnavailableNotification);
            }
            if (IsWipeRecoveryEligible &&
                (!hadSettingsBeforeStartup ||
                 SettingsService.LastLoadRecoveryState == SettingsLoadRecoveryState.DefaultsAfterFailure))
            {
                await RunOptionalStartupStepAsync("recovery-snapshot-notification", async () =>
                {
                    RecoverySnapshotCandidate? wipeRecoveryCandidate = null;
                    try
                    {
                        wipeRecoveryCandidate = await DataBackupService
                            .GetLatestValidRecoverySnapshotAsync(_startupProcessStartUtc);
                    }
                    catch (Exception ex)
                    {
                        // The wipe is only detectable on this single launch; a
                        // transient enumeration failure must not escalate into
                        // a lost offer — fall through to the legacy toast and
                        // leave the settings-page restore entry as the net.
                        Log($"[RecoveryOffer] Candidate detection failed: {ex.Message}");
                    }

                    if (wipeRecoveryCandidate is not null)
                    {
                        // The toast is one-shot and easy to miss, and the next
                        // boot loads the just-written defaults as Primary — the
                        // wipe stops being detectable. The marker keeps the
                        // offer alive until the user answers it.
                        await RecoveryOfferStore.WriteAsync(new RecoveryOfferMarker(
                            wipeRecoveryCandidate.Path,
                            wipeRecoveryCandidate.CreatedAtUtc,
                            wipeRecoveryCandidate.AppVersion,
                            wipeRecoveryCandidate.WidgetCount,
                            DateTimeOffset.UtcNow));
                        Log(
                            "[RecoveryOffer] Configuration wipe detected with a surviving recovery " +
                            $"snapshot ('{wipeRecoveryCandidate.Path}', {wipeRecoveryCandidate.WidgetCount} " +
                            "widgets); offer armed for the next interactive activation.");
                    }

                    ShowRecoverySnapshotAvailableNotification(
                        wipeRecoveryCandidate is null
                            ? await DataBackupService.GetLatestRecoverySnapshotAsync()
                            : ToRecoverySnapshotInfo(wipeRecoveryCandidate));
                });
            }

            // The recovery offer precedes the first-run flows it gates: an
            // interactive launch must not walk a wipe victim through onboarding
            // and a default widget before the restore question is answered.
            // Unattended launches defer the whole block — the offer surfaces on
            // the first external activation instead.
            RecoveryOfferOutcome recoveryOfferOutcome = RecoveryOfferOutcome.NotOffered;
            bool recoveryDecisionPending = false;
            if (!IsStartupMode && !IsShuttingDown)
            {
                // The offer window blocks the pipeline on a human decision:
                // keep feeding the startup watchdog while it is open so an
                // unanswered question on a tray-less machine cannot become a
                // "startup failed" fatal box — the surface step that marks
                // the lifeline runs only after this await.
                Task<RecoveryOfferOutcome> offerTask = ShowRecoveryOfferIfNeededAsync();
                while (!offerTask.IsCompleted)
                {
                    MarkStartupProgress();
                    await Task.WhenAny(offerTask, Task.Delay(TimeSpan.FromSeconds(30)));
                }

                recoveryOfferOutcome = await offerTask;
                if (recoveryOfferOutcome == RecoveryOfferOutcome.RestoreScheduled)
                {
                    return;
                }

                // A Dismissed return from an in-flight call means "the offer
                // is being asked elsewhere", not "declined": the activation
                // path defers the first-run flows while the window is open,
                // and the pipeline must honor the same contract or it walks
                // a wipe victim through onboarding under the open question.
                recoveryDecisionPending =
                    _recoveryOfferWindow is not null || _recoveryOfferInFlight;
            }

            // A restore may also have been scheduled from an activation that
            // ran during the startup's own activation initialization; either
            // way the process is shutting down and first-run flows must not
            // run inside it.
            if (IsShuttingDown)
            {
                return;
            }

            if (!recoveryDecisionPending)
            {
                await RunOptionalStartupStepAsync(
                    "initial-file-widget-setup",
                    () => EnsureInitialFileWidgetSetupAsync(
                        isInteractiveLaunch: !IsStartupMode,
                        allowDefaultAfterRecoveryFailure: recoveryOfferOutcome == RecoveryOfferOutcome.FreshStart));
            }
            await RunOptionalStartupStepAsync(
                "managed-storage-desktop-shortcut",
                () => ManagedStorageDesktopShortcutService.SyncAsync());

            RunOptionalStartupStep("desktop-auto-organization-watcher", () =>
            {
                DesktopAutoOrganizationWatcher = new DesktopAutoOrganizationWatcher(
                    SettingsService,
                    OrganizerService,
                    widgetManager);
                DesktopAutoOrganizationWatcher.ItemOrganized += ShowDesktopAutoOrganizationNotification;
                DesktopAutoOrganizationWatcher.AutoOrganizationTargetUnavailable +=
                    ShowDesktopAutoOrganizationTargetUnavailableNotification;
                if (SuppressFileMutatingStartup)
                {
                    // Constructed now (event wiring intact) but held idle for
                    // the suppression window: FileSystemWatcher stays off and
                    // no organize pass can trigger while the storm lasts.
                    var deferredStart = UiDispatcherQueue.CreateTimer();
                    deferredStart.Interval = StartupStormPolicy.SuppressionWindow;
                    deferredStart.Tick += (_, _) =>
                    {
                        deferredStart.Stop();
                        DesktopAutoOrganizationWatcher?.Start();
                    };
                    deferredStart.Start();
                    Log(
                        "[Startup] Storm guard: desktop auto-organization watcher start deferred " +
                        $"{StartupStormPolicy.SuppressionWindow.TotalMinutes:F0} minutes.");
                    return;
                }

                DesktopAutoOrganizationWatcher.Start();
            });

            // The availability probe can block on offline devices for the
            // network timeout, so it runs after startup on a pool thread —
            // never inside the settings load/save normalize passes.
            // Deep review R15 (F1 audit): this step used to be AWAITED by
            // the pipeline, so an offline mapped drive stalled every later
            // step (display watcher, onboarding, surface ensure) for the
            // SMB timeout, and could trip the 90s startup watchdog into the
            // unattended-exit loop. The probe is advisory only — truly fire
            // and forget, matching the comment above.
            RunOptionalStartupStep(
                "desktop-organization-target-availability",
                () =>
                {
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            DesktopOrganizationTargetAvailabilityAdvisor.WarnIfUnavailableTargets(
                                SettingsService,
                                ShowDesktopAutoOrganizationTargetUnavailableNotification);
                        }
                        catch (Exception ex)
                        {
                            Log($"[Startup] Target availability probe failed: {ex.Message}");
                        }
                    });
                });

            RunOptionalStartupStep("diagnostics-service", () =>
            {
                _diagnosticsService = new AppDiagnosticsService(UiDispatcherQueue);
                _diagnosticsService.StartAll();
            });

            // Start display area watcher for hot-plug detection
            RunOptionalStartupStep("display-area-watcher", () =>
            {
                _displayAreaWatcher = new DisplayAreaWatcherService(UiDispatcherQueue);
                _displayAreaWatcher.DisplaysChanged += OnDisplaysChanged;
                _displayAreaWatcher.Start();
                if (_trayWindow is not null)
                {
                    // The tray window outlives every widget window, so subscribing
                    // there is what retires the fallback poll. This runs after the
                    // recovery watcher's own attach, which is earlier in startup.
                    _displayAreaWatcher.AttachToMessageWindow(
                        WindowNative.GetWindowHandle(_trayWindow));
                }
            });
            RunOptionalStartupStep("virtual-display-advisor", () =>
                VirtualDisplayAdvisor.WarnIfPrimaryDisplayIsVirtual(
                    (titleKey, bodyKey) => ShowSettingsNotification(
                        titleKey,
                        bodyKey,
                        NotificationIcon.Warning)));
            MaterialCapabilityAdvisor.Initialize(OnMaterialCapabilitySettingsChanged);
            RunOptionalStartupStep("material-capability-advisor", () =>
                MaterialCapabilityAdvisor.WarnIfMaterialDegraded(
                    SettingsService.Settings.WidgetShell.WidgetMaterialType,
                    (titleKey, bodyKey) => ShowSettingsNotification(
                        titleKey,
                        bodyKey,
                        NotificationIcon.Warning,
                        "AppearanceMaterialSettings")));

            // Configure taskbar Jump List with quick actions
            SafeFireAndForget(
                () => JumpListService.ConfigureAsync(LocalizationService),
                "jump-list-configure");

            // Handle Jump List activation on first launch (not second instance)
            string? firstLaunchJumpArg =
                JumpListService.TryGetJumpListArgument(args.Arguments) ??
                JumpListService.TryGetJumpListArgument(
                    string.Join(' ', Environment.GetCommandLineArgs()));
            if (firstLaunchJumpArg is not null)
            {
                SafeFireAndForget(
                    () => JumpListService.HandleActivationAsync(firstLaunchJumpArg),
                    "jump-list-activation");
            }

            RunOptionalStartupStep("idle-memory-maintenance", StartVisibleIdleMemoryMaintenance);
            RunOptionalStartupStep("quiescence-working-set-trim", StartQuiescenceWorkingSetTrim);

            // The process must not keep running startup with nothing the user
            // can act on: it would own the single-instance mutex and swallow
            // every later launch.
            await EnsureStartupProducedUsableSurfaceAsync();

            RunOptionalStartupStep("elevation-notice", ShowElevationNoticeIfNeeded);

            EnsureStartupPipeline().WriteSummary();
            Log("OnLaunched completed successfully");
#if DEBUG
            if (Environment.GetEnvironmentVariable("DESKPANE_DEV_DATA_ROOT") is { } probeRoot &&
                probeRoot.Contains("architecture-shutdown-ownership-20260925",
                    StringComparison.OrdinalIgnoreCase) &&
                Environment.GetEnvironmentVariable("DESKPANE_DEV_SHUTDOWN_PROBE") is { } probeStage)
            {
                Log($"[ShutdownProbe] Starting stage={probeStage}");
                _ = ShutdownApplicationAsync();
            }
#endif
            // Startup registration does not gate the first usable widgets.
            // DirectStartupService serializes migration with user toggle changes.
            _ = Task.Run(() =>
            {
                if (StartupService.Current is DirectStartupService directStartupService)
                {
                    directStartupService.TryMigrateLegacyRegistration();
                }

                ApplyDefaultAutoStartOnce();
            });
#if DESKPANE_NATIVE_AOT && DESKPANE_AOT_SMOKE_HARNESS
            StartAotShortcutSmokeIfRequested();
            StartAotShellSmokeIfRequested();
            StartAotQuickAccessMutationSmokeIfRequested();
#endif
        }
        catch (Exception ex)
        {
            // Anything reaching this point failed before the tray lifeline was
            // established, so the process has nothing to offer and must not
            // keep holding the single-instance mutex.
            FailStartup("exception during startup", ex);
        }
    }

    /// <summary>
    /// Applies the one-time autostart default. Runs off the UI thread because
    /// registering the logon task shells out to schtasks; only the settings
    /// write returns to the dispatcher. Development data roots (portable runs,
    /// smoke harnesses) never touch the machine's startup registration.
    /// </summary>
    private void ApplyDefaultAutoStartOnce()
    {
        try
        {
            if (DeskPaneDataPathService.Current.IsDevelopmentRoot ||
                !AutoStartDefaultPolicy.ShouldApply(SettingsService.Settings))
            {
                return;
            }

            StartupRegistrationState initialState = StartupService.Current.GetState();
            StartupRegistrationState effective =
                AutoStartDefaultPolicy.Resolve(StartupService.Current);
            bool enabled = AutoStartDefaultPolicy.IsEnabledState(effective);

            void Commit()
            {
                SettingsService.Settings.AutoStart = enabled;
                if (AutoStartDefaultPolicy.ShouldMarkApplied(effective))
                {
                    SettingsService.Settings.AutoStartDefaultApplied = true;
                }

                SettingsService.SaveDebounced();
            }

            DeskPane.Platform.UiDispatch.RunOrDefer(Commit);

            Log(
                $"[AutoStart] One-time default applied: initial={initialState} effective={effective} mirror={enabled}");
        }
        catch (Exception ex)
        {
            Log($"[AutoStart] One-time default failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Adapts a freshly applied full restore to this machine. The archived
    /// settings describe the source machine: its startup registration and its
    /// user-profile-local paths do not exist here. The startup choice is
    /// re-registered only when the restored setting is "on" and this machine
    /// has no registration of its own (AutoStartDefaultApplied=true keeps the
    /// one-time default out of the way, so the two never race on the task
    /// backend); profile-local paths are rebased from the source user profile
    /// derived off the backup manifest. Scoped cloud restores never touch
    /// these settings and are skipped.
    /// </summary>
    private async Task<PostRestoreRecoverySummary> RecoverMachineLocalStateAfterRestoreAsync(
        DeskPaneRestoreApplyResult restoreResult)
    {
        if (restoreResult.Kind != DeskPaneRestoreApplyKind.Full || !restoreResult.Succeeded)
        {
            return PostRestoreRecoverySummary.None;
        }

        bool autoStartReRegistered = false;
        if (SettingsService.Settings.Core.AutoStart && SettingsService.Settings.Core.AutoStartDefaultApplied)
        {
            // Registration state reads and writes shell out to schtasks (and
            // the packaged channel may prompt for consent), so they stay off
            // the UI thread exactly like the one-time default.
            StartupRegistrationState state = await Task.Run(() => StartupService.Current.GetState());
            if (RestoreAutoStartReconcilePolicy.ShouldRegister(SettingsService.Settings.Core.AutoStart, state))
            {
                StartupOperationResult result = await Task.Run(() => StartupService.SetEnabled(true));
                // Only a live registration backs the notification's assertion:
                // the packaged channel's Pending means the consent dialog is
                // still in flight and the user may decline it.
                autoStartReRegistered = RestoreAutoStartReconcilePolicy.IsRegistered(result.State);
                Log($"[DataBackup] Post-restore autostart reconcile: {state} → {result.State}.");
            }
            else
            {
                Log($"[DataBackup] Post-restore autostart reconcile: state={state}, left as-is.");
            }
        }

        RestorePathRebaseResult rebase = RestoreMachineLocalPathRebase.Rebase(
            SettingsService.Settings,
            restoreResult.SourceDataPath,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        bool localPathsRebased = false;
        if (rebase.Changed)
        {
            if (await SettingsService.SaveCheckedAsync())
            {
                localPathsRebased = true;
                // The rebased root is where file widgets resolve immediately;
                // create it so an empty-but-valid folder is what they see.
                if (rebase.RebasedManagedStorageRootPath is { } storageRoot)
                {
                    try
                    {
                        Directory.CreateDirectory(storageRoot);
                    }
                    catch (Exception ex)
                    {
                        Log($"[DataBackup] Post-restore storage root create failed: {ex.Message}");
                    }
                }

                Log(
                    $"[DataBackup] Post-restore path rebase: settings={rebase.SettingsPathsRebased} " +
                    $"widgets={rebase.WidgetsRebased} sourceProfile='{rebase.SourceProfileRoot}'.");
            }
            else
            {
                // The rebased values live on only in this session's memory;
                // the disk still holds the source machine's paths. The
                // notification must not claim success, and manufacturing the
                // new root directory would paper over the persistence
                // failure the settings surface is already reporting.
                Log("[DataBackup] Post-restore path rebase could not be persisted; " +
                    "disk paths were left unchanged.");
            }
        }

        return new PostRestoreRecoverySummary(autoStartReRegistered, localPathsRebased);
    }

    private async Task CompleteStartupDesktopLayerInitializationAsync(
        Task<bool> readinessTask,
        WidgetManager widgetManager)
    {
        bool explorerDesktopReady = false;
        try
        {
            // Widget startup already has a 2.3-second temporary presentation.
            // Let that finish independently while the read-only Explorer probe runs.
            Task widgetPresentationSettled = Task.Delay(TimeSpan.FromMilliseconds(2400));
            explorerDesktopReady = await readinessTask;
            await widgetPresentationSettled;
        }
        catch (Exception ex)
        {
            Log($"[Startup] Explorer desktop readiness probe failed: {ex}");
        }
        finally
        {
            WidgetLayerService.EndStartupDesktopLayerAttachmentDeferral();
        }

        if (!ReferenceEquals(WidgetManager, widgetManager))
        {
            return;
        }

        Log(
            $"[Startup] Applying deferred widget desktop layer " +
            $"explorerReady={explorerDesktopReady}");
        widgetManager.RefreshVisibleWidgetDesktopLayers(
            "startup-explorer-desktop-ready");
    }

    private void InitializeGlobalHotkeyService(LocalizationService localizationService)
    {
        if (GlobalHotkeyService is null)
        {
            try
            {
                GlobalHotkeyService = new GlobalHotkeyService(
                    SettingsService,
                    localizationService,
                    () => ToggleTrayWidgetsAsync("global-hotkey"));
                Log("[Init] GlobalHotkeyService created after widget restore");
            }
            catch (Exception ex)
            {
                Log($"[Init] GlobalHotkeyService creation failed: {ex}");
            }
        }

        if (GlobalHotkeyService is not { } hotkeyService || _trayWindow is null)
        {
            return;
        }

        try
        {
            IntPtr trayWindowHandle = WindowNative.GetWindowHandle(_trayWindow);
            if (trayWindowHandle != IntPtr.Zero && !hotkeyService.IsRegistered)
            {
                Log(
                    $"[Init] Attaching GlobalHotkeyService after widget restore " +
                    $"hwnd=0x{trayWindowHandle.ToInt64():X}");
                hotkeyService.Attach(trayWindowHandle);
            }
        }
        catch (Exception ex)
        {
            Log($"[Init] GlobalHotkeyService attach failed: {ex}");
        }
    }

    /// <summary>
    /// Called when the set of displays changes (hot-plug, resolution change, etc.).
    /// Invalidates caches and triggers widget repositioning.
    /// </summary>
    private void OnDisplaysChanged()
    {
        try
        {
            Log("[DisplayAreaWatcher] Displays changed, queueing stable widget reposition");

            // Invalidate the desktop icon view cache since work areas may have changed
            WidgetLayerService.InvalidateDesktopIconViewCache();

            ApplyRemovalGrace();
            RequestDisplayTopologyRestore("display-area-watcher");
            VirtualDisplayAdvisor.WarnIfPrimaryDisplayIsVirtual(
                (titleKey, bodyKey) => ShowSettingsNotification(
                    titleKey,
                    bodyKey,
                    NotificationIcon.Warning));

            // TryEnqueue keeps the raise on the UI thread regardless of which
            // thread the watcher fired from.
            UiDispatcherQueue.TryEnqueue(() => DisplayTopologyChanged?.Invoke());
        }
        catch (Exception ex)
        {
            Log($"[DisplayAreaWatcher] OnDisplaysChanged failed: {ex}");
        }
    }

    /// <summary>
    /// Removal grace (spec 5.6): when the new display set only REMOVED
    /// displays (unplug, dock-off, KVM input switch, DP sleep), park the
    /// restore for a grace window instead of instantly reshuffling widgets.
    /// The shared 2s poll advances the grace; the displays returning cancels
    /// it outright.
    /// </summary>
    private void ApplyRemovalGrace()
    {
        if (_displayTopologyTransitionCoordinator?.Gate is not { } gate ||
            SettingsService is null)
        {
            return;
        }

        IReadOnlyList<string> current = DisplayIdentityTokens.CurrentSet();
        if (gate.ClosedReasons.Contains(DisplayTopologyGateReason.RemovalGrace))
        {
            gate.ObserveDisplays(current);
            return;
        }

        IReadOnlyList<string> active = DisplayIdentityTokens.ActiveProfileSet(SettingsService.Settings);
        if (active.Count > 0 && DisplayIdentityTokens.IsTrueSubset(current, active))
        {
            gate.StartRemovalGrace(active, current);
        }
    }

    private DispatcherQueueTimer? _topologyGatePollTimer;

    /// <summary>
    /// Shared 2s poll (spec 5.6): parks restores while a fullscreen app owns
    /// the screen, and advances an open removal grace. Cheap single P/Invoke
    /// per tick.
    /// </summary>
    private void StartTopologyGatePolling()
    {
        if (_topologyGatePollTimer is not null || UiDispatcherQueue is null)
        {
            return;
        }

        var gate = _displayTopologyTransitionCoordinator?.Gate;
        if (gate is null)
        {
            return;
        }

        _topologyGatePollTimer = UiDispatcherQueue.CreateTimer();
        _topologyGatePollTimer.Interval = TimeSpan.FromSeconds(2);
        _topologyGatePollTimer.IsRepeating = true;
        _topologyGatePollTimer.Tick += (_, _) =>
        {
            try
            {
                if (Win32Helper.IsFullscreenAppActive())
                {
                    gate.Close(DisplayTopologyGateReason.FullscreenApp);
                }
                else
                {
                    gate.Open(DisplayTopologyGateReason.FullscreenApp);
                }

                if (gate.ClosedReasons.Contains(DisplayTopologyGateReason.RemovalGrace))
                {
                    gate.ObserveDisplays(DisplayIdentityTokens.CurrentSet());
                }
            }
            catch (Exception ex)
            {
                Log($"[DisplayTopology] Gate poll failed: {ex.Message}");
            }
        };
        _topologyGatePollTimer.Start();
    }

    /// <summary>
    /// Startup settling (spec 5.6): when the displays present at launch are
    /// a subset of the last active arrangement, wait (bounded at 5 s) for the
    /// missing displays before the widget restore runs, so docked setups do
    /// not get the "everything crams onto the laptop, then moves back" dance.
    /// </summary>
    private async Task WaitForStartupDisplaySettlingAsync()
    {
        try
        {
            IReadOnlyList<string> active = DisplayIdentityTokens.ActiveProfileSet(SettingsService.Settings);
            if (active.Count == 0)
            {
                return;
            }

            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTimeOffset.UtcNow < deadline)
            {
                IReadOnlyList<string> current = DisplayIdentityTokens.CurrentSet();
                if (!DisplayIdentityTokens.IsTrueSubset(current, active))
                {
                    break;
                }

                await Task.Delay(500);
            }
        }
        catch (Exception ex)
        {
            Log($"[DisplayTopology] Startup settling failed: {ex.Message}");
        }
    }

    internal void RequestDisplayTopologyRestore(string reason)
    {
        _displayTopologyTransitionCoordinator?.RequestRestore(reason);
    }

    /// <summary>
    /// User reveal / drag / settings-page move ends the removal grace early
    /// (spec 5.6): the parked restore runs now.
    /// </summary>
    internal void EndDisplayRemovalGraceByUserAction()
    {
        _displayTopologyTransitionCoordinator?.Gate.EndGraceByUserAction();
    }

    /// <summary>
    /// Lifecycle gate events (spec 5.6): lock/logoff/remote-disconnect and
    /// display-off park topology restores; their counterparts reopen the
    /// gate (the reopen path re-requests any parked restore itself).
    /// </summary>
    private void OnLifecycleGateEvent(string eventName)
    {
        var gate = _displayTopologyTransitionCoordinator?.Gate;
        if (gate is null)
        {
            return;
        }

        switch (eventName)
        {
            case "session-locked":
                gate.Close(DisplayTopologyGateReason.SessionLocked);
                break;
            case "session-unlocked":
                gate.Open(DisplayTopologyGateReason.SessionLocked);
                break;
            case "display-power-off":
                gate.Close(DisplayTopologyGateReason.DisplayOff);
                break;
            case "display-power-on":
                gate.Open(DisplayTopologyGateReason.DisplayOff);
                break;
        }
    }

    private AppDiagnosticsService? _diagnosticsService;

    private void InitializeLifecycleRecoveryWatcher()
    {
        if (_trayWindow is null)
        {
            return;
        }

        try
        {
            IntPtr trayHwnd = WindowNative.GetWindowHandle(_trayWindow);
            if (trayHwnd != IntPtr.Zero)
            {
                _lifecycleRecoveryWatcher = new AppLifecycleRecoveryWatcher(
                    trayHwnd,
                    UiDispatcherQueue,
                    OnLifecycleRecoveryRequested,
                    FlushSettingsForEndSession,
                    OnLifecycleGateEvent);
            }
        }
        catch (Exception ex)
        {
            Log($"[Lifecycle] Recovery watcher initialization failed: {ex.Message}");
        }

        // Silent hook removal produces no message at all, so liveness needs an
        // active probe rather than another window message.
        if (Environment.GetEnvironmentVariable("DESKPANE_DISABLE_HOOK_WATCHDOG") is null)
        {
            try
            {
                _hookHealthWatchdog = new HookHealthWatchdog(
                    UiDispatcherQueue,
                    static message => Log(message),
                    static message => LogVerbose(message));
                _hookHealthWatchdog.Watch(() => GlobalHotkeyService);
                _hookHealthWatchdog.Watch(() => DesktopDoubleClickActivationService);
                // RegisterHotKey chords have no hook to probe; the watchdog
                // keeps them alive with a periodic unregister+register
                // round-trip instead (feedback 308/371).
                _hookHealthWatchdog.WatchMaintenance(() => GlobalHotkeyService);
            }
            catch (Exception ex)
            {
                Log($"[Lifecycle] Hook watchdog initialization failed: {ex.Message}");
                _hookHealthWatchdog?.Dispose();
                _hookHealthWatchdog = null;
            }
        }
    }

    private void OnLifecycleRecoveryRequested(string reason)
    {
        Log($"[Lifecycle] Recovery signal received: {reason}");
        _diagnosticsService?.RecordLifecycleEvent(reason);
        // S6 instrumentation (silent freeze after explorer restart): the
        // topology-recovery storm re-parents and re-positions every widget.
        // Verbose carries the timing breakdown; the default-level line fires
        // only when the storm ran long enough to read as a freeze, so field
        // logs surface the S6 signature without verbose enabled.
        long recoveryStart = System.Diagnostics.Stopwatch.GetTimestamp();
        WidgetLayerService.InvalidateDesktopIconViewCache();
        _displayAreaWatcher?.RefreshNow();
        RequestDisplayTopologyRestore("lifecycle-" + reason);
        double topologyMs =
            System.Diagnostics.Stopwatch.GetElapsedTime(recoveryStart).TotalMilliseconds;
        App.LogVerbose(
            $"[Lifecycle] Recovery topology phase reason={reason} " +
            $"tookMs={topologyMs:F0}");
        if (topologyMs > 2000)
        {
            Log(
                $"[Lifecycle] Recovery storm ran {topologyMs:F0}ms for " +
                $"{reason}; if the app felt frozen here, capture a dump (S6).");
        }

        bool requiresExternalRecovery =
            reason.Contains("resume", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("session-", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("explorer-restart", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("display-power-on", StringComparison.OrdinalIgnoreCase);
        if (requiresExternalRecovery)
        {
            try
            {
                GlobalHotkeyService?.RefreshRegistration();
                DesktopDoubleClickActivationService?.RefreshRegistration();
            }
            catch (Exception ex)
            {
                Log($"[Lifecycle] Global hotkey recovery failed for {reason}: {ex.Message}");
            }

            ScheduleExternalStateRecovery();
        }
    }

    private void FlushSettingsForEndSession(string reason)
    {
        // The OS ends the process after this flush without the shutdown
        // sequence, so the launch breadcrumb is closed here.
        _startupSessionLedger?.MarkCleanExit();
        // Deep review R15 (F1 audit): QUERYENDSESSION and ENDSESSION both
        // route here, and each pass used to perform an unconditional full
        // settings rewrite (up to 2x3s of UI-thread blocking inside
        // wndproc). With nothing pending the write is pure cost — skip it.
        // (The flush itself still clears the pending flag on failure, so
        // the second phase keeps its own safety net.)
        if (!SettingsService.HasPendingSave)
        {
            Log($"[Lifecycle] No pending settings to flush for {reason}.");
            DrainLogQueue();
            return;
        }

        Log($"[Lifecycle] Flushing settings for {reason}.");
        try
        {
            Task<bool> flushTask = Task.Run(
                () => SettingsService.FlushPendingSaveAsync(notifySubscribers: false));
            if (!flushTask.Wait(TimeSpan.FromSeconds(3)))
            {
                Log($"[Lifecycle] Settings flush timed out for {reason}.");
            }
            else if (!flushTask.Result)
            {
                Log($"[Lifecycle] Settings flush failed for {reason}.");
            }
        }
        catch (Exception ex)
        {
            Log($"[Lifecycle] Settings flush threw for {reason}: {ex}");
        }

        // The log queue is asynchronous; the OS terminates the process the
        // moment this returns, so the tail lines (including the flush
        // result itself) must be pushed out here or they are lost.
        DrainLogQueue();
    }

    private void StartNativeNotificationService()
    {
        EnsureNativeNotificationService();
        HandleCurrentNativeNotificationActivation();
    }

    private void EnsureNativeNotificationService()
    {
        // Keep the constructor's registration and wait handle alive. Replacing
        // it here could lose a notification received before settings/widgets load.
        NativeAppNotificationService service = _nativeNotificationService ??=
            new NativeAppNotificationService(QueueNativeNotificationActivation);
        _nativeNotificationBootstrap ??= new NativeNotificationActivationBootstrap(
            () => service.Register(),
            ReadCurrentNativeNotificationActivation,
            ex => Log($"[Notification] Activation bootstrap unavailable: {ex}"));
    }

    private static void QueueNativeNotificationActivation(NativeAppNotificationActivation activation)
    {
        try
        {
            NativeNotificationActivationEnvelopeWriteResult result =
                PendingNativeNotificationActivationStore.Store(activation);
            Log($"[Notification] Queued activation disposition={result.Disposition} error={result.Error ?? "none"}");
            if (result.Disposition == NativeNotificationActivationEnvelopeWriteDisposition.Stored)
                _activationEvent?.Set();
        }
        catch (Exception ex)
        {
            Log($"[Notification] Activation queue failed: {ex}");
        }
    }

    private void HandleCurrentNativeNotificationActivation()
    {
        if (_initialNotificationHandled)
            return;
        NativeAppNotificationActivation? activation = TryGetCurrentNativeNotificationActivation();
        if (activation is not null)
        {
            _initialNotificationHandled = true;
            QueueNativeNotificationActivation(activation);
        }
    }

    private void HandleNativeNotificationActivation(
        NativeAppNotificationActivation activation)
    {
        if (UiDispatcherQueue is { HasThreadAccess: false } dispatcherQueue)
        {
            dispatcherQueue.TryEnqueue(() => HandleNativeNotificationActivation(activation));
            return;
        }

        App.Log(
            $"[Notification] Native notification activated " +
            $"source={activation.Source} sourcePid={activation.SourceProcessId} " +
            $"envelope={activation.EnvelopeId ?? "none"} args={activation.Arguments}");
        OnNativeNotificationActivationObserved(activation);
        var notificationArguments = ParseNotificationArguments(activation.Arguments);
        if (notificationArguments.TryGetValue("type", out string? notificationType) &&
            string.Equals(
                notificationType,
                "desktop-organization",
                StringComparison.OrdinalIgnoreCase))
        {
            if (notificationArguments.TryGetValue("action", out string? action) &&
                string.Equals(action, "undo", StringComparison.OrdinalIgnoreCase) &&
                notificationArguments.TryGetValue("historyId", out string? historyId))
            {
                _ = UndoDesktopOrganizationFromNotificationAsync(historyId);
                return;
            }

            _ = RaiseTrayWidgetsAsync();
        }
        else if (notificationArguments.TryGetValue("type", out string? settingsNotificationType) &&
                 string.Equals(
                     settingsNotificationType,
                     "settings",
                     StringComparison.OrdinalIgnoreCase))
        {
            if (notificationArguments.TryGetValue("section", out string? settingsSectionTag) &&
                !string.IsNullOrWhiteSpace(settingsSectionTag))
            {
                ShowSettings(settingsSectionTag);
            }
            else
            {
                ShowSettings();
            }
        }
        else
        {
            _ = RaiseTrayWidgetsAsync();
        }
    }

    private void ShowDesktopAutoOrganizationNotification(
        DesktopAutoOrganizationCompleted completed)
    {
        if (UiDispatcherQueue is { HasThreadAccess: false } dispatcherQueue)
        {
            dispatcherQueue.TryEnqueue(() =>
                ShowDesktopAutoOrganizationNotification(completed));
            return;
        }

        _nativeNotificationService?.TryShow(
            LocalizationService.T("DesktopOrganization.Notification.Title"),
            LocalizationService.Format(
                "DesktopOrganization.Notification.Body",
                completed.FileName,
                completed.TargetWidgetName),
            new Dictionary<string, string>
            {
                ["type"] = "desktop-organization",
                ["historyId"] = completed.HistoryId
            },
            [
                new NativeAppNotificationAction(
                    LocalizationService.T("DesktopOrganization.Notification.Undo"),
                    new Dictionary<string, string>
                    {
                        ["type"] = "desktop-organization",
                        ["action"] = "undo",
                        ["historyId"] = completed.HistoryId
                    })
            ],
            options: new NativeAppNotificationOptions(
                Tag: "desktop-auto-organization",
                Group: DesktopOrganizationNotificationGroup));
    }

    /// <summary>
    /// User-visible counterpart of the recoverable-skip policy: a rule whose
    /// target folder is temporarily unreachable keeps the auto-organization
    /// switch on and is skipped at runtime, and this notice makes that skip
    /// visible instead of silent. Shared by the startup availability advisor
    /// and the watcher's move-time unavailability episodes.
    /// </summary>
    private void ShowDesktopAutoOrganizationTargetUnavailableNotification(
        DesktopOrganizationTargetUnavailableSummary summary)
    {
        if (UiDispatcherQueue is { HasThreadAccess: false } dispatcherQueue)
        {
            dispatcherQueue.TryEnqueue(() =>
                ShowDesktopAutoOrganizationTargetUnavailableNotification(summary));
            return;
        }

        if (LocalizationService is null || IsShuttingDown ||
            summary.WidgetNames.Count == 0)
        {
            return;
        }

        string title = LocalizationService.T(
            "DesktopOrganization.Auto.TargetUnavailableTitle");
        string message = summary.WidgetNames.Count == 1
            ? LocalizationService.Format(
                "DesktopOrganization.Auto.TargetUnavailableBody.Widget",
                summary.WidgetNames[0])
            : LocalizationService.Format(
                "DesktopOrganization.Auto.TargetUnavailableBody.Count",
                summary.WidgetNames.Count);
        if (_nativeNotificationService?.TryShow(
                title,
                message,
                new Dictionary<string, string>
                {
                    ["type"] = "settings",
                    ["section"] = "DesktopOrganizationSettings"
                },
                options: new NativeAppNotificationOptions(
                    Tag: DesktopOrganizationTargetUnavailableNotificationTag,
                    Group: DesktopOrganizationNotificationGroup)) == true || _trayIcon is null)
        {
            return;
        }

        try
        {
            _trayIcon.ShowNotification(
                title,
                message,
                NotificationIcon.Warning,
                customIconHandle: null,
                largeIcon: false,
                sound: false,
                respectQuietTime: true,
                realtime: false,
                timeout: TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            Log($"[DesktopAutoOrganization] Target-unavailable notification failed: {ex.Message}");
        }
    }

    private async Task UndoDesktopOrganizationFromNotificationAsync(string historyId)
    {
        try
        {
            OrganizationHistoryEntry? history = SettingsService.OrganizationHistory.Entries
                .FirstOrDefault(entry =>
                    string.Equals(entry.Id, historyId, StringComparison.Ordinal));
            await OrganizerService.UndoAsync(historyId);
            if (WidgetManager is not null && history is not null)
            {
                foreach (string widgetId in history.Items
                             .Select(item => item.TargetWidgetId)
                             .Where(id => !string.IsNullOrWhiteSpace(id))
                             .Distinct(StringComparer.Ordinal))
                {
                    await WidgetManager.RefreshFileWidgetAsync(widgetId);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[DesktopAutoOrganization] Notification undo failed: {ex}");
        }
    }

    private static Dictionary<string, string> ParseNotificationArguments(string arguments)
    {
        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return parsed;
        }

        foreach (var pair in arguments.Split(
                     ['&', ';'],
                     StringSplitOptions.RemoveEmptyEntries |
                     StringSplitOptions.TrimEntries))
        {
            int separatorIndex = pair.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            string key = Uri.UnescapeDataString(pair[..separatorIndex]);
            string value = Uri.UnescapeDataString(pair[(separatorIndex + 1)..]);
            if (!string.IsNullOrWhiteSpace(key))
            {
                parsed[key] = value;
            }
        }

        return parsed;
    }

    private NativeAppNotificationActivation? TryGetCurrentNativeNotificationActivation()
    {
        return _nativeNotificationBootstrap?.Capture();
    }

    private NativeAppNotificationActivation? ReadCurrentNativeNotificationActivation() =>
        _nativeNotificationService?.ReadCurrentActivation();

    private static void StorePendingJumpListArgument(string argument)
    {
        try
        {
            Directory.CreateDirectory(DeskPaneDataPathService.Current.RootPath);
            File.WriteAllText(PendingJumpListArgumentPath, argument);
            Log($"[JumpList] Forwarded jump list activation to running instance arg={argument}");
        }
        catch (Exception ex)
        {
            Log($"[JumpList] Failed to forward jump list activation: {ex}");
        }
    }

    private static string? TakePendingJumpListArgument()
    {
        try
        {
            if (!File.Exists(PendingJumpListArgumentPath))
            {
                return null;
            }

            string argument = File.ReadAllText(PendingJumpListArgumentPath);
            File.Delete(PendingJumpListArgumentPath);
            return string.IsNullOrWhiteSpace(argument) ? null : argument;
        }
        catch (Exception ex)
        {
            Log($"[JumpList] Failed to read forwarded jump list activation: {ex}");
            return null;
        }
    }

    private void RegisterActivationListener()
    {
        if (_activationEvent is null || _activationRegistration is not null)
        {
            return;
        }

        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            static (_, _) =>
            {
                App.UiDispatcherQueue?.TryEnqueue(() =>
                {
                    SafeFireAndForget(
                        () => Current.HandleExternalActivationAsync(),
                        "external-activation");
                });
            },
            null,
            Timeout.Infinite,
            false);
    }

    private async Task CompleteExternalActivationInitializationAsync()
    {
        _externalActivationReady = true;

        // The named auto-reset event retains a signal while startup is still
        // restoring services. Consume that signal on the UI thread before
        // registering the long-lived wait, then drain every queued envelope.
        bool startupSignalPending = false;
        try
        {
            startupSignalPending = _activationEvent?.WaitOne(0) == true;
        }
        catch (Exception ex)
        {
            Log($"[Activation] Failed to inspect the startup activation event: {ex.Message}");
        }

        try
        {
            if (startupSignalPending)
            {
                await HandleExternalActivationAsync();
            }
            else
            {
                DrainPendingNativeNotificationActivations();
                // Deep review R15 (F1 audit): a secondary instance writes the
                // pending jump-list/update-result file and then sets the
                // event; if the primary died in that window the signal died
                // with it and the stale file survived. Without consuming it
                // here, the next bare activation hours later replayed the
                // stale argument (an unexpected toggle/open-storage).
                // Running the same handler drains those files harmlessly —
                // no event means no envelope work, just the file take.
                await HandleExternalActivationAsync();
            }
        }
        finally
        {
            RegisterActivationListener();
        }
    }

    private async Task HandleExternalActivationAsync()
    {
        // An activation racing shutdown would re-show windows the teardown
        // sequence just closed (it runs after the settings-window step has
        // already nulled its references).
        if (IsShuttingDown)
        {
            Log("HandleExternalActivationAsync skipped: shutdown in progress");
            return;
        }

        if (!_externalActivationReady)
        {
            _externalActivationRequestedWhileBusy = true;
            Log("HandleExternalActivationAsync deferred until services are ready");
            return;
        }

        if (_externalActivationHandling)
        {
            _externalActivationRequestedWhileBusy = true;
            return;
        }

        _externalActivationHandling = true;
        Log("HandleExternalActivationAsync invoked");
        try
        {
            do
            {
                _externalActivationRequestedWhileBusy = false;
                ScheduleExternalStateRecovery();

                bool activationHandled = DrainPendingNativeNotificationActivations();
                string? jumpListArgument = TakePendingJumpListArgument();
                if (!string.IsNullOrWhiteSpace(jumpListArgument))
                {
                    await JumpListService.HandleActivationAsync(jumpListArgument);
                    activationHandled = true;
                }

                if (activationHandled)
                {
                    continue;
                }

                DateTimeOffset activationAtUtc = DateTimeOffset.UtcNow;
                bool settingsWindowOpen = false;
                bool coalesceBareActivation =
                    ExternalActivationPolicy.ShouldCoalesceBareActivation(
                        _lastBareExternalActivationAtUtc,
                        activationAtUtc,
                        settingsWindowOpen);
                _lastBareExternalActivationAtUtc = activationAtUtc;
                if (coalesceBareActivation)
                {
                    Log(
                        "[Activation] Coalesced duplicate bare activation " +
                        $"windowMs={ExternalActivationPolicy.BareActivationDuplicateWindow.TotalMilliseconds:F0} " +
                        "settingsOpen=true");
                    continue;
                }

                // The wipe-recovery offer outranks the first-run flows: a wipe
                // victim must answer the restore question before onboarding or
                // a default widget appears. RestoreScheduled: the process is
                // shutting down for the relaunch. Dismissed: keep the decision
                // pending and defer the first-run flows to a later activation.
                RecoveryOfferOutcome activationRecoveryOffer =
                    await ShowRecoveryOfferIfNeededAsync();
                if (activationRecoveryOffer == RecoveryOfferOutcome.RestoreScheduled ||
                    _recoveryOfferWindow is not null ||
                    _recoveryOfferInFlight)
                {
                    // RestoreScheduled: the process is shutting down for the
                    // relaunch. Window open or another surface mid-show: the
                    // decision is still live — defer this activation's
                    // first-run flows until it lands. A settled Dismissed
                    // needs no deferral: it already cleared the marker as an
                    // implicit fresh start.
                    continue;
                }

                await EnsureInitialFileWidgetSetupAsync(
                    isInteractiveLaunch: true,
                    allowDefaultAfterRecoveryFailure:
                        activationRecoveryOffer == RecoveryOfferOutcome.FreshStart);

                if (WidgetManager is not null)
                {
                    bool hasConfiguredWidgets = SettingsService.Settings.Widgets.Any(widget =>
                        widget.WidgetKind == WidgetKind.File &&
                        !widget.IsDisabled &&
                        !SettingsService.Settings.DeletedWidgetIds.Contains(widget.Id));
                    bool anyLoadedVisible = WidgetManager.HasVisibleFileWidgets;
                    BareExternalActivationAction fallbackAction =
                        ExternalActivationPolicy.DecideBareActivation(
                            new BareExternalActivationContext(
                                hasConfiguredWidgets,
                                anyLoadedVisible));
                    Log(
                        $"[Activation] Bare fallback action={fallbackAction} " +
                        $"configuredFileWidgets={hasConfiguredWidgets} " +
                        $"visibleFileWidgets={anyLoadedVisible}");

                    if (fallbackAction ==
                        BareExternalActivationAction.RestoreAllWidgetsAndOpenSettings)
                    {
                        await WidgetManager.SetAllWidgetsVisibleAsync(true);
                    }
                }

                OpenSettings();
            }
            while (_externalActivationRequestedWhileBusy);
        }
        finally
        {
            _externalActivationHandling = false;
        }
    }

    private bool DrainPendingNativeNotificationActivations()
    {
        bool handled = false;
        const int maxDrainCount = 128;
        for (int index = 0; index < maxDrainCount; index++)
        {
            NativeNotificationActivationEnvelopeTakeResult takeResult =
                PendingNativeNotificationActivationStore.TryTakeNext();
            switch (takeResult.Disposition)
            {
                case NativeNotificationActivationEnvelopeTakeDisposition.Empty:
                    return handled;
                case NativeNotificationActivationEnvelopeTakeDisposition.Consumed
                    when takeResult.Envelope is { } envelope:
                    handled = true;
                    Log(
                        $"[Notification] Consumed forwarded activation envelope " +
                        $"envelope={envelope.EnvelopeId} sourcePid={envelope.SourceProcessId} " +
                        $"userInput={envelope.UserInput.Count} legacy={envelope.IsLegacyArgumentsOnly}");
                    OnPendingNativeNotificationActivationConsumed(envelope);
                    HandleNativeNotificationActivation(
                        new NativeAppNotificationActivation(
                            envelope.Arguments,
                            envelope.UserInput,
                            envelope.ActivationSource,
                            envelope.CreatedAtUtc,
                            envelope.SourceProcessId,
                            envelope.EnvelopeId));
                    break;
                case NativeNotificationActivationEnvelopeTakeDisposition.Rejected:
                    handled = true;
                    Log(
                        $"[Notification] Rejected forwarded activation envelope " +
                        $"path={takeResult.Path ?? "none"} error={takeResult.Error ?? "unknown"}");
                    OnPendingNativeNotificationActivationRejected(
                        takeResult.Path,
                        takeResult.Error);
                    break;
                default:
                    Log(
                        $"[Notification] Failed to drain forwarded activation envelope " +
                        $"path={takeResult.Path ?? "none"} error={takeResult.Error ?? "unknown"}");
                    return handled;
            }
        }

        if (PendingNativeNotificationActivationStore.HasPendingActivation)
        {
            Log(
                $"[Notification] Forwarded activation drain yielded after " +
                $"{maxDrainCount} envelopes; scheduling the next batch.");
            try
            {
                // The auto-reset event retains this continuation even during
                // startup, before the long-lived listener is registered.
                _activationEvent?.Set();
            }
            catch (Exception ex)
            {
                Log($"[Notification] Failed to schedule the next activation batch: {ex.Message}");
            }
        }

        return handled;
    }

    partial void OnPendingNativeNotificationActivationConsumed(
        NativeNotificationActivationEnvelope envelope);

    partial void OnPendingNativeNotificationActivationRejected(
        string? path,
        string? error);

    partial void OnNativeNotificationActivationObserved(
        NativeAppNotificationActivation activation);

    private void ShowDataRestoreResultNotification(
        DeskPaneRestoreApplyResult result,
        PostRestoreRecoverySummary recovery)
    {
        if (!result.HadPendingRestore)
        {
            return;
        }

        string title = LocalizationService.T(result.Succeeded
            ? "Settings.DataBackup.RestoreAppliedTitle"
            : "Settings.DataBackup.RestoreApplyFailedTitle");
        string message = result.Succeeded
            ? BuildRestoreAppliedMessage(recovery)
            : LocalizationService.Format(
                "Settings.DataBackup.RestoreApplyFailedBody",
                result.ErrorMessage ?? string.Empty);
        // A failed restore deep-links to the backup section it blocks; a
        // successful one returns the user to the widget board.
        Dictionary<string, string>? restoreArguments = result.Succeeded
            ? null
            : new Dictionary<string, string>
            {
                ["type"] = "settings",
                ["section"] = "BackupRestoreSettings"
            };

        if (_nativeNotificationService?.TryShow(title, message, restoreArguments) == true || _trayIcon is null)
        {
            return;
        }

        try
        {
            _trayIcon.ShowNotification(
                title,
                message,
                NotificationIcon.Info,
                customIconHandle: null,
                largeIcon: false,
                sound: false,
                respectQuietTime: true,
                realtime: false,
                timeout: TimeSpan.FromSeconds(7));
        }
        catch (Exception ex)
        {
            Log($"[DataBackup] Restore result notification failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The restore-complete body plus one line per recovery action the launch
    /// performed, so a cross-machine restore tells the user its startup
    /// registration and local paths were adapted to this machine.
    /// </summary>
    private string BuildRestoreAppliedMessage(PostRestoreRecoverySummary recovery)
    {
        string message = LocalizationService.T("Settings.DataBackup.RestoreAppliedBody");
        if (recovery.AutoStartReRegistered)
        {
            message += "\n" + LocalizationService.T("Settings.DataBackup.RestoreAppliedAutoStartNote");
        }

        if (recovery.LocalPathsRebased)
        {
            message += "\n" + LocalizationService.T("Settings.DataBackup.RestoreAppliedLocalPathsNote");
        }

        return message;
    }

    private void ShowSettingsLoadRecoveryNotification()
    {
        switch (SettingsService.LastLoadRecoveryState)
        {
            case SettingsLoadRecoveryState.RecoveredFromBackup:
                ShowSettingsNotification(
                    "Settings.Persistence.RecoveredTitle",
                    "Settings.Persistence.RecoveredBody",
                    NotificationIcon.Info,
                    "Maintenance");
                break;
            case SettingsLoadRecoveryState.DefaultsAfterFailure:
                ShowSettingsNotification(
                    "Settings.Persistence.ResetTitle",
                    "Settings.Persistence.ResetBody",
                    NotificationIcon.Warning,
                    "Maintenance");
                break;
        }

        if (SettingsService.LastPersistenceFailure is { } failure)
        {
            OnSettingsPersistenceFailed(failure);
        }
    }

    /// <summary>
    /// Batch 3a of the settings-process split: an external snapshot (the
    /// settings host's commit) was applied, so the value-shaped fields that
    /// have no coordinator subscription on the main side get their diff
    /// appliers run against the live services. Runs on a pipe reader thread
    /// for theme changes — marshal window-affecting work to the UI thread.
    /// </summary>
    private void OnExternalSettingsSnapshotApplied(
        DeskPane.Models.AppSettings before,
        DeskPane.Models.AppSettings after)
    {
    }

    private void RefreshThemeAppearanceOnUi() => ThemeService.RefreshAppearance();

    private void OnSettingsPersistenceFailed(SettingsPersistenceFailure failure)
    {        Log(
            $"[SettingsService] Persistence failure operation={failure.Operation} " +
            $"at={failure.OccurredAt:O} message={failure.Message}");
        if (UiDispatcherQueue is { HasThreadAccess: false } dispatcher)
        {
            dispatcher.TryEnqueue(() => OnSettingsPersistenceFailed(failure));
            return;
        }

        if (DateTimeOffset.UtcNow - _lastSettingsPersistenceNotificationAt <
            TimeSpan.FromMinutes(5))
        {
            return;
        }

        _lastSettingsPersistenceNotificationAt = DateTimeOffset.UtcNow;
        ShowSettingsNotification(
            "Settings.Persistence.SaveFailedTitle",
            "Settings.Persistence.SaveFailedBody",
            NotificationIcon.Warning,
            "Maintenance");
    }

    private void OnMaterialCapabilitySettingsChanged()
    {
        // SettingsChanged can arrive off the UI thread and the notification
        // callback owns UI objects, so marshal through the app dispatcher.
        UiDispatcherQueue?.TryEnqueue(() =>
            MaterialCapabilityAdvisor.WarnIfMaterialDegraded(
                SettingsService.Settings.WidgetShell.WidgetMaterialType,
                (titleKey, bodyKey) => ShowSettingsNotification(
                    titleKey,
                    bodyKey,
                    NotificationIcon.Warning,
                    "AppearanceMaterialSettings")));
    }

    private void OnCloudBackupRunCompleted(CloudBackupRunCompletedInfo info)
    {
        if (IsShuttingDown) return;
        if (UiDispatcherQueue is { HasThreadAccess: false } dispatcher)
        {
            dispatcher.TryEnqueue(() => OnCloudBackupRunCompleted(info));
            return;
        }

        // An accepted-but-unverified upload is a degraded success, not a
        // failure: toast at most once per session so a laggy DAV listing
        // can't spam every interval, while a silently-dropping server still
        // surfaces instead of hiding behind the "last success" stamp.
        if (info is { Uploaded: true, UploadUnverified: true })
        {
            if (_cloudBackupUnverifiedToastShown)
            {
                return;
            }

            _cloudBackupUnverifiedToastShown = true;
            ShowSettingsNotification(
                "Settings.CloudBackup.UploadUnverified.Title",
                "Settings.CloudBackup.UploadUnverified.Body",
                NotificationIcon.Warning,
                "CloudBackupSettings");
            return;
        }

        // Only the FIRST failure of a scheduled streak toasts — the
        // settings status row carries the rest, so a dead endpoint can't
        // spam a notification on every interval tick. Manual failures are
        // already shown by the settings page that triggered them.
        if (info.Uploaded || !info.WasScheduled || !info.IsFirstFailureSinceSuccess)
        {
            return;
        }

        ShowSettingsNotification(
            "Settings.CloudBackup.ScheduledFailure.Title",
            "Settings.CloudBackup.ScheduledFailure.Body",
            NotificationIcon.Warning,
            "CloudBackupSettings");
    }

    private void OnAutomaticBackupFallbackDetected()
    {
        if (IsShuttingDown) return;
        if (UiDispatcherQueue is { HasThreadAccess: false } dispatcher)
        {
            dispatcher.TryEnqueue(OnAutomaticBackupFallbackDetected);
            return;
        }

        ShowSettingsNotification(
            "Settings.DataBackup.AutomaticBackupDirectory.FallbackTitle",
            "Settings.DataBackup.AutomaticBackupDirectory.FallbackBody",
            NotificationIcon.Warning,
            "BackupRestoreSettings");
    }

    /// <summary>
    /// Shows a settings deep-linking notification: toast first, tray
    /// balloon fallback. Returns true only when at least one channel
    /// actually displayed — one-time notices (the fully-elevated informed
    /// notice) ack on this verdict so a machine with toasts disabled and no
    /// tray yet retries next launch instead of swallowing the notice.
    /// </summary>
    private bool ShowSettingsNotification(
        string titleKey,
        string bodyKey,
        NotificationIcon icon,
        string? sectionTag = null)
    {
        if (LocalizationService is null || IsShuttingDown)
        {
            return false;
        }

        // Failure notices deep-link their body click into the owning settings
        // section; status notices without a section keep the raise-widgets
        // fallback below.
        Dictionary<string, string>? arguments = string.IsNullOrWhiteSpace(sectionTag)
            ? null
            : new Dictionary<string, string>
            {
                ["type"] = "settings",
                ["section"] = sectionTag
            };
        string title = LocalizationService.T(titleKey);
        string message = LocalizationService.T(bodyKey);
        if (_nativeNotificationService?.TryShow(title, message, arguments) == true)
        {
            return true;
        }

        if (_trayIcon is null)
        {
            return false;
        }

        try
        {
            _trayIcon.ShowNotification(
                title,
                message,
                icon,
                customIconHandle: null,
                largeIcon: false,
                sound: false,
                respectQuietTime: true,
                realtime: false,
                timeout: TimeSpan.FromSeconds(10));
            return true;
        }
        catch (Exception ex)
        {
            Log($"[SettingsService] Persistence notification failed: {ex.Message}");
            return false;
        }
    }

    private void ShowManagedStorageUnavailableNotification()
    {
        if (LocalizationService is null)
        {
            return;
        }

        string title = LocalizationService.T("Settings.ManagedPath.UnavailableTitle");
        string message = LocalizationService.T("Settings.ManagedPath.UnavailableBody");
        if (_nativeNotificationService?.TryShow(
                title,
                message,
                new Dictionary<string, string>
                {
                    ["type"] = "settings",
                    ["section"] = "ManagedStorage"
                }) == true || _trayIcon is null)
        {
            return;
        }

        try
        {
            _trayIcon.ShowNotification(
                title,
                message,
                NotificationIcon.Warning,
                customIconHandle: null,
                largeIcon: false,
                sound: false,
                respectQuietTime: true,
                realtime: false,
                timeout: TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            Log($"[WidgetManager] Managed storage unavailable notification failed: {ex.Message}");
        }
    }

    private void ShowRecoverySnapshotAvailableNotification(DeskPaneBackupSnapshotInfo? snapshot)
    {
        if (snapshot is null || LocalizationService is null)
        {
            return;
        }

        string title = LocalizationService.T("Settings.Recovery.AvailableTitle");
        string message = LocalizationService.Format(
            "Settings.Recovery.AvailableBody",
            snapshot.CreatedAtUtc.ToLocalTime().ToString("g"));
        Log($"[DataBackup] Recovery snapshot available after fresh settings start: {snapshot.Path}");

        if (_nativeNotificationService?.TryShow(
                title,
                message,
                new Dictionary<string, string>
                {
                    ["type"] = "settings",
                    ["section"] = "BackupRestoreSettings"
                }) == true || _trayIcon is null)
        {
            return;
        }

        try
        {
            _trayIcon.ShowNotification(
                title,
                message,
                NotificationIcon.Info,
                customIconHandle: null,
                largeIcon: false,
                sound: false,
                respectQuietTime: true,
                realtime: false,
                timeout: TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            Log($"[DataBackup] Recovery snapshot notification failed: {ex.Message}");
        }
    }

    /// <summary>
    /// A second-instance activation is also a useful lifecycle signal: it is
    /// commonly produced when the user returns after lock, sleep, Explorer
    /// restart, or a mapped-drive reconnection. Reconcile lightweight external
    /// state without rebuilding the whole application on every activation.
    /// </summary>
    private void ScheduleExternalStateRecovery()
    {
        if (Interlocked.Exchange(ref _externalStateRecoveryScheduled, 1) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(650).ConfigureAwait(false);
                UiDispatcherQueue?.TryEnqueue(() => _ = RecoverExternalStateAsync());
            }
            catch (Exception ex)
            {
                Log($"[Lifecycle] External state recovery scheduling failed: {ex.Message}");
                Volatile.Write(ref _externalStateRecoveryScheduled, 0);
            }
        });
    }

    private async Task RecoverExternalStateAsync()
    {
        try
        {
            if (WidgetManager is not null)
            {
                string[] fileWidgetIds = SettingsService.Settings.Widgets
                    .Where(widget => widget.WidgetKind == WidgetKind.File &&
                                     !widget.IsDisabled &&
                                     !SettingsService.Settings.DeletedWidgetIds.Contains(widget.Id))
                    .Select(widget => widget.Id)
                    .ToArray();

                foreach (string widgetId in fileWidgetIds)
                {
                    await WidgetManager.RefreshFileWidgetAsync(widgetId);
                }
            }

            Log("[Lifecycle] External state recovery completed.");
        }
        catch (Exception ex)
        {
            Log($"[Lifecycle] External state recovery failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _externalStateRecoveryScheduled, 0);
        }
    }

    /// <summary>
    /// Runs an explicit external-state reconciliation from the diagnostics
    /// page. Unlike the debounced lifecycle path this method is awaitable so
    /// the UI can report when file widgets have finished refreshing.
    /// </summary>
    public async Task ForceExternalStateRecoveryAsync()
    {
        await RecoverExternalStateAsync();
        _displayAreaWatcher?.RefreshNow();
    }

    private void OnLanguageChanged()
    {
        if (IsShuttingDown) return;
        Localized.RefreshAll(LocalizationService);
        RefreshTrayMenuText();
        RefreshTrayToolTipText();
        // Deep review R15 (F9 audit): the taskbar Jump List kept the old
        // language until the next launch — reconfigure it now (idempotent,
        // fully fault-tolerant per its contract).
        _ = JumpListService.ConfigureAsync(LocalizationService);
    }

    private void OpenSettings()
    {
        // Settings window removed; preferences are managed via the tray context menu.
    }

    public void ShowSettings()
    {
    }

    public void ShowSettings(string sectionTag)
    {
    }

    private async Task EnsureInitialFileWidgetSetupAsync(
        bool isInteractiveLaunch,
        bool allowDefaultAfterRecoveryFailure = false)
    {
        if (WidgetManager is null)
        {
            return;
        }

        AppSettings settings = SettingsService.Settings;
        InitialFileWidgetSetupDecision decision =
            InitialFileWidgetSetupPolicy.Evaluate(new InitialFileWidgetSetupSnapshot(
                isInteractiveLaunch,
                SettingsService.LastLoadRecoveryState,
                settings.HasResolvedInitialFileWidgetSetup,
                InitialFileWidgetSetupPolicy.HasConfiguredFileWidget(settings),
                allowDefaultAfterRecoveryFailure));
        Log(
            $"[InitialFileWidgetSetup] decision={decision} interactive={isInteractiveLaunch} " +
            $"loadState={SettingsService.LastLoadRecoveryState} " +
            $"resolved={settings.HasResolvedInitialFileWidgetSetup}");

        if (decision == InitialFileWidgetSetupDecision.ResolveExistingConfiguration)
        {
            settings.HasResolvedInitialFileWidgetSetup = true;
            await SettingsService.SaveAsync();
            return;
        }

        if (decision != InitialFileWidgetSetupDecision.CreateDefaultWidget)
        {
            return;
        }

        // CreateManagedWidgetAsync persists the new widget config. Set the
        // one-time marker first so both values are written by the same save.
        settings.HasResolvedInitialFileWidgetSetup = true;
        try
        {
            await WidgetManager.CreateInitialManagedWidgetAsync(
                LocalizationService.T("Widget.DefaultDesktopName"));
        }
        catch
        {
            // Directory creation can fail before a widget config exists. Keep
            // the setup pending in that case so a later interactive launch can
            // retry after the storage problem has been corrected.
            if (!InitialFileWidgetSetupPolicy.HasConfiguredFileWidget(settings))
            {
                settings.HasResolvedInitialFileWidgetSetup = false;
            }

            throw;
        }
    }

    private async Task<RecoveryOfferOutcome> ShowRecoveryOfferIfNeededAsync()
    {
        if (_recoveryOfferShownThisSession)
        {
            // Whichever surface showed the offer already recorded the user's
            // decision; hand it to this caller so the pipeline and activation
            // paths act on the same answer.
            return _lastRecoveryOfferOutcome;
        }

        if (_recoveryOfferInFlight)
        {
            // Another surface is mid-way through showing the offer; its window
            // decision has not landed yet, so defer this caller's first-run
            // flows rather than racing a second window.
            return RecoveryOfferOutcome.Dismissed;
        }

        if (IsShuttingDown || !IsWipeRecoveryEligible)
        {
            return RecoveryOfferOutcome.NotOffered;
        }

        RecoveryOfferMarker? marker;
        try
        {
            marker = RecoveryOfferStore.TryRead();
        }
        catch (Exception ex)
        {
            Log($"[RecoveryOffer] Marker read failed: {ex.Message}");
            return RecoveryOfferOutcome.NotOffered;
        }

        if (marker is null)
        {
            return RecoveryOfferOutcome.NotOffered;
        }

        _recoveryOfferInFlight = true;
        try
        {
            RecoverySnapshotCandidate? candidate;
            try
            {
                candidate = await DataBackupService
                    .GetLatestValidRecoverySnapshotAsync(_startupProcessStartUtc);
            }
            catch (Exception ex)
            {
                Log($"[RecoveryOffer] Candidate validation failed: {ex.Message}");
                return RecoveryOfferOutcome.NotOffered;
            }

            if (candidate is null)
            {
                await RecoveryOfferStore.ClearAsync();
                Log("[RecoveryOffer] Marker cleared; no valid recovery snapshot remains.");
                return RecoveryOfferOutcome.NotOffered;
            }

            _recoveryOfferShownThisSession = true;
            RecoveryOfferOutcome outcome;
            try
            {
                _recoveryOfferWindow = new RecoveryOfferWindow(LocalizationService, candidate);
                _recoveryOfferWindow.Closed += (_, _) => _recoveryOfferWindow = null;
                ThemeService.TrackWindow(_recoveryOfferWindow);
                _recoveryOfferWindow.Activate();
                Log($"[RecoveryOffer] Offer shown for snapshot '{candidate.Path}'.");

                RecoveryOfferDecision decision = await _recoveryOfferWindow.Completion;
                outcome = decision switch
                {
                    RecoveryOfferDecision.FreshStart => RecoveryOfferOutcome.FreshStart,
                    RecoveryOfferDecision.Restore => RecoveryOfferOutcome.RestoreScheduled,
                    _ => RecoveryOfferOutcome.Dismissed,
                };
            }
            catch (Exception ex)
            {
                Log($"[RecoveryOffer] Offer failed: {ex.Message}");
                outcome = RecoveryOfferOutcome.Dismissed;
            }

            // Restore is pre-recorded so activations arriving while the
            // restore transaction is being prepared see the user's answer
            // instead of NotOffered (which would run the first-run flows in a
            // process that is about to relaunch).
            _lastRecoveryOfferOutcome = outcome;

            switch (outcome)
            {
                case RecoveryOfferOutcome.RestoreScheduled:
                    if (!await TryScheduleRecoveryRestoreAsync(candidate))
                    {
                        _lastRecoveryOfferOutcome = outcome = RecoveryOfferOutcome.Dismissed;
                    }

                    break;
                case RecoveryOfferOutcome.FreshStart:
                    await HandleRecoveryOfferFreshStartAsync(relaxDefaultWidgetPolicy: true);
                    break;
                default:
                    // Closing the window counts as a fresh start (decision A):
                    // clear the marker so the rebuilt profile is never
                    // re-offered, but without the explicit button's
                    // DefaultsAfterFailure relaxation.
                    await HandleRecoveryOfferFreshStartAsync(relaxDefaultWidgetPolicy: false);
                    break;
            }

            Log($"[RecoveryOffer] Offer outcome={outcome}.");
            return outcome;
        }
        finally
        {
            _recoveryOfferInFlight = false;
        }
    }

    /// <summary>
    /// Prepares, confirms and schedules the full restore the offer proposed.
    /// The offer window already presented the snapshot's date, version and
    /// widget count, so its Restore button is the confirmation — the marker is
    /// cleared here as well because the pre-restore safety backup (which the
    /// pending restore keeps) could otherwise carry it back in.
    /// </summary>
    private async Task<bool> TryScheduleRecoveryRestoreAsync(RecoverySnapshotCandidate candidate)
    {
        bool restartScheduled = false;
        try
        {
            await DataBackupService.PrepareRestoreAsync(candidate.Path);
            if (!await DataBackupService.ConfirmPendingFullRestoreAsync())
            {
                await DataBackupService.CancelPendingRestoreAsync();
                Log("[RecoveryOffer] Restore confirmation failed; pending restore cancelled.");
                ShowRecoveryNotification(
                    "Settings.DataBackup.RestoreFailedTitle",
                    "Settings.DataBackup.RestoreFailedBody",
                    errorMessage: null,
                    isError: true);
                return false;
            }

            AppRelaunchScheduleResult relaunch = AppRelaunchService.ScheduleAfterCurrentProcessExit();
            if (!relaunch.Started)
            {
                await DataBackupService.CancelPendingRestoreAsync();
                Log($"[RecoveryOffer] Relaunch scheduling failed: {relaunch.ErrorMessage}");
                ShowRecoveryNotification(
                    "Settings.DataBackup.RestoreFailedTitle",
                    "Settings.DataBackup.RestoreFailedBody",
                    relaunch.ErrorMessage,
                    isError: true);
                return false;
            }

            restartScheduled = true;
            await RecoveryOfferStore.ClearAsync();
            Log($"[RecoveryOffer] Restore from '{candidate.Path}' scheduled; restarting.");
            await ShutdownForRestartAsync();
            return true;
        }
        catch (Exception ex)
        {
            Log($"[RecoveryOffer] Restore scheduling failed: {ex}");
            // A restore that already scheduled its relaunch helper must not be
            // cancelled by a later failure (e.g. the shutdown call itself
            // throwing): the helper owns the restart and the confirmed marker
            // is exactly what the next launch applies. Mirrors the manual
            // restore flow's restartScheduled guard.
            if (!restartScheduled)
            {
                try
                {
                    await DataBackupService.CancelPendingRestoreAsync();
                }
                catch (Exception cancelEx)
                {
                    Log($"[RecoveryOffer] Pending restore cleanup failed: {cancelEx.Message}");
                }

                ShowRecoveryNotification(
                    "Settings.DataBackup.RestoreFailedTitle",
                    "Settings.DataBackup.RestoreFailedBody",
                    ex.Message,
                    isError: true);
            }

            return restartScheduled;
        }
    }

    private async Task HandleRecoveryOfferFreshStartAsync(bool relaxDefaultWidgetPolicy)
    {
        try
        {
            await RecoveryOfferStore.ClearAsync();
        }
        catch (Exception ex)
        {
            Log($"[RecoveryOffer] Marker clear failed: {ex.Message}");
        }

        if (!relaxDefaultWidgetPolicy)
        {
            return;
        }

        // DefaultsAfterFailure resolves initial setup at load so a recovery
        // launch never manufactures widgets; the explicit fresh start reopens
        // that first-run decision (the policy's allow flag covers the rest).
        if (SettingsService.LastLoadRecoveryState == SettingsLoadRecoveryState.DefaultsAfterFailure &&
            SettingsService.Settings.HasResolvedInitialFileWidgetSetup)
        {
            SettingsService.Settings.HasResolvedInitialFileWidgetSetup = false;
            await SettingsService.SaveAsync();
        }
    }

    private void ShowRecoveryNotification(
        string titleKey,
        string bodyKey,
        string? errorMessage,
        bool isError = false)
    {
        if (LocalizationService is null)
        {
            return;
        }

        string title = LocalizationService.T(titleKey);
        string message = LocalizationService.Format(bodyKey, errorMessage ?? string.Empty);
        if (_nativeNotificationService?.TryShow(
                title,
                message,
                new Dictionary<string, string>
                {
                    ["type"] = "settings",
                    ["section"] = "BackupRestoreSettings"
                }) != true)
        {
            // Same guard as every other balloon fallback: the tray icon may
            // be absent (the hidden style removes it at startup), and
            // ShowNotification throws rather than no-ops in that state.
            try
            {
                _trayIcon?.ShowNotification(
                    title,
                    message,
                    isError ? NotificationIcon.Error : NotificationIcon.Info,
                    customIconHandle: null,
                    largeIcon: false,
                    sound: false,
                    respectQuietTime: true,
                    realtime: false,
                    timeout: TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                Log($"[Recovery] Tray balloon fallback failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// While the recovery decision is pending, automatic snapshots are paused
    /// (see <see cref="DeskPaneDataBackupService.SetRecoveryOfferPauseProbe"/>);
    /// a silent pause is its own data risk, so the user gets at most one
    /// reminder per process run (the gate is in-memory; a fresh boot can
    /// remind again) telling them a decision is waiting. Bounded nagging:
    /// the offer window and the pause end together as soon as they answer.
    /// </summary>
    private void ScheduleSnapshotPausedReminderIfNeeded()
    {
        if (IsShuttingDown ||
            _recoveryOfferWindow is not null ||
            (_lastSnapshotPausedReminderAtUtc is { } last &&
             DateTimeOffset.UtcNow - last < TimeSpan.FromHours(20)))
        {
            return;
        }

        try
        {
            if (RecoveryOfferStore.TryRead() is null)
            {
                return;
            }
        }
        catch
        {
            return;
        }

        _lastSnapshotPausedReminderAtUtc = DateTimeOffset.UtcNow;
        Log("[RecoveryOffer] Automatic backups are paused; reminding the user a recovery decision is pending.");
        UiDispatcherQueue?.TryEnqueue(() => ShowRecoveryNotification(
            "Settings.Recovery.SnapshotPausedReminderTitle",
            "Settings.Recovery.SnapshotPausedReminderBody",
            errorMessage: null));
    }

    private static DeskPaneBackupSnapshotInfo ToRecoverySnapshotInfo(
        RecoverySnapshotCandidate candidate) =>
        new(
            candidate.Path,
            "automatic",
            candidate.CreatedAtUtc,
            0,
            IsReadable: true,
            candidate.AppVersion,
            candidate.BackupSchemaVersion);

    /// <summary>
    /// Shows the elevation notice once startup has produced a usable
    /// surface. Skipped for unattended launches (the constructor already
    /// attempted a silent unelevated relaunch; nobody is watching a dialog)
    /// and on shutdown. The asymmetric mismatch keeps its every-time notice
    /// (self-healable, the user should act each elevated launch); since
    /// 2026-10-22 the same-level elevated desktop — silent for years — gets
    /// a one-time informed notice instead, because WinUI forbids XAML
    /// drag-out in an elevated process: the session is degraded in a way
    /// only a system-level change fixes, and the user deserves to know.
    /// Acknowledgement persists in settings, so the interruption is once
    /// per install, not once per launch.
    /// </summary>
    private void ShowElevationNoticeIfNeeded()
    {
        if (!_startupElevationAssessment.IsCurrentProcessElevated ||
            IsShuttingDown ||
            _elevationNoticeWindow is not null)
        {
            return;
        }

        if (_startupElevationAssessment.Mismatch == ShellIntegrityMismatch.AppAboveShell)
        {
            if (IsStartupMode)
            {
                return;
            }

            // Persisted RUNASADMIN-style compatibility layers keep relaunching
            // DeskPane elevated, so scrub them while the notice is up — whether
            // the user exits via the button or the window X, their next normal
            // launch must be able to land unelevated. Failures log only.
            _ = DragDropPermissionService.ClearElevationCompatibilityFlags();

            _elevationNoticeWindow = new ElevationNoticeWindow(LocalizationService, _startupElevationAssessment);
            _elevationNoticeWindow.Closed += (_, _) => _elevationNoticeWindow = null;
            ThemeService.TrackWindow(_elevationNoticeWindow);
            _elevationNoticeWindow.Activate();
            Log("[Elevation] Notice shown for interactive elevated launch");
            return;
        }

        // Same-level elevated desktop (built-in Administrator / UAC off).
        if (_startupElevationAssessment.FullyElevatedAttribution == FullyElevatedSessionAttribution.None ||
            SettingsService.Settings.Core.FullyElevatedNoticeAcknowledgedUtcTicks is not null)
        {
            return;
        }

        if (IsStartupMode)
        {
            ShowFullyElevatedNoticeToast();
            return;
        }

        _elevationNoticeWindow = new ElevationNoticeWindow(LocalizationService, _startupElevationAssessment);
        _elevationNoticeWindow.Closed += (_, _) => _elevationNoticeWindow = null;
        ThemeService.TrackWindow(_elevationNoticeWindow);
        _elevationNoticeWindow.Activate();
        // Ack on show: the window being seen completes the notice's job.
        MarkFullyElevatedNoticeAcknowledged();
        Log("[Elevation] Fully-elevated informed notice shown for interactive launch");
    }

    /// <summary>
    /// Unattended form of the fully-elevated informed notice: a toast (or
    /// tray balloon fallback) deep-linking into the compatibility diagnostic
    /// section, shown once per install. Ack only when at least one channel
    /// displayed — see <see cref="ShowSettingsNotification"/>.
    /// </summary>
    private void ShowFullyElevatedNoticeToast()
    {
        bool shown = ShowSettingsNotification(
            "Tray.FullyElevatedNotice.Title",
            "Tray.FullyElevatedNotice.Message",
            NotificationIcon.Info,
            "CompatibilityDiagnosticsSettings");
        if (shown)
        {
            MarkFullyElevatedNoticeAcknowledged();
        }

        Log($"[Elevation] Fully-elevated toast shown={shown}");
    }

    private void MarkFullyElevatedNoticeAcknowledged()
    {
        try
        {
            // Slice-direct access: new settings code reads the slice, keeping
            // the App.xaml.cs facade-access ratchet from growing.
            SettingsService.Settings.Core.FullyElevatedNoticeAcknowledgedUtcTicks =
                DateTimeOffset.UtcNow.UtcTicks;
            _ = SettingsService.SaveCheckedAsync();
        }
        catch (Exception ex)
        {
            // A failed persist only means the notice reappears next launch.
            Log($"[Elevation] Failed to persist fully-elevated notice ack: {ex.Message}");
        }
    }

    /// <summary>
    /// When the shell runs above this process's integrity, tray creation is
    /// blocked by UIPI rather than by a DeskPane defect. Once the tray retry
    /// loop has confirmed the icon is gone, surface the explanatory notice so
    /// the user is not left with an invisible tray and no reason — the
    /// session itself stays alive (the lifeline accepts the hotkey as a
    /// usable surface).
    /// </summary>
    private async Task ShowShellElevationNoticeWhenTrayFailsAsync(Task<bool> trayCreation)
    {
        bool trayCreated;
        try
        {
            trayCreated = await trayCreation;
        }
        catch (Exception ex)
        {
            Log($"[Elevation] Tray creation task faulted: {ex.Message}");
            trayCreated = false;
        }

        if (trayCreated ||
            IsStartupMode ||
            IsShuttingDown ||
            _elevationNoticeWindow is not null)
        {
            return;
        }

        _elevationNoticeWindow = new ElevationNoticeWindow(LocalizationService, _startupElevationAssessment);
        _elevationNoticeWindow.Closed += (_, _) => _elevationNoticeWindow = null;
        ThemeService.TrackWindow(_elevationNoticeWindow);
        _elevationNoticeWindow.Activate();
        Log("[Elevation] Notice shown for shell-above-app tray failure");
    }

    private static int s_lightMemoryCleanupGeneration;
    private static int s_backgroundMemoryCleanupGeneration;
    private static CancellationTokenSource?
        s_backgroundMemoryCleanupCancellationSource;
    private static long s_memoryCleanupEpoch;

    /// <summary>
    /// Changes only after a process working-set trim has made previously warmed
    /// XAML pages non-resident. A forced GC alone does not invalidate a live
    /// capsule layout and therefore must not make every widget cold again.
    /// </summary>
    internal static long MemoryCleanupEpoch => Volatile.Read(ref s_memoryCleanupEpoch);

    /// <summary>
    /// Raised (possibly from a background thread) after a working-set trim
    /// invalidated warmed state. Observers must marshal to their own threads.
    /// </summary>
    internal static event Action? MemoryCleanupEpochAdvanced;

    private static void AdvanceMemoryCleanupEpoch(string reason)
    {
        long cleanupEpoch = Interlocked.Increment(ref s_memoryCleanupEpoch);
        PerformanceLogger.Mark(
            "MemoryCleanupEpochAdvanced",
            $"epoch={cleanupEpoch} reason={reason}");
        Action? handlers = MemoryCleanupEpochAdvanced;
        if (handlers is null)
        {
            return;
        }

        foreach (Action handler in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                Log($"[MemoryCleanup] Epoch observer failed: {ex.Message}");
            }
        }
    }

    private void StartVisibleIdleMemoryMaintenance()
    {
        if (_visibleIdleMemoryMaintenanceTimer is null)
        {
            _visibleIdleMemoryMaintenanceTimer = UiDispatcherQueue.CreateTimer();
            _visibleIdleMemoryMaintenanceTimer.IsRepeating = false;
            _visibleIdleMemoryMaintenanceTimer.Tick += VisibleIdleMemoryMaintenanceTimer_Tick;
        }

        EffectivePerformanceSettings performance =
            PerformanceSettingsPolicy.Resolve(SettingsService.Settings);
        bool visibleIdleCleanupEnabled =
            ConfigureVisibleIdleMemoryTracker(performance);
        if (!visibleIdleCleanupEnabled)
        {
            _visibleIdleMemoryMaintenanceTimer.Stop();
            return;
        }

        var activity = CaptureMemoryCleanupActivity();
        _visibleIdleMemoryTracker.Observe(
            DateTimeOffset.UtcNow,
            visibleIdleCleanupEnabled &&
            MemoryCleanupPolicy.IsVisibleIdleCandidate(activity));
        ScheduleVisibleIdleMemoryMaintenance(
            TimeSpan.FromSeconds(VisibleIdleMemoryCheckIntervalSeconds));
    }

    private void ScheduleVisibleIdleMemoryMaintenance(TimeSpan delay)
    {
        if (_visibleIdleMemoryMaintenanceTimer is null)
        {
            return;
        }

        _visibleIdleMemoryMaintenanceTimer.Stop();
        _visibleIdleMemoryMaintenanceTimer.Interval = delay;
        _visibleIdleMemoryMaintenanceTimer.Start();
    }

    private void VisibleIdleMemoryMaintenanceTimer_Tick(
        Microsoft.UI.Dispatching.DispatcherQueueTimer sender,
        object args)
    {
        sender.Stop();
        if (Interlocked.Exchange(ref _visibleIdleMemoryMaintenanceRunning, 1) != 0)
        {
            return;
        }

        try
        {
            EffectivePerformanceSettings performance =
                PerformanceSettingsPolicy.Resolve(SettingsService.Settings);
            if (!ConfigureVisibleIdleMemoryTracker(performance))
            {
                return;
            }

            int visibleIdleDelaySeconds =
                performance.VisibleIdleCacheCleanupDelaySeconds;
            var activity = CaptureMemoryCleanupActivity();
            bool isVisibleIdleCandidate =
                MemoryCleanupPolicy.IsVisibleIdleCandidate(activity);
            DateTimeOffset maintenanceDueAt = DateTimeOffset.UtcNow;
            if (!_visibleIdleMemoryTracker.Observe(
                    maintenanceDueAt,
                    isVisibleIdleCandidate))
            {
                return;
            }

            using var process = Process.GetCurrentProcess();
            process.Refresh();
            long workingSetBefore = process.WorkingSet64;
            long privateBytesBefore = process.PrivateMemorySize64;

            Localized.PruneDeadTargets();
            IconHelper.IdleIconCacheReleaseResult cacheRelease =
                IconHelper.ReleaseIdleCaches(
                    TimeSpan.FromSeconds(Math.Max(1, visibleIdleDelaySeconds)));
            process.Refresh();
            _visibleIdleMemoryTracker.CommitMaintenance(
                DateTimeOffset.UtcNow);

            // Pre-1.4.5 visible-idle trim, restored with its original bloat
            // thresholds: only while the user is fully away (the idle contract
            // above) and only when both footprints are genuinely large, so an
            // idle desktop neither pays constant trim/fault churn nor jitters
            // an ambient animation from back-faults.
            bool visibleIdleTrimmed = false;
            if (SettingsService.Settings.IdleWorkingSetTrimEnabled)
            {
                MemoryCleanupActivitySnapshot trimActivity =
                    CaptureMemoryCleanupActivity();
                process.Refresh();
                if (MemoryCleanupPolicy.ShouldTrimVisibleIdleWorkingSet(
                        trimActivity,
                        process.WorkingSet64,
                        process.PrivateMemorySize64))
                {
                    visibleIdleTrimmed = Win32Helper.TrimWorkingSet();
                    CompleteWorkingSetTrim(visibleIdleTrimmed, "visible-idle");
                    if (visibleIdleTrimmed)
                    {
                        process.Refresh();
                    }
                }
            }

            Log(
                $"[Memory] Visible idle maintenance completed idleSeconds={visibleIdleDelaySeconds} " +
                $"workingSetTrimmed={visibleIdleTrimmed} " +
                $"workingSetBeforeMB={workingSetBefore / (1024.0 * 1024):F1} " +
                $"workingSetAfterMB={process.WorkingSet64 / (1024.0 * 1024):F1} " +
                $"privateBeforeMB={privateBytesBefore / (1024.0 * 1024):F1} " +
                $"privateAfterMB={process.PrivateMemorySize64 / (1024.0 * 1024):F1} " +
                $"forcedCollection=false warmCachePreserved=true " +
                $"releasedThumbs={cacheRelease.ReleasedThumbnails} " +
                $"releasedBitmaps={cacheRelease.ReleasedDecodedBitmaps} " +
                $"releasedIconBytes={cacheRelease.ReleasedIconByteEntries} " +
                $"releasedEstimatedMB={cacheRelease.ReleasedEstimatedBytes / (1024.0 * 1024):F1} " +
                $"interactiveCacheTrim={cacheRelease.ReleasedAnything} " +
                $"reason=visible-ux-preservation");
        }
        catch (Exception ex)
        {
            Log($"[Memory] Visible idle maintenance failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _visibleIdleMemoryMaintenanceRunning, 0);
            if (_visibleIdleMemoryMaintenanceTimer is not null &&
                ConfigureVisibleIdleMemoryTracker(
                    PerformanceSettingsPolicy.Resolve(SettingsService.Settings)))
            {
                ScheduleVisibleIdleMemoryMaintenance(
                    TimeSpan.FromSeconds(VisibleIdleMemoryCheckIntervalSeconds));
            }
        }
    }

    private void StopVisibleIdleMemoryMaintenance()
    {
        if (_visibleIdleMemoryMaintenanceTimer is null)
        {
            return;
        }

        _visibleIdleMemoryMaintenanceTimer.Stop();
        _visibleIdleMemoryMaintenanceTimer.Tick -= VisibleIdleMemoryMaintenanceTimer_Tick;
        _visibleIdleMemoryMaintenanceTimer = null;
        _visibleIdleMemoryTracker.Reset();
    }

    private bool ConfigureVisibleIdleMemoryTracker(
        EffectivePerformanceSettings performance)
    {
        int delaySeconds = performance.VisibleIdleCacheCleanupDelaySeconds;
        if (delaySeconds == PerformanceSettingsPolicy.CleanupNever)
        {
            _visibleIdleMemoryTracker.Reset();
            return false;
        }

        int cooldownSeconds = Math.Max(
            VisibleIdleMemoryMinimumCooldownSeconds,
            delaySeconds);
        _visibleIdleMemoryTracker.Configure(
            TimeSpan.FromSeconds(delaySeconds),
            TimeSpan.FromSeconds(cooldownSeconds));
        return true;
    }

    private MemoryCleanupActivitySnapshot CaptureMemoryCleanupActivity()
    {
        var widgetManager = WidgetManager;
        WidgetMemoryVisibilitySnapshot visibility =
            widgetManager?.CaptureMemoryCleanupVisibilitySnapshot() ?? default;
        IntPtr foregroundWindow = Win32Helper.GetForegroundWindow();
        bool isDeskPaneForeground =
            foregroundWindow != IntPtr.Zero &&
            Win32Helper.IsWindowVisible(foregroundWindow) &&
            IsDeskPaneWindow(foregroundWindow);
        bool isPointerOverDeskPane =
            Win32Helper.GetCursorPos(out var cursor) &&
            IsDeskPaneWindow(Win32Helper.WindowFromPoint(cursor));
        return new MemoryCleanupActivitySnapshot(
            HasVisibleWidgets: visibility.HasNativeVisibleWidgets,
            IsWidgetInteractionActive: widgetManager?.IsWidgetInteractionActive == true,
            IsSettingsOpen: false,
            IsOnboardingOpen: false,
            IsSearchPopupVisible: false,
            IsDeskPaneForeground: isDeskPaneForeground,
            IsPointerOverDeskPane: isPointerOverDeskPane,
            IsDesktopOrganizationOpen: _desktopOrganizationWindow is not null);
    }

    private static void CancelBackgroundMemoryCleanupDelay()
    {
        CancellationTokenSource? cancellationSource = Interlocked.Exchange(
            ref s_backgroundMemoryCleanupCancellationSource,
            null);
        if (cancellationSource is not null)
        {
            try
            {
                cancellationSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    internal static void CancelBackgroundMemoryCleanup(string reason = "activity")
    {
        CancelBackgroundMemoryCleanupDelay();
        Current._immediateHiddenWorkingSetTrimTracker.CancelPending();

        int generation = Interlocked.Increment(
            ref s_backgroundMemoryCleanupGeneration);
        PerformanceLogger.Mark(
            "BackgroundMemoryCleanupCancelled",
            $"generation={generation} reason={reason}");
        NotifyMemoryCleanupActivity();
    }

    internal static void NotifyPerformanceSettingsChanged()
    {
        CancelBackgroundMemoryCleanup();
        App app = Current;
        EffectivePerformanceSettings performance =
            PerformanceSettingsPolicy.Resolve(app.SettingsService.Settings);
        IconHelper.ConfigurePerformanceCacheBudget(performance.CacheBudget);
        app._visibleIdleMemoryTracker.Reset();
        if (app.ConfigureVisibleIdleMemoryTracker(performance))
        {
            app.ScheduleVisibleIdleMemoryMaintenance(
                TimeSpan.FromSeconds(VisibleIdleMemoryCheckIntervalSeconds));
        }
        else
        {
            app._visibleIdleMemoryMaintenanceTimer?.Stop();
        }
        app.StartQuiescenceWorkingSetTrim();
        if (app.WidgetManager is null ||
            app.WidgetManager
                .CaptureMemoryCleanupVisibilitySnapshot()
                .HasNativeVisibleWidgets)
        {
            return;
        }

        ScheduleBackgroundMemoryCleanup("performance-settings-changed");
    }

    internal static void NotifyMemoryCleanupActivity()
    {
        if (Application.Current is App app)
        {
            app._visibleIdleMemoryTracker.Reset();
            app.NoteQuiescenceWorkingSetTrimActivity();
        }
    }

    internal bool CanRunCompactExpansionWarmup =>
        WidgetManager?.IsWidgetInteractionActive != true;

    internal bool CanRunCriticalCompactExpansionWarmup => true;

    private readonly record struct MemoryCleanupDiagnosticSnapshot(
        long WorkingSetBytes,
        long PrivateBytes,
        long ManagedHeapBytes,
        int ThumbnailCacheCount,
        long ThumbnailEstimatedBytes,
        int IconCacheCount,
        int ShellKindCacheCount,
        int ShortcutMetadataCacheCount,
        int DecodedBitmapCacheCount,
        long DecodedBitmapEstimatedBytes,
        WidgetMemoryVisibilitySnapshot Visibility);

    private sealed class BackgroundMemoryCleanupOutcomeState
    {
        internal string Status = "scheduled";
        internal string Detail = "pending";
        internal string CleanupScope =
            PerformanceSettingsPolicy.DefaultHiddenCacheCleanupScope;
        internal string SoftCleanupResult = "not-run";
        internal string DeepReclaimResult = "not-run";
        internal bool DeepReclaimExecuted;
        internal long DeepReclaimDurationMilliseconds;
        internal long DeepReclaimReleasedHeapBytes;
        internal int ReleasedShellKindEntries;
        internal int ReleasedShortcutMetadataEntries;
        internal int ReleasedThumbnailEntries;
        internal int ReleasedDecodedBitmapEntries;
        internal int ReleasedIconByteEntries;
        internal long ReleasedEstimatedBytes;
        internal int ReleasedCachedContentCount;
        internal BackgroundMemoryCleanupStage CompletedStages =
            BackgroundMemoryCleanupStage.None;
        internal int ActivityRetryCount;
        internal int MaintainedContentHostCount;
    }

    private static MemoryCleanupDiagnosticSnapshot
        CaptureMemoryCleanupDiagnosticSnapshot()
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            WidgetMemoryVisibilitySnapshot visibility =
                Current.WidgetManager?
                    .CaptureMemoryCleanupVisibilitySnapshot() ?? default;
            return new MemoryCleanupDiagnosticSnapshot(
                process.WorkingSet64,
                process.PrivateMemorySize64,
                GC.GetGCMemoryInfo().HeapSizeBytes,
                PerformanceLogger.ThumbnailCacheCount,
                PerformanceLogger.ThumbnailEstimatedBytes,
                PerformanceLogger.IconCacheCount,
                FileService.ShellKindCacheEntryCount,
                ShortcutHelper.StoredMetadataCacheEntryCount,
                PerformanceLogger.DecodedBitmapCacheCount,
                PerformanceLogger.DecodedBitmapEstimatedBytes,
                visibility);
        }
        catch
        {
            return default;
        }
    }

    private static void LogBackgroundMemoryCleanupOutcome(
        int generation,
        string triggerReason,
        string mode,
        int softDelaySeconds,
        int deepDelaySeconds,
        long scheduledTimestamp,
        MemoryCleanupDiagnosticSnapshot before,
        BackgroundMemoryCleanupOutcomeState state)
    {
        MemoryCleanupDiagnosticSnapshot after =
            CaptureMemoryCleanupDiagnosticSnapshot();
        MemoryCleanupActivitySnapshot activity =
            Current.CaptureMemoryCleanupActivity();
        string details =
            $"generation={generation} status={state.Status} " +
            $"detail={state.Detail} trigger={triggerReason} mode={mode} " +
            $"cleanupScope={state.CleanupScope} " +
            $"softCleanupResult={state.SoftCleanupResult} " +
            $"deepReclaimResult={state.DeepReclaimResult} " +
            $"deepReclaimExecuted={state.DeepReclaimExecuted} " +
            $"deepReclaimDurationMs={state.DeepReclaimDurationMilliseconds} " +
            $"deepReclaimReleasedHeapMB={state.DeepReclaimReleasedHeapBytes / (1024.0 * 1024):F1} " +
            $"hiddenSeconds={Stopwatch.GetElapsedTime(scheduledTimestamp).TotalSeconds:F1} " +
            $"softDelaySeconds={softDelaySeconds} " +
            $"deepDelaySeconds={deepDelaySeconds} " +
            $"completedStages={state.CompletedStages} " +
            $"activityRetries={state.ActivityRetryCount} " +
            $"forcedCollection={state.DeepReclaimExecuted} workingSetTrimmed=false " +
            $"fullViewRebuilds=0 warmViewsPreserved=true " +
            $"workingSetBeforeMB={before.WorkingSetBytes / (1024.0 * 1024):F1} " +
            $"workingSetAfterMB={after.WorkingSetBytes / (1024.0 * 1024):F1} " +
            $"privateBeforeMB={before.PrivateBytes / (1024.0 * 1024):F1} " +
            $"privateAfterMB={after.PrivateBytes / (1024.0 * 1024):F1} " +
            $"managedHeapBeforeMB={before.ManagedHeapBytes / (1024.0 * 1024):F1} " +
            $"managedHeapAfterMB={after.ManagedHeapBytes / (1024.0 * 1024):F1} " +
            $"privateMinusGcHeapBeforeEstimateMB={Math.Max(0, before.PrivateBytes - before.ManagedHeapBytes) / (1024.0 * 1024):F1} " +
            $"privateMinusGcHeapAfterEstimateMB={Math.Max(0, after.PrivateBytes - after.ManagedHeapBytes) / (1024.0 * 1024):F1} " +
            $"thumbCacheBefore={before.ThumbnailCacheCount} " +
            $"thumbCacheAfter={after.ThumbnailCacheCount} " +
            $"thumbCacheBeforeMB={before.ThumbnailEstimatedBytes / (1024.0 * 1024):F1} " +
            $"thumbCacheAfterMB={after.ThumbnailEstimatedBytes / (1024.0 * 1024):F1} " +
            $"iconCacheBefore={before.IconCacheCount} " +
            $"iconCacheAfter={after.IconCacheCount} " +
            $"decodedBitmapBefore={before.DecodedBitmapCacheCount} " +
            $"decodedBitmapAfter={after.DecodedBitmapCacheCount} " +
            $"decodedBitmapBeforeMB={before.DecodedBitmapEstimatedBytes / (1024.0 * 1024):F1} " +
            $"decodedBitmapAfterMB={after.DecodedBitmapEstimatedBytes / (1024.0 * 1024):F1} " +
            $"shellKindCacheBefore={before.ShellKindCacheCount} " +
            $"shellKindCacheAfter={after.ShellKindCacheCount} " +
            $"shortcutCacheBefore={before.ShortcutMetadataCacheCount} " +
            $"shortcutCacheAfter={after.ShortcutMetadataCacheCount} " +
            $"releasedShellKindEntries={state.ReleasedShellKindEntries} " +
            $"releasedShortcutMetadataEntries={state.ReleasedShortcutMetadataEntries} " +
            $"releasedThumbs={state.ReleasedThumbnailEntries} " +
            $"releasedBitmaps={state.ReleasedDecodedBitmapEntries} " +
            $"releasedIconBytes={state.ReleasedIconByteEntries} " +
            $"releasedEstimatedMB={state.ReleasedEstimatedBytes / (1024.0 * 1024):F1} " +
            $"releasedCachedContents={state.ReleasedCachedContentCount} " +
            $"maintainedHosts={state.MaintainedContentHostCount} " +
            $"loadedWidgets={after.Visibility.LoadedWindowCount} " +
            $"logicalVisibleWidgets={after.Visibility.LogicalVisibleCount} " +
            $"nativeVisibleWidgets={after.Visibility.NativeVisibleCount} " +
            $"interactionActive={activity.IsWidgetInteractionActive} " +
            $"settingsOpen={activity.IsSettingsOpen} " +
            $"onboardingOpen={activity.IsOnboardingOpen} " +
            $"searchVisible={activity.IsSearchPopupVisible} " +
            $"desktopOrganizationOpen={activity.IsDesktopOrganizationOpen} " +
            $"deskBoxForegroundVisible={activity.IsDeskPaneForeground} " +
            $"pointerOverDeskPane={activity.IsPointerOverDeskPane}";
        Log($"[Memory] Background cleanup outcome {details}");
        PerformanceLogger.Mark("BackgroundMemoryCleanupOutcome", details);
    }

    internal static void ScheduleBackgroundMemoryCleanup(
        string reason = "unspecified")
    {
        if (IsShuttingDown) return;
        CancelBackgroundMemoryCleanupDelay();
        App app = Current;
        EffectivePerformanceSettings performance =
            PerformanceSettingsPolicy.Resolve(app.SettingsService.Settings);
        int generation = Interlocked.Increment(
            ref s_backgroundMemoryCleanupGeneration);
        long scheduledTimestamp = Stopwatch.GetTimestamp();
        MemoryCleanupDiagnosticSnapshot before =
            CaptureMemoryCleanupDiagnosticSnapshot();
        int softDelaySeconds = performance.HiddenCacheCleanupDelaySeconds;
        int deepDelaySeconds = performance.HiddenDeepCleanupDelaySeconds;
        string cleanupScope = performance.HiddenCacheCleanupScope;

        if (softDelaySeconds == PerformanceSettingsPolicy.CleanupNever &&
            deepDelaySeconds == PerformanceSettingsPolicy.CleanupNever)
        {
            Log(
                $"[Memory] Background cleanup disabled generation={generation} " +
                $"mode={performance.Mode} reason={reason}");
            PerformanceLogger.Mark(
                "BackgroundMemoryCleanupDisabled",
                $"generation={generation} mode={performance.Mode} reason={reason}");
            LogBackgroundMemoryCleanupOutcome(
                generation,
                reason,
                performance.Mode,
                softDelaySeconds,
                deepDelaySeconds,
                scheduledTimestamp,
                before,
                new BackgroundMemoryCleanupOutcomeState
                {
                    Status = "disabled",
                    Detail = "all-stages-disabled",
                    CleanupScope = cleanupScope
                });
            return;
        }

        if (!app.CanArmBackgroundMemoryCleanup(out string blockReason))
        {
            LogVerbose(
                $"[Memory] Background cleanup not armed generation={generation} " +
                $"reason={reason} blockedBy={blockReason}");
            PerformanceLogger.Mark(
                "BackgroundMemoryCleanupNotArmed",
                $"generation={generation} reason={reason} blockedBy={blockReason}");
            LogBackgroundMemoryCleanupOutcome(
                generation,
                reason,
                performance.Mode,
                softDelaySeconds,
                deepDelaySeconds,
                scheduledTimestamp,
                before,
                new BackgroundMemoryCleanupOutcomeState
                {
                    Status = "not-armed",
                    Detail = blockReason,
                    CleanupScope = cleanupScope
                });
            return;
        }

        string scheduleDetails =
            $"generation={generation} reason={reason} mode={performance.Mode} " +
            $"softDelaySeconds={softDelaySeconds} " +
            $"deepDelaySeconds={deepDelaySeconds} " +
            $"cleanupScope={cleanupScope} " +
            $"policy=conditional-deep-finalizer,no-working-set-trim,no-view-rebuild";
        Log($"[Memory] Background cleanup scheduled {scheduleDetails}");
        PerformanceLogger.Mark(
            "BackgroundMemoryCleanupScheduled",
            scheduleDetails);

        var cancellationSource = new CancellationTokenSource();
        CancellationTokenSource? replacedSource = Interlocked.Exchange(
            ref s_backgroundMemoryCleanupCancellationSource,
            cancellationSource);
        if (replacedSource is not null)
        {
            try
            {
                replacedSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        var dispatcherQueue = UiDispatcherQueue;
        bool enqueued = dispatcherQueue?.TryEnqueue(() =>
            SafeFireAndForget(() =>
                app.RunBackgroundMemoryCleanupScheduleAsync(
                    generation,
                    reason,
                    performance.Mode,
                    scheduledTimestamp,
                    softDelaySeconds,
                    deepDelaySeconds,
                    cleanupScope,
                    before,
                    cancellationSource))) == true;
        if (!enqueued)
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref s_backgroundMemoryCleanupCancellationSource,
                        null,
                        cancellationSource),
                    cancellationSource))
            {
                cancellationSource.Cancel();
            }

            cancellationSource.Dispose();
            Log(
                $"[Memory] Background cleanup scheduling failed " +
                $"generation={generation} reason={reason} " +
                $"error=dispatcher-unavailable");
            LogBackgroundMemoryCleanupOutcome(
                generation,
                reason,
                performance.Mode,
                softDelaySeconds,
                deepDelaySeconds,
                scheduledTimestamp,
                before,
                new BackgroundMemoryCleanupOutcomeState
                {
                    Status = "dispatch-failed",
                    Detail = "dispatcher-unavailable",
                    CleanupScope = cleanupScope
                });
        }
    }

    private async Task RunBackgroundMemoryCleanupScheduleAsync(
        int generation,
        string triggerReason,
        string mode,
        long scheduledTimestamp,
        int softDelaySeconds,
        int deepDelaySeconds,
        string cleanupScope,
        MemoryCleanupDiagnosticSnapshot before,
        CancellationTokenSource cancellationSource)
    {
        var state = new BackgroundMemoryCleanupOutcomeState
        {
            CleanupScope = cleanupScope
        };
        CancellationToken cancellationToken = cancellationSource.Token;
        string? loggedWaitReason = null;
        int activityBackoffAttempt = 0;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (generation !=
                    Volatile.Read(ref s_backgroundMemoryCleanupGeneration))
                {
                    state.Status = "cancelled";
                    state.Detail = "generation-changed";
                    PerformanceLogger.Mark(
                        "BackgroundMemoryCleanupCancelled",
                        $"generation={generation} reason=generation-changed");
                    return;
                }

                TimeSpan hiddenDuration =
                    Stopwatch.GetElapsedTime(scheduledTimestamp);
                TimeSpan? nextDelay =
                    MemoryCleanupPolicy.GetDelayUntilNextBackgroundStage(
                        hiddenDuration,
                        softDelaySeconds,
                        deepDelaySeconds,
                        state.CompletedStages);
                if (nextDelay is null)
                {
                    state.Status = "completed";
                    state.Detail = "all-configured-stages-completed";
                    PerformanceLogger.Mark(
                        "BackgroundMemoryCleanupScheduleCompleted",
                        $"generation={generation} " +
                        $"stages={state.CompletedStages}");
                    return;
                }

                if (nextDelay.Value > TimeSpan.Zero)
                {
                    await Task.Delay(nextDelay.Value, cancellationToken);
                    continue;
                }

                BackgroundMemoryCleanupStage dueStages =
                    MemoryCleanupPolicy.GetDueBackgroundStages(
                        hiddenDuration,
                        softDelaySeconds,
                        deepDelaySeconds,
                        state.CompletedStages);
                if (dueStages == BackgroundMemoryCleanupStage.None)
                {
                    await Task.Yield();
                    continue;
                }

                if (!CanRunBackgroundMemoryCleanup())
                {
                    string waitReason =
                        $"activity:{DescribeBackgroundMemoryCleanupBlockReason()}";
                    state.ActivityRetryCount++;
                    activityBackoffAttempt++;
                    TimeSpan retryDelay =
                        MemoryCleanupPolicy.GetCappedBackgroundRetryDelay(
                            activityBackoffAttempt,
                            BackgroundMemoryCleanupRetryDelaySeconds,
                            BackgroundMemoryCleanupMaximumRetryDelaySeconds);
                    state.Detail = waitReason;
                    if (!string.Equals(
                            loggedWaitReason,
                            waitReason,
                            StringComparison.Ordinal))
                    {
                        loggedWaitReason = waitReason;
                        Log(
                            $"[Memory] Background cleanup waiting " +
                            $"generation={generation} stages={dueStages} " +
                            $"reason={waitReason} " +
                            $"retrySeconds={retryDelay.TotalSeconds:F0}");
                    }

                    await Task.Delay(retryDelay, cancellationToken);
                    continue;
                }

                activityBackoffAttempt = 0;

                if (dueStages.HasFlag(
                        BackgroundMemoryCleanupStage.SoftCache))
                {
                    PerformanceLogger.Mark(
                        "BackgroundMemorySoftCleanupTriggered",
                        $"generation={generation}");
                    bool softCleanupCompleted =
                        await RunBackgroundSoftMemoryCleanupAsync(
                            generation,
                            softDelaySeconds,
                            cleanupScope,
                            state);
                    if (!softCleanupCompleted)
                    {
                        state.ActivityRetryCount++;
                        activityBackoffAttempt++;
                        TimeSpan retryDelay =
                            MemoryCleanupPolicy.GetCappedBackgroundRetryDelay(
                                activityBackoffAttempt,
                                BackgroundMemoryCleanupRetryDelaySeconds,
                                BackgroundMemoryCleanupMaximumRetryDelaySeconds);
                        await Task.Delay(retryDelay, cancellationToken);
                        continue;
                    }

                    state.CompletedStages |=
                        BackgroundMemoryCleanupStage.SoftCache;
                    activityBackoffAttempt = 0;
                    loggedWaitReason = null;
                }

                if (generation !=
                    Volatile.Read(ref s_backgroundMemoryCleanupGeneration))
                {
                    state.Status = "cancelled";
                    state.Detail = "generation-changed-after-soft-cleanup";
                    return;
                }

                if (dueStages.HasFlag(
                        BackgroundMemoryCleanupStage.DeepFinalizerCollection))
                {
                    // Warm retention deliberately stops at cache eviction. A
                    // full collection is reserved for the explicit
                    // all-recreatable scope, where the user has accepted that
                    // disconnected native graphs may be finalized.
                    if (!string.Equals(
                            cleanupScope,
                            PerformanceSettingsPolicy.HiddenCacheCleanupScopeAllRecreatable,
                            StringComparison.Ordinal))
                    {
                        state.DeepReclaimResult = "skipped-scope";
                        state.CompletedStages |=
                            BackgroundMemoryCleanupStage.DeepFinalizerCollection;
                    }
                    else
                    {
                        if (!CanRunBackgroundMemoryCleanup())
                        {
                            state.ActivityRetryCount++;
                            activityBackoffAttempt++;
                            TimeSpan retryDelay =
                                MemoryCleanupPolicy.GetCappedBackgroundRetryDelay(
                                    activityBackoffAttempt,
                                    BackgroundMemoryCleanupRetryDelaySeconds,
                                    BackgroundMemoryCleanupMaximumRetryDelaySeconds);
                            state.Detail =
                                $"activity:{DescribeBackgroundMemoryCleanupBlockReason()}";
                            await Task.Delay(retryDelay, cancellationToken);
                            continue;
                        }

                        PerformanceLogger.Mark(
                            "BackgroundMemoryDeepReclaimTriggered",
                            $"generation={generation}");
                        bool deepReclaimCompleted =
                            await RunBackgroundDeepMemoryCleanupAsync(
                                generation,
                                triggerReason,
                                state);
                        if (!deepReclaimCompleted)
                        {
                            state.ActivityRetryCount++;
                            activityBackoffAttempt++;
                            TimeSpan retryDelay =
                                MemoryCleanupPolicy.GetCappedBackgroundRetryDelay(
                                    activityBackoffAttempt,
                                    BackgroundMemoryCleanupRetryDelaySeconds,
                                    BackgroundMemoryCleanupMaximumRetryDelaySeconds);
                            await Task.Delay(retryDelay, cancellationToken);
                            continue;
                        }

                        state.CompletedStages |=
                            BackgroundMemoryCleanupStage.DeepFinalizerCollection;
                    }

                    activityBackoffAttempt = 0;
                    loggedWaitReason = null;
                }

                if (dueStages.HasFlag(
                        BackgroundMemoryCleanupStage.LongHiddenMaintenance))
                {
                    if (!CanRunBackgroundMemoryCleanup())
                    {
                        state.ActivityRetryCount++;
                        activityBackoffAttempt++;
                        TimeSpan retryDelay =
                            MemoryCleanupPolicy.GetCappedBackgroundRetryDelay(
                                activityBackoffAttempt,
                                BackgroundMemoryCleanupRetryDelaySeconds,
                                BackgroundMemoryCleanupMaximumRetryDelaySeconds);
                        await Task.Delay(retryDelay, cancellationToken);
                        continue;
                    }

                    PerformanceLogger.Mark(
                        "BackgroundMemoryLongHiddenMaintenanceTriggered",
                        $"generation={generation}");
                    LongHiddenWidgetMaintenanceResult maintenanceResult =
                        WidgetManager?.RunLongHiddenNoRebuildMaintenance() ??
                        default;
                    state.MaintainedContentHostCount +=
                        maintenanceResult.ContentHostCount;
                    int releasedCachedContents = 0;
                    if (string.Equals(
                            cleanupScope,
                            PerformanceSettingsPolicy.HiddenCacheCleanupScopeAllRecreatable,
                            StringComparison.Ordinal))
                    {
                        LongHiddenWidgetResourceReleaseResult releaseResult =
                            WidgetManager?.ReleaseLongHiddenInactiveContent() ??
                            default;
                        releasedCachedContents = releaseResult.CachedContentCount;
                        state.ReleasedCachedContentCount += releasedCachedContents;
                    }
                    string maintenanceDetails =
                        $"generation={generation} " +
                        $"hosts={maintenanceResult.ContentHostCount} " +
                        $"releasedCachedContents={releasedCachedContents} " +
                        $"fullViewRebuilds=0 warmViewsPreserved=true";
                    Log(
                        $"[Memory] Long-hidden no-rebuild maintenance completed " +
                        maintenanceDetails);
                    PerformanceLogger.Mark(
                        "LongHiddenWidgetNoRebuildMaintenanceCompleted",
                        maintenanceDetails);
                    state.CompletedStages |=
                        BackgroundMemoryCleanupStage.LongHiddenMaintenance;
                    activityBackoffAttempt = 0;
                    loggedWaitReason = null;
                }
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            state.Status = "cancelled";
            state.Detail = "cancellation-requested";
        }
        catch (Exception ex)
        {
            state.Status = "failed";
            state.Detail = ex.GetType().Name;
            Log(
                $"[Memory] Background cleanup coordinator failed " +
                $"generation={generation}: {ex}");
        }
        finally
        {
            Interlocked.CompareExchange(
                ref s_backgroundMemoryCleanupCancellationSource,
                null,
                cancellationSource);
            try
            {
                LogBackgroundMemoryCleanupOutcome(
                    generation,
                    triggerReason,
                    mode,
                    softDelaySeconds,
                    deepDelaySeconds,
                    scheduledTimestamp,
                    before,
                    state);
            }
            catch (Exception ex)
            {
                Log(
                    $"[Memory] Background cleanup outcome logging failed " +
                    $"generation={generation}: {ex.Message}");
            }

            cancellationSource.Dispose();
        }
    }

    private bool CanArmBackgroundMemoryCleanup(out string blockReason)
    {
        if (WidgetManager is null)
        {
            blockReason = "widget-manager-unavailable";
            return false;
        }

        WidgetMemoryVisibilitySnapshot visibility =
            WidgetManager.CaptureMemoryCleanupVisibilitySnapshot();
        if (visibility.HasNativeVisibleWidgets)
        {
            blockReason =
                $"widgets-visible-native-{visibility.NativeVisibleCount}";
            return false;
        }

        if (_desktopOrganizationWindow is not null)
        {
            blockReason = "desktop-organization-open";
            return false;
        }

        blockReason = string.Empty;
        return true;
    }

    private bool CanRunBackgroundMemoryCleanup()
    {
        if (!CanArmBackgroundMemoryCleanup(out _))
        {
            return false;
        }

        // The scheduler may wake up after the user has moved back over a
        // hidden DeskPane HWND. Recheck the complete idle contract immediately
        // before touching caches or running the finalizer collection.
        MemoryCleanupActivitySnapshot activity =
            CaptureMemoryCleanupActivity();
        return MemoryCleanupPolicy.IsDeepCleanupCandidate(activity);
    }

    private string DescribeBackgroundMemoryCleanupBlockReason()
    {
        if (!CanArmBackgroundMemoryCleanup(out string blockReason))
        {
            return blockReason;
        }

        MemoryCleanupActivitySnapshot activity =
            CaptureMemoryCleanupActivity();
        if (activity.IsWidgetInteractionActive)
        {
            return "widget-interaction";
        }

        if (activity.IsDeskPaneForeground)
        {
            return "deskpane-foreground";
        }

        if (activity.IsPointerOverDeskPane)
        {
            return "pointer-over-deskpane";
        }

        return "unknown";
    }

    private Task<bool> RunBackgroundSoftMemoryCleanupAsync(
        int generation,
        int hiddenDelaySeconds,
        string cleanupScope,
        BackgroundMemoryCleanupOutcomeState state)
    {
        if (generation != Volatile.Read(ref s_backgroundMemoryCleanupGeneration) ||
            !CanRunBackgroundMemoryCleanup())
        {
            return Task.FromResult(false);
        }

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        long workingSetBefore = process.WorkingSet64;
        long privateBytesBefore = process.PrivateMemorySize64;
        long managedHeapBefore = GC.GetGCMemoryInfo().HeapSizeBytes;

        Localized.PruneDeadTargets();
        IconHelper.IdleIconCacheReleaseResult cacheRelease =
            string.Equals(
                cleanupScope,
                PerformanceSettingsPolicy.HiddenCacheCleanupScopeWarm,
                StringComparison.Ordinal)
                ? IconHelper.ReleaseIdleCaches(
                    TimeSpan.FromSeconds(Math.Max(1, hiddenDelaySeconds)))
                : IconHelper.ReleaseHiddenCaches();
        int releasedShellKindEntries = 0;
        int releasedShortcutMetadataEntries = 0;
        if (string.Equals(
                cleanupScope,
                PerformanceSettingsPolicy.HiddenCacheCleanupScopeAllRecreatable,
                StringComparison.Ordinal))
        {
            releasedShellKindEntries = FileService.ReleaseHiddenShellKindCache();
            releasedShortcutMetadataEntries =
                ShortcutHelper.ReleaseHiddenMetadataCache();
        }

        bool releasedAnything =
            cacheRelease.ReleasedAnything ||
            releasedShellKindEntries > 0 ||
            releasedShortcutMetadataEntries > 0;
        state.SoftCleanupResult = releasedAnything ? "released" : "no-op";
        state.ReleasedShellKindEntries += releasedShellKindEntries;
        state.ReleasedShortcutMetadataEntries += releasedShortcutMetadataEntries;
        state.ReleasedThumbnailEntries += cacheRelease.ReleasedThumbnails;
        state.ReleasedDecodedBitmapEntries +=
            cacheRelease.ReleasedDecodedBitmaps;
        state.ReleasedIconByteEntries += cacheRelease.ReleasedIconByteEntries;
        state.ReleasedEstimatedBytes += cacheRelease.ReleasedEstimatedBytes;

        if (generation != Volatile.Read(ref s_backgroundMemoryCleanupGeneration) ||
            !CanRunBackgroundMemoryCleanup())
        {
            LogVerbose(
                "[Memory] Background soft cleanup completed while UI activity resumed");
        }

        process.Refresh();
        long managedHeapAfter = GC.GetGCMemoryInfo().HeapSizeBytes;
        Log(
            $"[Memory] Background soft cleanup completed hiddenSeconds={hiddenDelaySeconds} " +
            $"workingSetBeforeMB={workingSetBefore / (1024.0 * 1024):F1} " +
            $"workingSetAfterMB={process.WorkingSet64 / (1024.0 * 1024):F1} " +
            $"privateBeforeMB={privateBytesBefore / (1024.0 * 1024):F1} " +
            $"privateAfterMB={process.PrivateMemorySize64 / (1024.0 * 1024):F1} " +
            $"managedHeapBeforeMB={managedHeapBefore / (1024.0 * 1024):F1} " +
            $"managedHeapAfterMB={managedHeapAfter / (1024.0 * 1024):F1} " +
            $"cleanupScope={cleanupScope} result={state.SoftCleanupResult} " +
            $"forcedCollection=false trimmed=false warmCachePreserved=true " +
            $"releasedThumbs={cacheRelease.ReleasedThumbnails} " +
            $"releasedBitmaps={cacheRelease.ReleasedDecodedBitmaps} " +
            $"releasedIconBytes={cacheRelease.ReleasedIconByteEntries} " +
            $"releasedShellKindEntries={releasedShellKindEntries} " +
            $"releasedShortcutMetadataEntries={releasedShortcutMetadataEntries} " +
            $"releasedEstimatedMB={cacheRelease.ReleasedEstimatedBytes / (1024.0 * 1024):F1}");
        return Task.FromResult(true);
    }

    private async Task<bool> RunBackgroundDeepMemoryCleanupAsync(
        int generation,
        string triggerReason,
        BackgroundMemoryCleanupOutcomeState state)
    {
        if (generation !=
                Volatile.Read(ref s_backgroundMemoryCleanupGeneration) ||
            !CanRunBackgroundMemoryCleanup())
        {
            return false;
        }

        MemoryCleanupDiagnosticSnapshot before =
            CaptureMemoryCleanupDiagnosticSnapshot();
        MemoryReclaimResult reclaimResult = await Task.Run(
            () => MemoryReclaimer.TryCollect(
                $"background:{triggerReason}"));
        state.DeepReclaimResult = reclaimResult.Status.ToString();
        state.DeepReclaimExecuted = reclaimResult.Executed;
        state.DeepReclaimDurationMilliseconds =
            reclaimResult.DurationMilliseconds;
        state.DeepReclaimReleasedHeapBytes =
            reclaimResult.ReleasedHeapBytes;

        // The pre-1.4.5 idle trim: page everything out once the user is fully
        // away. This stage only runs after every widget is hidden and all
        // activity gates cleared, so no visible surface can jitter from the
        // back-faults. Advancing the epoch lets widgets re-prime warmed layout
        // state lazily when they come back.
        bool workingSetTrimmed = false;
        if (reclaimResult.Executed &&
            SettingsService.Settings.IdleWorkingSetTrimEnabled &&
            !(SettingsService.Settings.ImmediateHiddenWorkingSetTrimEnabled &&
              _immediateHiddenWorkingSetTrimTracker.TrimmedCurrentHiddenSession))
        {
            workingSetTrimmed = Win32Helper.TrimWorkingSet();
            CompleteWorkingSetTrim(workingSetTrimmed, triggerReason);
        }

        MemoryCleanupDiagnosticSnapshot after =
            CaptureMemoryCleanupDiagnosticSnapshot();
        Log(
            $"[Memory] Deep finalizer cleanup completed " +
            $"status={reclaimResult.Status} " +
            $"durationMs={reclaimResult.DurationMilliseconds} " +
            $"collections={reclaimResult.CollectionsBefore}->" +
            $"{reclaimResult.CollectionsAfter} " +
            $"gcHeapBeforeMB={before.ManagedHeapBytes / (1024.0 * 1024):F1} " +
            $"gcHeapAfterMB={after.ManagedHeapBytes / (1024.0 * 1024):F1} " +
            $"privateBeforeMB={before.PrivateBytes / (1024.0 * 1024):F1} " +
            $"privateAfterMB={after.PrivateBytes / (1024.0 * 1024):F1} " +
            $"workingSetBeforeMB={before.WorkingSetBytes / (1024.0 * 1024):F1} " +
            $"workingSetAfterMB={after.WorkingSetBytes / (1024.0 * 1024):F1} " +
            $"releasedHeapMB={reclaimResult.ReleasedHeapBytes / (1024.0 * 1024):F1} " +
            $"reclaimPrivateBeforeMB={reclaimResult.PrivateBeforeBytes / (1024.0 * 1024):F1} " +
            $"reclaimPrivateAfterMB={reclaimResult.PrivateAfterBytes / (1024.0 * 1024):F1} " +
            $"reason={triggerReason} " +
            $"workingSetTrimmed={workingSetTrimmed} fullViewRebuilds=0");
        PerformanceLogger.Mark(
            "BackgroundMemoryDeepReclaimCompleted",
            $"status={reclaimResult.Status} " +
            $"durationMs={reclaimResult.DurationMilliseconds} " +
            $"releasedHeapMB={reclaimResult.ReleasedHeapBytes / (1024.0 * 1024):F1} " +
            $"releasedPrivateMB={reclaimResult.ReleasedPrivateBytes / (1024.0 * 1024):F1} " +
            $"reason={triggerReason}");

        // A cooldown or in-progress veto must not consume the deep stage: the
        // scheduler used to treat it as completed, so the finalizer collection
        // never actually ran for that hidden session. Returning false makes
        // the pipeline back off and retry until the cooldown window elapses.
        return reclaimResult.Status is not (MemoryReclaimStatus.SkippedCooldown
            or MemoryReclaimStatus.SkippedInProgress);
    }

    private bool CanRunDeepMemoryCleanup(out string blockReason)
    {
        MemoryCleanupActivitySnapshot activity =
            CaptureMemoryCleanupActivity();
        if (!MemoryCleanupPolicy.IsDeepCleanupCandidate(activity))
        {
            if (activity.IsSettingsOpen)
            {
                blockReason = "settings-open";
            }
            else if (activity.IsOnboardingOpen)
            {
                blockReason = "onboarding-open";
            }
            else if (activity.IsSearchPopupVisible)
            {
                blockReason = "search-popup-visible";
            }
            else if (activity.IsDesktopOrganizationOpen)
            {
                blockReason = "desktop-organization-open";
            }
            else if (activity.IsWidgetInteractionActive)
            {
                blockReason = "widget-interaction";
            }
            else if (activity.IsDeskPaneForeground)
            {
                blockReason = "deskpane-foreground";
            }
            else if (activity.IsPointerOverDeskPane)
            {
                blockReason = "pointer-over-deskpane";
            }
            else
            {
                blockReason = "activity";
            }

            return false;
        }

        blockReason = string.Empty;
        return true;
    }

    private async Task RunHeavyOperationDeepMemoryCleanupAsync(
        int generation,
        string reason,
        int? requiredBackgroundGeneration)
    {
        if (generation != Volatile.Read(ref s_lightMemoryCleanupGeneration))
        {
            return;
        }

        if (requiredBackgroundGeneration is int requiredGeneration &&
            requiredGeneration !=
                Volatile.Read(ref s_backgroundMemoryCleanupGeneration))
        {
            PerformanceLogger.Mark(
                "DeepMemoryReclaimSkipped",
                $"reason=background-generation-changed trigger={reason}");
            return;
        }

        if (!CanRunDeepMemoryCleanup(out string blockReason))
        {
            LogVerbose(
                $"[Memory] Deep finalizer cleanup deferred " +
                $"reason={reason} blockedBy={blockReason}");
            PerformanceLogger.Mark(
                "DeepMemoryReclaimDeferred",
                $"reason={reason} blockedBy={blockReason}");
            return;
        }

        MemoryCleanupDiagnosticSnapshot before =
            CaptureMemoryCleanupDiagnosticSnapshot();
        MemoryReclaimResult reclaimResult = await Task.Run(
            () => MemoryReclaimer.TryCollect($"heavy-operation:{reason}"));
        if (generation != Volatile.Read(ref s_lightMemoryCleanupGeneration))
        {
            return;
        }

        MemoryCleanupDiagnosticSnapshot after =
            CaptureMemoryCleanupDiagnosticSnapshot();
        Log(
            $"[Memory] Heavy-operation deep cleanup completed " +
            $"status={reclaimResult.Status} " +
            $"durationMs={reclaimResult.DurationMilliseconds} " +
            $"collections={reclaimResult.CollectionsBefore}->" +
            $"{reclaimResult.CollectionsAfter} " +
            $"privateBeforeMB={before.PrivateBytes / (1024.0 * 1024):F1} " +
            $"privateAfterMB={after.PrivateBytes / (1024.0 * 1024):F1} " +
            $"workingSetBeforeMB={before.WorkingSetBytes / (1024.0 * 1024):F1} " +
            $"workingSetAfterMB={after.WorkingSetBytes / (1024.0 * 1024):F1} " +
            $"releasedHeapMB={reclaimResult.ReleasedHeapBytes / (1024.0 * 1024):F1} " +
            $"releasedPrivateMB={reclaimResult.ReleasedPrivateBytes / (1024.0 * 1024):F1} " +
            $"reason={reason} workingSetTrimmed=false fullViewRebuilds=0");
        PerformanceLogger.Mark(
            "HeavyOperationDeepMemoryCleanupCompleted",
            $"status={reclaimResult.Status} " +
            $"durationMs={reclaimResult.DurationMilliseconds} " +
            $"releasedHeapMB={reclaimResult.ReleasedHeapBytes / (1024.0 * 1024):F1} " +
            $"releasedPrivateMB={reclaimResult.ReleasedPrivateBytes / (1024.0 * 1024):F1} " +
            $"reason={reason}");
    }

    internal static void ScheduleLightMemoryCleanup(
        bool completedHeavyOperation = false,
        int? requiredBackgroundGeneration = null)
    {
        if (IsShuttingDown) return;
        int generation = Interlocked.Increment(ref s_lightMemoryCleanupGeneration);
        App app = Current;
        var dispatcherQueue = App.UiDispatcherQueue;
        bool enqueued = dispatcherQueue?.TryEnqueue(async () =>
        {
            try
            {
                await Task.Delay(2000);
                if (generation != Volatile.Read(ref s_lightMemoryCleanupGeneration))
                {
                    return;
                }

                if (requiredBackgroundGeneration is int requiredGeneration &&
                    requiredGeneration !=
                        Volatile.Read(ref s_backgroundMemoryCleanupGeneration))
                {
                    PerformanceLogger.Mark(
                        "BackgroundMemoryCleanupCancelled",
                        "reason=widgets-restored");
                    return;
                }

                if (completedHeavyOperation)
                {
                    await app.RunHeavyOperationDeepMemoryCleanupAsync(
                        generation,
                        reason: "completed-heavy-operation",
                        requiredBackgroundGeneration: requiredBackgroundGeneration);
                }

                Localized.PruneDeadTargets();
                PerformanceLogger.SampleMemory("light-cleanup-completed");
                LogVerbose(
                    "[Memory] Delayed maintenance completed " +
                    $"deepReclaimRequested={completedHeavyOperation} " +
                    "workingSetTrimmed=false heapCompaction=false");
            }
            catch (Exception ex)
            {
                Log($"[Memory] Delayed cleanup failed: {ex}");
            }
        }) == true;
        if (!enqueued)
        {
            Log(
                $"[Memory] Delayed cleanup scheduling failed " +
                $"generation={generation} error=dispatcher-unavailable");
        }
    }

    public async Task ShutdownForRestartAsync()
    {
        Log("ShutdownForRestartAsync invoked");
        await ShutdownApplicationAsync();
    }

    private async void ExitApplication()
    {
        Log("ExitApplication invoked");
        await ShutdownApplicationAsync();
    }

    /// <summary>
    /// Delayed termination that guarantees the process dies even when
    /// Application.Exit fails to unwind the XAML loop. Stage one is the
    /// graceful Environment.Exit. It never returns, and under CoreCLR it
    /// waits forever for a finalizer thread that a cross-apartment WinRT
    /// release can block, so stage two is armed first on its own thread and
    /// hard-terminates. Both are background threads, so neither keeps a
    /// healthy shutdown alive; the log drain mirrors the startup fatal path
    /// so the exit tail is not swallowed.
    /// </summary>
    private static void ArmShutdownExitBackstop(TimeSpan delay)
    {
        var hardStop = new Thread(() =>
        {
            Thread.Sleep(delay + ShutdownHardTerminateGrace);
            Log("[Shutdown] Exit backstop did not complete; terminating the process.");
            DrainLogQueue();
            Win32Helper.TerminateCurrentProcess(0);
        })
        {
            IsBackground = true,
            Name = "DeskPane shutdown hard stop"
        };
        hardStop.Start();

        _ = Task.Run(async () =>
        {
            await Task.Delay(delay);
            Log($"[Shutdown] Exit backstop fired: the XAML loop was still alive {delay.TotalSeconds:F0}s after Exit().");
            DrainLogQueue();
            Environment.Exit(0);
        });
    }

    private async Task ShutdownApplicationAsync()
    {
        // Recorded first so a death anywhere inside teardown still reads as
        // an intentional exit on the next launch (storm-guard semantics).
        _startupSessionLedger?.MarkCleanExit();
        bool dependentTeardownCompleted = true;
        try
        {
            dependentTeardownCompleted = await ShutdownCoreAsync();
        }
        catch (Exception ex)
        {
            Log($"[Shutdown] Teardown failed: {ex}");
        }
        finally
        {
            // Teardown must never leave a tray-less process holding the
            // single-instance mutex, so the exit is unconditional.
            // Keep the tray's dispatcher host alive until the asynchronous
            // shutdown sequence and all of its continuations have completed.
            try
            {
                if (!dependentTeardownCompleted)
                {
                    // Keep dependent widget windows and services alive, but
                    // close the tray dispatcher host so the process can exit.
                    Log("[Shutdown] Dependent teardown skipped after a deadline.");
                }
                _trayWindow?.Close();
                _trayWindow = null;
            }
            catch (Exception ex) { Log($"[Shutdown] Tray window close failed: {ex}"); }
            finally
            {
                // The watchdog path guarantees process death within seconds;
                // releasing the mutex there would open a window where a new
                // instance starts while this process may still be writing.
                // Let process death release it instead. The throw path
                // finishes its writes before Exit(), so the manual release
                // must remain unconditional there to never leave a pumping
                // process holding the mutex.
                if (dependentTeardownCompleted)
                {
                    try
                    {
                        try { _singleInstanceMutex?.ReleaseMutex(); }
                        catch (ApplicationException) { }
                        _singleInstanceMutex?.Dispose();
                        _singleInstanceMutex = null;
                    }
                    catch (Exception ex) { Log($"[Shutdown] Mutex release failed: {ex}"); }
                }
                try { DrainLogQueue(); }
                finally
                {
                    // Application.Exit only requests the XAML loop to stop.
                    // When teardown was skipped, its pump can stay alive after
                    // Exit returns (measured: the deadline probe pumped
                    // forever); even a completed teardown can leave dispatcher
                    // work that keeps re-queueing itself, so the UI thread
                    // spins and the loop never drains — a silent zombie with
                    // tray gone, mutex released, process resident and pinning
                    // the thumbnail proxy's stdin pipe (feedback 555/556,
                    // #530/#532). Arm the hard exit on every path; a healthy
                    // Exit() outruns the timer and the delayed call never fires.
                    ArmShutdownExitBackstop(TimeSpan.FromSeconds(
                        dependentTeardownCompleted ? 5 : 3));
                    if (dependentTeardownCompleted &&
                        AppRelaunchService.PendingOsRestartOnShutdown &&
                        DistributionService.IsMicrosoftStore)
                    {
                        // Store builds have no detached updater helper; the
                        // WinAppSDK restart agent terminates this process on
                        // success and never returns. On failure the pending
                        // restore marker on disk still applies on next launch.
                        try
                        {
                            Microsoft.Windows.AppLifecycle.AppInstance.Restart(
                                string.Empty);
                        }
                        catch (Exception restartEx)
                        {
                            Log($"[Shutdown] Store relaunch failed: {restartEx}");
                        }
                        AppRelaunchService.PendingOsRestartOnShutdown = false;
                    }
                    Exit();
                }
            }
        }
    }

    private async Task<bool> ShutdownCoreAsync()
    {
        Interlocked.Exchange(ref s_shutdownRequested, 1);
        // The canonical shutdown moment for UI dispatch: drop pending
        // deferrals and refuse further dispatch so nothing replays post-exit.
        DeskPane.Platform.UiDispatch.MarkShutdown();
        TimeSpan shutdownGrace = TimeSpan.FromSeconds(15);
        return await _shutdownSequence.RunAsync(
            ShutdownStep.Sync("backup-subscriptions", () =>
            {
                CloudBackupService.BackupRunCompleted -= OnCloudBackupRunCompleted;
                DataBackupService.AutomaticSnapshotFallbackDetected -= OnAutomaticBackupFallbackDetected;
            }),
            ShutdownStep.Sync("memory-maintenance", () =>
            {
                StopVisibleIdleMemoryMaintenance();
                StopQuiescenceWorkingSetTrim();
                CancelBackgroundMemoryCleanup();
                Interlocked.Increment(ref s_lightMemoryCleanupGeneration);
            }),
            // Observers stop before windows, so topology callbacks cannot
            // access partially closed surfaces. Each owner has its own step.
            ShutdownStep.Sync("display-watcher", () => { _displayAreaWatcher?.Dispose(); _displayAreaWatcher = null; }),
            ShutdownStep.Sync("display-transition", () => { _displayTopologyTransitionCoordinator?.Dispose(); _displayTopologyTransitionCoordinator = null; }),
            ShutdownStep.Sync("lifecycle-watcher", () => { _lifecycleRecoveryWatcher?.Dispose(); _lifecycleRecoveryWatcher = null; }),
            ShutdownStep.Sync("hook-watchdog", () => { _hookHealthWatchdog?.Dispose(); _hookHealthWatchdog = null; }),
            ShutdownStep.Sync("diagnostics", () => { _diagnosticsService?.Dispose(); _diagnosticsService = null; }),
            ShutdownStep.Sync("desktop-organization-watcher", () =>
            {
                if (DesktopAutoOrganizationWatcher is not null)
                {
                    DesktopAutoOrganizationWatcher.ItemOrganized -= ShowDesktopAutoOrganizationNotification;
                    DesktopAutoOrganizationWatcher.Dispose();
                    DesktopAutoOrganizationWatcher = null;
                }
            }),
            ShutdownStep.Sync("desktop-activation", () => { DesktopDoubleClickActivationService?.Dispose(); DesktopDoubleClickActivationService = null; }),
            ShutdownStep.Sync("notifications", () => { _nativeNotificationService?.Dispose(); _nativeNotificationService = null; }),
            // Hooks must be removed while their owning tray window still exists.
            ShutdownStep.Sync("global-hotkey", () => { GlobalHotkeyService?.Dispose(); GlobalHotkeyService = null; }),
            ShutdownStep.Sync("widgets", () =>
            {
                WidgetManager?.CloseAll();
                if (DesktopIconVisibilityService.AreDesktopIconsVisible() == false)
                {
                    DesktopIconVisibilityService.SetDesktopIconsVisible(true);
                }
            }),
            ShutdownStep.Sync("desktop-organization-window", () => { _desktopOrganizationWindow?.CloseForShutdown(); _desktopOrganizationWindow = null; }),
            ShutdownStep.Sync("recovery-offer-window", () => { _recoveryOfferWindow?.Close(); _recoveryOfferWindow = null; }),
            ShutdownStep.Sync("elevation-notice-window", () => { _elevationNoticeWindow?.Close(); _elevationNoticeWindow = null; }),
            // Closing a surface may commit its final edit/order. Flush after
            // those consumers are closed, while persistence services still
            // live. Deep review R15 (F1 audit): this was the ONLY unbounded
            // IO teardown step — FlushPendingSaveAsync first awaits the
            // per-directory FileWriteLock, which an in-flight debounced save
            // stuck on a bad disk could hold forever; with no deadline the
            // exit backstop (armed only after ShutdownCoreAsync returns)
            // never armed, leaving the exact zombie shape the backstop was
            // built to prevent. Same semantics as the backup step: bounded,
            // no abort — the write itself is IO-only and later teardown
            // cannot race it into a disposed state.
            ShutdownStep.Bounded("settings-flush", async () =>
            {
                if (!await SettingsService.FlushPendingSaveAsync(notifySubscribers: false))
                    throw new IOException("Final settings flush failed.");
            }, shutdownGrace),
            ShutdownStep.Sync("app-subscriptions", () =>
            {
                SettingsService.PersistenceFailed -= OnSettingsPersistenceFailed;
                SettingsService.SettingsChanged -= OnMaterialCapabilitySettingsChanged;
                LocalizationService.LanguageChanged -= OnLanguageChanged;
            }),
            ShutdownStep.Sync("tray-icon", () => { _trayIcon?.Dispose(); _trayIcon = null; }),
            ShutdownStep.Sync("activation-registration", () => { _activationRegistration?.Unregister(null); _activationRegistration = null; }),
            ShutdownStep.Sync("activation-event", () => { _activationEvent?.Dispose(); _activationEvent = null; }),
            // The currently registered disposables are synchronous; container
            // disposal stays on the UI thread for ThemeService's WinRT objects.
            ShutdownStep.Sync("service-container", () => Services.Dispose()),
            ShutdownStep.Sync("log-drain", DrainLogQueue),
            ShutdownStep.Sync("single-instance", () =>
            {
                try { _singleInstanceMutex?.ReleaseMutex(); }
                catch (ApplicationException) { }
                _singleInstanceMutex?.Dispose();
                _singleInstanceMutex = null;
            }));
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log($"Unhandled exception: {e.Exception}");
        // Handled first: the runtime must not tear the process down while the
        // fatal path reports the failure to the user.
        e.Handled = true;

        if (!IsStartupLifelineEstablished)
        {
            // Before the tray exists the process has nothing to fall back on,
            // and swallowing the exception would leave it holding the
            // single-instance mutex without any UI.
            FailStartup("unhandled exception before the startup lifeline was established", e.Exception);
        }
    }

    private IReadOnlyList<IntPtr> GetRaisedBandAuxiliaryWindowHandles()
    {
        List<IntPtr> handles = new(1);
        if (_desktopOrganizationWindow is { } organize)
        {
            handles.Add(organize.WindowHandle);
        }

        return handles;
    }

    private void ToggleTheme()
    {
        var settings = SettingsService.Settings;
        settings.Theme = settings.Theme switch
        {
            "Light" => "Dark",
            "Dark" => "Light",
            _ => "Light"
        };
        SettingsService.SaveDebounced();
        ThemeService.RefreshAppearance();
    }
}
