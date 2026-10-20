using System.Net.Http;
using System.Text.Json;
using RPM.Fingerprint.Catalog;

namespace RPM.Fingerprint.External;

/// <summary>
/// Small client for the MusicBrainz public API. It is free and needs no API key, but anonymous use
/// is limited to 1 request per second per IP address. MusicBrainz requires a real User-Agent.
///
/// Used to turn a recording UUID returned by AcoustID into full metadata: title, artists,
/// release group, release year, country, label and so on.
/// </summary>
public sealed class MusicBrainzClient
{
    private const string Endpoint = "https://musicbrainz.org/ws/2/recording/";

    /// <summary>The spacing between requests that MusicBrainz asks for: one per second, plus 100 ms
    /// in case the two clocks differ.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(1100);

    /// <summary>How long one attempt may take. It must stay well below the Lambda's own 60 s limit,
    /// because a connection that never answers holds the spacing, and every lookup waiting behind
    /// it. A slow release-group lookup can take about 16 s.</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(30);

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static IRequestSpacing? _spacing;
    private static Action<MusicBrainzCall>? _observe;

    /// <summary>
    /// Sets how this process spaces its requests and where it reports each one. Called once, at
    /// startup, by the only process that is allowed to call MusicBrainz (see
    /// <see cref="CatalogueReach"/>).
    ///
    /// <para>There is no default: if this is not called, <see cref="FetchAsync"/> throws. A default
    /// spacing would be per process, which is wrong for the API, and the API is the only caller.</para>
    /// </summary>
    public static void Configure(IRequestSpacing spacing, Action<MusicBrainzCall> observe)
    {
        ArgumentNullException.ThrowIfNull(spacing);
        ArgumentNullException.ThrowIfNull(observe);
        _spacing = spacing;
        _observe = observe;
    }

    private readonly string _userAgent;

    public MusicBrainzClient(string userAgent = "RPM/0.1 ( https://rpmrpmrpm.com )")
    {
        _userAgent = userAgent;
    }

    private Task<string?> GetAsync(string url, CancellationToken ct) => FetchAsync(url, _userAgent, ct);

    /// <summary>
    /// Sends one GET when this process's turn comes. A 503 (the rate limiter's answer) is retried with
    /// a growing backoff. Returns the body, or null on 404, which means MusicBrainz has no such item.
    ///
    /// <para>Every other failure throws <see cref="MusicBrainzUnreachableException"/>: another
    /// status, a timeout, a failed connection. It is one type, so a caller cannot catch the status
    /// and miss the timeout. It derives from <see cref="InvalidOperationException"/> because that is
    /// what every existing caller catches as "could not ask".</para>
    ///
    /// <para>Static and public, because MusicBrainz enforces its limit per address, not per object:
    /// every request this process makes to the service must take a turn from the same
    /// spacing.</para>
    /// </summary>
    public static async Task<string?> FetchAsync(string url, string userAgent, CancellationToken ct = default)
    {
        // Checked here as well as in the app's HTTP pipeline, because this fetcher has its own client
        // and a caller that reaches it does not pass through Wire. Both checks enforce the same rule:
        // see CatalogueReach.
        CatalogueReach.Check(new Uri(url));

        var spacing = _spacing ?? throw new InvalidOperationException(
            "MusicBrainz spacing was never configured. Call MusicBrainzClient.Configure at startup.");

        const int maxAttempts = 4;
        var path = new Uri(url).AbsolutePath;

        for (var attempt = 0; ; attempt++)
        {
            var queued = System.Diagnostics.Stopwatch.GetTimestamp();
            await spacing.TakeTurnAsync(ct);
            var sent = System.Diagnostics.Stopwatch.GetTimestamp();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(AttemptTimeout);

            System.Net.HttpStatusCode status;
            string body;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd(userAgent);

                using var response = await Http.SendAsync(request, timeout.Token);
                status = response.StatusCode;
                body = await response.Content.ReadAsStringAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
            {
                Report(path, 0, queued, sent);
                throw new MusicBrainzUnreachableException(
                    ex is OperationCanceledException
                        ? $"MusicBrainz did not answer {path} within {AttemptTimeout.TotalSeconds:0}s"
                        : $"MusicBrainz could not be reached for {path}: {ex.Message}",
                    ex);
            }

            Report(path, (int)status, queued, sent);

            if (status == System.Net.HttpStatusCode.NotFound)
                return null;

            if (status == System.Net.HttpStatusCode.ServiceUnavailable && attempt < maxAttempts - 1)
            {
                // Wait before taking the next turn, so a throttled caller does not hold a place in the
                // queue while it waits.
                await Task.Delay(TimeSpan.FromSeconds(1 << attempt), ct);
                continue;
            }

            if (status != System.Net.HttpStatusCode.OK)
                throw new MusicBrainzUnreachableException(
                    $"MusicBrainz HTTP {(int)status}: {body[..Math.Min(200, body.Length)]}");

            return body;
        }
    }

    private static void Report(string path, int status, long queued, long sent)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        _observe?.Invoke(new MusicBrainzCall(
            path, status,
            System.Diagnostics.Stopwatch.GetElapsedTime(queued, sent),
            System.Diagnostics.Stopwatch.GetElapsedTime(sent, now)));
    }

    public sealed record RecordingDetails(
        string Id,
        string Title,
        string[] Artists,
        string? FirstReleaseDate,
        string? PrimaryReleaseTitle,
        string? PrimaryReleaseGroupId,
        string? Disambiguation);

    public async Task<RecordingDetails?> LookupRecordingAsync(string recordingId, CancellationToken ct = default)
    {
        // inc=artists+releases+release-groups gives enough to build a useful display.
        var url = $"{Endpoint}{recordingId}?inc=artists+releases+release-groups&fmt=json";
        var body = await GetAsync(url, ct);
        if (body is null)
            return null;

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        string id = root.GetProperty("id").GetString() ?? recordingId;
        string title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
        string? disambiguation = root.TryGetProperty("disambiguation", out var d) ? d.GetString() : null;
        string? firstReleaseDate = root.TryGetProperty("first-release-date", out var frd) ? frd.GetString() : null;

        var artists = new List<string>();
        if (root.TryGetProperty("artist-credit", out var ac))
        {
            foreach (var a in ac.EnumerateArray())
            {
                if (a.TryGetProperty("name", out var n))
                    artists.Add(n.GetString() ?? "");
                else if (a.TryGetProperty("artist", out var ar) && ar.TryGetProperty("name", out var arn))
                    artists.Add(arn.GetString() ?? "");
            }
        }

        // Use the first release as the representative one.
        string? primaryReleaseTitle = null;
        string? primaryReleaseGroupId = null;
        if (root.TryGetProperty("releases", out var rels) && rels.GetArrayLength() > 0)
        {
            var first = rels[0];
            if (first.TryGetProperty("title", out var rt))
                primaryReleaseTitle = rt.GetString();
            if (first.TryGetProperty("release-group", out var rg) && rg.TryGetProperty("id", out var rgid))
                primaryReleaseGroupId = rgid.GetString();
        }

        return new RecordingDetails(id, title, artists.ToArray(), firstReleaseDate, primaryReleaseTitle, primaryReleaseGroupId, disambiguation);
    }

    /// <summary>One track slot of a release, flattened across media in medium order.
    /// <see cref="Position"/> is the overall 1-based slot, not the per-medium number.
    ///
    /// <para>Every field after the length comes from extra <c>inc=</c> values on the same request, and
    /// all of them have defaults: a caller that only needs titles and durations is not affected, and
    /// a release that MusicBrainz knows nothing more about gives nulls.</para></summary>
    /// <param name="Artist">The track's own artist. On a compilation this differs from the release
    /// artist on every row. A rip often gets this wrong by writing one name on all rows.</param>
    /// <param name="Work">The composition, when the recording is a performance of one. See
    /// <see cref="MusicBrainzRelations.Work"/>: for a symphony this is the parent work and
    /// <paramref name="MovementName"/> is the movement, which is the opposite of how MusicBrainz
    /// nests them.</param>
    public sealed record ReleaseTrack(
        uint Position,
        string Title,
        string? Artist,
        double? LengthSeconds,
        string? ArtistSort = null,
        string? RecordingId = null,
        string? ArtistId = null,
        string? Isrc = null,
        string? Work = null,
        string? MovementName = null,
        uint MovementNumber = 0,
        string? Composers = null,
        string? ComposerSort = null,
        string? Lyricist = null,
        string? Arranger = null,
        string? Conductor = null,
        string? Ensemble = null,
        string? Soloists = null);

    /// <summary>One medium (disc) of a release with its own ordered tracks. Kept separate
    /// because an album-in-one-file rip is usually ONE disc of a multi-disc set.</summary>
    /// <param name="Format">"CD", "DVD-Video" and so on. This tells a DVD apart from the CD in the same
    /// set.</param>
    /// <param name="Title">The medium's own title in the set ("The Hits", "B-Sides"), when named. Null otherwise.</param>
    public sealed record ReleaseMediumDetails(uint Position, IReadOnlyList<ReleaseTrack> Tracks, string? Format = null, string? Title = null);

    /// <param name="OriginalYear">When the record first came out, from the release group's
    /// first-release-date. It differs from <paramref name="Year"/> on every reissue, and that
    /// difference is why both are kept.</param>
    public sealed record ReleaseDetails(
        string Id,
        string Title,
        string? Artist,
        uint Year,
        IReadOnlyList<ReleaseTrack> Tracks,
        IReadOnlyList<ReleaseMediumDetails> Media,
        string? ArtistSort = null,
        string? ArtistId = null,
        string? ReleaseGroupId = null,
        uint OriginalYear = 0,
        string? Barcode = null,
        string? Label = null,
        string? CatalogNumber = null,
        string? Genre = null,
        string? Country = null,
        string? PrimaryType = null,
        IReadOnlyList<string>? SecondaryTypes = null,
        IReadOnlyList<string>? Genres = null);

    /// <summary>
    /// What a release lookup asks for.
    ///
    /// <para>All of these go on the same request, in the same rate-limited second: MusicBrainz limits
    /// calls, not includes, so asking for the credits costs response size, not time. The classical
    /// fields need three of them together: <c>work-rels</c> reaches the composition,
    /// <c>work-level-rels</c> reaches its parent work and its composer, and
    /// <c>recording-level-rels</c> reaches the conductor and the orchestra. If any one is removed, the
    /// chain breaks without an error and nothing is filled.</para>
    /// </summary>
    private const string ReleaseIncludes =
        "recordings+media+artist-credits+isrcs+genres+labels+release-groups" +
        "+artist-rels+recording-level-rels+work-rels+work-level-rels";

    /// <summary>Fetches a release's full ordered track list with durations (the expected tracks used
    /// to align boundaries inside an album-in-one-file rip), plus everything else the catalogue
    /// knows about the pressing and the people on it.</summary>
    public async Task<ReleaseDetails?> LookupReleaseAsync(string releaseId, CancellationToken ct = default)
    {
        var url = $"https://musicbrainz.org/ws/2/release/{releaseId}?inc={ReleaseIncludes}&fmt=json";
        var body = await GetAsync(url, ct);
        if (body is null)
            return null;

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        string id = root.GetProperty("id").GetString() ?? releaseId;
        string title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
        var credit = MusicBrainzRelations.ArtistCredit(root);
        string? artist = credit.Name ?? JoinArtistCredit(root);
        uint year = MusicBrainzRelations.Year(root, "date");

        var releaseGroup = root.TryGetProperty("release-group", out var rg) ? rg : default;
        var (label, catalogNumber) = MusicBrainzRelations.LabelInfo(root);

        var tracks = new List<ReleaseTrack>();
        var mediaList = new List<ReleaseMediumDetails>();
        if (root.TryGetProperty("media", out var media))
        {
            foreach (var medium in media.EnumerateArray()
                         .OrderBy(m => m.TryGetProperty("position", out var mp) ? mp.GetInt32() : 0))
            {
                uint mediumPos = medium.TryGetProperty("position", out var mpos) && mpos.TryGetUInt32(out var mpv)
                    ? mpv : (uint)(mediaList.Count + 1);
                var mediumTracks = new List<ReleaseTrack>();
                if (medium.TryGetProperty("tracks", out var mTracks))
                {
                    foreach (var trk in mTracks.EnumerateArray()
                                 .OrderBy(x => x.TryGetProperty("position", out var tp) ? tp.GetInt32() : 0))
                    {
                        string trkTitle = trk.TryGetProperty("title", out var tt) ? tt.GetString() ?? "" : "";
                        var recording = trk.TryGetProperty("recording", out var rec) ? rec : default;

                        double? lengthSec = null;
                        if (trk.TryGetProperty("length", out var len) && len.ValueKind == JsonValueKind.Number)
                            lengthSec = len.GetDouble() / 1000.0;
                        else if (recording.ValueKind == JsonValueKind.Object &&
                                 recording.TryGetProperty("length", out var rlen) && rlen.ValueKind == JsonValueKind.Number)
                            lengthSec = rlen.GetDouble() / 1000.0;

                        // The track's own credit first, the recording's second, the release's last. On a
                        // compilation the first two differ from the third on every row, which is why the
                        // track has an artist of its own.
                        var trkCredit = MusicBrainzRelations.ArtistCredit(trk);
                        if (trkCredit.IsEmpty)
                            trkCredit = MusicBrainzRelations.ArtistCredit(recording);

                        var performers = MusicBrainzRelations.Recording(recording);
                        var work = MusicBrainzRelations.Work(recording);

                        mediumTracks.Add(new ReleaseTrack(
                            (uint)(mediumTracks.Count + 1),
                            trkTitle,
                            trkCredit.Name ?? artist,
                            lengthSec,
                            ArtistSort: trkCredit.SortName ?? credit.SortName,
                            RecordingId: recording.ValueKind == JsonValueKind.Object &&
                                         recording.TryGetProperty("id", out var rid) ? rid.GetString() : null,
                            ArtistId: trkCredit.Id ?? credit.Id,
                            Isrc: MusicBrainzRelations.FirstIsrc(recording),
                            Work: work.Work,
                            MovementName: work.MovementName,
                            MovementNumber: work.MovementNumber,
                            Composers: work.Composers,
                            ComposerSort: work.ComposerSort,
                            Lyricist: work.Lyricist,
                            Arranger: work.Arranger,
                            Conductor: performers.Conductor,
                            Ensemble: performers.Ensemble,
                            Soloists: performers.Soloists));
                    }
                }
                string? mediumFormat = medium.TryGetProperty("format", out var mf) ? mf.GetString() : null;
                string? mediumTitle = medium.TryGetProperty("title", out var mtt) && mtt.GetString() is { Length: > 0 } mts
                    ? mts : null;
                mediaList.Add(new ReleaseMediumDetails(mediumPos, mediumTracks, mediumFormat, mediumTitle));
                // The flat list keeps the overall order for single-disc callers.
                foreach (var mt in mediumTracks)
                    tracks.Add(mt with { Position = (uint)(tracks.Count + 1) });
            }
        }

        return new ReleaseDetails(
            id, title, artist, year, tracks, mediaList,
            ArtistSort: credit.SortName,
            ArtistId: credit.Id,
            ReleaseGroupId: releaseGroup.ValueKind == JsonValueKind.Object &&
                            releaseGroup.TryGetProperty("id", out var rgid) ? rgid.GetString() : null,
            // The release group's first release is when the record came out. This pressing's date is
            // when THIS pressing came out. They are equal for an original and differ for a reissue.
            OriginalYear: MusicBrainzRelations.Year(releaseGroup, "first-release-date"),
            Barcode: root.TryGetProperty("barcode", out var bc) ? bc.GetString() : null,
            Label: label,
            CatalogNumber: catalogNumber,
            // The release's own voted genres, falling back to the release group's: a single pressing
            // often has no genre tags when the record as a whole does.
            Genre: MusicBrainzRelations.TopGenre(root, releaseGroup),
            Country: root.TryGetProperty("country", out var co) ? co.GetString() : null,
            PrimaryType: PrimaryTypeOf(releaseGroup),
            SecondaryTypes: SecondaryTypesOf(releaseGroup),
            // Every voted genre, not only the top one. The top vote is often the broad word ("electronic")
            // and the second the one that says which corner ("trip hop"). The desktop reads the whole list
            // against its own shelves and styles (Genres.PickVoted).
            Genres: MusicBrainzRelations.VotedGenres(root, releaseGroup));
    }

    /// <summary>The release group's primary type ("Album", "EP", "Single"), or null where it has none. What
    /// kind of release this is belongs to the group, not to one pressing of it.</summary>
    internal static string? PrimaryTypeOf(JsonElement releaseGroup)
        => releaseGroup.ValueKind == JsonValueKind.Object
           && releaseGroup.TryGetProperty("primary-type", out var type) && type.ValueKind == JsonValueKind.String
            ? type.GetString() is { Length: > 0 } name ? name : null
            : null;

    /// <summary>The release group's secondary types ("Live", "Compilation", "Mixtape/Street"), empty where it
    /// has none.</summary>
    internal static IReadOnlyList<string> SecondaryTypesOf(JsonElement releaseGroup)
        => releaseGroup.ValueKind == JsonValueKind.Object
           && releaseGroup.TryGetProperty("secondary-types", out var types) && types.ValueKind == JsonValueKind.Array
            ? [.. types.EnumerateArray()
                .Where(t => t.ValueKind == JsonValueKind.String)
                .Select(t => t.GetString())
                .OfType<string>()
                .Where(t => t.Length > 0)]
            : [];

    /// <summary>One DVD found by a search, with enough detail to choose between results without a
    /// second call.</summary>
    /// <param name="MediumPosition">Which medium of the release is the DVD. A CD+DVD set has both.</param>
    /// <param name="TrackCount">Compared with the disc's chapter count, which is the only structural
    /// information a DVD gives.</param>
    public sealed record DvdCandidate(
        string ReleaseId,
        string Title,
        string? Artist,
        uint Year,
        string? Format,
        uint MediumPosition,
        int TrackCount);

    /// <summary>
    /// One medium of one release, as the search endpoint describes it. This is enough to tell
    /// pressings of the same album apart, which matters most on vinyl: a 1977 UK pressing and a 2013
    /// reissue have the same songs at different lengths.
    ///
    /// <para>No track lengths. The search does not return them; see
    /// <see cref="SearchReleasesAsync"/>.</para>
    /// </summary>
    /// <param name="Year">0 when the release has no date.</param>
    /// <param name="Date">The date as given: a year only, or a full date where known.</param>
    /// <param name="Country">Where this pressing came out: "GB", "US", "XE" (Europe), "XW" (worldwide).</param>
    /// <param name="Format">"12&quot; Vinyl", "CD", "Cassette" and so on. Null when the release does not
    /// say.</param>
    /// <param name="CatalogNumber">Printed on the sleeve and stamped in the run-out, so it is the
    /// most reliable way to identify the exact pressing.</param>
    /// <param name="Disambiguation">MusicBrainz's own note: "deluxe edition", "remastered".</param>
    /// <param name="Score">MusicBrainz's relevance for the query, 0..100.</param>
    public sealed record ReleaseCandidate(
        string ReleaseId,
        string Title,
        string? Artist,
        uint Year,
        string? Date,
        string? Country,
        string? Format,
        uint MediumPosition,
        int TrackCount,
        string? Label,
        string? CatalogNumber,
        string? Disambiguation,
        double Score,
        /// <summary>The release GROUP this pressing belongs to: the album in general, not this
        /// pressing of it. Null when the search result does not include it. Artwork services outside
        /// MusicBrainz use it as their key: fanart.tv stores covers under the group, not the release,
        /// because a hand-made HD cover shows the album, not one pressing.</summary>
        string? ReleaseGroupId = null)
    {
        /// <summary>True for any record format. These are the pressings whose track lengths can
        /// match a vinyl recording.</summary>
        public bool IsVinyl => Format?.Contains("Vinyl", StringComparison.OrdinalIgnoreCase) == true
                            || Format?.Contains("LP", StringComparison.OrdinalIgnoreCase) == true;

        /// <summary>Front cover of THIS pressing, from the Cover Art Archive. The key is the release, so
        /// it is the cover of this edition, not a general one for the album name. Not every release
        /// has art: the request returns 404, and that is the only way to find out.</summary>
        public string CoverArtUrl => $"https://coverartarchive.org/release/{ReleaseId}/front-250";
    }

    /// <summary>
    /// Searches MusicBrainz for music DVDs that match free text: what the user types, or the disc's
    /// volume label as a first suggestion.
    ///
    /// <para>
    /// A DVD has no DiscID. The MusicBrainz DiscID is a hash of a CD's table of contents, and a DVD
    /// has no table of contents, so a DVD cannot be looked up by ID. It can only be searched for.
    /// That is why this is a manual search and not the automatic lookup that CDs get.
    /// </para>
    ///
    /// <para>
    /// The format filter must name both spellings. MusicBrainz stores music DVDs under two different
    /// formats, <c>DVD</c> and <c>DVD-Video</c>. Asking only for <c>format:DVD</c> returns nothing,
    /// with no error, for a disc stored as the other. Most concert DVDs are stored as the other.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<DvdCandidate>> SearchDvdReleasesAsync(
        string text, int limit = 25, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var query = $"{Escape(text)} AND (format:DVD OR format:\"DVD-Video\")";
        var url = $"https://musicbrainz.org/ws/2/release/?query={Uri.EscapeDataString(query)}"
                + $"&limit={limit}&fmt=json";

        var body = await GetAsync(url, ct);
        if (body is null)
            return [];

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("releases", out var releases))
            return [];

        var found = new List<DvdCandidate>();

        foreach (var release in releases.EnumerateArray())
        {
            string id = release.TryGetProperty("id", out var rid) ? rid.GetString() ?? "" : "";
            string title = release.TryGetProperty("title", out var rt) ? rt.GetString() ?? "" : "";
            string? artist = JoinArtistCredit(release);

            uint year = 0;
            if (release.TryGetProperty("date", out var date) && date.GetString() is { Length: >= 4 } ds
                && uint.TryParse(ds[..4], out var y))
                year = y;

            if (!release.TryGetProperty("media", out var media))
                continue;

            // A release can contain a CD and a DVD. Only the DVD mediums are candidates, and each is
            // offered separately, because the user has one disc, not the whole set.
            foreach (var medium in media.EnumerateArray())
            {
                var format = medium.TryGetProperty("format", out var mf) ? mf.GetString() : null;
                if (format is null || !format.Contains("DVD", StringComparison.OrdinalIgnoreCase))
                    continue;

                int trackCount = medium.TryGetProperty("track-count", out var tc) && tc.TryGetInt32(out var tcv)
                    ? tcv : 0;
                uint position = medium.TryGetProperty("position", out var mp) && mp.TryGetUInt32(out var mpv)
                    ? mpv : 1;

                found.Add(new DvdCandidate(id, title, artist, year, format, position, trackCount));
            }
        }

        return found;
    }

    /// <summary>
    /// Every record format, as a filter for <see cref="SearchReleasesAsync"/>. Without it, a search
    /// for a famous album returns almost only CDs (thirteen to one for "Rumours"), and the vinyl
    /// pressing does not appear.
    ///
    /// <para>Each size is listed separately because the format field is <b>not tokenised</b>:
    /// <c>format:Vinyl</c> matches nothing, while <c>format:"12\" Vinyl"</c> matches all 12-inch
    /// records. Both were checked against the live index; the quotes make the difference.
    /// Shellac is included because a 78 is a record too.</para>
    /// </summary>
    public const string VinylFormats =
        "(format:\"12\\\" Vinyl\" OR format:\"7\\\" Vinyl\" OR format:\"10\\\" Vinyl\" " +
        "OR format:\"Vinyl\" OR format:\"Shellac\")";

    /// <summary>
    /// The Lucene fragment that limits a search to one medium, or null for
    /// <see cref="ReleaseMedia.Any"/>. Null means no fragment, not a fragment that matches
    /// everything.
    ///
    /// <para>Every variant of a medium is listed for the same reason as the vinyl sizes: the
    /// format field is not tokenised, so <c>format:CD</c> misses "Enhanced CD" and <c>format:DVD</c>
    /// misses "DVD-Video". A name that MusicBrainz does not use costs nothing (an OR term that matches
    /// no release), but a missing name hides pressings from the user.</para>
    /// </summary>
    public static string? FormatFilter(ReleaseMedia media) => media switch
    {
        ReleaseMedia.Vinyl => VinylFormats,
        ReleaseMedia.Cd =>
            "(format:\"CD\" OR format:\"CD-R\" OR format:\"Enhanced CD\" OR format:\"Copy Control CD\" " +
            "OR format:\"HDCD\" OR format:\"Data CD\")",
        ReleaseMedia.Cassette => "(format:\"Cassette\")",
        ReleaseMedia.Digital => "(format:\"Digital Media\")",
        ReleaseMedia.Sacd =>
            "(format:\"SACD\" OR format:\"Hybrid SACD\" OR format:\"Hybrid SACD (CD layer)\" " +
            "OR format:\"Hybrid SACD (SACD layer)\")",
        ReleaseMedia.Dvd => "(format:\"DVD\" OR format:\"DVD-Audio\" OR format:\"DVD-Video\")",
        ReleaseMedia.BluRay => "(format:\"Blu-ray\" OR format:\"Blu-ray-R\")",
        ReleaseMedia.ReelToReel => "(format:\"Reel-to-reel\")",
        ReleaseMedia.EightTrack => "(format:\"8-Track Cartridge\")",
        _ => null,
    };

    /// <summary>
    /// The Lucene query that a release search sends, built from what the user asked for. Kept
    /// separate from the request so the facets can be tested without the network. A pressing is told
    /// apart from other pressings by <c>catno</c>, <c>country</c> and year, and each must be written
    /// correctly (a quoted phrase for the catalogue number, a date range for the year).
    /// The user's text is escaped. The format fragment comes from code, not from the user, so it is
    /// not escaped (see <see cref="VinylFormats"/>).
    /// </summary>
    public static string BuildReleaseSearchQuery(
        string text, string? artist = null, string? formatFilter = null,
        string? catalogNumber = null, string? country = null, uint? year = null)
    {
        // Built from a list of clauses because the album is one facet among several and is often
        // missing (a search for an artist only, or a catalogue number only). A query that starts with
        // " AND artist:..." is a Lucene syntax error.
        var clauses = new List<string>(5);
        if (!string.IsNullOrWhiteSpace(text))
            clauses.Add(Escape(text));
        if (!string.IsNullOrWhiteSpace(artist))
            clauses.Add($"artist:\"{Escape(artist)}\"");
        if (!string.IsNullOrWhiteSpace(formatFilter))
            clauses.Add(formatFilter);
        if (!string.IsNullOrWhiteSpace(catalogNumber))
            clauses.Add($"catno:\"{Escape(catalogNumber)}\"");
        if (!string.IsNullOrWhiteSpace(country))
            clauses.Add($"country:{Escape(country)}");
        // A RANGE, not a prefix. MusicBrainz indexes date as a date field, and a wildcard on a date
        // field matches nothing: "date:2008*" returns zero releases with no error. A range is the
        // documented way to say "any time in this year", and it matches a year alone, a year and
        // month, and a full date.
        if (year is > 0)
            clauses.Add($"date:[{year} TO {year}]");
        return string.Join(" AND ", clauses);
    }

    /// <summary>
    /// Searches releases by name, for a user who wants to name their record instead of having it
    /// recognised. One entry per medium: a release is often a set of several discs, and the user has
    /// one of them.
    ///
    /// <para>The result contains only what the search endpoint returns. It does NOT include per-track
    /// lengths, which need one <see cref="LookupReleaseAsync"/> per release at one second each. Rank
    /// on <see cref="ReleaseCandidate.TrackCount"/> and <see cref="ReleaseCandidate.Score"/> first,
    /// and look up only the shortlist.</para>
    /// </summary>
    /// <param name="text">What the user typed: an album name, escaped as literal text. May be empty,
    /// because an artist or a catalogue number is a complete search on its own.</param>
    /// <param name="artist">Narrows the search when the artist is already known, and is the whole
    /// search when no album is given.</param>
    /// <param name="formatFilter">A raw Lucene fragment ANDed onto the query, for callers that only
    /// want one kind of medium; see <see cref="VinylFormats"/>. Not escaped: it is built in code, not
    /// typed by the user.</param>
    /// <param name="filterCatalogNumber">The pressing's catalogue number (Lucene <c>catno</c>). This is
    /// the most reliable way to find the exact record, and it is stamped in the run-out.</param>
    /// <param name="filterCountry">Two-letter country of the pressing (Lucene <c>country</c>).</param>
    /// <param name="filterYear">Year of the pressing, matched as a <c>date</c> RANGE that covers the
    /// whole year, because MusicBrainz dates are full <c>YYYY-MM-DD</c> and a wildcard on its date
    /// field matches nothing.</param>
    public async Task<IReadOnlyList<ReleaseCandidate>> SearchReleasesAsync(
        string text, string? artist = null, int limit = 25, string? formatFilter = null,
        string? filterCatalogNumber = null, string? filterCountry = null, uint? filterYear = null,
        CancellationToken ct = default)
    {
        var query = BuildReleaseSearchQuery(text, artist, formatFilter, filterCatalogNumber, filterCountry, filterYear);
        // An album is not required (an artist or a catalogue number is a search on its own), but
        // something is: an empty query asks MusicBrainz for the whole database.
        if (string.IsNullOrWhiteSpace(text)
            && string.IsNullOrWhiteSpace(artist)
            && string.IsNullOrWhiteSpace(filterCatalogNumber))
            return [];

        var url = $"https://musicbrainz.org/ws/2/release/?query={Uri.EscapeDataString(query)}"
                + $"&limit={limit}&fmt=json";

        var body = await GetAsync(url, ct);
        if (body is null)
            return [];

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("releases", out var releases))
            return [];

        var found = new List<ReleaseCandidate>();
        foreach (var release in releases.EnumerateArray())
        {
            string id = release.TryGetProperty("id", out var rid) ? rid.GetString() ?? "" : "";
            string title = release.TryGetProperty("title", out var rt) ? rt.GetString() ?? "" : "";
            string? credited = JoinArtistCredit(release);
            string? date = release.TryGetProperty("date", out var dt) ? dt.GetString() : null;
            uint year = date is { Length: >= 4 } ds && uint.TryParse(ds[..4], out var y) ? y : 0;

            // "GB", "US", "XE" (Europe), "XW" (worldwide). On vinyl the pressing decides whether the
            // track lengths will match.
            string? country = release.TryGetProperty("country", out var c) ? c.GetString() : null;
            string? disambiguation = release.TryGetProperty("disambiguation", out var dis)
                ? Blank(dis.GetString()) : null;
            double score = release.TryGetProperty("score", out var sc) && sc.TryGetDouble(out var scv)
                ? scv : 0;

            var (label, catalogNumber) = ReadLabel(release);

            string? groupId = release.TryGetProperty("release-group", out var rg)
                           && rg.TryGetProperty("id", out var rgid)
                ? rgid.GetString() : null;

            if (!release.TryGetProperty("media", out var media))
                continue;

            foreach (var medium in media.EnumerateArray())
            {
                string? format = medium.TryGetProperty("format", out var mf) ? mf.GetString() : null;
                int trackCount = medium.TryGetProperty("track-count", out var tc) && tc.TryGetInt32(out var tcv)
                    ? tcv : 0;
                uint position = medium.TryGetProperty("position", out var mp) && mp.TryGetUInt32(out var mpv)
                    ? mpv : 1;

                found.Add(new ReleaseCandidate(
                    id, title, credited, year, date, country, format, position, trackCount,
                    label, catalogNumber, disambiguation, score, groupId));
            }
        }
        return found;
    }

    /// <summary>The label and catalogue number of the first label credit. On a record the catalogue
    /// number is stamped in the run-out and printed on the sleeve, so it is how a pressing is told
    /// from its reissues.</summary>
    private static (string? Label, string? CatalogNumber) ReadLabel(JsonElement release)
    {
        if (!release.TryGetProperty("label-info", out var infos) || infos.ValueKind != JsonValueKind.Array)
            return (null, null);

        foreach (var info in infos.EnumerateArray())
        {
            string? catalog = info.TryGetProperty("catalog-number", out var cn) ? Blank(cn.GetString()) : null;
            string? name = info.TryGetProperty("label", out var lbl) && lbl.TryGetProperty("name", out var ln)
                ? Blank(ln.GetString()) : null;
            if (catalog is not null || name is not null)
                return (name, catalog);
        }
        return (null, null);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>Escapes the Lucene syntax in what the user typed, so a quote or colon is searched for
    /// as a character instead of making the query fail. Public because every query sent to the
    /// service needs it, including queries built outside this class.</summary>
    public static string Escape(string text)
    {
        var escaped = new System.Text.StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (c is '+' or '-' or '&' or '|' or '!' or '(' or ')' or '{' or '}' or '[' or ']'
                  or '^' or '"' or '~' or '*' or '?' or ':' or '\\' or '/')
                escaped.Append('\\');
            escaped.Append(c);
        }
        return escaped.ToString();
    }

    /// <summary>The release found for a CD's table of contents. <see cref="Tracks"/> is the matching
    /// medium's track list: the disc in the drive, not the whole box set.</summary>
    public sealed record DiscLookup(
        string ReleaseId,
        string Title,
        string? Artist,
        uint Year,
        uint DiscNumber,
        uint DiscCount,
        IReadOnlyList<ReleaseTrack> Tracks,
        string? ArtistSort = null,
        string? ArtistId = null,
        string? ReleaseGroupId = null,
        uint OriginalYear = 0,
        string? Barcode = null,
        string? Label = null,
        string? CatalogNumber = null,
        string? Genre = null,
        string? Country = null,
        string? Format = null,
        string? DiscSubtitle = null,
        string? PrimaryType = null,
        IReadOnlyList<string>? SecondaryTypes = null,
        IReadOnlyList<string>? Genres = null);

    /// <summary>
    /// What a disc-id lookup asks for: the same includes as a release lookup.
    ///
    /// <para>A CD rip uses this lookup, and the rip is the only time this data is fetched: tracks come
    /// off the disc with no tags, and a finished rip is not looked up again. If this asked for less
    /// than a release lookup, the files RPM creates itself would be its worst-tagged files.</para>
    /// </summary>
    private const string DiscIncludes =
        "artists+recordings+artist-credits+isrcs+genres+labels+release-groups" +
        "+artist-rels+recording-level-rels+work-rels+work-level-rels";

    /// <summary>
    /// Looks a CD up by its disc ID. When the exact disc ID has never been submitted (common for
    /// pressings that differ by a few frames), it falls back to the fuzzy TOC search, which matches
    /// on the track offsets.
    /// </summary>
    /// <param name="discId">A disc ID as computed by <c>MusicBrainzDiscId</c>.</param>
    /// <param name="tocString">The <c>?toc=</c> form of the same TOC, for the fuzzy fallback.</param>
    public async Task<DiscLookup?> LookupDiscAsync(string discId, string? tocString = null,
        CancellationToken ct = default)
    {
        var url = $"https://musicbrainz.org/ws/2/discid/{Uri.EscapeDataString(discId)}" +
                  $"?inc={DiscIncludes}&cdstubs=no&fmt=json";

        var body = await GetAsync(url, ct);

        if (body is null && !string.IsNullOrEmpty(tocString))
        {
            // "-" is MusicBrainz's placeholder for "I don't have a known disc ID, match my TOC".
            var fuzzy = $"https://musicbrainz.org/ws/2/discid/-?toc={Uri.EscapeDataString(tocString)}" +
                        $"&inc={DiscIncludes}&cdstubs=no&fmt=json";
            body = await GetAsync(fuzzy, ct);
        }

        if (body is null)
            return null;

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("releases", out var releases) ||
            releases.ValueKind != JsonValueKind.Array || releases.GetArrayLength() == 0)
            return null;

        var release = releases[0];
        string releaseId = release.TryGetProperty("id", out var rid) ? rid.GetString() ?? "" : "";
        string title = release.TryGetProperty("title", out var rt) ? rt.GetString() ?? "" : "";
        var credit = MusicBrainzRelations.ArtistCredit(release);
        string? artist = credit.Name ?? JoinArtistCredit(release);
        uint year = MusicBrainzRelations.Year(release, "date");

        var releaseGroup = release.TryGetProperty("release-group", out var drg) ? drg : default;
        var (discLabel, discCatalogNumber) = MusicBrainzRelations.LabelInfo(release);

        // Everything known about the pressing, built in one place so both return paths include it.
        DiscLookup Result(uint number, uint count, IReadOnlyList<ReleaseTrack> found,
                          string? format = null, string? subtitle = null) =>
            new(releaseId, title, artist, year, number, count, found,
                ArtistSort: credit.SortName,
                ArtistId: credit.Id,
                ReleaseGroupId: releaseGroup.ValueKind == JsonValueKind.Object &&
                                releaseGroup.TryGetProperty("id", out var drgid) ? drgid.GetString() : null,
                OriginalYear: MusicBrainzRelations.Year(releaseGroup, "first-release-date"),
                Barcode: release.TryGetProperty("barcode", out var dbc) ? dbc.GetString() : null,
                Label: discLabel,
                CatalogNumber: discCatalogNumber,
                Genre: MusicBrainzRelations.TopGenre(release, releaseGroup),
                Country: release.TryGetProperty("country", out var dco) ? dco.GetString() : null,
                Format: format,
                DiscSubtitle: subtitle,
                PrimaryType: PrimaryTypeOf(releaseGroup),
                SecondaryTypes: SecondaryTypesOf(releaseGroup),
                Genres: MusicBrainzRelations.VotedGenres(release, releaseGroup));

        // A release can have several discs. Pick the medium that has this disc ID. After a fuzzy
        // lookup the ID does not appear, so fall back to the first medium.
        if (!release.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Array ||
            media.GetArrayLength() == 0)
            return Result(1, 1, []);

        var medium = media.EnumerateArray().FirstOrDefault(m => MediumHasDisc(m, discId));
        if (medium.ValueKind != JsonValueKind.Object)
            medium = media[0];

        uint discNumber = medium.TryGetProperty("position", out var pos) && pos.TryGetUInt32(out var pv) ? pv : 1;
        uint discCount = (uint)media.GetArrayLength();

        var tracks = new List<ReleaseTrack>();
        if (medium.TryGetProperty("tracks", out var mediumTracks) && mediumTracks.ValueKind == JsonValueKind.Array)
        {
            foreach (var track in mediumTracks.EnumerateArray()
                         .OrderBy(t => t.TryGetProperty("position", out var tp) ? tp.GetInt32() : 0))
            {
                string trackTitle = track.TryGetProperty("title", out var tt) ? tt.GetString() ?? "" : "";
                var recording = track.TryGetProperty("recording", out var rec) ? rec : default;

                double? lengthSeconds = null;
                if (track.TryGetProperty("length", out var len) && len.ValueKind == JsonValueKind.Number)
                    lengthSeconds = len.GetDouble() / 1000.0;
                else if (recording.ValueKind == JsonValueKind.Object &&
                         recording.TryGetProperty("length", out var rlen) && rlen.ValueKind == JsonValueKind.Number)
                    lengthSeconds = rlen.GetDouble() / 1000.0;

                var trackCredit = MusicBrainzRelations.ArtistCredit(track);
                if (trackCredit.IsEmpty)
                    trackCredit = MusicBrainzRelations.ArtistCredit(recording);

                var performers = MusicBrainzRelations.Recording(recording);
                var work = MusicBrainzRelations.Work(recording);

                uint position = track.TryGetProperty("position", out var p) && p.TryGetUInt32(out var pv2)
                    ? pv2 : (uint)(tracks.Count + 1);

                tracks.Add(new ReleaseTrack(
                    position,
                    trackTitle,
                    trackCredit.Name ?? artist,
                    lengthSeconds,
                    ArtistSort: trackCredit.SortName ?? credit.SortName,
                    RecordingId: recording.ValueKind == JsonValueKind.Object &&
                                 recording.TryGetProperty("id", out var drid) ? drid.GetString() : null,
                    ArtistId: trackCredit.Id ?? credit.Id,
                    Isrc: MusicBrainzRelations.FirstIsrc(recording),
                    Work: work.Work,
                    MovementName: work.MovementName,
                    MovementNumber: work.MovementNumber,
                    Composers: work.Composers,
                    ComposerSort: work.ComposerSort,
                    Lyricist: work.Lyricist,
                    Arranger: work.Arranger,
                    Conductor: performers.Conductor,
                    Ensemble: performers.Ensemble,
                    Soloists: performers.Soloists));
            }
        }

        return Result(
            discNumber, discCount, tracks,
            format: medium.TryGetProperty("format", out var dmf) ? dmf.GetString() : null,
            subtitle: medium.TryGetProperty("title", out var dmt) && dmt.GetString() is { Length: > 0 } dms
                ? dms : null);
    }

    private static bool MediumHasDisc(JsonElement medium, string discId) =>
        medium.TryGetProperty("discs", out var discs) &&
        discs.ValueKind == JsonValueKind.Array &&
        discs.EnumerateArray().Any(d =>
            d.TryGetProperty("id", out var id) &&
            string.Equals(id.GetString(), discId, StringComparison.Ordinal));

    private static string? JoinArtistCredit(JsonElement element)
    {
        if (!element.TryGetProperty("artist-credit", out var ac) || ac.ValueKind != JsonValueKind.Array)
            return null;
        var names = new List<string>();
        foreach (var a in ac.EnumerateArray())
        {
            if (a.TryGetProperty("name", out var n))
                names.Add(n.GetString() ?? "");
            else if (a.TryGetProperty("artist", out var ar) && ar.TryGetProperty("name", out var arn))
                names.Add(arn.GetString() ?? "");
        }
        return names.Count == 0 ? null : string.Join(", ", names);
    }
}
