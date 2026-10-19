using RPM.Core.Metadata.Providers;

namespace RPM.Core.Library;

/// <summary>Who is on a list, and how many more there are than were named.</summary>
/// <param name="Named">The acts with the most songs on it, most first, ties by name. Empty when no song
/// on the list has a credit.</param>
/// <param name="More">How many other acts are on it: the ones there was no room to name. Zero when the
/// named ones are all of them, which is the case a caller describes differently.</param>
public sealed record Featured(IReadOnlyList<string> Named, int More);

/// <summary>
/// Who a playlist is made of, for the line under its name.
///
/// <para><b>Counted by act rather than by credit.</b> A credit is what a tagger typed. The same artist
/// is often typed in several ways across one list: "Kanye West", "Kanye West &amp; Ty Dolla $ign",
/// "Jay-Z &amp; Kanye West". Counted as written, those are three artists with one song each, and the
/// person with fifteen songs on the list never appears. Split into the acts they name, they are one
/// artist with three songs, which is the fact the line is meant to show.</para>
///
/// <para><b>The cost.</b> <see cref="ArtistCredit.Split"/> splits on the separators that taggers actually
/// type. Each separator can also appear inside a real name. "Earth, Wind &amp; Fire" comes out as three
/// acts. This is accepted here and nowhere else, because this is one decorative line under a heading.
/// The alternative is to print the credit whole. That keeps the band name intact, but it does not count
/// the artist the list is built around. Nothing is filed, shelved or looked up using this answer. See
/// <c>ArtistWall</c>, which asks the same question more carefully, because it decides where records
/// are stored.</para>
/// </summary>
public static class PlaylistFeatures
{
    /// <summary>
    /// The acts on a list, most songs first.
    /// </summary>
    /// <param name="credits">One credit per song, in the list's order. Null or blank credits are skipped.
    /// They are not counted as an unnamed act.</param>
    /// <param name="most">How many names to show. The caller must pass this in, because only the caller
    /// knows how many names fit on the line. This method does not know how wide the line is.</param>
    public static Featured Of(IEnumerable<string?> credits, int most)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(most);

        // The first spelling seen is kept, and counting ignores case. A list with "MIA" and "M.I.A." shows
        // one artist to a reader. Choosing by the list's own order is the only rule that is not arbitrary.
        var songs = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
        var spelling = new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);

        foreach (var credit in credits)
            foreach (var name in ArtistCredit.Split(credit))
            {
                songs[name] = songs.GetValueOrDefault(name) + 1;
                spelling.TryAdd(name, name);
            }

        if (songs.Count == 0)
            return new Featured([], 0);

        // Most songs first, then by name. The second key gives the same line every time it is drawn.
        // Without it, artists with equal counts would appear in whatever order the dictionary returns them.
        var named = songs
            .OrderByDescending(act => act.Value)
            .ThenBy(act => act.Key, StringComparer.CurrentCultureIgnoreCase)
            .Take(most)
            .Select(act => spelling[act.Key])
            .ToList();

        return new Featured(named, songs.Count - named.Count);
    }
}
