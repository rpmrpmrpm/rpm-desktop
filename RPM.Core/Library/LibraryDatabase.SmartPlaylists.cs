using Microsoft.Data.Sqlite;

using RPM.Core.Metadata.Providers;

namespace RPM.Core.Library;

/// <summary>
/// The playlists the library writes for itself. See <see cref="SmartPlaylistKind"/> for what one is.
///
/// <para>A kind is a JOIN, a WHERE and an ORDER BY and nothing else. It is said once, in
/// <see cref="SmartPlaylistRule"/>, and both the list and the count read it from there. If the rule
/// were written twice, once for the rows and once for the subtitle, the pane could announce 900 songs
/// above a list of 200. The two clauses drift apart, and neither is wrong on its own. The switch has
/// no fallback arm, so a kind added to the enum does not compile until its rule has been written.</para>
///
/// <para>Both reads happen on the UI thread when the playlist pane reloads, so every rule is bounded:
/// the ones over the track rows order by an indexed column and stop at
/// <see cref="SmartPlaylists.MaxTracks"/>, and the ones over the listening log are windowed by
/// <c>at_utc_ticks</c>, which is indexed, and never look further back than
/// <see cref="SmartPlaylists.HistoryDays"/>.</para>
/// </summary>
public sealed partial class LibraryDatabase
{
    /// <summary>
    /// The clause that narrows a generated list to what can actually be played, or empty.
    ///
    /// <para>
    /// A generated list is a question asked of the track rows. On a borrowed shelf whose computer is not
    /// on the network, most of those rows describe files on another computer. Favourites marked on that
    /// shelf are the reason this clause exists. The list is meant to play them, and a list that silently
    /// skips half of them is the worst possible result.
    /// </para>
    ///
    /// <para>Qualified as <c>tracks.cloud_state</c>: the listening rules join a grouped read of
    /// <c>listen</c>, and an unqualified column would be ambiguous if that table ever gains one.</para>
    /// </summary>
    private string Reachable =>
        ReachTerm("tracks.cloud_state") is { Length: > 0 } reach ? $" AND {reach}" : "";

    /// <summary>
    /// A song the listener has turned down is never offered by a list the library writes itself.
    ///
    /// <para>This is all that a dislike does, and that is deliberate. A verdict could have been weighed:
    /// pushed down the ranking, or counted against its genre. Each of those is a rule that someone has
    /// to trust without being able to see it work. This one is simple to check. The song is removed,
    /// and it does not come back.</para>
    ///
    /// <para>It does not touch the heart's own lists. "The songs you loved" asks about hearts and
    /// answers with hearts. A song cannot be both (see <see cref="TrackVerdict"/>), so there is
    /// nothing here for it to remove.</para>
    ///
    /// <para><b>Asked of ONE kind, which is the whole of what a verdict is about.</b> A dislike means
    /// the song does not suit this one list. It can still suit another list. A song turned down in
    /// Chill is removed from Chill and from every genre and decade list cut from it. Dancefloor and
    /// Focus still offer it, as before. Verdicts are stored per kind for this reason.</para>
    /// </summary>
    private static string NotTurnedDown(SmartPlaylistKind kind) =>
        " AND NOT EXISTS (SELECT 1 FROM track_verdict v" +
        " WHERE v.relative_path = tracks.relative_path" +
        $" AND v.kind = {(int)kind} AND v.verdict = {(int)TrackVerdict.Disliked})";

    /// <summary>
    /// A song by an act, or on a record, that the listener has refused for this kind of list is not
    /// offered either.
    ///
    /// <para><b>Stated here as well as in the pool, because the two cover different lists.</b> The mood
    /// lists are composed in code and pass SQL a list of paths. Their refusals are therefore removed
    /// from the POOL (see <see cref="MoodPool.Allowed"/>). That is the only place they can be removed
    /// without the refused songs still using up window slots. Every other generated list is a query,
    /// and this is where a query is narrowed. One rule, two forms: the same split that
    /// <see cref="LikedByKind"/> and <see cref="NotTurnedDown"/> already follow.</para>
    ///
    /// <para>The artist name comes from <see cref="Credit"/>. That follows
    /// <see cref="RecommendRefusals.CreditOf"/>, because that is the name the menu stores the refusal
    /// under, and the name <see cref="MoodPool"/> caps by. Three ways of reading "who is this by" would
    /// be three chances for them to disagree.</para>
    /// </summary>
    private static string NotByARefusedName(SmartPlaylistKind kind) =>
        " AND NOT EXISTS (SELECT 1 FROM recommend_refusal r" +
        $" WHERE r.kind = {(int)kind} AND ((r.scope = {(int)RecommendScope.Album}" +
        " AND r.subject = tracks.folder) OR (r.scope = " + (int)RecommendScope.Artist +
        $" AND r.subject = {Credit})))";

    /// <summary>
    /// Who a song is by, in SQL. It follows <see cref="RecommendRefusals.CreditOf"/>'s rule as closely
    /// as a SELECT can.
    ///
    /// <para><b>Why it is written twice.</b> The mood lists are composed in code and remove their
    /// refusals from the pool. Every other generated list is a query and removes them here. Both must
    /// produce the same name, or a refusal made from one list has no effect on another.</para>
    ///
    /// <para><b>Where it is only an approximation.</b>
    /// <see cref="ArtistCredit.IsVariousArtists"/> compares names reduced to letters and digits only,
    /// which SQL cannot do in general. This folds case, spaces, hyphens and full stops. That covers
    /// every way the three spellings are written in practice. A tagger that writes "Various / Artists"
    /// would be read as a compilation by the pool, and as an act by this clause. The spellings come from
    /// <see cref="ArtistCredit.VariousSpellings"/>, so the two readings always use the same ones.</para>
    /// </summary>
    private static readonly string Credit = BuildCredit();

    private static string BuildCredit()
    {
        // The two folds must match exactly, so they sit next to each other. FoldHere folds the
        // literals in C# before SQL compares them with FoldInSql.
        static string FoldInSql(string column) =>
            $"REPLACE(REPLACE(REPLACE(LOWER(TRIM({column})), ' ', ''), '-', ''), '.', '')";

        static string FoldHere(string name) =>
            name.Trim().ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace(".", "");

        var various = string.Join(", ",
            ArtistCredit.VariousSpellings.Select(name => $"'{FoldHere(name).Replace("'", "''")}'"));

        return $"""
            CASE WHEN NULLIF(TRIM(tracks.album_artist), '') IS NOT NULL
                      AND {FoldInSql("tracks.album_artist")} NOT IN ({various})
                 THEN TRIM(tracks.album_artist)
                 ELSE COALESCE(NULLIF(TRIM(tracks.artist), ''), '')
            END
            """;
    }

    /// <summary>
    /// Everything a generated list is narrowed by, beyond its own rule, in one string.
    ///
    /// <para>This is one property, not two clauses added at each call site. The list and its subtitle
    /// are built separately. If a narrowing were added to one and forgotten in the other, the pane would
    /// announce fifty songs above a list of forty. The answer is defined in one place, here.</para>
    /// </summary>
    private string Offerable(SmartPlaylistKind kind) =>
        Reachable + NotTurnedDown(kind) + NotByARefusedName(kind);

    /// <summary>
    /// Where the next reading of ONE mood list starts in its pool.
    ///
    /// <para><b>This is the one part of a generated list that is deliberately not stable.</b>
    /// Everything else here gives the same answer each time it is asked, and the mood cut is held for
    /// the whole session for that reason. A list capped at fifty out of a pool of two hundred has to
    /// show a different fifty sometimes. Otherwise the cap turns a large collection into a small one.
    /// The window moves only at the two moments described below, and not on every read.</para>
    ///
    /// <para>It advances in steps rather than being picked at random, so the whole pool is seen over
    /// several readings instead of the same songs coming up by chance.</para>
    ///
    /// <para><b>Two things move it, and nothing else does: starting the app, and the "generate a new
    /// list" button.</b> Opening a list does not move it, and neither does playing one from its row. A
    /// list that nobody has asked to change keeps the same songs. A phone is sent the whole cut, so a
    /// change there would show up as well. Every read of a list returns it as it stands.</para>
    ///
    /// <para><b>The presses are counted per list.</b> The count belongs to each list, not to the whole
    /// collection, so that pressing refresh on one list does not step every list. The phone is sent the
    /// whole cut, so a single shared count would change every list there too. The launch is still
    /// shared by every list, because starting the app happens to the whole collection, not to one row.</para>
    ///
    /// <para>The window is keyed by the whole <see cref="SmartPlaylistId"/>, because the pool is the
    /// narrowed one. "calm" and "calm rock" draw on different pools, and they are two lists to walk,
    /// not one.</para>
    ///
    /// <para><b>The window is a count of readings, not an offset in songs.</b> A step of fifty songs
    /// does not work. The pool is whatever the narrowing leaves, and when the size of the pool divides
    /// evenly into the step, the window never moves. For example, a Dancefloor funk pool of 25 songs
    /// sits behind a list of 11. 50 % 25 is zero, so every press returned the same eleven songs, and
    /// fourteen were never reached. Making the step the pool's own business avoids this. See
    /// <see cref="MoodPool.Window"/>.</para>
    ///
    /// <para>Only a request for a new list advances the window. The pane reads a subtitle for every list
    /// it draws. If those reads moved the window too, a dozen lists would use up their steps before anyone
    /// had asked for a new list.</para>
    /// </summary>
    private readonly Dictionary<SmartPlaylistId, int> _moodWindows = [];

    /// <summary>
    /// The lock over the four values this file cuts ONCE per session and then answers from memory:
    /// the mood window, the mood shelf, the published cut, and the folders on each genre.
    ///
    /// <para><b>It is separate from <c>_gate</c> because none of the four values writes anything.</b>
    /// The writing connection's lock is <c>_gate</c>, and a sync commit holds it across its whole batch.
    /// Each of the four builds itself outside that lock. But each one also took it first, to check
    /// whether its value already existed. So a read had to wait for the whole import. Drawing one
    /// playlist row, or building the catalogue a peer asked for, waited for a forty-thousand-row import
    /// to finish before it could find out that the answer was already in memory. Measured at 3.9 and
    /// 4.3 seconds against a batch that size. This was the cause of the multi-second hang on a
    /// borrowed shelf. See LibraryDatabaseConcurrencyTests.</para>
    ///
    /// <para>Nothing under this lock touches either connection. So it never nests with <c>_gate</c> or
    /// <c>_readGate</c>, and it cannot be half of a deadlock. <b>Do not use <c>_gate</c> here.</b> A
    /// memoised value has nothing to do with the writer.</para>
    ///
    /// <para><see cref="Gate"/> is used here too, for the same reason as the connections. The cut this
    /// lock guards is the one suspected of the hang. It is taken from a worker thread and read from the
    /// UI thread. A lock that is not instrumented, next to two instrumented ones, would hide the next
    /// problem of this kind.</para>
    /// </summary>
    private readonly Gate _cuts = new("cuts");

    internal int MoodWindow(SmartPlaylistId id, bool advance)
    {
        using (_cuts.Take())
        {
            var pressed = _moodWindows.TryGetValue(id, out var reading) ? reading : 0;

            // A list that has never been advanced is at zero. Asking where it is must not store an
            // entry for it, because the pane asks for every list it draws a subtitle for.
            if (advance)
                _moodWindows[id] = ++pressed;

            // The launch of this collection plus the presses this list has had. There are two numbers
            // because they are kept differently. The launch is stored against the library and survives
            // a restart. The presses belong to this session only.
            return _moodLaunch + pressed;
        }
    }

    /// <summary>Which launch of this collection the app is on. Read once, when the library is opened.
    /// </summary>
    private int _moodLaunch;

    /// <summary>The key the launch count is stored under. It is stored against the library, not as an
    /// app setting. If the app is pointed at another folder, it uses that library's count.</summary>
    internal const string MoodLaunchKey = "mood_launch";

    /// <summary>
    /// Steps every generated list on by one, because the app has been started.
    /// </summary>
    /// <remarks>
    /// <para>Called from the constructor, after both connections are up. It reads the stored count and
    /// writes the new one back.</para>
    ///
    /// <para><b>Stored against the library rather than counted in memory.</b> A number that started at
    /// zero on every launch would give the same fifty songs each time the app was opened, which would
    /// defeat "generated on app start". Stored, it is a different list each time, and the same list
    /// appears on the screen and on the phone.</para>
    ///
    /// <para>Every list moves by the same one, which is what "on app start" means. A press moves only
    /// the one list it was pressed on (see <see cref="MoodWindow"/>).</para>
    /// </remarks>
    private void NoteLaunchForGeneratedLists()
    {
        _moodLaunch = GetMetaVersion(MoodLaunchKey) + 1;
        SetMeta(MoodLaunchKey, _moodLaunch.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private MoodShelf? _moods;

    /// <summary>
    /// The collection's moods as they stood when this session started. This is the cut that every mood
    /// list agrees with during this launch.
    ///
    /// <para><b>Taken once and then held for the life of the process, on purpose.</b> The measuring
    /// pass fills the shelf over many launches, one track at a time, so the distribution the bands are
    /// cut from keeps changing. If the cut were rebuilt on each read, "Calm" would hold different songs
    /// every time it was opened, and songs would leave it while the listener had changed nothing. That
    /// looks like a bug, and nothing on the screen explains it. The cost is that a track measured this
    /// afternoon joins its list tomorrow.</para>
    ///
    /// <para><b>What arrives out of order here, and why it is safe.</b> The pass writes readings from a
    /// worker thread through <c>SaveTrackMood</c> (under <c>_gate</c>) for as long as the app is open.
    /// This property is read on the UI thread whenever a list is drawn. The two sides never contend.
    /// The two reads that build the cut take <c>_readGate</c> like every other read. After that, this
    /// holds no connection and asks the database nothing. A write that lands during the build is simply
    /// not in the cut. That gives the same answer as a write that lands a second later, and both match
    /// the rule for this session: the cut is fixed until the next launch. The only ordering that would
    /// matter is two threads building it at once, and the lock below prevents that. Otherwise they
    /// would get two different cuts, and the lists would disagree with each other for the rest of the
    /// session.</para>
    /// </summary>
    public MoodShelf SessionMoods
    {
        get
        {
            using (_cuts.Take())
            {
                // Read inside the lock so two threads cannot each decide it is missing and build one.
                if (_moods is not null)
                    return _moods;
            }

            // Built outside the lock: it is three whole-table reads and a sort, and holding the cut
            // lock across it would make one reader wait out another's build for no gain.
            //
            // The catalogues' answer is read ONCE and used twice. It decides which cohort a record is
            // cut against, and which records a list prefers. Two reads a moment apart could disagree
            // while the styles sweep is running. A cut whose ranges and narrowings come from two
            // different states cannot be reasoned about.
            var facts = CatalogueFactsByFolder();
            var built = MoodShelf.Build(
                QueryMeasuredMoods(),
                MoodBands.GenreByFolder(QueryAlbumGenreCounts(), facts),
                facts,
                // Read here, not inside the cut, for the same reason as the catalogues' answer. One
                // read is used once per kind, so no two lists disagree about what the listener has
                // said while a verdict is being written on another thread.
                LikedByKind());

            using (_cuts.Take())
            {
                // Whoever got here first wins. A second cut built in a race is thrown away rather than
                // installed, because two lists drawn from two cuts is the exact disagreement this
                // property exists to prevent.
                return _moods ??= built;
            }
        }
    }

    private ListeningProfile? _profile;

    /// <summary>
    /// What the listening log says about this listener, as it stood when this session first asked.
    /// Every list that depends on listening habits uses this same description.
    /// </summary>
    /// <remarks>
    /// Held for the life of the process, for the same reason as <see cref="SessionMoods"/>, and the
    /// argument is the same. If it were rolled up again on each read, "Your mix" would hold different
    /// songs each time it was opened, and songs would leave it while the listener had touched nothing.
    /// The cost is that listening from this evening counts from tomorrow.
    ///
    /// <para><b>What it does NOT hold still is the hour.</b> The profile says which kinds of music get
    /// played in which part of the day. Which part it is now is read from the clock that the rule is
    /// given each time it is composed. So the list changes through the day while the description does
    /// not.</para>
    ///
    /// <para>Three grouped reads over the log run for the playlist pane, and the pane composes a rule
    /// for each list it offers. Without this, opening the pane would roll up the whole log once for
    /// every list. It is built outside the lock and installed under it, the same as the mood cut. Two
    /// threads building it at once would get two descriptions, and the lists would disagree with each
    /// other for the whole session.</para>
    /// </remarks>
    public ListeningProfile SessionProfile
    {
        get
        {
            using (_cuts.Take())
            {
                if (_profile is not null)
                    return _profile;
            }

            var built = ReadListeningProfile(ListeningWindow.Everything, DateTime.UtcNow.Ticks);

            using (_cuts.Take())
                return _profile ??= built;
        }
    }

    /// <summary>One song "Your mix" may draw on: loved or played, with the record it is on.</summary>
    /// <remarks>
    /// The pool is narrowed HERE rather than in the arithmetic. The narrowing is the one hard rule of
    /// that list, and SQLite can state it in the WHERE: a song with no heart and no plays is not the
    /// listener's, whatever the hour says. Everything else about the list is a ranking, and a ranking
    /// is not something to ask SQL for. See <see cref="HourlyMix"/>.
    ///
    /// <para>This returns every such song, not the best fifty. Which fifty to keep depends on a weighting
    /// this query does not know about. It also depends on a cap that has to see the whole pool to spread
    /// the records. This is the same answer <see cref="QueryDecadeChart"/> gives to the same question. The
    /// result is bounded by what somebody has loved or played, not by what they own. On a real shelf that
    /// is a fraction of the whole.</para>
    /// </remarks>
    public IReadOnlyList<MixSong> QueryMixSongs()
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                """
                SELECT relative_path, folder, favorite, plays, last_played_utc_ticks
                FROM tracks
                WHERE favorite = 1 OR plays > 0
                """;

            using var reader = cmd.ExecuteReader();
            var songs = new List<MixSong>();
            while (reader.Read())
            {
                songs.Add(new MixSong(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2) != 0,
                    reader.GetInt32(3),
                    reader.GetInt64(4)));
            }

            return songs;
        }
    }

    private Cloud.MoodListsPayload? _moodCut;

    /// <summary>
    /// This session's generated lists, whole, as everything outside this machine gets them.
    ///
    /// <para><b>One cut, however many people ask for it.</b> The phone reads it after a push, and a
    /// borrower reads it inside the catalogue their machine fetches. If it were built twice, the two
    /// copies could differ: the mood window moves, rows change, and the styles sweep can land. Then two
    /// people looking at the same shelf would see two different Slow Burns. So it is built once, here,
    /// beside the <see cref="SessionMoods"/> cut it is taken from.</para>
    ///
    /// <para>Read with <c>advance: false</c>. A cut is a reading of the lists as they stand, and it
    /// must not use up the window that the person at this computer has not asked to move.</para>
    /// </summary>
    public Cloud.MoodListsPayload SessionMoodLists
    {
        get
        {
            using (_cuts.Take())
            {
                if (_moodCut is not null)
                    return _moodCut;
            }

            // Built outside the lock: it queries every offered list, and holding the cut lock across
            // that would make one reader wait out another's build for no gain.
            var built = Cloud.MoodListPush.Build(this, SmartPlaylists.SentOut, DateTime.UtcNow.Ticks);

            using (_cuts.Take())
            {
                // Whoever got here first wins, for the same reason as SessionMoods. Two cuts installed
                // in a race would disagree, and this lock exists to prevent that.
                return _moodCut ??= built;
            }
        }
    }

    /// <summary>
    /// Builds the cut again and puts it in place of the session's cut, so that everything that
    /// publishes it publishes the same one.
    ///
    /// <para><b>Why a cut is built twice at all.</b> What crosses to the phone is the lists as they
    /// stand in the cloud. A record with no RPM id names nothing the phone can ask for, so it is left
    /// out. The answer changes as an upload finishes, as records arrive, and as tags change. A cut taken
    /// once at launch would show the collection as it was in the first minute, until the app was
    /// restarted. See <c>MoodListPushService</c>, which asks for this when one of those things has
    /// happened.</para>
    ///
    /// <para><b>The session's cut is replaced, not sidestepped.</b> The phone reads what is pushed, and
    /// a borrower reads <see cref="SessionMoodLists"/> inside the catalogue their machine fetches. A second
    /// cut standing beside the first would show two people one shelf with two different Slow Burns. The
    /// new cut is installed under <see cref="_cuts"/>, and the payload is a record. A reader therefore
    /// gets a whole old cut or a whole new one, never half of each.</para>
    ///
    /// <para><b><see cref="SessionMoods"/> is not rebuilt</b>, on purpose. That reading decides which
    /// records are calm. It is cut once, behind a lock, so that no two lists disagree about it. What is
    /// asked again here is which records each list holds. That is the same question the pane's own
    /// sizes read again.</para>
    /// </summary>
    public Cloud.MoodListsPayload RecutMoodLists()
    {
        // Built outside the lock, like the first cut: it queries every offered list.
        var built = Cloud.MoodListPush.Build(this, SmartPlaylists.SentOut, DateTime.UtcNow.Ticks);

        using (_cuts.Take())
            return _moodCut = built;
    }

    /// <summary>
    /// Every generated list this collection can offer: one per kind, plus one per genre under the
    /// moods that are cut within a genre at all, plus the decades.
    ///
    /// <para><b>Kept here rather than in the pane that draws them.</b> Two things read this list: the
    /// playlists tree, and the push that sends the cut to the phone. A phone that shows lists the
    /// computer does not offer, or is missing one it should offer, is the disagreement that follows
    /// from writing the rule down twice.</para>
    ///
    /// <para>The genres shown are decided by the shelf, not by a caller. A genre appears as it fills and
    /// disappears if it empties, so the offer follows the collection and not a list that someone wrote
    /// down. <see cref="SmartPlaylistKind.Sleep"/> is deliberately not broken out by genre. It is an
    /// absolute cut over everything, so "sleep jazz" would be just the jazz that happens to be in it,
    /// not an answer to anything.</para>
    /// </summary>
    /// <param name="kinds">The kinds on offer, in order. The caller states them because they differ by
    /// shelf: a borrowed shelf offers one row and none of the moods.</param>
    /// <param name="withDecades">Whether the decade lists are among them. False on a borrowed shelf,
    /// where a decade of somebody else's collection is a question about them.</param>
    public IReadOnlyList<SmartPlaylistId> OfferedGeneratedLists(
        IReadOnlyList<SmartPlaylistKind> kinds, bool withDecades = true)
    {
        ArgumentNullException.ThrowIfNull(kinds);

        var offered = new List<SmartPlaylistId>();

        foreach (var kind in kinds)
        {
            offered.Add(SmartPlaylistId.WholeShelf(kind));

            if (!SmartPlaylists.BrokenOutByGenre.Contains(kind))
                continue;

            // This uses the narrowed floor, because every id built here carries a genre. With the
            // whole-shelf floor, only genres large enough to fill a shelf-sized row would be offered,
            // and the genre pages of all the other genres would draw no mood shelf at all.
            foreach (var genre in SessionMoods.ListFor(kind)
                                              .GenresWorthListing(SmartPlaylists.LeastForNarrowedRow(kind)))
            {
                // A genre that another list has absorbed is not offered. Its songs are in that list
                // now, so a row of its own would be the same music under a second name. See MoodSets.
                if (MoodSets.IsAbsorbed(kind, genre))
                    continue;

                offered.Add(SmartPlaylistId.Within(kind, genre));
            }
        }

        if (withDecades)
            foreach (var decade in SmartPlaylists.Decades)
                offered.Add(SmartPlaylistId.In(decade));

        return offered;
    }

    /// <summary>
    /// The album folders filed under one of the app's genres, as this session reads the collection.
    ///
    /// <para><b>Why the folders and not a WHERE on the tag.</b> The <c>genre</c> column holds whatever
    /// the last tagger typed, such as "Hip Hop", "Rap" or "Pop Rap". Which of the app's own genres that
    /// text belongs to is decided in code, by <see cref="Metadata.Genres.Match"/>, over all of a
    /// record's tags at once. SQL cannot ask that question. So it is answered here, once, and the query
    /// is handed a list of folders to join against. This is the same shape that
    /// <see cref="SmartPlaylistRule.Shelf"/> uses for a list of paths, and for the same reason.</para>
    ///
    /// <para><b>The wall's fold, not the mood cut's. The two are not the same.</b> The genres wall, and
    /// so a genre's page, its shelves and everything on them, files a record by taking the tag that most
    /// of its tracks carry and matching THAT (see <c>LibraryAlbumIndex.ReadAlbumGenres</c> and
    /// <see cref="GenreWall.Build"/>). The mood cut matches every tag first and then takes the dominant
    /// answer (<see cref="MoodBands.GenreByFolder"/>). That is the right rule for a distribution, and it
    /// is a different rule from this one. The two disagree on a record whose most common tag matches
    /// nothing, and whose second tag matches something.</para>
    ///
    /// <para>This follows the wall, because that is what the lists are for. The lists this narrows are
    /// offered on a genre's page, beside the shelves the wall built. A page whose "most played rock"
    /// left out a record that stands on the shelf above it would disagree with itself. The mood lists on
    /// that same page are cut by the mood shelf's own rule and keep it. A band is a percentile taken
    /// within a cohort. Re-cutting the cohort here to match a shelf label would change what "calm" means.</para>
    ///
    /// <para><b>Held for the session</b>, as <see cref="SessionMoods"/> is, with the same effect: a record
    /// retagged this afternoon moves shelves at the next launch. A genre's page and its generated lists
    /// are read many times in an evening. Folding every tag in the library on each read would mean a
    /// whole-table scan for each row drawn.</para>
    /// </summary>
    /// <param name="genre">One of <see cref="Metadata.Genres.All"/>. Anything else has no folders. That
    /// is an ordinary answer, not an error: a list narrowed to a genre the collection has none of is an
    /// empty list.</param>
    public IReadOnlyList<string> FoldersOnGenre(string genre)
    {
        ArgumentNullException.ThrowIfNull(genre);

        using (_cuts.Take())
            if (_foldersByGenre is not null)
                return Found(_foldersByGenre);

        // Built outside the lock, like SessionMoods. It is a whole-table read and a fold, and holding
        // the cut lock across it would make one reader wait out another's build.
        //
        // The commonest tag per folder is used. If two tags tie, the alphabetically first one wins. A
        // record that is half Jazz and half Blues has to land somewhere definite, or it would move
        // between two reads of the same rows. Only after that is the tag matched. That order is the wall's.
        var best = new Dictionary<string, (string Genre, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, tagged, count) in QueryAlbumGenreCounts())
        {
            if (best.TryGetValue(folder, out var standing)
                && (standing.Count > count
                    || (standing.Count == count
                        && string.Compare(standing.Genre, tagged, StringComparison.CurrentCultureIgnoreCase) <= 0)))
                continue;
            best[folder] = (tagged, count);
        }

        var built = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (folder, standing) in best)
        {
            if (Metadata.Genres.Match(standing.Genre) is not { } filed)
                continue;   // a tag the app cannot file is on no genre, which is the wall's answer too

            if (!built.TryGetValue(filed, out var folders))
                built[filed] = folders = [];
            folders.Add(folder);
        }

        using (_cuts.Take())
            return Found(_foldersByGenre ??= built);

        IReadOnlyList<string> Found(Dictionary<string, List<string>> by)
            => by.TryGetValue(genre, out var folders) ? folders : [];
    }

    private Dictionary<string, List<string>>? _foldersByGenre;

    /// <summary>
    /// Which songs a generated playlist is made of, and what order they are in. This is everything one
    /// kind means, in the fragments that every read of it needs.
    ///
    /// <para><b>Two shapes, because there are two kinds of question.</b> Most lists are something the
    /// track rows can answer: a JOIN, a WHERE and an ORDER BY, which SQLite evaluates. <see cref="Shelf"/>
    /// is the other kind. A band is a percentile taken within the record's own genre, over a distribution
    /// that has to be held in memory, and no WHERE clause can express that. Without a shared form, each
    /// of the three places a rule is read would need an if-statement for the two shapes. So the difference
    /// is stated here, and <see cref="Compose"/> is the only way in. Every reader gets the same three
    /// fragments and never learns which shape produced them.</para>
    /// </summary>
    private abstract record SmartPlaylistRule
    {
        /// <summary>
        /// One rule's contribution to a statement: the fragments, and the values to bind.
        ///
        /// <para>Kept apart from the command, so that <see cref="Compose"/> needs no connection. See
        /// there for what that buys. <see cref="BindTo"/> is called with the read gate held, which is
        /// where everything that touches the connection belongs.</para>
        /// </summary>
        /// <param name="From">Everything between FROM and WHERE, <c>tracks</c> included. This is the whole
        /// clause, not a join added to a fixed <c>FROM tracks</c>, because which table leads decides
        /// whether a list comes back quickly or is waited for. A rule that hands SQLite a list of paths
        /// must put that list first. See <see cref="Shelf.Compose"/> for the measurement.</param>
        public sealed record Composed(
            string From,
            string Where,
            string Order,
            IReadOnlyList<(string Name, object Value)> Values)
        {
            /// <summary>A rule that needs no values of its own. It is named here, rather than written as
            /// an empty list at each site, so that a rule cannot pass null and mean this.</summary>
            public static readonly IReadOnlyList<(string Name, object Value)> Nothing = [];

            public void BindTo(SqliteCommand cmd)
            {
                ArgumentNullException.ThrowIfNull(cmd);
                foreach (var (name, value) in Values)
                    cmd.Parameters.AddWithValue(name, value);
            }
        }

        /// <summary>Whether a song's heart being turned over can change what is in this list.</summary>
        public abstract bool NeedsMarks { get; }

        /// <summary>
        /// Whether asking this question a second time can honestly return a different list.
        ///
        /// <para>The rule states this itself. The surface that offers it does not test it. The same is
        /// true of <see cref="NeedsMarks"/>. A list kept somewhere else, of "the ones that move", goes out
        /// of date quietly the first time a new rule is written that is not on it.</para>
        /// </summary>
        public abstract bool CanRegenerate { get; }

        /// <summary>
        /// The fragments this rule contributes and the values they need, as data.
        ///
        /// <para><b>Returns a value rather than writing onto the command. The gate is the reason.</b>
        /// Composing a mood list means building its pool, narrowing it to a genre, taking the window, and
        /// serialising the paths. Composing a decade's chart means reading the chart and picking its
        /// records. Measured over 19,747 tracks, that is 66 to 135 ms a list, while the SQL that follows
        /// takes about 3 ms. If a rule wrote onto the command, all of that would run inside
        /// <c>_readGate</c>. A pane that reads 84 subtitles would then hold the reading connection for
        /// about eleven seconds, and every other reader in the app would queue behind it. A rule that
        /// returns a value can be composed with no connection held, so the gate covers only the query.</para>
        ///
        /// <para>Returning the parameters instead of binding them keeps the two shapes interchangeable. A
        /// rule that needs the clock and a rule that needs two hundred paths are still the same object to
        /// everyone who reads one.</para>
        /// </summary>
        /// <param name="advance">Whether this read is a request for a NEW list. The other reads want the
        /// list as it stands: opening it, playing it, counting it for the pane's subtitle, or cutting it
        /// for the phone. Only the first moves the mood window on.</param>
        /// <param name="readings">How many lists' worth to take in one run. One for anything somebody is
        /// about to look at. More only for the cut sent to the phone, which is sent as a run and takes its
        /// own window of it (see <see cref="SmartPlaylists.ReadingsSent"/>). This is used by the rules that
        /// hold a POOL deeper than the list they show. The others give their whole answer at once, so there
        /// is nothing deeper for them to give.</param>
        public abstract Composed Compose(
            long nowUtcTicks, LibraryDatabase db, bool advance, int readings);

        /// <summary>
        /// A list the track rows can answer on their own.
        /// </summary>
        /// <param name="Join">What has to be joined to <c>tracks</c>, or empty when the track rows answer
        /// alone. The listening lists join a grouped read of <c>listen</c>.</param>
        /// <param name="Where">The condition, without the keyword.</param>
        /// <param name="Order">The ordering, without the keyword. It ends in a column that no two rows share,
        /// so two reads of the same rows cannot come back in different orders.</param>
        public sealed record Query(string Join, string Where, string Order) : SmartPlaylistRule
        {
            /// <summary>Whether the rule asks what time it is. This is read from the SQL, not declared beside
            /// it. A flag that says one thing while the SQL says another is a rule that silently stops being
            /// windowed.</summary>
            public bool NeedsNow => Join.Contains("$now", StringComparison.Ordinal)
                                 || Where.Contains("$now", StringComparison.Ordinal);

            /// <summary>Whether the rule reads the heart. This is read from the SQL, for the same reason as
            /// <see cref="NeedsNow"/>. A list kept elsewhere, of "the ones about favourites", goes quietly out
            /// of date the first time a rule is written that is not on it. What this decides (whether an open
            /// list re-reads itself when a song is loved) fails by simply not keeping up. That kind of error is
            /// never reported.</summary>
            public override bool NeedsMarks => Where.Contains("favorite", StringComparison.Ordinal);

            /// <summary>No, and nothing behind it could make it yes. The ORDER BY puts the songs worth having
            /// at the top, and the LIMIT is where the list stops. No deeper pool is being cut into by the limit,
            /// so a second reading of "the songs you marked a favourite" gives the same songs. Shuffling them
            /// is what the shuffle button is for.</summary>
            public override bool CanRegenerate => false;

            public override Composed Compose(
                long nowUtcTicks, LibraryDatabase db, bool advance, int readings) =>
                new($"tracks {Join}", Where, Order,
                    NeedsNow ? [("$now", nowUtcTicks)] : Composed.Nothing);
        }

        /// <summary>
        /// A list that had to be worked out over the whole collection before it could name a song.
        ///
        /// <para>The songs arrive as an ordered array of paths. They are handed to SQLite as one JSON
        /// parameter and joined through <c>json_each</c>. There is one parameter rather than one per path:
        /// the ceiling is <see cref="SmartPlaylists.MaxTracks"/>, and an IN list that long would be a query
        /// with two hundred bound variables in it. <c>json_each</c> also numbers the array, so the order the
        /// shelf ranked the songs in survives into the ORDER BY instead of being sorted away.</para>
        /// </summary>
        /// <param name="Kind">Which of the shelf's lists this is. It is resolved through
        /// <see cref="MoodShelf.ListFor"/>, so the mapping is stated in one place.</param>
        /// <param name="Genre">The genre it is narrowed to, or null for the whole list. It is carried on the
        /// rule rather than read from the kind, because the kind cannot say it: "calm" and "calm rock" are one
        /// <see cref="SmartPlaylistKind"/> and two lists.</param>
        public sealed record Shelf(SmartPlaylistKind Kind, string? Genre, int Decade) : SmartPlaylistRule
        {
            /// <summary>Nothing here reads the heart: a band is a fact about the audio.</summary>
            public override bool NeedsMarks => false;

            /// <summary>Yes. For example, fifty songs are shown from a pool of two hundred, and the window moves
            /// on when the button is pressed (see <see cref="LibraryDatabase.MoodWindow"/>). Whether this
            /// particular list has anything left to give is a question about the pool, not about the shape of
            /// the rule. It is asked in <see cref="SmartPlaylistCanRegenerate"/>.</summary>
            public override bool CanRegenerate => true;

            public override Composed Compose(
                long nowUtcTicks, LibraryDatabase db, bool advance, int readings)
            {
                ArgumentNullException.ThrowIfNull(db);

                var pool = Pool(db.SessionMoods, Kind, Genre, Decade, db.RecommendRefusalsSaid());

                // Each reading takes a different window of the pool. The pool itself belongs to the session
                // and does not move, so this changes WHICH of the eligible songs are shown, not which songs
                // are eligible. How many come back is the same either way. The record and artist caps are
                // applied HERE, inside the window, rather than to the pool. One song per album is a rule about
                // the list the listener sees. Applying it to the pool would put the album's other songs out of
                // reach for good.
                // Ordered to play rather than ranked to read. The songs returned are the same as Window would
                // give. Only their order differs. See MoodFlow for the reasoning. The keys are read here, per
                // list, because track_analysis fills in during the session, while the cut above does not change.
                // See LibraryDatabase.MusicalKeysOf.
                // A run for the phone, one list for anybody else. The caps are applied across the whole run. So
                // the run holds one song per record over all of it, and each reading in it is a different list.
                // The ORDER is settled one reading at a time. The first reading of a run is therefore the same
                // list, in the same order, as the one reading this screen asks for. See MoodPool.InFlow, where
                // both halves are written out.
                var songs = pool.InFlow(
                    SmartPlaylists.MostSongs(Kind),
                    readings,
                    db.MoodWindow(new SmartPlaylistId(Kind, Genre, Decade), advance)
                        + MoodSets.Turn(Kind, Genre),
                    db.MusicalKeysOf);

                // The path list must lead the FROM clause. The order is not a style choice.
                //
                // Written the other way round, as FROM tracks JOIN json_each(...), SQLite scans the whole
                // tracks table and checks the JSON array for each row. That is fifty times about twenty
                // thousand row visits to find fifty songs. Driven from json_each, it is fifty seeks on the
                // table's own primary key. Measured on a 20,066-track library with the SQLite version the app
                // ships (3.53.3):
                //
                //     50 paths    180.38 ms  ->  0.42 ms
                //     200 paths   476.08 ms  ->  0.96 ms
                //
                // The pane composes one of these per offered list, so the difference adds up to most of a
                // minute. The planner picks the wrong side because this database has no sqlite_stat1 table.
                // It has never been ANALYZEd, so the planner has to guess how many rows a virtual table holds.
                // Stating the order here stops it guessing.
                //
                // "1 = 1" rather than an empty string: Reachable appends " AND ..." to whatever this says, and
                // an empty WHERE would leave the query starting with AND.
                return new Composed(
                    "json_each($paths) shelf JOIN tracks ON tracks.relative_path = shelf.value",
                    "1 = 1",
                    "shelf.key",
                    [("$paths", JsonList(songs))]);
            }
        }

        /// <summary>
        /// Any list of the track rows, narrowed to the records of one genre.
        ///
        /// <para><b>A wrapper, not a fourth field on <see cref="Query"/>.</b> "Most played" and "most
        /// played rock" are the same question asked of different rows. The question does not change, only
        /// the rows it is asked of. If the genre were a field on the rule itself, every rule would have to
        /// carry a narrowing that it mostly does not use, and any rule could be built without one by
        /// mistake. Wrapped, a narrowed list is a different object, and there is exactly one place that
        /// makes one.</para>
        ///
        /// <para>The genre is a list of folders, not a condition on the tag. Which of the app's genres a
        /// tag belongs to is decided in code. See <see cref="LibraryDatabase.FoldersOnGenre"/>, where that
        /// is written out.</para>
        /// </summary>
        /// <param name="Asked">The question, unnarrowed. Everything about the list except which rows it may
        /// draw on is this.</param>
        /// <param name="Genre">One of <see cref="Metadata.Genres.All"/>.</param>
        public sealed record OnGenre(Query Asked, string Genre) : SmartPlaylistRule
        {
            /// <summary>The wrapped question's answers, both of them. Narrowing the rows changes which records
            /// are in the list, and nothing about what the list is. A genre-narrowed favourites list still
            /// changes when a heart is turned over.</summary>
            public override bool NeedsMarks => Asked.NeedsMarks;

            public override bool CanRegenerate => Asked.CanRegenerate;

            public override Composed Compose(
                long nowUtcTicks, LibraryDatabase db, bool advance, int readings)
            {
                ArgumentNullException.ThrowIfNull(db);

                var asked = Asked.Compose(nowUtcTicks, db, advance, readings);
                var folders = JsonList(db.FoldersOnGenre(Genre));

                // Appended to whatever the question already joins, rather than folded into its WHERE. An inner
                // join against a list is how every other rule here narrows to a set of rows. It also keeps the
                // question's own condition readable as the question.
                return asked with
                {
                    From = asked.From + "\nJOIN json_each($folders) genre ON genre.value = tracks.folder",
                    Values = [.. asked.Values, ("$folders", (object)folders)],
                };
            }
        }

        /// <summary>
        /// A decade's chart: the songs of it that the world plays, with the records taking turns.
        ///
        /// <para><b>Not a <see cref="Query"/>.</b> As SQL it would be an ORDER BY on <c>world_plays</c> and
        /// a LIMIT, which is what a chart is. But the chart of one collection is mostly made of its albums.
        /// For example, in one shelf's 1960s, sixteen of the fifty songs came from Abbey Road, eleven came
        /// from Sounds of Silence, and five records held all fifty. The ranking would be right, but the list
        /// would be unreadable.</para>
        ///
        /// <para><b>A turn each, rather than one each.</b> The mood lists cap a record at one song, because
        /// they draw on a deep pool. A decade cannot do that. A collection may hold only a few records from
        /// a decade that the world plays at all. A cap would leave a short list, and the row would fall under
        /// <see cref="SmartPlaylists.LeastToShow"/> and stop being drawn. Taking turns keeps the fifty and
        /// moves the repeats to the bottom. See <see cref="RecordTurns"/>.</para>
        ///
        /// <para>Composed as a list of paths, the same way <see cref="Shelf"/> is composed, because the order
        /// is decided in code and SQL is only asked to fetch the rows.</para>
        /// </summary>
        public sealed record Chart(int Decade) : SmartPlaylistRule
        {
            /// <summary>Nothing here reads the heart. What the world plays is not the listener's opinion of
            /// it.</summary>
            public override bool NeedsMarks => false;

            /// <summary>No. The chart is the chart. A second reading of "what the world played in the seventies"
            /// gives the same songs, and shuffling them is what the shuffle button is for. This is the same
            /// answer <see cref="Query"/> gives, for the same reason.</summary>
            public override bool CanRegenerate => false;

            public override Composed Compose(
                long nowUtcTicks, LibraryDatabase db, bool advance, int readings)
            {
                ArgumentNullException.ThrowIfNull(db);

                var songs = RecordTurns.Taken(db.QueryDecadeChart(Decade), song => song.Folder)
                    .Take(SmartPlaylists.MostSongs(SmartPlaylistKind.Decade))
                    .Select(song => song.RelativePath)
                    .ToList();

                // Driven from the path list, for the reason Shelf.Compose gives and measures.
                return new Composed(
                    "json_each($paths) chart JOIN tracks ON tracks.relative_path = chart.value",
                    "1 = 1",
                    "chart.key",
                    [("$paths", JsonList(songs))]);
            }
        }

        /// <summary>
        /// What to put on now: loved and much-played songs, leaning towards the kinds of music this
        /// listener plays at this time of day.
        ///
        /// <para><b>Not a <see cref="Query"/>, and it could not be one.</b> Two of the three facts it
        /// combines are in the track rows. The third is a roll-up of the listening log for each part of the
        /// day, with a weight per genre. No WHERE clause reaches that, and no ORDER BY can add three numbers
        /// that live in three places. The pool is narrowed in SQL, by loved or played, which is a condition.
        /// Everything after that is <see cref="HourlyMix"/>.</para>
        ///
        /// <para>Composed as a list of paths, the same way <see cref="Shelf"/> and <see cref="Chart"/>
        /// are, and driven from that list for the reason measured there.</para>
        /// </summary>
        public sealed record Hourly : SmartPlaylistRule
        {
            /// <summary>Yes. A heart is one of the three facts it ranks on, so a song loved while the list is open
            /// belongs in it straight away.</summary>
            public override bool NeedsMarks => true;

            /// <summary>No. Asked twice in the same hour, it returns the same list, because every fact behind it is
            /// the same. This is the same answer <see cref="Query"/> and <see cref="Chart"/> give. The list changes
            /// when the hour does, and that is not a button.</summary>
            public override bool CanRegenerate => false;

            public override Composed Compose(
                long nowUtcTicks, LibraryDatabase db, bool advance, int readings)
            {
                ArgumentNullException.ThrowIfNull(db);

                // The local hour, taken from the clock this rule was given, not from DateTime.Now. A rule that
                // answers differently on two calls in the same second cannot be tested, and QuerySmartPlaylist
                // already follows this rule. The log's own hours are local too (listen.local_hour), so the two agree.
                var hour = new DateTime(nowUtcTicks, DateTimeKind.Utc).ToLocalTime().Hour;

                // The same genre table the profile was built from. This and the weights then cannot disagree about
                // what kind of music a record is.
                var songs = HourlyMix.Songs(
                    db.QueryMixSongs(),
                    MoodBands.GenreByFolder(db.QueryAlbumGenreCounts(), db.CatalogueFactsByFolder()),
                    db.SessionProfile.GenresAt(DayParts.Of(hour)),
                    SmartPlaylists.MostSongs(SmartPlaylistKind.RightNow),
                    SmartPlaylists.MostFromOneRecord);

                return new Composed(
                    "json_each($paths) mix JOIN tracks ON tracks.relative_path = mix.value",
                    "1 = 1",
                    "mix.key",
                    [("$paths", JsonList(songs))]);
            }
        }

        /// <summary>
        /// Which of a mood's songs a narrowing means: one decade, one genre, or all of them.
        ///
        /// <para>This is said once and read twice: by <see cref="Shelf.Compose"/>, which builds the list, and
        /// by <see cref="SmartPlaylistCanRegenerate"/>, which asks how deep the pool behind it is. If it were
        /// written twice, the two could drift apart. The drift would show as a button offering a new list
        /// that the list itself cannot produce.</para>
        /// </summary>
        /// <param name="moods">The session's whole cut, not one band of it. A narrowed list may draw from
        /// squares in other bands (see <see cref="MoodSets"/>). Passing the whole shelf, rather than one
        /// <see cref="MoodList"/>, is what allows that. The band a list leads on is still
        /// <paramref name="kind"/>, and is what a whole-shelf or decade reading uses.</param>
        /// <param name="refusals">The acts and records this kind of list has been told to stop offering. There
        /// is no default: a pool built without them would have forgotten, and a band the listener refused could
        /// show up in the list again. Pass <see cref="RecommendRefusals.None"/> to mean that nobody has refused
        /// anything.</param>
        public static MoodPool Pool(
            MoodShelf moods, SmartPlaylistKind kind, string? genre, int decade,
            RecommendRefusals refusals)
        {
            ArgumentNullException.ThrowIfNull(moods);

            if (decade > 0)
                return moods.ListFor(kind).InDecade(decade).Allowed(refusals, kind);

            if (genre is null)
                return moods.ListFor(kind).WholeList.Allowed(refusals, kind);

            // The squares this list draws from. For most lists this is only its own square. They are gathered
            // by band. The cells of one band are one narrowing of one ranked list. Taking them separately would
            // rank each shelf against itself, and then interleave three lists that were never compared. Across
            // bands there is nothing to compare, which is what InTurn is for. See MoodPool.InTurn.
            //
            // The flavour is taken from the lead, not from each cell. A set is one named list, and the styles
            // are what that name promises. If it were asked per cell, "Jazz Club" would prefer soft rock inside
            // its soul and quiet storm inside its blues. That is three lists under one name.
            var prefer = MoodFlavour.Styles(kind, genre);
            var byBand = MoodSets.CellsFor(kind, genre)
                                 .GroupBy(c => c.Kind)
                                 .Select(band => moods.ListFor(band.Key)
                                                      .Within([.. band.Select(c => c.Genre)], prefer))
                                 .ToList();

            // Refused after the merge, not before. A refusal is about the list in front of the listener. Taking
            // it off each band separately would let a refused act's slot be filled from the band beside it. This
            // is the same fault that the note on Allowed describes.
            return MoodPool.InTurn(byBand).Allowed(refusals, kind);
        }

        public static SmartPlaylistRule For(SmartPlaylistId id) => id.Kind switch
        {
            // The track rows answer these on their own.

            // Newest first. A list of songs somebody loved reads best in the order they were loved. The path
            // breaks the tie for rows favourited before the timestamp existed.
            SmartPlaylistKind.FavoriteTracks => Narrowed(id, new Query(
                "", "favorite = 1", "favorited_utc_ticks DESC, relative_path ASC")),

            // The most often played first, then the most recently played. Among songs played the same number of
            // times, the one still being played leads. This stops a list of all-time counts from freezing into
            // what was played two years ago. Never played is not "played least". It is not an answer to the
            // question at all, which is what `plays > 0` says.
            SmartPlaylistKind.MostPlayed => Narrowed(id, new Query(
                "", "plays > 0", "plays DESC, last_played_utc_ticks DESC, relative_path ASC")),

            // Rows that predate the column were given the file's modified time, so this is
            // accurate on an old mirror. A row that is still zero has never had anything recorded.
            SmartPlaylistKind.RecentlyAdded => Narrowed(id, new Query(
                "", "added_utc_ticks > 0", "added_utc_ticks DESC, relative_path ASC")),

            SmartPlaylistKind.RecentlyPlayed => new Query(
                "", "plays > 0", "last_played_utc_ticks DESC, relative_path ASC"),

            // Newest first. The record bought last week and not yet heard is the one somebody means to play. A
            // record that has sat there for six years is not news.
            SmartPlaylistKind.NeverPlayed => Narrowed(id, new Query(
                "", "plays = 0", "added_utc_ticks DESC, relative_path ASC")),

            // The longest silent comes first. A favourite that was never played at all has a timestamp of zero,
            // so it leads. That is right: it is the strongest form of the same fact.
            SmartPlaylistKind.LovedAndForgotten => new Query(
                "",
                $"favorite = 1 AND last_played_utc_ticks < $now - {Days(SmartPlaylists.ForgottenDays)}",
                "last_played_utc_ticks ASC, relative_path ASC"),

            SmartPlaylistKind.PlayedNotLoved => new Query(
                "",
                $"favorite = 0 AND plays >= {SmartPlaylists.PlayedNotLovedPlays}",
                "plays DESC, last_played_utc_ticks DESC, relative_path ASC"),

            // These ask the listening log.

            SmartPlaylistKind.OnRepeat => new Query(
                Counted(SmartPlaylists.RecentDays),
                $"l.plays >= {SmartPlaylists.OnRepeatPlays}",
                "l.plays DESC, l.last_at DESC, tracks.relative_path ASC"),

            // Played properly, then not for months. The window is longer than the others', because the question
            // itself is about a while ago.
            SmartPlaylistKind.Rediscover => new Query(
                Counted(SmartPlaylists.RediscoverDays),
                $"l.plays >= {SmartPlaylists.RediscoverPlays} "
                    + $"AND l.last_at < $now - {Days(SmartPlaylists.ForgottenDays)}",
                "l.plays DESC, l.last_at DESC, tracks.relative_path ASC"),

            // Not "played once at midnight". It means played at night, as most of what happens to it does. The
            // share is written as integers, so no rounding decides who is in: three fifths, times five.
            SmartPlaylistKind.LateNight => new Query(
                Counted(SmartPlaylists.HistoryDays),
                $"l.night >= {SmartPlaylists.LateNightListens} AND l.night * 5 >= l.plays * 3",
                "l.night DESC, l.plays DESC, tracks.relative_path ASC"),

            // The first time the log ever saw the song. On an upgraded mirror the log begins on the day of the
            // upgrade, so for the first month this reads "songs played since the log began". That is why the list
            // is not offered until it has a few rows in it (see SmartPlaylists.LeastToShow).
            SmartPlaylistKind.FirstListens => new Query(
                Counted(SmartPlaylists.HistoryDays),
                $"l.first_at >= $now - {Days(SmartPlaylists.RecentDays)}",
                "l.first_at DESC, tracks.relative_path ASC"),

            // Skipped at least twice as often as it is played, and skipped more than a couple of times. One bad
            // evening is not a verdict. This is the only list that uses skips. Skips are written down even though
            // they do not count as plays, which is why this list can be built at all.
            SmartPlaylistKind.KeepSkipping => new Query(
                Counted(SmartPlaylists.HistoryDays),
                $"l.skips >= {SmartPlaylists.SkipsBeforeItShows} AND l.skips >= l.plays * 2",
                "l.skips DESC, l.plays ASC, tracks.relative_path ASC"),

            // These three are worked out over the whole shelf, not asked of the rows.
            //
            // Each names which of the session's three lists it is and nothing else. Where the cut falls, and when
            // it is taken, are MoodShelf's business. A rule that also decided that would write the recipe down in
            // two places, which is what SmartPlaylistRule exists to prevent.

            SmartPlaylistKind.Calm => new Shelf(id.Kind, id.Genre, id.Decade),
            SmartPlaylistKind.Energetic => new Shelf(id.Kind, id.Genre, id.Decade),
            SmartPlaylistKind.Danceable => new Shelf(id.Kind, id.Genre, id.Decade),
            SmartPlaylistKind.Focus => new Shelf(id.Kind, id.Genre, id.Decade),

            // A whole genre, rather than a question about it. The tag is read with LIKE and not matched through
            // Genres, on purpose. The folded label loses "Classical Crossover" and "Neoclassical", and this is the
            // one list where those belong.
            //
            // Ordered by the record, and then by where the track sits on it, so a symphony arrives whole and in its
            // own order. This is why it is a plain rule and not a mood band. The path breaks the tie for any track
            // with no track number.
            SmartPlaylistKind.Classical => new Query(
                "",
                "genre IS NOT NULL AND genre LIKE '%lassical%'",
                "folder ASC, disc_number ASC, track_number ASC, relative_path ASC"),

            // This one is not about the listener at all.
            //
            // The world's favourite songs from one decade, out of the records this shelf holds. This is a plain
            // question of the track rows. The year gives the decade, and world_plays gives how much of the world
            // plays the song (see LibraryDatabase.Popularity). The list is ordered by that and stops at fifty,
            // which is what a chart is.
            //
            // "world_plays > 0" rather than IS NOT NULL. Zero is a song the world was asked about and does not play.
            // Including those would make a top fifty of songs nobody listens to. Null is a song nobody has asked
            // about yet, and it is not in the chart either. The list fills in behind the sweep, so it does not claim
            // an answer it does not have yet.
            SmartPlaylistKind.Decade => new Chart(id.Decade),

            // This one combines all three.
            //
            // What the listener loves, what they play, and what they play at this hour, in one ranking. Neither a
            // genre nor a decade narrows it. The hour already decides which kinds of music it leans on. A "right now
            // rock" list would be that decision overruled by hand.
            SmartPlaylistKind.RightNow => new Hourly(),

            // Absolute, not cut within anything, so it takes neither a genre nor a decade, even if one is passed to
            // it. See SmartPlaylists.BrokenOutByGenre and BrokenOutByEra, which stop the pane offering either.
            SmartPlaylistKind.Sleep => new Shelf(id.Kind, null, 0),

            _ => throw new ArgumentOutOfRangeException(
                nameof(id), id, "This generated playlist has no rule yet."),
        };

        /// <summary>
        /// The same question, held to one genre's records where the list being built is a genre's.
        ///
        /// <para>This is the one place a <see cref="Query"/> is ever wrapped. It is a step in the table
        /// rather than a rule of its own. So the four kinds that have a per-genre form say so where they are
        /// defined, and the other kinds cannot grow one by accident, because they never call this. Which four
        /// is <see cref="SmartPlaylists.EssentialForAGenre"/>. The two must agree: this decides what the
        /// database can build, and that decides what the surfaces may ask for.</para>
        ///
        /// <para>A genre passed to a kind that does not call this is ignored, the same way a decade passed to
        /// <see cref="SmartPlaylistKind.Sleep"/> is. See the note there.</para>
        /// </summary>
        private static SmartPlaylistRule Narrowed(SmartPlaylistId id, Query asked)
            => id.Genre is { } genre ? new OnGenre(asked, genre) : asked;

        /// <summary>
        /// One track's listening, grouped, over the last <paramref name="days"/>. This is the join that every
        /// log-reading rule uses.
        ///
        /// <para>Every rule uses the same shape, on purpose. The group is bounded by an indexed range, so the
        /// pane's reload cost depends on how recently somebody has been listening, not on how long they have
        /// owned the app. Every rule then reads the same set of numbers.</para>
        /// </summary>
        private static string Counted(int days) =>
            $"""
            JOIN (
                SELECT relative_path AS path,
                       SUM(CASE WHEN counted = 1 THEN 1 ELSE 0 END) AS plays,
                       SUM(CASE WHEN counted = 0 THEN 1 ELSE 0 END) AS skips,
                       SUM(CASE WHEN counted = 1 AND (local_hour >= {SmartPlaylists.NightFromHour}
                                                      OR local_hour < {SmartPlaylists.NightUntilHour})
                                THEN 1 ELSE 0 END) AS night,
                       MIN(CASE WHEN counted = 1 THEN at_utc_ticks END) AS first_at,
                       MAX(CASE WHEN counted = 1 THEN at_utc_ticks END) AS last_at
                FROM listen
                WHERE at_utc_ticks >= $now - {Days(days)}
                GROUP BY relative_path
            ) l ON l.path = tracks.relative_path
            """;

        /// <summary>A number of days as ticks, written into the SQL. It is not a parameter, because it is a
        /// constant of the rule and not something a caller chooses.</summary>
        private static string Days(int days) => (days * TimeSpan.TicksPerDay).ToString(
            System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The songs one generated playlist stands for, in its own order, capped at
    /// <paramref name="limit"/>. Nothing is stored. The list is this query, run again each time the row is
    /// opened, so it can never disagree with its own rule.
    /// </summary>
    /// <param name="nowUtcTicks">What time it is, for the kinds whose question has a window in it. It is not
    /// read from the clock in here. A query that answers differently on two calls in the same second cannot
    /// be tested, and the pane needs one reading across all of its rows.</param>
    /// <param name="advance">
    /// Whether somebody is asking for a NEW list, as opposed to reading the one that is there.
    ///
    /// <para><b>No default, on purpose.</b> A mood list shows a window of its pool and moves that window
    /// when this is true, so the list comes back different. Exactly one caller passes true: the "generate a
    /// new list" button. Everything else reads the list as it stands: opening it, playing it from its row,
    /// counting it for a subtitle, or cutting it for the phone. A default here would make forgetting the
    /// question the way to change somebody's list.</para>
    /// </param>
    public IReadOnlyList<LibraryTrack> QuerySmartPlaylist(
        SmartPlaylistId id, long nowUtcTicks, bool advance, int limit = SmartPlaylists.MaxTracks)
        => QueryRun(id, nowUtcTicks, advance, readings: 1, limit);

    /// <summary>
    /// The same list, several readings deep, in one run.
    ///
    /// <para><b>What the phone is sent.</b> The phone cannot cut a list of its own. The arithmetic is
    /// percentiles over a collection it does not have. So it is sent more than it shows, and it takes its
    /// own window of the run when somebody presses refresh there. See
    /// <see cref="SmartPlaylists.ReadingsSent"/>, where the count and its effects are written down.</para>
    ///
    /// <para>Never advances. A run is a reading of the list as it stands, and the phone's window belongs to
    /// the phone. If a push stepped this computer's lists, what is on this screen would change because
    /// something was sent somewhere else.</para>
    ///
    /// <para>A kind with no pool behind it returns its ordinary list, however many readings are asked for,
    /// because there is nothing deeper to give.</para>
    /// </summary>
    public IReadOnlyList<LibraryTrack> QuerySmartPlaylistRun(
        SmartPlaylistId id, long nowUtcTicks, int readings)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(readings, 1);

        return QueryRun(
            id, nowUtcTicks, advance: false, readings,
            limit: SmartPlaylists.MostSongs(id.Kind) * readings);
    }

    private IReadOnlyList<LibraryTrack> QueryRun(
        SmartPlaylistId id, long nowUtcTicks, bool advance, int readings, int limit)
    {
        // Composed before the gate is taken, never inside it. See SmartPlaylistRule.Compose for what
        // it costs when it is the other way round.
        var composed = SmartPlaylistRule.For(id).Compose(nowUtcTicks, this, advance, readings);

        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            composed.BindTo(cmd);
            // tracks.* rather than *: a join brings its own columns, and ReadTrack reads by name.
            cmd.CommandText =
                $"SELECT tracks.* FROM {composed.From} WHERE {composed.Where}{Offerable(id.Kind)} " +
                $"ORDER BY {composed.Order} LIMIT $limit";
            // The kind's own ceiling as well as the caller's. A caller that asks for more than a list
            // allows gets the list's own number, not the number it asked for.
            cmd.Parameters.AddWithValue(
                "$limit", Math.Clamp(limit, 0, SmartPlaylists.MostSongs(id.Kind) * readings));

            using var reader = cmd.ExecuteReader();
            var tracks = new List<LibraryTrack>();
            while (reader.Read())
                tracks.Add(ReadTrack(reader));
            return tracks;
        }
    }

    /// <summary>
    /// Whether a song's heart being turned over can change what is in this generated list.
    ///
    /// <para>Asked by the playlist pane, which re-reads an open list on a mark only when the answer is
    /// yes. That is three of the twelve kinds. The other nine are questions about listening. Repainting
    /// one of them because a song somewhere was loved would make the pane flicker for nothing.</para>
    /// </summary>
    public static bool SmartPlaylistNeedsMarks(SmartPlaylistKind kind) =>
        SmartPlaylistRule.For(SmartPlaylistId.WholeShelf(kind)).NeedsMarks;

    /// <summary>
    /// Whether asking for this list again would return a different list. The surface uses this to decide
    /// whether to offer a way to ask.
    ///
    /// <para>Two things have to be true, and neither implies the other. First, the RULE must be one whose
    /// answer moves. The mood lists show fifty out of a pool and move the window when asked. Every list
    /// asked of the track rows is its own whole answer and comes back identical. Second, this particular
    /// POOL must be deeper than the list cut out of it. A window of fifty over a pool of forty is the same
    /// forty songs starting in a different place. A button promising a new list would then be describing
    /// a rotation.</para>
    ///
    /// <para>It takes the whole <see cref="SmartPlaylistId"/> rather than a kind, because the pool is the
    /// narrowed one. "calm" may have four hundred songs behind it while "calm rock" has thirty. Those are
    /// two different answers to this question.</para>
    /// </summary>
    public bool SmartPlaylistCanRegenerate(SmartPlaylistId id)
    {
        if (!SmartPlaylistRule.For(id).CanRegenerate)
            return false;

        // Safe to reach the shelf only because the line above has already refused every kind that is
        // not one of its lists. MoodShelf.ListFor has no arm for the others and would throw.
        var pool = SmartPlaylistRule.Pool(
            SessionMoods, id.Kind, id.Genre, id.Decade, RecommendRefusalsSaid());

        // Compared with what the list ACTUALLY SHOWS, not with the most it is ever allowed to show. That
        // difference is the whole reason this method is right.
        //
        // A list stops at MostSongs, but it usually stops well before that. One song per record and five per
        // act (see MoodPool.Window) mean a narrow shelf runs out of records long before it runs out of songs.
        // For example, the adrenaline soul list draws on a band of 71 songs and shows 11 of them, because those
        // 71 sit on about eleven albums. Compared with the ceiling, a pool of fifty behind a list of eleven
        // answers "nothing more to give", although thirty-nine songs in it have never been shown. The button
        // was then hidden on exactly the lists that had the most left to offer.
        //
        // The window is read WITHOUT advancing. This is a question about the list, and asking it must not
        // move the reader on to the next one.
        var shown = pool.Window(
            SmartPlaylists.MostSongs(id.Kind),
            MoodWindow(id, advance: false) + MoodSets.Turn(id.Kind, id.Genre)).Count;
        return pool.Count > shown;
    }

    /// <summary>
    /// How many songs a generated playlist holds and how long they play. The playlist pane's subtitle
    /// counts them this way, and the count decides whether the row is drawn at all.
    ///
    /// <para>Counted through the same ceiling the list stops at. The subtitle describes the list the row
    /// opens, not how many songs in the collection happen to satisfy the rule. The ceiling is not a
    /// parameter for that reason. Two limits would be two ways for the count and the list to disagree, and
    /// there is one right answer here.</para>
    /// </summary>
    public (int Tracks, double Seconds, bool Explicit) SmartPlaylistTotals(
        SmartPlaylistId id, long nowUtcTicks)
    {
        var (tracks, seconds, marked, _) = Totals(id, nowUtcTicks, withGenres: false);
        return (tracks, seconds, marked);
    }

    /// <summary>
    /// The same pass, plus what the list is made of: the raw genre tag of every song in it.
    /// </summary>
    /// <remarks>
    /// This is for the sentence under a mood list's name, which says which part of somebody's collection the
    /// mood drew from. The home screen writes it under a list card, and the playlist page writes it under the
    /// same list's title. Both fold the tags in the same way. See <c>MoodBlurb.Spoken</c>.
    ///
    /// <para><b>Why it is here and not in a query of its own.</b> Composing a mood list means building its
    /// pool, narrowing it, taking the window and serialising the paths. Measured at 66 to 135 ms a list. A
    /// second method would compose again and take the reading gate again, for a fact this pass already has.
    /// The genres come from the same composition and the same gate. See the note in <see cref="Totals"/>.</para>
    ///
    /// <para>Raw tags in list order, not folded and not counted. Which of them are worth naming is
    /// <c>MoodBlurb</c>'s decision, and it gives the same answer on both screens. If the database decided it
    /// too, that rule would be written down twice.</para>
    /// </remarks>
    public (int Tracks, bool Explicit, IReadOnlyList<string?> Genres) SmartPlaylistCard(
        SmartPlaylistId id, long nowUtcTicks)
    {
        var (tracks, _, marked, genres) = Totals(id, nowUtcTicks, withGenres: true);
        return (tracks, marked, genres);
    }

    /// <summary>
    /// What a generated list holds, from one composition and one take of the reading gate.
    /// </summary>
    /// <param name="withGenres">Whether to read each song's genre tag as well. This is stated at both callers
    /// and never defaulted. It is a second result set over the same rows. A caller that got it by forgetting
    /// would pay for a read it does not use, about eighty times in the pane.</param>
    private (int Tracks, double Seconds, bool Explicit, IReadOnlyList<string?> Genres) Totals(
        SmartPlaylistId id, long nowUtcTicks, bool withGenres)
    {
        // Not an ask. A subtitle counts the list somebody would get, and it must not use up the window that
        // the button is about to need.
        //
        // Composed before the gate is taken, never inside it. This method is where that was shown to matter.
        // The playlist pane asks it once per offered list, 84 times on this collection. While composition ran
        // under the gate, the reading connection was held for about eleven seconds, against about 300 ms of
        // actual query time.
        var composed = SmartPlaylistRule.For(id).Compose(nowUtcTicks, this, advance: false, readings: 1);

        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            composed.BindTo(cmd);
            // The explicit flag comes from the SAME pass. A generated list is a query, not songs somebody put
            // somewhere. So the only way to know whether it holds an explicit song is to look at what it would
            // return, and this pass is already looking. Asked separately, it would be a second composition and a
            // second take of the reading gate for each list, about eighty times.
            //
            // MAX over the advisory rather than a second COUNT. The question is whether there is one at all. The
            // COALESCE handles a list with no rows, where MAX returns null.
            //
            // The genres, where they are wanted, are a SECOND STATEMENT over the same rows, not a fourth column of
            // this one. The rows are one per song, and the rest is one row per list. Folding them together would
            // need a group_concat that the caller then has to split apart. It is still one command, so it is one
            // round trip and one gate.
            var rows =
                $"""
                    SELECT tracks.duration_seconds, COALESCE(tracks.advisory, 0) AS advisory,
                           tracks.genre AS genre
                      FROM {composed.From}
                     WHERE {composed.Where}{Offerable(id.Kind)} ORDER BY {composed.Order} LIMIT $limit
                """;

            cmd.CommandText =
                $"""
                SELECT COUNT(*), COALESCE(SUM(duration_seconds), 0),
                       COALESCE(MAX(advisory = {(int)Models.ContentAdvisory.Explicit}), 0)
                  FROM (
                {rows}
                );
                """
                + (withGenres ? $"SELECT genre FROM (\n{rows}\n);" : "");

            // The same ceiling the list stops at, so the subtitle counts the songs the row opens.
            cmd.Parameters.AddWithValue("$limit", SmartPlaylists.MostSongs(id.Kind));

            using var reader = cmd.ExecuteReader();

            var totals = reader.Read()
                ? (Tracks: Convert.ToInt32(reader.GetValue(0)),
                   Seconds: Convert.ToDouble(reader.GetValue(1)),
                   Explicit: Convert.ToInt32(reader.GetValue(2)) != 0)
                : (Tracks: 0, Seconds: 0d, Explicit: false);

            if (!withGenres)
                return (totals.Tracks, totals.Seconds, totals.Explicit, []);

            var genres = new List<string?>(totals.Tracks);
            if (reader.NextResult())
                while (reader.Read())
                    genres.Add(reader.IsDBNull(0) ? null : reader.GetString(0));

            return (totals.Tracks, totals.Seconds, totals.Explicit, genres);
        }
    }
}
