namespace DeskBox.Services;

using DeskBox.Models;

public enum WidgetTitleIconMode
{
    FilledMono,
    LineMono,
    Color,
    Hidden,
    TextLabel
}

public static class WidgetTitleIconModeNames
{
    public const string FilledMono = Contracts.WidgetTitleIconKinds.FilledMono;
    public const string LineMono = Contracts.WidgetTitleIconKinds.LineMono;
    public const string Color = Contracts.WidgetTitleIconKinds.Color;
    public const string Hidden = Contracts.WidgetTitleIconKinds.Hidden;
    public const string TextLabel = Contracts.WidgetTitleIconKinds.TextLabel;

    public static WidgetTitleIconMode NormalizeMode(string? value)
    {
        return Enum.TryParse(value, ignoreCase: true, out WidgetTitleIconMode mode)
            ? mode
            : WidgetTitleIconMode.Color;
    }

    public static string NormalizeSettingValue(string? value)
    {
        return ToSettingValue(NormalizeMode(value));
    }

    public static string ToSettingValue(WidgetTitleIconMode mode)
    {
        return mode switch
        {
            WidgetTitleIconMode.FilledMono => FilledMono,
            WidgetTitleIconMode.LineMono => LineMono,
            WidgetTitleIconMode.Color => Color,
            WidgetTitleIconMode.Hidden => Hidden,
            WidgetTitleIconMode.TextLabel => TextLabel,
            _ => Color
        };
    }
}

public enum WidgetTitleIconKind
{
    Default,
    ManagedStorage,
    MappedFolder
}

public static class WidgetTitleIconKindNames
{
    public const string Default = nameof(WidgetTitleIconKind.Default);
    public const string ManagedStorage = nameof(WidgetTitleIconKind.ManagedStorage);
    public const string MappedFolder = nameof(WidgetTitleIconKind.MappedFolder);

    public static WidgetTitleIconKind NormalizeKind(string? value)
    {
        return Enum.TryParse(value, ignoreCase: true, out WidgetTitleIconKind kind)
            ? kind
            : WidgetTitleIconKind.Default;
    }

    public static string ToSettingValue(WidgetTitleIconKind kind)
    {
        return kind switch
        {
            WidgetTitleIconKind.ManagedStorage => ManagedStorage,
            WidgetTitleIconKind.MappedFolder => MappedFolder,
            _ => Default
        };
    }

    public static string FromFileWidget(bool followsDefaultStoragePath)
    {
        return followsDefaultStoragePath ? ManagedStorage : MappedFolder;
    }

    public static string FromWidgetKind(WidgetKind kind)
    {
        return kind switch
        {
            WidgetKind.File => ManagedStorage,
            _ => Default
        };
    }

    public static string FromLegacyGlyph(string? glyph)
    {
        return glyph switch
        {
            "\uE8B7" => ManagedStorage,
            "\uE71B" => MappedFolder,
            _ => Default
        };
    }

    public static string GetLocalizationKey(WidgetTitleIconKind kind)
    {
        return kind switch
        {
            WidgetTitleIconKind.ManagedStorage => "WidgetTitleIcon.Label.ManagedStorage",
            WidgetTitleIconKind.MappedFolder => "WidgetTitleIcon.Label.MappedFolder",
            _ => "WidgetTitleIcon.Label.Default"
        };
    }

    public static string GetColorAssetName(WidgetTitleIconKind kind)
    {
        return kind switch
        {
            WidgetTitleIconKind.ManagedStorage => "managed-storage",
            WidgetTitleIconKind.MappedFolder => "mapped-folder",
            _ => "default"
        };
    }
}
