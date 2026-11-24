using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Pure logical grouping policy for Scheme A (in-place virtual desktop groups):
/// desktop files stay on the real Windows desktop without creating managed
/// folders or moving files on disk.
/// </summary>
public static class VirtualDesktopGroupPolicy
{
    public const string FoldersCategoryId = "folders";

    private static readonly DesktopOrganizationClassifier Classifier = new();

    /// <summary>
    /// Returns true if <paramref name="path"/> is a direct child of the user's
    /// personal desktop or the public desktop.
    /// </summary>
    public static bool IsDesktopItemPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return false;
        }

        string? parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent))
        {
            return false;
        }

        var (userDesktop, publicDesktop) = FileService.GetDesktopPaths();
        return string.Equals(parent, userDesktop, StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrWhiteSpace(publicDesktop) &&
                string.Equals(parent, publicDesktop, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Decides whether a desktop item at <paramref name="itemPath"/> belongs to
    /// <paramref name="targetWidget"/>.
    /// Explicit assignment in <see cref="WidgetConfig.Items"/> always wins;
    /// otherwise unassigned desktop items are routed by
    /// <see cref="WidgetConfig.VirtualDesktopCategoryId"/>.
    /// </summary>
    public static bool ShouldIncludeItem(
        WidgetConfig targetWidget,
        IReadOnlyList<WidgetConfig> allWidgets,
        string itemPath)
    {
        if (!targetWidget.IsVirtualDesktopGroup || string.IsNullOrWhiteSpace(itemPath))
        {
            return false;
        }

        string normalizedPath = NormalizePath(itemPath);

        // 1. Explicitly assigned to this virtual group.
        if (ContainsPath(targetWidget.Items, normalizedPath))
        {
            return true;
        }

        // 2. Explicitly assigned to another active virtual group -> do not duplicate.
        for (int i = 0; i < allWidgets.Count; i++)
        {
            WidgetConfig other = allWidgets[i];
            if (ReferenceEquals(other, targetWidget) ||
                string.Equals(other.Id, targetWidget.Id, StringComparison.Ordinal) ||
                !other.IsVirtualDesktopGroup ||
                other.IsDisabled)
            {
                continue;
            }

            if (ContainsPath(other.Items, normalizedPath))
            {
                return false;
            }
        }

        // 3. Auto-route unassigned desktop items by category id.
        if (string.IsNullOrWhiteSpace(targetWidget.VirtualDesktopCategoryId))
        {
            return false;
        }

        string itemCategory = ResolveItemCategoryId(normalizedPath);
        return string.Equals(
            targetWidget.VirtualDesktopCategoryId,
            itemCategory,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Logically assigns <paramref name="paths"/> to <paramref name="targetWidget"/>
    /// without touching the filesystem. Removes those paths from any other
    /// virtual desktop group and returns the IDs of all affected widgets.
    /// </summary>
    public static IReadOnlyList<string> AssignPathsToVirtualGroup(
        WidgetConfig targetWidget,
        IReadOnlyList<WidgetConfig> allWidgets,
        IEnumerable<string> paths)
    {
        var affectedIds = new HashSet<string>(StringComparer.Ordinal) { targetWidget.Id };
        var normalizedPaths = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedPaths.Count == 0)
        {
            return [targetWidget.Id];
        }

        // Remove from all other virtual desktop groups.
        for (int i = 0; i < allWidgets.Count; i++)
        {
            WidgetConfig other = allWidgets[i];
            if (string.Equals(other.Id, targetWidget.Id, StringComparison.Ordinal) ||
                !other.IsVirtualDesktopGroup)
            {
                continue;
            }

            int removed = other.Items.RemoveAll(item =>
                normalizedPaths.Contains(NormalizePath(item.Path), StringComparer.OrdinalIgnoreCase));
            if (removed > 0)
            {
                affectedIds.Add(other.Id);
            }
        }

        // Add to target virtual group if not already present.
        int nextOrder = targetWidget.Items.Count > 0
            ? targetWidget.Items.Max(item => item.SortOrder) + 1
            : 0;
        foreach (string path in normalizedPaths)
        {
            if (!ContainsPath(targetWidget.Items, path))
            {
                targetWidget.Items.Add(new WidgetItemConfig
                {
                    Path = path,
                    SortOrder = nextOrder++
                });
            }
        }

        return affectedIds.ToList();
    }

    public static string ResolveItemCategoryId(string itemPath)
    {
        if (Directory.Exists(itemPath))
        {
            return FoldersCategoryId;
        }

        return Classifier.Classify(itemPath).CategoryId;
    }

    private static bool ContainsPath(IReadOnlyList<WidgetItemConfig> items, string normalizedPath)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (string.Equals(NormalizePath(items[i].Path), normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim();
        }
    }
}
