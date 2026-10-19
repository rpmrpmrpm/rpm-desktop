namespace RPM.Core.Radio;

/// <summary>
/// Whether a station carries advertising, and whether it is running an advert right now. These are two
/// different questions with two different answers, and only the second is ever certain.
///
/// <para><b>What the directory knows is very little.</b> Radio Browser has many fields about a station, and
/// none of them is about advertising. The only signal is the tag list, which users enter. Across the
/// most-played stations, only about one in twenty-five has a tag that says anything about advertising. So
/// <see cref="CarriesAds"/> is a claim about the stations that DO say so. Silence means silence: a station
/// with no tag is not marked either way.</para>
///
/// <para><b>Public is not the same as ad-free.</b> That is why both are marked. NPO carries STER, NPR
/// carries sponsorship, and 1LIVE carries spots. The tag says who owns a broadcaster, not whether the
/// listener hears advertising. Only the tags that explicitly say otherwise, such as "non-commercial" or "no
/// ads", take a station out.</para>
/// </summary>
public static class RadioAds
{
    /// <summary>
    /// Whether this station's own tags say it carries advertising.
    ///
    /// <para>The denial is read first and wins. A station tagged both "public radio" and "non-commercial"
    /// has said something specific, and the specific answer is used.</para>
    /// </summary>
    public static bool CarriesAds(IReadOnlyList<string>? tags)
        => CarriesAds(tags, name: null, homepage: null);

    /// <summary>
    /// Whether this station carries advertising: what its tags say, and failing that, who broadcasts it.
    /// </summary>
    /// <remarks>
    /// The tags are read first, because they are the station's own word about itself. A station that says
    /// "non-commercial" is believed, even where its broadcaster is on the list. The broadcaster list
    /// answers for everything else, which on most stations is everything. See <see
    /// cref="RadioBroadcasters"/> for why a list is needed at all.
    /// </remarks>
    public static bool CarriesAds(IReadOnlyList<string>? tags, string? name, string? homepage)
    {
        var carries = false;

        foreach (var raw in tags ?? [])
        {
            var tag = Fold(raw);
            if (tag.Length == 0)
                continue;

            if (AdFree.Any(mark => tag.Contains(mark, StringComparison.Ordinal)))
                return false;

            carries |= Advertised.Any(mark => tag.Contains(mark, StringComparison.Ordinal));
        }

        return carries || RadioBroadcasters.CarriesAds(name, homepage);
    }

    /// <summary>
    /// Whether what a station is announcing right now is an advertisement rather than a record.
    ///
    /// <para>This is the one that can be certain, because the station said so. When a break starts,
    /// stations put a marker in the same ICY field that a song's title goes in. The marker is a word, not a
    /// name: "Advertisement", "Reclame", "Werbung", "Publicité".</para>
    ///
    /// <para><b>The whole line has to be the marker, not merely contain it.</b> "Commercial" is a word that
    /// turns up in song titles and band names. A listener who is told that Radio M2O is in an ad break,
    /// because a song has "commercial" in its title, is being misled about something they can hear for
    /// themselves.</para>
    /// </summary>
    public static bool IsAdBreak(string? announced)
    {
        var line = Fold(announced ?? "");
        return line.Length > 0 && Breaks.Contains(line);
    }

    /// <summary>
    /// Whether what the station is announcing is a break ENDING. This is not something to print at all.
    ///
    /// <para>The spots are over, but no record has been named yet. So the honest readout is the one the
    /// dial shows before a station's first announcement: the station, and nothing about a song. Printing
    /// "Ad break" here would say the opposite of what the station just said. The marker is read from the
    /// same field as the other markers (see <see cref="IcyFields"/>).</para>
    ///
    /// <para>Only iHeart's marker is known to be written this way, so only iHeart's marker is read. A
    /// station that ends its breaks silently, which is nearly all of them, simply announces the next
    /// record.</para>
    /// </summary>
    public static bool IsBreakOver(string? announced)
    {
        var line = Fold(announced ?? "");
        return line.Length > 0 && Overs.Contains(line);
    }

    /// <summary>A tag or an announcement with everything that varies taken out: case, the punctuation
    /// stations wrap a marker in ("- Werbung -", "[reclame]"), and the accents a tagger may or may not have
    /// typed. Left as one string with its inner spaces, so "public radio" is still two words.</summary>
    private static string Fold(string text)
    {
        var folded = new System.Text.StringBuilder(text.Length);

        foreach (var c in text)
        {
            var letter = Deaccent(char.ToLowerInvariant(c));
            if (char.IsLetterOrDigit(letter))
                folded.Append(letter);
            else if (folded.Length > 0 && folded[^1] != ' ')
                folded.Append(' ');   // any run of punctuation or space is one space
        }

        return folded.ToString().TrimEnd();
    }

    /// <summary>The accented letters that these words are written with. This is a table, not a
    /// normalisation pass. The words are known, and only a few accented vowels occur in them.</summary>
    private static char Deaccent(char c) => c switch
    {
        'é' or 'è' or 'ê' or 'ë' => 'e',
        'à' or 'â' or 'á' or 'ä' => 'a',
        'í' or 'ì' or 'î' or 'ï' => 'i',
        'ó' or 'ò' or 'ô' or 'ö' => 'o',
        'ú' or 'ù' or 'û' or 'ü' => 'u',
        _ => c,
    };

    /// <summary>
    /// Tags that say a station carries advertising.
    ///
    /// <para>These are tags that stations actually use in the directory, not guesses. "government" and
    /// "public service" are here for the same reason as "public radio": they name a broadcaster that almost
    /// always runs spots.</para>
    /// </summary>
    private static readonly string[] Advertised =
        ["commercial", "public radio", "public service", "public broadcast", "offentlich rechtlich",
         "government", "state radio"];

    /// <summary>The tags that say a station does not carry advertising. They are checked first, so that
    /// "non commercial", which contains "commercial", is never read as an advertising station.</summary>
    private static readonly string[] AdFree =
        ["non commercial", "noncommercial", "no ads", "no advert", "ad free", "advert free",
         "werbefrei", "sans pub"];

    /// <summary>
    /// What a station puts in the title field while a break runs, in the languages the app is read in and a
    /// few more that European stations use.
    /// </summary>
    /// <remarks>
    /// Matched whole. A station that announces the sponsor by name instead cannot be told apart from a
    /// record by anything here, and it is not guessed at.
    /// </remarks>
    private static readonly HashSet<string> Breaks = new(StringComparer.Ordinal)
    {
        "ad", "ads", "advert", "adverts", "advertisement", "advertisements", "advertising",
        "ad break", "adbreak", "commercial", "commercials", "commercial break",
        "reclame", "reklame", "reclames", "werbung", "werbepause",
        "publicite", "publicidad", "publicidade", "pubblicita", "anuncios", "spot publicitario",

        // US radio calls a break a spot block. The iHeart encoders write it in the field, with the ids
        // around it. IcyFields has removed the ids by the time this runs, so only the marker is left.
        "spot block", "spot block start",
    };

    /// <summary>The marker that says a break has just finished. It has its own set, because it means the
    /// opposite: see <see cref="IsBreakOver"/>.</summary>
    private static readonly HashSet<string> Overs = new(StringComparer.Ordinal)
    {
        "spot block end",
    };
}
