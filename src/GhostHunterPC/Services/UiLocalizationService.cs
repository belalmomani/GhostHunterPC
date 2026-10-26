using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace GhostHunterPC.Services;

public sealed class UiLocalizationService
{
    private static readonly IReadOnlyDictionary<string, string> ArabicTexts = LoadTexts("ar");
    private static readonly IReadOnlyDictionary<string, string> EnglishTexts = LoadTexts("en");

    public bool IsArabic { get; private set; } = true;

    public event EventHandler? LanguageChanged;

    public string GetText(string key)
    {
        var resources = IsArabic ? ArabicTexts : EnglishTexts;
        return resources.TryGetValue(key, out var resourceText)
            ? resourceText
            : throw new KeyNotFoundException($"Localization key '{key}' was not found.");
    }

    private static IReadOnlyDictionary<string, string> LoadTexts(string language)
    {
        var assembly = typeof(UiLocalizationService).Assembly;
        var resourceSuffix = $"UiStrings.{language}.json";
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase));
        using var stream = (resourceName is null
            ? null
            : assembly.GetManifestResourceStream(resourceName))
            ?? File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Resources", resourceSuffix));
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidDataException($"Localization resource '{resourceSuffix}' is empty.");
    }

    public void ToggleLanguage()
    {
        SetLanguage(IsArabic ? "en" : "ar");
    }

    public void SetLanguage(string language)
    {
        if (language is not ("ar" or "en"))
        {
            throw new ArgumentOutOfRangeException(nameof(language), "Supported languages are ar and en.");
        }

        var wasArabic = IsArabic;
        IsArabic = language == "ar";
        var culture = CultureInfo.GetCultureInfo(IsArabic ? "ar-SA" : "en-US");
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        if (wasArabic != IsArabic)
        {
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
