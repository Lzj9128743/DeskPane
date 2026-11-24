using DeskPane.Models;

namespace DeskPane.Services;

public sealed partial class DesktopOrganizationTransaction
{
    internal static SemaphoreSlim OperationGate { get; } = new(1, 1);

    private readonly SettingsService _settingsService;
    private readonly FileService _fileService;
    private readonly DesktopOrganizationRecoveryStore _recoveryStore;
    private readonly IDesktopOrganizationTransfer _transfer;
    private readonly WidgetManager? _widgetManager;

    public bool HasPendingRecovery => _recoveryStore.HasPendingJournal;

    internal DesktopAutoOrganizationSuppressionRegistry? AutoOrganizationSuppressions { get; init; }

    public DesktopOrganizationTransaction(
        SettingsService settingsService,
        FileService fileService,
        DesktopOrganizationRecoveryStore? recoveryStore = null,
        IDesktopOrganizationTransfer? transfer = null,
        WidgetManager? widgetManager = null)
    {
        _settingsService = settingsService;
        _fileService = fileService;
        _transfer = transfer ?? new DesktopOrganizationTransfer(fileService);
        _recoveryStore = recoveryStore ?? new DesktopOrganizationRecoveryStore();
        _widgetManager = widgetManager;
    }

    public async Task<DesktopOrganizationExecutionResult> ExecuteAsync(
        DesktopOrganizationPlan plan,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(plan, progress: null, cancellationToken);
    }

    public async Task<DesktopOrganizationExecutionResult> ExecuteAsync(
        DesktopOrganizationPlan plan,
        IProgress<DesktopOrganizationProgress>? progress,
        CancellationToken cancellationToken = default,
        IntPtr ownerWindowHandle = default)
    {
        await OperationGate.WaitAsync(cancellationToken);
        try
        {
            if (HasPendingRecovery)
                throw new DesktopOrganizationPendingRecoveryException();
            ValidatePlan(plan);

            var settings = _settingsService.Settings;
            var historyEntries = _settingsService.OrganizationHistory.Entries;
            var originalWidgets = settings.Widgets.ToList();
            var originalRules = settings.DesktopOrganizationRules.ToList();
            var originalHistory = historyEntries.ToList();
            var retainedItems = new List<DesktopOrganizationRetainedItem>();
            var createdWidgets = CreateCandidateWidgets(plan, settings);
            bool committed = false;

            try
            {
                string userDesktop = FileService.GetDesktopPaths().UserDesktop;
                int totalCount = plan.Targets.Sum(target => target.Items.Count);
                int completedCount = 0;
                var completedItems = new List<OrganizationHistoryItem>(totalCount);
                var historyTargets = new List<OrganizationHistoryTarget>(plan.Targets.Count);

                foreach (DesktopOrganizationTargetPlan target in plan.Targets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WidgetConfig? widget = settings.Widgets.FirstOrDefault(candidate =>
                        string.Equals(candidate.Id, target.TargetWidgetId, StringComparison.Ordinal));
                    if (widget is null)
                    {
                        continue;
                    }

                    widget.IsVirtualDesktopGroup = true;
                    widget.FollowsDefaultStoragePath = false;
                    widget.ManagedFolderName = null;
                    widget.MappedFolderPath = userDesktop;
                    if (string.IsNullOrWhiteSpace(widget.VirtualDesktopCategoryId) &&
                        !string.IsNullOrWhiteSpace(target.CategoryId))
                    {
                        widget.VirtualDesktopCategoryId = target.CategoryId;
                    }

                    VirtualDesktopGroupPolicy.AssignPathsToVirtualGroup(
                        widget,
                        settings.Widgets,
                        target.Items.Select(item => item.SourcePath));

                    historyTargets.Add(new OrganizationHistoryTarget
                    {
                        WidgetId = widget.Id,
                        WidgetName = widget.Name,
                        DirectoryPath = userDesktop,
                        WasCreated = target.CreatesWidget
                    });

                    foreach (DesktopOrganizationFileSnapshot item in target.Items)
                    {
                        completedItems.Add(new OrganizationHistoryItem
                        {
                            Name = Path.GetFileName(item.SourcePath),
                            SourcePath = item.SourcePath,
                            DestinationPath = item.SourcePath,
                            TargetWidgetId = widget.Id,
                            TargetWidgetName = widget.Name,
                            SourceScope = item.SourceScope,
                            Size = item.Size,
                            LastWriteTimeUtc = item.LastWriteTimeUtc
                        });
                        completedCount++;
                    }

                    progress?.Report(new DesktopOrganizationProgress(
                        completedCount,
                        totalCount,
                        widget.Id,
                        widget.Name,
                        target.Items.LastOrDefault()?.SourceScope ?? DesktopOrganizationSourceScope.Personal));
                }

                AddRulesForNewTargets(plan, settings.DesktopOrganizationRules);
                var history = new OrganizationHistoryEntry
                {
                    Id = plan.Id,
                    ActionType = OrganizationActionType.DesktopOrganization,
                    TransferMode = "Virtual",
                    CanUndo = false,
                    WidgetName = "Desktop organization",
                    Targets = historyTargets,
                    Items = completedItems,
                    IsUndone = false
                };
                if (history.Items.Count > 0)
                {
                    historyEntries.Insert(0, history);
                }

                if (!await _settingsService.SaveCheckedAsync(notifySubscribers: false) ||
                    !await _settingsService.OrganizationHistory.SaveCheckedAsync())
                {
                    throw new IOException("Persisting the virtual desktop organization commit failed.");
                }

                committed = true;
                if (OrganizationHistoryPolicy.ApplyRetentionPolicy(historyEntries))
                {
                    await _settingsService.OrganizationHistory.SaveCheckedAsync();
                    await _settingsService.SaveAsync(notifySubscribers: false);
                }

                return new DesktopOrganizationExecutionResult
                {
                    History = history,
                    CompletedItems = completedItems,
                    CreatedWidgets = createdWidgets,
                    RetainedItems = retainedItems
                };
            }
            catch
            {
                if (committed)
                {
                    throw;
                }

                settings.Widgets = originalWidgets;
                settings.DesktopOrganizationRules = originalRules;
                _settingsService.OrganizationHistory.Entries = originalHistory;
                await _settingsService.OrganizationHistory.SaveCheckedAsync();
                await _settingsService.SaveAsync(notifySubscribers: false);
                throw;
            }
        }
        finally
        {
            OperationGate.Release();
        }
    }

    private static void ValidatePlan(DesktopOrganizationPlan plan)
    {
        if (plan.Targets.Count == 0 || plan.EligibleItemCount == 0)
        {
            throw new InvalidOperationException("The desktop organization plan is empty.");
        }

        string desktop = Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.DesktopPath));
        string storage = Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.StorageRootPath));
        string? storageRoot = Path.GetPathRoot(storage);
        if (!string.IsNullOrWhiteSpace(storageRoot) &&
            string.Equals(
                storage,
                Path.TrimEndingDirectorySeparator(storageRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new DesktopOrganizationInvalidStorageRootException(
                "A drive root cannot be used as the managed storage folder.");
        }

        if (string.Equals(desktop, storage, StringComparison.OrdinalIgnoreCase) ||
            storage.StartsWith($"{desktop}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            throw new DesktopOrganizationInvalidStorageRootException(
                "The managed storage root cannot be the desktop or one of its subfolders.");
        }

        if (!string.IsNullOrWhiteSpace(plan.PublicDesktopPath) && FileService.PathsOverlap(storage, plan.PublicDesktopPath))
            throw new DesktopOrganizationInvalidStorageRootException("Managed storage overlaps the public desktop.");
        foreach (var item in plan.Targets.SelectMany(target => target.Items))
        {
            string root = item.SourceScope == DesktopOrganizationSourceScope.Public ? plan.PublicDesktopPath : plan.DesktopPath;
            if (string.IsNullOrWhiteSpace(root) || !item.IsEligible ||
                (item.SourceScope == DesktopOrganizationSourceScope.Public && !plan.IncludePublicDesktop) ||
                (item.SourceScope == DesktopOrganizationSourceScope.Personal && !plan.IncludePersonalDesktop) ||
                !string.Equals(Path.GetDirectoryName(Path.GetFullPath(item.SourcePath)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("An item is outside the selected desktop scope.");
        }

        foreach (DesktopOrganizationTargetPlan target in plan.Targets)
        {
            string directory = Path.GetFullPath(target.TargetDirectoryPath);
            if (FileService.PathsOverlap(directory, desktop) ||
                (!string.IsNullOrWhiteSpace(plan.PublicDesktopPath) && FileService.PathsOverlap(directory, plan.PublicDesktopPath)))
            {
                throw new InvalidOperationException(
                    "An organization target cannot be the desktop or one of its subfolders.");
            }
            if (!directory.StartsWith(
                    $"{storage}{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(directory, storage, StringComparison.OrdinalIgnoreCase))
            {
                // Reused mapped widgets may intentionally live outside the
                // default root. Only new widget destinations are constrained.
                if (target.CreatesWidget)
                {
                    throw new InvalidOperationException("A new organization target is outside the managed storage root.");
                }
            }
        }
    }

    internal const long FreeSpaceSafetyMarginBytes = 16L * 1024 * 1024;

    internal static void ValidateAvailableSpace(
        DesktopOrganizationPlan plan,
        Func<string, long>? availableFreeBytesProvider = null)
    {
        foreach (var requirement in ComputeRequiredSpaceByDrive(plan))
        {
            long availableFreeBytes = availableFreeBytesProvider is not null
                ? availableFreeBytesProvider(requirement.Key)
                : new DriveInfo(requirement.Key).AvailableFreeSpace;
            if (availableFreeBytes < requirement.Value + FreeSpaceSafetyMarginBytes)
            {
                throw new DesktopOrganizationInsufficientSpaceException(requirement.Key);
            }
        }
    }

    /// <summary>
    /// A same-volume move is a metadata rename and consumes no additional
    /// space; only items crossing volumes are charged against the target
    /// drive. Directory sizes are not recursed (existing behavior).
    /// </summary>
    internal static IReadOnlyDictionary<string, long> ComputeRequiredSpaceByDrive(
        DesktopOrganizationPlan plan)
    {
        var requiredByDrive = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in plan.Targets
                     .SelectMany(target => target.Items.Select(item => new
                     {
                         Target = target.TargetDirectoryPath,
                         Snapshot = item
                     })))
        {
            string? targetRoot = Path.GetPathRoot(Path.GetFullPath(entry.Target));
            if (string.IsNullOrWhiteSpace(targetRoot) ||
                targetRoot.StartsWith(@"\\", StringComparison.Ordinal))
            {
                continue;
            }

            string? sourceRoot = Path.GetPathRoot(Path.GetFullPath(entry.Snapshot.SourcePath));
            if (string.Equals(targetRoot, sourceRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            requiredByDrive.TryGetValue(targetRoot, out long current);
            requiredByDrive[targetRoot] = current + entry.Snapshot.Size;
        }

        return requiredByDrive;
    }

    private static void RevalidateSource(DesktopOrganizationFileSnapshot item)
    {
        var attributes = File.GetAttributes(item.SourcePath);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Offline | FileAttributes.Temporary)) != 0)
            throw new DesktopOrganizationSourceChangedException(item.Name);
        if (item.IsDirectory)
        {
            var directory = new DirectoryInfo(item.SourcePath);
            if (!directory.Exists ||
                directory.LastWriteTimeUtc != item.LastWriteTimeUtc)
            {
                throw new DesktopOrganizationSourceChangedException(item.Name);
            }

            return;
        }

        var file = new FileInfo(item.SourcePath);
        if (!file.Exists ||
            file.Length != item.Size ||
            file.LastWriteTimeUtc != item.LastWriteTimeUtc)
        {
            throw new DesktopOrganizationSourceChangedException(item.Name);
        }
    }

    private List<WidgetConfig> CreateCandidateWidgets(
        DesktopOrganizationPlan plan,
        AppSettings settings)
    {
        var created = new List<WidgetConfig>();
        string userDesktop = FileService.GetDesktopPaths().UserDesktop;
        foreach (DesktopOrganizationTargetPlan target in plan.Targets.Where(target => target.CreatesWidget))
        {
            var bounds = target.PlannedBounds;
            var config = new WidgetConfig
            {
                Id = target.TargetWidgetId,
                Name = target.SuggestedDisplayName,
                IsDefaultTitle = false,
                WidgetKind = WidgetKind.File,
                IsVirtualDesktopGroup = true,
                VirtualDesktopCategoryId = target.CategoryId,
                MappedFolderPath = userDesktop,
                FollowsDefaultStoragePath = false,
                ManagedFolderName = null,
                BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
                Width = settings.DefaultWidgetWidth,
                Height = settings.DefaultWidgetHeight,
                IsVisible = true
            };
            // Organizer-planned rects flow through the placement funnel as
            // explicit bounds (spec 5.8 step 1); without a manager (older
            // callers) the planned rect is applied directly as before.
            if (_widgetManager is not null)
            {
                _widgetManager.ApplyNewWidgetPlacement(
                    config,
                    new NewWidgetPlacementContext(
                        bounds is null
                            ? null
                            : new Windows.Graphics.RectInt32(
                                (int)Math.Round(bounds.Value.X),
                                (int)Math.Round(bounds.Value.Y),
                                Math.Max(1, (int)Math.Round(bounds.Value.Width)),
                                Math.Max(1, (int)Math.Round(bounds.Value.Height)))));
            }
            else
            {
                config.X = bounds?.X ?? 100;
                config.Y = bounds?.Y ?? 100;
            }

            settings.Widgets.Add(config);
            created.Add(config);
        }

        return created;
    }

    private static DesktopOrganizationRecoveryJournal BuildJournal(DesktopOrganizationPlan plan)
    {
        return new DesktopOrganizationRecoveryJournal
        {
            TransactionId = plan.Id,
            CreatedWidgetIds = plan.Targets
                .Where(target => target.CreatesWidget)
                .Select(target => target.TargetWidgetId)
                .ToList(),
            Items = plan.Targets
                .SelectMany(target => target.Items.Select(item => new DesktopOrganizationRecoveryItem
                {
                    SourceScope = item.SourceScope,
                    Size = item.IsDirectory ? null : item.Size,
                    LastWriteTimeUtc = item.LastWriteTimeUtc,
                    SourcePath = item.SourcePath,
                    TargetWidgetId = target.TargetWidgetId,
                    DestinationPath = Path.Combine(target.TargetDirectoryPath, item.Name)
                }))
                .ToList()
        };
    }

    private static void ReserveDestinations(DesktopOrganizationRecoveryJournal journal)
    {
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DesktopOrganizationRecoveryItem item in journal.Items)
        {
            item.DestinationPath = FileService.GetAvailablePath(item.DestinationPath, reserved);
        }
    }

    private static void AddRulesForNewTargets(
        DesktopOrganizationPlan plan,
        ICollection<DesktopOrganizationRule> rules)
    {
        foreach (DesktopOrganizationTargetPlan target in plan.Targets.Where(target => target.CreatesWidget))
        {
            string[] sourceCategories = target.Items
                .Select(item => item.CategoryId)
                .Where(categoryId => !string.IsNullOrWhiteSpace(categoryId))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            rules.Add(new DesktopOrganizationRule
            {
                TargetWidgetId = target.TargetWidgetId,
                CategoryIds = sourceCategories.Length > 0
                    ? sourceCategories.ToList()
                    : [target.CategoryId]
            });
        }
    }

    private static OrganizationHistoryEntry CreateHistory(
        DesktopOrganizationPlan plan,
        DesktopOrganizationRecoveryJournal journal)
    {
        var targetsById = plan.Targets.ToDictionary(target => target.TargetWidgetId, StringComparer.Ordinal);
        return new OrganizationHistoryEntry
        {
            Id = plan.Id,
            ActionType = OrganizationActionType.DesktopOrganization,
            TransferMode = "Move",
            CanUndo = journal.Items.Any(item => item.Completed),
            WidgetName = "Desktop organization",
            Targets = plan.Targets.Select(target => new OrganizationHistoryTarget
            {
                WidgetId = target.TargetWidgetId,
                WidgetName = target.SuggestedDisplayName,
                DirectoryPath = target.TargetDirectoryPath,
                WasCreated = target.CreatesWidget
            }).ToList(),
            Items = journal.Items
                .Where(item => item.Completed)
                .Select(item => new OrganizationHistoryItem
            {
                Name = Path.GetFileName(item.DestinationPath),
                SourcePath = item.SourcePath,
                DestinationPath = item.DestinationPath,
                TargetWidgetId = item.TargetWidgetId,
                TargetWidgetName = targetsById[item.TargetWidgetId].SuggestedDisplayName,
                SourceScope = item.SourceScope,
                Size = item.Size,
                LastWriteTimeUtc = item.LastWriteTimeUtc,
                // The receipt captured at move time travels with the history:
                // undo must verify against the object that was moved, not
                // whatever later occupies the destination path.
                DestinationIdentity = item.DestinationIdentity
                }).ToList()
        };
    }

    private static DesktopOrganizationPlan CreateCommittedPlan(
        DesktopOrganizationPlan plan,
        DesktopOrganizationRecoveryJournal journal)
    {
        var completedPathsByTarget = journal.Items
            .Where(item => item.Completed)
            .GroupBy(item => item.TargetWidgetId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(item => item.SourcePath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.Ordinal);
        return new DesktopOrganizationPlan
        {
            Id = plan.Id,
            DesktopPath = plan.DesktopPath,
            StorageRootPath = plan.StorageRootPath,
            ExcludedItems = plan.ExcludedItems.ToList(),
            Targets = plan.Targets
                .Where(target => completedPathsByTarget.ContainsKey(target.TargetWidgetId))
                .Select(target => target.CloneWith(
                    target.TargetWidgetId,
                    target.SuggestedDisplayName,
                    target.TargetDirectoryPath,
                    target.CreatesWidget,
                    target.Items.Where(item =>
                        completedPathsByTarget[target.TargetWidgetId].Contains(
                            item.SourcePath))))
                .Where(target => target.Items.Count > 0)
                .ToList()
        };
    }

    private static DesktopOrganizationRetainedItem CreateRetainedItem(
        DesktopOrganizationFileSnapshot item,
        Exception exception)
    {
        if (exception is FileService.FileTransferPartialFailureException { InnerException: { } cause })
            exception = cause;
        DesktopOrganizationRetentionReason reason = exception switch
        {
            DesktopOrganizationSourceChangedException =>
                DesktopOrganizationRetentionReason.SourceChanged,
            OperationCanceledException => DesktopOrganizationRetentionReason.Canceled,
            UnauthorizedAccessException =>
                DesktopOrganizationRetentionReason.AccessDenied,
            FileNotFoundException or DirectoryNotFoundException =>
                DesktopOrganizationRetentionReason.Unavailable,
            IOException ioException when IsSharingViolation(ioException) =>
                DesktopOrganizationRetentionReason.InUse,
            IOException => DesktopOrganizationRetentionReason.Unavailable,
            _ => DesktopOrganizationRetentionReason.TransferFailed
        };
        return new DesktopOrganizationRetainedItem(
            item.SourcePath,
            item.Name,
            reason,
            exception.Message,
            item.SourceScope);
    }

    private static bool IsSharingViolation(IOException exception)
    {
        int code = exception.HResult & 0xFFFF;
        return code is 32 or 33;
    }

    /// <summary>
    /// Captures the object identity at the item's final resting path right
    /// after a physical move completed (files and directories alike). For a
    /// forward journal that path is the organization destination; for an undo
    /// journal it is the restore path. Recovery may only move the item again
    /// while the object at that path still carries this identity; a failed
    /// capture leaves it null, which means no automatic authority.
    /// </summary>
    private static void RecordDestinationIdentity(DesktopOrganizationRecoveryItem item) =>
        RecordDestinationIdentity(item, item.DestinationPath);

    internal static void RecordDestinationIdentity(
        DesktopOrganizationRecoveryItem item,
        string path)
    {
        // Captures only the object identity. The Size/LastWriteTimeUtc
        // snapshot keeps the values from organization time — they are the
        // conjunction baseline, so a file whose content changed after the
        // move (same object id, different size/mtime) still fails the match.
        if (FileService.TryCaptureSourceIdentity(path) is not { } identity ||
            identity.FileId is not { } fileId)
        {
            item.DestinationIdentity = null;
            return;
        }

        item.DestinationIdentity = new DesktopOrganizationDestinationIdentity
        {
            VolumeSerialNumber = identity.VolumeSerialNumber,
            FileIdHigh = fileId.High,
            FileIdLow = fileId.Low,
        };
    }

    private static bool EntryExists(string path) =>
        File.Exists(path) || Directory.Exists(path);

    private static void RemoveEmptyCreatedDirectories(IEnumerable<string> directories)
    {
        foreach (string directory in directories
                     .OrderByDescending(path => path.Length))
        {
            try
            {
                if (Directory.Exists(directory) &&
                    !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch
            {
            }
        }
    }

    private sealed class DesktopOrganizationSourceChangedException(string itemName)
        : IOException($"The desktop item changed after it was scanned: {itemName}");
}
