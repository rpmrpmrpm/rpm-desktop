namespace RPM.Core.Library;

/// <summary>
/// An artist's popular songs: the songs of theirs the world plays most (<see cref="LibraryDatabase.SaveWorldPlays"/>),
/// one per title, as many as the screen asks for and never more than half of everything of theirs.
///
/// <para>The rules are the Android TV app's and the iPhone app's (<c>ArtistPopular</c>), so an artist's
/// popular songs are the same list on every screen that shows them.</para>
/// <list type="bullet">
/// <item><b>One per title.</b> A studio take and its live version are one song to a listener. The title is
/// compared without what is in brackets (<see cref="TitleKey"/>), so "Hello (Live at the BBC)" is
/// "Hello".</item>
/// <item><b>At most half of their songs.</b> An artist with one record of ten songs gets five, so the list is
/// not the record again above itself.</item>
/// <item><b>None below <see cref="Fewest"/>.</b> Two songs are not a list of popular songs, they are two
/// songs.</item>
/// </list>
/// </summary>
public static class ArtistPopular
{
    /// <summary>The fewest songs that make a list. Fewer than this and there is no list at all.</summary>
    public const int Fewest = 4;

    /// <summary>
    /// The popular songs out of <paramref name="played"/>, most played first.
    /// </summary>
    /// <param name="played">The artist's songs that have a play count, with it. The order is kept for songs
    /// with the same count, so the caller decides how ties fall (the database gives them in path order).</param>
    /// <param name="title">A song's title. A song with no title counts as its own title.</param>
    /// <param name="plays">A song's play count. Songs counted at nought or less are left out.</param>
    /// <param name="songs">How many songs of theirs there are in all, counted or not: the half rule's
    /// whole.</param>
    /// <param name="most">The most the screen shows.</param>
    public static IReadOnlyList<T> Ranked<T>(
        IEnumerable<T> played, Func<T, string?> title, Func<T, long> plays, int songs, int most)
    {
        ArgumentNullException.ThrowIfNull(played);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(plays);

        var cap = Math.Min(most, songs / 2);
        if (cap < Fewest)
            return [];

        // OrderByDescending is a stable sort: songs counted the same keep the order they were given in.
        var titles = new HashSet<string>(StringComparer.Ordinal);
        var listed = new List<T>(cap);
        foreach (var song in played.Where(s => plays(s) > 0).OrderByDescending(plays))
        {
            if (!titles.Add(TitleKey(title(song))))
                continue;
            listed.Add(song);
            if (listed.Count == cap)
                break;
        }

        return listed.Count >= Fewest ? listed : [];
    }

    /// <summary>A title as one song: lower case, with what is in round or square brackets taken off. A title
    /// that is nothing but brackets is kept whole, so it is not the same song as every other such
    /// title.</summary>
    public static string TitleKey(string? title)
    {
        var whole = (title ?? "").ToLowerInvariant();
        var text = whole;
        while (true)
        {
            var open = text.IndexOfAny(['(', '[']);
            if (open < 0)
                break;
            var end = text.IndexOf(text[open] == '(' ? ')' : ']', open);
            if (end < 0)
                break;
            text = text.Remove(open, end - open + 1);
        }

        var plain = text.Trim();
        return plain.Length > 0 ? plain : whole;
    }
}
