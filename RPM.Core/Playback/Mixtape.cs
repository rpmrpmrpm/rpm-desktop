namespace RPM.Core.Playback;

/// <summary>
/// A list that ends goes on as a mixtape. When the last song of an album, a playlist or a single starts, songs
/// like the list are queued after it as a song radio (<see cref="Library.SongRadio"/>), so the music carries on
/// and does not stop. The radio is seeded from the whole list: its first song is the song the radio compares
/// with, and every song of the list is in its history, so the list is not played again.
///
/// <para>The rules are here, apart from the player, so they can be tested: when a list carries on, what the
/// radio starts from, and what it leaves out. The player asks them when a song goes on the deck, and queues the
/// songs the radio picks.</para>
/// </summary>
public static class Mixtape
{
    /// <summary>How many of the latest listens a mixtape leaves out. A song heard in the last hours is not what
    /// a listener wants to hear again as the next surprise.</summary>
    public const int Spares = 60;

    /// <summary>
    /// Whether the song at <paramref name="playing"/>, which has just started, ends its list of
    /// <paramref name="count"/> songs, so a mixtape carries on after it.
    ///
    /// <para>Not after a song a radio queued: that radio tops itself up already. Not on repeat, where the list
    /// goes round again, and not on shuffle, where the last place in the list is not the end of it. Not in a
    /// DJ set, which mixes its own records and ends where it ends.</para>
    /// </summary>
    public static bool CarriesOn(int playing, int count, bool playingIsRadio, bool repeat, bool shuffle, bool djSet) =>
        count > 0 && playing == count - 1 && !playingIsRadio && !repeat && !shuffle && !djSet;

    /// <summary>
    /// What the mixtape starts from: the list's first song, and every song of the list in its order, each once.
    /// Songs the collection does not hold (null, as the caller reads them) are left out. Null when none is held,
    /// since a radio then has nothing to compare with.
    /// </summary>
    /// <param name="list">The list's songs in play order, as paths relative to the collection's root, or null
    /// for a song that is not in it.</param>
    public static (string Origin, IReadOnlyList<string> History)? Seed(IEnumerable<string?> list)
    {
        ArgumentNullException.ThrowIfNull(list);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var history = new List<string>();
        foreach (var song in list)
            if (song is { Length: > 0 } && seen.Add(song))
                history.Add(song);

        return history.Count == 0 ? null : (history[0], history);
    }

    /// <summary>
    /// The songs a mixtape must not pick: everything in the queue, and the songs of the latest
    /// <see cref="Spares"/> listens (<paramref name="heard"/>, newest first, as
    /// <see cref="Library.LibraryDatabase.RecentlyHeard"/> reads them). Only the first <see cref="Spares"/> of
    /// <paramref name="heard"/> are taken, whatever the caller hands in.
    /// </summary>
    public static IReadOnlySet<string> Spared(IEnumerable<string> inQueue, IEnumerable<string> heard)
    {
        ArgumentNullException.ThrowIfNull(inQueue);
        ArgumentNullException.ThrowIfNull(heard);

        var spared = new HashSet<string>(inQueue, StringComparer.OrdinalIgnoreCase);
        foreach (var song in heard.Take(Spares))
            spared.Add(song);
        return spared;
    }
}
