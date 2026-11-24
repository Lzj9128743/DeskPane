// Copyright (c) DeskPane. All rights reserved.

using DeskPane.Models;
using DeskPane.Services;

namespace DeskPane.Tests;

public sealed class WidgetSettingsMenuHelperTests
{
    [Fact]
    public void GetSettingsSectionTag_Returns_Expected_Tag_For_File()
    {
        Assert.Equal("FileDisplaySettings", WidgetSettingsMenuHelper.GetSettingsSectionTag(WidgetKind.File));
        Assert.Null(WidgetSettingsMenuHelper.GetSettingsSectionTag(WidgetKind.Unsupported));
    }

    [Fact]
    public void GetLocalizationKey_Returns_Expected_Key()
    {
        Assert.Equal("Widget.Settings.FileWidget", WidgetSettingsMenuHelper.GetLocalizationKey(WidgetKind.File));
        Assert.Equal("Common.Configure", WidgetSettingsMenuHelper.GetLocalizationKey(WidgetKind.Unsupported));
    }
}
