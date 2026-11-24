using DeskBox.Contracts;
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using Microsoft.UI.Xaml;

namespace DeskBox.Services;

/// <summary>
/// Creates widget content views without owning host windows or z-order behavior.
/// </summary>
public sealed class WidgetContentFactory
{
    private readonly LocalizationService _localizationService;

    public WidgetContentFactory(LocalizationService localizationService)
    {
        _localizationService = localizationService;
    }

    internal static readonly IReadOnlyList<WidgetContentDescriptor> DescriptorList =
    [
        new(
            WidgetKind.File,
            "DeskBox",
            "\uE8A5",
            WidgetContentStage.Implemented,
            CanShowInCreateEntry: true,
            WidgetContentAvailability.Available,
            "WidgetContent.File.StatusLabel",
            "WidgetContent.File.StatusDescription",
            "Common.NewWidget")
    ];

    private static readonly IReadOnlyDictionary<WidgetKind, WidgetContentDescriptor> Descriptors =
        DescriptorList.ToDictionary(descriptor => descriptor.WidgetKind);

    public IWidgetContent CreateExistingContent(WidgetConfig config, FrameworkElement view)
    {
        return new ExistingWidgetContent(config, view);
    }

    public IWidgetContent CreatePlaceholderContent(WidgetConfig config)
    {
        var descriptor = GetDescriptor(config.WidgetKind);
        if (!descriptor.HasPlaceholderContent)
        {
            throw new NotSupportedException($"Widget kind '{config.WidgetKind}' does not have placeholder content.");
        }

        return new PlaceholderWidgetContent(config, descriptor);
    }

    internal IWidgetContent CreateDetachedContent(
        WidgetConfig config,
        SettingsService? settingsService = null)
    {
        throw new NotSupportedException(
            $"Widget kind '{config.WidgetKind}' does not have detached content.");
    }

    internal bool CanCreateDetachedContent(WidgetKind widgetKind) => false;

    public IReadOnlyList<WidgetContentDescriptor> GetDescriptors() => DescriptorList;

    public WidgetContentDescriptor GetDescriptor(WidgetKind widgetKind)
    {
        if (Descriptors.TryGetValue(widgetKind, out var descriptor))
        {
            return descriptor;
        }

        throw new NotSupportedException($"Widget kind '{widgetKind}' does not have a content descriptor.");
    }

    public IReadOnlyList<WidgetContentDescriptor> GetCreateEntryDescriptors() =>
        DescriptorList.Where(descriptor => descriptor.CanShowInCreateEntry).ToArray();

    public bool HasImplementedContent(WidgetKind widgetKind) =>
        Descriptors.TryGetValue(widgetKind, out var descriptor) &&
        descriptor.HasImplementedContent;

    public bool IsPlaceholderOnly(WidgetKind widgetKind) =>
        Descriptors.TryGetValue(widgetKind, out var descriptor) &&
        descriptor.IsPlaceholderOnly;

    public bool CanShowInCreateEntry(WidgetKind widgetKind) =>
        Descriptors.TryGetValue(widgetKind, out var descriptor) &&
        descriptor.CanShowInCreateEntry;

    public bool IsAvailable(WidgetKind widgetKind) =>
        Descriptors.TryGetValue(widgetKind, out var descriptor) &&
        descriptor.IsAvailable;

    public bool IsPlanned(WidgetKind widgetKind) =>
        Descriptors.TryGetValue(widgetKind, out var descriptor) &&
        descriptor.IsPlanned;

    public bool CanCreatePlaceholderContent(WidgetKind widgetKind) =>
        Descriptors.TryGetValue(widgetKind, out var descriptor) &&
        descriptor.HasPlaceholderContent;
}
