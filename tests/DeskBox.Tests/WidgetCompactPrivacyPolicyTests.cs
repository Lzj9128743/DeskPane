using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetCompactPrivacyPolicyTests
{
    [Fact]
    public void ResolveContentMode_HidesSmartTextForFileWidgets()
    {
        Assert.Equal(
            SettingsService.WidgetCompactContentModeSummary,
            WidgetCompactPrivacyPolicy.ResolveContentMode(
                SettingsService.WidgetCompactContentModeSmart,
                enabled: true,
                widgetKind: WidgetKind.File));
    }

    [Theory]
    [InlineData(WidgetKind.File, true)]
    [InlineData(WidgetKind.Unsupported, false)]
    public void HidesSensitiveContent_CoversExpectedWidgets(
        WidgetKind widgetKind,
        bool expected)
    {
        Assert.Equal(
            expected,
            WidgetCompactPrivacyPolicy.HidesSensitiveContent(
                enabled: true,
                widgetKind: widgetKind));
    }
}
