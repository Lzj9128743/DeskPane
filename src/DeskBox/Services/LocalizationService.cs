using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskBox.Services;

public sealed class LocalizationService
{
    public const string LanguageSystem = SettingsService.LanguageSystem;
    public const string LanguageChinese = SettingsService.LanguageChinese;
    public const string LanguageEnglish = SettingsService.LanguageEnglish;

    private readonly SettingsService _settingsService;
    private string? _cachedCultureName;

    public event Action? LanguageChanged;

    public LocalizationService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public string LanguageSetting => NormalizeLanguageSetting(_settingsService.Settings.Language);

    public string CurrentCultureName
    {
        get
        {
            if (_cachedCultureName is { } cached)
            {
                return cached;
            }

            string language = LanguageSetting;
            if (language == LanguageSystem)
            {
                language = ResolveSystemLanguage();
            }

            _cachedCultureName = language;
            return language;
        }
    }

    public bool IsEnglish => string.Equals(CurrentCultureName, LanguageEnglish, StringComparison.OrdinalIgnoreCase);
    public bool IsChinese => !IsEnglish;
    public bool IsTraditionalChinese => false;

    public string ApiLanguageCode => IsEnglish ? "en" : "zh";

    public IReadOnlyList<string> AvailableLanguageSettings { get; } =
    [
        LanguageSystem,
        LanguageChinese,
        LanguageEnglish
    ];

    public string GetLanguageDisplayName(string language) =>
        NormalizeLanguageSetting(language) switch
        {
            LanguageChinese => "简体中文",
            LanguageEnglish => "English",
            _ => T("Language.System")
        };

    public void SetLanguage(string language)
    {
        string normalized = NormalizeLanguageSetting(language);
        if (string.Equals(_settingsService.Settings.Language, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _settingsService.Settings.Language = normalized;
        _cachedCultureName = null;
        _settingsService.SaveDebounced(notifySubscribers: false);
        RaiseLanguageChanged();
    }

    public void NotifyExternalLanguageChange()
    {
        _cachedCultureName = null;
        RaiseLanguageChanged();
    }

    private void RaiseLanguageChanged()
    {
        if (LanguageChanged is null)
        {
            return;
        }

        foreach (var handler in LanguageChanged.GetInvocationList())
        {
            try
            {
                ((Action)handler).Invoke();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[LocalizationService] LanguageChanged handler '{handler.Method.DeclaringType?.Name}.{handler.Method.Name}' threw: {ex.Message}");
            }
        }
    }

    public string T(string key)
    {
        var table = IsEnglish ? EnUs : ZhCn;
        if (table.TryGetValue(key, out string? value))
        {
            return value;
        }

        if (EnUs.TryGetValue(key, out value))
        {
            return value;
        }

        return ZhCn.TryGetValue(key, out value) ? value : key;
    }

    public string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(key), args);

    public static string DefaultText(string key) =>
        ZhCn.TryGetValue(key, out string? value) ? value : key;

    public static string DefaultFormat(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, DefaultText(key), args);

    public static string NormalizeLanguageSetting(string? language)
    {
        if (string.Equals(language, LanguageEnglish, StringComparison.OrdinalIgnoreCase))
        {
            return LanguageEnglish;
        }

        if (string.Equals(language, LanguageChinese, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(language, "zh-TW", StringComparison.OrdinalIgnoreCase))
        {
            return LanguageChinese;
        }

        return LanguageSystem;
    }

    private static string ResolveSystemLanguage()
    {
        string name = CultureInfo.CurrentUICulture.Name;
        return name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? LanguageChinese
            : LanguageEnglish;
    }

    private static Dictionary<string, string>? _zhCn;
    private static Dictionary<string, string>? _enUs;
    private static readonly object s_loadLock = new();

    private static Dictionary<string, string> ZhCn
    {
        get
        {
            if (_zhCn is not null) return _zhCn;
            lock (s_loadLock)
            {
                _zhCn ??= LoadStringResource("DeskBox.Strings.zh-CN.json");
            }
            return _zhCn;
        }
    }

    private static Dictionary<string, string> EnUs
    {
        get
        {
            if (_enUs is not null) return _enUs;
            lock (s_loadLock)
            {
                _enUs ??= LoadStringResource("DeskBox.Strings.en-US.json");
            }
            return _enUs;
        }
    }

    private static Dictionary<string, string> LoadStringResource(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            System.Diagnostics.Debug.WriteLine($"[LocalizationService] Resource not found: {resourceName}");
            return [];
        }
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        return JsonSerializer.Deserialize(
            json,
            LocalizationJsonContext.Default.LocalizedStrings) ?? [];
    }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(
    typeof(Dictionary<string, string>),
    TypeInfoPropertyName = "LocalizedStrings")]
internal sealed partial class LocalizationJsonContext : JsonSerializerContext
{
}
