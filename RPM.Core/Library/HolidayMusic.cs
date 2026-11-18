using System.Text.RegularExpressions;

namespace RPM.Core.Library;

/// <summary>
/// Christmas and New Year music: which records are holiday music, and when the holiday is.
///
/// <para><b>Found by its words, not by its sound.</b> Holiday music is a subject, and the sound model did
/// not find it: asked for "christmas music", seven or eight of its ten best guesses on a real library were
/// not holiday songs. A title, an album or a genre that names the holiday is a fact somebody wrote down.
/// The words are those of the listening test (<c>rpm-notes/valence-moods/make_examples.py</c>), in the
/// languages that collection holds.</para>
///
/// <para>Pure, like <see cref="MoodBands"/>: the caller reads the tags and the clock.</para>
/// </summary>
public static partial class HolidayMusic
{
    /// <summary>
    /// Whether a track is holiday music.
    ///
    /// <para><b>The album or the genre makes the whole record holiday music.</b> "Merry Christmas" is a
    /// Christmas album, every song on it included, and so is a record tagged with the genre
    /// "Christmas".</para>
    ///
    /// <para><b>"Holiday" on its own does not count in a title or an album.</b> It is usually a summer
    /// holiday: "Dreadlock Holiday", Madonna's "Holiday", Cliff Richard's "Summer Holiday". It counts
    /// beside another holiday word ("Holiday Christmas Songs"), and it counts as a genre, where it is
    /// what the stores call Christmas music.</para>
    /// </summary>
    public static bool IsHoliday(string? title, string? album, string? genre) =>
        Names(album) || Names(genre, holidayAlone: true) || Names(title);

    /// <summary>
    /// Whether <paramref name="local"/> falls in the holiday season: from 22 November to 6 January,
    /// both included.
    ///
    /// <para>The shops' Christmas starts in late November, about five weeks before it. The season ends
    /// on the sixth of January, Twelfth Night, when the decorations come down. Outside it, a list of
    /// Christmas songs is not offered, and the holiday songs stay out of every other list too.</para>
    /// </summary>
    /// <param name="local">The listener's own date. The time of day is not read.</param>
    public static bool InSeason(DateTime local) =>
        (local.Month == 11 && local.Day >= SeasonStartsOnNovember)
        || local.Month == 12
        || (local.Month == 1 && local.Day <= SeasonEndsOnJanuary);

    /// <summary>The first day of the season, in November.</summary>
    public const int SeasonStartsOnNovember = 22;

    /// <summary>The last day of the season, in January.</summary>
    public const int SeasonEndsOnJanuary = 6;

    private static bool Names(string? text, bool holidayAlone = false)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (Match match in Words().Matches(text))
            if (holidayAlone || !match.Value.StartsWith("holiday", StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>
    /// Text that every holiday word contains, for a database to narrow by before <see cref="IsHoliday"/>
    /// reads the words properly. A superset: a track whose tags hold none of these cannot be holiday
    /// music, and one that holds one may not be ("Santa Barbara").
    ///
    /// <para>"noël" is listed in both cases because SQLite's LIKE ignores case for the English letters
    /// only.</para>
    /// </summary>
    public static IReadOnlyList<string> Hints { get; } =
    [
        "christmas", "xmas", "x-mas", "kerst", "noel", "noël", "NOËL", "navidad", "weihnacht", "holiday",
        "jingle", "santa", "sleigh", "mistletoe", "winter wonderland", "silent night", "stille nacht",
        "new year", "auld lang syne",
    ];

    // "Santa" is the saint before a place's name far more often than it is Father Christmas: Santa Barbara,
    // Santa Monica, Santa Fe. Those are left out by name, and so is a remixer called Santa ("Rush (Tom
    // Santa Remix)"). "Santa Claus", "Santa Baby" and "Santa Tell Me" are not.
    [GeneratedRegex(
        @"\b(christmas|xmas|x-mas|kerst|kerstmis|noel|noël|navidad|weihnacht\w*|holiday|holidays|jingle|" +
        @"santa(?!\s+(?:barbara|monica|fe|cruz|clara|maria|ana|rosa|cecilia|esmeralda|teresa|catalina|marta|lucia" +
        @"|remix|mix|edit|dub|version)\b)|" +
        @"sleigh|mistletoe|winter wonderland|silent night|stille nacht|new year'?s?|auld lang syne)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Words();
}
