namespace RPM.Core.Library;

/// <summary> A playlist the library writes for itself, from what it already knows, rather than one that
/// somebody filled by hand.
///
/// <para>Each kind is a question whose answer changes: the songs marked as favourites, the songs played
/// most, the songs loved and then forgotten. Nothing is stored. There is no <c>pl_node</c> row, so there
/// is nothing to keep in step. A generated list cannot go stale, and it cannot be edited into disagreeing
/// with its own rule. It is read again each time it is opened.</para>
///
/// <para>To add a kind, add a member here and an arm in each switch that reads one. The switches are the
/// rule in <c>LibraryDatabase.SmartPlaylistRule</c>, the least it is worth showing in
/// <see cref="SmartPlaylists.LeastToShow"/>, and the name and glyph in the pane. None of these switches
/// has a fallback arm. So the compiler lists every one that needs the new kind, and a new kind cannot
/// quietly answer with an old kind's query.</para>
/// </summary>
public enum SmartPlaylistKind
{
    // ── what the track rows already say ──

    /// <summary>Every song the listener has marked a favourite, the most recently marked first.</summary>
    FavoriteTracks,

    /// <summary>The songs played most, the most played first. Songs never played are not in it.</summary>
    MostPlayed,

    /// <summary>The songs that joined the collection most recently.</summary>
    RecentlyAdded,

    /// <summary>The songs played most recently, whenever that was.</summary>
    RecentlyPlayed,

    /// <summary>Music the collection holds that has never been played once.</summary>
    NeverPlayed,

    /// <summary>Favourites nobody has played in half a year, longest-silent first.</summary>
    LovedAndForgotten,

    /// <summary>Played often, but never marked as a favourite. These are the hearts the listener forgot
    /// to give.</summary>
    PlayedNotLoved,

    // ── what the listening log says ──

    /// <summary>Played several times in the last month.</summary>
    OnRepeat,

    /// <summary>Played a lot once and not for months: the records worth putting on again.</summary>
    Rediscover,

    /// <summary>The songs whose listening happens at night, not merely the ones heard once at
    /// night.</summary>
    LateNight,

    /// <summary>Songs heard for the first time in the last month.</summary>
    FirstListens,

    /// <summary>The songs that get skipped far more often than they get played.</summary>
    KeepSkipping,

    // ── what the audio says ──
    //
    // These three are not questions about the track rows. No WHERE clause can answer them. Each band is a
    // percentile taken within the record's own genre, over a distribution held in memory. They are answered
    // from the shelf instead. See MoodShelf and LibraryDatabase.SmartPlaylistRule.Shelf.

    /// <summary>The quiet, still end of every kind of music the collection holds. It is calm for its own
    /// genre, so a shelf of techno and classical contributes both, rather than only the
    /// classical.</summary>
    Calm,

    /// <summary>The loud, busy end of every kind of music the collection holds.</summary>
    Energetic,

    /// <summary>A lot of events over a level that barely moves: the records that keep a floor going. It
    /// sits beside the two bands rather than between them, so a song can be here and in
    /// neither.</summary>
    Danceable,

    /// <summary>The same still level with little happening over it: music to work through. It is the
    /// other half of the evenness axis that <see cref="Danceable"/> reads, and it never holds the same
    /// records as Danceable.
    /// <para>It is not a quieter Calm. That is why it is a kind of its own, and not a narrower band. A
    /// steady loop at full tilt is nothing like calm, and it is exactly what this list is for.</para>
    /// </summary>
    Focus,

    /// <summary>
    /// The collection's classical music, longest works first.
    ///
    /// <para><b>Why it is here and not a mood row.</b> Classical never appeared under Calm, Energetic or
    /// Danceable, and it never could. Those are cut within a genre. A genre with only a few measured
    /// tracks makes a band below any useful floor. A genre that small cannot answer a percentile
    /// question. It can still answer "show me the classical".</para>
    ///
    /// <para><b>A plain rule gives classical what it most needs.</b> The mood lists take at most one song
    /// from any record (<see cref="SmartPlaylists.MostFromOneRecord"/>). For a symphony, that means one
    /// movement. This list asks the track rows directly, so a work arrives whole and in order.</para>
    ///
    /// <para>It is read from the genre tag, not from the folded shelf label. So a record tagged
    /// "Classical Crossover" or "Neoclassical" is in it. This is the reading that <c>Genres.Match</c>
    /// would otherwise fold away.</para>
    /// </summary>
    Classical,

    // ── what the world says ──

    /// <summary>
    /// The songs of one decade that the world plays most.
    ///
    /// <para><b>The one list here that is not about this listener at all.</b> Every other list is a
    /// question about the collection, or about what has been played in it. This one asks what the WORLD
    /// plays, from the records that happen to be on the shelf. That is what makes a decade list worth
    /// having. A shelf's seventies can be a few hundred songs, and most of them are album tracks. The
    /// question people mean by "the seventies" is about the songs everybody knows.</para>
    ///
    /// <para>It is ranked by <c>tracks.world_plays</c>. An unhurried pass fills that column from
    /// ListenBrainz. So the list is empty on a fresh install, and it fills in over the following days, as
    /// the mood lists do behind the measuring pass. A song that nobody has asked about yet is not in it,
    /// because null is not zero.</para>
    ///
    /// <para>It is not cut within a genre and not cut by mood. A decade is the whole of it. The decade
    /// itself is held in <see cref="SmartPlaylistId.Decade"/>.</para>
    /// </summary>
    Decade,

    /// <summary>
    /// Quiet, dark and without a swing in it: music to fall asleep to.
    ///
    /// <para>This is the one mood that is NOT cut within a genre. It could not be. Calm asks where a
    /// record sits among its own kind, so the calmest techno on the shelf would be calm and would belong
    /// in that list. Nobody sleeps through techno. This list asks an absolute question of the whole
    /// collection. That is why it has no per-genre lists under it, while the other moods do.</para>
    /// </summary>
    Sleep,

    // ── what all three say at once ──

    /// <summary> What to put on now: the songs the listener loves and the songs they play, ranked towards
    /// the kinds of music they play at THIS time of day.
    ///
    /// <para><b>The only list here that is a ranking rather than a question.</b> Every other kind has a
    /// yes or a no behind it: favourite, played this month, calm for its genre. This one combines three
    /// facts that can disagree. The answer to "what do I put on at eleven at night" is not any one of
    /// them. A favourite in the wrong kind of music for the hour is still a favourite. A song played
    /// forty times in the genre they play every evening beats both. Written as an AND of three
    /// conditions, the list would usually be empty. See <see cref="HourlyMix"/>, which does the
    /// arithmetic.</para>
    ///
    /// <para>This kind is last in the enum on purpose. The values are stored as integers.
    /// <c>track_verdict.kind</c> holds one per row, so a kind inserted anywhere else would renumber every
    /// verdict already stored.</para>
    /// </summary>
    RightNow,
}

/// <summary>
/// WHICH generated playlist: the kind, and the genre it is narrowed to.
///
/// <para>A kind alone stopped being enough as soon as the mood lists could be broken out by genre. "Calm"
/// and "calm rock" are the same question asked of different shelves. An enum has no room for the shelf,
/// so this pair is a type. It is the only way to name a generated list.</para>
///
/// <para><b>Every genre is a stated answer.</b> <see cref="WholeShelf"/> says "all of them" in code, so
/// no call site can mean the whole list by forgetting an argument. This is the same rule that
/// <c>LibraryDatabase.WholeQueue</c> follows, for the same reason. A narrowing that defaults to absent is
/// one that someone will forget. When that happens, it fails silently, in the direction of showing too
/// much.</para>
/// </summary>
/// <param name="Kind">Which question is being asked.</param>
/// <param name="Genre">The genre it is narrowed to, one of <see cref="Metadata.Genres.All"/>, or null for
/// the whole shelf. Only the kinds in <see cref="SmartPlaylists.BrokenOutByGenre"/> may carry a genre.
/// These are the moods that the playlists tree nests by genre. So may the kinds in
/// <see cref="SmartPlaylists.EssentialForAGenre"/>, which are the plain lists that a genre's own page
/// offers. The rule that builds any other kind ignores a genre it is given. That is the same thing that
/// happens to a decade on <see cref="SmartPlaylistKind.Sleep"/>.</param>
/// <param name="Decade">The decade it is narrowed to, one of <see cref="SmartPlaylists.Decades"/>, or
/// zero for no decade. Only the kinds in <see cref="SmartPlaylists.BrokenOutByEra"/> may carry one.
///
/// <para><b>A list is narrowed by a genre or by a decade, never by both.</b> "Calm rock" and "calm in the
/// seventies" are two ways of cutting one question. Combining the two would give a third cut. Four moods
/// by eighteen genres by six decades is four hundred and thirty-two rows. That is a filing system, not a
/// shelf of playlists. The factories are the only way to build one of these. None of them sets both,
/// which makes this rule true by construction, not just intended.</para></param>
public readonly record struct SmartPlaylistId(SmartPlaylistKind Kind, string? Genre, int Decade)
{
    /// <summary>The whole list: every genre and every decade together.</summary>
    public static SmartPlaylistId WholeShelf(SmartPlaylistKind kind) => new(kind, null, 0);

    /// <summary>The same question asked of one genre only.</summary>
    public static SmartPlaylistId Within(SmartPlaylistKind kind, string genre)
    {
        ArgumentNullException.ThrowIfNull(genre);
        return new(kind, genre, 0);
    }

    /// <summary>The same question asked of one decade only.</summary>
    /// <param name="decade">The first year of it: 1960, 1970 and so on. It is refused where it is not one
    /// of <see cref="SmartPlaylists.Decades"/>. A decade that nothing offers is a list that nothing can
    /// draw. A silent empty list is worse than an exception.</param>
    public static SmartPlaylistId In(int decade)
    {
        if (!SmartPlaylists.Decades.Contains(decade))
            throw new ArgumentOutOfRangeException(
                nameof(decade), decade, "That is not a decade this shelf is broken out by.");
        return new(SmartPlaylistKind.Decade, null, decade);
    }

    /// <summary>
    /// One mood, held to one decade.
    ///
    /// <para>The rule table has read <c>id.Decade</c> for these four kinds since the moods were added.
    /// <c>SmartPlaylistRule.Pool</c> narrows by it. But nothing could name such a list, because the
    /// factories are the only way to build an id, and no factory set a decade on a mood. So the decade
    /// breakout was reachable in the database and nowhere else. This method names it. It is used by
    /// <see cref="MoodQuery"/>, where "calm music from the seventies" arrives. It builds nothing that the
    /// rule table does not already handle.</para>
    ///
    /// <para>There is no genre here, and no factory sets both. See the note on
    /// <see cref="SmartPlaylistId.Decade"/>.</para>
    /// </summary>
    /// <param name="kind">One of <see cref="SmartPlaylists.BrokenOutByEra"/>. Any other kind is refused.
    /// Every other kind ignores a decade it is given, and a list that silently drops its own narrowing is
    /// worse than an exception.</param>
    /// <param name="decade">The first year of it, one of <see cref="SmartPlaylists.Decades"/>.</param>
    public static SmartPlaylistId InEra(SmartPlaylistKind kind, int decade)
    {
        if (!SmartPlaylists.BrokenOutByEra.Contains(kind))
            throw new ArgumentOutOfRangeException(
                nameof(kind), kind, "This generated list is not broken out by decade.");
        if (!SmartPlaylists.Decades.Contains(decade))
            throw new ArgumentOutOfRangeException(
                nameof(decade), decade, "That is not a decade this shelf is broken out by.");
        return new(kind, null, decade);
    }

    /// <summary>Whether this is narrowed to a genre at all.</summary>
    public bool IsWithinGenre => Genre is not null;

    /// <summary>Whether this is narrowed to a decade at all.</summary>
    public bool IsWithinDecade => Decade > 0;

    /// <summary>Whether this is the pooled list: no genre and no decade. It is the whole of what the mood
    /// holds. This states the answer for "all of it", rather than leaving two absences for a reader to
    /// add up.</summary>
    public bool IsWholeShelf => Genre is null && Decade == 0;
}

/// <summary>The generated playlists, in the order they are listed under the tree's pinned folder.</summary>
public static class SmartPlaylists
{
    /// <summary>Every kind, in listing order. The tree draws one row for each entry that has something in
    /// it. All of them go inside the one pinned folder.</summary>
    public static IReadOnlyList<SmartPlaylistKind> All { get; } =
    [
        SmartPlaylistKind.FavoriteTracks,
        SmartPlaylistKind.RightNow,
        SmartPlaylistKind.MostPlayed,
        SmartPlaylistKind.OnRepeat,
        SmartPlaylistKind.RecentlyAdded,
        SmartPlaylistKind.FirstListens,
        SmartPlaylistKind.RecentlyPlayed,
        SmartPlaylistKind.LateNight,
        SmartPlaylistKind.Rediscover,
        SmartPlaylistKind.LovedAndForgotten,
        SmartPlaylistKind.PlayedNotLoved,
        SmartPlaylistKind.NeverPlayed,
        SmartPlaylistKind.KeepSkipping,
        SmartPlaylistKind.Calm,
        SmartPlaylistKind.Energetic,
        SmartPlaylistKind.Danceable,
        SmartPlaylistKind.Focus,
        SmartPlaylistKind.Sleep,

        // Beside the moods rather than among them. It is a genre, not a feeling. It is listed here
        // because a shelf's classical music cannot be reached from this pane otherwise. Its mood band is
        // too small to show: a few songs, below any useful floor.
        SmartPlaylistKind.Classical,
    ];

    /// <summary>
    /// The kinds worth generating over a shelf borrowed from somebody else, and why it is only one.
    ///
    /// <para>A generated list is a question asked of the track rows. On a borrowed shelf, most of those
    /// rows answer about the wrong thing. "Recently added" would date every record to the evening the
    /// share was set up. The listening lists would report what the borrower has played from somebody
    /// else's collection. That is a handful of songs, and it makes the borrower look as if they have
    /// almost no music. Only the favourites are a fact about the reader that stays true wherever the
    /// record lives. They were marked at this desk, on purpose, about these songs.</para>
    ///
    /// <para>Two named lists rather than a flag. The surface has to say which shelf it is drawing from. A
    /// new kind cannot arrive on a borrowed shelf just by being added to <see cref="All"/>.</para>
    /// </summary>
    /// <summary>
    /// The kinds that do NOT live in the pinned folder, and where they live instead.
    ///
    /// <para><see cref="All"/> is the list that the generated playlists folder shows. A decade is not in
    /// it. The decades have a folder of their own. A row called "Decade" with no decade on it is not a
    /// list that anyone can open. The kind is named here rather than left out, so the two lists together
    /// still cover every kind. That stops a kind from being added and then quietly appearing nowhere at
    /// all.</para>
    /// </summary>
    public static IReadOnlyList<SmartPlaylistKind> InTheirOwnFolder { get; } =
    [
        SmartPlaylistKind.Decade,
    ];

    public static IReadOnlyList<SmartPlaylistKind> OnABorrowedShelf { get; } =
    [
        SmartPlaylistKind.FavoriteTracks,
    ];

    /// <summary>
    /// The kinds the home screen offers, and why it is not all of them.
    ///
    /// <para>The home screen asks one question: what have you been listening to, and what should you put
    /// on next? So the lists it offers are the ones somebody would PLAY. Four of the kinds are not that.
    /// <see cref="SmartPlaylistKind.NeverPlayed"/> and <see cref="SmartPlaylistKind.KeepSkipping"/> are
    /// things to do about a collection, not music to put on.
    /// <see cref="SmartPlaylistKind.PlayedNotLoved"/> is a list of hearts you have not given, which is a
    /// chore with a soundtrack. <see cref="SmartPlaylistKind.RecentlyAdded"/> is already shown on that
    /// screen as a row of records. It asks the same question twice, once in songs and once in
    /// sleeves.</para>
    ///
    /// <para><b>Two more are left out for the same reason.</b> <see cref="SmartPlaylistKind.MostPlayed"/>
    /// and <see cref="SmartPlaylistKind.RecentlyPlayed"/> are each already a row of RECORDS on that
    /// screen, "Most played" and "Recently listened". Showing them as songs too would ask the same
    /// question twice on one page. They are still in the playlists tree, and they are still what the
    /// banner draws its own cards from.</para>
    ///
    /// <para>A third named list is used, rather than a filter written at the screen. This is the same
    /// reason the borrowed shelf has a named list. A kind added to <see cref="All"/> must not arrive on
    /// the home screen just by being appended to a list. Someone has to decide whether it is music or
    /// housekeeping.</para>
    /// </summary>
    public static IReadOnlyList<SmartPlaylistKind> OnTheHomeScreen { get; } =
    [
        // First, because it is the one card on that row that answers the question the screen is asking:
        // what do I put on now. The other cards name a way of sorting the collection.
        SmartPlaylistKind.RightNow,

        SmartPlaylistKind.OnRepeat,
        SmartPlaylistKind.FavoriteTracks,
        SmartPlaylistKind.FirstListens,
        SmartPlaylistKind.Rediscover,
        SmartPlaylistKind.LovedAndForgotten,
        SmartPlaylistKind.LateNight,

        // The lists that come from the audio belong here on the plainest reading of the question. They
        // are music to put on. Unlike every row above them, they need no listening history at all. So
        // they are the only thing this screen can offer someone on their first evening with the app.
        SmartPlaylistKind.Calm,
        SmartPlaylistKind.Energetic,
        SmartPlaylistKind.Danceable,
        SmartPlaylistKind.Focus,
        SmartPlaylistKind.Sleep,
    ];

    /// <summary>
    /// The kinds that cannot be composed without <see cref="LibraryDatabase.SessionMoods"/>.
    /// </summary>
    /// <remarks> That cut is three whole-table reads and a sort. It is built once per session, by
    /// whichever of these kinds asks first. The first one pays for it, and the others read the cut that
    /// is already in memory. On a shelf of 1,394 albums the first kind takes about 1.9 s and each of the
    /// others under 20 ms. The cut is nearly two thirds of the time the home screen takes to load, and
    /// the screen waits for it.
    ///
    /// <para>So the screen composes everything else first, and comes back for these. This set is the line
    /// between the two. It is a named set rather than a test written at the screen, for the same reason
    /// as <see cref="OnTheHomeScreen"/>. A kind added to <see cref="All"/> that relies on the cut must be
    /// put here by someone who decides so. It must not be found later by a screen that became
    /// slow.</para>
    /// </remarks>
    public static IReadOnlySet<SmartPlaylistKind> NeedTheMoodCut { get; } = new HashSet<SmartPlaylistKind>
    {
        SmartPlaylistKind.Calm,
        SmartPlaylistKind.Energetic,
        SmartPlaylistKind.Danceable,
        SmartPlaylistKind.Focus,
        SmartPlaylistKind.Sleep,
    };

    /// <summary>
    /// The kinds that can be broken out one genre at a time. There are only four.
    ///
    /// <para>These are the moods that are CUT within a genre in the first place. Calm means calm for
    /// rock, so asking for rock alone asks a question that the recipe already answered.
    /// <see cref="SmartPlaylistKind.Sleep"/> is not among them, and cannot be. It is an absolute cut over
    /// the whole collection (see <see cref="MoodBands.SleepAt"/>). So "sleep jazz" would be the jazz
    /// records that happen to be in it, not a question anyone asked. Everything else in <see cref="All"/>
    /// is about listening, not about sound, and it has no genre reading at all.</para>
    ///
    /// <para>This is a named list rather than a test written at the pane, for the same reason as the
    /// other named lists. A kind added to <see cref="All"/> must not gain genre children just by being
    /// appended to a list. Someone has to decide whether the question has a per-genre form at all.</para>
    /// </summary>
    /// <summary>
    /// The lists that live in the moods folder, in the order they are drawn.
    ///
    /// <para><b>A folder of its own, beside the decades.</b> The moods are a top-level folder, just as
    /// the decades are. The pinned folder was holding two different kinds of question: what the music
    /// SOUNDS like, and what the listener has DONE with it. The second kind is much the longer list, so
    /// the first was buried at the bottom.</para>
    ///
    /// <para><b>Classical is in this folder on purpose.</b> It is the one entry that is not cut from a
    /// measurement. It is a genre, filed by its label. It belongs here because of what a listener reaches
    /// for it FOR, which is a room and a frame of mind. It is not here because of how it is built. A
    /// folder is a place to look, not a claim about how the lists are made.</para>
    ///
    /// <para>Named here rather than tested for at the pane, for the same reason as
    /// <see cref="BrokenOutByGenre"/>. A kind added to <see cref="All"/> must not fall into this folder
    /// just by being appended to a list. Someone has to decide whether it belongs. Everything in
    /// <see cref="All"/> that is NOT here goes in the pinned folder, so the two lists together cover
    /// everything.</para>
    /// </summary>
    public static IReadOnlyList<SmartPlaylistKind> Moods { get; } =
    [
        SmartPlaylistKind.Calm,
        SmartPlaylistKind.Energetic,
        SmartPlaylistKind.Danceable,
        SmartPlaylistKind.Focus,
        SmartPlaylistKind.Sleep,
        SmartPlaylistKind.Classical,
    ];

    /// <summary> The listening lists that home shows after Your mix, in the order home puts them. The
    /// desktop generates them and pushes them to the cloud, where the phone and the television read them.
    ///
    /// <para>These lists are sent in addition to the moods, the decades and Your mix. So the phone and
    /// the television also show On repeat, Favourite tracks, New to you, Rediscover, Loved and forgotten
    /// and Late night. On repeat, New to you, Rediscover and Late night are cut from this machine's own
    /// listening log. The phone's and the television's plays reach the play totals and the last-played
    /// times, which Loved and forgotten and Your mix read. They do not reach the log.</para>
    /// </summary>
    public static IReadOnlyList<SmartPlaylistKind> Listening { get; } =
    [
        SmartPlaylistKind.OnRepeat,
        SmartPlaylistKind.FavoriteTracks,
        SmartPlaylistKind.FirstListens,
        SmartPlaylistKind.Rediscover,
        SmartPlaylistKind.LovedAndForgotten,
        SmartPlaylistKind.LateNight,
    ];

    /// <summary> The generated lists that go to the owner's own phone and television, and to nobody else:
    /// Your mix and the listening lists (<see cref="Listening"/>), in the order that home puts them.
    ///
    /// <para>These are cut from the owner's listening. On someone else's screen they would be a record of
    /// what the owner has been playing. <see cref="Cloud.MoodListPush.ForOthers"/> removes them from
    /// anything that goes to another person.</para>
    /// </summary>
    public static IReadOnlyList<SmartPlaylistKind> OwnerOnly { get; } =
    [
        SmartPlaylistKind.RightNow,
        .. Listening,
    ];

    /// <summary> What the cut sends out of this machine: the owner-only lists first, in the order home
    /// puts them, then the moods. The decades are added by the cut itself.
    /// </summary>
    public static IReadOnlyList<SmartPlaylistKind> SentOut { get; } = [.. OwnerOnly, .. Moods];

    /// <summary>
    /// The kinds a GENRE's page offers as that genre's own lists, and why they are not the four moods.
    ///
    /// <para><see cref="BrokenOutByGenre"/> is the moods, and it is about the playlists tree. Those four
    /// are cut within a genre in the first place, so the tree nests a genre row under each one. This list
    /// answers a different question: what would someone on the rock page want to put on? The answer is
    /// the plain lists. What of this genre you play most, what arrived last, what you have marked, and
    /// what you have never heard. None of these four needs a measurement or a mood cut. So a genre's page
    /// can offer them on a collection that the analysis pass has never touched.</para>
    ///
    /// <para><b>Two lists rather than one, and they overlap in nothing.</b> A genre's page shows both
    /// these and the moods narrowed to the same genre, as two rows. They are two kinds of answer. These
    /// are about what the listener has DONE with the music. Those are about what it SOUNDS like. Merged
    /// into one row, they would be a shelf of eight cards with no order anyone could read.</para>
    ///
    /// <para>The listening lists are deliberately absent. "Late night rock" and "rediscover rock" are
    /// real questions. But each one is a window over the listen log, narrowed twice. On a genre that
    /// holds a twentieth of a collection, the answer is nearly always under <see cref="LeastToShow"/>. A
    /// row of cards that are mostly missing looks like a page that failed to load.</para>
    ///
    /// <para>Named here rather than tested for at the page, for the same reason as the other lists. A
    /// kind added to <see cref="All"/> must not become a genre's list just by being appended. The
    /// database must agree too. See <c>SmartPlaylistRule.Narrowed</c>, which decides whether a narrowed
    /// list can be built.</para>
    /// </summary>
    public static IReadOnlyList<SmartPlaylistKind> EssentialForAGenre { get; } =
    [
        SmartPlaylistKind.MostPlayed,
        SmartPlaylistKind.FavoriteTracks,
        SmartPlaylistKind.RecentlyAdded,
        SmartPlaylistKind.NeverPlayed,
    ];

    public static IReadOnlyList<SmartPlaylistKind> BrokenOutByGenre { get; } =
    [
        SmartPlaylistKind.Calm,
        SmartPlaylistKind.Energetic,
        SmartPlaylistKind.Danceable,
        SmartPlaylistKind.Focus,
    ];

    /// <summary>
    /// Whether this kind is answered from the measuring pass rather than from the track rows.
    ///
    /// <para>This tells a listener the truth about why a list is missing. These five are cut from
    /// measurements that the pass writes as it walks the collection. On a shelf the pass has not reached,
    /// they are empty. Importing more music will not fix that. Waiting for the pass will. Every other
    /// kind answers from rows that exist as soon as a file is filed.
    /// </para>
    ///
    /// <para>The same four <see cref="BrokenOutByGenre"/> holds, plus Sleep. Sleep is a measured cut too.
    /// It is absent from that list only because it is cut across the whole collection, not within a
    /// genre.</para>
    /// </summary>
    public static bool NeedsMeasuring(SmartPlaylistKind kind) => kind
        is SmartPlaylistKind.Calm
        or SmartPlaylistKind.Energetic
        or SmartPlaylistKind.Danceable
        or SmartPlaylistKind.Focus
        or SmartPlaylistKind.Sleep;

    /// <summary>
    /// The decades a mood can be broken out by, oldest first.
    ///
    /// <para>There are six. They stop where a listener's own collection does. Before the sixties, a home
    /// library is a handful of reissues rather than a decade of music. The current decade is still being
    /// made. A decade with too little in it is simply not drawn. So the ends of this list cost nothing
    /// when they are empty. See <see cref="LeastToShow"/>.</para>
    ///
    /// <para>They are named by their first year, not as strings, because a decade is arithmetic here.
    /// <c>MoodList.InDecade</c> tests a year against it. "1970s" is a label, not a number.</para>
    /// </summary>
    public static IReadOnlyList<int> Decades { get; } = [1960, 1970, 1980, 1990, 2000, 2010];

    /// <summary>
    /// How many of a decade's songs the list holds.
    ///
    /// <para>Fifty, which is what a "top songs of" list is. It is a chart rather than a shelf. The
    /// fiftieth most played song of the eighties is still a song that everybody knows. The two hundredth
    /// is an album track with a number beside it.</para>
    /// </summary>
    public const int SongsInADecade = 50;

    /// <summary>
    /// The kinds that can be broken out one decade at a time, and why it is the same four.
    ///
    /// <para>These are the moods that are cut WITHIN a cohort. So narrowing them again by when a record
    /// was made asks a question that still means something. The calm end of the seventies is the
    /// seventies records that this listener finds calm. <see cref="SmartPlaylistKind.Sleep"/> is not
    /// among them, for the same reason it has no genre rows. It is an absolute cut over the whole
    /// collection. So "sleep in the eighties" would be the eighties records that happen to be in the
    /// sleep list, not a question anyone asked.</para>
    ///
    /// <para>This is a named list rather than a test written at the pane. The reason is the same as for
    /// the other named lists. A kind added to <see cref="All"/> must not gain decade children just by
    /// being appended to a list. Someone has to decide whether the question has a per-decade form at
    /// all.</para>
    /// </summary>
    public static IReadOnlyList<SmartPlaylistKind> BrokenOutByEra { get; } =
    [
        SmartPlaylistKind.Calm,
        SmartPlaylistKind.Energetic,
        SmartPlaylistKind.Danceable,
        SmartPlaylistKind.Focus,
    ];

    /// <summary>
    /// How many songs a generated list is allowed to be.
    ///
    /// <para>This is a ceiling, not the whole answer. These lists are read on the UI thread when a row is
    /// opened, and a library can hold a million tracks. Every kind is ordered so that the songs worth
    /// having come first. That is what lets the ceiling be low.</para>
    ///
    /// <para><b>Two hundred, not two thousand.</b> A generated list is a recommendation. A recommendation
    /// two thousand songs long is just the collection in a different order, which is the thing these
    /// lists exist to avoid. Two hundred is about half a day of music. It is long enough to put on and
    /// leave, and short enough that the two hundredth song still belongs on it. The limit is set by what
    /// a listener would read, not by what the UI thread can handle.</para>
    /// </summary>
    public const int MaxTracks = 200;

    /// <summary>
    /// The fewest dated songs an era window has to hold before a mood list is narrowed to it.
    ///
    /// <para><b>Because a list that merges decades reads as incoherent.</b> A list might hold a soul song
    /// by one artist next to a dance song by another, decades apart. The mood bands are cut on how a
    /// record SOUNDS, and they say nothing about when it was made. So a genre's calm end can be forty
    /// years wide. <see cref="MoodList.InOneEra"/> narrows each pool to a contiguous run of decades,
    /// around the one that most of it is in. This constant is where it stops narrowing.</para>
    ///
    /// <para><b>This is the FLOOR, and it does one job.</b> A pool with fewer dated songs than this is
    /// left alone entirely. There is nothing to be coherent about in a shelf that nobody has put years
    /// on. Narrowing it would be arithmetic dressed up as an opinion. How WIDE the window then grows is a
    /// different question with a different answer. See <see cref="EnoughInOneEra"/>. The two are separate
    /// because they answer different questions.</para>
    ///
    /// <para><b>Sixty, and it is a compromise, not a measurement.</b> It sits just above
    /// <see cref="MostSongs"/>, so that a shelf which is narrowed at all has enough dated songs to fill a
    /// list. Raising it does not make lists deeper. It only makes fewer shelves era-coherent at all. If
    /// it were raised to the depth of <see cref="EnoughInOneEra"/>, several rows that narrow today would
    /// stop narrowing and would span the sixties to now. That is the complaint the era window exists to
    /// answer.</para>
    /// </summary>
    public const int LeastForOneEra = 60;

    /// <summary>
    /// How much the era window grows to hold before it stops widening.
    ///
    /// <para><b>A hundred and fifty. This number decides DEPTH.</b> A mood list shows
    /// <see cref="MostSongs"/> songs. A window that holds only sixty could offer barely more than one
    /// list. Then every opening would show nearly the same songs, and the generate button would have
    /// almost nothing to give back. Three lists' worth is enough that a second and a third reading are
    /// genuinely different music.</para>
    ///
    /// <para><b>The cost, stated plainly.</b> A wider window is a less coherent one. The reason the
    /// narrowing exists at all is that a mood is cut on how a record SOUNDS, and that can be forty years
    /// wide. On a real collection, some rows widen by one decade. The thinnest widen to nearly everything
    /// they hold. A shelf that cannot reach this depth in any window keeps all the decades it has. That
    /// is what it did before there was a window.</para>
    ///
    /// <para>Never below <see cref="LeastForOneEra"/>. A window smaller than the floor would contradict
    /// the floor that allowed narrowing at all.</para>
    /// </summary>
    public const int EnoughInOneEra = 150;

    /// <summary>
    /// How many lists' worth of one list the phone is sent.
    ///
    /// <para><b>The phone has to be able to press the button too.</b> A generated list is built from
    /// percentiles over a whole collection. The phone has only a fraction of that collection, so it
    /// cannot build a list, and it never will. What it can do is take its own window of one run, if it is
    /// sent more than it shows. The desktop sends this many lists' worth in one run, best first. The
    /// phone shows a slice of it, and steps to the next slice when someone presses refresh.</para>
    ///
    /// <para>One run is sent, not this many separate readings joined end to end. The difference matters.
    /// A run holds one song per RECORD across the whole of it, so every slice of it is a different list.
    /// Four readings joined together would each be capped separately. A shallow pool would then give back
    /// four lists that mostly repeat each other.</para>
    ///
    /// <para>Four, because the store accepts five hundred records on a list, and four lists of fifty is
    /// two hundred. In practice the pool is what limits it. A shelf whose era window holds 150 records
    /// gives three slices and then wraps. That is an honest answer, not a fault.</para>
    ///
    /// <para><b>Four is the most, not the number every kind gets.</b> See <see cref="ReadingsSentFor"/>.
    /// Not every kind shows fifty. Four readings of a kind that shows two hundred would be eight hundred
    /// records, which the store refuses.</para>
    /// </summary>
    public const int ReadingsSent = 4;

    /// <summary>
    /// The most records the store will accept on one list.
    ///
    /// <para>This is the API's own number, copied here because the API does not share code with this
    /// project. The two must be changed together: <c>CloudController.MaxMoodListTracks</c>.</para>
    ///
    /// <para>It is stated on this side because a cut is sent whole. One list over the ceiling does not
    /// drop that list. The whole cut is refused, and the phone keeps showing whatever it had before. For
    /// example, a run of four readings of a kind that shows two hundred songs would send 800 records on
    /// one list. The whole run would then be refused, and every generated list on the phone would go
    /// stale.</para>
    /// </summary>
    public const int MostSentOnOneList = 500;

    /// <summary>
    /// How many readings of THIS kind are sent.
    ///
    /// <para><see cref="ReadingsSent"/>, unless that many of this kind's lists would be more records than
    /// <see cref="MostSentOnOneList"/>. In that case, as many whole readings as fit are sent. The answer
    /// is never fewer than one. A kind whose single list is already over the limit is a different
    /// problem, and one reading is the least that can be sent.</para>
    ///
    /// <para><b>The limit is per kind, because the store's limit is per list.</b> The moods show fifty
    /// songs, and they get all four readings. Classical shows <see cref="MaxTracks"/>. Four of those
    /// would be eight hundred records, so it gets two, which is four hundred and inside the store's
    /// limit. The phone does not need to be told. It reads how long the run is and how many songs one
    /// list shows, and it takes its slices from that.</para>
    ///
    /// <para><b>One for a listening list</b> (<see cref="Listening"/>). These are rankings of what was
    /// played or hearted, with no next reading to step to. A second reading would be the songs ranked two
    /// hundred and first onwards, which this machine never shows under that name.</para>
    /// </summary>
    public static int ReadingsSentFor(SmartPlaylistKind kind) =>
        Listening.Contains(kind)
            ? 1
            : Math.Max(1, Math.Min(ReadingsSent, MostSentOnOneList / MostSongs(kind)));

    /// <summary>
    /// The ceiling that one particular kind stops at, where it is not the general one.
    ///
    /// <para><see cref="SmartPlaylistKind.Sleep"/> stops at fifty. It is the one list that somebody puts
    /// on without meaning to hear the end of it. So length is not what makes it good. A hundred and
    /// eighty songs deep, it is the same twenty records over again, and the part anybody reaches is the
    /// top of it either way. Fifty songs is about three hours, which is longer than the reason it was put
    /// on.</para>
    ///
    /// <para>The short ceiling is why <see cref="MoodShelf"/> spreads that list across records, rather
    /// than ranking it strictly by stillness. Cut at fifty, a strict ranking could be fifteen tracks by
    /// one artist. That is the opposite of what a short list is for.</para>
    /// </summary>
    public static int MostSongs(SmartPlaylistKind kind) => kind switch
    {
        SmartPlaylistKind.Calm or SmartPlaylistKind.Energetic
            or SmartPlaylistKind.Danceable or SmartPlaylistKind.Focus
            or SmartPlaylistKind.Sleep => 50,

        // Also fifty, for the same reason: it is put on, not read down. It runs for about three hours.
        SmartPlaylistKind.RightNow => 50,
        SmartPlaylistKind.Decade => SongsInADecade,
        _ => MaxTracks,
    };

    /// <summary>
    /// How much of one RECORD a mood list will hold.
    ///
    /// <para><b>One song per record.</b> A mood is measured per track. So a whole album recorded in one
    /// session lands in one band together. A strictly ranked list of fifty is often eight songs from one
    /// record. A cap of two would keep fifty songs to about twenty-five records. But twenty-five records
    /// can still be the same album twice, over and over, and a listener hears that.</para>
    ///
    /// <para><b>What it costs is depth.</b> A list is now as long as its band has distinct ALBUMS. On a
    /// real collection that is between a fifth and a third of the band's tracks, so the lists get
    /// shorter. Three
    /// things depend on that depth. A list stops at <see cref="MostSongs"/>. A row is only drawn above
    /// <see cref="LeastToShow"/>. The generate button only appears while the pool holds more than fifty
    /// songs. The second of these changed along with this cap. See its own note.</para>
    /// </summary>
    public const int MostFromOneRecord = 1;

    /// <summary>
    /// How much of one ACT a mood list will hold.
    ///
    /// <para>Five. This is looser than the record cap on purpose. Hearing an artist again across a long
    /// list is not a fault. Hearing the same album again is. An act with three quiet records may bring
    /// some of each.</para>
    /// </summary>
    public const int MostFromOneArtist = 5;

    // ── the windows the rules are written in ──

    /// <summary>"Lately": the month behind us.</summary>
    public const int RecentDays = 30;

    /// <summary>Long enough without hearing something that putting it on again is a rediscovery.</summary>
    public const int ForgottenDays = 180;

    /// <summary>
    /// How far back the lists that read the whole log are allowed to look.
    ///
    /// <para>This is a horizon, not a preference. Without it, every reload of the playlist pane groups
    /// the whole listening history to decide whether one row is worth drawing. That cost grows without
    /// limit, while the answer stops changing. What somebody skipped a long time ago does not decide
    /// whether they skip it now.</para>
    /// </summary>
    public const int HistoryDays = 365;

    /// <summary>Rediscover looks back further than the rest: its whole question is about a while
    /// ago.</summary>
    public const int RediscoverDays = 2 * 365;

    /// <summary>Plays in <see cref="RecentDays"/> before something is "on repeat".</summary>
    public const int OnRepeatPlays = 3;

    /// <summary>Plays before a silence counts as having lost something rather than never having had
    /// it.</summary>
    public const int RediscoverPlays = 3;

    /// <summary>Plays before never having been favourited says anything.</summary>
    public const int PlayedNotLovedPlays = 5;

    /// <summary>Night-time listens before a song is a night-time song.</summary>
    public const int LateNightListens = 3;

    /// <summary>Skips before skipping is a habit rather than an accident.</summary>
    public const int SkipsBeforeItShows = 3;

    /// <summary>Night starts here, on the listener's own clock.</summary>
    public const int NightFromHour = 23;

    /// <summary>And ends here. Hours below this and at or above <see cref="NightFromHour"/> are
    /// night.</summary>
    public const int NightUntilHour = 5;

    /// <summary>
    /// The fewest songs THIS list may hold and still be worth a row.
    ///
    /// <para><b>The id is taken, not the kind, because the floor depends on more than the kind.</b> A
    /// mood over the whole shelf, and the same mood narrowed to one genre, are two questions of very
    /// different size. A caller that passed <c>id.Kind</c> would silently ask the wrong one. This is the
    /// mistake that <see cref="LeastForNarrowedRow"/> exists to prevent. There is no overload that takes
    /// a kind, so a call site cannot forget which question it is asking.</para>
    /// </summary>
    public static int LeastToShow(SmartPlaylistId id)
        => id.IsWithinGenre || id.IsWithinDecade
            ? LeastForNarrowedRow(id.Kind)
            : LeastForWholeShelf(id.Kind);

    /// <summary>
    /// The floor for a row narrowed to one genre or one decade.
    ///
    /// <para><b>Six, where the whole shelf asks for ten. The difference is not a softer rule. It is the
    /// same rule, applied to the right group of songs.</b> A mood list takes at most one song from any
    /// record (<see cref="MostFromOneRecord"/>). So a row is exactly as long as its band has distinct
    /// ALBUMS. Over the whole shelf, that is many albums, and ten is a small number. Narrowed to one
    /// genre, it is only the albums of THAT genre in THAT band. On a real collection, that is a single
    /// figure for most genres outside the biggest shelves.</para>
    ///
    /// <para>A lower floor lets the smaller genres show their rows. At ten, many genres (for example
    /// Indie, House, Latin and Punk) would draw no mood row, because their best bands hold only eight or
    /// nine albums. The decades are narrowed the same way.</para>
    ///
    /// <para>Six songs from six different records is a playlist. Six songs from two records would not be,
    /// and that cannot happen. The cap is what makes the count of songs a count of records.</para>
    /// </summary>
    public static int LeastForNarrowedRow(SmartPlaylistKind kind)
        => Math.Min(LeastForWholeShelf(kind), NarrowedFloor);

    /// <summary>The floor a narrowed row is held to, where its kind asks for more over the whole shelf.
    /// Kinds that already ask for less than this keep their own number. For example, a favourites row
    /// narrowed to a genre still appears at one song, because one song somebody hearted is the whole of
    /// what that list claims to be.</summary>
    private const int NarrowedFloor = 6;

    /// <summary>
    /// The fewest songs a generated list may hold over the WHOLE shelf and still be worth a row.
    ///
    /// <para>This is what "show it when there is something to show" means. A list of two is not a
    /// playlist. A pane full of nearly empty rows, on a library nobody has played yet, says less than a
    /// pane with three good rows. The lists that answer from the track rows appear as soon as they hold
    /// anything, because their data is the collection itself, and it is there on day one. The lists that
    /// read the log need a few weeks of listening before they mean anything.</para>
    ///
    /// <para>A row narrowed to a genre or a decade is a smaller question. It answers to
    /// <see cref="LeastForNarrowedRow"/>. <see cref="LeastToShow(SmartPlaylistId)"/> picks between them.
    /// Every caller that holds an id should ask it.</para>
    /// </summary>
    public static int LeastForWholeShelf(SmartPlaylistKind kind) => kind switch
    {
        SmartPlaylistKind.FavoriteTracks => 1,
        SmartPlaylistKind.MostPlayed => 1,
        SmartPlaylistKind.RecentlyAdded => 1,
        SmartPlaylistKind.RecentlyPlayed => 1,
        SmartPlaylistKind.NeverPlayed => 1,
        SmartPlaylistKind.LovedAndForgotten => 1,
        SmartPlaylistKind.PlayedNotLoved => 3,

        SmartPlaylistKind.OnRepeat => 4,
        SmartPlaylistKind.Rediscover => 5,
        SmartPlaylistKind.LateNight => 5,
        SmartPlaylistKind.FirstListens => 5,
        SmartPlaylistKind.KeepSkipping => 3,

        // Higher than any of the others, and for a reason none of them have. These four appear while the
        // measuring pass is still walking the collection. So the first thing a listener would see is a
        // list of four songs that grows all week. The floor is what makes the row mean "here is some calm
        // music" as soon as it appears, rather than "here is what has been measured so far".
        //
        // TEN. A list is as long as its band has distinct ALBUMS, because MostFromOneRecord is one. Ten
        // songs from ten different records is a playlist. Ten songs from five records is not.
        SmartPlaylistKind.Calm => 10,
        SmartPlaylistKind.Energetic => 10,
        SmartPlaylistKind.Danceable => 10,
        SmartPlaylistKind.Focus => 10,
        SmartPlaylistKind.Sleep => 10,

        // A decade fills in as the popularity data arrives. So the row appears as soon as there is a
        // handful to show, not after the whole collection has been looked up.
        SmartPlaylistKind.Decade => 10,

        // This comes from a genre, not a measurement. So it is there on the first day, for anyone who
        // owns such music. The row appears as soon as the collection holds a handful.
        SmartPlaylistKind.Classical => 10,

        // Ten, the same as the moods, for the same kind of reason: this one fills in from listening, not
        // from the measuring pass. On a first evening it holds whatever has been loved or played so far,
        // which is a handful that grows all week. Under ten it is not a mix. A row called "Your mix" with
        // four songs in it promises something it cannot yet do. It also takes one song per record
        // (MostFromOneRecord), so ten songs are ten records.
        SmartPlaylistKind.RightNow => 10,

        _ => throw new ArgumentOutOfRangeException(
            nameof(kind), kind, "Nobody has said how much of this list is worth showing."),
    };
}
