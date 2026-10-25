using RPM.Core.Metadata.Providers;
using RPM.Fingerprint.Catalog;

namespace RPM.Core.Library;

/// <summary>
/// The acts in this collection closest to one act, for the related artists row on an artist's page. Read from
/// the collection alone: no service of the app's says who is related to whom.
///
/// <para>Three things make two acts alike, and the strongest of them is taken:</para>
/// <list type="bullet">
/// <item><b>Records made together.</b> An act on one of the other's records, or the other way round, counts
/// most: <see cref="Together"/> for each such record.</item>
/// <item><b>The same genres.</b> The share of each act's own records on each genre card, overlapped: 1 for two
/// acts whose records are filed alike, 0 for none in common. A record on no card
/// (<see cref="GenreWall.Unclassified"/>) may still carry a genre tag that no card answers to, so the same
/// overlap of those tags as written counts too.</item>
/// <item><b>The lists they share.</b> Playlists and generated lists, each counting for as few acts as it holds
/// (a list of three acts says more than one of thirty): the share of the act's own list weight the other is
/// on. Most collections are tagged unevenly, and an untagged act is still placed by the lists it is in.</item>
/// </list>
///
/// <para>An act with no record in common and less than <see cref="LeastAlike"/> of anything else is not
/// related. The compilations act is never one. At most <see cref="Most"/>, closest first; a tie goes to the
/// act with more songs, then to the name.</para>
///
/// <para>The Android TV app's rule (RelatedArtists.kt), so the row is the same on every screen that shows
/// it.</para>
/// </summary>
public static class RelatedArtists
{
    /// <summary>The most acts the row holds.</summary>
    public const int Most = 12;

    /// <summary>What one record made together counts, against an overlap of at most 1.</summary>
    public const double Together = 2.0;

    /// <summary>How alike an act must be without a record in common.</summary>
    public const double LeastAlike = 0.5;

    /// <summary>The acts related to <paramref name="artist"/>, closest first.</summary>
    /// <param name="artist">The act whose page it is.</param>
    /// <param name="artists">Every act on the artists wall (<see cref="ArtistWall.Build"/>).</param>
    /// <param name="genres">The genres wall (<see cref="GenreWall.Build"/>): which card each record is
    /// on.</param>
    /// <param name="albumTags">Each record's genre tag as written, by folder, where it has one. Only read for
    /// records on no card.</param>
    /// <param name="lists">Every list of the collection, as the folders of the songs on it. An act is on a
    /// list when one of its own records holds one of the list's songs.</param>
    public static IReadOnlyList<ArtistWallItem> Of(
        ArtistWallItem artist,
        IReadOnlyList<ArtistWallItem> artists,
        IReadOnlyList<GenreWallItem> genres,
        IReadOnlyDictionary<string, string> albumTags,
        IEnumerable<IReadOnlyCollection<string>> lists)
    {
        ArgumentNullException.ThrowIfNull(artist);
        ArgumentNullException.ThrowIfNull(artists);
        ArgumentNullException.ThrowIfNull(genres);
        ArgumentNullException.ThrowIfNull(albumTags);
        ArgumentNullException.ThrowIfNull(lists);

        var cardOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var genre in genres)
        {
            if (genre.Name == GenreWall.Unclassified)
                continue;
            foreach (var folder in genre.Folders)
                cardOf[folder] = genre.Name;
        }

        var tagOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, tag) in albumTags)
            if (Tag(tag) is { } folded)
                tagOf[folder] = folded;

        var mine = new HashSet<string>(artist.Folders, StringComparer.OrdinalIgnoreCase);
        var myCards = Shares(artist.Own, cardOf);
        var myTags = Shares(artist.Own, tagOf);
        var me = NameMatch.Squash(artist.Name);
        var listed = ListShares(me, artists, lists);

        return artists
            .Where(other => NameMatch.Squash(other.Name) != me && !ArtistCredit.IsVariousArtists(other.Name))
            .Select(other =>
            {
                var together = other.Folders.Count(mine.Contains);
                var alike = Math.Max(
                    Math.Max(Overlap(myCards, Shares(other.Own, cardOf)), Overlap(myTags, Shares(other.Own, tagOf))),
                    listed.GetValueOrDefault(NameMatch.Squash(other.Name)));
                return (Act: other, Together: together, Score: (together * Together) + alike);
            })
            .Where(scored => scored.Together > 0 || scored.Score >= LeastAlike)
            .OrderByDescending(scored => scored.Score)
            .ThenByDescending(scored => scored.Act.Tracks)
            .ThenBy(scored => scored.Act.Name, StringComparer.Ordinal)
            .Take(Most)
            .Select(scored => scored.Act)
            .ToList();
    }

    /// <summary>A genre tag as one value: trimmed and lower case. Null for an empty tag.</summary>
    public static string? Tag(string? tag) =>
        tag?.Trim().ToLowerInvariant() is { Length: > 0 } folded ? folded : null;

    /// <summary>
    /// For every other act, the share of <paramref name="me"/>'s list weight they are on. Each list
    /// <paramref name="me"/> is on weighs one over the number of acts on it, and an act on all of them has
    /// 1. A list with fewer than two acts on it says nothing about anybody.
    /// </summary>
    private static Dictionary<string, double> ListShares(
        string me, IReadOnlyList<ArtistWallItem> artists, IEnumerable<IReadOnlyCollection<string>> lists)
    {
        var owners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var act in artists)
        {
            var name = NameMatch.Squash(act.Name);
            foreach (var folder in act.Own)
            {
                if (!owners.TryGetValue(folder, out var names))
                    owners[folder] = names = new HashSet<string>(StringComparer.Ordinal);
                names.Add(name);
            }
        }

        var total = 0.0;
        var shared = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var list in lists)
        {
            var acts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var folder in list)
                if (owners.TryGetValue(folder, out var names))
                    acts.UnionWith(names);
            if (!acts.Contains(me) || acts.Count < 2)
                continue;

            var weight = 1.0 / acts.Count;
            total += weight;
            foreach (var act in acts)
                if (act != me)
                    shared[act] = shared.GetValueOrDefault(act) + weight;
        }

        if (total == 0)
            return [];
        foreach (var act in shared.Keys.ToList())
            shared[act] /= total;
        return shared;
    }

    /// <summary>The share of <paramref name="folders"/> on each card (or tag). Records on none are left out
    /// of the count.</summary>
    private static Dictionary<string, double> Shares(IEnumerable<string> folders, IReadOnlyDictionary<string, string> of)
    {
        var filed = folders.Select(f => of.GetValueOrDefault(f)).OfType<string>().ToList();
        if (filed.Count == 0)
            return [];
        return filed.GroupBy(f => f, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => (double)g.Count() / filed.Count, StringComparer.Ordinal);
    }

    /// <summary>How much two shares have in common: the sum, over each card, of the smaller share.</summary>
    private static double Overlap(Dictionary<string, double> a, Dictionary<string, double> b) =>
        a.Sum(entry => Math.Min(entry.Value, b.GetValueOrDefault(entry.Key)));
}
