using RPM.Fingerprint.Catalog;
using System.Text.RegularExpressions;
using RPM.Core.Analysis;

namespace RPM.Core.Metadata.Providers;

/// <summary>
/// One artist tag, read as the list of acts it names.
///
/// <para>This is shared, not owned by one screen. Both heads of the app ask the same question of the same
/// field, and they must get the same answer. The track pane splits a credit into the row of cards it
/// pages through. The header's photograph falls back to the lead name when the catalogues have nothing
/// filed under the credit as typed. Two splitters would give two different answers about who is playing,
/// shown on screen at the same time.</para>
/// </summary>
public static class ArtistCredit
{
    /// <summary>
    /// The names in one artist credit, using only the separators a tagger normally types.
    ///
    /// <para>These are a comma, a slash, an ampersand, or the word "feat" in any of its spellings. Each of
    /// them can also appear inside an artist name ("Earth, Wind &amp; Fire", "AC/DC"). So this is only used
    /// for a credit that the catalogues did not recognise as one act. Splitting is the second guess, never
    /// the first.</para>
    ///
    /// <para>Brackets count as separators too. "Artist A (feat. Artist B)" is the same credit with the
    /// guest in parentheses. A name with a stray bracket left on it matches nothing.</para>
    /// </summary>
    public static IReadOnlyList<string> Split(string? credit) => Cut(credit, ArtistSplit);

    /// <summary>
    /// The same as <see cref="Split"/>, but the joining words count as separators too: "with", "and", and a
    /// bare "x" for a collaboration.
    ///
    /// <para>This is not the ordinary reading, and it must stay separate. Each of these words appears
    /// inside real names. "Sleeping With Sirens" is a band, and "Bob and Earl" is a duo that should not be
    /// cut in half. A credit is far more often one act with a long name than two acts joined by a word. So
    /// this is asked second, by a caller that already believes the credit names more than one act and can
    /// check the answer against acts it knows (see <c>ArtistWall.Build</c>, the one caller that decides
    /// anything from it).</para>
    /// </summary>
    public static IReadOnlyList<string> SplitWide(string? credit) => Cut(credit, ArtistSplitWide);

    /// <summary>
    /// The same names as <see cref="Split"/>, grouped by the guest markers instead of run together. "Tyler,
    /// The Creator feat. Frank Ocean" is <c>[["Tyler", "The Creator"], ["Frank Ocean"]]</c>.
    ///
    /// <para>This is for a caller that joins split names back together. "feat" says outright that what
    /// follows is somebody else. A comma does not say that, and half the names it sits inside are one act.
    /// So the two cuts are not equally safe to undo. A caller that undoes one must be able to tell them
    /// apart. See <c>ArtistWall.Rejoined</c>, the only caller.</para>
    ///
    /// <para>The names are the same as <see cref="Split"/> gives, in the same order. The cap and the
    /// dropped duplicates are counted across the whole credit, not per group.</para>
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> SplitGuests(string? credit)
    {
        if (string.IsNullOrWhiteSpace(credit))
            return [];

        var groups = new List<IReadOnlyList<string>>(2);
        var all = new List<string>(4);

        foreach (var group in GuestSplit.Split(credit))
        {
            var names = new List<string>(4);
            foreach (var part in NameSplit.Split(group))
            {
                if (Named(part, all) is not { } name)
                    continue;

                all.Add(name);
                names.Add(name);
                if (all.Count == MaxCredited)
                    break;
            }

            if (names.Count > 0)
                groups.Add(names);
            if (all.Count == MaxCredited)
                break;
        }

        return groups;
    }

    private static IReadOnlyList<string> Cut(string? credit, Regex separators)
    {
        if (string.IsNullOrWhiteSpace(credit))
            return [];

        var names = new List<string>(4);
        foreach (var part in separators.Split(credit))
        {
            if (Named(part, names) is not { } name)
                continue;

            names.Add(name);
            if (names.Count == MaxCredited)
                break;
        }

        return names;
    }

    /// <summary>
    /// One piece of a cut credit as a name, or null when it is not a name.
    ///
    /// <para>A single character is not a name. It is what is left when a splitter cuts in the wrong place.
    /// Duplicates are dropped too: "A feat. A" is one artist typed twice. The compilation marker is not a
    /// person. A credit reading "Various Artists, Diana Ross" has one artist in it. A card for the phrase
    /// would not help anyone.</para>
    /// </summary>
    private static string? Named(string part, List<string> already)
    {
        var name = part.Trim().Trim('(', ')', '[', ']', '-', '–').Trim();

        return name.Length < 2
               || IsVariousArtists(name)
               || already.Contains(name, StringComparer.OrdinalIgnoreCase)
            ? null
            : name;
    }

    /// <summary>
    /// The lead name in a credit that names more than one act, or null when there is no second name.
    ///
    /// <para>This is what a picture falls back to, and nothing else. See the note on <see cref="Split"/>
    /// about why this is asked second. A credit with one name gives null, not the name itself, so the
    /// caller cannot ask the same question twice. A credit whose split returns the whole string also gives
    /// null. That is a credit with a separator in it and no real split.</para>
    /// </summary>
    public static string? First(string? credit)
    {
        var names = Split(credit);
        return names.Count > 1 && !NameMatch.Same(names[0], credit) ? names[0] : null;
    }

    /// <summary>
    /// Whether a name is the compilation marker rather than a person.
    ///
    /// <para>All three spellings that taggers write are checked, compared the same way as every other name.
    /// Case, spacing and punctuation make no difference, so "various artists" and "Various Artists." are
    /// the same. The app's own spelling (see <see cref="AlbumResolver.VariousArtists"/>) is one of the
    /// three.</para>
    /// </summary>
    public static bool IsVariousArtists(string? name)
        => name is { Length: > 0 } && VariousNames.Contains(NameMatch.Squash(name));

    /// <summary>The three spellings as they are written, not as they are squashed. This is public because
    /// SQL has to ask the same question and cannot call <see cref="IsVariousArtists"/>. See
    /// <c>LibraryDatabase.NotByARefusedName</c>, which builds its comparison from this list, so the two
    /// readings always use the same spellings.</summary>
    public static readonly IReadOnlyList<string> VariousSpellings =
        ["Various", "Various Artist", AlbumResolver.VariousArtists];

    private static readonly HashSet<string> VariousNames =
        [.. VariousSpellings.Select(NameMatch.Squash)];

    /// <summary>The point where a credit stops being a credit. Eight names is already a long row under a
    /// card in a 320-point pane. A field that names more acts than that is a compilation's track list in
    /// the artist tag, not a record that eight people made.
    ///
    /// <para>This is public because the pane merges the album's credit with the track's credit, and it must
    /// stop at the same place the split does. Two different limits would let a pair of credits go past the
    /// limit the row was measured for.</para></summary>
    public const int MaxCredited = 8;

    /// <summary>
    /// The separators: a comma, a slash, an ampersand, and a semicolon. Most taggers use a semicolon where
    /// ID3v2.4 uses a null character to separate a list.
    ///
    /// <para>"feat" and its spellings need a bracket or a space in front of them, never nothing. With
    /// nothing required, the pattern would cut "Defeat Ed" in half at the "feat". The space is all that
    /// tells the word from the syllable. The bracket is there because "Artist A (feat. Artist B)" is the
    /// same credit with the guest in parentheses. That form is at least as common as the plain one.</para>
    /// </summary>
    private static readonly Regex ArtistSplit = new(
        @"\s*[,;/&]\s*|[\s(\[]+(?:feat\.?|featuring|ft\.?)\s+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The two halves of <see cref="ArtistSplit"/>, for <see cref="SplitGuests"/>. This holds only
    /// the guest markers, and <see cref="NameSplit"/> holds the punctuation. Together they match the same
    /// pattern as <see cref="ArtistSplit"/>, so the grouped and flat readings give the same
    /// names.</summary>
    private static readonly Regex GuestSplit = new(
        @"[\s(\[]+(?:feat\.?|featuring|ft\.?)\s+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex NameSplit = new(
        @"\s*[,;/&]\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The separators above, plus the words a collaboration is joined with. Each word must have a
    /// space on both sides. That is what keeps "x" out of "Malcolm X" and "and" out of "Land". See
    /// <see cref="SplitWide"/> for why these are not in the ordinary list.</summary>
    private static readonly Regex ArtistSplitWide = new(
        @"\s*[,;/&]\s*|[\s(\[]+(?:feat\.?|featuring|ft\.?)\s+|\s+(?:with|and|x)\s+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
