// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Helpers;

/// <summary>
/// Builds a submenu for switching widget background style between pure
/// Transparent (Solid 0%, no blur) and Acrylic (20% - 100% opacity).
/// </summary>
internal static class WidgetOpacityMenuBuilder
{
    private static readonly double[] AcrylicPresets = [0.20, 0.40, 0.60, 0.80, 1.0];

    public static MenuFlyoutSubItem Create(
        LocalizationService localization,
        SettingsService settingsService,
        double? fontSize = null,
        Thickness? padding = null,
        double? minWidth = null)
    {
        var subMenu = new MenuFlyoutSubItem
        {
            Text = GetMenuTitle(localization),
            Icon = new FontIcon { Glyph = "\uE706" }
        };

        if (fontSize.HasValue)
        {
            subMenu.FontSize = fontSize.Value;
        }

        if (padding.HasValue)
        {
            subMenu.Padding = padding.Value;
        }

        if (minWidth.HasValue)
        {
            subMenu.MinWidth = minWidth.Value;
        }

        PopulateItems(subMenu, localization, settingsService, fontSize, padding);
        return subMenu;
    }

    public static void Refresh(
        MenuFlyoutSubItem subMenu,
        LocalizationService localization,
        SettingsService settingsService,
        double? fontSize = null,
        Thickness? padding = null)
    {
        subMenu.Text = GetMenuTitle(localization);
        PopulateItems(subMenu, localization, settingsService, fontSize, padding);
    }

    private static void PopulateItems(
        MenuFlyoutSubItem subMenu,
        LocalizationService localization,
        SettingsService settingsService,
        double? fontSize,
        Thickness? padding)
    {
        subMenu.Items.Clear();
        bool isChinese = localization.IsChinese;
        bool isSolid = settingsService.Settings.WidgetMaterialType == SettingsService.WidgetMaterialTypeSolid;
        double currentOpacity = Math.Clamp(
            settingsService.Settings.WidgetOpacity,
            SettingsService.MinWidgetOpacity,
            SettingsService.MaxWidgetOpacity);

        // Option 1: Pure transparent (Solid 0%, no Acrylic blur)
        bool isTransparentSelected = isSolid && currentOpacity < 0.05;
        var transparentItem = CreatePresetItem(
            isChinese ? "全透明（无模糊）" : "Transparent (No Blur)",
            isTransparentSelected,
            fontSize,
            padding);
        transparentItem.Click += (_, _) => ApplyBackground(
            settingsService,
            SettingsService.WidgetMaterialTypeSolid,
            0.0);
        subMenu.Items.Add(transparentItem);

        subMenu.Items.Add(new MenuFlyoutSeparator());

        // Option 2: Acrylic frost presets (20% - 100%)
        foreach (double preset in AcrylicPresets)
        {
            bool isSelected = !isSolid && Math.Abs(currentOpacity - preset) < 0.05;
            var item = CreatePresetItem(
                FormatAcrylicPresetLabel(preset, isChinese),
                isSelected,
                fontSize,
                padding);
            double targetOpacity = preset;
            item.Click += (_, _) => ApplyBackground(
                settingsService,
                SettingsService.WidgetMaterialTypeAcrylic,
                targetOpacity);
            subMenu.Items.Add(item);
        }
    }

    private static MenuFlyoutItem CreatePresetItem(
        string text,
        bool isSelected,
        double? fontSize,
        Thickness? padding)
    {
        var item = new MenuFlyoutItem
        {
            Text = text,
            Icon = new FontIcon { Glyph = isSelected ? "\uE73E" : "\uE915" }
        };

        if (fontSize.HasValue)
        {
            item.FontSize = fontSize.Value;
        }

        if (padding.HasValue)
        {
            item.Padding = padding.Value;
        }

        return item;
    }

    public static void ApplyBackground(
        SettingsService settingsService,
        string materialType,
        double opacity)
    {
        double clamped = Math.Clamp(
            opacity,
            SettingsService.MinWidgetOpacity,
            SettingsService.MaxWidgetOpacity);

        settingsService.Settings.WidgetMaterialType =
            materialType == SettingsService.WidgetMaterialTypeSolid
                ? SettingsService.WidgetMaterialTypeSolid
                : SettingsService.WidgetMaterialTypeAcrylic;
        settingsService.Settings.WidgetOpacity = clamped;
        settingsService.NotifyAppearancePreviewNow();
        settingsService.SaveDebounced(notifySubscribers: false);
    }

    private static string GetMenuTitle(LocalizationService localization) =>
        localization.IsChinese ? "背景质感与透明度" : "Background & Opacity";

    private static string FormatAcrylicPresetLabel(double opacity, bool isChinese)
    {
        int percent = (int)Math.Round(opacity * 100);
        return percent switch
        {
            80 => isChinese ? "亚克力 80%（默认）" : "Acrylic 80% (Default)",
            _ => isChinese ? $"亚克力 {percent}%" : $"Acrylic {percent}%"
        };
    }
}
