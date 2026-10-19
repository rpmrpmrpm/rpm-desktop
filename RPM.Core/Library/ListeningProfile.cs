namespace RPM.Core.Library;

/// <summary>How far back a roll-up reads.
///
/// <para><b>Every window is stated.</b> <see cref="Everything"/> says "all of the log" out loud, rather
/// than leaving an argument off. <c>LibraryDatabase.WholeQueue</c> and
/// <see cref="SmartPlaylistId.WholeShelf"/> follow the same rule. A window that defaults to absent is one
/// that somebody will forget. It then fails silently, by answering with five years of listening when the
/// question was about this month.</para>
/// </summary>
/// <param name="Days">How many days back, or null for the whole log.</param>
public readonly record struct ListeningWindow(int? Days)
{
    /// <summary>Every listen the log still holds, as far back as
    /// <see cref="LibraryDatabase.ListenKeepDays"/> allows.</summary>
    public static ListeningWindow Everything { get; } = new((int?)null);

    /// <summary>The last <paramref name="days"/> days.</summary>
    public static ListeningWindow Last(int days)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(days);
        return new(days);
    }

    /// <summary>A month. Most habit questions mean this window: long enough to hold a pattern, and short
    /// enough to show who the listener is now rather than who they were.</summary>
    public static ListeningWindow LastMonth { get; } = new(30);

    /// <summary>The earliest tick this window takes in, or null where it takes in all of them. Null is used
    /// rather than <see cref="long.MinValue"/>, so the read can leave the clause off entirely. Otherwise
    /// SQLite would compare every row against a number that nothing can be below.</summary>
    internal long? SinceUtcTicks(long nowUtcTicks) =>
        Days is { } days ? nowUtcTicks - days * TimeSpan.TicksPerDay : null;
}

/// <summary>Which part of the day a listen happened in: the bucket that the hour on the row falls into.
///
/// <para>There are four buckets, not twenty-four. Twenty-four buckets over one person's listening is mostly
/// zeros. The pattern is in the shape of the day rather than in the hour. A listener who plays jazz after
/// eleven and something loud at eight in the morning is clear at this resolution, and is noise at the
/// other.</para>
/// </summary>
public enum DayPart
{
    /// <summary>Late and early: from <see cref="DayParts.NightFrom"/> until
    /// <see cref="DayParts.NightUntil"/>. This is the one part that wraps around midnight. That is why it
    /// is written as two comparisons, not as a range.</summary>
    Night,

    Morning,
    Afternoon,
    Evening,
}

/// <summary>Where the day is cut, stated once.
///
/// <para><b>The night edges belong to the listening rules.</b> They are
/// <see cref="SmartPlaylists.NightFromHour"/> and <see cref="SmartPlaylists.NightUntilHour"/>, which is the
/// same night that the "Late night" generated playlist uses. Two answers to "is this the middle of the
/// night" would mean one app disagreeing with itself about the same hour on the same evening.</para>
///
/// <para><b>Folded here, not in SQL.</b> The read groups by the raw hour, and this turns that hour into a
/// part. So there is exactly one statement of where the day is cut. A CASE in the query would be a second
/// statement, and the two would drift apart the first time an edge moved.</para>
/// </summary>
public static class DayParts
{
    /// <summary>Night begins here. This is the same hour that <see cref="SmartPlaylists.NightFromHour"/>
    /// names.</summary>
    public const int NightFrom = SmartPlaylists.NightFromHour;

    /// <summary>Night ends here.</summary>
    public const int NightUntil = SmartPlaylists.NightUntilHour;

    /// <summary>Morning runs from the end of night until this hour.</summary>
    public const int MorningUntil = 12;

    /// <summary>Afternoon runs until this hour. After it, evening lasts until night begins.</summary>
    public const int AfternoonUntil = 18;

    /// <summary>The four parts, in the order a day goes through them, starting from the morning. This is
    /// not the enum declaration order. <see cref="DayPart.Night"/> is declared first because it is the part
    /// with the wrap rule. A chart drawn in declaration order would start the day at eleven at
    /// night.</summary>
    public static IReadOnlyList<DayPart> AcrossTheDay { get; } =
        [DayPart.Morning, DayPart.Afternoon, DayPart.Evening, DayPart.Night];

    /// <summary>Which part an hour on the listener's own clock belongs to.</summary>
    /// <param name="localHour">0 to 23, as <c>ListenEntry.LocalHour</c> stamped it.</param>
    public static DayPart Of(int localHour)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(localHour);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(localHour, 23);

        // Night is checked first, as two comparisons. It is the one part that wraps midnight, so it cannot
        // be written as "below the next edge".
        if (localHour >= NightFrom || localHour < NightUntil)
            return DayPart.Night;
        if (localHour < MorningUntil)
            return DayPart.Morning;
        if (localHour < AfternoonUntil)
            return DayPart.Afternoon;
        return DayPart.Evening;
    }
}

/// <summary>How much listening one line of a roll-up stands for.
///
/// <para>Plays and skips are carried side by side, and they are not split off at the read. They answer
/// different questions from the same rows. A second query to get the other half would be a second scan, and
/// it could disagree with the first. What a listener plays is taste. What they consistently skip is also
/// taste, and it is a stronger signal.</para>
/// </summary>
/// <param name="Plays">Listens that counted. See <c>Listen.Counts</c>, which decided it at the time and
/// wrote the answer down. A later change to the rule therefore cannot rewrite what somebody did.</param>
/// <param name="Skips">Listens that did not count.</param>
/// <param name="Seconds">Audio actually delivered, plays and skips together. This is the time the line cost
/// the listener. It is not the same as the total length of the songs in it.</param>
public readonly record struct ListeningTally(int Plays, int Skips, double Seconds)
{
    /// <summary>Nothing listened to. Every bucket that nobody has played returns this.</summary>
    public static ListeningTally None { get; }

    /// <summary>Everything that happened, whether it counted or not.</summary>
    public int Listens => Plays + Skips;

    public static ListeningTally operator +(ListeningTally a, ListeningTally b) =>
        new(a.Plays + b.Plays, a.Skips + b.Skips, a.Seconds + b.Seconds);

    /// <summary>The named form of <c>operator +</c>. The analyzers call this one.</summary>
    public static ListeningTally Add(ListeningTally a, ListeningTally b) => a + b;
}

/// <summary>One genre's standing in some part of the listener's week.
/// </summary>
/// <param name="Genre">One of <see cref="Metadata.Genres.All"/>. A record whose tags name no genre the app
/// recognises is not a genre of its own, so it is not listed here. See
/// <see cref="MoodBands.GenreByFolder"/>, which uses the same rule for the same reason.</param>
/// <param name="Tally">What was played of it.</param>
/// <param name="Share">This genre's plays as a fraction of every genred play in that bucket. The shares in
/// one bucket add up to one, so they can be used as weights. This is not a fraction of everything played. A
/// collection where half the records are untagged would otherwise report every genre as a minority. That
/// would say something about the tags, not about the listener.</param>
public readonly record struct GenreShare(string Genre, ListeningTally Tally, double Share);

/// <summary>An act the listener actually plays, and when they last did.</summary>
/// <param name="Name">Spelled the way the collection spells them. See <see cref="ArtistCanonicalizer"/>.
/// Variants fold together, so a band that is spelled four ways in the tags is one line here, not
/// four.</param>
/// <param name="LastAtUtcTicks">The most recent listen of any kind. This lets "who are they into now" be
/// answered from this value without a second read.</param>
public readonly record struct PlayedArtist(string Name, ListeningTally Tally, long LastAtUtcTicks);

/// <summary>A record the listener actually plays.</summary>
/// <param name="Folder">The album's folder. This app means the folder when it says which record something
/// is. It is the same key that <c>AlbumFactsRow</c>, the mood shelf's per-record cap and the favourites all
/// use.</param>
/// <param name="Title">Its album tag, or null where nothing on it carries one. The title is not filled in
/// from the folder name here. What a nameless record should be called is for the code that draws it to
/// decide. A made-up title in a roll-up would go into data that other code joins against.</param>
public readonly record struct PlayedAlbum(
    string Folder, string? Title, string? Artist, ListeningTally Tally, long LastAtUtcTicks);

/// <summary>One album folder's listening in one hour of one weekday, as the read hands it over.</summary>
public readonly record struct ListenedRecord(
    string Folder, string? Album, string? Artist,
    int LocalHour, int LocalDayOfWeek, bool Counted,
    int Listens, double Seconds, long LastAtUtcTicks);

/// <summary>One track credit's listening, as the read hands it over. It is not broken down by hour. Who a
/// listener plays and when they play them are two questions. Crossing them here would make a table of
/// mostly zeros that nothing asked for.</summary>
public readonly record struct ListenedCredit(
    string Artist, bool Counted, int Listens, double Seconds, long LastAtUtcTicks);

/// <summary>What the listening log says about the listener, rolled up: which kinds of music they play at
/// which part of the day, which they play on which day of the week, and who and what they play most.
///
/// <para><b>Read when it is asked for, never held.</b> The mood shelf is cut once and kept for the session,
/// because a list that reshuffled under a still hand looks like a bug. A profile is the opposite kind of
/// thing. It is a description rather than a list, and nothing drawn from it is a set of rows that the user
/// would notice moving. A profile that was kept would go stale, and it would quietly answer with the habits
/// of whenever it was built. It is derived from a fact that holds whenever it is asked.</para>
///
/// <para><b>What is NOT in it: reachability.</b> A generated list narrows to what can actually be played
/// right now, because a list exists to be played. A profile is a record of the past. A record that has
/// since gone off the network was still played on the evening it was played. Filtering it out would make it
/// look as though the listener's taste changed when a device went offline.</para>
/// </summary>
public sealed class ListeningProfile
{
    private readonly Dictionary<DayPart, ListeningTally> _byPart;
    private readonly Dictionary<DayOfWeek, ListeningTally> _byDay;
    private readonly Dictionary<DayPart, List<GenreShare>> _genresByPart;
    private readonly Dictionary<DayOfWeek, List<GenreShare>> _genresByDay;

    private ListeningProfile(
        ListeningWindow window,
        ListeningTally whole,
        Dictionary<DayPart, ListeningTally> byPart,
        Dictionary<DayOfWeek, ListeningTally> byDay,
        IReadOnlyList<GenreShare> genres,
        Dictionary<DayPart, List<GenreShare>> genresByPart,
        Dictionary<DayOfWeek, List<GenreShare>> genresByDay,
        IReadOnlyList<PlayedArtist> artists,
        IReadOnlyList<PlayedAlbum> albums)
    {
        Window = window;
        Whole = whole;
        _byPart = byPart;
        _byDay = byDay;
        _genresByPart = genresByPart;
        _genresByDay = genresByDay;
        Artists = artists;
        Genres = genres;
        Albums = albums;
    }

    /// <summary>The profile of a listener who has not listened to anything yet. An empty log, a fresh
    /// mirror and a window with nothing in it all return this. Every reader below answers safely from it,
    /// so callers do not need to test for it first.</summary>
    public static ListeningProfile Nothing { get; } = new(
        ListeningWindow.Everything, ListeningTally.None, [], [], [], [], [], [], []);

    /// <summary>How much listening a line needs before it is worth leaning on.
    ///
    /// <para>The cut is not applied here. Every line is returned, ranked, and the caller states its own
    /// cut. The number is written here because it needs one home. A share of 100 per cent taken from two
    /// plays is arithmetic, not a habit. Anything that chooses music from this should say clearly how much
    /// evidence it wanted.</para>
    /// </summary>
    public const int LeastToLean = 8;

    /// <summary>Which window this was read over. It is carried on the profile, so a caller that stores or
    /// reports a profile cannot lose the fact of what it describes.</summary>
    public ListeningWindow Window { get; }

    /// <summary>Everything in the window, whatever it was and whenever it happened.</summary>
    public ListeningTally Whole { get; }

    /// <summary>The acts they play, most played first, then most recent. Every credit in the window is
    /// included. The cut is the caller's to make. See <see cref="LeastToLean"/>.</summary>
    public IReadOnlyList<PlayedArtist> Artists { get; }

    /// <summary>The kinds of music they play across the WHOLE window, most played first, each with its
    /// share of the genred plays in it.
    /// </summary>
    /// <remarks>This is the same roll-up that <see cref="GenresAt"/> gives for one part of the day, but
    /// over everything. It is its own list, not a sum of the seven weekday buckets at the call site. A
    /// caller that added those up would also have to recalculate the shares. Two callers doing that would
    /// give two answers to "what does this person listen to".
    ///
    /// <para>Use this when the question is about taste rather than a moment: what somebody is into, not
    /// what suits the hour. <see cref="GenresAt"/> answers the other question. It stays the right one for
    /// anything that reads the clock.</para>
    /// </remarks>
    public IReadOnlyList<GenreShare> Genres { get; }

    /// <summary>The records they play, ordered the same way.</summary>
    public IReadOnlyList<PlayedAlbum> Albums { get; }

    /// <summary>What was listened to in one part of the day, across the whole window.</summary>
    public ListeningTally At(DayPart part) => _byPart.GetValueOrDefault(part);

    /// <summary>The same for one day of the week.</summary>
    public ListeningTally On(DayOfWeek day) => _byDay.GetValueOrDefault(day);

    /// <summary>What kinds of music this listener actually plays at that time of day, most played first,
    /// each with its share of the genred plays in that part.
    ///
    /// <para>This is the roll-up that anything picking music for "now" needs. Read the clock, fold it with
    /// <see cref="DayParts.Of"/>, and the shares are the weights.</para>
    /// </summary>
    public IReadOnlyList<GenreShare> GenresAt(DayPart part) =>
        _genresByPart.TryGetValue(part, out var shares) ? shares : [];

    /// <summary>The same question, asked of the weekday. This is a separate axis, not a cross with the
    /// hour. Saturday afternoon and Tuesday afternoon differ in most people's listening. But a table of 28
    /// cells over one person's month is mostly empty, and an empty cell reads as "they never do that" when
    /// it really means "not yet".</summary>
    public IReadOnlyList<GenreShare> GenresOn(DayOfWeek day) =>
        _genresByDay.TryGetValue(day, out var shares) ? shares : [];

    /// <summary>Builds the profile from what the read gathered.
    ///
    /// <para>This is kept separate from the read, and it takes plain rows. So all of this can be tested
    /// without a database. It uses the same layout as <see cref="MoodShelf.Build"/> and
    /// <see cref="MoodBands.GenreByFolder"/>, for the same reason. The arithmetic is where the mistakes
    /// are, and it should be reachable without a mirror on the disk.</para>
    /// </summary>
    /// <param name="window">The window the rows were read over. It is carried onto the profile. Nothing
    /// here filters by it again. The read already did, and filtering twice gives two chances to
    /// disagree.</param>
    /// <param name="records">One row per album folder per hour per weekday per counted/not.</param>
    /// <param name="credits">One row per track credit per counted/not.</param>
    /// <param name="genreByFolder">What each record is filed under. This is
    /// <see cref="MoodBands.GenreByFolder"/>, the same table that the mood bands are cut against. So this
    /// and the generated playlists always agree about what kind of music a record is. A folder missing from
    /// the table has no genre the app recognises. It is left out of the genre roll-ups, but it is still
    /// counted in <see cref="At"/>, <see cref="On"/> and <see cref="Whole"/>.</param>
    /// <param name="artists">How the collection spells its artists, so variants fold together. Pass
    /// <see cref="ArtistCanonicalizer.Empty"/> to fold nothing. That is what an empty library canonicalizes
    /// against.</param>
    public static ListeningProfile Build(
        ListeningWindow window,
        IReadOnlyList<ListenedRecord> records,
        IReadOnlyList<ListenedCredit> credits,
        IReadOnlyDictionary<string, string> genreByFolder,
        ArtistCanonicalizer artists)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(credits);
        ArgumentNullException.ThrowIfNull(genreByFolder);
        ArgumentNullException.ThrowIfNull(artists);

        var whole = ListeningTally.None;
        var byPart = new Dictionary<DayPart, ListeningTally>();
        var byDay = new Dictionary<DayOfWeek, ListeningTally>();
        var genreWhole = new Dictionary<(bool Bucket, string Genre), ListeningTally>();
        var genrePart = new Dictionary<(DayPart Bucket, string Genre), ListeningTally>();
        var genreDay = new Dictionary<(DayOfWeek Bucket, string Genre), ListeningTally>();
        var albums = new Dictionary<string, PlayedAlbum>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in records)
        {
            var tally = Tally(row.Counted, row.Listens, row.Seconds);
            var part = DayParts.Of(row.LocalHour);
            var day = Weekday(row.LocalDayOfWeek);

            whole += tally;
            byPart[part] = byPart.GetValueOrDefault(part) + tally;
            byDay[day] = byDay.GetValueOrDefault(day) + tally;

            // A record the app cannot file is left out of the genre roll-ups, but it stays in the totals
            // above. Both parts are deliberate. It is not a kind of music, so it must not become a group of
            // its own. And it was listened to, so the part of the day it was listened to in counts it,
            // whether or not anything can say what it was.
            if (genreByFolder.TryGetValue(row.Folder, out var genre))
            {
                // One bucket, so the shares are of every genred play in the window. The same Shares helper
                // is used for the other two, rather than dividing here. Otherwise the whole-window shares
                // and the per-part shares could mean different things.
                genreWhole[(true, genre)] = genreWhole.GetValueOrDefault((true, genre)) + tally;
                genrePart[(part, genre)] = genrePart.GetValueOrDefault((part, genre)) + tally;
                genreDay[(day, genre)] = genreDay.GetValueOrDefault((day, genre)) + tally;
            }

            // The title and the artist are the same for every row of one folder. So the first row that
            // carries them wins, and the rest only add up. The read's own MIN() has already chosen which
            // spelling that is. This keeps the first non-null value, rather than letting a later row blank
            // it.
            var known = albums.GetValueOrDefault(row.Folder);
            albums[row.Folder] = new PlayedAlbum(
                row.Folder,
                known.Title ?? row.Album,
                known.Artist ?? row.Artist,
                known.Tally + tally,
                Math.Max(known.LastAtUtcTicks, row.LastAtUtcTicks));
        }

        // Folded by the collection's own spelling, then keyed by it. So "Sigur Ros" and its accented
        // spelling are one act. Resolve returns a name unchanged where the library has no opinion. That is
        // the right answer, not a failure. It means this is the only way anybody here spells them.
        var byArtist = new Dictionary<string, PlayedArtist>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in credits)
        {
            // A credit that is only spaces is not an act with no name. NULLIF in the read catches the empty
            // string, but whitespace gets past it. Resolve returns a blank name unchanged rather than
            // resolving it. This is deliberate: naming an unnamed track is the job of identification, not
            // of spelling. So blank names are refused here instead.
            if (artists.Resolve(row.Artist) is not { } resolved || string.IsNullOrWhiteSpace(resolved))
                continue;

            var name = resolved.Trim();
            var known = byArtist.GetValueOrDefault(name);
            byArtist[name] = new PlayedArtist(
                name,
                known.Tally + Tally(row.Counted, row.Listens, row.Seconds),
                Math.Max(known.LastAtUtcTicks, row.LastAtUtcTicks));
        }

        return new ListeningProfile(
            window,
            whole,
            byPart,
            byDay,
            Shares(genreWhole).GetValueOrDefault(true) ?? [],
            Shares(genrePart),
            Shares(genreDay),
            [.. byArtist.Values.OrderByDescending(a => a.Tally.Plays)
                               .ThenByDescending(a => a.LastAtUtcTicks)
                               .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)],
            [.. albums.Values.OrderByDescending(a => a.Tally.Plays)
                             .ThenByDescending(a => a.LastAtUtcTicks)
                             .ThenBy(a => a.Folder, StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>Turns a genre tally per bucket into a ranked list per bucket, with each genre's share of
    /// its own bucket's plays.
    ///
    /// <para>The shares are of plays, not listens. A share is read as a statement of taste, and a skip is
    /// the opposite of one. A bucket in which nothing counted has no shares to give, so every line in it
    /// reads nought. That is accurate: the listening happened, and none of it counted as a play.</para>
    /// </summary>
    private static Dictionary<TBucket, List<GenreShare>> Shares<TBucket>(
        Dictionary<(TBucket Bucket, string Genre), ListeningTally> tallies)
        where TBucket : notnull
    {
        var plays = new Dictionary<TBucket, int>();
        foreach (var (key, tally) in tallies)
            plays[key.Bucket] = plays.GetValueOrDefault(key.Bucket) + tally.Plays;

        var byBucket = new Dictionary<TBucket, List<GenreShare>>();
        foreach (var (key, tally) in tallies)
        {
            var total = plays.GetValueOrDefault(key.Bucket);
            if (!byBucket.TryGetValue(key.Bucket, out var list))
                byBucket[key.Bucket] = list = [];
            list.Add(new GenreShare(key.Genre, tally, total == 0 ? 0 : (double)tally.Plays / total));
        }

        // Most played first. The name breaks ties, so two reads of the same rows cannot come back in
        // different orders. This is the rule that every generated list's ORDER BY already follows.
        foreach (var list in byBucket.Values)
            list.Sort((a, b) => b.Tally.Plays != a.Tally.Plays
                ? b.Tally.Plays.CompareTo(a.Tally.Plays)
                : string.CompareOrdinal(a.Genre, b.Genre));

        return byBucket;
    }

    private static ListeningTally Tally(bool counted, int listens, double seconds) =>
        counted ? new ListeningTally(listens, 0, seconds) : new ListeningTally(0, listens, seconds);

    /// <summary>The stored weekday, as the framework's own. <c>ListenEntry.LocalDayOfWeek</c> is cast
    /// straight from <see cref="DayOfWeek"/>, so it is already 0 for Sunday. This reads the value back. It
    /// refuses a number that was never a weekday, rather than returning a weekday that nobody
    /// stored.</summary>
    private static DayOfWeek Weekday(int stored)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(stored);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(stored, 6);
        return (DayOfWeek)stored;
    }
}
