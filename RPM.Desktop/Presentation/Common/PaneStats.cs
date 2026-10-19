using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// What a pane's subtitle says: the one place the counts and the running time are worded, so the
/// library and the playlists state the same kind of fact in the same voice rather than each inventing
/// its own sentence.
/// </summary>
internal static class PaneStats
{
    /// <summary>What separates one fact from the next. It is the same bullet that the album blocks put
    /// between a record's genre and its year, so a subtitle reads as one line of the same
    /// interface.</summary>
    private const string Separator = "  •  ";

    /// <summary>
    /// A span of time at the scale it actually is: minutes for an EP, hours for a shelf, days for a
    /// collection. A number of minutes is the right way to say four minutes. It is a useless way to say three
    /// weeks, and a library's subtitle is far more often the second case.
    /// </summary>
    public static string Duration(double seconds)
    {
        var minutes = WholeMinutes(seconds);

        if (minutes < 60)
            return Loc.Format("Stats_Minutes", minutes);

        var hours = minutes / 60;
        if (hours < 24)
            return Loc.Format("Stats_HoursMinutes", hours, minutes % 60);

        var days = hours / 24;
        return days == 1
            ? Loc.Format("Stats_DaysHoursOne", hours % 24)
            : Loc.Format("Stats_DaysHours", days, hours % 24);
    }

    /// <summary>
    /// A running time in whole minutes: seconds rounded to the nearest second, then the part of a minute
    /// dropped. The pane's subtitle and a record's summary both take their number from this, so they cannot
    /// disagree about the same songs. If one rounded and the other dropped the seconds, a 5:47 track could
    /// read as "5 min" in one place and "6 minutes" in the other. Dropping is the rule because the playlist
    /// length, Pegasus and the phone (<c>AlbumPage.runtime</c>, <c>PlaylistsPage.runtime</c>) drop them too.
    /// </summary>
    public static long WholeMinutes(double seconds) => (long)Math.Round(Math.Max(0, seconds)) / 60;

    /// <summary>The collection: how many records, how many songs, how long they play.</summary>
    public static string Library(int albums, int songs, double seconds)
        => songs <= 0
            ? Loc.Get("Stats_Empty")
            : string.Join(Separator, Count(albums, "Stats_AlbumsOne", "Stats_Albums"),
                                     Count(songs, "Stats_SongsOne", "Stats_Songs"),
                                     Duration(seconds));

    /// <summary>The playlist tree: how many playlists, and how long they run.</summary>
    public static string Playlists(int playlists, double seconds)
        => playlists <= 0
            ? Loc.Get("Stats_Empty")
            : string.Join(Separator, Count(playlists, "Stats_PlaylistsOne", "Stats_Playlists"),
                                     Duration(seconds));

    /// <summary>The radio: how many stations the dial is currently carrying.</summary>
    public static string Stations(int stations)
        => stations <= 0
            ? ""   // Nothing is listed yet, or the search found nothing. The list itself says so.
            : Count(stations, "Stats_StationsOne", "Stats_Stations");

    /// <summary>
    /// How many of a borrowed shelf's playlists are being held back while its owner is off the network. This
    /// is the clause that stops a narrowed pane from looking like a lost one.
    ///
    /// <para>It uses the same count wording as the subtitle beside it. So "1 playlist" and "3 playlists" are
    /// worded the same way as everywhere else in the app.</para>
    /// </summary>
    public static string PlaylistsOffline(int playlists)
        => Loc.Format("Playlists_OfflineHidden",
                      Count(playlists, "Stats_PlaylistsOne", "Stats_Playlists"));

    /// <summary>One count, worded for one or for many. "1 album" is not "1 albums" in any of the five
    /// languages. The singular is also the first case that a new collection shows.</summary>
    private static string Count(int value, string oneKey, string manyKey)
        => value == 1 ? Loc.Get(oneKey) : Loc.Format(manyKey, value);
}
