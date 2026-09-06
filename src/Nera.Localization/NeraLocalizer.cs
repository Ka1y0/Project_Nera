using System.Globalization;
using System.Resources;

namespace ChipsStudio.Nera.Localization;

/// <summary>Stable language identity and sort key; only Display is user-visible.</summary>
public sealed record NeraLanguageOption(
    string Preference, string DisplayResourceKey, string CanonicalEnglishName, bool IsSystem = false)
{
    public string Display => NeraLocalizer.Get(DisplayResourceKey);
}

/// <summary>One local, embedded resource source shared by App, Control and AgentBridge.
/// It never localizes machine keys, codes, Runtime parameters or protocol fields.</summary>
public static class NeraLocalizer
{
    private static readonly ResourceManager Resources = new(
        "ChipsStudio.Nera.Localization.Resources.Strings", typeof(NeraLocalizer).Assembly);
    private static readonly object Gate = new();
    private static string preference_ = "system";
    private static string language_ = MapSystemLanguage(CultureInfo.CurrentUICulture.Name);
    public static event EventHandler? LanguageChanged;
    public static string Preference { get { lock (Gate) return preference_; } }
    public static string Language { get { lock (Gate) return language_; } }
    public static IReadOnlyList<NeraLanguageOption> LanguageOptions { get; } =
        Array.AsReadOnly(new[]
        {
            new NeraLanguageOption("system", "Language.FollowSystem", "System", IsSystem: true),
            new NeraLanguageOption("en-US", "Language.EnUs", "English"),
            new NeraLanguageOption("zh-TW", "Language.ZhTw", "Traditional Chinese"),
            new NeraLanguageOption("zh-CN", "Language.ZhCn", "Simplified Chinese")
        }.OrderByDescending(option => option.IsSystem)
            .ThenBy(option => option.CanonicalEnglishName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(option => option.Preference, StringComparer.Ordinal)
            .ToArray());
    public static IReadOnlyList<string> SupportedPreferences { get; } =
        Array.AsReadOnly(LanguageOptions.Select(option => option.Preference).ToArray());
    public static IReadOnlyList<string> SupportedLanguages { get; } =
        Array.AsReadOnly(LanguageOptions.Where(option => !option.IsSystem)
            .Select(option => option.Preference).ToArray());

    public static bool IsValidPreference(string? value) =>
        SupportedPreferences.Contains(value, StringComparer.Ordinal);

    public static string MapSystemLanguage(string? value)
    {
        string tag = (value ?? string.Empty).Replace('_', '-');
        if (tag.Equals("zh-Hant", StringComparison.OrdinalIgnoreCase) ||
            tag.StartsWith("zh-Hant-", StringComparison.OrdinalIgnoreCase) ||
            new[] { "zh-TW", "zh-HK", "zh-MO" }.Any(p =>
                tag.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                tag.StartsWith(p + "-", StringComparison.OrdinalIgnoreCase))) return "zh-TW";
        if (tag.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase) ||
            tag.StartsWith("zh-Hans-", StringComparison.OrdinalIgnoreCase) ||
            new[] { "zh-CN", "zh-SG" }.Any(p =>
                tag.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                tag.StartsWith(p + "-", StringComparison.OrdinalIgnoreCase))) return "zh-CN";
        return "en-US";
    }

    public static void SetLanguage(string preference, string? systemLanguage = null)
    {
        if (!IsValidPreference(preference)) throw new ArgumentException(
            "Unsupported Nera language preference.", nameof(preference));
        string language = preference == "system"
            ? MapSystemLanguage(systemLanguage ?? CultureInfo.CurrentUICulture.Name) : preference;
        bool changed;
        lock (Gate)
        {
            changed = preference_ != preference || language_ != language;
            preference_ = preference;
            language_ = language;
        }
        if (changed) LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string Get(string key, params object?[] args) =>
        GetForLanguage(Language, key, args);

    public static string GetForLanguage(string language, string key, params object?[] args)
    {
        CultureInfo culture = CultureInfo.GetCultureInfo(MapSystemLanguage(language));
        string value = Resources.GetString(key, culture) ?? key;
        return args.Length == 0 ? value : string.Format(culture, value, args);
    }

    public static IReadOnlyDictionary<string, string> GetResourceSet(string language)
    {
        ResourceSet? set = Resources.GetResourceSet(
            CultureInfo.GetCultureInfo(MapSystemLanguage(language)), true, true);
        return set?.Cast<System.Collections.DictionaryEntry>().ToDictionary(
            entry => (string)entry.Key, entry => (string)entry.Value!, StringComparer.Ordinal)
            ?? new Dictionary<string, string>();
    }

    /// <summary>Translates a known legacy presentation message without altering its machine code.
    /// Unknown backend diagnostics never leak as untranslated ordinary UI text.</summary>
    public static string LocalizeMessage(string? message, string? code = null, bool success = false)
    {
        if (!string.IsNullOrWhiteSpace(code))
        {
            string errorKey = "Error." + code;
            if (Resources.GetString(errorKey, CultureInfo.GetCultureInfo(Language)) is string value)
                return value;
        }
        if (!string.IsNullOrWhiteSpace(message))
        {
            foreach (string language in SupportedLanguages)
                foreach ((string key, string value) in GetResourceSet(language))
                    if (string.Equals(value, message, StringComparison.Ordinal)) return Get(key);
        }
        return Get(success ? "Common.Applied" : "Error.OPERATION_FAILED");
    }
}
