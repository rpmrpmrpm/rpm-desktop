using RPM.Core.Models;

namespace RPM.Core.Library;

/// <summary>A tag a queued file can be asked to carry before it counts as ready.</summary>
public enum QueueTag
{
    Artist,
    AlbumArtist,
    Album,
    Title,
    Track,
    Year,
    Genre,
    Subgenre,
}

/// <summary>
/// What a queued file has to say about itself before the bench stops asking.
///
/// <para>No single set of tags is right for everyone. One collection cannot be used without genres, and
/// another has never used them. Someone who files bootlegs has no catalogue year to give. So the set
/// belongs to the user. It is held here as one list. The mark on each row, the count on the chip, and
/// the filter all read this list, so they always agree.</para>
///
/// <para>The set is static because it is a preference of the person using the app, not a fact about a
/// library. The same list applies to whichever library is open. The shell sets it once at startup, and
/// again whenever the settings change.</para>
/// </summary>
public static class QueueTags
{
    /// <summary>Every tag that may be asked for, in the order a picker offers them.</summary>
    public static readonly IReadOnlyList<QueueTag> All =
        [QueueTag.Artist, QueueTag.AlbumArtist, QueueTag.Album, QueueTag.Title,
         QueueTag.Track, QueueTag.Year, QueueTag.Genre, QueueTag.Subgenre];

    /// <summary>
    /// What is asked for when nobody has said otherwise: everything except the subgenre.
    ///
    /// <para><b>Not the subgenre.</b> The other tags are what a file must have before it is filed at all,
    /// and a library is expected to carry them. A subgenre is narrower than most collections are tagged.
    /// Many records are rock records and nothing more. Asking for a subgenre by default would mark half of
    /// a normal shelf as incomplete, which would teach people to ignore the mark. Anyone who wants it can
    /// add it with one tick.</para>
    /// </summary>
    public static readonly IReadOnlyList<QueueTag> Default =
        [.. All.Where(tag => tag != QueueTag.Subgenre)];

    private static IReadOnlyList<QueueTag> _required = Default;

    /// <summary>The same list as a bitmask. Every row asks this question several times while the grid
    /// scrolls. A bitmask lets the check run without allocating a list, an enumerator, or a copy of the
    /// row.</summary>
    private static int _mask = MaskOf(Default);

    /// <summary>The tags a file must carry to count as ready. This is never empty. An empty set would mark
    /// every row complete, and the bench would stop asking for anything.</summary>
    public static IReadOnlyList<QueueTag> Required
    {
        get => _required;
        set
        {
            _required = value is { Count: > 0 } wanted ? [.. All.Where(wanted.Contains)] : Default;
            _mask = MaskOf(_required);
        }
    }

    /// <summary>Whether a file has every tag that is currently required. It takes the fields directly,
    /// not a track. The caller is a grid row, and building a track just to ask would cost more than the
    /// check.</summary>
    public static bool Satisfied(
        string? artist, string? albumArtist, string? album, string? title,
        uint track, uint year, string? genre, string? subgenre)
    {
        if (Asked(QueueTag.Artist) && !Said(artist)) return false;
        if (Asked(QueueTag.AlbumArtist) && !Said(albumArtist)) return false;
        if (Asked(QueueTag.Album) && !Said(album)) return false;
        if (Asked(QueueTag.Title) && !Said(title)) return false;
        if (Asked(QueueTag.Track) && track == 0) return false;
        if (Asked(QueueTag.Year) && year == 0) return false;
        if (Asked(QueueTag.Genre) && !Said(genre)) return false;
        if (Asked(QueueTag.Subgenre) && !Said(subgenre)) return false;
        return true;
    }

    /// <summary>The required tags that are missing, in <see cref="All"/> order. This is for the tooltip,
    /// and is only asked when the user points at a row.</summary>
    public static IReadOnlyList<QueueTag> MissingFrom(
        string? artist, string? albumArtist, string? album, string? title,
        uint track, uint year, string? genre, string? subgenre)
    {
        var missing = new List<QueueTag>(All.Count);
        void Need(QueueTag tag, bool held)
        {
            if (Asked(tag) && !held)
                missing.Add(tag);
        }

        Need(QueueTag.Artist, Said(artist));
        Need(QueueTag.AlbumArtist, Said(albumArtist));
        Need(QueueTag.Album, Said(album));
        Need(QueueTag.Title, Said(title));
        Need(QueueTag.Track, track > 0);
        Need(QueueTag.Year, year > 0);
        Need(QueueTag.Genre, Said(genre));
        Need(QueueTag.Subgenre, Said(subgenre));
        return missing;
    }

    /// <summary>The same check for a whole track. Use this when the caller already holds one.</summary>
    public static bool Satisfied(TrackInfo wanted) => Satisfied(
        wanted.Artist, wanted.AlbumArtist, wanted.Album, wanted.Title,
        wanted.TrackNumber, wanted.Year, wanted.Genre, wanted.Subgenre);

    /// <summary>The same check as SQL, over the wanted columns of the queue table. It is true for a row
    /// that is missing a required tag. The SQL is built from <see cref="Required"/> each time it is
    /// asked, so a change in the settings shows on the next count, not only after a restart.</summary>
    public static string MissingSql()
    {
        // Every tag has its own arm, and there is no default arm. A default arm would answer for a new
        // tag without the compiler noticing. For example, a new tag could be counted as a missing genre.
        // A separate arm for each tag makes the compiler ask for the clause.
        var parts = Required.Select(tag => tag switch
        {
            QueueTag.Artist => "w_artist IS NULL OR trim(w_artist) = ''",
            QueueTag.AlbumArtist => "w_album_artist IS NULL OR trim(w_album_artist) = ''",
            QueueTag.Album => "w_album IS NULL OR trim(w_album) = ''",
            QueueTag.Title => "w_title IS NULL OR trim(w_title) = ''",
            QueueTag.Track => "w_track = 0",
            QueueTag.Year => "w_year = 0",
            QueueTag.Genre => "w_genre IS NULL OR trim(w_genre) = ''",
            QueueTag.Subgenre => "w_subgenre IS NULL OR trim(w_subgenre) = ''",
            _ => throw new ArgumentOutOfRangeException(nameof(tag), tag, "no clause for this tag"),
        });

        return "(" + string.Join(" OR ", parts) + ")";
    }

    private static bool Asked(QueueTag tag) => (_mask & (1 << (int)tag)) != 0;

    private static int MaskOf(IEnumerable<QueueTag> tags)
    {
        var mask = 0;
        foreach (var tag in tags)
            mask |= 1 << (int)tag;
        return mask;
    }

    private static bool Said(string? value) => !string.IsNullOrWhiteSpace(value);
}
