namespace RPM.Core.Metadata;

/// <summary>
/// One record in the library, reduced to the facts that a similarity check needs.
///
/// <para>This is a type and not a tuple because it is the only way into the ranking. Each axis that <see
/// cref="SimilarAlbums"/> scores reads its facts from here. So an axis that needs a fact the library does
/// not hold cannot be written until that fact is added to this type.</para>
/// </summary>
/// <param name="Folder">The library folder. It is both the identity of the record and the tie-break. Never
/// null: each card in the row is a record on the shelf.</param>
/// <param name="Genres">The genres the record is filed under, already mapped to the app's own closed list
/// (see <see cref="Genres.Match"/>). Empty is normal, because many files have no genre tag. It means the
/// genre axis is not used for this record. It does not mean the record scores zero on it.
/// </param>
/// <param name="Year">The year, or 0 when the record does not say. Zero is never treated as a date. An
/// untagged record is not from year zero. Its era is unknown.</param>
public sealed record AlbumTraits(
    string Folder,
    string Album,
    string Artist,
    IReadOnlyList<string> Genres,
    int Year);

/// <summary>One card in the "similar albums" row: a record on this shelf, and why it is there.</summary>
/// <param name="Why">The axis that put it in the row. The caller can show this or test it. It is not a
/// score. A reader does not want a number, and a number cannot be checked by looking at the row.
/// </param>
public sealed record SimilarAlbum(
    string Title,
    string Artist,
    string Folder,
    SimilarBecause Why);

/// <summary>Why a record is suggested. The values are in order of how much they explain. The first one that
/// applies is the label on the card.</summary>
public enum SimilarBecause
{
    /// <summary>Another act with similar music. Their records are filed under the same genres.</summary>
    LikeTheArtist,

    /// <summary>This record and that one share a genre, whatever their makers do otherwise.</summary>
    SameGenre,

    /// <summary>They share only the era. This is the weakest reason, so it comes last.</summary>
    SameEra,

    /// <summary>No genres on either side and no years. The row is filled in a fixed order (see <see
    /// cref="SimilarAlbums.Pick"/>), so that it is full rather than short.</summary>
    JustOnTheShelf,
}

/// <summary>
/// What to put in the row under an album: other records on this shelf that are worth playing next.
///
/// <para><b>Only the library.</b> Every card is a record the listener owns, so each card opens an album. A
/// suggestion the listener cannot play is a suggestion about someone else's shelf.</para>
///
/// <para><b>Somewhere else, not more of the same.</b> The act whose record is open is left out of its own
/// row. No act appears twice while another act is still waiting to be shown. The row has only a few chances
/// to send the listener somewhere new, so spending them on the same artist's other records is a waste.
/// Those records are one press away on that artist's page. What is left is other acts, nearest music
/// first.</para>
///
/// <para><b>What "similar" is asked of.</b> Three axes, weighted and added. Each one is asked only where
/// both records can answer it (see <see cref="Score"/>):</para>
/// <list type="bullet">
/// <item>the two records' own genres. This is the sharpest information the shelf has, so it has the most
/// weight;</item>
/// <item>the two acts' genres: every record each act made, pooled. This is the app's answer to "similar
/// artists". It is also why a record with no genre tag of its own is still placed;</item>
/// <item>the era, measured as the distance between the two years rather than by decade. 1979 and 1980 are
/// the same music. "The seventies" and "the eighties" are not.</item>
/// </list>
///
/// <para><b>Mood is missing, so there is no mood axis.</b> The library has no mood data, in the tags or in
/// the mirror. The audio analysis holds tempo, key and intensity. The app could compute a mood from these,
/// but only for records that have been through the analyser. The services that once supplied mood data no
/// longer offer it: AcousticBrainz no longer collects data, and Spotify's audio features are closed to new
/// applications. An absent axis is not the same as an axis that scores zero. <see cref="Score"/> divides by
/// the axes it could ask. A mood axis added later is one more term and one more weight, and none of the
/// numbers below have to change.</para>
///
/// <para><b>The order is fixed.</b> Ties break on the folder, so the same album gives the same row each
/// time. If the library has no genres and no years at all, the result is marked <see
/// cref="SimilarBecause.JustOnTheShelf"/>. It is not presented as a real reason. The order must not depend
/// on the order of a dictionary, which is not fixed by the code.</para>
/// </summary>
public static class SimilarAlbums
{
    /// <summary>How many suggestions a row holds. Nine fills the full width of the two panels above the
    /// row. Six cards at a readable size would leave a third of that width empty.</summary>
    public const int DefaultCount = 9;

    /// <summary>
    /// How many records one act gets while any other act is still waiting: one. This is the diversity rule.
    /// Nine cards are filled by nine acts, each represented by whichever record fits best beside this one.
    ///
    /// <para>This is a preference, not a law. A shelf with only four other acts cannot show nine of them,
    /// and a short row helps nobody. So <see cref="Pick"/> makes a second pass and takes more records from
    /// the same acts when it has to. The rule is "spread out as far as the shelf allows", not "never
    /// twice".</para>
    /// </summary>
    public const int PreferredPerAct = 1;

    /// <summary>How far apart two years can be and still count as the same era at all. At 25 years the axis
    /// scores zero. Inside that span the score falls in a straight line. The span is a distance and not a
    /// decade, because a decade has hard edges. 1979 and 1980 are the same music, and a rule that puts them
    /// in different eras measures nothing useful.</summary>
    public const int EraSpan = 25;

    // Weights are ratios, not absolute sizes. Score divides by the weight of the axes it asked, so what
    // matters is how much each axis counts compared with the others.

    /// <summary>The pooled genres of the two acts. This is the app's "similar artists" measure. It has less
    /// weight than the record genres because it is blunter: every record an act made goes into one
    /// set.</summary>
    private const double ArtistGenreWeight = 1.5;

    /// <summary>The genres of the two records. This is the sharpest information the library has, so it has
    /// the most weight of the three axes.</summary>
    private const double AlbumGenreWeight = 2.0;

    /// <summary>The era. This counts least. Two records twenty years apart in the same genre are more alike
    /// than two records from the same year in different genres.</summary>
    private const double EraWeight = 1.0;

    /// <summary>
    /// The row for <paramref name="subject"/>, best first.
    /// </summary>
    /// <param name="subject">The record being looked at. It never appears in its own row.</param>
    /// <param name="library">Every album on the shelf. The subject may or may not be in this list. The
    /// subject is removed from the result either way, so a caller cannot forget to remove it. The subject's
    /// own genres are added to its act whichever case applies. The act profiles are built from this whole
    /// list, so the list decides what "similar artist" can mean. If a search's results are passed in, the
    /// answer is about that search.</param>
    /// <param name="count">How many cards. This is a stated number and not a default that changes by call
    /// site.</param>
    public static IReadOnlyList<SimilarAlbum> Pick(
        AlbumTraits subject,
        IReadOnlyList<AlbumTraits> library,
        int count)
    {
        if (count <= 0)
            return [];

        var subjectKey = Fold(subject.Artist);
        var byArtist = PoolGenresByArtist(library);

        // The subject's own genres are added to its act's pool here. They are not expected to come in with
        // the shelf. Today the caller passes the whole collection, which includes the subject, and this
        // method filters the subject out again. But then the "similar artists" axis would work only for
        // callers that include the subject. For any other caller it would do nothing, with no error. An
        // axis that is switched off by an argument the method does not document is worse than having no
        // axis.
        if (subject.Genres.Count > 0)
        {
            if (!byArtist.TryGetValue(subjectKey, out var own))
                byArtist[subjectKey] = own = new HashSet<string>(StringComparer.Ordinal);

            foreach (var genre in subject.Genres)
                own.Add(genre);
        }

        var subjectArtistGenres = Genres(byArtist, subjectKey);

        var seen = new HashSet<string>(StringComparer.Ordinal) { Key(subject.Artist, subject.Album) };
        var scored = new List<(AlbumTraits Album, double Score, SimilarBecause Why)>();

        foreach (var other in library)
        {
            if (string.IsNullOrWhiteSpace(other.Album)) continue;

            // Skip this whole act, not only its best or nearest record. The listener wants a row that goes
            // somewhere else. The rest of this artist's shelf is one press away on their page.
            if (Fold(other.Artist) == subjectKey) continue;

            // Skip a record that is already in the list. The key is the folded artist and album, not the
            // folder. A listener with the same album in two folders has one record.
            if (!seen.Add(Key(other.Artist, other.Album))) continue;

            var otherArtistGenres = Genres(byArtist, Fold(other.Artist));

            scored.Add((
                other,
                Score(subject, other, subjectArtistGenres, otherArtistGenres),
                Because(subject, other, subjectArtistGenres, otherArtistGenres)));
        }

        // Best first. The folder breaks ties, so the same record gives the same row each time. A
        // dictionary's enumeration order does not guarantee that.
        scored.Sort((a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            return byScore != 0
                ? byScore
                : string.CompareOrdinal(a.Album.Folder, b.Album.Folder);
        });

        // Round one takes the best record of each act, in the order the acts come out of the sort. Round
        // two fills any places that a thin shelf left empty, best first, without the act limit. Two rounds
        // are used, not one counter. A counter lets an act take a second card as soon as its first card is
        // placed. Then an act with three near-identical records would take three cards before the fourth
        // act got one, which is what the spread is meant to stop.
        //
        // Taken records are tracked by folder and not by act. If acts were tracked, round two would take
        // again every card that round one picked, because the count of their act is one by then, which is
        // exactly the state that round two looks for.
        var picked = new List<SimilarAlbum>(count);
        var acts = new Dictionary<string, int>(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var round = 0; round < 2 && picked.Count < count; round++)
        {
            foreach (var (album, _, why) in scored)
            {
                if (picked.Count >= count) break;
                if (taken.Contains(album.Folder)) continue;

                var act = Fold(album.Artist);
                acts.TryGetValue(act, out var already);
                if (round == 0 && already >= PreferredPerAct) continue;

                acts[act] = already + 1;
                taken.Add(album.Folder);
                picked.Add(new SimilarAlbum(album.Album, album.Artist, album.Folder, why));
            }
        }

        return picked;
    }

    /// <summary>
    /// How alike two records are, from zero to one.
    ///
    /// <para>Each axis is asked only where BOTH records can answer it. The total is divided by the weight
    /// of the axes that were asked. This means an untagged record is not punished for missing tags. A
    /// record with no year is not from a far-away era. Its era is simply unknown. Scoring it as zero on
    /// that axis would sink every untagged record to the bottom of every row. That would rank the shelf by
    /// how well it was tagged, which nobody asked for.</para>
    ///
    /// <para>This is also where a mood axis would be added: one more term and one more weight. The numbers
    /// already here keep their meaning.</para>
    /// </summary>
    private static double Score(
        AlbumTraits subject,
        AlbumTraits other,
        IReadOnlyCollection<string> subjectArtistGenres,
        IReadOnlyCollection<string> otherArtistGenres)
    {
        double total = 0, asked = 0;

        void Ask(double weight, double value)
        {
            total += weight * value;
            asked += weight;
        }

        if (subjectArtistGenres.Count > 0 && otherArtistGenres.Count > 0)
            Ask(ArtistGenreWeight, Overlap(subjectArtistGenres, otherArtistGenres));

        if (subject.Genres.Count > 0 && other.Genres.Count > 0)
            Ask(AlbumGenreWeight, Overlap(subject.Genres, other.Genres));

        if (subject.Year > 0 && other.Year > 0)
            Ask(EraWeight, Era(subject.Year, other.Year));

        // There is no mood term. See the class summary for why. A term that scores zero would say that two
        // records feel nothing alike, and the app has no basis for that claim.

        return asked > 0 ? total / asked : 0;
    }

    /// <summary>The axis that explains why a card is in the row: the first one that applies, strongest
    /// first. This is separate from <see cref="Score"/>. A reason is not a threshold on the total. A record
    /// can score well by adding up three weak axes, and there is no honest sentence for that. "Same genre"
    /// is a sentence a reader can check by looking.</summary>
    private static SimilarBecause Because(
        AlbumTraits subject,
        AlbumTraits other,
        IReadOnlyCollection<string> subjectArtistGenres,
        IReadOnlyCollection<string> otherArtistGenres)
    {
        if (subject.Genres.Count > 0 && other.Genres.Count > 0 && Overlap(subject.Genres, other.Genres) > 0)
            return SimilarBecause.SameGenre;

        if (subjectArtistGenres.Count > 0 && otherArtistGenres.Count > 0
            && Overlap(subjectArtistGenres, otherArtistGenres) > 0)
            return SimilarBecause.LikeTheArtist;

        if (subject.Year > 0 && other.Year > 0 && Era(subject.Year, other.Year) > 0)
            return SimilarBecause.SameEra;

        return SimilarBecause.JustOnTheShelf;
    }

    /// <summary>How much two sets of genres are the same: the shared count divided by the count of all
    /// genres in either set. Sharing one of two labels counts for more than sharing one of nine. So "both
    /// folk records" scores higher than "both filed under six things, one of which is folk".</summary>
    private static double Overlap(IReadOnlyCollection<string> a, IReadOnlyCollection<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
            return 0;

        var union = new HashSet<string>(a, StringComparer.Ordinal);
        var shared = 0;
        foreach (var genre in b)
            if (!union.Add(genre))
                shared++;

        return union.Count == 0 ? 0 : (double)shared / union.Count;
    }

    /// <summary>How close two years are, from zero to one. The score falls in a straight line to zero at
    /// <see cref="EraSpan"/>. Both years are known when this is called. See <see cref="Score"/>, which
    /// checks this first.</summary>
    private static double Era(int a, int b)
        => Math.Max(0, 1 - Math.Abs(a - b) / (double)EraSpan);

    /// <summary>Every genre each act is filed under, pooled across its whole catalogue. This is the
    /// "similar artists" axis. Two acts are alike when their music is alike. The only evidence the library
    /// has for that is what its own files say.
    ///
    /// <para>The pool is built per act and not per record, on purpose. A tagged record passes its genres to
    /// the same artist's untagged records. So an album with no tags is still placed by the artist's other
    /// work.</para></summary>
    private static Dictionary<string, HashSet<string>> PoolGenresByArtist(IReadOnlyList<AlbumTraits> library)
    {
        var pooled = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var album in library)
        {
            if (album.Genres.Count == 0) continue;

            var key = Fold(album.Artist);
            if (!pooled.TryGetValue(key, out var genres))
                pooled[key] = genres = new HashSet<string>(StringComparer.Ordinal);

            foreach (var genre in album.Genres)
                genres.Add(genre);
        }

        return pooled;
    }

    private static IReadOnlyCollection<string> Genres(
        Dictionary<string, HashSet<string>> pooled, string artistKey)
        => pooled.TryGetValue(artistKey, out var genres) ? genres : [];

    private static string Key(string? artist, string? album) => $"{Fold(artist)}|{Fold(album)}";

    /// <summary>Folds text for comparison. Case, accents, punctuation and a leading "the" do not make two
    /// records different. The shared album entries are matched on the same fold.
    ///
    /// <para>This is internal and not private because <see cref="Library.HomeShelf.FromTaste"/> asks the
    /// same question of a played credit and an album's artist tag. Two different folds would give two
    /// answers to whether a listener plays this act.</para></summary>
    internal static string Fold(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var decomposed = text.Trim().Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(decomposed.Length);
        var pendingSpace = false;

        foreach (var ch in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch)
                == System.Globalization.UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                pendingSpace = false;
                sb.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                pendingSpace = true;
            }
        }

        var folded = sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
        return folded.StartsWith("the ", StringComparison.Ordinal) ? folded[4..] : folded;
    }
}
