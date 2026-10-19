using RPM.Core.Metadata;

namespace RPM.Core.Library;

/// <summary>One card on the genres wall: the kind of music, and the acts the collection files under it.
/// </summary>
/// <param name="Name">The genre as the app spells it. It is one of <see cref="Genres.All"/>, or
/// <see cref="GenreWall.Unclassified"/> for music that matches none of them.</param>
/// <param name="Artists">The acts on this genre, biggest first. The order is by the most records under this
/// genre, then by the most songs. The card is drawn from the first act a picture can be found for, so the
/// order matters.</param>
/// <param name="Folders">The album folders filed under this genre, in no particular order.
///
/// <para><b>Why the folders are on the item.</b> The acts are enough to draw a card. A genre page also asks
/// questions that the acts cannot answer: what arrived last, what is played most, which decades it covers.
/// Each of these is a question about records, not people. So they are worked out here, once, on the same
/// pass that files the records. An act's folders are the folders of every genre the act is on. So a page
/// that took its records from its artists would show a rock band's jazz record on the rock
/// page.</para></param>
public sealed record GenreWallItem(
    string Name, IReadOnlyList<ArtistWallItem> Artists, IReadOnlyList<string> Folders)
{
    /// <summary>Whether two readings of the wall say the same about this genre: the same name, the same
    /// records and the same acts in the same order.
    ///
    /// <para>This is asked because the wall is rebuilt whole on every library change, anywhere in the
    /// collection. Examples are a peer's catalogue arriving, an import finishing, or a play count being
    /// written. The genre page that is open is opened again on the new item each time (see
    /// <c>MainPage.FeedGenres</c>). Most of those readings describe a genre that has not changed. Reopening
    /// on one takes the page's shelves apart and rebuilds them for whoever is reading it.</para>
    ///
    /// <para><b>The generated <c>==</c> does not answer this.</b> Two of the three members are lists. A
    /// record compares lists by reference, and the lists are new objects on every build. So equality is
    /// always false, and every reading looks like news.</para>
    ///
    /// <para>The acts are compared IN ORDER. The folders are not. The order of the acts is part of the
    /// card: biggest first, and the first act with a picture is the face the card wears. So the order is
    /// part of what the genre is. The folders have no order worth comparing.</para>
    /// </summary>
    public bool SameAs(GenreWallItem other)
    {
        if (ReferenceEquals(this, other))
            return true;

        return SameActsAs(other)
            && Folders.Count == other.Folders.Count
            && Folders.Order(StringComparer.OrdinalIgnoreCase)
                      .SequenceEqual(other.Folders.Order(StringComparer.OrdinalIgnoreCase),
                                     StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Whether two readings name the same genre with the same acts in the same order. This is the
    /// half of <see cref="SameAs"/> that the card's PHOTOGRAPH depends on.
    ///
    /// <para><b>Asked apart from the records, because the two change at different times.</b> The card wears
    /// the face of the first act on this genre that anybody has a picture of. So the face is right only
    /// while the acts are right. The records change on every import, and they say nothing about the face.
    /// If the acts change, the card may be wearing the face of somebody who has left the collection. For
    /// example, signing out of an account changes the acts. The card must then not keep the face of an
    /// artist who came from the cloud catalogue.</para>
    /// </summary>
    public bool SameActsAs(GenreWallItem other)
    {
        if (ReferenceEquals(this, other))
            return true;

        return string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase)
            && Artists.Count == other.Artists.Count
            && Artists.Select(a => a.Name)
                      .SequenceEqual(other.Artists.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>The collection read by WHAT KIND of music it is: one card per genre the app knows, in the
/// app's own order, each holding the artists filed under it.
///
/// <para>It is built on the app's closed list of genres, not on the tags as typed. A tag holds whatever the
/// last tagger wrote, such as "Hip Hop", "Rap" or "Pop Rap". A wall built on those strings would show three
/// cards for one kind of music, plus a hundred cards nobody asked for. So each record's tag goes through
/// <see cref="Genres.Match"/> first. What comes out is one of the genres the app files music under.</para>
///
/// <para><b>And everything else is one card.</b> A record with no genre tag and a record with a tag the
/// list has no room for are the same to a reader. Both are music the app cannot classify. Both go on
/// <see cref="Unclassified"/>, the last card on the wall.</para>
///
/// <para>It is pure and does not use the database, like <see cref="ArtistWall"/>. The caller passes in the
/// acts and each album folder's tag. This returns the wall.</para>
/// </summary>
public static class GenreWall
{
    /// <summary>The card for music the app cannot file: untagged, or tagged with something none of the
    /// app's genres matches. It has its own word rather than <see cref="Genres.Unknown"/>. That is a genre
    /// a person picks in a tag box, and it reads as a genre. This word means the absence of one.
    /// </summary>
    public const string Unclassified = "Other";

    /// <summary>The wall, in the app's own genre order, with <see cref="Unclassified"/> last.
    ///
    /// <para>Only genres that have somebody on them are included. A card with no artist has no background
    /// and nothing to open. A wall of many empty cards for a collection that is all rock would look like an
    /// app that has lost the music.</para>
    /// </summary>
    /// <param name="artists">The acts, as the artists wall has them. See <see cref="ArtistWall.Build"/>. An
    /// act is on a genre if one of their records is, whether it is their own record or a guest appearance.
    /// So a singer with one song on a jazz compilation is on the jazz wall.</param>
    /// <param name="albumGenres">What each album folder is tagged with, keyed by folder. A folder missing
    /// from this has nothing tagged. That is the most common route to <see cref="Unclassified"/>.</param>
    public static IReadOnlyList<GenreWallItem> Build(
        IEnumerable<ArtistWallItem> artists,
        IReadOnlyDictionary<string, string> albumGenres)
    {
        // For each genre: each act, and how many of that act's records are on this genre. Keyed by NAME
        // rather than by instance, because the count orders the card. An act reached through two of their
        // folders is one artist with two records on the genre, not two artists.
        var under = new Dictionary<string, Dictionary<string, (ArtistWallItem Artist, int Records)>>(
            StringComparer.Ordinal);

        // Work out each folder's genre once per FOLDER, not once per artist per folder. A record with three
        // names on it would otherwise be checked three times. Matching a tag means folding it to plain
        // letters. That is cheap when done once, but a keystroke in the library's search rebuilds the whole
        // wall.
        var filed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var artist in artists)
        {
            foreach (var folder in artist.Folders)
            {
                if (!filed.TryGetValue(folder, out var genre))
                    filed[folder] = genre = GenreOf(folder, albumGenres);

                if (!under.TryGetValue(genre, out var acts))
                    under[genre] = acts = new Dictionary<string, (ArtistWallItem, int)>(
                        StringComparer.OrdinalIgnoreCase);

                acts[artist.Name] = acts.TryGetValue(artist.Name, out var standing)
                    ? (standing.Artist, standing.Records + 1)
                    : (artist, 1);
            }
        }

        // The records on each genre, from the same pass that filed them. This is taken from `filed`, not
        // from the acts. A folder is on exactly one genre. An act is on as many genres as its records are.
        // Gathering an act's folders back up would put every record the act ever made on every genre the
        // act touched.
        var records = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (folder, genre) in filed)
        {
            if (!records.TryGetValue(genre, out var folders))
                records[genre] = folders = [];
            folders.Add(folder);
        }

        var wall = new List<GenreWallItem>(under.Count);
        foreach (var genre in Order)
        {
            if (!under.TryGetValue(genre, out var acts))
                continue;

            // Most records on this genre first, then most songs anywhere, then by name. The count is for
            // this genre only, not the whole collection. An act with four hundred rock songs and two jazz
            // songs is not who the jazz card is about.
            wall.Add(new GenreWallItem(
                genre,
                [.. acts.Values
                    .OrderByDescending(a => a.Records)
                    .ThenByDescending(a => a.Artist.Tracks)
                    .ThenBy(a => a.Artist.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(a => a.Artist)],
                records.TryGetValue(genre, out var folders) ? folders : []));
        }
        return wall;
    }

    /// <summary>The card for one album folder's music: the app's own genre that the tag matches, or
    /// <see cref="Unclassified"/> for everything else. That covers a folder with no tag, and a tag that
    /// matches nothing.
    /// </summary>
    private static string GenreOf(string folder, IReadOnlyDictionary<string, string> albumGenres)
        => albumGenres.TryGetValue(folder, out var tagged) ? Genres.Match(tagged) ?? Unclassified
                                                          : Unclassified;

    /// <summary>The order the cards are shown in: the app's own list, with the unfiled card at the end.
    ///
    /// <para><see cref="Genres.Unknown"/> is dropped here. It is the list's word for the same music that
    /// <see cref="Unclassified"/> holds. A wall with both would show two cards for one answer. A record
    /// that somebody tagged "Other" by hand matches nothing, so it lands on the same card as a record with
    /// no tag. That is correct.</para>
    /// </summary>
    private static readonly IReadOnlyList<string> Order =
        [.. Genres.All.Where(g => g != Genres.Unknown), Unclassified];
}
