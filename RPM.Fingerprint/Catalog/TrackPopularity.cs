using System.Text.Json;

namespace RPM.Fingerprint.Catalog;

/// <summary>One track of a release as MusicBrainz lists it: where it sits, what it is called, and the
/// recording it is a copy of. That recording id is the one the listening figures are filed under.</summary>
public sealed record ReleaseTrack(int Position, string Title, string RecordingMbid);

/// <summary>
/// How much of the world has played one recording, as ListenBrainz counts it.
/// </summary>
/// <param name="Listens">Times played, across everybody who submits their listening there.</param>
/// <param name="Listeners">How many people that was. Kept beside the plays because the pair says
/// something neither says alone: a song with fifty thousand plays from two hundred people is somebody's
/// favourite, and one with fifty thousand from twenty thousand people is a hit.</param>
public sealed record RecordingPlays(string RecordingMbid, long Listens, int Listeners);

/// <summary>Where a track sits on a record: its disc, counted from one, and its position on that disc.</summary>
public readonly record struct TrackPlace(int Disc, int Position);

/// <summary>
/// What asking about a record's best-known song came back with.
///
/// <para>Three answers rather than a title or null, because "nothing stood out" and "nobody could be
/// asked" are opposite facts, and a null runs them together. The first is worth keeping. It is the
/// answer, and keeping it stops a record being asked about on every visit. The second must NOT be
/// kept. MusicBrainz answers 503 to about half the requests it is sent in a busy minute, and a failure
/// written down as "this record has no favourite" would hide the star for good.</para>
/// </summary>
public abstract record PopularTrackAnswer
{
    private PopularTrackAnswer() { }

    /// <summary>The song, by the name the catalogue gives it.</summary>
    public sealed record Found(string Title) : PopularTrackAnswer;

    /// <summary>Asked, and there is no one song to mark: nobody has played this record, no song leads
    /// the others clearly, or no catalogue has heard of it. Worth writing down.</summary>
    public sealed record NoFavourite : PopularTrackAnswer;

    /// <summary>The question could not be put: a service down, a rate limit outlasted, no network.
    /// Not worth writing down. The next visit asks again.</summary>
    public sealed record Unavailable : PopularTrackAnswer;

    public static readonly PopularTrackAnswer Nothing = new NoFavourite();
    public static readonly PopularTrackAnswer NotAsked = new Unavailable();

    /// <summary>A title if there is one, and <see cref="Nothing"/> if there is not. This makes this
    /// the one place that decides whether a song is worth a star.</summary>
    public static PopularTrackAnswer Of(string? title) =>
        title is { Length: > 0 } named ? new Found(named) : Nothing;
}

/// <summary>
/// Which song on a record the world plays most.
///
/// <para>Two services answer it between them, and neither on its own. MusicBrainz knows what is on the
/// record and gives every track a recording id. ListenBrainz counts plays against those ids. So the
/// route is: find the release, read its tracks, ask for their figures, and take the biggest.</para>
///
/// <para><b>What this measures.</b> Listens submitted to ListenBrainz, an open scrobbling service with a
/// modest, self-selected population. It is not the charts, and it is not streaming numbers. On a
/// well-known record the hit still stands clear of the rest by a wide margin, which is all this is asked
/// to say. On an obscure record the counts are small and the answer may be no more than noise. That is
/// why a record nobody there has played gets no star, rather than an arbitrary one.</para>
///
/// <para>Everything here is parsing and choosing, with no fetching in it. That is why it sits beside the
/// catalogue contracts rather than in the app. <b>The asking is done by the RPM API.</b> Both services
/// are catalogues, both are rate-limited per address, and the same record's figures are the same answer
/// for every listener. The app does not ask directly: <see cref="RPM.Fingerprint.External.CatalogueReach"/>
/// refuses that. What the app does with the figures, such as which song earns a star and by how much it
/// has to lead, is still the app's own work, in the part below that takes no URL.</para>
/// </summary>
public static class TrackPopularity
{
    /// <summary>Where the plays are asked for. It is a POST, and there is no GET that does the same job.
    /// So the answer is kept in the album's own row, not in the app's web cache, whose keys are URLs.</summary>
    public const string PlaysUrl = "https://api.listenbrainz.org/1/popularity/recording";

    /// <summary>
    /// Where a release's own recording ids are turned into the ones the listening is counted under.
    ///
    /// <para>This step is not optional. It is the whole difference between a right answer and a wrong one.
    /// MusicBrainz has a separate recording for each time a song is pressed, so a famous song exists many
    /// times over. ListenBrainz counts every play against ONE of them, whichever one its mapper chose. If
    /// the ids printed on the sleeve are used, the figure describes that pressing alone.</para>
    ///
    /// <para>The effect is large. A song can show a few dozen listens while the other tracks on the same
    /// record show over a hundred thousand, because the plays are filed under a different recording. The
    /// mapping sends each id to the recording under which its plays are filed.</para>
    /// </summary>
    public const string CanonicalUrl = "https://labs.api.listenbrainz.org/recording-mbid-lookup/json";

    private const string MusicBrainzBase = "https://musicbrainz.org/ws/2";


    /// <summary>
    /// One release with its tracks, which is where the recording ids are.
    ///
    /// <para>This type does not search. Finding WHICH release an album is meant is a Lucene query checked
    /// against the artist and the title. That check exists in one place, <c>Keyring</c>, which also holds
    /// the pressings. So the release can be narrowed by track count rather than matched by name. A second
    /// copy of the check here is what that type was made to prevent.</para></summary>
    public static string ReleaseUrl(string releaseMbid) =>
        $"{MusicBrainzBase}/release/{Uri.EscapeDataString(releaseMbid)}?inc=recordings&fmt=json";


    /// <summary>The tracks of a release, in the order they are pressed.</summary>
    public static IReadOnlyList<ReleaseTrack> Tracks(string releaseJson)
    {
        if (string.IsNullOrWhiteSpace(releaseJson))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(releaseJson);
            if (!doc.RootElement.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Array)
                return [];

            var tracks = new List<ReleaseTrack>();
            foreach (var disc in media.EnumerateArray())
            {
                if (!disc.TryGetProperty("tracks", out var list) || list.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var track in list.EnumerateArray())
                {
                    if (!track.TryGetProperty("recording", out var recording))
                        continue;
                    if (Id(recording) is not { Length: > 0 } mbid)
                        continue;

                    // The TRACK's title, not the recording's. They are the same on most records and
                    // differ on exactly the ones this has to get right: a live take listed as "Lost on
                    // You (live at Harvard and Stone)" on the sleeve is the recording "Lost on You"
                    // underneath, and taking the recording's name would put two identical titles on the
                    // list with no way to tell which the figures belong to.
                    var title = Text(track, "title") ?? Text(recording, "title");
                    if (title is not { Length: > 0 })
                        continue;

                    tracks.Add(new ReleaseTrack(Count(track, "position"), title, mbid));
                }
            }

            return tracks;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The body ListenBrainz is asked with: the recordings to report on, and nothing else.</summary>
    public static string PlaysBody(IEnumerable<string> recordingMbids)
    {
        ArgumentNullException.ThrowIfNull(recordingMbids);
        var mbids = recordingMbids.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return JsonSerializer.Serialize(new PlaysRequest(mbids), CatalogJson.Default.PlaysRequest);
    }

    /// <summary>The body the mapping is asked with: one object per recording, which is the shape that
    /// service takes.</summary>
    public static string CanonicalBody(IEnumerable<string> recordingMbids)
    {
        ArgumentNullException.ThrowIfNull(recordingMbids);
        IReadOnlyList<CanonicalRequestRow> mbids = recordingMbids
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(m => new CanonicalRequestRow(m))
            .ToArray();
        return JsonSerializer.Serialize(mbids, CatalogJson.Default.IReadOnlyListCanonicalRequestRow);
    }

    /// <summary>The mapping that came back, from the id on the sleeve to the id the plays are counted
    /// under. A recording the service has never heard of is simply absent, and stays itself.</summary>
    public static IReadOnlyDictionary<string, string> Canonical(string canonicalJson)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(canonicalJson))
            return map;

        try
        {
            using var doc = JsonDocument.Parse(canonicalJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return map;

            foreach (var row in doc.RootElement.EnumerateArray())
            {
                // Keyed on what was ASKED rather than on the order it came back in: the answer is a set,
                // not a list, and a recording the service dropped would otherwise shift every mapping
                // after it onto the wrong song.
                if (Text(row, "original_recording_mbid") is not { Length: > 0 } asked)
                    continue;
                if (Text(row, "canonical_recording_mbid") is not { Length: > 0 } canonical)
                    continue;
                map[asked] = canonical;
            }
        }
        catch (JsonException)
        {
            return map;   // no mapping: the release's own ids are used
        }

        return map;
    }

    /// <summary>The same tracks, each pointed at the recording its plays are counted under. Ids the
    /// mapping did not answer for are left alone: their own figure is a poor answer, and no figure at
    /// all is not a better one.</summary>
    public static IReadOnlyList<ReleaseTrack> Canonicalised(
        IReadOnlyList<ReleaseTrack> tracks, IReadOnlyDictionary<string, string> canonical)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(canonical);

        return [.. tracks.Select(t => canonical.TryGetValue(t.RecordingMbid, out var mapped)
            ? t with { RecordingMbid = mapped }
            : t)];
    }

    /// <summary>What came back: a figure per recording. A recording nobody has played is simply absent,
    /// and reads as nothing played rather than as an error.</summary>
    /// <summary>
    /// The listens of each track, by disc and position on the disc.
    ///
    /// <para><b>By POSITION rather than by title. That is the whole reason this is a function instead of
    /// a join at the call site.</b> The catalogue spells a title its own way: a bracketed remaster note,
    /// an accent the tagger did not type, "Pt. 2" against "Part 2". A match on the words drops the tracks
    /// it is least sure about, which on a famous record are the famous ones. A position is a number both
    /// sides agree on.</para>
    ///
    /// <para><b>The disc is counted from the listing.</b> <see cref="Tracks"/> lists every disc in
    /// pressing order, each counted from one, with no disc number. A position that does not follow the one
    /// before it starts the next disc. Keying by position alone would let track 3 of a second disc overwrite
    /// track 3 of the first. A deluxe edition's demos would then stand in for its hits.</para>
    ///
    /// <para>Handed the CANONICALISED tracks, never the pressing's own. The counts are filed under the
    /// recording the world plays, and asking about the copy in hand answers near zero for a hit. See
    /// <see cref="CanonicalUrl"/>.</para>
    ///
    /// <para>A track the counts say nothing about is absent rather than nought. Nobody asked about it and
    /// nobody answered. Nought is a claim, and the caller writes claims down.</para>
    /// </summary>
    public static IReadOnlyDictionary<TrackPlace, RecordingPlays> PlaysByPlace(
        IReadOnlyList<ReleaseTrack> counted, IReadOnlyList<RecordingPlays> plays)
    {
        ArgumentNullException.ThrowIfNull(counted);
        ArgumentNullException.ThrowIfNull(plays);

        var byMbid = new Dictionary<string, RecordingPlays>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in plays)
            byMbid[row.RecordingMbid] = row;

        var byPlace = new Dictionary<TrackPlace, RecordingPlays>();
        var disc = 1;
        var last = 0;
        foreach (var track in counted)
        {
            if (track.Position <= 0)
                continue;

            if (track.Position <= last)
                disc++;
            last = track.Position;

            if (byMbid.TryGetValue(track.RecordingMbid, out var found))
                byPlace[new TrackPlace(disc, track.Position)] = found;
        }

        return byPlace;
    }

    public static IReadOnlyList<RecordingPlays> Plays(string playsJson)
    {
        if (string.IsNullOrWhiteSpace(playsJson))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(playsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            var plays = new List<RecordingPlays>();
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                if (Text(row, "recording_mbid") is not { Length: > 0 } mbid)
                    continue;
                plays.Add(new RecordingPlays(
                    mbid,
                    Number(row, "total_listen_count"),
                    (int)Number(row, "total_user_count")));
            }

            return plays;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The most played track's title, or null when there is no answer worth drawing.
    ///
    /// <para>Null when nothing has been played, and null when the record is a tie or near enough to one. A
    /// mark saying "this is the one people play" has to be earned. On a record whose songs are all within a
    /// very small margin of each other, it would be picking a winner out of noise. The bar is a clear lead
    /// over the runner-up, not a lead of one listen.</para>
    /// </summary>
    /// <param name="lead">How far ahead of the second-placed track the winner has to be, as a fraction
    /// of the runner-up's plays. The caller states it, so the rule is visible at the call site rather
    /// than buried here.</param>
    public static string? MostPlayed(
        IReadOnlyList<ReleaseTrack> tracks, IReadOnlyList<RecordingPlays> plays, double lead)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(plays);

        var counted = plays.ToDictionary(p => p.RecordingMbid, p => p.Listens, StringComparer.OrdinalIgnoreCase);

        // A record can list one recording twice. The studio take and a bonus live version share an id on
        // some releases. The plays belong to the recording, not the track, so summing them would count the
        // same listens twice. The highest track wins on its own figure, and the order between two tracks of
        // one recording is settled by which is pressed first.
        return MostPlayedOf(
            [.. tracks.Select(t => (t.Title, t.Position, counted.GetValueOrDefault(t.RecordingMbid, 0L)))],
            lead,
            minimum: 1);
    }

    /// <summary>
    /// The same choice made over plain counts: whichever source they came from, the song that leads by
    /// enough, or null.
    /// </summary>
    /// <param name="counted">Title, where it sits on the record, and how many plays. Position only ever
    /// settles a tie, so a source with no track numbers can pass the order it answered in.</param>
    /// <param name="minimum">What the winner has to have been played before it is worth marking at all.
    /// One for a source counting the whole world; more for a smaller room, where a song with two plays
    /// leads a record nobody has heard.</param>
    public static string? MostPlayedOf(
        IReadOnlyList<(string Title, int Position, long Plays)> counted, double lead, long minimum)
    {
        ArgumentNullException.ThrowIfNull(counted);

        var ranked = counted
            .OrderByDescending(x => x.Plays)
            .ThenBy(x => x.Position)
            .ToList();

        if (ranked.Count == 0 || ranked[0].Plays < Math.Max(minimum, 1))
            return null;

        if (ranked.Count > 1 && ranked[1].Plays > 0
            && ranked[0].Plays < ranked[1].Plays * (1 + lead))
            return null;

        return ranked[0].Title;
    }

    /// <summary>
    /// What a song needs before RPM's own listeners are allowed to name it the record's best known.
    ///
    /// <para>Five, and the number is doing real work: the counters start at nothing and fill from the
    /// people who have this app, so on the day a record's first listener finishes its second track that
    /// song leads the album two plays to nought. A star on that is not a report about the record, it is
    /// a report about one evening.</para>
    /// </summary>
    public const long RpmListenersMinimum = 5;

    /// <summary>
    /// The lead the album page asks for: a quarter again as many plays as the next song.
    ///
    /// <para>Set against real records, not picked by guess. At half again, two albums in eight off a real
    /// shelf were refused a star for closeness, including one whose top song led by 1.3x and plainly is the
    /// single. A quarter still refuses the genuine near-ties (Lady Gaga's "Chromatica", measured at 1.2x, has
    /// no one song people go to), while marking the records where a song does lead. It is a threshold on a
    /// noisy measure either way, which is why it is stated here once and passed in, rather than buried at the
    /// comparison.</para>
    /// </summary>
    public const double AlbumPageLead = 0.25;

    private static string? Id(JsonElement e) =>
        e.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Count(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static long Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    /// <summary>The whole credit, joined as it is written: "Calvin Harris feat. Ellie Goulding" is one
    /// name for the purpose of asking whether this release is the record we asked for.</summary>
    private static string? CreditedArtist(JsonElement release)
    {
        if (!release.TryGetProperty("artist-credit", out var credit) || credit.ValueKind != JsonValueKind.Array)
            return null;

        var names = credit.EnumerateArray()
            .Select(c => c.TryGetProperty("artist", out var artist) ? Text(artist, "name") : null)
            .Where(n => n is { Length: > 0 });

        return string.Join(" ", names);
    }

    /// <summary>A quotation mark inside a Lucene phrase ends the phrase; a backslash before it does
    /// not. Nothing else in a title has that power, so nothing else is touched.</summary>
    private static string Escape(string text) => text.Replace("\"", "\\\"", StringComparison.Ordinal);
}
