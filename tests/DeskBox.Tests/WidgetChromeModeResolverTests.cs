using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetChromeModeResolverTests
{
    [Fact]
    public void Resolve_UsesInteractiveGlobalDefaultForFile()
    {
        var settingsService = new SettingsService();
        settingsService.Settings.InteractiveWidgetChromeMode = SettingsService.WidgetChromeModeCompact;
        var descriptor = TestServices.CreateWidgetContentFactory().GetDescriptor(WidgetKind.File);
        var config = new WidgetConfig { WidgetKind = WidgetKind.File };

        var mode = new WidgetChromeModeResolver(settingsService).Resolve(config, descriptor);

        Assert.Equal(WidgetChromeMode.Compact, mode);
    }

    [Fact]
    public void Resolve_InstanceOverrideWinsOverGlobalDefault()
    {
        var settingsService = new SettingsService();
        settingsService.Settings.InteractiveWidgetChromeMode = SettingsService.WidgetChromeModeOverlay;
        var descriptor = TestServices.CreateWidgetContentFactory().GetDescriptor(WidgetKind.File);
        var config = new WidgetConfig { WidgetKind = WidgetKind.File };
        WidgetChromeModeNames.SetOverrideMode(config, WidgetChromeMode.Standard);

        var mode = new WidgetChromeModeResolver(settingsService).Resolve(config, descriptor);

        Assert.Equal(WidgetChromeMode.Standard, mode);
    }

    [Fact]
    public void SetOverrideMode_SystemRemovesMetadata()
    {
        var config = new WidgetConfig { WidgetKind = WidgetKind.File };
        WidgetChromeModeNames.SetOverrideMode(config, WidgetChromeMode.Hidden);

        WidgetChromeModeNames.SetOverrideMode(config, WidgetChromeMode.System);

        Assert.False(config.Metadata.ContainsKey(WidgetChromeModeNames.MetadataKey));
    }
}
