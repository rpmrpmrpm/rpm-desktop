using System.Globalization;

namespace RPM.Desktop.Services.Shell;

/// <summary>
/// The listener's country: the one they chose, or Windows' region when they have not chosen. The radio
/// leads with it (<see cref="RPM.Core.Radio.RadioCountries.ForPicker"/>) and names its essential
/// stations after it.
/// </summary>
internal static class AppCountry
{
    /// <summary>A country the picker offers: its ISO code and its name in the app's language.</summary>
    public sealed record Entry(string Code, string Name);

    /// <summary>The code the listener is in, or null where neither the choice nor Windows names a
    /// country.</summary>
    public static string? Code(SettingsService settings) => Valid(settings.Country) ?? SystemCode();

    /// <summary>Windows' region, as a code, or null where it is not a country (the invariant region, or
    /// a numeric area such as "Latin America").</summary>
    public static string? SystemCode()
    {
        try
        {
            return Valid(RegionInfo.CurrentRegion.TwoLetterISORegionName);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// A country's name, as near the app's language as the system can give it, or the code when the system
    /// has no name for it.
    /// </summary>
    /// <remarks>
    /// The system names countries in Windows' display language only (<see cref="RegionInfo.DisplayName"/>),
    /// whatever the app speaks. So that name is used only when the app speaks Windows' language. Otherwise
    /// the English name is used, shortened the way the radio's rows shorten it: an English screen on a Dutch
    /// Windows reads "Netherlands", not "Nederland".
    /// </remarks>
    public static string Name(string code)
    {
        try
        {
            var region = new RegionInfo(code);
            var speaking = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            return speaking == CultureInfo.InstalledUICulture.TwoLetterISOLanguageName && speaking != "en"
                ? region.DisplayName
                : RPM.Core.Radio.RadioCountries.Short(region.EnglishName, region.TwoLetterISORegionName);
        }
        catch (ArgumentException)
        {
            return code;
        }
    }

    /// <summary>Every country the system can name, A to Z in the app's language.</summary>
    public static IReadOnlyList<Entry> All()
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                if (Valid(new RegionInfo(culture.Name).TwoLetterISORegionName) is { } code)
                    codes.Add(code);
            }
            catch (ArgumentException) { }
        }

        return [.. codes
            .Select(code => new Entry(code, Name(code)))
            .OrderBy(e => e.Name, StringComparer.CurrentCulture)];
    }

    /// <summary>The code in capitals when it is two letters that name a country, and null otherwise.</summary>
    private static string? Valid(string? code)
    {
        if (code is not { Length: 2 } || !char.IsAsciiLetter(code[0]) || !char.IsAsciiLetter(code[1]))
            return null;
        code = code.ToUpperInvariant();
        return code == "IV" ? null : code;
    }
}
