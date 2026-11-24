using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DeskPane.Core.Persistence;
using DeskPane.FileSafety;
using DeskPane.Models;

using DeskPane.Contracts;

namespace DeskPane.Services;

public sealed partial class DeskPaneDataBackupService
{
    private const int BackupSchemaVersion = 2;
    // Upper bound the wipe-recovery peek shares so its offer never proposes
    // an archive the restore validator's schema gate would refuse.
    internal const int CurrentBackupSchemaVersion = BackupSchemaVersion;
    private const int MinimumSupportedBackupSchemaVersion = 1;
    private const int MaxPreRestoreBackupCount = 5;
    private const int MaxRestoreFileCount = 100_000;
    // The widget-style document is a small projected JSON (~60 scalar
    // fields); 8 MiB is far beyond any legitimate document.
    private const long MaxWidgetStyleEntryBytes = 8L * 1024 * 1024;
    private const long MaxRestoreFileSizeBytes = 4L * 1024 * 1024 * 1024;
    private const long MaxRestoreTotalSizeBytes = 16L * 1024 * 1024 * 1024;
    private const int MaxSnapshotCopyAttempts = 4;
    private const int MaxScopedRestoreApplyAttempts = 3;
    /// <summary>
    /// <see cref="Exception.Data"/> key stamped on the InvalidDataException
    /// for an unsupported schema version so the UI can localize the
    /// failure. Carries the unsupported version number.</summary>
    public const string BackupSchemaVersionDataKey = "DeskPane.BackupSchemaVersion";
    /// <summary>Stamped on InvalidDataException when the archive lacks a usable
    /// manifest — i.e. the picked file is not a DeskPane backup at all.</summary>
    public const string BackupManifestInvalidDataKey = "DeskPane.BackupManifestInvalid";
    private static readonly SettingsJsonContext s_settingsDataJsonContext =
        new(CreateDataJsonOptions());

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _rootPath;
    private readonly string _recoveryRootPath;
    private volatile AutomaticBackupOptions _automaticBackupOptions = AutomaticBackupOptions.Default;
    private volatile string? _lastAutomaticSnapshotFallbackMessage;

    /// <summary>
    /// Current automatic-backup schedule and folder policy. Updated from live
    /// settings; the default matches the pre-setting behavior (daily, 7 kept,
    /// default recovery folder).
    /// </summary>
    public AutomaticBackupOptions AutomaticBackupOptions => _automaticBackupOptions;

    /// <summary>
    /// Set when the most recent automatic snapshot had to fall back from the
    /// configured custom directory to the default recovery directory; cleared
    /// once a snapshot succeeds in the configured directory again.
    /// </summary>
    public string? LastAutomaticSnapshotFallbackMessage => _lastAutomaticSnapshotFallbackMessage;

    /// <summary>
    /// Raised once per fallback streak when an automatic snapshot falls back
    /// from the configured custom directory to the default recovery directory.
    /// </summary>
    public event Action? AutomaticSnapshotFallbackDetected;

    public void UpdateAutomaticBackupOptions(AutomaticBackupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _automaticBackupOptions = options;
        if (options.CustomDirectory is null)
        {
            _lastAutomaticSnapshotFallbackMessage = null;
        }
    }

    public DeskPaneDataBackupService()
        : this(
            DeskPaneDataPathService.Current.RootPath,
            DeskPaneDataPathService.Current.RecoveryDirectory)
    {
    }

    internal DeskPaneDataBackupService(string rootPath, string? recoveryRootPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _recoveryRootPath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(recoveryRootPath)
                ? Path.Combine(Path.GetDirectoryName(_rootPath) ?? _rootPath, "DeskPane-Recovery")
                : recoveryRootPath);
    }

    private Func<bool>? _isRecoveryOfferPending;

    /// <summary>
    /// Wires the wipe-recovery pending probe. While a recovery decision is
    /// pending (its marker exists), scheduled automatic snapshots stay paused:
    /// every snapshot of the fresh-default profile would occupy a retention
    /// slot and evict the pre-wipe snapshots the recovery offer exists to
    /// serve. Manual "back up now" (forced) snapshots stay available — the
    /// user is already looking at the backup page.
    /// </summary>
    public void SetRecoveryOfferPauseProbe(Func<bool>? isPending) => _isRecoveryOfferPending = isPending;

    private Func<string?>? _recoverySnapshotRetentionPin;

    /// <summary>
    /// Wires the pending-recovery snapshot pin. While a wipe-recovery
    /// decision is pending, the marker-referenced archive is exempt from
    /// retention pruning (it does not consume a retention slot), so manual
    /// "back up now" runs during the pending window cannot evict the
    /// snapshot the offer restores from. The pin ends with the marker, the
    /// same lifecycle <see cref="PrunePreRestoreBackups"/> gives its
    /// safety-archive pin.
    /// </summary>
    public void SetRecoverySnapshotRetentionPin(Func<string?>? pinProvider) =>
        _recoverySnapshotRetentionPin = pinProvider;

    private bool IsRecoveryOfferPauseActive =>
        _isRecoveryOfferPending?.Invoke() == true;

    internal string DataDirectory => Path.Combine(_rootPath, "data");

    /// <summary>
    /// Automatic snapshots are retained outside the app-data root so they
    /// survive a normal direct-install uninstall and can be restored after
    /// reinstall. On the store channel, MSIX AppData write virtualization
    /// places them under Packages/[PFN]/LocalCache instead: they are
    /// deleted with the package on uninstall but kept across updates over
    /// the same PackageFamilyName.
    /// </summary>
    internal string AutomaticSnapshotDirectory => Path.Combine(_recoveryRootPath, "automatic");

    /// <summary>
    /// Directory the next automatic snapshot will be written to: the
    /// configured custom directory when it is usable, otherwise the default
    /// recovery directory. Only the directory shape is validated here; write
    /// access is probed when a snapshot is actually created.
    /// </summary>
    public string EffectiveAutomaticSnapshotDirectory =>
        ResolveAutomaticSnapshotTarget(probeWriteAccess: false).Directory;

    /// <summary>
    /// Folder status for the settings UI: which custom directory is configured
    /// and whether it would be used right now. Missing directories are still
    /// reported as active because they are created on demand.
    /// </summary>
    public AutomaticBackupDirectoryStatus GetAutomaticBackupDirectoryStatus()
    {
        AutomaticSnapshotTarget target = ResolveAutomaticSnapshotTarget(probeWriteAccess: false);
        return new AutomaticBackupDirectoryStatus(
            _automaticBackupOptions.CustomDirectory,
            target.Directory,
            target.Directory != AutomaticSnapshotDirectory);
    }

    /// <summary>
    /// Validates a user-selected folder for automatic snapshots. Folders inside
    /// the app-data root are rejected because snapshots would then back up the
    /// recovery copies of previous snapshots.
    /// </summary>
    public bool IsValidCustomAutomaticBackupDirectory(string path, out string? rejectionReasonKey)
    {
        rejectionReasonKey = null;
        try
        {
            string fullPath = Path.GetFullPath(path);
            if (IsPathInsideDirectory(fullPath, _rootPath))
            {
                rejectionReasonKey = "Settings.DataBackup.AutomaticBackupDirectory.InvalidInsideDataRoot";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            rejectionReasonKey = "Settings.DataBackup.AutomaticBackupDirectory.InvalidPath";
            return false;
        }
    }

    private readonly record struct AutomaticSnapshotTarget(string Directory, bool UsedFallback);

    private AutomaticSnapshotTarget ResolveAutomaticSnapshotTarget(bool probeWriteAccess)
    {
        string? customDirectory = _automaticBackupOptions.CustomDirectory;
        if (string.IsNullOrWhiteSpace(customDirectory))
        {
            return new AutomaticSnapshotTarget(AutomaticSnapshotDirectory, UsedFallback: false);
        }

        try
        {
            string fullPath = Path.GetFullPath(customDirectory.Trim());
            if (IsPathInsideDirectory(fullPath, _rootPath))
            {
                App.Log(
                    $"[DataBackup] Custom backup directory '{fullPath}' is inside the DeskPane data root; " +
                    "using the default recovery directory instead.");
                return new AutomaticSnapshotTarget(AutomaticSnapshotDirectory, UsedFallback: true);
            }

            // A file occupying the path can never become the snapshot directory.
            if (File.Exists(fullPath))
            {
                App.Log(
                    $"[DataBackup] Custom backup directory '{fullPath}' is a file; " +
                    "using the default recovery directory instead.");
                return new AutomaticSnapshotTarget(AutomaticSnapshotDirectory, UsedFallback: true);
            }

            if (probeWriteAccess)
            {
                Directory.CreateDirectory(fullPath);
                // Probe write access so read-only locations fall back to the
                // default directory instead of failing every snapshot attempt.
                string probePath = Path.Combine(
                    fullPath,
                    $".deskpane-backup-probe-{Guid.NewGuid():N}");
                File.WriteAllText(probePath, string.Empty);
                TryDeleteFile(probePath);
            }

            return new AutomaticSnapshotTarget(fullPath, UsedFallback: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            App.Log(
                $"[DataBackup] Custom backup directory '{customDirectory}' is not usable ({ex.Message}); " +
                "using the default recovery directory instead.");
            return new AutomaticSnapshotTarget(AutomaticSnapshotDirectory, UsedFallback: true);
        }
    }

    // Keep snapshots written by builds released before recovery isolation
    // visible and restorable during the transition.
    internal string LegacyAutomaticSnapshotDirectory => Path.Combine(_rootPath, "backups", "automatic");

    internal string PreRestoreBackupDirectory => Path.Combine(_rootPath, "backups", "pre-restore");

    internal string RestoreStagingDirectory => Path.Combine(_rootPath, "restore-staging");

    internal string BackupSnapshotStagingDirectory => Path.Combine(_rootPath, "backup-staging");

    internal string PendingRestoreMarkerPath => Path.Combine(_rootPath, "restore-pending.json");

    public async Task<string?> CreateAutomaticSnapshotIfDueAsync(
        CancellationToken cancellationToken = default)
    {
        return await CreateAutomaticSnapshotAsync(force: false, cancellationToken);
    }

    public async Task<string?> CreateAutomaticSnapshotNowAsync(
        CancellationToken cancellationToken = default)
    {
        return await CreateAutomaticSnapshotAsync(force: true, cancellationToken);
    }

    private async Task<string?> CreateAutomaticSnapshotAsync(
        bool force,
        CancellationToken cancellationToken)
    {
        return (await CreateAutomaticSnapshotResultAsync(force, cancellationToken)).ArchivePath;
    }

    public async Task<LocalBackupResult> CreateAutomaticSnapshotResultAsync(
        bool force,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!force && IsRecoveryOfferPauseActive)
            {
                App.Log("[DataBackup] Skipped the scheduled snapshot: a recovery decision is pending; " +
                        "automatic snapshots stay paused until the user answers the recovery offer.");
                return new(BackupOutcome.Skipped);
            }

            AutomaticBackupOptions options = _automaticBackupOptions;
            // "Back up now" (force) stays available even when the schedule is off.
            if (!options.IsEnabled && !force)
            {
                return new(BackupOutcome.Skipped);
            }

            if (!HasBackupSourceData())
            {
                return new(BackupOutcome.Skipped);
            }

            AutomaticSnapshotTarget target = ResolveAutomaticSnapshotTarget(probeWriteAccess: true);
            Directory.CreateDirectory(target.Directory);
            string? latestSnapshot = Directory
                .EnumerateFiles(target.Directory, "DeskPane-Auto-*.zip")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (!force && latestSnapshot is not null &&
                DateTime.UtcNow - File.GetLastWriteTimeUtc(latestSnapshot) <
                    TimeSpan.FromMinutes(Math.Max(1, options.IntervalMinutes)))
            {
                return new(BackupOutcome.Skipped);
            }

            string snapshotPath = GetAvailableArchivePath(
                target.Directory,
                $"DeskPane-Auto-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            await CreateArchiveCoreAsync(snapshotPath, "automatic", cancellationToken);
            // Deep review R15 (F7 audit): the archive is durable at this
            // point — a retention-prune or directory enumeration throwing
            // (custom directory on a slow network share) used to report the
            // WHOLE run as Failed and skip the fallback-status bookkeeping,
            // even though the snapshot itself was fine. Prune and the
            // fallback bookkeeping now degrade to log lines.
            try
            {
                PruneAutomaticSnapshots(
                    target.Directory,
                    options.RetentionCount,
                    _recoverySnapshotRetentionPin?.Invoke());
            }
            catch (Exception pruneEx)
            {
                App.Log($"[DataBackup] Automatic snapshot retention prune failed (snapshot kept): {pruneEx}");
            }
            if (target.UsedFallback)
            {
                string fallbackMessage =
                    $"Custom backup directory '{options.CustomDirectory}' was unavailable; " +
                    $"snapshot saved to '{target.Directory}'.";
                bool firstFallbackInStreak = _lastAutomaticSnapshotFallbackMessage is null;
                _lastAutomaticSnapshotFallbackMessage = fallbackMessage;
                if (firstFallbackInStreak)
                {
                    AutomaticSnapshotFallbackDetected?.Invoke();
                }
            }
            else
            {
                _lastAutomaticSnapshotFallbackMessage = null;
            }

            App.Log($"[DataBackup] Created automatic snapshot '{snapshotPath}'.");
            return new(BackupOutcome.Succeeded, snapshotPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Log($"[DataBackup] Automatic snapshot failed: {ex}");
            return new(BackupOutcome.Failed, Error: ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> ExportBackupAsync(
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        destinationDirectory = Path.GetFullPath(destinationDirectory);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!HasBackupSourceData())
            {
                throw new InvalidOperationException("DeskPane data directory is empty.");
            }

            Directory.CreateDirectory(destinationDirectory);
            string backupPath = GetAvailableArchivePath(
                destinationDirectory,
                $"DeskPane-Backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            await CreateArchiveCoreAsync(backupPath, "manual", cancellationToken);
            App.Log($"[DataBackup] Exported backup '{backupPath}'.");
            return backupPath;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Domain-scoped cloud backup (roadmap §10): archives only the enabled
    /// domains' data files, plus widget-style.json when the WidgetStyle
    /// domain is on. The manifest carries the domain list so the archive
    /// can never be mistaken for a full backup.
    /// </summary>
    /// <param name="widgetStyleProvider">
    /// Supplies the widget-style document bytes when the scope includes
    /// <see cref="CloudBackupDomain.WidgetStyle"/>; the service itself does
    /// not hold a settings reference, so the caller projects live settings.
    /// </param>
    public async Task<string> ExportScopedBackupAsync(
        string destinationDirectory,
        CloudBackupDomain scope,
        Func<CancellationToken, Task<byte[]?>>? widgetStyleProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        destinationDirectory = Path.GetFullPath(destinationDirectory);
        if (scope == CloudBackupDomain.None)
        {
            throw new ArgumentException("At least one backup domain must be enabled.", nameof(scope));
        }

        await _gate.WaitAsync(cancellationToken);
        string? snapshotRoot = null;
        try
        {
            Directory.CreateDirectory(destinationDirectory);
            snapshotRoot = Path.Combine(BackupSnapshotStagingDirectory, $"scoped-{Guid.NewGuid():N}");
            string snapshotDataDirectory = Path.Combine(snapshotRoot, "data");
            Directory.CreateDirectory(snapshotDataDirectory);

            await CreateScopedDataSnapshotAsync(snapshotDataDirectory, scope, cancellationToken);

            IReadOnlyDictionary<string, byte[]>? rootEntries = null;
            if (scope.HasFlag(CloudBackupDomain.WidgetStyle) && widgetStyleProvider is not null)
            {
                byte[]? styleDocument = await widgetStyleProvider(cancellationToken);
                if (styleDocument is { Length: > 0 })
                {
                    rootEntries = new Dictionary<string, byte[]>
                    {
                        [CloudBackupDomains.WidgetStyleEntryName] = styleDocument
                    };
                }
            }

            string backupPath = GetAvailableArchivePath(
                destinationDirectory,
                $"DeskPane-CloudBackup-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            // The manifest must only claim domains that actually shipped —
            // WidgetStyle without a produced document does not count.
            CloudBackupDomain shippedScope = rootEntries is null
                ? scope & ~CloudBackupDomain.WidgetStyle
                : scope;
            if (shippedScope == CloudBackupDomain.None)
            {
                throw new InvalidOperationException(
                    "The scoped backup would be empty — no domain produced content.");
            }
            await CreateArchiveFromSnapshotAsync(
                backupPath,
                CloudBackupDomains.BackupKind,
                snapshotDataDirectory,
                cancellationToken,
                CloudBackupDomains.ToManifestNames(shippedScope),
                rootEntries,
                DeviceIdentity.Id);
            App.Log($"[DataBackup] Exported scoped backup '{backupPath}' (domains: {shippedScope}).");
            return backupPath;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(snapshotRoot))
            {
                TryDeleteDirectory(snapshotRoot);
            }

            _gate.Release();
        }
    }

    /// <summary>
    /// Copies only the enabled file domains' paths into the snapshot
    /// staging dir. No FileSafety metadata and no OperationGate barrier
    /// here: scoped domains never carry settings/history/journal, and
    /// widget stores are not transaction files — the per-file stable copy
    /// is the existing semantic.
    /// </summary>
    private async Task CreateScopedDataSnapshotAsync(
        string snapshotDataDirectory,
        CloudBackupDomain scope,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(DataDirectory))
        {
            return;
        }

        foreach (string sourcePath in Directory
                     .EnumerateFiles(DataDirectory, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = Path.GetRelativePath(DataDirectory, sourcePath)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (!CloudBackupDomains.IsInScope(scope, relativePath))
            {
                continue;
            }

            App.MarkStartupProgress();
            string destinationPath = Path.Combine(
                snapshotDataDirectory,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await CopyStableSnapshotFileAsync(sourcePath, destinationPath, cancellationToken);
        }
    }

    public async Task<DeskPaneRestorePreparation> PrepareRestoreAsync(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        archivePath = Path.GetFullPath(archivePath);
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("The selected DeskPane backup does not exist.", archivePath);
        }

        await _gate.WaitAsync(cancellationToken);
        string? stagingRoot = null;
        try
        {
            DeletePendingRestoreCore();
            stagingRoot = Path.Combine(RestoreStagingDirectory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingRoot);

            RestoreArchiveInfo archiveInfo = await ExtractAndValidateRestoreArchiveAsync(
                archivePath,
                stagingRoot,
                cancellationToken);
            if (archiveInfo.Manifest.Domains is { Count: > 0 })
            {
                // A scoped cloud backup holds only its domains — the
                // whole-directory swap below would wipe everything else.
                throw new InvalidDataException(
                    "This is a scoped cloud backup; restore it through cloud restore.");
            }

            string stagedDataDirectory = Path.Combine(stagingRoot, "data");
            // Pre-restore archives are the safety net for restores: they can
            // be created without settings.json, so they must also be
            // restorable without it. Manual/automatic snapshots stay strict.
            bool allowMissingSettings = string.Equals(
                archiveInfo.Manifest.Kind, "pre-restore", StringComparison.Ordinal);
            ValidateRestoreData(
                stagedDataDirectory,
                requireSettings: !allowMissingSettings);

            var marker = new PendingRestoreMarker(
                stagingRoot,
                archivePath,
                DateTimeOffset.UtcNow,
                archiveInfo.Manifest.CreatedAtUtc,
                archiveInfo.Manifest.AppVersion,
                Domains: null,
                AllowMissingSettings: allowMissingSettings,
                SourceDataPath: archiveInfo.Manifest.SourceDataPath);
            await WritePendingRestoreMarkerAtomicallyAsync(
                PendingRestoreMarkerPath,
                marker,
                cancellationToken);
            App.Log($"[DataBackup] Prepared restore from '{archivePath}'.");
            return new DeskPaneRestorePreparation(
                archiveInfo.Manifest.CreatedAtUtc,
                archiveInfo.Manifest.AppVersion,
                archiveInfo.FileCount,
                archiveInfo.TotalUncompressedBytes,
                archiveInfo.Manifest.SchemaVersion,
                archiveInfo.Manifest.SchemaVersion >= 2,
                IsFromNewerAppVersion: IsBackupFromNewerApp(archiveInfo.Manifest.AppVersion));
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(stagingRoot))
            {
                TryDeleteDirectory(stagingRoot);
            }

            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Scoped cloud restore (roadmap §10): stages a cloud backup and marks
    /// it pending with the domain list. Apply replaces ONLY the staged
    /// domains' files in the live data directory — every other path stays
    /// untouched, and the marker makes a mid-apply crash retry-safe.
    /// </summary>
    /// <param name="requestedDomains">
    /// Domains the user asked to restore; the applied scope is the
    /// intersection with the archive's manifest domains (an archive lacking
    /// a requested domain simply cannot provide it).
    /// </param>
    public async Task<DeskPaneRestorePreparation> PrepareScopedRestoreAsync(
        string archivePath,
        CloudBackupDomain requestedDomains,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        archivePath = Path.GetFullPath(archivePath);
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("The selected DeskPane backup does not exist.", archivePath);
        }

        await _gate.WaitAsync(cancellationToken);
        string? stagingRoot = null;
        try
        {
            DeletePendingRestoreCore();
            stagingRoot = Path.Combine(RestoreStagingDirectory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingRoot);

            RestoreArchiveInfo archiveInfo = await ExtractAndValidateRestoreArchiveAsync(
                archivePath,
                stagingRoot,
                cancellationToken);

            CloudBackupDomain manifestScope = CloudBackupDomains.FromManifestNames(
                archiveInfo.Manifest.Domains);
            if (manifestScope == CloudBackupDomain.None)
            {
                throw new InvalidDataException(
                    "This is not a scoped cloud backup; use the full restore flow.");
            }

            CloudBackupDomain appliedScope = manifestScope & requestedDomains;
            if (appliedScope == CloudBackupDomain.None)
            {
                throw new InvalidDataException(
                    "The backup does not contain any of the requested domains.");
            }

            string stagedDataDirectory = Path.Combine(stagingRoot, "data");
            if (Directory.Exists(stagedDataDirectory))
            {
                // The archive must not carry files outside its own manifest
                // domains — a forged manifest could otherwise make the
                // scoped apply overwrite non-domain data. Files from a
                // manifest domain the user did not request are staged but
                // simply not applied.
                foreach (string stagedFile in Directory.EnumerateFiles(
                             stagedDataDirectory, "*", SearchOption.AllDirectories))
                {
                    string stagedRelative = Path
                        .GetRelativePath(stagedDataDirectory, stagedFile)
                        .Replace(Path.DirectorySeparatorChar, '/');
                    if (!CloudBackupDomains.IsInScope(manifestScope, stagedRelative))
                    {
                        throw new InvalidDataException(
                            $"Scoped backup entry '{stagedRelative}' is outside the manifest domains.");
                    }
                }
            }
            IReadOnlyList<DeskPaneTodoWidgetRemap> remaps = [];
            IReadOnlyList<string> unmapped = [];
            IReadOnlyList<DeskPaneDomainItemCount> domainItemCounts = [];
            int attachmentReferences = 0;

            var marker = new PendingRestoreMarker(
                stagingRoot,
                archivePath,
                DateTimeOffset.UtcNow,
                archiveInfo.Manifest.CreatedAtUtc,
                archiveInfo.Manifest.AppVersion,
                CloudBackupDomains.ToManifestNames(appliedScope),
                ScopedRestoreConfirmed: false);
            await WritePendingRestoreMarkerAtomicallyAsync(
                PendingRestoreMarkerPath,
                marker,
                cancellationToken);
            App.Log($"[DataBackup] Prepared scoped restore from '{archivePath}' (domains: {appliedScope}).");
            return new DeskPaneRestorePreparation(
                archiveInfo.Manifest.CreatedAtUtc,
                archiveInfo.Manifest.AppVersion,
                archiveInfo.FileCount,
                archiveInfo.TotalUncompressedBytes,
                archiveInfo.Manifest.SchemaVersion,
                archiveInfo.Manifest.SchemaVersion >= 2,
                CloudBackupDomains.ToManifestNames(appliedScope),
                archiveInfo.Manifest.SourceDeviceId,
                remaps,
                unmapped,
                domainItemCounts,
                attachmentReferences,
                IsFromNewerAppVersion: IsBackupFromNewerApp(archiveInfo.Manifest.AppVersion));
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(stagingRoot))
            {
                TryDeleteDirectory(stagingRoot);
            }

            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CancelPendingRestoreAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            DeletePendingRestoreCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<DeskPaneRestoreApplyResult> ApplyPendingRestoreAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        string? rollbackRoot = null;
        try
        {
            if (!File.Exists(PendingRestoreMarkerPath))
            {
                return DeskPaneRestoreApplyResult.NoPendingRestore;
            }

            PendingRestoreMarker marker = await ReadPendingRestoreMarkerAsync(cancellationToken);
            string stagingRoot = Path.GetFullPath(marker.StagingRoot);
            if (!IsPathInsideDirectory(stagingRoot, RestoreStagingDirectory))
            {
                throw new InvalidDataException("The pending restore staging path is invalid.");
            }

            if (marker.Domains is { Count: > 0 } &&
                marker.ScopedRestoreConfirmed == false)
            {
                // A crash or timed-out shutdown must never apply a cloud
                // restore while its confirmation dialog was still open.
                DeletePendingRestoreCore();
                App.Log("[DataBackup] Discarded an unconfirmed scoped restore.");
                return DeskPaneRestoreApplyResult.NoPendingRestore;
            }

            if (marker.Domains is not { Count: > 0 } && !marker.FullRestoreConfirmed)
            {
                // Same defense for full restores: the marker predates its
                // confirmation dialog, so an unanswered dialog (hidden
                // settings window, exit, crash) must discard the restore
                // instead of swapping the whole data directory on the next
                // launch.
                DeletePendingRestoreCore();
                App.Log("[DataBackup] Discarded an unconfirmed full restore.");
                return DeskPaneRestoreApplyResult.NoPendingRestore;
            }

            if (marker.Domains is { Count: > 0 } markerDomains)
            {
                return await ApplyScopedRestoreCoreAsync(
                    marker,
                    stagingRoot,
                    CloudBackupDomains.FromManifestNames(markerDomains),
                    cancellationToken);
            }

            string stagedDataDirectory = Path.Combine(stagingRoot, "data");
            ValidateRestoreData(
                stagedDataDirectory,
                requireSettings: !marker.AllowMissingSettings);

            marker = await EnsurePreRestoreSafetyBackupAsync(marker, cancellationToken);

            rollbackRoot = Path.Combine(_rootPath, "restore-rollback", Guid.NewGuid().ToString("N"));
            string rollbackDataDirectory = Path.Combine(rollbackRoot, "data");
            Directory.CreateDirectory(rollbackRoot);
            if (Directory.Exists(DataDirectory))
            {
                Directory.Move(DataDirectory, rollbackDataDirectory);
            }

            try
            {
                Directory.Move(stagedDataDirectory, DataDirectory);
            }
            catch
            {
                if (!Directory.Exists(DataDirectory) && Directory.Exists(rollbackDataDirectory))
                {
                    Directory.Move(rollbackDataDirectory, DataDirectory);
                }

                throw;
            }

            TryDeleteFile(PendingRestoreMarkerPath);
            TryDeleteDirectory(stagingRoot);
            TryDeleteDirectory(rollbackRoot);
            App.Log($"[DataBackup] Applied pending restore from '{marker.ArchivePath}'.");
            return new DeskPaneRestoreApplyResult(
                true,
                true,
                null,
                DeskPaneRestoreApplyKind.Full,
                marker.SourceDataPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Log($"[DataBackup] Pending restore failed: {ex}");
            // Deep review R15 (F7 audit): when BOTH rename attempts failed,
            // the user's entire data set only exists in the rollback
            // directory — name it explicitly so it can be recovered by hand.
            if (!string.IsNullOrWhiteSpace(rollbackRoot) && Directory.Exists(rollbackRoot))
            {
                App.Log($"[DataBackup] User data preserved at rollback path '{rollbackRoot}'.");
            }

            DeletePendingRestoreCore();
            return new DeskPaneRestoreApplyResult(true, false, ex.Message, DeskPaneRestoreApplyKind.Full);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(rollbackRoot) &&
                Directory.Exists(rollbackRoot) &&
                Directory.Exists(DataDirectory))
            {
                TryDeleteDirectory(rollbackRoot);
            }

            _gate.Release();
        }
    }

    /// <summary>
    /// Applies a scoped cloud restore: replaces only the marked domains'
    /// files inside the live data directory. Everything outside the domains
    /// — settings.json, FileSafety files, other widget stores — is never
    /// touched. The operation is idempotent (staged files are the source of
    /// truth and are not modified), so a mid-apply crash leaves the marker
    /// in place and the next boot simply retries to convergence.
    /// </summary>
    private async Task<DeskPaneRestoreApplyResult> ApplyScopedRestoreCoreAsync(
        PendingRestoreMarker marker,
        string stagingRoot,
        CloudBackupDomain scope,
        CancellationToken cancellationToken)
    {
        string stagedDataDirectory = Path.Combine(stagingRoot, "data");
        try
        {
            // Full local safety net before ANY restore — same as the
            // classic path.
            marker = await EnsurePreRestoreSafetyBackupAsync(marker, cancellationToken);

            Directory.CreateDirectory(DataDirectory);
            foreach (CloudBackupDomain domain in CloudBackupDomains.FileDomains)
            {
                if (!scope.HasFlag(domain))
                {
                    continue;
                }

                if (!marker.ReplaceItemData)
                {
                    continue;
                }

                // Snapshot-faithful domain replace: live domain files absent
                // from the staged snapshot are deleted, then staged files
                // are copied in. Non-domain paths are never enumerated.
                foreach (string liveFile in Directory
                             .EnumerateFiles(DataDirectory, "*", SearchOption.AllDirectories)
                             .ToArray())
                {
                    string liveRelative = Path
                        .GetRelativePath(DataDirectory, liveFile)
                        .Replace(Path.DirectorySeparatorChar, '/');
                    if (CloudBackupDomains.IsInDomain(domain, liveRelative))
                    {
                        File.Delete(liveFile);
                    }
                }

                if (!Directory.Exists(stagedDataDirectory))
                {
                    continue;
                }

                foreach (string stagedFile in Directory.EnumerateFiles(
                             stagedDataDirectory, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string stagedRelative = Path
                        .GetRelativePath(stagedDataDirectory, stagedFile)
                        .Replace(Path.DirectorySeparatorChar, '/');
                    if (!CloudBackupDomains.IsInDomain(domain, stagedRelative))
                    {
                        continue;
                    }

                    string destinationPath = Path.Combine(
                        DataDirectory,
                        stagedRelative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                    File.Copy(stagedFile, destinationPath, overwrite: true);
                }
            }

            if (scope.HasFlag(CloudBackupDomain.WidgetStyle))
            {
                string styleDocumentPath = Path.Combine(
                    stagingRoot,
                    CloudBackupDomains.WidgetStyleEntryName);
                if (File.Exists(styleDocumentPath))
                {
                    byte[] documentBytes = await File.ReadAllBytesAsync(
                        styleDocumentPath,
                        cancellationToken);
                    WidgetStyleBackupProjection.ApplyResult styleResult =
                        await WidgetStyleBackupProjection.ApplyAsync(
                            documentBytes,
                            Path.Combine(DataDirectory, "settings.json"),
                            Path.Combine(DataDirectory, "widget-layout.json"),
                            cancellationToken);
                    App.Log(
                        $"[DataBackup] Widget style restore: applied={styleResult.Applied}, " +
                        $"shell={styleResult.ShellFieldsPatched}, widgets={styleResult.WidgetsPatched}" +
                        (styleResult.SkippedReason is null ? "." : $", skipped={styleResult.SkippedReason}."));
                }
            }

            TryDeleteFile(PendingRestoreMarkerPath);
            TryDeleteDirectory(stagingRoot);
            App.Log($"[DataBackup] Applied scoped restore from '{marker.ArchivePath}' (domains: {scope}).");
            return new DeskPaneRestoreApplyResult(true, true, null, DeskPaneRestoreApplyKind.Scoped);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The marker and staging stay in place → next boot retries;
            // the pre-restore zip is the manual recovery net. Retries are
            // bounded: a deterministically failing archive would otherwise
            // block every scheduled upload forever.
            int attempts = marker.ApplyAttemptCount + 1;
            if (attempts >= MaxScopedRestoreApplyAttempts)
            {
                App.Log(
                    $"[DataBackup] Scoped restore abandoned after {attempts} attempts: {ex.Message}");
                TryDeleteFile(PendingRestoreMarkerPath);
                TryDeleteDirectory(stagingRoot);
                return new DeskPaneRestoreApplyResult(
                    true,
                    false,
                    $"Restore failed on {attempts} launches and was abandoned: {ex.Message}",
                    DeskPaneRestoreApplyKind.Scoped);
            }

            App.Log(
                $"[DataBackup] Scoped restore failed (will retry on next launch, " +
                $"attempt {attempts}/{MaxScopedRestoreApplyAttempts}): {ex}");
            try
            {
                // The counter must persist even when the apply token is
                // already cancelled — losing it would restart the budget.
                await WritePendingRestoreMarkerAtomicallyAsync(
                    PendingRestoreMarkerPath,
                    marker with { ApplyAttemptCount = attempts },
                    CancellationToken.None);
            }
            catch (Exception writeEx)
            {
                App.Log($"[DataBackup] Failed to persist restore attempt count: {writeEx.Message}");
            }

            return new DeskPaneRestoreApplyResult(true, false, ex.Message, DeskPaneRestoreApplyKind.Scoped);
        }
    }

    /// <summary>
    /// Records the item-restore mode chosen in the confirm dialog onto the
    /// pending marker — additive merge (default) vs snapshot-faithful
    /// replace. The choice is made after PrepareScopedRestoreAsync wrote
    /// the marker, so it is rewritten atomically; returns false when
    /// nothing is pending.
    /// </summary>
    internal async Task<bool> SetPendingRestoreItemReplaceModeAsync(
        bool replaceItemData,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(PendingRestoreMarkerPath))
            {
                return false;
            }

            PendingRestoreMarker marker = await ReadPendingRestoreMarkerAsync(cancellationToken);
            await WritePendingRestoreMarkerAtomicallyAsync(
                PendingRestoreMarkerPath,
                marker with
                {
                    ReplaceItemData = replaceItemData,
                    ScopedRestoreConfirmed = marker.Domains is { Count: > 0 }
                        ? true
                        : marker.ScopedRestoreConfirmed
                },
                cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Persists the user's "restore" answer onto a pending FULL restore
    /// marker. The marker is written before its confirmation dialog opens,
    /// so this call is what authorizes the next launch to apply it; until
    /// then the startup gate discards the marker. Returns false when
    /// nothing is pending or the pending restore is scoped (scoped
    /// restores confirm through their own item-mode call).
    /// </summary>
    internal async Task<bool> ConfirmPendingFullRestoreAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(PendingRestoreMarkerPath))
            {
                return false;
            }

            PendingRestoreMarker marker = await ReadPendingRestoreMarkerAsync(cancellationToken);
            if (marker.Domains is { Count: > 0 })
            {
                return false;
            }

            await WritePendingRestoreMarkerAtomicallyAsync(
                PendingRestoreMarkerPath,
                marker with { FullRestoreConfirmed = true },
                cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Item-level merge conflict rule shared by the todo and quick-capture
    /// merge paths: the staged record wins only when it is strictly newer
    /// AND is not a tombstone covering a live local record — a merge must
    /// never reduce what the local device can see.
    /// </summary>
    private static bool ShouldRemoteItemWin(
        bool remoteIsDeleted,
        DateTimeOffset remoteUpdatedAt,
        bool localIsDeleted,
        DateTimeOffset localUpdatedAt)
    {
        return !(remoteIsDeleted && !localIsDeleted) && remoteUpdatedAt > localUpdatedAt;
    }

    private async Task<RestoreArchiveInfo> ExtractAndValidateRestoreArchiveAsync(
        string archivePath,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        // A non-zip or truncated archive throws here — that's the common
        // "user picked the wrong file" case, so it gets the same localized
        // "not a valid backup" surface as a missing manifest.
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            throw new InvalidDataException("The selected file is not a readable backup archive.", ex)
            {
                Data = { [BackupManifestInvalidDataKey] = true }
            };
        }

        using (archive)
        {
            return await ExtractAndValidateManifestAsync(archive, stagingRoot, cancellationToken);
        }
    }

    private async Task<RestoreArchiveInfo> ExtractAndValidateManifestAsync(
        ZipArchive archive,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        DeskPaneBackupManifest manifest;
        try
        {
            ZipArchiveEntry? manifestEntry = archive.Entries.SingleOrDefault(entry =>
                string.Equals(entry.FullName, "manifest.json", StringComparison.Ordinal));
            if (manifestEntry is null)
            {
                throw new InvalidDataException("The backup manifest is missing.")
                {
                    Data = { [BackupManifestInvalidDataKey] = true }
                };
            }

            if (manifestEntry.Length > 1024 * 1024)
            {
                throw new InvalidDataException("The backup manifest is too large.")
                {
                    Data = { [BackupManifestInvalidDataKey] = true }
                };
            }

            await using (Stream manifestStream = manifestEntry.Open())
            {
                // The declared Length is only the cheap pre-check — cap the
                // actual inflated bytes so a crafted entry cannot expand
                // unboundedly into the JSON parser.
                var manifestBytes = new MemoryStream();
                await CopyAndHashAsync(
                    manifestStream,
                    manifestBytes,
                    cancellationToken,
                    maxBytes: 1024 * 1024);
                manifestBytes.Position = 0;
                manifest = await JsonSerializer.DeserializeAsync(
                               manifestBytes,
                               BackupJsonContext.Default.BackupManifest,
                               cancellationToken) ??
                           throw new InvalidDataException("The backup manifest is invalid.")
                           {
                               Data = { [BackupManifestInvalidDataKey] = true }
                           };
            }
        }
        catch (Exception ex) when (
            (ex is InvalidDataException or JsonException or NotSupportedException) &&
            !ex.Data.Contains(BackupManifestInvalidDataKey))
        {
            // Malformed manifest JSON, unsupported compression, truncated
            // entries — same user-facing story as a missing manifest.
            throw new InvalidDataException("The backup manifest is unreadable.", ex)
            {
                Data = { [BackupManifestInvalidDataKey] = true }
            };
        }

        if (manifest.SchemaVersion < MinimumSupportedBackupSchemaVersion ||
            manifest.SchemaVersion > BackupSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported DeskPane backup schema version {manifest.SchemaVersion}.")
            {
                Data = { [BackupSchemaVersionDataKey] = manifest.SchemaVersion }
            };
        }

        // Newer-app backups are NOT rejected: the schema gate above is the
        // real format contract, and a hard version block strands every
        // device that has not updated yet during a staggered rollout. The
        // preparation record carries IsFromNewerAppVersion so both restore
        // dialogs can warn that newer-version-only data may not survive.

        string destinationRoot = EnsureTrailingDirectorySeparator(
            Path.GetFullPath(Path.Combine(stagingRoot, "data")));
        var extractedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extractedFiles = new Dictionary<string, DeskPaneBackupFileManifest>(StringComparer.OrdinalIgnoreCase);
        DeskPaneBackupFileManifest? extractedWidgetStyle = null;
        int fileCount = 0;
        long totalUncompressedBytes = 0;
        // Remaining ACTUAL-byte budget: separate from the declared-length
        // sum because a malformed entry can under-report its true size.
        long totalExtractedBudget = MaxRestoreTotalSizeBytes;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(entry.FullName, "manifest.json", StringComparison.Ordinal))
            {
                continue;
            }

            if (entry.FullName.Contains('\\') ||
                (!entry.FullName.StartsWith("data/", StringComparison.Ordinal) &&
                 !string.Equals(entry.FullName, "data", StringComparison.Ordinal) &&
                 !string.Equals(
                     entry.FullName,
                     CloudBackupDomains.WidgetStyleEntryName,
                     StringComparison.Ordinal)))
            {
                throw new InvalidDataException($"Unexpected backup entry '{entry.FullName}'.");
            }

            string destinationPath = Path.GetFullPath(
                Path.Combine(stagingRoot, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            string dataRootPath = destinationRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            bool isWidgetStyleEntry = string.Equals(
                entry.FullName,
                CloudBackupDomains.WidgetStyleEntryName,
                StringComparison.Ordinal);
            if (!string.Equals(destinationPath, dataRootPath, StringComparison.OrdinalIgnoreCase) &&
                !destinationPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase) &&
                !(isWidgetStyleEntry &&
                  string.Equals(
                      destinationPath,
                      Path.Combine(stagingRoot, CloudBackupDomains.WidgetStyleEntryName),
                      StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException($"Unsafe backup entry '{entry.FullName}'.");
            }

            bool isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
            if (isDirectory)
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            if (string.Equals(entry.FullName, "data", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The backup data root entry must be a directory.");
            }

            if (isWidgetStyleEntry)
            {
                // The style entry bypasses the per-file manifest but not the
                // safety budget: it is a small JSON document by construction,
                // so a dedicated cap stops a crafted archive from expanding
                // unboundedly before the DOM parse.
                if (entry.Length > MaxWidgetStyleEntryBytes)
                {
                    throw new InvalidDataException("The backup widget-style document is oversized.");
                }
            }
            else
            {
                fileCount++;
                if (fileCount > MaxRestoreFileCount || entry.Length > MaxRestoreFileSizeBytes)
                {
                    throw new InvalidDataException("The backup contains too many files or an oversized file.");
                }
            }

            totalUncompressedBytes = checked(totalUncompressedBytes + entry.Length);
            if (totalUncompressedBytes > MaxRestoreTotalSizeBytes)
            {
                throw new InvalidDataException("The expanded backup is too large.");
            }

            if (!extractedPaths.Add(destinationPath))
            {
                throw new InvalidDataException($"Duplicate backup entry '{entry.FullName}'.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using Stream source = entry.Open();
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);
            // The declared entry.Length checks above are only the cheap early
            // reject; the hard caps are enforced on the bytes the deflate
            // stream actually produces, capped at whichever budget runs out
            // first — the per-file limit or the remaining total budget.
            long extractedBudget = isWidgetStyleEntry
                ? MaxWidgetStyleEntryBytes
                : MaxRestoreFileSizeBytes;
            (long extractedLength, string sha256) = await CopyAndHashAsync(
                source,
                destination,
                cancellationToken,
                Math.Min(extractedBudget, totalExtractedBudget));
            totalExtractedBudget -= extractedLength;
            if (isWidgetStyleEntry)
            {
                extractedWidgetStyle = new DeskPaneBackupFileManifest(
                    entry.FullName,
                    extractedLength,
                    sha256);
            }
            else
            {
                string relativePath = entry.FullName["data/".Length..];
                extractedFiles[relativePath] = new DeskPaneBackupFileManifest(
                    relativePath,
                    extractedLength,
                    sha256);
            }
        }

        bool isScopedArchive = manifest.Domains is { Count: > 0 };
        if (fileCount == 0 && !isScopedArchive)
        {
            throw new InvalidDataException("The backup contains no DeskPane data files.");
        }

        if (manifest.SchemaVersion >= 2 &&
            (extractedFiles.Count > 0 || !isScopedArchive))
        {
            ValidateIntegrityManifest(manifest.Files, extractedFiles);
        }

        // widget-style.json bypasses Files (root-level, not data-relative)
        // but is covered by its own manifest field. Verify when declared —
        // archives written before the field existed legitimately lack it;
        // a declared entry with no matching archive entry is corruption.
        if (manifest.WidgetStyleFile is { } expectedStyle)
        {
            if (extractedWidgetStyle is null ||
                expectedStyle.Length != extractedWidgetStyle.Length ||
                !string.Equals(
                    expectedStyle.Sha256,
                    extractedWidgetStyle.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The backup widget-style document failed integrity validation.");
            }
        }

        if (!isScopedArchive)
        {
            ValidateRestoreData(
                Path.Combine(stagingRoot, "data"),
                requireSettings: !string.Equals(
                    manifest.Kind, "pre-restore", StringComparison.Ordinal));
        }

        return new RestoreArchiveInfo(manifest, fileCount, totalUncompressedBytes);
    }

    private static void ValidateRestoreData(string dataDirectory, bool requireSettings = true)
    {
        if (!Directory.Exists(dataDirectory) ||
            !Directory.EnumerateFiles(dataDirectory, "*", SearchOption.AllDirectories).Any())
        {
            throw new InvalidDataException("The backup data directory is empty.");
        }

        string settingsPath = Path.Combine(dataDirectory, "settings.json");
        if (requireSettings && !File.Exists(settingsPath))
        {
            throw new InvalidDataException("The backup is missing settings.json.");
        }

        ValidateJsonFileIfPresent<AppSettings>(
            settingsPath,
            s_settingsDataJsonContext.AppSettings);
        ValidateJsonFileIfPresent<DesktopOrganizationHistoryData>(
            Path.Combine(dataDirectory, "desktop-organization-history.json"),
            DesktopOrganizationHistoryJsonContext.Default.DesktopOrganizationHistoryData);
        ValidateJsonFileIfPresent<WidgetLayoutDocument>(
            Path.Combine(dataDirectory, "widget-layout.json"),
            WidgetLayoutJsonContext.Default.WidgetLayoutDocument);
    }

    private static void ValidateJsonFileIfPresent<T>(
        string path,
        JsonTypeInfo<T> jsonTypeInfo)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(path);
            if (JsonSerializer.Deserialize(json, jsonTypeInfo) is null)
            {
                throw new JsonException("The JSON document contains null.");
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new InvalidDataException(
                $"Backup data file '{Path.GetFileName(path)}' is invalid.",
                ex);
        }
    }

    private static JsonSerializerOptions CreateDataJsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static string? TryGetStoreRelativePath(string originalPath, string storeRelativePath)
    {
        string normalizedOriginal = originalPath.Replace('\\', '/');
        string normalizedStore = storeRelativePath.Trim('/').Replace('\\', '/');
        int storeIndex = normalizedOriginal.IndexOf(
            $"/{normalizedStore}/",
            StringComparison.OrdinalIgnoreCase);
        if (storeIndex < 0)
        {
            return null;
        }

        return normalizedOriginal[(storeIndex + 1)..].Replace('/', Path.DirectorySeparatorChar);
    }

    private static bool TryGetRelativePathInsideDirectory(
        string path,
        string directory,
        out string relativePath)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            string fullDirectory = Path.GetFullPath(directory);
            if (IsPathInsideDirectory(fullPath, fullDirectory))
            {
                relativePath = Path.GetRelativePath(fullDirectory, fullPath);
                return true;
            }
        }
        catch
        {
        }

        relativePath = string.Empty;
        return false;
    }

    private async Task<PendingRestoreMarker> ReadPendingRestoreMarkerAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            string json = await File.ReadAllTextAsync(PendingRestoreMarkerPath, cancellationToken);
            return JsonSerializer.Deserialize(
                       json,
                       BackupJsonContext.Default.PendingRestoreMarker) ??
                   throw new InvalidDataException("The pending restore marker is invalid.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The pending restore marker is invalid.", ex);
        }
    }

    private void DeletePendingRestoreCore()
    {
        if (File.Exists(PendingRestoreMarkerPath))
        {
            try
            {
                string json = File.ReadAllText(PendingRestoreMarkerPath);
                PendingRestoreMarker? marker = JsonSerializer.Deserialize(
                    json,
                    BackupJsonContext.Default.PendingRestoreMarker);
                if (marker is not null &&
                    IsPathInsideDirectory(marker.StagingRoot, RestoreStagingDirectory))
                {
                    TryDeleteDirectory(marker.StagingRoot);
                }
            }
            catch (Exception ex)
            {
                App.Log($"[DataBackup] Failed to read pending restore while cancelling: {ex.Message}");
            }
        }

        TryDeleteFile(PendingRestoreMarkerPath);
        TryDeleteDirectory(RestoreStagingDirectory);
    }

    private static async Task WritePendingRestoreMarkerAtomicallyAsync(
        string path,
        PendingRestoreMarker marker,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                tempPath,
                JsonSerializer.Serialize(
                    marker,
                    BackupJsonContext.Default.PendingRestoreMarker),
                cancellationToken);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private bool HasBackupSourceData()
    {
        // "Source data" means data the archive filter would actually keep:
        // a data directory holding only device.id, sync/ protocol state or
        // cache/ produces an empty snapshot whose validation would fail —
        // and that failure would block the restore the net protects.
        return Directory.Exists(DataDirectory) &&
            Directory.EnumerateFiles(DataDirectory, "*", SearchOption.AllDirectories)
                .Any(path => ShouldIncludeInBackup(
                    Path.GetRelativePath(DataDirectory, path)
                        .Replace(Path.DirectorySeparatorChar, '/')));
    }

    private async Task CreateArchiveCoreAsync(
        string archivePath,
        string backupKind,
        CancellationToken cancellationToken,
        bool requireSettings = true)
    {
        string snapshotRoot = Path.Combine(
            BackupSnapshotStagingDirectory,
            Guid.NewGuid().ToString("N"));
        string snapshotDataDirectory = Path.Combine(snapshotRoot, "data");
        try
        {
            await CreateDataSnapshotAsync(
                snapshotDataDirectory, cancellationToken, requireSettings);
            ValidateRestoreData(snapshotDataDirectory, requireSettings);
            await CreateArchiveFromSnapshotAsync(
                archivePath,
                backupKind,
                snapshotDataDirectory,
                cancellationToken);
        }
        finally
        {
            TryDeleteDirectory(snapshotRoot);
            TryDeleteEmptyDirectory(BackupSnapshotStagingDirectory);
        }
    }

    public async Task<IReadOnlyList<DeskPaneBackupSnapshotInfo>> GetSnapshotInventoryAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Snapshots are intentionally not migrated when the custom folder
            // changes, so both the configured and the default directories can
            // hold restorable copies at the same time.
            var paths = new[]
                {
                    (Directory: EffectiveAutomaticSnapshotDirectory, Kind: "automatic"),
                    (Directory: AutomaticSnapshotDirectory, Kind: "automatic"),
                    (Directory: LegacyAutomaticSnapshotDirectory, Kind: "automatic"),
                    (Directory: PreRestoreBackupDirectory, Kind: "pre-restore")
                }
                .Where(item => Directory.Exists(item.Directory))
                .DistinctBy(item => item.Directory, StringComparer.OrdinalIgnoreCase)
                .SelectMany(item => Directory.EnumerateFiles(item.Directory, "*.zip")
                    .Select(path => (path, item.Kind)))
                .OrderByDescending(item => File.GetLastWriteTimeUtc(item.path))
                .ToList();

            var snapshots = new List<DeskPaneBackupSnapshotInfo>(paths.Count);
            foreach ((string path, string kind) in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileInfo file = new(path);
                SnapshotManifestSummary summary = await ReadSnapshotManifestSummaryAsync(path, cancellationToken);
                snapshots.Add(new DeskPaneBackupSnapshotInfo(
                    path,
                    kind,
                    summary.CreatedAtUtc ?? file.LastWriteTimeUtc,
                    file.Length,
                    summary.IsReadable,
                    summary.AppVersion,
                    summary.SchemaVersion));
            }

            return snapshots;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Finds the newest readable snapshot stored outside the app-data root.
    /// This is used after a reinstall when the local settings file no longer
    /// exists, so DeskPane can point the user to a safe recovery copy.
    /// </summary>
    public async Task<DeskPaneBackupSnapshotInfo?> GetLatestRecoverySnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!Directory.Exists(AutomaticSnapshotDirectory))
            {
                return null;
            }

            foreach (string path in Directory
                         .EnumerateFiles(AutomaticSnapshotDirectory, "DeskPane-Auto-*.zip")
                         .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileInfo file = new(path);
                SnapshotManifestSummary summary = await ReadSnapshotManifestSummaryAsync(path, cancellationToken);
                if (!summary.IsReadable)
                {
                    continue;
                }

                return new DeskPaneBackupSnapshotInfo(
                    path,
                    "automatic",
                    summary.CreatedAtUtc ?? file.LastWriteTimeUtc,
                    file.Length,
                    true,
                    summary.AppVersion,
                    summary.SchemaVersion);
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// True when any automatic recovery snapshot predates
    /// <paramref name="notAfterUtc"/> — the cheap existence probe the
    /// wipe-recovery detection uses before suppressing the startup snapshot
    /// on a fresh-default launch.
    /// </summary>
    internal async Task<bool> HasRecoverySnapshotNotAfterAsync(
        DateTimeOffset notAfterUtc,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return EnumerateRecoverySnapshotFiles()
                .Any(path => File.GetLastWriteTimeUtc(path) <= notAfterUtc);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Newest automatic recovery snapshot that predates this process and
    /// passes the wipe-recovery content gates (readable manifest and settings,
    /// resolved initial setup, live widgets, a settings schema this build can
    /// own). Newest-first with per-archive peek validation: the archive
    /// created moments after a wipe captures fresh defaults rather than the
    /// lost profile, so file time alone must never pick the candidate.
    /// </summary>
    internal async Task<RecoverySnapshotCandidate?> GetLatestValidRecoverySnapshotAsync(
        DateTimeOffset notAfterUtc,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (string path in EnumerateRecoverySnapshotFiles()
                         .Where(path => File.GetLastWriteTimeUtc(path) <= notAfterUtc)
                         .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RecoverySnapshotPeek peek = await PeekRecoverySnapshotAsync(path, cancellationToken);
                if (RecoverySnapshotCandidatePolicy.Evaluate(peek) !=
                    RecoverySnapshotCandidateVerdict.Valid)
                {
                    continue;
                }

                return new RecoverySnapshotCandidate(
                    path,
                    peek.CreatedAtUtc ?? File.GetLastWriteTimeUtc(path),
                    peek.AppVersion,
                    peek.BackupSchemaVersion,
                    peek.LiveWidgetCount,
                    IsBackupFromNewerApp(peek.AppVersion));
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private IEnumerable<string> EnumerateRecoverySnapshotFiles()
    {
        string[] directories =
        [
            EffectiveAutomaticSnapshotDirectory,
            AutomaticSnapshotDirectory,
            LegacyAutomaticSnapshotDirectory
        ];
        foreach (string directory in directories
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(Directory.Exists))
        {
            foreach (string path in Directory.EnumerateFiles(directory, "DeskPane-Auto-*.zip"))
            {
                yield return path;
            }
        }
    }

    private static async Task<RecoverySnapshotPeek> PeekRecoverySnapshotAsync(
        string snapshotPath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var input = new FileStream(
                snapshotPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 81920,
                useAsync: true);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
            return RecoverySnapshotPeekReader.Read(archive);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return RecoverySnapshotPeek.Unreadable;
        }
    }

    public async Task<bool> DeleteSnapshotAsync(
        string snapshotPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        snapshotPath = Path.GetFullPath(snapshotPath);

        if (!snapshotPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            (!IsPathInsideDirectory(snapshotPath, EffectiveAutomaticSnapshotDirectory) &&
             !IsPathInsideDirectory(snapshotPath, AutomaticSnapshotDirectory) &&
             !IsPathInsideDirectory(snapshotPath, LegacyAutomaticSnapshotDirectory) &&
             !IsPathInsideDirectory(snapshotPath, PreRestoreBackupDirectory)))
        {
            throw new InvalidOperationException("The selected backup snapshot is not managed by DeskPane.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(snapshotPath))
            {
                return false;
            }

            File.Delete(snapshotPath);
            App.Log($"[DataBackup] Deleted snapshot '{snapshotPath}'.");
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<SnapshotManifestSummary> ReadSnapshotManifestSummaryAsync(
        string snapshotPath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var input = new FileStream(
                snapshotPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 81920,
                useAsync: true);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
            ZipArchiveEntry? manifestEntry = archive.Entries.SingleOrDefault(entry =>
                string.Equals(entry.FullName, "manifest.json", StringComparison.Ordinal));
            if (manifestEntry is null || manifestEntry.Length > 1024 * 1024)
            {
                return SnapshotManifestSummary.Unreadable;
            }

            await using Stream manifestStream = manifestEntry.Open();
            // The declared Length is only the cheap pre-check — a malformed
            // entry can inflate past it, so cap the actual bytes read.
            var manifestBytes = new MemoryStream();
            await CopyAndHashAsync(
                manifestStream,
                manifestBytes,
                cancellationToken,
                maxBytes: 1024 * 1024);
            manifestBytes.Position = 0;
            DeskPaneBackupManifest? manifest = await JsonSerializer.DeserializeAsync(
                manifestBytes,
                BackupJsonContext.Default.BackupManifest,
                cancellationToken);
            return manifest is null
                ? SnapshotManifestSummary.Unreadable
                : new SnapshotManifestSummary(
                    true,
                    manifest.CreatedAtUtc,
                    manifest.AppVersion,
                    manifest.SchemaVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return SnapshotManifestSummary.Unreadable;
        }
    }

    private async Task CreateDataSnapshotAsync(
        string snapshotDataDirectory,
        CancellationToken cancellationToken,
        bool requireSettings = true)
    {
        string settingsPath = Path.Combine(DataDirectory, "settings.json");
        // The pre-restore safety net must not require settings.json: a device
        // whose settings were lost or quarantined still has widget stores
        // worth preserving before a restore overwrites them.
        if (requireSettings && !File.Exists(settingsPath))
        {
            throw new InvalidOperationException("DeskPane settings are not available for backup.");
        }

        (string SourcePath, string RelativePath)[] sourceFiles = Directory
            .EnumerateFiles(DataDirectory, "*", SearchOption.AllDirectories)
            .Select(path => (
                SourcePath: path,
                RelativePath: Path.GetRelativePath(DataDirectory, path)
                    .Replace(Path.DirectorySeparatorChar, '/')))
            .Where(file => ShouldIncludeInBackup(file.RelativePath))
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Directory.CreateDirectory(snapshotDataDirectory);

        // FileSafety metadata must come from a single transaction epoch:
        // settings, the device-layout store, the organization-history store
        // and the recovery journal are committed as a unit under
        // OperationGate. The file SET is resolved inside the gate — a journal
        // created while we waited for the gate must land in the snapshot, or
        // the backup would hold settings@T1 with history@T0 and no WAL to
        // converge them. Hold the gate only for these few small files; the
        // rest still copies one by one from the pre-enumerated list.
        //
        // settings.json and widget-layout.json additionally commit under the
        // settings write gate — a plain settings save does not take
        // OperationGate, so without it the snapshot could tear mid-save into
        // a pair that never existed on disk (settings@S0 with layout@S1).
        // Lock order is OperationGate-then-write-gate everywhere, matching
        // DesktopOrganizationTransaction's own save path.
        string[] fileSafetyMetadata =
        [
            "settings.json",
            "widget-layout.json",
            "desktop-organization-history.json",
            "desktop-organization-recovery.json"
        ];
        var metadataSet = new HashSet<string>(fileSafetyMetadata, StringComparer.OrdinalIgnoreCase);
        await DesktopOrganizationTransaction.OperationGate.WaitAsync(cancellationToken);
        try
        {
            SemaphoreSlim settingsWriteLock = SettingsService.FileWriteLockFor(DataDirectory);
            await settingsWriteLock.WaitAsync(cancellationToken);
            try
            {
                foreach (string relativePath in fileSafetyMetadata)
                {
                    string sourcePath = Path.Combine(DataDirectory, relativePath);
                    if (!File.Exists(sourcePath)) continue;
                    await CopyStableSnapshotFileAsync(
                        sourcePath,
                        Path.Combine(snapshotDataDirectory, relativePath),
                        cancellationToken);
                }
            }
            finally
            {
                settingsWriteLock.Release();
            }
        }
        finally
        {
            DesktopOrganizationTransaction.OperationGate.Release();
        }

        foreach ((string sourcePath, string relativePath) in sourceFiles)
        {
            if (metadataSet.Contains(relativePath)) continue;
            // A multi-gigabyte snapshot legitimately runs longer than the
            // startup watchdog's stall window; each file proves progress.
            App.MarkStartupProgress();
            cancellationToken.ThrowIfCancellationRequested();
            string destinationPath = Path.Combine(
                snapshotDataDirectory,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await CopyStableSnapshotFileAsync(sourcePath, destinationPath, cancellationToken);
        }
    }

    private async Task CreateArchiveFromSnapshotAsync(
        string archivePath,
        string backupKind,
        string snapshotDataDirectory,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? manifestDomains = null,
        IReadOnlyDictionary<string, byte[]>? rootEntries = null,
        string? sourceDeviceId = null)
    {
        (string SourcePath, string RelativePath)[] sourceFiles = Directory
            .EnumerateFiles(snapshotDataDirectory, "*", SearchOption.AllDirectories)
            .Select(path => (
                SourcePath: path,
                RelativePath: Path.GetRelativePath(snapshotDataDirectory, path)
                    .Replace(Path.DirectorySeparatorChar, '/')))
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string tempArchivePath = $"{archivePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var output = new FileStream(
                             tempArchivePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             useAsync: true))
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var fileManifest = new List<DeskPaneBackupFileManifest>(sourceFiles.Length);
                    foreach ((string sourcePath, string relativePath) in sourceFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ZipArchiveEntry entry = archive.CreateEntry($"data/{relativePath}", CompressionLevel.Fastest);
                        await using var source = new FileStream(
                            sourcePath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            bufferSize: 81920,
                            useAsync: true);
                        await using Stream destination = entry.Open();
                        (long length, string sha256) = await CopyAndHashAsync(
                            source,
                            destination,
                            cancellationToken);
                        fileManifest.Add(new DeskPaneBackupFileManifest(relativePath, length, sha256));
                    }

                    // Root-level entries (e.g. widget-style.json) live beside
                    // manifest.json — they are backup artifacts, not data
                    // files, and scoped restore reads them from staging root.
                    DeskPaneBackupFileManifest? widgetStyleManifest = null;
                    if (rootEntries is not null)
                    {
                        foreach ((string entryName, byte[] content) in rootEntries)
                        {
                            ZipArchiveEntry rootEntry = archive.CreateEntry(
                                entryName,
                                CompressionLevel.Fastest);
                            await using Stream entryStream = rootEntry.Open();
                            await entryStream.WriteAsync(content, cancellationToken);
                            if (string.Equals(
                                    entryName,
                                    CloudBackupDomains.WidgetStyleEntryName,
                                    StringComparison.Ordinal))
                            {
                                widgetStyleManifest = new DeskPaneBackupFileManifest(
                                    entryName,
                                    content.Length,
                                    Convert.ToHexString(SHA256.HashData(content)));
                            }
                        }
                    }

                    var manifest = new DeskPaneBackupManifest(
                        BackupSchemaVersion,
                        backupKind,
                        DateTimeOffset.UtcNow,
                        typeof(DeskPaneDataBackupService).Assembly.GetName().Version?.ToString() ?? "unknown",
                        DataDirectory,
                        fileManifest,
                        manifestDomains,
                        sourceDeviceId,
                        widgetStyleManifest);
                    ZipArchiveEntry manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
                    await using (Stream manifestStream = manifestEntry.Open())
                    {
                        await JsonSerializer.SerializeAsync(
                            manifestStream,
                            manifest,
                            BackupJsonContext.Default.BackupManifest,
                            cancellationToken);
                    }
                }

                await output.FlushAsync(cancellationToken);
            }

            // The final rename commits the archive. Cancellation is accepted
            // before it; after it succeeds the completed backup is retained.
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempArchivePath, archivePath, overwrite: false);
        }
        finally
        {
            TryDeleteFile(tempArchivePath);
        }
    }

    private static async Task CopyStableSnapshotFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= MaxSnapshotCopyAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryDeleteFile(destinationPath);

            try
            {
                var before = new FileInfo(sourcePath);
                long expectedLength = before.Length;
                DateTime expectedLastWriteUtc = before.LastWriteTimeUtc;

                long copiedLength;
                await using (var source = new FileStream(
                                 sourcePath,
                                 FileMode.Open,
                                 FileAccess.Read,
                                 FileShare.ReadWrite | FileShare.Delete,
                                 bufferSize: 81920,
                                 useAsync: true))
                await using (var destination = new FileStream(
                                 destinationPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 bufferSize: 81920,
                                 useAsync: true))
                {
                    (copiedLength, _) = await CopyAndHashAsync(source, destination, cancellationToken);
                    await destination.FlushAsync(cancellationToken);
                }

                var after = new FileInfo(sourcePath);
                if (after.Exists &&
                    copiedLength == expectedLength &&
                    after.Length == expectedLength &&
                    after.LastWriteTimeUtc == expectedLastWriteUtc)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == MaxSnapshotCopyAttempts)
                {
                    break;
                }
            }

            if (attempt < MaxSnapshotCopyAttempts)
            {
                await Task.Yield();
            }
        }

        TryDeleteFile(destinationPath);
        throw new IOException($"DeskPane data file changed while creating a backup snapshot: '{sourcePath}'.");
    }

    private void PruneAutomaticSnapshots(
        string directory,
        int retentionCount,
        string? pinnedPath = null)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        int kept = 0;
        foreach (string candidatePath in Directory
                     .EnumerateFiles(directory, "DeskPane-Auto-*.zip")
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            if (pinnedPath is not null &&
                string.Equals(candidatePath, pinnedPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (++kept > Math.Max(1, retentionCount))
            {
                TryDeleteFile(candidatePath);
            }
        }
    }

    /// <summary>
    /// Creates the pre-restore safety net once per pending-restore
    /// transaction. A scoped restore retries on every launch until it
    /// converges; creating a fresh archive per attempt would stack
    /// post-restore states and eventually prune the FIRST snapshot — the
    /// only one that actually holds pre-restore data. The pinned path is
    /// written into the marker BEFORE the archive is created so a crash
    /// can never leave an unbounded accumulation, and the pin exempts the
    /// archive from pruning while the marker lives.
    /// </summary>
    private async Task<PendingRestoreMarker> EnsurePreRestoreSafetyBackupAsync(
        PendingRestoreMarker marker,
        CancellationToken cancellationToken)
    {
        if (marker.SafetyBackupPath is { } pinned && File.Exists(pinned))
        {
            return marker;
        }

        if (!HasBackupSourceData())
        {
            return marker;
        }

        Directory.CreateDirectory(PreRestoreBackupDirectory);
        string preRestorePath = GetAvailableArchivePath(
            PreRestoreBackupDirectory,
            $"DeskPane-PreRestore-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        await WritePendingRestoreMarkerAtomicallyAsync(
            PendingRestoreMarkerPath,
            marker with { SafetyBackupPath = preRestorePath },
            cancellationToken);
        await CreateArchiveCoreAsync(
            preRestorePath, "pre-restore", cancellationToken,
            requireSettings: false);
        PrunePreRestoreBackups(preRestorePath);
        App.Log($"[DataBackup] Created pre-restore backup '{preRestorePath}'.");
        // The pinned path must flow back to the caller's marker instance:
        // the retry-attempt rewrite below does `marker with {...}` and would
        // otherwise clobber the pin from the disk copy, stacking one safety
        // archive per failed launch.
        return marker with { SafetyBackupPath = preRestorePath };
    }

    private void PrunePreRestoreBackups(string? pinnedPath = null)
    {
        int kept = 0;
        foreach (string candidatePath in Directory
                     .EnumerateFiles(PreRestoreBackupDirectory, "DeskPane-PreRestore-*.zip")
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            if (pinnedPath is not null &&
                string.Equals(candidatePath, pinnedPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (++kept > MaxPreRestoreBackupCount)
            {
                TryDeleteFile(candidatePath);
            }
        }
    }

    private static string GetAvailableArchivePath(string directory, string fileName)
    {
        string candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int suffix = 2; ; suffix++)
        {
            candidate = Path.Combine(directory, $"{stem}-{suffix}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static bool IsPathInsideDirectory(string path, string directory)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            string directoryPrefix = EnsureTrailingDirectorySeparator(Path.GetFullPath(directory));
            return fullPath.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// ResilientJsonStore sidecars only ever sit next to a DeskPane *.json
    /// store: "&lt;store&gt;.json.bak" and
    /// "&lt;store&gt;.json.corrupt-&lt;timestamp&gt;-&lt;guid&gt;". Scoped to
    /// that exact namespace so user files under attachments/ (which keep
    /// their original names) are never filtered out.
    /// </summary>
    private static bool IsInternalStoreRecoveryArtifact(string relativePath) =>
        relativePath.EndsWith(".json.bak", StringComparison.OrdinalIgnoreCase) ||
        relativePath.Contains(".json.corrupt-", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldIncludeInBackup(string relativePath)
    {
        // DeskPane-managed disposable subtrees first: nothing under them is
        // user data, and they can never contain a managed-attachments dir.
        // cache/ (widget image caches) and weather-cache.json regenerate on
        // next use; quick-capture exports/thumbnails are derived artifacts.
        //
        // device.id is excluded deliberately: it is installation-local
        // identity, not user data. Carrying it into a backup would clone the
        // device identity onto every machine that restores it — silently
        // misattributing sync-layer provenance. DeviceIdentity.GetOrCreate
        // regenerates a fresh ID on first use after a restore.
        //
        // sync/ is excluded deliberately too: it is device-local protocol
        // state (outbox queue, pull cursors, revision map, conflict log).
        // Restoring it onto another machine would replay stale cursors and
        // push intents against data the restore also rewrote — the sync
        // contract assigns it no backup semantics at all.
        if (relativePath.StartsWith("quick-capture/thumbnails/", StringComparison.OrdinalIgnoreCase) ||
            relativePath.StartsWith("quick-capture/exports/", StringComparison.OrdinalIgnoreCase) ||
            relativePath.StartsWith("cache/", StringComparison.OrdinalIgnoreCase) ||
            relativePath.StartsWith("sync/", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(relativePath, "weather-cache.json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(relativePath, "device.id", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Managed attachments are user data with their ORIGINAL filenames —
        // no extension or sidecar heuristic may ever drop them (a user file
        // literally named "config.json.bak" or "file.tmp" must still be
        // backed up, or restore leaves dangling attachment metadata).
        if (IsAttachmentPath(relativePath))
        {
            return true;
        }

        // .tmp files and ResilientJsonStore sidecars are machine-local
        // recovery artifacts, not user data: a stale recovery .bak inside a
        // backup would resurrect a ghost pending journal on the restore
        // machine, and .corrupt-* quarantines are dead forensics. The store
        // regenerates its .bak on the next save anyway. The artifact check
        // is scoped to the ResilientJsonStore naming convention itself
        // ("<store>.json.bak" / "<store>.json.corrupt-*").
        // The style-restore journal is in-flight transaction state, not user
        // data: carrying pending/committed/orig files into a backup would
        // land a half-applied restore on the target machine and let a stale
        // journal fire the first time a style restore runs there.
        // The wipe-recovery offer marker is machine-local session state for
        // an in-flight decision: restoring it onto a machine (or back onto
        // this one through an old archive) would re-offer a restore right
        // after the user finished one. Detection rewrites it when warranted.
        if (relativePath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
            relativePath.Contains(".style-restore.", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(relativePath, RecoveryOfferStore.MarkerFileName, StringComparison.OrdinalIgnoreCase) ||
            IsInternalStoreRecoveryArtifact(relativePath))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Managed attachment directories hold user files under their original
    /// names — "attachments/" as a path segment anywhere under the data
    /// root marks user content (widgets/&lt;id&gt;/attachments/,
    /// quick-capture/attachments/, ...), which must never be filtered by
    /// extension or store-sidecar heuristics.
    /// </summary>
    private static bool IsAttachmentPath(string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/');
        return normalized.StartsWith("attachments/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/attachments/", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(long Length, string Sha256)> CopyAndHashAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken,
        long maxBytes = long.MaxValue)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        long totalBytes = 0;
        long bytesSinceProgressMark = 0;
        const long progressMarkIntervalBytes = 32L * 1024 * 1024;
        while (true)
        {
            int bytesRead = await source.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            // Enforce the byte budget on what the stream ACTUALLY yields —
            // the zip central directory's declared Length is attacker-
            // controlled and cannot be trusted to bound inflation.
            if (totalBytes + bytesRead > maxBytes)
            {
                throw new InvalidDataException(
                    "The backup contains too many files or an oversized file.");
            }

            hash.AppendData(buffer, 0, bytesRead);
            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalBytes = checked(totalBytes + bytesRead);
            // A single multi-gigabyte file can outlast the startup watchdog's
            // stall window on slow storage; mark progress per chunk so the
            // copy itself proves the process is alive.
            bytesSinceProgressMark += bytesRead;
            if (bytesSinceProgressMark >= progressMarkIntervalBytes)
            {
                bytesSinceProgressMark = 0;
                App.MarkStartupProgress();
            }
        }

        return (totalBytes, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static void ValidateIntegrityManifest(
        IReadOnlyList<DeskPaneBackupFileManifest>? expectedFiles,
        IReadOnlyDictionary<string, DeskPaneBackupFileManifest> extractedFiles)
    {
        if (expectedFiles is null || expectedFiles.Count == 0)
        {
            throw new InvalidDataException("The backup integrity manifest is missing or empty.");
        }

        var expectedByPath = new Dictionary<string, DeskPaneBackupFileManifest>(StringComparer.OrdinalIgnoreCase);
        foreach (DeskPaneBackupFileManifest expected in expectedFiles)
        {
            if (expected is null ||
                string.IsNullOrWhiteSpace(expected.Path) ||
                expected.Path.Contains('\\') ||
                expected.Path.StartsWith("/", StringComparison.Ordinal) ||
                expected.Path.Split('/').Any(segment => segment is "" or "." or "..") ||
                !expectedByPath.TryAdd(expected.Path, expected))
            {
                throw new InvalidDataException("The backup integrity manifest contains an invalid path.");
            }

            if (expected.Length < 0 ||
                string.IsNullOrWhiteSpace(expected.Sha256) ||
                expected.Sha256.Length != 64 ||
                !expected.Sha256.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException(
                    $"The backup integrity entry for '{expected.Path}' is invalid.");
            }
        }

        if (expectedByPath.Count != extractedFiles.Count)
        {
            throw new InvalidDataException("The backup file list does not match its integrity manifest.");
        }

        foreach ((string path, DeskPaneBackupFileManifest actual) in extractedFiles)
        {
            if (!expectedByPath.TryGetValue(path, out DeskPaneBackupFileManifest? expected) ||
                expected.Length != actual.Length ||
                !string.Equals(expected.Sha256, actual.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Backup integrity validation failed for '{path}'.");
            }
        }
    }

    private static bool IsBackupFromNewerApp(string? backupVersion)
    {
        string? currentVersion = typeof(DeskPaneDataBackupService).Assembly.GetName().Version?.ToString();
        return TryParseVersion(backupVersion, out Version? backup) &&
               TryParseVersion(currentVersion, out Version? current) &&
               backup > current;
    }

    private static bool TryParseVersion(string? value, out Version? version)
    {
        string normalized = (value ?? string.Empty).Split(['-', '+'], 2)[0];
        return Version.TryParse(normalized, out version);
    }

    private static string EnsureTrailingDirectorySeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) ||
               path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private sealed record DeskPaneBackupManifest(
        int SchemaVersion,
        string Kind,
        DateTimeOffset CreatedAtUtc,
        string AppVersion,
        string? SourceDataPath = null,
        IReadOnlyList<DeskPaneBackupFileManifest>? Files = null,
        // Cloud-backup domain names (CloudBackupDomains.ToManifestName).
        // Absent on classic full backups; a non-empty list means the archive
        // holds ONLY those domains and must go through the scoped restore —
        // a whole-directory swap would wipe everything else. Null stays
        // unwritten so classic manifests keep their canonical key set.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<string>? Domains = null,
        // Scoped cloud backups only: the full device id that produced the
        // archive (the file name only carries the 8-char suffix). Surfaced
        // in the restore confirmation; never used to gate restores — a
        // wiped device regenerates its id and still owns its backups.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? SourceDeviceId = null,
        // Integrity entry for the root-level widget-style.json — it lives
        // outside data/ so it cannot ride in Files. Optional: archives
        // written before this field existed simply skip the check.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        DeskPaneBackupFileManifest? WidgetStyleFile = null);

    private sealed record DeskPaneBackupFileManifest(
        string Path,
        long Length,
        string Sha256);

    private sealed record RestoreArchiveInfo(
        DeskPaneBackupManifest Manifest,
        int FileCount,
        long TotalUncompressedBytes);

    private sealed record PendingRestoreMarker(
        string StagingRoot,
        string ArchivePath,
        DateTimeOffset PreparedAtUtc,
        DateTimeOffset BackupCreatedAtUtc,
        string AppVersion,
        // Non-empty → scoped cloud restore: replace only the listed domains'
        // files instead of swapping the whole data directory. Null stays
        // unwritten so classic markers keep their canonical key set.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<string>? Domains = null,
        // A "pre-restore" safety archive legitimately lacks settings.json —
        // it captured whatever survived on a device that lost it. Refusing
        // to restore it would strand the very data it exists to protect.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        bool AllowMissingSettings = false,
        // The safety archive belongs to this pending-restore transaction,
        // not to a single apply attempt: retries reuse the FIRST snapshot
        // (the only one holding pre-restore data) and pruning must never
        // age it out while the marker lives.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? SafetyBackupPath = null,
        // Scoped restores only: the item-restore mode the user picked in
        // the confirm dialog. True = snapshot-faithful domain replace;
        // false/absent = additive merge (nothing local is deleted). The
        // Existing markers without a confirmation field keep their original
        // additive-merge fallback for compatibility.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        bool ReplaceItemData = false,
        // Bounded retries: a deterministically failing scoped restore
        // (e.g. an archive from a future schema) must not retry on every
        // boot forever — the pending marker also blocks scheduled uploads.
        // Rewritten atomically on each failed apply.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        int ApplyAttemptCount = 0,
        // New scoped markers are explicitly unconfirmed until the user
        // chooses an item mode. Null means a pre-existing marker or full
        // restore and retains its historical apply behavior.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        bool? ScopedRestoreConfirmed = null,
        // Full restores are likewise unconfirmed until the user answers the
        // restore dialog: the marker is written before that dialog opens, so
        // a crash, hidden window, or exit in between must never let the next
        // launch swap the whole data directory. Markers from versions
        // without this field deserialize as false and are discarded — the
        // safe direction, since the user simply re-runs the restore.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        bool FullRestoreConfirmed = false,
        // Full restores only: the source machine's data directory recorded
        // by the backup manifest. The apply swap moves only files, so after
        // it lands this is the sole trace of where the archived settings'
        // machine-local paths (managed storage root, custom snapshot
        // folder) originally lived — the post-restore recovery step derives
        // the source user profile from it. Null on scoped markers and on
        // markers written before this field existed; those launches simply
        // skip the rebase.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? SourceDataPath = null);

    [JsonSourceGenerationOptions(
        GenerationMode = JsonSourceGenerationMode.Metadata,
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        WriteIndented = true)]
    [JsonSerializable(
        typeof(DeskPaneBackupManifest),
        TypeInfoPropertyName = "BackupManifest")]
    [JsonSerializable(
        typeof(DeskPaneBackupFileManifest),
        TypeInfoPropertyName = "BackupFileManifest")]
    [JsonSerializable(
        typeof(PendingRestoreMarker),
        TypeInfoPropertyName = "PendingRestoreMarker")]
    private sealed partial class BackupJsonContext : JsonSerializerContext
    {
    }
}

public sealed record DeskPaneBackupSnapshotInfo(
    string Path,
    string Kind,
    DateTimeOffset CreatedAtUtc,
    long SizeBytes,
    bool IsReadable,
    string? AppVersion,
    int SchemaVersion);

/// <summary>
/// Effective automatic-snapshot folder state for the settings UI. A configured
/// directory that is not active means snapshots currently fall back to the
/// default recovery directory.
/// </summary>
public sealed record AutomaticBackupDirectoryStatus(
    string? ConfiguredDirectory,
    string EffectiveDirectory,
    bool IsCustomDirectoryActive);

internal sealed record SnapshotManifestSummary(
    bool IsReadable,
    DateTimeOffset? CreatedAtUtc,
    string? AppVersion,
    int SchemaVersion)
{
    public static SnapshotManifestSummary Unreadable { get; } = new(false, null, null, 0);
}

public sealed record DeskPaneRestorePreparation(
    DateTimeOffset BackupCreatedAtUtc,
    string AppVersion,
    int FileCount,
    long TotalUncompressedBytes,
    int BackupSchemaVersion,
    bool HasIntegrityManifest,
    // Non-empty on scoped cloud restores — the domains that will be applied.
    IReadOnlyList<string>? Domains = null,
    // Scoped cloud restores only: full device id recorded in the archive
    // manifest (informational — never a restore gate).
    string? SourceDeviceId = null,
    // Scoped restores only: staged todo stores whose source widget id does
    // not exist on this device were remapped onto live todo widgets so the
    // restored data stays visible.
    IReadOnlyList<DeskPaneTodoWidgetRemap>? TodoWidgetRemaps = null,
    // Source widget ids that could not be remapped (no free todo widget on
    // this device). Their files are still restored under the source id —
    // preserved on disk even though no widget currently reads them.
    IReadOnlyList<string>? UnmappedTodoWidgetIds = null,
    // Scoped cloud restores only: live-item count per applied item domain
    // (todo-data, quick-capture-data), so the confirm dialog can say "this
    // domain holds 0 items" instead of letting an empty domain silently
    // wipe local data. WidgetStyle is a settings projection, not item
    // data, and never appears here.
    IReadOnlyList<DeskPaneDomainItemCount>? DomainItemCounts = null,
    // Scoped restores only: attachment/image references inside the staged
    // item data. Attachment FILES never ship in a cloud backup, so the
    // confirm dialog warns that these references dangle after a
    // cross-device restore.
    int AttachmentReferenceCount = 0,
    // True when the archive was written by a newer DeskPane build. Restore
    // is allowed — the schema version is the real compatibility gate — but
    // the confirm dialog warns that newer-version-only fields may not
    // survive being saved again by this older build.
    bool IsFromNewerAppVersion = false);

/// <summary>One todo-store directory remapped from source to target widget id.</summary>
public sealed record DeskPaneTodoWidgetRemap(string SourceWidgetId, string TargetWidgetId);

/// <summary>
/// Planned staging mutations for a scoped todo restore: directory remaps,
/// item-level merges (source store folded into a target when the snapshot
/// has more lists than this device has free widgets), source ids that could
/// not land on any live widget, and staged dirs the archive's own style
/// document does not list as live on the source device (orphan debris).
/// </summary>
internal sealed record TodoRestorePlan(
    IReadOnlyList<DeskPaneTodoWidgetRemap> Remaps,
    IReadOnlyList<DeskPaneTodoWidgetRemap> Merges,
    IReadOnlyList<string> Unmapped,
    IReadOnlyList<string> JunkSourceIds)
{
    internal static TodoRestorePlan Empty { get; } = new(
        Array.Empty<DeskPaneTodoWidgetRemap>(),
        Array.Empty<DeskPaneTodoWidgetRemap>(),
        Array.Empty<string>(),
        Array.Empty<string>());
}

/// <summary>
/// Live-item count for one applied item domain in a scoped restore —
/// <paramref name="Domain"/> is the manifest name
/// (<see cref="CloudBackupDomains.ToManifestName"/>), <paramref name="Items"/>
/// the non-deleted entries the domain will actually restore.
/// </summary>
public sealed record DeskPaneDomainItemCount(string Domain, int Items);

internal enum DeskPaneRestoreApplyKind
{
    None = 0,
    Full,
    Scoped,
}

internal sealed record DeskPaneRestoreApplyResult(
    bool HadPendingRestore,
    bool Succeeded,
    string? ErrorMessage,
    DeskPaneRestoreApplyKind Kind = DeskPaneRestoreApplyKind.None,
    string? SourceDataPath = null)
{
    public static DeskPaneRestoreApplyResult NoPendingRestore { get; } = new(false, true, null);
}
