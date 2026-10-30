using System.Globalization;

namespace RPM.Desktop.Services.Shell;

/// <summary>
/// The language the app speaks: one of the five its string table carries, or Windows' own.
///
/// <para><b>What a change reaches.</b> <see cref="Apply"/> points <see cref="Strings.Loc"/> at the chosen
/// language, so every screen built after it is in that language. Screens already on screen keep the old one
/// until they are built again. The setup wizard rebuilds itself when the language is picked. The settings
/// page says that the rest follows on the next start, and offers the restart.</para>
///
/// <para>The UI culture follows the choice as well, so a formatted string that reads it agrees with the
/// text around it. The formatting culture (dates, numbers) is left to Windows' region, which is a
/// different question from the language.</para>
/// </summary>
internal static class AppLanguage
{
    /// <summary>The languages the string table carries, in the order the picker offers them. The same
    /// list as appsettings.json's Cultures and gen_locales.py's LOCALES.</summary>
    public static readonly IReadOnlyList<string> Offered = ["en", "fr", "es", "de", "zh-Hans"];

    /// <summary>Windows' UI culture as the process found it, before any choice replaced it.</summary>
    private static readonly CultureInfo SystemUi = CultureInfo.CurrentUICulture;

    /// <summary>The offered tag that <paramref name="tag"/> names, or null when it names none of them.</summary>
    public static string? Known(string? tag) =>
        Offered.FirstOrDefault(o => string.Equals(o, tag, StringComparison.OrdinalIgnoreCase));

    /// <summary>The offered language Windows itself would get: its UI language when the table has it,
    /// and English otherwise.</summary>
    public static string SystemDefault()
    {
        for (var culture = SystemUi; !string.IsNullOrEmpty(culture.Name); culture = culture.Parent)
        {
            // Every Chinese that is not written in traditional characters reads the simplified table.
            if (culture.TwoLetterISOLanguageName == "zh")
                return culture.Name.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                       || culture.Name is "zh-TW" or "zh-HK" or "zh-MO"
                    ? "en"
                    : "zh-Hans";
            if (Known(culture.Name) is { } known)
                return known;
        }
        return "en";
    }

    /// <summary>The language the app is speaking: the stored choice, or Windows' when there is none.</summary>
    public static string Current(SettingsService settings) => Known(settings.Language) ?? SystemDefault();

    /// <summary>A language's name in that language, with a capital, the way a picker of languages lists
    /// them: "Deutsch", "Français", "中文（简体）". So each reader finds their own whatever the screen is
    /// in.</summary>
    public static string NativeName(string tag)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(tag);
            var name = culture.NativeName;
            return name.Length == 0 ? tag : char.ToUpper(name[0], culture) + name[1..];
        }
        catch (CultureNotFoundException)
        {
            return tag;
        }
    }

    /// <summary>Speaks <paramref name="tag"/> from now on, or Windows' language when it is null or not
    /// one of <see cref="Offered"/>. Called at startup with the stored choice, and when the user picks
    /// one.</summary>
    public static void Apply(string? tag)
    {
        tag = Known(tag);

        try
        {
            var ui = tag is null ? SystemUi : CultureInfo.GetCultureInfo(tag);
            CultureInfo.DefaultThreadCurrentUICulture = ui;
            CultureInfo.CurrentUICulture = ui;
        }
        catch (CultureNotFoundException) { }

#if RPM_WINUI
        // What the framework's own text (and a loader made after this) reads. Empty clears it.
        try { Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = tag ?? ""; }
        catch { }
#else
        try { Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = tag ?? ""; }
        catch { }
#endif

        Strings.Loc.Use(tag);
        RPM.Core.Diagnostics.Scope.Said("boot", $"language {tag ?? "system"}");
    }
}
