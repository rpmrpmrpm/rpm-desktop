using RPM.Core.Analysis;
using RPM.Core.Metadata;

namespace RPM.Core.Library;

/// <summary>
/// One mood list: every song in it, ranked, and reachable either whole or one genre at a time.
///
/// <para>The list is ranked once and filtered when it is read. Filtering keeps the ranked order.
/// A separate list for each genre would hold the same songs about twenty times.</para>
/// </summary>
public sealed class MoodList
{
    /// <summary>One song in the list: where it is, what its record is filed under, which record it is
    /// on, and who it is by. The last two are used by the caps, see <see cref="MoodPool"/>.</summary>
    /// <param name="Year">The year of the record, or 0 when it is not known. Used by
    /// <see cref="InOneEra"/> only. A mood describes how a track sounds, and the year is the one fact
    /// that the sound cannot give.</param>
    /// <param name="Liked">Whether the listener has said this song belongs in a list like this one.
    /// See <see cref="InOneEra"/>, which is one of the places where it changes the result.</param>
    /// <param name="Styles">The styles that the catalogues give this record's release, mapped onto
    /// RPM's own names. Only <see cref="MoodFlavour"/> reads it. It is empty for a record that no
    /// catalogue has been asked about, which is normal and not a fault.
    /// <paramref name="Genre"/> is the shelf, and it answers a different question.</param>
    /// <param name="Sound">The three numbers that the meter produced. <see cref="MoodFlow"/> orders
    /// the finished list by them. It cannot read them from the ranking, because the ranking turns all
    /// three into one percentile, and a percentile says how calm a song is, not what it sounds
    /// like.</param>
    /// <param name="WithinGenreBy">Where this song sits in its own genre's row. This can differ from
    /// its place in the pooled list above it. See <see cref="Within"/> for why one list has two
    /// orders.</param>
    internal readonly record struct Song(
        string Path, string? Genre, string Folder, string Artist, int Year, bool Liked,
        IReadOnlyList<string> Styles, Sound Sound, double WithinGenreBy);

    /// <summary>
    /// How much a genre row may prefer the songs that sound like that genre.
    ///
    /// <para>The value is 0.1, the same bound as <see cref="LyricLift.Weight"/>, for the same reason.
    /// The ranking runs from 0 to 1 across the band. A term added to it may reorder songs inside the
    /// band, and must never move a song out of it. Both terms can apply to one song in one genre row,
    /// so together they move a song by at most 0.2 of the band, which still cannot overturn it.</para>
    /// </summary>
    internal const double TypicalWeight = 0.1;

    /// <summary>
    /// What a track measured, as the meter left it. See <see cref="Analysis.MoodReading"/> for where
    /// the three numbers come from and what each one means.
    ///
    /// <para>The values are held on the song and not looked up for each comparison. The ordering
    /// compares every pair in a list of fifty songs, which would be 2,500 dictionary lookups for a
    /// list that somebody is waiting for.</para>
    /// </summary>
    internal readonly record struct Sound(double Brightness, double Onsets, double Dynamics)
    {
        /// <summary>A song that the measured rows do not carry. It has no place in the order and is
        /// put last, see <see cref="MoodFlow.Ordered"/>.</summary>
        internal static readonly Sound Nothing = new(-1, -1, -1);

        /// <summary>Whether there is a reading at all. This is the meter's own test: a negative value
        /// is what it stores for "not measured", see <see cref="Analysis.MoodReading.Known"/>.</summary>
        internal bool Known => Brightness >= 0;
    }

    private readonly IReadOnlyList<Song> _ranked;

    /// <param name="notPooled">The genres that a pooled reading of this list leaves out, see
    /// <see cref="MoodBands.NotPooledInto"/>. They are held here and not removed from
    /// <paramref name="ranked"/>, because a genre row from the same list can be entirely about one of
    /// them. The exclusion applies to pooled readings only, not to the songs.</param>
    internal MoodList(IReadOnlyList<Song> ranked, IReadOnlySet<string> notPooled)
    {
        _ranked = ranked;
        _notPooled = notPooled;
    }

    private readonly IReadOnlySet<string> _notPooled;

    internal static readonly MoodList Empty = new([], MoodBands.NotPooledInto(SmartPlaylistKind.Sleep));

    /// <summary>Every song in the list, before any narrowing or exclusion. For <see cref="MoodShelf.MoodsByPath"/>.</summary>
    internal IEnumerable<string> Paths => _ranked.Select(r => r.Path);

    /// <summary>The songs that a pooled reading may use: everything in the mood, except the genres
    /// that this mood does not pool (<see cref="MoodBands.NotPooledInto"/>). A song filed under no
    /// genre is kept, because a missing genre is not evidence against it.</summary>
    private IEnumerable<Song> Pooled =>
        _notPooled.Count == 0 ? _ranked
                              : _ranked.Where(r => r.Genre is null || !_notPooled.Contains(r.Genre));

    /// <summary>Every song in the list, best first, whatever it is filed under. This property is the
    /// stated answer for "all of them", so no caller has to pass a genre argument that it might
    /// forget.</summary>
    public MoodPool WholeList => new(InOneEra([.. Pooled]));

    /// <summary>
    /// The songs in it made in one decade, in the same order the whole list has them.
    ///
    /// <para><b><see cref="InOneEra"/> is not applied.</b> That step stops a list from mixing
    /// decades. This list is one decade by construction, and narrowing it again could only remove the
    /// ends of what was asked for.</para>
    ///
    /// <para><b>An undated song is in no decade.</b> Elsewhere in this file a missing year is a fact
    /// that nobody can judge, and the song is kept. Here the year is the question, and a song that
    /// cannot answer it does not belong in the answer. Both rules follow one principle: do not pretend
    /// to know when something was made.</para>
    /// </summary>
    /// <param name="decade">The first year of the decade, one of <see cref="SmartPlaylists.Decades"/>.</param>
    public MoodPool InDecade(int decade)
    {
        return new([.. Pooled.Where(r => r.Year > 0 && r.Year - r.Year % 10 == decade)]);
    }

    /// <summary>
    /// The songs in it filed under any of <paramref name="shelves"/>, in the same order the whole list
    /// has them.
    ///
    /// <para><b>The argument is a set of shelves, because a list is not always one shelf.</b> "Jazz
    /// Club" is the calm end of Jazz, Blues and Soul together, see <see cref="MoodSets"/>. One shelf is
    /// the ordinary case, and it is a set of one.</para>
    /// </summary>
    /// <param name="shelves">The genres this list draws on, as <see cref="Metadata.Genres.All"/>
    /// spells them. An empty set gives an empty pool and not the whole list: a narrowing to nothing
    /// must not become no narrowing at all.</param>
    /// <param name="prefer">The styles this list is named after, or none, see
    /// <see cref="MoodFlavour.Styles"/>. It is a preference and never a filter, see
    /// <see cref="Preferring"/>.</param>
    public MoodPool Within(IReadOnlyList<string> shelves, IReadOnlyList<string> prefer)
    {
        ArgumentNullException.ThrowIfNull(shelves);
        ArgumentNullException.ThrowIfNull(prefer);

        // A set, because the check runs once for each song in the list and the shelves do not change
        // meanwhile. The comparison is ordinal: both sides are the app's own genre names, not text
        // typed by a tagger.
        var wanted = new HashSet<string>(shelves, StringComparer.Ordinal);

        // This is the one place where a list is re-ranked and not only narrowed.
        //
        // The other methods keep the order they are given: the narrowings drop songs, the caps skip
        // them, and neither reorders, because the ranking decides quality. This method does reorder,
        // because it answers a different question from the list it is cut from. "The calmest music I
        // own" wants the calmest songs of any genre. "The calmest rock I own" wants that, and wants the
        // answer to be rock. So a genre row prefers the songs that sound like the middle of their own
        // genre (see MoodShelf.Ranks and MoodBands.TypicalFor), by a bounded amount that cannot move a
        // song out of the band it was picked from.
        //
        // The path breaks ties, as in the pooled ranking, so two reads of one unchanged shelf give the
        // same order.
        var inGenre = _ranked
            .Where(r => r.Genre is not null && wanted.Contains(r.Genre))
            .OrderByDescending(r => r.WithinGenreBy)
            .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new(InOneEra(Preferring(inGenre, prefer)));
    }

    /// <summary>
    /// The songs that the catalogues say are the kind of thing the list is named after, or all of the
    /// songs when there are too few of those to make a list.
    ///
    /// <para><b>This runs before the era window, not after.</b> Both narrow, and the order decides
    /// what the era is measured over. With the flavour first, the era is "the decade this list's funk
    /// is mostly from", which is the question the pair asks. In the other order it would be the decade
    /// of the whole soul shelf, and the funk would be narrowed to another era.</para>
    ///
    /// <para>The minimum is <see cref="MoodFlavour.LeastToPrefer"/>, which is the length of the list,
    /// so narrowing never leaves a row shorter than it was. A collection whose tags do not carry the
    /// word gets the list unchanged.</para>
    /// </summary>
    internal static IReadOnlyList<Song> Preferring(IReadOnlyList<Song> ranked, IReadOnlyList<string> prefer)
    {
        if (prefer.Count == 0)
            return ranked;

        // The names are folded on both sides and held in a set, because the check runs once for each
        // song in the list and the wanted names do not change meanwhile.
        var wanted = new HashSet<string>(prefer.Select(GenreText.Fold), StringComparer.Ordinal);

        // A liked song counts as flavoured whatever the catalogues filed it under. The style list is
        // the app's evidence that a record is the kind of thing the list is named after. A like is the
        // listener saying so about this song, which is better evidence. InOneEra follows the same rule
        // for the same reason: dropping the song would overrule the listener with a calculation.
        var flavoured = ranked
            .Where(s => s.Liked || s.Styles.Any(style => wanted.Contains(GenreText.Fold(style))))
            .ToList();

        return flavoured.Count >= MoodFlavour.LeastToPrefer ? flavoured : ranked;
    }

    /// <summary>
    /// The list narrowed to one era: a run of neighbouring decades around the decade that holds most
    /// of it.
    ///
    /// <para><b>A mood describes how a song sounds, not when it was made.</b> The danceable end of a
    /// genre can span forty years. Read from top to bottom, that is a playlist that keeps changing
    /// subject, for example a sixties soul list with a French house record in it, or a swing list with
    /// nineties singers. The ranking cannot see the problem, because both records really are
    /// danceable soul. They are not the same music.</para>
    ///
    /// <para><b>The era is taken from the collection and never imposed on it.</b> Nothing here says
    /// that jazz means the forties. The method finds the decade that most of this listener's pool is
    /// in and keeps the run around it. A shelf of modern soul narrows to modern soul, not to an empty
    /// list. That is why this is a mode and not a table of dates: a table would be a guess about a
    /// collection that nobody has seen, and it would empty a list whenever it was wrong.</para>
    ///
    /// <para><b>The window grows over the decades that are present, not over the numbers.</b>
    /// Stepping ten years at a time through a shelf with a gap either loops or stops early. Walking
    /// the decades that hold songs cannot do either, and a gap is not a boundary: a pool of sixties
    /// and eighties records with nothing between them is still one run.</para>
    ///
    /// <para><b>An undated song is kept whatever the window is.</b> It cannot be placed, so it is not
    /// evidence that the list is mixed. Removing it would gut a badly tagged shelf for a fault in the
    /// tags and not in the music. A shelf with no years at all is therefore not narrowed, and
    /// <see cref="SmartPlaylists.LeastForOneEra"/> makes that explicit by refusing to narrow below
    /// it.</para>
    /// </summary>
    internal static IReadOnlyList<Song> InOneEra(IReadOnlyList<Song> ranked)
    {
        var perDecade = new Dictionary<int, int>();
        var dated = 0;
        foreach (var song in ranked)
        {
            if (song.Year <= 0)
                continue;
            dated++;
            var decade = song.Year - song.Year % 10;
            perDecade[decade] = perDecade.GetValueOrDefault(decade) + 1;
        }

        // Too few songs have a year to decide anything. This is not a failure: nobody has put years
        // on this shelf.
        if (dated < SmartPlaylists.LeastForOneEra)
            return ranked;

        // The decade that holds most songs. The earliest decade wins a tie, so two reads of one shelf
        // cannot narrow to different eras. Every generated list's ORDER BY follows the same rule.
        var mode = perDecade.OrderByDescending(d => d.Value).ThenBy(d => d.Key).First().Key;

        var order = perDecade.Keys.OrderBy(d => d).ToList();
        var lo = order.IndexOf(mode);
        var hi = lo;
        var held = perDecade[mode];

        // Grow outward from the mode, always taking the fuller side, until the window holds enough
        // songs. A side that has run out gives -1, and every real count is at least 1, so the other
        // side always wins. This stops the walk at both ends.
        //
        // "Enough" is EnoughInOneEra and not the minimum above. They are two different questions:
        // whether a shelf may be narrowed at all, and how deep the window has to be to draw more than
        // one list from. A shelf that cannot reach that depth in any window keeps all of its decades.
        while (held < SmartPlaylists.EnoughInOneEra && (lo > 0 || hi < order.Count - 1))
        {
            var before = lo > 0 ? perDecade[order[lo - 1]] : -1;
            var after = hi < order.Count - 1 ? perDecade[order[hi + 1]] : -1;

            if (after >= before)
                held += perDecade[order[++hi]];
            else
                held += perDecade[order[--lo]];
        }

        var from = order[lo];
        var until = order[hi];

        // Keep the ranking's own order, which decides quality. This drops songs and never reorders
        // them, the same contract that MoodPool keeps.
        //
        // A liked song is kept whatever the window is. The window is the app's guess at what belongs
        // together. A like means the listener has already answered that question for this song, and
        // dropping it would overrule them with a calculation.
        return [.. ranked.Where(s => s.Liked
                                  || s.Year <= 0
                                  || (s.Year - s.Year % 10 >= from && s.Year - s.Year % 10 <= until))];
    }

    /// <summary>
    /// The songs that a listener's own mood list draws on (<see cref="MoodRecipe"/>): this list
    /// narrowed to the recipe's genres, decades and acts, in the order the whole list has them. An
    /// empty axis does not narrow. With no genres named, the pooled reading is used, as in
    /// <see cref="WholeList"/>. A genre that is named is used even where this mood does not pool it,
    /// because the listener asked for it by name.
    /// <para>An undated song is in no decade, as <see cref="InDecade"/> says.</para>
    /// </summary>
    public MoodPool Matching(MoodRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var genres = recipe.Genres.ToHashSet(StringComparer.Ordinal);
        var decades = recipe.Decades.ToHashSet();
        var from = genres.Count == 0 ? Pooled : _ranked.Where(r => r.Genre is not null && genres.Contains(r.Genre));
        return new([.. from.Where(r => decades.Count == 0 || (r.Year > 0 && decades.Contains(r.Year - r.Year % 10)))
                           .Where(r => recipe.NamesActOf(r.Artist))]);
    }

    /// <summary>The genres this list holds songs of, most songs first, for the window's genre choices.</summary>
    public IReadOnlyList<(string Genre, int Songs)> GenresHeld() =>
        [.. _ranked.Where(r => r.Genre is not null)
                   .GroupBy(r => r.Genre!, StringComparer.Ordinal)
                   .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                   .Select(g => (g.Key, g.Count()))];

    /// <summary>The acts this list holds songs by, each act of a shared credit on its own, most songs first, for the
    /// window's artist suggestions.</summary>
    public IReadOnlyList<(string Act, int Songs)> ActsHeld() =>
        [.. _ranked.Where(r => !string.IsNullOrWhiteSpace(r.Artist))
                   .SelectMany(r => Metadata.Providers.ArtistCredit.Split(r.Artist).DefaultIfEmpty(r.Artist))
                   .GroupBy(a => a.Trim(), StringComparer.OrdinalIgnoreCase)
                   .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                   .Select(g => (g.Key, g.Count()))];

    /// <summary>
    /// The genres worth offering a list of their own, biggest first.
    /// </summary>
    /// <param name="least">The fewest songs a genre needs here to be worth a row, see
    /// <see cref="SmartPlaylists.LeastToShow"/>. A genre with four calm records in it is not a calm
    /// jazz playlist, it is four records.</param>
    public IReadOnlyList<string> GenresWorthListing(int least) =>
        [.. _ranked.Where(r => r.Genre is not null)
                   .GroupBy(r => r.Genre!, StringComparer.Ordinal)
                   .Where(g => g.Count() >= least)
                   .OrderByDescending(g => g.Count())
                   .ThenBy(g => g.Key, StringComparer.Ordinal)
                   .Select(g => g.Key)];
}

/// <summary>
/// The songs that one list may draw on, best first, with nothing yet removed for repeating a record.
///
/// <para><b>The caps belong to a list and not to a pool.</b> A list has at most one song from any
/// one record and five by any one act. The next reading is free to take a different song from the
/// same record. The caps exist so that the listener does not hear an album twice in the list in
/// front of them. They are not meant to stop the listener hearing two songs of an album in a
/// fortnight.</para>
///
/// <para><b>If the caps were applied to the pool, the other songs would be lost.</b> No reading of
/// the list could reach them, and the pool would be as short as the list, so every refresh would be
/// the same songs in a different order. On a real collection, 307 calm electronic tracks over 111
/// records became a pool of 42, and the list was also 42 songs.</para>
///
/// <para>Ranked order is kept throughout. The window decides where reading starts and the caps
/// decide what is skipped on the way. Neither reorders anything, because the ranking decides
/// quality.</para>
/// </summary>
public sealed class MoodPool
{
    private readonly IReadOnlyList<MoodList.Song> _songs;

    internal MoodPool(IReadOnlyList<MoodList.Song> songs) => _songs = songs;

    /// <summary>How many songs are eligible: every song that the narrowings left, before any cap.
    ///
    /// <para>This says whether a new reading has anywhere to go. A pool deeper than the list can show
    /// something different, and a pool that is not deeper cannot. See
    /// <c>LibraryDatabase.SmartPlaylistCanRegenerate</c>, which controls the regenerate button.</para></summary>
    public int Count => _songs.Count;

    /// <summary>The eligible songs themselves, in ranked order and uncapped. For a caller that asks
    /// about the pool and not about a list drawn from it.</summary>
    public IReadOnlyList<string> Songs => [.. _songs.Select(r => r.Path)];

    /// <summary>
    /// Several pools read as one, taking one song from each in turn until all are used.
    ///
    /// <para><b>For a list that draws from more than one band.</b> Two bands never share a song, so
    /// their pools cannot be ranked against each other: no number says whether a loud house record
    /// beats a still one. Laid end to end, the first band would fill the whole list and the second
    /// would never be reached, because a list stops at <see cref="SmartPlaylists.MostSongs"/>. Taken
    /// in turn, every band reaches the listener in proportion to its size, and each band keeps its own
    /// ranking inside its own turn.</para>
    ///
    /// <para>A song in two pools is kept once, at the first place it appears. The cells of one band
    /// are one narrowing and cannot overlap, so this only happens where a record is filed under two of
    /// the shelves that a set names.</para>
    ///
    /// <para>This order decides only what the window reaches first. What is finally played is ordered
    /// by <see cref="InFlow"/>, which re-sequences whatever the window picked.</para>
    /// </summary>
    internal static MoodPool InTurn(IReadOnlyList<MoodPool> pools)
    {
        ArgumentNullException.ThrowIfNull(pools);
        if (pools.Count == 1)
            return pools[0];

        var taken = new List<MoodList.Song>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deepest = pools.Count == 0 ? 0 : pools.Max(p => p._songs.Count);

        for (var round = 0; round < deepest; round++)
            foreach (var pool in pools)
                if (round < pool._songs.Count && seen.Add(pool._songs[round].Path))
                    taken.Add(pool._songs[round]);

        return new MoodPool(taken);
    }

    /// <summary>
    /// The same pool with every refused act and record removed.
    ///
    /// <para><b>The refusals are applied to the pool and not to the list drawn from it.</b> If they
    /// were applied after the window, a refused act would still use up slots: the list would come
    /// back four songs short, and the songs that should have moved up would sit behind a band that
    /// nobody wants to hear. Applied here, the window is spent on songs that can actually appear, and
    /// <see cref="Count"/>, which says whether a new reading has anywhere to go, counts what is
    /// really left.</para>
    ///
    /// <para>This runs before the caps for the same reason. One song per record is a rule about the
    /// list in front of the listener, and a record that cannot appear at all must not take that
    /// place.</para>
    /// </summary>
    /// <param name="kind">Which list is asking. A refusal applies to one kind, see
    /// <see cref="RecommendRefusals"/>.</param>
    public MoodPool Allowed(RecommendRefusals refusals, SmartPlaylistKind kind)
    {
        ArgumentNullException.ThrowIfNull(refusals);
        return refusals.Any
            ? new([.. _songs.Where(s => refusals.Allows(kind, s.Folder, s.Artist))])
            : this;
    }

    /// <summary>
    /// One list: <paramref name="count"/> songs read from <paramref name="from"/> songs in, wrapping,
    /// taking at most <see cref="SmartPlaylists.MostFromOneRecord"/> off any record and
    /// <see cref="SmartPlaylists.MostFromOneArtist"/> by any act.
    ///
    /// <para><b>Why two openings of one list differ.</b> The pool is fixed for the session and the
    /// window moves through it, so a list drawn from a deeper pool is a different list each time it is
    /// opened. Where the caps skipped a record's second song in one reading, a window that starts
    /// further in may take that song and skip the first.</para>
    ///
    /// <para>For a pool that can fill the list, the number of songs returned does not depend on
    /// <paramref name="from"/>, because the walk wraps around the whole pool. The same records can be
    /// reached from every starting point. This lets the pane's subtitle and the list itself agree on
    /// the count while showing different songs.</para>
    /// </summary>
    public IReadOnlyList<string> Window(int count, int from)
        => [.. Picked(count, from).Select(s => s.Path)];

    /// <summary>
    /// Which song of each record a reading takes: the first, then the second, and round again.
    ///
    /// <para>It is built once for each reading, from the pool's own order. A record's songs are
    /// numbered in the order of the ranking, so the first reading takes the best song of each
    /// record.</para>
    /// </summary>
    private readonly struct Turn
    {
        private readonly int[] _place;   // where each song sits among its record's songs
        private readonly int[] _held;    // how many songs its record has in this pool
        private readonly int _reading;

        internal Turn(int[] place, int[] held, int reading)
        {
            _place = place;
            _held = held;
            _reading = reading;
        }

        /// <summary>Whether this reading takes the song at <paramref name="index"/> in the pool.</summary>
        internal bool Takes(int index)
        {
            var held = _held[index];
            if (held <= SmartPlaylists.MostFromOneRecord)
                return true;

            // The places this reading takes, wrapping around the record's own songs.
            var wanted = ((_place[index] - (_reading * SmartPlaylists.MostFromOneRecord)) % held + held) % held;
            return wanted < SmartPlaylists.MostFromOneRecord;
        }
    }

    /// <summary>Number every song within its own record, in the pool's order.</summary>
    private Turn TurnOf(int reading)
    {
        var place = new int[_songs.Count];
        var held = new int[_songs.Count];
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var counted = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var song in _songs)
            counted[song.Folder] = counted.GetValueOrDefault(song.Folder) + 1;

        for (var i = 0; i < _songs.Count; i++)
        {
            var folder = _songs[i].Folder;
            place[i] = seen.GetValueOrDefault(folder);
            seen[folder] = place[i] + 1;
            held[i] = counted[folder];
        }

        return new Turn(place, held, reading);
    }

    /// <summary>
    /// Where reading number <paramref name="reading"/> starts in a pool of <paramref name="songs"/>.
    ///
    /// <para><b>The start moves by one song for each reading, so the step does not depend on the
    /// pool.</b> A fixed step in songs would depend on the pool size. The pool is whatever the
    /// narrowing left, and a pool whose size divides the step would never move. For example, with a
    /// step of 50, a pool of 25 songs always starts at the same place, and every press of "new list"
    /// returns the same songs. On a real collection, four lists were frozen in this way. One of them
    /// showed 11 songs out of a pool of 25, and the other 14 could not be reached.</para>
    ///
    /// <para><b>With one song per reading, every start is visited before any repeats.</b> The
    /// readings walk the whole pool and come back to the beginning only after seeing all of it, so no
    /// song is permanently out of reach. This also keeps <c>LibraryDatabase.SmartPlaylistCanRegenerate</c>
    /// correct. It asks whether the pool is deeper than the list, and every extra song can really be
    /// shown.</para>
    ///
    /// <para><b>A step of one and not a bigger jump.</b> A bigger step moves further through the
    /// pool but can land on the first song of a record. A start on a record's first song picks exactly
    /// what the reading before it picked: the same list from a different place. A step of one always
    /// lands inside the run that the last reading started on, so the record it belongs to gives up a
    /// different song every time. The distance that a reading moves does not make it a new list. The
    /// caps do: they turn one step into a different song from every record that has one to
    /// spare.</para>
    /// </summary>
    private static int StartOf(int reading, int songs)
    {
        if (songs <= 1)
            return 0;

        return (int)(((reading % songs) + songs) % songs);
    }

    /// <summary>
    /// A listener's own mood list (<see cref="MoodRecipe"/>): at most <paramref name="count"/> songs,
    /// best first, then ordered to play as a mood list is (<see cref="MoodFlow.Ordered"/>).
    ///
    /// <para><b>It has its own caps.</b> A mood list takes one song from a record and five from an
    /// act (<see cref="SmartPlaylists.MostFromOneRecord"/>, <see cref="SmartPlaylists.MostFromOneArtist"/>),
    /// so that it does not read as one album or one act. A list made of two named acts is meant to be
    /// those two acts, and under those caps it would hold only ten songs. Where acts are named, a record
    /// gives up to <see cref="RecordsGiveWhenActsNamed"/> songs and no act is capped. Where none are
    /// named, the caps of the mood lists apply.</para>
    /// </summary>
    /// <param name="actsNamed">Whether the recipe names acts. The caller states it from the recipe.
    /// There is no default.</param>
    public IReadOnlyList<string> ForRecipe(
        int count, bool actsNamed, Func<IReadOnlyList<string>, IReadOnlyDictionary<string, MusicalKey>> keysOf)
    {
        ArgumentNullException.ThrowIfNull(keysOf);
        var perRecord = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var perArtist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var recordCap = actsNamed ? RecordsGiveWhenActsNamed : SmartPlaylists.MostFromOneRecord;
        var picked = new List<MoodList.Song>(Math.Min(count, _songs.Count));
        foreach (var song in _songs)
        {
            if (picked.Count >= count)
                break;
            if (perRecord.GetValueOrDefault(song.Folder) >= recordCap)
                continue;
            var credited = !string.IsNullOrWhiteSpace(song.Artist);
            if (!actsNamed && credited && perArtist.GetValueOrDefault(song.Artist) >= SmartPlaylists.MostFromOneArtist)
                continue;
            perRecord[song.Folder] = perRecord.GetValueOrDefault(song.Folder) + 1;
            if (credited)
                perArtist[song.Artist] = perArtist.GetValueOrDefault(song.Artist) + 1;
            picked.Add(song);
        }
        if (picked.Count < MoodFlow.LeastToOrder)
            return [.. picked.Select(s => s.Path)];
        return [.. MoodFlow.Ordered(picked, keysOf([.. picked.Select(s => s.Path)])).Select(s => s.Path)];
    }

    /// <summary>How many songs one record gives a listener's own list that names acts. See <see cref="ForRecipe"/>.</summary>
    public const int RecordsGiveWhenActsNamed = 3;

    /// <summary>
    /// The same list, ordered so that each song sounds like the one before it, and not by how far
    /// into its band it sits. <see cref="MoodFlow"/> is the ordering and the reason for it.
    ///
    /// <para><b>There are two methods and not one method with a flag, because the callers differ.</b>
    /// The pane that asks whether a list can be regenerated needs to know HOW MANY songs a window
    /// gives, and does not use their order. The rule that builds the list that somebody is about to
    /// play needs the order and nothing else. Each caller must say which one it means.
    /// <c>LoudnessScanner.Measure</c> and <c>Scan</c> have the same shape.</para>
    ///
    /// <para>The songs that come back are always the same as those of <see cref="Window"/>. The
    /// selection is the window's and the caps', and this method does not change it.</para>
    ///
    /// <para><b>A run is picked as one and ordered one reading at a time. The two halves are
    /// different on purpose.</b> The picking runs across all of <paramref name="shows"/> times
    /// <paramref name="readings"/>. This makes every reading a different set of records, because the
    /// caps are spent once over the run and not once for each reading. The ordering is for each reading,
    /// because an ordering is a property of the list that somebody plays.
    /// <see cref="MoodFlow.Ordered"/> walks to the nearest-sounding song first and normalises its
    /// distances over whatever it is given. A run of two hundred songs ordered as a whole would have a
    /// first fifty drawn from all over the run, which is not the fifty that this computer shows, in
    /// any order. Ordered one reading at a time, the first reading of a run is the list that this
    /// computer shows, song for song and in the same order, and every later reading is a whole list
    /// and not a slice of a longer one. This lets the phone and this screen agree about a list while
    /// the phone steps its own window (<c>GeneratedLists.window</c>).</para>
    /// </summary>
    /// <param name="shows">How many songs one list has: <see cref="SmartPlaylists.MostSongs"/> for
    /// the kind. It is also where one reading stops and the next begins.</param>
    /// <param name="readings">How many lists to pick. One for a list that somebody is looking at;
    /// <see cref="SmartPlaylists.ReadingsSentFor"/> for a run going to the phone. <b>No default:</b>
    /// a run and a list are picked from the same pool and differ only in this value, and a caller that
    /// forgot it would silently publish the wrong depth.</param>
    /// <param name="keysOf">Called with the songs that this window landed on, to get their keys, see
    /// <see cref="LibraryDatabase.MusicalKeysOf"/>. It is a function and not a dictionary because the
    /// songs are not known until the window and the caps have run. A dictionary would need the songs to
    /// be picked twice: once to learn what to ask about and once to order them. It is called once for
    /// the whole run and not once for each reading. A key belongs to a song, so the one answer orders
    /// every reading in it.</param>
    public IReadOnlyList<string> InFlow(
        int shows, int readings, int from,
        Func<IReadOnlyList<string>, IReadOnlyDictionary<string, MusicalKey>> keysOf)
    {
        ArgumentNullException.ThrowIfNull(keysOf);
        ArgumentOutOfRangeException.ThrowIfLessThan(shows, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(readings, 1);

        var picked = Picked(shows * readings, from);
        if (picked.Count < MoodFlow.LeastToOrder)
            return [.. picked.Select(s => s.Path)];

        var keys = keysOf([.. picked.Select(s => s.Path)]);

        var flowed = new List<string>(picked.Count);
        for (var at = 0; at < picked.Count; at += shows)
        {
            // One reading's own songs, ordered among themselves. A block shorter than
            // MoodFlow.LeastToOrder is returned as it stands, which is what Ordered does with one.
            var reading = new List<MoodList.Song>(Math.Min(shows, picked.Count - at));
            for (var i = at; i < picked.Count && i < at + shows; i++)
                reading.Add(picked[i]);

            flowed.AddRange(MoodFlow.Ordered(reading, keys).Select(s => s.Path));
        }

        return flowed;
    }

    private IReadOnlyList<MoodList.Song> Picked(int count, int from)
    {
        if (_songs.Count == 0 || count <= 0)
            return [];

        var at = StartOf(from, _songs.Count);
        var turn = TurnOf(from);

        var perRecord = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var perArtist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var picked = new List<MoodList.Song>(Math.Min(count, _songs.Count));

        for (var i = 0; i < _songs.Count && picked.Count < count; i++)
        {
            var index = (at + i) % _songs.Count;
            var song = _songs[index];

            // ── Which of this record's songs this reading takes ──
            //
            // A record that shows one song in one reading must not be used up for the next reading.
            // For example, a pool of 36 songs over 14 records, with one record holding 13 of the 36,
            // showed 14 songs and left 22 unshown. Rotating a single start pointer moved only one of
            // them for each press, because a start only changes which song the record it lands in gives
            // up.
            //
            // So the reading picks the turn and not the place: each record hands over its first song,
            // then its second, and comes back to its first only after every one of them has been heard.
            // A record with 13 songs in the pool has 13 readings. A record with a single song has one,
            // and repeats it, which is correct for a record with nothing else to give.
            //
            // The start rotates as well, and both are needed. The turn refreshes a list that shows
            // every record it has. The start refreshes a list so long that the caps ran out of room
            // before the records did, where what has to change is which records are reached at all.
            if (!song.Liked && !turn.Takes(index))
                continue;

            // ── The caps, and the one kind of song that is exempt from them ──
            //
            // A song the listener has liked for this list is never skipped by a cap. The caps exist so
            // that a list does not read as one album or one act. A like is the listener saying that this
            // song belongs here, and the caps are the app's guess about the same question. Where the
            // two disagree, the listener wins. InOneEra and Preferring follow the same rule.
            //
            // A liked song still counts towards the caps, so this is not a hole. Three liked songs from
            // one record all appear, and a fourth song that nobody has said anything about does not.
            // The listener's own picks always stay and the app's own picks stay capped. A liked song
            // that was exempt and also not counted would let one record fill the rest of the list
            // behind it.
            if (!song.Liked
                && perRecord.GetValueOrDefault(song.Folder) >= SmartPlaylists.MostFromOneRecord)
                continue;

            // An empty credit is not one act with a hundred songs: a shelf of untagged files would
            // otherwise be capped at five between all of them.
            var credited = !string.IsNullOrWhiteSpace(song.Artist);
            if (!song.Liked && credited
                && perArtist.GetValueOrDefault(song.Artist) >= SmartPlaylists.MostFromOneArtist)
                continue;

            perRecord[song.Folder] = perRecord.GetValueOrDefault(song.Folder) + 1;
            if (credited)
                perArtist[song.Artist] = perArtist.GetValueOrDefault(song.Artist) + 1;

            picked.Add(song);
        }

        return picked;
    }
}

/// <summary>
/// The collection's moods, cut once when the session starts: the quiet end of each kind of music,
/// the busy end, the danceable, and the music somebody could fall asleep to.
///
/// <para><b>The cut is made once and then held. This is a decision and not a cache.</b> The
/// measuring pass fills a shelf over many launches, one track at a time, so the distribution that
/// the bands are cut from keeps changing. If the cut were repeated at every reading, a list called
/// "Calm" would hold different songs each time it was opened, and songs would leave it while nobody
/// had changed anything. That looks like a bug, and nothing on screen explains it. So the cut is
/// taken once, at launch, and every list drawn in that session agrees with every other.</para>
///
/// <para><b>Four of the five are cut within a genre and the fifth is not.</b> Calm, Energetic,
/// Danceable and Focus ask where a record sits among its own kind, so each of them can be broken out
/// per genre, and "calm" means calm for rock. <see cref="Sleep"/> asks an absolute question: is this
/// quiet and still enough to fall asleep to? It has no genre reading at all. The calmest techno on
/// the shelf is calm, and nobody sleeps through it. See <see cref="MoodBands.SleepAt"/>.</para>
///
/// <para><b>Nothing here is stored.</b> The three columns stay the raw numbers that the meter
/// produced. The bands are calculated from them, so the recipe can change without a migration or a
/// stale row.</para>
///
/// <para>Pure and database-free, like <see cref="GenreWall"/> and <see cref="HomeShelf"/>.</para>
/// </summary>
public sealed class MoodShelf
{
    /// <summary>The calm end of every genre, most clearly calm first.</summary>
    public MoodList Calm { get; }

    /// <summary>The busy end of every genre, most clearly energetic first.</summary>
    public MoodList Energetic { get; }

    /// <summary>Busy songs with a steady level, most driving first.</summary>
    public MoodList Danceable { get; }

    /// <summary>Sparse songs with a steady level, stillest first. This is the other half of the axis
    /// that <see cref="Danceable"/> reads, and the two never hold the same records.</summary>
    public MoodList Focus { get; }

    /// <summary>Quiet, dark songs with no swing, stillest first. Absolute and not per
    /// genre.</summary>
    public MoodList Sleep { get; }

    /// <summary>How many measured tracks the cut was taken from. Zero means the pass has not run,
    /// which is different from a collection with no calm music in it.</summary>
    public int MeasuredTracks { get; }

    private MoodShelf(
        MoodList calm, MoodList energetic, MoodList danceable, MoodList focus, MoodList sleep,
        int measured)
    {
        Calm = calm;
        Energetic = energetic;
        Danceable = danceable;
        Focus = focus;
        Sleep = sleep;
        MeasuredTracks = measured;
    }

    /// <summary>
    /// The list that one mood kind stands for.
    ///
    /// <para>It is here and not at each caller because two callers need it: the rule that queries a
    /// list and the pane that decides which genre rows to draw under it. A kind mapped to a list in
    /// two places could end up mapped to two different lists.</para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The kind is not one of the moods. This is not an
    /// empty list on purpose: asking the mood shelf about a kind it does not hold is a mistake at the
    /// call site, and an empty answer would hide it behind a row that is never drawn.</exception>
    public MoodList ListFor(SmartPlaylistKind kind) => kind switch
    {
        SmartPlaylistKind.Calm => Calm,
        SmartPlaylistKind.Energetic => Energetic,
        SmartPlaylistKind.Danceable => Danceable,
        SmartPlaylistKind.Focus => Focus,
        SmartPlaylistKind.Sleep => Sleep,
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind), kind, "This generated playlist is not one the mood shelf answers."),
    };

    /// <summary>
    /// Which of the five lists each song is in, by relative path. A song in none of them is absent.
    ///
    /// <para>Built on each call. It is read once when a song radio starts (<see cref="RadioCrate"/>),
    /// which treats two songs in the same list as closer in mood.</para>
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlySet<SmartPlaylistKind>> MoodsByPath()
    {
        var byPath = new Dictionary<string, HashSet<SmartPlaylistKind>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kind in (SmartPlaylistKind[])[
                     SmartPlaylistKind.Calm, SmartPlaylistKind.Energetic, SmartPlaylistKind.Danceable,
                     SmartPlaylistKind.Focus, SmartPlaylistKind.Sleep])
        {
            foreach (var path in ListFor(kind).Paths)
            {
                if (!byPath.TryGetValue(path, out var kinds))
                    byPath[path] = kinds = [];
                kinds.Add(kind);
            }
        }

        return byPath.ToDictionary(
            p => p.Key, p => (IReadOnlySet<SmartPlaylistKind>)p.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>An unmeasured collection: five empty lists. This is a shelf before the pass has
    /// measured anything, and every caller gets it instead of a null to test for.</summary>
    public static readonly MoodShelf Unmeasured =
        new(MoodList.Empty, MoodList.Empty, MoodList.Empty, MoodList.Empty, MoodList.Empty, 0);

    /// <summary>
    /// Cuts the bands and ranks each list in the order it is read.
    /// </summary>
    /// <param name="measured">Every track that has a reading,
    /// <see cref="LibraryDatabase.QueryMeasuredMoods"/>.</param>
    /// <param name="genreByFolder">What each folder is filed under, in canonical form, see
    /// <see cref="MoodBands.GenreByFolder"/>.</param>
    /// <param name="likedByKind">Which lists the listener has said each song belongs in, see
    /// <see cref="LibraryDatabase.LikedByKind"/>. It is per kind, because a verdict is about one list.
    /// A song kept inside the Chill window by a like is not kept inside the Dancefloor window by it,
    /// and a shelf-wide reading would let one press speak for five lists.</param>
    public static MoodShelf Build(
        IReadOnlyList<MeasuredMood> measured,
        IReadOnlyDictionary<string, string> genreByFolder,
        IReadOnlyDictionary<string, CatalogueFacts> factsByFolder,
        IReadOnlyDictionary<string, IReadOnlySet<SmartPlaylistKind>> likedByKind)
    {
        ArgumentNullException.ThrowIfNull(likedByKind);
        ArgumentNullException.ThrowIfNull(measured);
        ArgumentNullException.ThrowIfNull(genreByFolder);
        ArgumentNullException.ThrowIfNull(factsByFolder);


        if (measured.Count == 0)
            return Unmeasured;

        var bands = MoodBands.Cut(measured, genreByFolder);

        // Read once for the whole cut, like the bands, so every Chill row drawn in this session is
        // placed against one reading of the collection. See LyricLift: only Chill uses it.
        var words = LyricLift.Over(measured);

        // Ranked, not only collected. A generated list is read from the top and stops at
        // SmartPlaylists.MaxTracks, so the order decides what is in the list and not only how it
        // looks: the two hundred most clearly calm records, and not two hundred calm records in
        // whatever order the database returned them.
        var calm = new List<(string Path, string? Genre, double By, double WithinBy)>();
        var energetic = new List<(string Path, string? Genre, double By, double WithinBy)>();
        var danceable = new List<(string Path, string? Genre, double By, double WithinBy)>();
        var focus = new List<(string Path, string? Genre, double By, double WithinBy)>();
        var sleep = new List<(string Path, string? Genre, double By, double WithinBy)>();

        foreach (var track in measured)
        {
            var label = bands.LabelFor(track, genreByFolder);

            switch (label.Band)
            {
                case MoodBand.Calm:
                    // The only place where the words are read. Confidence is what the measurements
                    // said. The lift is what the words add to it. It is bounded so that it can only
                    // reorder songs that the measurements already put near each other, and it is 0
                    // for a song that nobody has read, which is most of them.
                    calm.Add(Ranks(track, label.Genre, label.Confidence + words.For(track),
                                   bands, genreByFolder, SmartPlaylistKind.Calm));
                    break;
                case MoodBand.Energetic:
                    energetic.Add(Ranks(track, label.Genre, label.Confidence,
                                        bands, genreByFolder, SmartPlaylistKind.Energetic));
                    break;
                case MoodBand.None:
                    break;
            }

            if (label.Danceable)
                danceable.Add(Ranks(track, label.Genre, label.Drive,
                                    bands, genreByFolder, SmartPlaylistKind.Danceable));

            // A track is never both danceable and focused: one wants busy and the other wants sparse,
            // and the two cuts do not meet. It is asked beside the band and not inside it, like
            // danceable, because a record can be focused and at neither end of its genre's arousal.
            // Most of this list is made of such records.
            if (label.Focused)
                focus.Add(Ranks(track, label.Genre, label.Stillness,
                                bands, genreByFolder, SmartPlaylistKind.Focus));

            // Sleep is asked separately and against the whole collection, so a record can be sleep
            // music and be in no band at all. Most are, because a nocturne is unremarkable among
            // classical music.
            //
            // Three things that the measurement cannot decide are decided here. First, a genre that is
            // never sleep music whatever it measures (MoodBands.NeverSleep). Second, a track marked
            // explicit, which is not a sound but is not what anybody means by this list. Third, below,
            // whether the whole record is sleep music and not only this track of it.
            if (label.Genre is not null && MoodBands.NeverSleep.Contains(label.Genre))
                continue;
            if (track.IsExplicit)
                continue;
            if (bands.Sleepiness(track) is > 0 and var sleepiness)
                // Sleep has no genre rows, so the second order is the first. See BrokenOutByGenre.
                sleep.Add((track.RelativePath, label.Genre, sleepiness, sleepiness));
        }

        return new MoodShelf(
            Ranked(calm, measured, factsByFolder, SmartPlaylistKind.Calm, likedByKind),
            Ranked(energetic, measured, factsByFolder, SmartPlaylistKind.Energetic, likedByKind),
            Ranked(danceable, measured, factsByFolder, SmartPlaylistKind.Danceable, likedByKind),
            Ranked(focus, measured, factsByFolder, SmartPlaylistKind.Focus, likedByKind),
            SpreadAcrossRecords(
                OffSleepyRecords(sleep, measured), measured, factsByFolder, likedByKind),
            measured.Count);
    }

    /// <summary>
    /// Keeps only the sleep candidates whose record is broadly sleep music too.
    ///
    /// <para>The meter judges a track and a listener judges a record. For example, four tracks by one
    /// vaporwave artist measure as the stillest on a real shelf, but they are the ambient background
    /// of an album that is not sleep music. Every number about them is right and the list would still
    /// be wrong. So a record has to provide <see cref="MoodBands.SleepShareOfRecord"/> of itself, and
    /// at least <see cref="MoodBands.SleepLeastOnRecord"/> tracks, before any of it is offered.</para>
    ///
    /// <para>The share is taken over the record's measured tracks and not over all of them, because
    /// those are the only ones that anything is known about. While the sweep is still walking a
    /// collection, a record with two of its ten tracks read is a record two tracks long as far as this
    /// method can tell. So a partly measured album qualifies more easily than a finished one. This is
    /// the safe direction: the error gives a shorter list and not a wrong one.</para>
    /// </summary>
    private static List<(string Path, string? Genre, double By, double WithinBy)> OffSleepyRecords(
        List<(string Path, string? Genre, double By, double WithinBy)> candidates,
        IReadOnlyList<MeasuredMood> measured)
    {
        var folderOf = measured.ToDictionary(m => m.RelativePath, m => m.Folder, StringComparer.OrdinalIgnoreCase);

        var measuredPerFolder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in measured)
            measuredPerFolder[m.Folder] = measuredPerFolder.GetValueOrDefault(m.Folder) + 1;

        var sleepyPerFolder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
            if (folderOf.TryGetValue(c.Path, out var folder))
                sleepyPerFolder[folder] = sleepyPerFolder.GetValueOrDefault(folder) + 1;

        return [.. candidates.Where(c =>
        {
            if (!folderOf.TryGetValue(c.Path, out var folder))
                return false;
            int sleepy = sleepyPerFolder.GetValueOrDefault(folder);
            int all = measuredPerFolder.GetValueOrDefault(folder);
            return sleepy >= MoodBands.SleepLeastOnRecord
                && all > 0
                && (double)sleepy / all >= MoodBands.SleepShareOfRecord;
        })];
    }

    /// <summary>
    /// The sleep list, ordered one record at a time and not strictly by stillness: the best track of
    /// every record, then the second best of every record, and so on.
    ///
    /// <para><b>The list is read from the top and stops at fifty</b>
    /// (<see cref="SmartPlaylists.MostSongs"/>). Ranked strictly, the fifty stillest songs on a real
    /// collection were fifteen by one artist and came from seventeen artists in all: the same record
    /// over and over, which is what somebody who puts on a sleep list least wants. Spread this way, the
    /// same fifty songs came from twenty-two artists, and every prefix of the list is varied and not
    /// only the whole list.</para>
    ///
    /// <para>Records are taken in the order of their best track, so the stillest record still leads
    /// and the order is still about the measurement. This is the only list ordered this way. The other
    /// lists are read to find something, and putting the most clearly calm record behind a round of
    /// one song per album would make them worse at their job.</para>
    /// </summary>
    private static MoodList SpreadAcrossRecords(
        List<(string Path, string? Genre, double By, double WithinBy)> rows,
        IReadOnlyList<MeasuredMood> measured,
        IReadOnlyDictionary<string, CatalogueFacts> factsByFolder,
        IReadOnlyDictionary<string, IReadOnlySet<SmartPlaylistKind>> likedByKind)
    {
        var folderOf = measured.ToDictionary(m => m.RelativePath, m => m.Folder, StringComparer.OrdinalIgnoreCase);

        var byPath = new Dictionary<string, MeasuredMood>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in measured)
            byPath[m.RelativePath] = m;

        // A folder the catalogues have not reached has no styles, which is the same empty answer as one
        // they described in words RPM does not know. Both mean "this record is not narrowed".
        IReadOnlyList<string> StylesOf(string folder)
            => factsByFolder.TryGetValue(folder, out var facts) ? facts.Styles : [];

        // Sleep is one list and has no genre rows, so a like on it can only have been said of sleep.
        // It is still asked in the same way as for every other list and not read as a flag: one rule
        // in one place.
        bool LikedHere(string path)
            => likedByKind.TryGetValue(path, out var kinds) && kinds.Contains(SmartPlaylistKind.Sleep);

        // The turn-taking itself is RecordTurns, which the decade charts also read: one rule in one
        // place. A path that the folder map does not carry takes its own turn under its own name.
        var spread = RecordTurns
            .Taken(
                rows.OrderByDescending(r => r.By).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase),
                r => folderOf.GetValueOrDefault(r.Path, r.Path))
            .Select(song => byPath.TryGetValue(song.Path, out var m)
                ? new MoodList.Song(song.Path, song.Genre, m.Folder, m.Artist, m.Year, LikedHere(song.Path), StylesOf(m.Folder), SoundOf(m), song.WithinBy)
                : new MoodList.Song(song.Path, song.Genre, song.Path, "", 0, false, [], MoodList.Sound.Nothing, song.WithinBy))
            .ToList();

        // The sleep list needs no pooled exclusion: MoodBands.NeverSleep has already kept rapping and
        // spoken word out of it at the source. It can do that because sleep has no genre rows that
        // need those songs.
        return new MoodList(spread, MoodBands.NotPooledInto(SmartPlaylistKind.Sleep));
    }

    /// <summary>The three numbers of a measured row, as the song carries them. It is one line in one
    /// place, so the two builds below cannot put them on in a different order.</summary>
    private static MoodList.Sound SoundOf(MeasuredMood m) => new(m.Brightness, m.Onsets, m.Dynamics);

    /// <summary>
    /// One ranked row: where the song sits in the pooled list, and where it sits in its own genre's
    /// row.
    ///
    /// <para>There are two orders because the two lists ask different questions. "The calmest music I
    /// own" wants the calmest songs of any genre. "The calmest rock I own" wants that and wants the
    /// answer to be rock, so a genre row prefers the songs that sound like the middle of their genre,
    /// on whatever axis the band has not already used. See <see cref="MoodBands.TypicalFor"/>, which is
    /// that measurement and explains which axis it reads.</para>
    ///
    /// <para>The preference is bounded by <see cref="MoodList.TypicalWeight"/>, so it reorders songs
    /// that the band put together and never moves one across the band. A song is in the row because
    /// the measurements put it there.</para>
    /// </summary>
    private static (string Path, string? Genre, double By, double WithinBy) Ranks(
        MeasuredMood track, string? genre, double by, MoodBands bands,
        IReadOnlyDictionary<string, string> genreByFolder, SmartPlaylistKind kind)
    {
        var typical = bands.TypicalFor(track, genreByFolder, kind);
        return (track.RelativePath, genre, by, by + (MoodList.TypicalWeight * ((2 * typical) - 1)));
    }

    /// <summary>Strongest first, and the path breaks ties. Two reads of an unchanged shelf must
    /// come back in the same order, and the strengths tie often: every track in a cohort smaller
    /// than <c>MoodBands.LeastForOwnCut * 4</c> is capped at the same number by the same calculation.
    /// </summary>
    private static MoodList Ranked(
        List<(string Path, string? Genre, double By, double WithinBy)> rows, IReadOnlyList<MeasuredMood> measured,
        IReadOnlyDictionary<string, CatalogueFacts> factsByFolder,
        SmartPlaylistKind kind,
        IReadOnlyDictionary<string, IReadOnlySet<SmartPlaylistKind>> likedByKind)
    {
        var byPath = new Dictionary<string, MeasuredMood>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in measured)
            byPath[m.RelativePath] = m;

        IReadOnlyList<string> StylesOf(string folder)
            => factsByFolder.TryGetValue(folder, out var facts) ? facts.Styles : [];

        // Said of this list only. A like on the chill row says nothing about the dancefloor one.
        bool LikedHere(string path)
            => likedByKind.TryGetValue(path, out var kinds) && kinds.Contains(kind);

        return new([.. rows.OrderByDescending(r => r.By)
                           .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
                           .Select(r => byPath.TryGetValue(r.Path, out var m)
                               ? new MoodList.Song(r.Path, r.Genre, m.Folder, m.Artist, m.Year, LikedHere(r.Path), StylesOf(m.Folder), SoundOf(m), r.WithinBy)
                               // A path that the measured rows do not carry: its own folder, no act
                               // and no year, which reads as a song that cannot be placed in a decade
                               // and not as one from the year 0.
                               : new MoodList.Song(r.Path, r.Genre, r.Path, "", 0, false, [], MoodList.Sound.Nothing, r.WithinBy))],
                    MoodBands.NotPooledInto(kind));
    }
}
