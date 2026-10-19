namespace RPM.Core.Library;

/// <summary>What one entry in the quick selection stands for.</summary>
public enum QuickPickKind
{
    /// <summary>One song, keyed by its library-relative path.</summary>
    Song,

    /// <summary>One record, keyed by its library-relative folder.</summary>
    Album,

    /// <summary>One playlist, saved or generated. It is keyed as <see cref="LibraryDatabase.PlaylistKey"/>
    /// writes it, or by its node id.</summary>
    Playlist,

    /// <summary>One radio station, keyed by its uuid.</summary>
    Station,
}

/// <summary>One entry: its kind, and the key that names it.</summary>
public readonly record struct QuickPick(QuickPickKind Kind, string Key);

/// <summary>
/// The quick selection under the home screen's cards: the things a listener plays most, of whatever kind
/// they are.
///
/// <para><b>The four kinds are not ranked against each other.</b> A song played fifty times, a record
/// played fifty times, a playlist started fifty times, and a station heard for fifty hours are four
/// numbers with four different meanings. Any weighting between them would be a guess that looks like
/// arithmetic. So each kind is ranked within itself, by the data that knows about it. Songs and records
/// are ranked by the library database. Playlists are ranked by the play log, and stations by the station
/// log. This class then deals one entry from each kind in turn.</para>
///
/// <para><see cref="HomeFeatures.Strip"/> uses the same design for the banner, for the same reason. A
/// single ranking would give sixteen songs on one collection and sixteen records on another. Dealing from
/// each kind avoids that.</para>
/// </summary>
public static class QuickPicks
{
    /// <summary>How many entries the home screen shows: four panels of four items each.</summary>
    public const int Count = 16;

    /// <summary>How many rows one panel holds. The panels are columns, so this is the height of the block.
    /// <c>Count / Panels</c> is therefore not a coincidence. It is the definition.</summary>
    public const int Tall = 4;

    /// <summary>How many panels stand side by side.</summary>
    public const int Panels = Count / Tall;

    /// <summary>
    /// The selection, in the order it is laid out.
    /// </summary>
    /// <remarks>
    /// Each kind gives one entry before any kind gives a second. A kind with nothing to offer is skipped
    /// over, so there is no gap. On a new collection most kinds are empty, because nothing has been played
    /// yet.
    ///
    /// <para>Dealing stops when the count is full or every source is used up. A collection that can only
    /// answer with songs gets sixteen songs. That is better than four songs and twelve gaps. It is also
    /// what a listener who only plays songs has.</para>
    /// </remarks>
    /// <param name="songs">Most played first, by library-relative path.</param>
    /// <param name="albums">Most played first, by library-relative folder.</param>
    /// <param name="playlists">Most played first, keyed as the play log keys them.</param>
    /// <param name="stations">Most listened first, by uuid.</param>
    /// <param name="count">How many to choose, at most.</param>
    public static IReadOnlyList<QuickPick> Choose(
        IReadOnlyList<string> songs,
        IReadOnlyList<string> albums,
        IReadOnlyList<string> playlists,
        IReadOnlyList<string> stations,
        int count)
    {
        if (count <= 0)
            return [];

        (QuickPickKind Kind, IReadOnlyList<string> Keys)[] sources =
        [
            (QuickPickKind.Song, songs),
            (QuickPickKind.Album, albums),
            (QuickPickKind.Playlist, playlists),
            (QuickPickKind.Station, stations),
        ];

        var picked = new List<QuickPick>(count);

        // How far each source has been read. Each source has its own position, so a short source is
        // skipped once it runs out.
        var cursor = new int[sources.Length];

        while (picked.Count < count)
        {
            var dealt = false;

            for (var i = 0; i < sources.Length && picked.Count < count; i++)
            {
                var (kind, keys) = sources[i];

                // An entry with no key is stepped past. It loses only its own turn, not the whole turn of
                // its source. If this were a continue, a single empty key at the front of a list would
                // end the whole deal, because no source dealt anything in that round.
                while (cursor[i] < keys.Count && string.IsNullOrWhiteSpace(keys[cursor[i]]))
                    cursor[i]++;

                if (cursor[i] >= keys.Count)
                    continue;

                picked.Add(new QuickPick(kind, keys[cursor[i]++]));
                dealt = true;
            }

            // Every source is used up. Without this check the loop would run forever on a collection too
            // small to fill the selection, such as every new collection.
            if (!dealt)
                break;
        }

        return picked;
    }

    /// <summary>
    /// The selection cut into the panels it is drawn in, down each panel and then across.
    /// </summary>
    /// <remarks>
    /// <b>Down before across. The layout reads down each column first. A wrap panel works differently.</b>
    /// Sixteen items in four columns of four means the first four go down the first column. A panel filled
    /// across would put the four most played items in a row along the top, and the sixteenth at the bottom
    /// right. That reads as a grid, not as four lists.
    ///
    /// <para>Short panels are returned short, not padded. A collection that can only answer with six things
    /// gets a panel of four, a panel of two, and two panels that are not drawn.</para>
    /// </remarks>
    public static IReadOnlyList<IReadOnlyList<QuickPick>> InPanels(
        IReadOnlyList<QuickPick> picks, int tall = Tall)
    {
        ArgumentNullException.ThrowIfNull(picks);

        if (tall <= 0 || picks.Count == 0)
            return [];

        var panels = new List<IReadOnlyList<QuickPick>>();
        for (var at = 0; at < picks.Count; at += tall)
            panels.Add([.. picks.Skip(at).Take(tall)]);

        return panels;
    }
}
