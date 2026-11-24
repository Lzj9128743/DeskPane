using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetRegistryTests
{
    [Fact]
    public void Default_KnowsFileWidgetAndRejectsUnsupported()
    {
        var registry = WidgetRegistry.Default;

        Assert.True(registry.IsKnown(WidgetKind.File));
        Assert.True(registry.CanCreateWindow(WidgetKind.File));
        Assert.False(registry.IsKnown(WidgetKind.Unsupported));
        Assert.False(registry.CanCreateWindow(WidgetKind.Unsupported));
    }

    [Fact]
    public void IsAvailableForSession_RejectsUnsupportedKind()
    {
        var registry = WidgetRegistry.Default;
        var unsupportedWidget = new WidgetConfig
        {
            WidgetKind = WidgetKind.Unsupported
        };

        Assert.False(registry.IsAvailableForSession(unsupportedWidget, new AppSettings()));
    }
}
