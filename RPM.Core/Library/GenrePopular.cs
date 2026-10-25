using RPM.Fingerprint.Catalog;

namespace RPM.Core.Library;

/// <summary>
/// A genre's popular songs: the songs the world plays most (<see cref="LibraryDatabase.SaveWorldPlays"/>),
/// across the genre rather than one act. It follows <see cref="ArtistPopular"/>'s rules (one per title, none
/// below <see cref="ArtistPopular.Fewest"/>), with two more, which are the Android TV app's
/// (GenrePopular.kt):
/// <list type="bullet">
/// <item><b>From the records played most here.</b> A genre can hold hundreds of records. The songs are taken
/// from its <see cref="Records"/> records the listener plays most, so the list is the genre as this
/// collection knows it.</item>
/// <item><b>At most <see cref="PerAct"/> songs from one act</b> (a record's album artist), so one famous act
/// does not fill the list.</item>
/// </list>
/// </summary>
public static class GenrePopular
{
    /// <summary>How many of the genre's records the songs are taken from.</summary>
    public const int Records = 40;

    /// <summary>The most songs one act has in the list.</summary>
    public const int PerAct = 3;

    /// <summary>The records the songs are taken from: the ones played most here first, then in the order
    /// given, at most <see cref="Records"/>.</summary>
    /// <param name="plays">How often the listener has played a record. Records never played keep the order
    /// they were given in, after the played ones.</param>
    public static IReadOnlyList<T> RecordsToRank<T>(IEnumerable<T> records, Func<T, int> plays)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(plays);

        // OrderByDescending is a stable sort: records played as often keep the order they were given in.
        return [.. records.OrderByDescending(plays).Take(Records)];
    }

    /// <summary>
    /// The popular songs out of <paramref name="played"/>, most played first.
    /// </summary>
    /// <param name="played">The songs of the records that have a play count, with it. Songs counted the same
    /// keep the order they are given in.</param>
    /// <param name="title">A song's title.</param>
    /// <param name="plays">A song's play count. Songs counted at nought or less are left out.</param>
    /// <param name="act">Who a song's record is filed under. Compared as names are compared
    /// (<see cref="NameMatch.Squash"/>).</param>
    /// <param name="songs">How many songs the records hold in all: the half rule's whole.</param>
    /// <param name="most">The most the screen shows.</param>
    public static IReadOnlyList<T> Ranked<T>(
        IEnumerable<T> played, Func<T, string?> title, Func<T, long> plays, Func<T, string?> act, int songs, int most)
    {
        ArgumentNullException.ThrowIfNull(act);

        var perAct = new Dictionary<string, int>(StringComparer.Ordinal);
        var kept = new List<T>(most);
        foreach (var song in ArtistPopular.Ranked(played, title, plays, songs, int.MaxValue))
        {
            var who = NameMatch.Squash(act(song));
            var had = perAct.GetValueOrDefault(who);
            if (had >= PerAct)
                continue;
            perAct[who] = had + 1;
            kept.Add(song);
            if (kept.Count == most)
                break;
        }

        return kept.Count >= ArtistPopular.Fewest ? kept : [];
    }
}
