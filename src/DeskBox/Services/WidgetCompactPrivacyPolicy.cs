using DeskBox.Models;

namespace DeskBox.Services;

public static class WidgetCompactPrivacyPolicy
{
    public static bool HidesSensitiveContent(bool enabled, WidgetKind widgetKind) =>
        enabled && widgetKind == WidgetKind.File;

    public static string ResolveContentMode(
        string? contentMode,
        bool enabled,
        WidgetKind widgetKind)
    {
        string normalized = SettingsService.NormalizeWidgetCompactContentMode(contentMode);
        bool hidesSmartDetail =
            HidesSensitiveContent(enabled, widgetKind) &&
            normalized == SettingsService.WidgetCompactContentModeSmart &&
            widgetKind == WidgetKind.File;
        return hidesSmartDetail
            ? SettingsService.WidgetCompactContentModeSummary
            : normalized;
    }
}
