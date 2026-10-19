using RPM.Core.Metadata;

namespace RPM.Core.Library;

/// <summary>Which row of the home view a record is standing in, and therefore what the row's own heading
/// and ordering mean. The rows answer different questions from the same shelf. A row that did not say which
/// question it answered would be four rows of records in four orders.</summary>
public enum HomeRow
{
    /// <summary>What has been played lately, most recent first. It is taken from the album's newest played
    /// track.</summary>
    Listened,

    /// <summary>What to play next, worked out from the records above. See
    /// <see cref="HomeShelf.Suggested"/>. It is the only row here that is not a sort.</summary>
    Suggested,

    /// <summary>Records by the acts and kinds of music this listener plays MOST. The ranking uses all of
    /// their listening, not just the last few records. See <see cref="HomeShelf.FromTaste"/>. It is the
    /// other row that is not a sort. The two answer different questions. <see cref="Suggested"/> answers
    /// "like what you just played". This row answers "like what you play".</summary>
    Taste,

    /// <summary>What has been played most, all time, counted in tracks played rather than in
    /// records.</summary>
    Played,

    /// <summary>What arrived last.</summary>
    Added,
}

/// <summary>The home view's arithmetic: which records stand in which of its rows, using the shelf and
/// nothing else.
///
/// <para><b>It lives here, not in the view,</b> for the same reason as the album page's suggestions. A row
/// of records is a question asked of a collection, and a question asked of a collection can be tested with
/// fixed inputs. The view draws what this returns and decides nothing.</para>
///
/// <para><b>Nothing in it is a preference.</b> Every row comes from facts the library already keeps about
/// what has been played: the album's newest play, the album's plays added up, and when each album was
/// added. So a listener who has played nothing sees the rows that do not need a history, and none of the
/// rows that do. A home view with empty rows for a new collection would be the app describing what it would
/// have said if the user had used it.</para>
/// </summary>
public static class HomeShelf
{
    /// <summary>How many records a row holds. The row scrolls sideways, so this is not what fits on screen.
    /// It is how far down each order is worth going before the answer stops being useful. The twentieth
    /// most played record is still one of the listener's. The two hundredth is just the collection in a
    /// different order.</summary>
    public const int RowLength = 20;

    /// <summary>The row the taste is read from: what has been played, or, where nothing has, what arrived
    /// last.
    ///
    /// <para><b>A borrowed shelf is the main reason for this.</b> A collection lent from another computer
    /// carries its records and no listening at all. The listing shows what somebody HAS, never what they
    /// have played. Seeded from an empty row, the suggestions are empty too. The screen then shows one row
    /// of recent arrivals over a shelf of thousands of records. The newest records are a worse answer than
    /// a week of listening. They are a far better answer than nothing. They are also the only fact about a
    /// borrowed shelf that is true of the LENDER and not of the borrower. The same holds on a first evening
    /// with one's own collection.</para>
    ///
    /// <para>The seeds are also what the suggestions must not repeat. Both rows are on one screen, one
    /// under the other. So the answer is a row rather than a flag, and the caller excludes whatever it
    /// seeded with.</para>
    /// </summary>
    public static IReadOnlyList<string> Basis(
        IReadOnlyList<string> listened, IReadOnlyList<string> added) =>
        listened.Count > 0 ? listened : added;

    /// <summary>How many of the recently played records the suggestions are worked out from.
    ///
    /// <para>Not one, which is what the album page's row uses, and not all of them. One record is a mood
    /// rather than a taste. A single evening of one genre would fill the row with that genre. All of them
    /// is no taste at all. A collection played over years averages out to the collection itself, and the
    /// row becomes a shuffle.</para>
    /// </summary>
    public const int TasteSeeds = 5;

    /// <summary>Records with a play behind them, newest listen first. Ties are broken by folder, so the row
    /// is the same on two reads of an unchanged shelf. An order that reshuffles for a reader who has done
    /// nothing looks like a bug wherever they notice it.</summary>
    public static IReadOnlyList<string> Listened(IReadOnlyList<LibraryDatabase.AlbumFactsRow> shelf, int count) =>
        [.. shelf.Where(a => a.LastPlayedAt > 0)
                 .OrderByDescending(a => a.LastPlayedAt)
                 .ThenBy(a => a.Folder, StringComparer.OrdinalIgnoreCase)
                 .Take(Math.Max(0, count))
                 .Select(a => a.Folder)];

    /// <summary>Records with plays behind them, most played first. Plays are counted as TRACKS played, not
    /// as times through the album. So a record played once from start to end ranks above a single song
    /// played twice. That is what "most listened" means for a shelf of albums.</summary>
    public static IReadOnlyList<string> Played(IReadOnlyList<LibraryDatabase.AlbumFactsRow> shelf, int count) =>
        [.. shelf.Where(a => a.Plays > 0)
                 .OrderByDescending(a => a.Plays)
                 .ThenByDescending(a => a.LastPlayedAt)
                 .ThenBy(a => a.Folder, StringComparer.OrdinalIgnoreCase)
                 .Take(Math.Max(0, count))
                 .Select(a => a.Folder)];

    /// <summary>How far down a ranked row the banner may reach for the card it holds up.
    /// </summary>
    /// <remarks>Eight. It is a claim about what the row's heading still means. "Most played" is true of
    /// the third most played record on a shelf. It is not true of the hundredth. Eight is enough that a
    /// listener with a handful of favourite records sees a different one each time. It is few enough that
    /// each one is plainly one of theirs.
    /// </remarks>
    public const int BannerReach = 8;

    /// <summary>The front of a ranked row is put into an order that the draw decides. The rest of the row
    /// is left alone.
    /// </summary>
    /// <remarks><b>For the banner, never for the row.</b> A row headed "Most played" is a ranking. It must
    /// keep its own order, or the heading stops being true of it. The banner is not a ranking. It holds up
    /// ONE record and says which question put it there. So it may show any of the records that the question
    /// plainly answers. If the banner used the row's own order, it would show the single most played record
    /// for as long as that record stayed ahead. On a collection that has been owned for years, that means
    /// for ever.
    ///
    /// <para><b>The same draw rule as the suggestions row</b> (see <see cref="Suggested"/>). The answer
    /// changes each time the app is opened, and it stays the same for the whole session. A banner that
    /// rearranged itself while somebody was looking at it would be worse than one that never changed. The
    /// app passes in <c>App.Launch</c>.</para>
    ///
    /// <para>The tail is kept, in order, rather than dropped. The banner steps past any record that another
    /// card has already taken, so it needs somewhere to step to.</para>
    /// </remarks>
    /// <param name="ranked">The row, best first.</param>
    /// <param name="draw">Which draw this is. The same number gives the same order.</param>
    /// <param name="salt">What makes this row's shuffle its own. Two rows that draw on one launch must not
    /// walk the same shuffle. The same reason applies to the seeds in <see cref="Suggested"/>.</param>
    /// <param name="reach">How far into the row the shuffle goes. This is <see cref="BannerReach"/> for the
    /// banner. It has no default. How far down a heading stays true is a fact about the heading. A caller
    /// that left it out would shuffle the whole row.</param>
    public static IReadOnlyList<string> Varied(
        IReadOnlyList<string> ranked, int draw, string salt, int reach)
    {
        ArgumentNullException.ThrowIfNull(ranked);

        if (reach <= 1 || ranked.Count <= 1)
            return ranked;

        var front = Math.Min(reach, ranked.Count);
        return [.. Shuffled([.. ranked.Take(front)], draw, salt), .. ranked.Skip(front)];
    }

    /// <summary>Records newest on the shelf first, played or not. This is the one row that a collection
    /// opened for the first time can fill.</summary>
    public static IReadOnlyList<string> Added(IReadOnlyList<LibraryDatabase.AlbumFactsRow> shelf, int count) =>
        [.. shelf.OrderByDescending(a => a.AddedAt)
                 .ThenBy(a => a.Folder, StringComparer.OrdinalIgnoreCase)
                 .Take(Math.Max(0, count))
                 .Select(a => a.Folder)];

    /// <summary>What to play next, based on what has been played lately.
    ///
    /// <para>This uses the same ranking as the album page's row (<see cref="SimilarAlbums.Pick"/>). It is
    /// asked once for each recently played record, and the results are merged. So a listener who has played
    /// two things this week gets a row that answers to both. The ranking is asked per seed and not from an
    /// average of the seeds. There is no such thing as an average record. The mean of a jazz record and a
    /// grime record is neither, and a ranking given that average would return whatever sits between
    /// them.</para>
    ///
    /// <para>The results are taken round-robin, not seed by seed. So the front of the row has one
    /// suggestion from each of the last few records, rather than all of the first seed's answers. A reader
    /// looks at the front of a row, so the front should cover the width of their week.</para>
    ///
    /// <para>Nothing already in the row it is suggested FROM is repeated. A suggestion is a record the
    /// listener has not just played. The recently played row is directly above this row on the same
    /// screen.</para>
    /// </summary>
    /// <param name="seeds">The recently played records, newest first. This is the answer from
    /// <see cref="Listened"/>, cut to <see cref="TasteSeeds"/>. An empty list gives an empty row. That is
    /// the honest answer for a listener who has played nothing, because there is no taste to read.</param>
    /// <param name="traits">Every record on the shelf as the ranking asks about it.</param>
    /// <param name="exclude">Folders the row must not repeat. These are the ones already on screen above
    /// it.</param>
    /// <param name="draw">Which draw out of the pool this is. The same number gives the same row, so a
    /// screen re-read while somebody is looking at it does not rearrange under them. A different number
    /// gives a different row. The app passes in the launch. See <see cref="Pool"/>.</param>
    public static IReadOnlyList<string> Suggested(
        IReadOnlyList<AlbumTraits> seeds,
        IReadOnlyList<AlbumTraits> traits,
        IReadOnlyCollection<string> exclude,
        int count,
        int draw)
    {
        if (count <= 0 || seeds.Count == 0)
            return [];

        var taken = new HashSet<string>(exclude, StringComparer.OrdinalIgnoreCase);
        var answers = seeds
            .Select(seed => Drawn(SimilarAlbums.Pick(seed, traits, count * Pool), draw, seed.Folder))
            .ToList();

        var row = new List<string>(count);
        for (var round = 0; row.Count < count; round++)
        {
            var addedThisRound = false;
            foreach (var answer in answers)
            {
                if (round >= answer.Count)
                    continue;

                addedThisRound = true;
                if (taken.Add(answer[round]))
                {
                    row.Add(answer[round]);
                    if (row.Count == count)
                        return row;
                }
            }

            // Every seed is out of suggestions. Without this check the loop never ends on a shelf too small
            // to fill the row. Every new collection is that small.
            if (!addedThisRound)
                break;
        }

        return row;
    }

    /// <summary>How many of the played acts the taste row leans on. This is enough that a listener with a
    /// few favourites and a long tail is described by more than their top three. It is short enough that
    /// the twentieth act they have ever played does not weigh the same as the first.</summary>
    public const int LeaningArtists = 12;

    /// <summary>How many kinds of music the taste row leans on, for the same reason. This is fewer than the
    /// acts, because there are fewer genres to begin with. A listener plays hundreds of acts and a handful
    /// of genres. The sixth genre down is already a minority of their listening.</summary>
    public const int LeaningGenres = 6;

    /// <summary>How much an act the listener plays is worth, compared with a genre they play. The value is
    /// higher on purpose. "You play this band" is a fact about the listener. "You play jazz" is a fact
    /// about a thousand records. This is the same ordering the album ranking uses between an artist's
    /// genres and an album's.</summary>
    public const double ActLean = 1.6;

    /// <summary>The share of its score that a record the listener has ALREADY PLAYED keeps. It is not zero.
    /// A listener with a small collection, or one who has been through most of it, would otherwise get an
    /// empty row. A record played once a year ago is a fair thing to offer again. The share is low enough
    /// that everything unheard comes first while there is anything unheard left.</summary>
    public const double PlayedShare = 0.3;

    /// <summary>Records by the acts and kinds of music the listener plays most.
    /// </summary>
    /// <remarks>The other recommendations row is <see cref="Suggested"/>. The difference is the question,
    /// not the arithmetic. That row asks what is LIKE the last few records played, so it follows a listener
    /// from one evening to the next. This row asks what they are into, using their whole listening history,
    /// so it barely moves. A listener who spent a week on one record gets a row of that record's neighbours
    /// from the first, and their own shelf from this one.
    ///
    /// <para><b>Unheard records first.</b> A row of what somebody already plays is the most-played row,
    /// which is two rows down. This row is for records they own and have not played. So a record with plays
    /// behind it keeps <see cref="PlayedShare"/> of its score rather than being removed. The row has to
    /// fill on a small collection too. A record played once a year ago is a fair offer.</para>
    ///
    /// <para><b>Drawn from a pool, like the other row.</b> It uses the same machinery for the same reason.
    /// The scoring is arithmetic over facts that change slowly. Without a draw, this row would be the same
    /// records in the same order until the listener's habits changed. The app passes in the launch.</para>
    /// </remarks>
    /// <param name="artists">The acts they play, most played first. See
    /// <see cref="ListeningProfile.Artists"/>. An empty list gives an empty row.</param>
    /// <param name="genres">The kinds of music they play, most played first, with each one's share. See
    /// <see cref="ListeningProfile.Genres"/>. An empty list is ordinary. A collection with no genre tags
    /// has none, and the row is then made from the acts alone.</param>
    /// <param name="traits">Every record on the shelf as the ranking asks about it.</param>
    /// <param name="played">The folders with a play behind them. They can then be ranked lower rather than
    /// removed. This is given separately and not worked out from <paramref name="artists"/>, because an act
    /// being played does not say which of their records was.</param>
    /// <param name="exclude">Folders the row must not repeat. These are the ones already on screen above
    /// it.</param>
    /// <param name="draw">Which draw out of the pool this is, as <see cref="Suggested"/> takes it.</param>
    public static IReadOnlyList<string> FromTaste(
        IReadOnlyList<PlayedArtist> artists,
        IReadOnlyList<GenreShare> genres,
        IReadOnlyList<AlbumTraits> traits,
        IReadOnlyCollection<string> played,
        IReadOnlyCollection<string> exclude,
        int count,
        int draw)
    {
        ArgumentNullException.ThrowIfNull(artists);
        ArgumentNullException.ThrowIfNull(genres);
        ArgumentNullException.ThrowIfNull(traits);
        ArgumentNullException.ThrowIfNull(played);
        ArgumentNullException.ThrowIfNull(exclude);

        if (count <= 0 || artists.Count == 0)
            return [];

        // Each act's weight is its share of the leaned-on acts' plays. So a favourite band ranks above the
        // twelfth act they have played, rather than tying with it. The names are folded the same way the
        // album ranking folds a credit. So "The xx" in the log and "the xx" on a record are one act.
        var top = artists.Take(LeaningArtists).ToList();
        var actPlays = (double)top.Sum(a => (long)a.Tally.Plays);
        var acts = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var act in top)
        {
            var key = SimilarAlbums.Fold(act.Name);
            if (key.Length == 0)
                continue;

            acts[key] = actPlays > 0 ? act.Tally.Plays / actPlays : 1.0 / top.Count;
        }

        // The genre shares are already weights that add to one over the window (see GenreShare.Share), so
        // they are used as they are, not re-normalised over the few that are kept. A listener whose top six
        // genres make up a third of their listening should not have those six read as their whole taste.
        var kinds = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var share in genres.Take(LeaningGenres))
            kinds[share.Genre] = share.Share;

        var skip = new HashSet<string>(exclude, StringComparer.OrdinalIgnoreCase);
        var heard = new HashSet<string>(played, StringComparer.OrdinalIgnoreCase);

        var scored = new List<(string Folder, double Score)>();
        foreach (var album in traits)
        {
            if (skip.Contains(album.Folder))
                continue;

            var score = acts.GetValueOrDefault(SimilarAlbums.Fold(album.Artist)) * ActLean;
            foreach (var genre in album.Genres)
                score += kinds.GetValueOrDefault(genre);

            if (score <= 0)
                continue;   // neither their act nor their kind of music: not an answer to this question

            scored.Add((album.Folder, heard.Contains(album.Folder) ? score * PlayedShare : score));
        }

        scored.Sort((a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            return byScore != 0
                ? byScore
                : string.Compare(a.Folder, b.Folder, StringComparison.OrdinalIgnoreCase);
        });

        // One salt for the whole row rather than one per seed. This row has no seeds. It is one ranking
        // over the shelf. The salt is a constant, so the draw alone decides the shuffle.
        return Shuffled([.. scored.Take(count * Pool).Select(s => s.Folder)], draw, "taste")
               .Take(count)
               .ToList();
    }

    /// <summary>How many times a row's worth of similar records is asked for per seed. This lets the row
    /// be DRAWN from a pool, rather than being the pool itself.
    /// </summary>
    /// <remarks>If the row asked for exactly its own length, it would be the same records on every launch
    /// until something else was played. The seeds are what was played last, and the ranking is arithmetic.
    /// The listener would keep seeing the same few albums.
    ///
    /// <para>Three, and not more, because the tail of the pool is the least certain part of the ranking.
    /// Sixty records off one seed are all plausibly like it. Six hundred are the collection again. Asking
    /// for more costs nothing. The ranking walks the whole shelf either way, and it takes the best n at the
    /// end.</para>
    /// </remarks>
    public const int Pool = 3;

    /// <summary>One seed's pool, shuffled by the draw.
    /// </summary>
    /// <remarks>The pool is ordered by score. Past its first few records, the order is a heuristic, not a
    /// ranking anyone could defend. The fortieth most similar record and the twentieth are the same kind of
    /// claim. So the draw shuffles the whole pool, and the row takes its front. This gives what the feature
    /// is for: a different row on each launch, made of records that are all still answers to "what is this
    /// record like".
    ///
    /// <para>The result depends on both the draw and the seed. Two seeds that draw on the same launch must
    /// not walk the same shuffle. If they did, the row's round-robin would take the same positions from
    /// each.</para>
    /// </remarks>
    private static List<string> Drawn(IReadOnlyList<SimilarAlbum> pool, int draw, string seed)
        => Shuffled([.. pool.Select(s => s.Folder)], draw, seed);

    /// <summary>One pool, shuffled by the draw and by a salt the caller chooses. This is shared by
    /// <see cref="Drawn"/> and by the taste row, which has no seed. A single copy of the generator means
    /// the two cannot drift apart and answer differently.
    /// </summary>
    private static List<string> Shuffled(List<string> folders, int draw, string salt)
    {
        // A stable hash of the salt, folded into the draw. The shuffle belongs to this caller and does not
        // change when the framework's string hashing does. That hashing is per process, and it would break
        // the promise that the same draw gives the same row across runs.
        var state = draw;
        foreach (var c in salt)
            state = unchecked((state * 31) + c);
        state = unchecked(state * (int)2654435761);

        // Fisher-Yates over a small deterministic generator. Random is not used. The result must be the
        // same on every machine and in every process for a given draw. Random's shared instance does not
        // promise that, and its seeded form does not promise it across runtimes.
        for (var i = folders.Count - 1; i > 0; i--)
        {
            state = unchecked((state * 1103515245) + 12345);
            var j = (int)((uint)(state >> 8) % (uint)(i + 1));
            (folders[i], folders[j]) = (folders[j], folders[i]);
        }

        return folders;
    }
}

/// <summary>Which of the home screen's questions a featured card is answering. The banner at the top of the
/// screen shows one card at a time, and the kind is what its kicker line says. A card that did not name its
/// question would be an album picture with no reason to be the one on screen.</summary>
public enum HomeFeatureKind
{
    /// <summary>The record that was on last. On the first opening of a session, this is the record that
    /// was on when the app was closed.
    ///
    /// <para><b>The same fact as <see cref="Listened"/>, asked for one record rather than twenty.</b> It is
    /// the newest play in the collection. It has a card of its own, because "carry on with what you were
    /// doing" is a different offer from "here is something you played lately". It is also the first thing
    /// most people want from a home screen. The two never show the same record. The strip features a record
    /// once, and this card is dealt first.</para>
    ///
    /// <para><b>It opens the record. It does not resume a position.</b> Nothing in this app saves where
    /// playback had got to within a track. So there is no position to return to. What the card can say
    /// honestly is which record was playing last. That survives closing the app, because it is a fact
    /// stored in the track rows.</para>
    /// </summary>
    Continue,

    /// <summary>The newest record on the shelf.</summary>
    Added,

    /// <summary>One of the lists the library writes out of what has been played.</summary>
    MadeForYou,

    /// <summary>A record the ranking picked from recent listening.</summary>
    Suggested,

    /// <summary>A record with the most plays behind it.</summary>
    Played,

    /// <summary>A record played recently.</summary>
    Listened,

    /// <summary>A radio station, ranked by how much of it has been listened to. It is the one card in the
    /// strip that is not on the shelf. A station is not a record, and the mirror holds nothing about it. So
    /// the key is the station's uuid, and the answer comes from <see cref="Radio.RadioListens"/> rather
    /// than from this class.</summary>
    Radio,
}

/// <summary>One card in the featured banner: which question put it there, and what it opens. The key is a
/// library-relative folder for every kind except <see cref="HomeFeatureKind.MadeForYou"/>. For that kind it
/// is the list's own name, as the row of lists spells it. For <see cref="HomeFeatureKind.Radio"/> it is the
/// station's uuid.</summary>
public readonly record struct HomeFeature(HomeFeatureKind Kind, string Key);

/// <summary>The banner at the top of the home screen. It holds a few cards, shows one at a time, and is
/// stepped through with the two keys on the plate.
/// </summary>
public static class HomeFeatures
{
    /// <summary>How many cards the banner holds. There is one per question the screen asks, and there are
    /// seven (see <see cref="HomeFeatureKind.Radio"/> and <see cref="HomeFeatureKind.Continue"/>). More
    /// cards would make a strip nobody reaches the end of. The rows below are where a collection is browsed.
    ///
    /// <para>The count is seven, not six, because of the radio card. At six, the station would take the
    /// last slot from <see cref="HomeFeatureKind.Listened"/> on any collection able to fill the strip. So
    /// the station would silently remove a whole question. At seven, each question still has its card. The
    /// station takes the second round's first pick, which is one more record from a question already on
    /// the strip. That is the trade, and it is the one worth making.</para></summary>
    public const int Count = 7;

    /// <summary>The featured cards, in the order the banner steps through them.
    ///
    /// <para><b>One from each question before a second from any of them.</b> The banner is the one place on
    /// this screen that mixes the rows. A strip of five records that all arrived last week would be the
    /// Recently added row drawn larger. Sources with nothing in them are skipped, and the strip fills from
    /// the rest. A new collection needs this. Nothing has been played there, so four of the five questions
    /// have no answer, and the banner shows the newest records.</para>
    ///
    /// <para><b>A record is featured once.</b> The most played record is often also a recent listen. The
    /// same sleeve twice in a strip of five would make the banner look stuck. Lists are counted apart from
    /// records. The two are different things, and their names cannot collide.</para>
    /// </summary>
    /// <param name="continued">The record that was on last, or empty where nothing has been played. It is
    /// one entry, not a list. There is only one last record, and a second-to-last one is not an answer to
    /// this question. It is dealt first, so the card gets the record it names, not one that another
    /// question has already taken.</param>
    /// <param name="added">Newest on the shelf first, as <see cref="HomeShelf.Added"/> answers.</param>
    /// <param name="lists">The generated lists worth offering, best first, by name.</param>
    /// <param name="suggested">What the ranking picked, as <see cref="HomeShelf.Suggested"/>
    /// answers.</param>
    /// <param name="played">Most plays first, as <see cref="HomeShelf.Played"/> answers.</param>
    /// <param name="listened">Newest listen first, as <see cref="HomeShelf.Listened"/> answers.</param>
    /// <param name="stations">The radio stations heard most, most first, by uuid. This is the answer from
    /// <see cref="Radio.RadioListens.Top"/>. It is empty until a station has been left on long enough to
    /// count. That is true of most first evenings. The strip then fills from the other sources.</param>
    /// <param name="isVariousArtists">Whether the record at a folder is filed under Various Artists. Those
    /// records are never featured, on any card, including <see cref="HomeFeatureKind.Continue"/>. A card is
    /// drawn from the act's photograph, and a compilation has no act to photograph, so its card would be a
    /// blank plate. This is required rather than defaulted, so a caller cannot forget it.</param>
    /// <param name="count">How many cards to fill, at most.</param>
    public static IReadOnlyList<HomeFeature> Strip(
        IReadOnlyList<string> continued,
        IReadOnlyList<string> added,
        IReadOnlyList<string> lists,
        IReadOnlyList<string> suggested,
        IReadOnlyList<string> played,
        IReadOnlyList<string> listened,
        IReadOnlyList<string> stations,
        Func<string, bool> isVariousArtists,
        int count)
    {
        if (count <= 0)
            return [];

        // Dropped from the records before dealing, not skipped while dealing. So a question whose first
        // answer is a compilation offers its next answer instead of losing its turn.
        IReadOnlyList<string> Records(IReadOnlyList<string> keys) => [.. keys.Where(k => !isVariousArtists(k))];

        (HomeFeatureKind Kind, IReadOnlyList<string> Keys)[] sources =
        [
            // First, and this is the whole reason the card works. Every other question could also answer
            // with the record that was last on. Whichever question was dealt first would take it. Dealt
            // first, this card gets it, and the rest step past it.
            (HomeFeatureKind.Continue, Records(continued)),

            (HomeFeatureKind.Added, Records(added)),
            (HomeFeatureKind.MadeForYou, lists),
            (HomeFeatureKind.Suggested, Records(suggested)),
            (HomeFeatureKind.Played, Records(played)),
            (HomeFeatureKind.Listened, Records(listened)),

            // Last, so a collection that can answer every other question keeps its records and gains a
            // card, rather than trading one away. On a collection that cannot, such as a new one where four
            // of the questions have no answer, the round steps past the empty sources. The station then
            // comes up near the front, which is where it belongs in that case.
            (HomeFeatureKind.Radio, stations),
        ];

        var strip = new List<HomeFeature>(count);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // How far into each source the strip has read. This is kept per source, not as one round number. So
        // a source whose next entry is already on the strip is stepped past, and does not lose its turn.
        var cursor = new int[sources.Length];

        for (var round = 0; strip.Count < count && round < count; round++)
        {
            var addedThisRound = false;
            for (var i = 0; i < sources.Length && strip.Count < count; i++)
            {
                var (kind, keys) = sources[i];
                while (cursor[i] < keys.Count && taken.Contains(Mark(kind, keys[cursor[i]])))
                    cursor[i]++;

                if (cursor[i] >= keys.Count)
                    continue;

                var key = keys[cursor[i]++];
                taken.Add(Mark(kind, key));
                strip.Add(new HomeFeature(kind, key));
                addedThisRound = true;
            }

            // Every source has been read to the end. Without this check, the loop spins on a collection too
            // small to fill the strip. Every new collection is that small.
            if (!addedThisRound)
                break;
        }

        return strip;
    }

    /// <summary>What "already featured" is checked against. A list's name, a station's uuid and a folder
    /// are three different namespaces. So a list with the same name as a folder must not push that record
    /// off the strip.</summary>
    private static string Mark(HomeFeatureKind kind, string key) => kind switch
    {
        HomeFeatureKind.MadeForYou => "list\n" + key,
        HomeFeatureKind.Radio => "station\n" + key,
        _ => "album\n" + key,
    };
}
