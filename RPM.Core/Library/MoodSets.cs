namespace RPM.Core.Library;

/// <summary>One square of the mood grid: one band for one shelf. For example, "the loud end of House" or
/// "the still end of Rap". A list that is not widened by this table is exactly one
/// square.</summary>
/// <param name="Kind">The band: one of <see cref="SmartPlaylists.BrokenOutByGenre"/>.</param>
/// <param name="Genre">The shelf, one of <see cref="Metadata.Genres.All"/>.</param>
public readonly record struct MoodCell(SmartPlaylistKind Kind, string Genre);

/// <summary>
/// Which squares of the mood grid each named list actually draws from.
///
/// <para><b>A list is normally one square.</b> Its name, its id and its songs all describe that one square.
/// Jazz Club, for example, is the calm end of Jazz and nothing else. Most lists are still like this. A
/// square with no line in this table is one of them. This table adds the lists that are wider than one
/// square. A listener does not ask for "the calm end of Blues". They ask for a jazz club, and a jazz club
/// has some soul and some blues in it.</para>
///
/// <para><b>The id does not move.</b> A widened list keeps the id of the square it started on, which is
/// its LEAD cell. So nothing about how a list is stored, shared, pinned or named changes. Only the source
/// of its songs changes. See <see cref="SmartPlaylistId"/>, which is what travels.</para>
///
/// <para><b>A set may cross bands. This is the one thing here that is not obvious.</b> Two bands never
/// share a song, because one wants busy music and the other sparse music, by construction. So a list that
/// draws from two bands is not a wider cut of the same music. It is two kinds of music in one list. This
/// is deliberate where it appears. "House Party" is the loud house and the long still house, because both
/// are what someone means by a house party. A cell reached this way is <see cref="Absorbed"/>. It is no
/// longer a row of its own, because the list that took it over shows its songs.</para>
///
/// <para><b>A cell in a set is NOT absorbed unless it is named below.</b> "Jazz Club" draws from Blues
/// and Soul, and both keep their own calm rows. The two lists hold the same records and show different
/// songs from them, which is what <see cref="Turn"/> is for. Absorption is a separate decision from
/// drawing, and it is written separately.</para>
///
/// <para>Pure, and it does not use the database. It is the counterpart of <see cref="MoodFlavour"/>. The
/// name of each list is the other half of this, and it lives with the interface, in
/// <c>SmartPlaylistFace.Titles</c>. A test holds the two together, so a cell that is absorbed here cannot
/// keep a name there.</para>
/// </summary>
public static class MoodSets
{
    /// <summary>
    /// The lists that draw from more than their own square, by the square they lead on.
    ///
    /// <para>The lead cell is always first in its own set. Everything after it is in the order it was
    /// added. The pools are interleaved in that order, and the lead should be the one that fills
    /// first.</para>
    /// </summary>
    private static readonly Dictionary<MoodCell, MoodCell[]> Sets = new()
    {
        // ── Chill ──

        // Just the Bars: the quiet end of hip-hop, wherever it is filed. "Rap" and "Hip-Hop / Rap" are
        // two shelves that hold one kind of music. A record tagged "Rap" is on the first shelf, and a
        // record tagged "Hip Hop" is on the second. A listener who asks for quiet bars does not mean one
        // shelf or the other.
        [new(SmartPlaylistKind.Calm, "Hip-Hop / Rap")] =
        [
            new(SmartPlaylistKind.Calm, "Hip-Hop / Rap"),
            new(SmartPlaylistKind.Calm, "Rap"),
        ],

        // Jazz Club: the room, not the shelf. Blues and soul keep their own calm rows. See the class
        // notes on drawing and absorbing.
        [new(SmartPlaylistKind.Calm, "Jazz")] =
        [
            new(SmartPlaylistKind.Calm, "Jazz"),
            new(SmartPlaylistKind.Calm, "Blues"),
            new(SmartPlaylistKind.Calm, "Soul"),
        ],

        // After the Party: what is still playing at four in the morning. Disco, and the funk and R&B that
        // came before it.
        [new(SmartPlaylistKind.Calm, "Disco")] =
        [
            new(SmartPlaylistKind.Calm, "Disco"),
            new(SmartPlaylistKind.Calm, "Funk"),
            new(SmartPlaylistKind.Calm, "R&B"),
        ],

        // ── Adrenaline ──

        // Main Floor: the big room. Everything that would be played on it, pop included. It also takes
        // over the loud indie. See Absorbed, where Guitar Fuzz goes.
        [new(SmartPlaylistKind.Energetic, "Electronic / Dance")] =
        [
            new(SmartPlaylistKind.Energetic, "Electronic / Dance"),
            new(SmartPlaylistKind.Energetic, "House"),
            new(SmartPlaylistKind.Energetic, "Club"),
            new(SmartPlaylistKind.Energetic, "Pop"),
            new(SmartPlaylistKind.Energetic, "Indie"),
        ],

        // Hard Bop: the loud end of the same three shelves Jazz Club takes the quiet end of.
        [new(SmartPlaylistKind.Energetic, "Jazz")] =
        [
            new(SmartPlaylistKind.Energetic, "Jazz"),
            new(SmartPlaylistKind.Energetic, "Blues"),
            new(SmartPlaylistKind.Energetic, "Soul"),
        ],

        // Funkadelica: also takes over the funk. See Absorbed, where Funk Fever goes.
        [new(SmartPlaylistKind.Energetic, "Soul")] =
        [
            new(SmartPlaylistKind.Energetic, "Soul"),
            new(SmartPlaylistKind.Energetic, "Funk"),
            new(SmartPlaylistKind.Energetic, "Jazz"),
        ],

        // House Party: a house party is not one tempo. It has the loud house, the club and techno beside
        // it, and the long still records that a house party also plays at five in the morning. Those
        // still records are the Focus band. That is why Long Player is absorbed, not kept.
        [new(SmartPlaylistKind.Energetic, "House")] =
        [
            new(SmartPlaylistKind.Energetic, "House"),
            new(SmartPlaylistKind.Energetic, "Club"),
            new(SmartPlaylistKind.Energetic, "Electronic / Dance"),
            new(SmartPlaylistKind.Energetic, "Techno"),
            new(SmartPlaylistKind.Focus, "House"),
        ],

        // Rapsody: hip-hop across both shelves and across three bands: the loud, the instrumental (Focus)
        // and the bounce (Dancefloor). The last two are absorbed, so they are not lists of their own.
        [new(SmartPlaylistKind.Energetic, "Hip-Hop / Rap")] =
        [
            new(SmartPlaylistKind.Energetic, "Hip-Hop / Rap"),
            new(SmartPlaylistKind.Energetic, "Rap"),
            new(SmartPlaylistKind.Focus, "Rap"),
            new(SmartPlaylistKind.Danceable, "Rap"),
        ],

        // ── Dancefloor ──

        // Four to the Floor: the beat the name is, wherever it is filed.
        [new(SmartPlaylistKind.Danceable, "Electronic / Dance")] =
        [
            new(SmartPlaylistKind.Danceable, "Electronic / Dance"),
            new(SmartPlaylistKind.Danceable, "House"),
            new(SmartPlaylistKind.Danceable, "Club"),
        ],

        // Barn Dance: the dancing folk and the loud folk in one list. It crosses bands, as House Party
        // does. It also takes over the loud folk. See Absorbed, where Stomp and Holler goes.
        [new(SmartPlaylistKind.Danceable, "Folk")] =
        [
            new(SmartPlaylistKind.Danceable, "Folk"),
            new(SmartPlaylistKind.Energetic, "Folk"),
        ],

        // ── Focus ──

        // 9-to-5: music to work through, which is not a genre. It also takes over the indie. See
        // Absorbed, where Headphones On goes.
        [new(SmartPlaylistKind.Focus, "Electronic / Dance")] =
        [
            new(SmartPlaylistKind.Focus, "Electronic / Dance"),
            new(SmartPlaylistKind.Focus, "Pop"),
            new(SmartPlaylistKind.Focus, "Rock"),
            new(SmartPlaylistKind.Focus, "Indie"),
        ],
    };

    /// <summary>
    /// The squares that are no longer lists of their own, and the list that took over each one.
    ///
    /// <para>An absorbed cell is not offered, and it has no title and no picture. Its songs are not lost.
    /// They are in the list named beside it, which draws from this cell.</para>
    ///
    /// <para><b>Stated here, not worked out from <see cref="Sets"/>.</b> Being drawn from and being taken
    /// over are different things. Most drawn cells are neither. For example, Blues keeps its calm row
    /// while Jazz Club draws on it. A rule that absorbed every cell that any set touches would empty half
    /// the grid, and nothing on screen would explain why.</para>
    /// </summary>
    private static readonly Dictionary<MoodCell, MoodCell> Absorbed = new()
    {
        // "Funk Fever" -> Funkadelica.
        [new(SmartPlaylistKind.Energetic, "Funk")] = new(SmartPlaylistKind.Energetic, "Soul"),

        // "Long Player" -> House Party.
        [new(SmartPlaylistKind.Focus, "House")] = new(SmartPlaylistKind.Energetic, "House"),

        // "Instrumentals" and "Bounce" -> Rapsody.
        [new(SmartPlaylistKind.Focus, "Rap")] = new(SmartPlaylistKind.Energetic, "Hip-Hop / Rap"),
        [new(SmartPlaylistKind.Danceable, "Rap")] = new(SmartPlaylistKind.Energetic, "Hip-Hop / Rap"),

        // "Headphones On" -> 9-to-5.
        [new(SmartPlaylistKind.Focus, "Indie")] = new(SmartPlaylistKind.Focus, "Electronic / Dance"),

        // "Guitar Fuzz" -> Main Floor. Its songs do not hold together as one list.
        [new(SmartPlaylistKind.Energetic, "Indie")] = new(SmartPlaylistKind.Energetic, "Electronic / Dance"),

        // "Stomp and Holler" -> Barn Dance.
        [new(SmartPlaylistKind.Energetic, "Folk")] = new(SmartPlaylistKind.Danceable, "Folk"),
    };

    /// <summary>
    /// The squares one narrowed mood list draws from, the lead first.
    ///
    /// <para>A list with no line in the table draws from its own square only. That is the case for most
    /// lists.</para>
    /// </summary>
    public static IReadOnlyList<MoodCell> CellsFor(SmartPlaylistKind kind, string genre)
    {
        ArgumentNullException.ThrowIfNull(genre);
        var lead = new MoodCell(kind, genre);
        return Sets.TryGetValue(lead, out var cells) ? cells : [lead];
    }

    /// <summary>Whether this square has been taken over by another list, so it is not a row of its own.
    /// This is checked wherever the lists on offer are counted. See
    /// <c>LibraryDatabase.OfferedGeneratedLists</c>.</summary>
    public static bool IsAbsorbed(SmartPlaylistKind kind, string genre)
    {
        ArgumentNullException.ThrowIfNull(genre);
        return Absorbed.ContainsKey(new MoodCell(kind, genre));
    }

    /// <summary>Every square that is no longer a list, with the list that took it. This is for the test
    /// that checks this table against the titles. It is not used on screen.</summary>
    public static IEnumerable<KeyValuePair<MoodCell, MoodCell>> EveryAbsorbed => Absorbed;

    /// <summary>Every set in the table, by its lead. For the same test.</summary>
    public static IEnumerable<KeyValuePair<MoodCell, MoodCell[]>> EverySet => Sets;

    /// <summary>
    /// Which song off a shared record this list takes.
    ///
    /// <para><b>Needed because two lists can hold the same records.</b> Jazz Club and Slow Blues both
    /// draw on the calm blues. Both take at most one song from any record
    /// (<see cref="SmartPlaylists.MostFromOneRecord"/>). Without this turn they would take the same song,
    /// and the second list would look like a shorter copy of the first.</para>
    ///
    /// <para>The mechanism that picks songs already exists. <c>MoodPool.Window</c> takes its reading
    /// number, and rotates which song of each record it picks. That is what makes two openings of one
    /// list differ. This adds a fixed turn for each list on top of that reading. So two lists opened at
    /// the same moment are at different places in every record they share. Both still move on when they
    /// are read again.</para>
    ///
    /// <para>The hash is written out here, not taken from <c>string.GetHashCode</c>. That method is
    /// salted per process. The same list would then sit at a different turn on every launch, and the
    /// songs would change for a listener who had changed nothing. This uses FNV-1a over the band and the
    /// shelf. The result is stable over time and costs a dozen adds.</para>
    /// </summary>
    public static int Turn(SmartPlaylistKind kind, string? genre)
    {
        if (genre is null)
            return 0;   // The whole shelf needs no turn: no other list shares its records.

        unchecked
        {
            const uint Offset = 2166136261, Prime = 16777619;
            var hash = Offset;

            hash = (hash ^ (uint)(int)kind) * Prime;
            foreach (var ch in genre)
                hash = (hash ^ ch) * Prime;

            // Small and non-negative. It is added to a reading number that already walks through the
            // records. What matters is that two lists differ. How far apart they are does not matter.
            return (int)(hash % 1024);
        }
    }
}
