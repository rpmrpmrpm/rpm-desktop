using System.Text.RegularExpressions;

namespace RPM.Core.Library;

/// <summary>
/// Christmas and New Year music: which records are holiday music, which of them are about the new year,
/// and when each holiday is.
///
/// <para><b>Found by its words, not by its sound.</b> Holiday music is a subject, and the sound model did
/// not find it: asked for "christmas music", seven or eight of its ten best guesses on a real library were
/// not holiday songs. A title, an album or a genre that names the holiday is a fact somebody wrote down.
/// The words are those of the listening test (<c>rpm-notes/valence-moods/make_examples.py</c>), in the
/// languages that collection holds, and the New Year's words of the same languages.</para>
///
/// <para><b>Two uses.</b> Outside the holiday season (<see cref="InSeason"/>) every holiday song stays out
/// of every list, so nobody gets Silent Night in a summer Chill list. The New Year's Eve list
/// (<see cref="SmartPlaylistKind.NewYearsEve"/>) is built from the New Year's songs
/// (<see cref="IsNewYear"/>) and a party, and holds no Christmas songs.</para>
///
/// <para>Pure, like <see cref="MoodBands"/>: the caller reads the tags and the clock.</para>
/// </summary>
public static partial class HolidayMusic
{
    /// <summary>
    /// Whether a track is holiday music: Christmas or New Year.
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
        Names(Words(), album) || Names(Words(), genre, holidayAlone: true) || Names(Words(), title);

    /// <summary>
    /// Whether a track is about the new year: "Happy New Year", "Auld Lang Syne", and the same in the
    /// other languages. The album makes the whole record a New Year's record, as for
    /// <see cref="IsHoliday"/>. Every New Year's song is also holiday music.
    /// </summary>
    public static bool IsNewYear(string? title, string? album, string? genre) =>
        Names(NewYearWords(), album) || Names(NewYearWords(), genre) || Names(NewYearWords(), title);

    /// <summary>
    /// Whether <paramref name="local"/> falls in the holiday season: from 22 November to 6 January,
    /// both included.
    ///
    /// <para>The shops' Christmas starts in late November, about five weeks before it. The season ends
    /// on the sixth of January, Twelfth Night, when the decorations come down. Outside it, the holiday
    /// songs stay out of every list.</para>
    /// </summary>
    /// <param name="local">The listener's own date. The time of day is not read.</param>
    public static bool InSeason(DateTime local) =>
        (local.Month == 11 && local.Day >= SeasonStartsOnNovember)
        || local.Month == 12
        || (local.Month == 1 && local.Day <= SeasonEndsOnJanuary);

    /// <summary>
    /// Whether <paramref name="local"/> is in the days the New Year's Eve list is offered: from Boxing Day,
    /// 26 December, to New Year's Day, both included.
    ///
    /// <para>From the day after Christmas, so the list is there while the party is planned, and until the
    /// day after, for the morning. Never in the Christmas days themselves, when it would sit beside a
    /// Christmas the list has no songs for.</para>
    /// </summary>
    /// <param name="local">The listener's own date. The time of day is not read.</param>
    public static bool IsNewYearsEveTime(DateTime local) =>
        (local.Month == 12 && local.Day >= NewYearsEveFromDecember) || (local.Month == 1 && local.Day == 1);

    /// <summary>The first day of the season, in November.</summary>
    public const int SeasonStartsOnNovember = 22;

    /// <summary>The last day of the season, in January.</summary>
    public const int SeasonEndsOnJanuary = 6;

    /// <summary>The first day the New Year's Eve list is offered, in December.</summary>
    public const int NewYearsEveFromDecember = 26;

    private static bool Names(Regex words, string? text, bool holidayAlone = false)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (Match match in words.Matches(text))
            if (holidayAlone || !match.Value.StartsWith("holiday", StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>
    /// Text that every holiday word contains, for a database to narrow by before <see cref="IsHoliday"/>
    /// reads the words properly. A superset: a track whose tags hold none of these cannot be holiday
    /// music, and one that holds one may not be ("Santa Barbara").
    ///
    /// <para>"noël" and "année" are listed in both cases because SQLite's LIKE ignores case for the
    /// English letters only.</para>
    /// </summary>
    public static IReadOnlyList<string> Hints { get; } =
    [
        "christmas", "xmas", "x-mas", "kerst", "noel", "noël", "NOËL", "navidad", "weihnacht", "holiday",
        "jingle", "santa", "sleigh", "mistletoe", "winter wonderland", "silent night", "stille nacht",
        "new year", "auld lang syne", "nieuwjaar", "oudejaar", "oud en nieuw", "silvester", "nochevieja",
        "año nuevo", "AÑO NUEVO", "bonne année", "BONNE ANNÉE",
    ];

    // The New Year's words, in the languages of the holiday words below. "Oudejaar" takes in
    // oudejaarsavond and oudejaarsdag; "Silvester" is the German and Dutch name for the night itself.
    private const string NewYear =
        @"new year'?s?|auld lang syne|nieuwjaar\w*|oudejaar\w*|oud en nieuw|silvester|nochevieja|año nuevo|bonne année";

    // "Santa" is the saint before a place's name far more often than it is Father Christmas: Santa Barbara,
    // Santa Monica, Santa Fe. Those are left out by name, and so is a remixer called Santa ("Rush (Tom
    // Santa Remix)"). "Santa Claus", "Santa Baby" and "Santa Tell Me" are not.
    [GeneratedRegex(
        @"\b(christmas|xmas|x-mas|kerst|kerstmis|noel|noël|navidad|weihnacht\w*|holiday|holidays|jingle|" +
        @"santa(?!\s+(?:barbara|monica|fe|cruz|clara|maria|ana|rosa|cecilia|esmeralda|teresa|catalina|marta|lucia" +
        @"|remix|mix|edit|dub|version)\b)|" +
        @"sleigh|mistletoe|winter wonderland|silent night|stille nacht|" + NewYear + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Words();

    [GeneratedRegex(@"\b(" + NewYear + @")\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NewYearWords();
}
