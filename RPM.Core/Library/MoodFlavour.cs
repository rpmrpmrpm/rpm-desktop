using RPM.Core.Metadata;

namespace RPM.Core.Library;

/// <summary>What a list wants beyond its mood and its genre: the styles that say a record is the kind of
/// thing the list is named after.
///
/// <para><b>The name promises more than the cut delivers.</b> "Slow Burn" is the quiet end of Rock. The
/// quiet end of Rock is not all soft rock. It is also a ballad, an acoustic take, and whatever else the
/// mood range picked up along the way. "Big Room" is the loud end of Electronic. That covers trance, garage
/// and a film score with a loud minute in it. The mood range and the genre together get a list most of the
/// way to its name, but not all the way.</para>
///
/// <para><b>The rest of the way comes from the catalogues, not from a tagger's typing.</b> Genre is the
/// user's own field, with one value that they set. A user may tidy it to "Rock", which is correct, but
/// matching on a substring of that tag would then break their lists. So the narrowing reads
/// <see cref="CatalogueFacts"/>. That holds every style that Discogs and MusicBrainz filed the release
/// under, mapped onto RPM's own names and kept against the release rather than the folder. It is a
/// controlled vocabulary.</para>
///
/// <para><b>Names are matched whole.</b> A mapped style is one entry from a known list, so "Soft Rock" is
/// matched by its full name and cannot match part of another name. Both sides are folded through
/// <see cref="GenreText.Fold"/>, so a difference in spelling is not a miss.</para>
///
/// <para><b>A preference, never a filter.</b> A rule that required a style would empty most of these lists,
/// because a catalogue has not been asked about every record. So a list takes its flavoured songs when
/// there are enough of them to fill it. Otherwise it takes what it would take without styles. This has the
/// same shape as <see cref="MoodList.InOneEra"/>, for the same reason: a narrowing that can empty a row
/// will do so. A record that no catalogue has described is not narrowed.</para>
///
/// <para><b>Only the pairs that a collection can answer have lines.</b> A pair with no line takes its whole
/// mood range. Shelves that are already one style, such as the whole of Funk, need no line, because
/// preferring that style would narrow nothing.</para>
/// </summary>
public static class MoodFlavour
{
    /// <summary>How many flavoured songs a list needs before it is worth narrowing to them.
    ///
    /// <para>Fifty. This is <see cref="SmartPlaylists.MostSongs"/> for every kind that has a flavour, so
    /// the rule is simply "enough to fill the list". Below that number the narrowed list would be shorter
    /// than the list it replaces. That is a worse answer to "make it fit the name" than the loose list it
    /// started with.</para>
    /// </summary>
    public const int LeastToPrefer = 50;

    /// <summary>The styles each pair prefers, by name, from <see cref="Subgenres.All"/>.
    ///
    /// <para>The styles are the corners of a shelf, never the shelf itself. A list on the Rock shelf that
    /// asked for "Rock" would match everything and narrow nothing, so the rock lists ask for the kinds of
    /// rock. Every name here must be one that the vocabulary holds. A name it does not hold would be a line
    /// that never matches anything, and that silent failure is what this approach exists to prevent.</para>
    /// </summary>
    private static readonly Dictionary<(SmartPlaylistKind Kind, string Genre), string[]> Wanted = new()
    {
        // ── Chill ──
        // Singer-Songwriter is left out, although it is the obvious word and sits under this shelf. It
        // belongs to three shelves at once (Folk, Soul and Rock), so the matcher has no single key for it.
        // A line asking for it would never match, so it is not listed.
        [(SmartPlaylistKind.Calm, "Rock")] =
            ["Soft Rock", "Folk Rock", "Acoustic Rock", "Country Rock", "Roots Rock"],
        [(SmartPlaylistKind.Calm, "Pop")] =
            ["Ballad", "Easy Listening", "Sophisti-Pop", "Adult Contemporary", "Traditional Pop"],
        [(SmartPlaylistKind.Calm, "Hip-Hop / Rap")] =
            ["Conscious Hip Hop", "Alternative Hip Hop"],
        [(SmartPlaylistKind.Calm, "R&B")] =
            ["Quiet Storm", "Alternative R&B"],

        // ── Adrenaline ──
        [(SmartPlaylistKind.Energetic, "Rock")] =
            ["Hard Rock", "Progressive Rock", "Psychedelic Rock", "Art Rock", "Arena Rock", "Garage Rock"],
        [(SmartPlaylistKind.Energetic, "Pop")] =
            ["Dance-Pop", "Electropop", "Europop", "Hyperpop"],
        [(SmartPlaylistKind.Energetic, "Hip-Hop / Rap")] =
            ["Hardcore Hip Hop", "East Coast Hip Hop", "West Coast Hip Hop", "Industrial Hip Hop"],
        [(SmartPlaylistKind.Energetic, "R&B")] =
            ["New Jack Swing", "Contemporary R&B"],
        [(SmartPlaylistKind.Energetic, "Electronic / Dance")] =
            ["Trance", "Eurodance", "Hardstyle", "Industrial", "Drum and Bass", "Breakbeat"],

        // ── Dancefloor ──
        [(SmartPlaylistKind.Danceable, "Rock")] =
            ["New Wave", "Dance-Rock", "Funk Rock", "Glam Rock"],
        [(SmartPlaylistKind.Danceable, "Pop")] =
            ["Dance-Pop", "Europop", "Electropop"],
        [(SmartPlaylistKind.Danceable, "R&B")] =
            ["New Jack Swing", "Contemporary R&B"],
        [(SmartPlaylistKind.Danceable, "Electronic / Dance")] =
            ["Eurodance", "Italo Dance", "Alternative Dance", "UK Garage", "Speed Garage"],

        // There are no lines for the jazz, classical, folk, country, reggae, latin or world pairs. A line
        // with no catalogue data behind it would narrow on a style the collection may not hold.
    };

    /// <summary>The styles one list prefers, or nothing where the pair has none.</summary>
    public static IReadOnlyList<string> Styles(SmartPlaylistKind kind, string? genre) =>
        genre is not null && Wanted.TryGetValue((kind, genre), out var styles) ? styles : [];

    /// <summary>Every style named in the table. A test checks these against the vocabulary, so the
    /// check does not depend on a reader holding both in their head.</summary>
    public static IEnumerable<string> EveryStyleNamed => Wanted.Values.SelectMany(v => v);
}
