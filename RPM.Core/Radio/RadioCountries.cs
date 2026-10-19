namespace RPM.Core.Radio;

/// <summary>
/// The name a country is shown under in a list, which is not always the name the register uses.
///
/// <para>Radio Browser files countries under their formal ISO names, such as "The United Kingdom Of Great
/// Britain And Northern Ireland", "The Republic Of Korea" and "Bolivarian Republic Of Venezuela". These
/// names are correct, but they are too long for a narrow column. The longest one loses much of its text to
/// an ellipsis. Many countries sort under T because their name starts with "The".</para>
///
/// <para><b>Two rules. The first is a list of exceptions.</b> A country whose common name is a different
/// word from its formal name cannot be shortened by tidying. Nothing turns "The Republic Of Korea" into
/// "South Korea", or "The Russian Federation" into "Russia". So those countries are named in a list here,
/// by ISO code, because the string is the part that changes. Everything else is tidied. The leading "The"
/// is removed, and the small words that the directory writes in title case are put back in lower case.
/// "Trinidad And Tobago" is not a name that anybody writes.</para>
///
/// <para><b>The code identifies the country, never the name.</b> The name is what is shown. The code is
/// what the directory is queried with. So shortening a name is safe: no search, no query and no saved
/// station passes through here.</para>
/// </summary>
public static class RadioCountries
{
    /// <summary>How many countries the country picker offers.</summary>
    public const int Offered = 80;

    /// <summary>
    /// The countries the country picker offers: the <see cref="Offered"/> with the most stations, under
    /// their short names, A to Z.
    ///
    /// <para>A phone head has a port of this method, with the same tests. Change both, or the heads will
    /// offer different countries.</para>
    ///
    /// <para>Countries are chosen by station count, because a list of two hundred entries, half of which
    /// hold three stations each, is not worth scrolling. The list is shown A to Z, because the count is not
    /// visible to the reader. It is sorted with the current culture's comparison, so "Türkiye" is filed
    /// under T.</para>
    ///
    /// <para>The names are shortened before the sort, so a country is filed under the letter that its shown
    /// name starts with: "The Netherlands" is filed under N, not T.</para>
    ///
    /// <para>There is one entry per code. The directory lists Brazil and Italy a second time, under the
    /// lowercase codes "br" and "it", with one station each. The client uppercases every code. So the
    /// entries are merged here, and the cut does not rely on the small duplicates falling outside
    /// it.</para>
    ///
    /// <para>Ties on station count are decided by code, so the cut does not depend on the order in which
    /// the directory answered.</para>
    /// </summary>
    public static IReadOnlyList<RadioFacet> ForPicker(IReadOnlyList<RadioFacet> countries)
    {
        ArgumentNullException.ThrowIfNull(countries);

        return [.. countries
            .GroupBy(c => c.Code.Trim().ToUpperInvariant(), StringComparer.Ordinal)
            .Select(same => new RadioFacet(
                same.OrderByDescending(c => c.StationCount).First().Name,
                same.Key,
                same.Sum(c => c.StationCount)))
            .OrderByDescending(c => c.StationCount)
            .ThenBy(c => c.Code, StringComparer.Ordinal)
            .Take(Offered)
            .Select(c => c with { Name = Short(c.Name, c.Code) })
            .OrderBy(c => c.Name, StringComparer.CurrentCulture)
            .ThenBy(c => c.Code, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The name to show for this country: its common name where that differs from the register's, and
    /// otherwise the register's own name, tidied.
    /// </summary>
    /// <param name="name">What the directory calls it.</param>
    /// <param name="code">Its ISO 3166-1 alpha-2 code, which the exceptions are keyed by. An entry with no
    /// code (the directory has a few) is tidied instead. Tidying needs only the string.</param>
    public static string Short(string? name, string? code)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";

        if (!string.IsNullOrWhiteSpace(code)
            && Common.TryGetValue(code.Trim().ToUpperInvariant(), out var known))
            return known;

        return Tidy(name.Trim());
    }

    /// <summary>
    /// The countries whose common name is not a shortening of their formal name.
    ///
    /// <para>These are keyed by code and written out one per line, because this is a list of facts about
    /// the world, not a rule with exceptions. No pattern turns "The Democratic Peoples Republic Of Korea"
    /// into "North Korea". A rule that tried would also rename things it should not.</para>
    /// </summary>
    private static readonly Dictionary<string, string> Common = new(StringComparer.Ordinal)
    {
        ["GB"] = "United Kingdom",
        ["US"] = "United States",
        ["RU"] = "Russia",
        ["KR"] = "South Korea",
        ["KP"] = "North Korea",
        ["TW"] = "Taiwan",
        ["VE"] = "Venezuela",
        ["IR"] = "Iran",
        ["SY"] = "Syria",
        ["TZ"] = "Tanzania",
        ["MD"] = "Moldova",
        ["MK"] = "North Macedonia",
        ["LA"] = "Laos",
        ["CD"] = "DR Congo",
        ["CG"] = "Congo",
        ["PS"] = "Palestine",
        ["VA"] = "Vatican City",
        ["FM"] = "Micronesia",
        ["BN"] = "Brunei",
        ["CV"] = "Cape Verde",
        ["SH"] = "Saint Helena",
        ["MF"] = "Saint Martin",
        ["SX"] = "Sint Maarten",
        ["FK"] = "Falkland Islands",
        ["CC"] = "Cocos Islands",
        ["UM"] = "U.S. Outlying Islands",
        ["VI"] = "U.S. Virgin Islands",
        ["VG"] = "British Virgin Islands",
        ["IO"] = "British Indian Ocean Territory",
        ["SJ"] = "Svalbard",
    };

    /// <summary>
    /// The register's own name, made readable: no leading "The", and the joining words in lower case.
    ///
    /// <para>"The Netherlands" becomes Netherlands, "Trinidad And Tobago" becomes Trinidad and Tobago, and
    /// "Isle Of Man" becomes Isle of Man. Only the article is removed. A name that this does not recognise
    /// is returned as it arrived. That is the right result for the many countries that are already called
    /// by their everyday names.</para>
    /// </summary>
    private static string Tidy(string name)
    {
        if (name.StartsWith("The ", StringComparison.OrdinalIgnoreCase))
            name = name[4..].TrimStart();

        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < words.Length; i++)
            if (Joining.Contains(words[i]))
                words[i] = words[i].ToLowerInvariant();

        return string.Join(' ', words);
    }

    /// <summary>The words that are written in lower case when they come after the first word. The first
    /// word is never changed. "The Gambia" loses its article earlier, and must not start with a lower
    /// case word.</summary>
    private static readonly HashSet<string> Joining =
        new(StringComparer.OrdinalIgnoreCase) { "And", "Of", "The", "Da", "De" };
}
