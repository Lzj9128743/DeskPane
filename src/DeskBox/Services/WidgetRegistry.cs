using DeskBox.Models;

namespace DeskBox.Services;

public sealed record WidgetKindRegistration(
    WidgetKind WidgetKind,
    bool CanCreateWindow,
    bool IsImplemented);

/// <summary>
/// Central registry for widget kinds known to DeskBox.
/// </summary>
public sealed class WidgetRegistry
{
    private readonly IReadOnlyDictionary<WidgetKind, WidgetKindRegistration> _registrations;

    public static WidgetRegistry Default { get; } = CreateDefault();

    public WidgetRegistry(IEnumerable<WidgetKindRegistration> registrations)
    {
        _registrations = registrations.ToDictionary(registration => registration.WidgetKind);
    }

    public bool IsKnown(WidgetKind widgetKind) =>
        _registrations.ContainsKey(widgetKind);

    public bool CanCreateWindow(WidgetKind widgetKind) =>
        _registrations.TryGetValue(widgetKind, out var registration) &&
        registration.CanCreateWindow;

    public bool IsImplemented(WidgetKind widgetKind) =>
        _registrations.TryGetValue(widgetKind, out var registration) &&
        registration.IsImplemented;

    public bool IsAvailableForSession(WidgetConfig widget, AppSettings settings) =>
        CanCreateWindow(widget.WidgetKind);

    private static WidgetRegistry CreateDefault() =>
        new([new(WidgetKind.File, CanCreateWindow: true, IsImplemented: true)]);
}
