using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using RPM.Fingerprint;

namespace RPM.Core.Cloud;

/// <summary>The store is full. The user has to delete something or stop syncing.</summary>
public sealed class CloudQuotaExceededException(long usedBytes, long quotaBytes)
    : Exception($"Cloud storage full: {usedBytes} of {quotaBytes} bytes used.")
{
    public long UsedBytes { get; } = usedBytes;
    public long QuotaBytes { get; } = quotaBytes;
}

/// <summary>The server refused to link this file. Either the caller could not prove that they hold it,
/// or the bytes were not what they said they were. Both mean that this track is not going up, and
/// retrying will not change that.
/// </summary>
public sealed class CloudRejectedException(string message) : Exception(message);

/// <summary>A playlist write that the store refused, because the copy being replaced is not the copy
/// that the store holds. Somebody else, such as another computer or a phone, wrote in between.
///
/// <para><b>This is not a failure.</b> Nothing was lost and nothing was written. The store declined to
/// let one head silently overwrite another head's change. <see cref="Current"/> is what is actually
/// stored. It is handed over with the refusal, so the caller can merge and write again without asking
/// the store again.</para>
/// </summary>
public sealed class PlaylistConflictException(CloudPlaylist current, string message) : Exception(message)
{
    /// <summary>The playlist as the store holds it now.</summary>
    public CloudPlaylist Current { get; } = current;
}

/// <summary>
/// An album's stored cover, as the cloud holds it: which album, and when the picture was last written.
///
/// <para>
/// <paramref name="UpdatedAt"/> is the only thing that changes when a cover is replaced. The object's
/// address is derived from the album. So a better scan of the same record lands at the same URL, and a
/// client that has the old one cached has nothing else to tell them apart. Every cover cache is keyed on
/// this value. Zero means the server did not say. It reads as "no version known", not as "new".
/// </para>
/// </summary>
public sealed record StoredCover(string AlbumKey, long UpdatedAt, string? Url = null);

public sealed record CloudQuota(
    [property: JsonPropertyName("usedBytes")] long UsedBytes,
    [property: JsonPropertyName("quotaBytes")] long QuotaBytes,
    [property: JsonPropertyName("fileCount")] int FileCount,
    /// <summary>No ceiling. This is what supporting rpm buys. False comes from a server that predates the
    /// field. Such a server reports the free account's 100 GB limit.</summary>
    [property: JsonPropertyName("unlimited")] bool Unlimited = false)
{
    /// <summary>The same answer as the room it describes. This decides whether anything more may be sent.
    /// See <see cref="CloudSpace"/>.</summary>
    public CloudSpace Space => new(UsedBytes, QuotaBytes, Unlimited);
}

/// <summary>The catalogue fields: what a client needs to draw an album, rather than a bag of files.
/// Every field is optional, and zero means "not known" rather than zero. Rows written before the
/// catalogue existed carry none of them until they are backfilled.
/// </summary>
public sealed record CloudTrackTags(
    [property: JsonPropertyName("durationSeconds")] double DurationSeconds = 0,
    [property: JsonPropertyName("trackNumber")] int TrackNumber = 0,
    [property: JsonPropertyName("trackCount")] int TrackCount = 0,
    [property: JsonPropertyName("discNumber")] int DiscNumber = 0,
    [property: JsonPropertyName("discCount")] int DiscCount = 0,
    [property: JsonPropertyName("year")] int Year = 0,
    [property: JsonPropertyName("genre")] string? Genre = null,
    [property: JsonPropertyName("albumArtist")] string? AlbumArtist = null,
    // What the file is. Same "zero means unknown" rule as the rest. A row uploaded before these existed
    // carries none of them, and a lossy encode has no bit depth to carry.
    [property: JsonPropertyName("extension")] string? Extension = null,
    [property: JsonPropertyName("channels")] int Channels = 0,
    [property: JsonPropertyName("sampleRate")] int SampleRate = 0,
    [property: JsonPropertyName("bitsPerSample")] int BitsPerSample = 0,
    [property: JsonPropertyName("bitrateKbps")] int BitrateKbps = 0,
    [property: JsonPropertyName("codecDescription")] string? CodecDescription = null,
    // Which composition the track is part of. A row uploaded before these existed reads them back as
    // null or zero, which means "not known". The desktop's catalogue refresh fills them in on its next
    // pass. This is the same approach as the format fields above.
    [property: JsonPropertyName("work")] string? Work = null,
    [property: JsonPropertyName("movementName")] string? MovementName = null,
    [property: JsonPropertyName("movementNumber")] int MovementNumber = 0,
    [property: JsonPropertyName("composer")] string? Composer = null,
    // How loud the recording is. This lets a track played from the cloud, on a phone that has never
    // held the file, be levelled with the tracks around it. Same "zero means unknown" rule as the
    // rest. A row uploaded before these existed carries neither value. Whoever holds the bytes fills
    // them in: the desktop's catalogue refresh, or the backfill in the transcoder.
    [property: JsonPropertyName("loudnessLufs")] double LoudnessLufs = 0,
    [property: JsonPropertyName("truePeak")] double TruePeak = 0,
    // The listener's own mark on the song. It is on the catalogue row, not only in the sidecar. The
    // sidecar is one blob per track, with the cover inside it. So a phone that draws hearts across a
    // library would have to fetch a full-size JPEG per song to find out which songs are favourites.
    // False is a real answer here, unlike the text fields above, where blank means "not known". Removing
    // a favourite must also be sent to the store.
    [property: JsonPropertyName("favorite")] bool Favorite = false,
    [property: JsonPropertyName("favoritedAt")] long FavoritedAt = 0,
    // Whether the file's own flag calls the lyrics explicit. This makes the E beside a title a fact from
    // the record, not a guess. It is on the catalogue row for the same reason as the mark above it. The
    // answer lives in the sidecar. A phone that draws badges down a track list would otherwise fetch a
    // full-size JPEG per song to find out which songs have one. Zero is ContentAdvisory.Unknown. That is
    // the answer for the many files that carry no flag, and also for a row that nobody has read yet. For
    // a badge the two are the same, because neither draws one.
    [property: JsonPropertyName("advisory")] int Advisory = 0,
    // Which RECORD this is, not only which album. A phone has no files to read tags from, and nothing
    // that it could pick a pressing with. Without these fields it would look up the shared album entry by
    // artist and title alone, and it could land on a pressing that the uploading computer never used.
    // These fields belong to the album, not the track, and they are the same on every row of it. The row
    // is only the place where a client can reach them, without fetching a sidecar for each song.
    [property: JsonPropertyName("catno")] string? Catno = null,
    [property: JsonPropertyName("barcode")] string? Barcode = null,
    [property: JsonPropertyName("mbReleaseId")] string? MusicBrainzReleaseId = null,
    [property: JsonPropertyName("discId")] string? MusicBrainzDiscId = null,
    // What kind of release the record is, as ReleaseTypes.ToStored writes it ("Album", "Ep", "Live"...). The
    // phone and the TV shelve an artist's records by it instead of guessing from the title. Album-level like
    // the pressing above it, and on the row for the same reason.
    [property: JsonPropertyName("releaseType")] string? ReleaseType = null)
{
    /// <summary>Whether the row knows how loud the recording is. Zero means "not measured". A real
    /// reading is negative, because music sits below full scale. A track at exactly zero LUFS would be
    /// a square wave.</summary>
    public bool HasLoudness => LoudnessLufs < 0;

    /// <summary>The advisory flag that the row states, or null when it states none. Zero means "not known".
    /// A value that no client writes is read the same way, so a pull never sets a flag that nobody
    /// chose.</summary>
    [JsonIgnore]
    public Models.ContentAdvisory? StatedAdvisory => Advisory switch
    {
        (int)Models.ContentAdvisory.Explicit => Models.ContentAdvisory.Explicit,
        (int)Models.ContentAdvisory.Clean => Models.ContentAdvisory.Clean,
        _ => null,
    };

    /// <summary>Whether the row knows what the file is, as opposed to what it holds. This is false on every
    /// row written before the format fields existed. The desktop's catalogue refresh looks for these
    /// rows.</summary>
    public bool HasFormat => !string.IsNullOrWhiteSpace(Extension) || Channels > 0 || SampleRate > 0;

    /// <summary>What the sleeve would print: "FLAC", "16/44.1", "5.1". Unknown parts are left out. The list
    /// is empty when the row has no format at all.</summary>
    public IReadOnlyList<string> Format => HasFormat
        ? Metadata.AudioFormat.Describe(
            Extension, Channels, CodecDescription, BitsPerSample, SampleRate, BitrateKbps)
        : [];
}

public sealed record CloudTrack(
    [property: JsonPropertyName("rpmId")] string RpmId,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("relativePath")] string? RelativePath,
    [property: JsonPropertyName("originalSize")] long OriginalSize,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("artist")] string? Artist,
    [property: JsonPropertyName("album")] string? Album,
    [property: JsonPropertyName("addedAt")] long AddedAt,
    [property: JsonPropertyName("tags")] CloudTrackTags? Tags = null,
    /// <summary>
    /// When the store last wrote this row, by the store's own clock (unix seconds).
    ///
    /// <para>This is what makes more than one machine possible. A client compares it with the stamp it
    /// last accepted for the same row. Both are server clocks, so nothing depends on two computers
    /// agreeing about the time. Zero on rows written before the field existed. It reads as "never changed
    /// since it was uploaded".
    /// </para>
    /// </summary>
    [property: JsonPropertyName("updatedAt")] long UpdatedAt = 0)
{
    /// <summary>Track length, or zero when the row has not been backfilled. The player still learns the
    /// real length after it has parsed the stream. This value lets a track list show the length before
    /// the track is played.</summary>
    public TimeSpan Duration =>
        Tags is { DurationSeconds: > 0 } t ? TimeSpan.FromSeconds(t.DurationSeconds) : TimeSpan.Zero;

    // Where a row sits on the record is not a property here. That rule belongs to the desktop, and it
    // lives in one place: AlbumTracklist.ByDisc, through CloudAlbumGrouping.DiscOf and TrackOf. Do not
    // add an ordinal to this record. It would be a second rule for the same question, and it could
    // disagree with the desktop about an untagged disc, which the desktop reads as disc 1.
}

/// <summary>The store's answer about one track that it was asked to vouch for.
///
/// <para><see cref="Ok"/> means that everything is there: the row, a blob whose stored checksum matches
/// the hash it is filed under, and this user's sidecar. If anything is missing, <see cref="Reason"/>
/// says which part. That is worth a line in the log. It should not be a silent decision not to free
/// some space.</para>
/// </summary>
public sealed record CloudVerification(
    [property: JsonPropertyName("rpmId")] string RpmId,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("sha256")] string? Sha256 = null,
    [property: JsonPropertyName("blobBytes")] long BlobBytes = 0,
    [property: JsonPropertyName("sidecarBytes")] long SidecarBytes = 0,
    [property: JsonPropertyName("reason")] string? Reason = null);

internal sealed record VerifyResponse(
    [property: JsonPropertyName("files")] IReadOnlyList<CloudVerification> Files);

/// <summary>What the store said after it accepted a set of mood lists.</summary>
public sealed record MoodListsPutResponse(
    [property: JsonPropertyName("computedAt")] long ComputedAt,
    [property: JsonPropertyName("lists")] int Lists);

/// <summary>One generated list as the cloud holds it.</summary>
public sealed record CloudMoodList(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("genre")] string? Genre,
    [property: JsonPropertyName("decade")] int Decade,
    [property: JsonPropertyName("trackIds")] IReadOnlyList<string> TrackIds);

/// <summary>A whole set of mood lists as the cloud holds it.
/// </summary>
/// <param name="ComputedAt">When the store stamped it, in unix milliseconds. This lets a phone show how
/// old the lists are. A computer that has not run for a month is a real state. A silent month-old list
/// is not the same thing as a current one.</param>
public sealed record CloudMoodLists(
    [property: JsonPropertyName("recipe")] int Recipe,
    [property: JsonPropertyName("computedAt")] long ComputedAt,
    [property: JsonPropertyName("lists")] IReadOnlyList<CloudMoodList> Lists);

/// <summary>A playlist as the cloud holds it: a name and the tracks it plays, in order, by RPM id.
///
/// <para>This is flat on purpose. A playlist that nests other playlists is resolved to its play order
/// before it is uploaded. The desktop already has to do this to play it. So anything that reads a cloud
/// playlist gets a list that it can play, not a graph that it must walk and guard against cycles. The
/// cost is that editing a nested playlist leaves its parents out of date until they are uploaded again. A
/// sync run does that.
/// </para>
///
/// <para>
/// <paramref name="Folder"/> is the one part of the tree that does travel: the folders that a playlist
/// sits in, outermost first. It is a list of segments, not one path string. A folder can be named
/// anything that a person can type, including a name with a slash in it. A separator that can appear
/// inside a name cannot be undone, so the segments are kept apart. An empty folder means the top of the
/// tree. A playlist stored before this field existed reads back with no field at all, which also means
/// the top of the tree.
/// </para>
/// </summary>
public sealed record CloudPlaylist(
    [property: JsonPropertyName("playlistId")] string PlaylistId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("trackIds")] IReadOnlyList<string> TrackIds,
    [property: JsonPropertyName("folder")] IReadOnlyList<string>? Folder,
    [property: JsonPropertyName("updatedAt")] long UpdatedAt = 0)
{
    /// <summary>The folders that this playlist sits in, outermost first. This is never null. Read it
    /// through here, not through <see cref="Folder"/>. The field is absent on every playlist written
    /// before folders were stored, and "no field" and "top of the tree" are the same answer.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> FolderChain => Folder ?? [];
}

/// <summary>
/// The desktop's half of the cloud store.
///
/// <para>An upload is deliberately not one call. The client asks first (<see cref="UploadAsync"/> →
/// <c>init</c>). The server answers in one of two ways. Either nobody has these bytes, so send them. Or
/// somebody already has them, so prove that you have them too. The second answer is what stops a hash
/// from being a key to a stranger's music. It is also why uploading a track that ten thousand other
/// people already own costs three round trips and no audio at all.
/// </para>
/// </summary>
public sealed partial class RpmCloudClient(HttpClient http, Func<CancellationToken, Task<string?>> accessTokenProvider)
{
    /// <summary>What the server said about the user's storage after an upload.</summary>
    /// <param name="Renditions">Whether the head clip and the free rate were made and stored as part of
    /// this upload. False does not mean that the upload failed. The track is in the cloud either way. It
    /// only means that the catch-up pass should come back for them.</param>
    public sealed record UploadOutcome(CloudQuota Quota, bool Deduplicated, bool Renditions);

    public async Task<CloudQuota> GetQuotaAsync(CancellationToken ct = default)
        => await SendAsync<CloudQuota>(HttpMethod.Get, "cloud/quota", null, ct);

    /// <summary>Reports names that stand for the same record. Each bundle is one track: a set of keys
    /// that this machine worked out at the same moment.
    ///
    /// <para>What is sent is not the music, and not the metadata. It is the fact that a hash of one file,
    /// a hash of what it decoded to, and a track id are three names for one piece of audio. Only the
    /// machine that holds the file can know this, and every machine throws it away today. Once it is
    /// written down, everybody who holds any one of those names can find what has already been worked out
    /// about the record.</para>
    /// </summary>
    /// <returns>The record that each bundle resolved to, in the order sent. Null where the store could not
    /// place a bundle.</returns>
    public async Task<IReadOnlyList<string?>> SubmitRecordingKeysAsync(
        IReadOnlyList<IReadOnlyList<string>> bundles, CancellationToken ct = default)
    {
        if (bundles is not { Count: > 0 })
            return [];

        var said = await SendAsync<RecordingKeysResponse>(HttpMethod.Post, "recordings/keys",
            JsonContent.Create(
                new KeyBundlesRequest([.. bundles.Select(keys => new KeyBundle(keys))]),
                RpmCloudJson.Default.KeyBundlesRequest), ct);

        return said.Results is null ? [] : [.. said.Results.Select(r => r.RecordingId)];
    }

    private sealed record RecordingKeysResponse(
        [property: JsonPropertyName("results")] IReadOnlyList<RecordingKeyResult>? Results);

    private sealed record RecordingKeyResult(
        [property: JsonPropertyName("recordingId")] string? RecordingId);

    /// <summary>What the world already knows about these records. There is one bundle of names per
    /// track. Answers come in the order asked, with null where nothing is known.
    ///
    /// <para>This is asked before anything is opened. If somebody has already worked out a track's loudness
    /// or beat grid, this machine does not have to decode the track. The cheapest name to ask with is the
    /// hash of the tag-stripped file. Making that hash costs no decode.</para>
    /// </summary>
    public async Task<IReadOnlyList<KnownRecording?>> LookUpRecordingsAsync(
        IReadOnlyList<IReadOnlyList<string>> bundles, CancellationToken ct = default)
    {
        if (bundles is not { Count: > 0 })
            return [];

        var said = await SendAsync<RecordingLookUpResponse>(HttpMethod.Post, "recordings/facts/lookup",
            JsonContent.Create(
                new KeyBundlesRequest([.. bundles.Select(keys => new KeyBundle(keys))]),
                RpmCloudJson.Default.KeyBundlesRequest), ct);

        if (said.Results is null)
            return [];

        return [.. said.Results.Select(r =>
            r.RecordingId is { Length: > 0 } id ? new KnownRecording(id, r.Facts ?? []) : null)];
    }

    /// <summary>Says what this machine measured about one track, under the names that it knows the track
    /// by.
    /// </summary>
    /// <returns>The record that the track was filed under, or null when the names say nothing that the
    /// store can place. The usual case is a track that this machine has only one name for.</returns>
    public async Task<string?> ReportRecordingAsync(
        IReadOnlyList<string> keys, IReadOnlyList<RecordingFactBody> facts, CancellationToken ct = default)
    {
        if (keys is not { Count: > 0 })
            return null;

        var said = await SendAsync<RecordingReportResponse>(HttpMethod.Post, "recordings/facts",
            JsonContent.Create(
                new FactsReportRequest(keys, facts), RpmCloudJson.Default.FactsReportRequest), ct);

        return said.RecordingId;
    }

    /// <summary>Asks the store to work out these records, for the ones that nobody has analysed yet.
    ///
    /// <para>This is the look-ahead that a set makes on a machine that cannot analyse well for itself, or
    /// cannot analyse at all for a record that it is streaming. The answer comes at once. <em>ready</em>
    /// means that somebody already knows the record. <em>queued</em> means that the store's own transcoder
    /// will read it. Nothing waits on this. A record with no beat grid is blended rather than beat-matched,
    /// and its grid arrives while the track before it plays.</para>
    /// </summary>
    public async Task<(int Ready, int Queued)> RequestAnalysisAsync(
        IReadOnlyList<string> rpmIds, CancellationToken ct = default)
    {
        if (rpmIds is not { Count: > 0 })
            return (0, 0);

        try
        {
            var said = await SendAsync<AnalyseResponse>(HttpMethod.Post, "recordings/analyse",
                JsonContent.Create(new RpmIdsRequest(rpmIds), RpmCloudJson.Default.RpmIdsRequest), ct);
            return (said.Ready, said.Queued);
        }
        catch (Exception)
        {
            // A set does not stop because the store refuses a request for one record.
            return (0, 0);
        }
    }

    private sealed record AnalyseResponse(
        [property: JsonPropertyName("ready")] int Ready,
        [property: JsonPropertyName("queued")] int Queued);

    /// <summary>Runs one window of a record through the store's beat model, and returns what the model
    /// said.
    ///
    /// <para>This is for a computer that cannot run the model itself. It decodes its own file and takes
    /// its own spectrogram. Neither step is expensive. Only the window crosses the wire, packed by <see
    /// cref="GridWire"/>. The model on the other end is the one that ships with the app. So the grid that
    /// comes back is the grid that this machine would have produced if it could run the model. That is
    /// what makes it safe to publish to the pool for everybody else.</para>
    ///
    /// <para>Null when the store does not answer. The caller then falls back to the DSP grid, the same as
    /// it does for a missing model file.</para>
    /// </summary>
    /// <param name="window">The window: <paramref name="frames"/> × <paramref name="bands"/> values,
    /// packed by <see cref="GridWire.Pack"/> at <paramref name="precision"/>.</param>
    /// <param name="precision">How the window is packed. It is named in the request, so the store reads
    /// it that way and answers at the same precision.</param>
    public async Task<(string Beat, string Downbeat)?> ReadGridWindowAsync(
        string window, int frames, int bands, GridPrecision precision, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(window))
            return null;

        try
        {
            var said = await SendAsync<GridWindowResponse>(HttpMethod.Post, "recordings/grid",
                JsonContent.Create(
                    new GridWindowRequest(frames, bands, window, GridWire.Name(precision)),
                    RpmCloudJson.Default.GridWindowRequest), ct);

            return said.Beat is { Length: > 0 } beat && said.Downbeat is { Length: > 0 } downbeat
                ? (beat, downbeat)
                : null;
        }
        // Everything except a cancel. A cancel is an answer of its own. If it were swallowed and turned
        // into "no window", the caller would take it for a store that did not answer. See
        // CloudBeatModel.Ask, which is why the caller's own token does not reach this far.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private sealed record GridWindowResponse(
        [property: JsonPropertyName("beat")] string? Beat,
        [property: JsonPropertyName("downbeat")] string? Downbeat);

    private sealed record RecordingLookUpResponse(
        [property: JsonPropertyName("results")] IReadOnlyList<KnownRecordingBody>? Results);

    private sealed record KnownRecordingBody(
        [property: JsonPropertyName("recordingId")] string? RecordingId,
        [property: JsonPropertyName("facts")] IReadOnlyList<RecordingFactBody>? Facts);

    private sealed record RecordingReportResponse(
        [property: JsonPropertyName("recordingId")] string? RecordingId);

    /// <summary>Sends one failure that the user asked to send. This is sent only when the user presses
    /// Report. Crash reports are the other path, and they too need the user's consent (see <see
    /// cref="ReportCrashAsync"/>). The app keeps its failures locally and shows them. This method is what
    /// the button beside a failure does.
    /// </summary>
    /// <returns>The server's id for the report, or null when the server would not take it. The caller shows
    /// "could not send" and leaves the button active. Reporting is optional on both sides.</returns>
    public async Task<string?> ReportErrorAsync(ErrorReport report, CancellationToken ct = default)
    {
        try
        {
            var response = await SendAsync<ErrorReportResponse>(HttpMethod.Post, "errors",
                JsonContent.Create(report, RpmCloudJson.Default.ErrorReport), ct);

            return response.ErrorId;
        }
        catch (Exception ex) when (
            ex is HttpRequestException            // offline, DNS, a 500
                or CloudRejectedException         // the API would not take it
                or InvalidOperationException      // not signed in, or an empty answer
                or TaskCanceledException)         // it took too long to be useful
        {
            return null;
        }
    }

    /// <summary>The largest crash report body that the server accepts, in bytes. This is the API's
    /// <c>CrashReportLimits.MaxRequestBytes</c>.</summary>
    public const int MaxCrashRequestBytes = 1_500_000;

    /// <summary>Sends one crash report. This is called only when the user has agreed to crash reports.
    /// The app key goes with every request from this client. The user's token goes too, when there is
    /// one. So a report from a signed-in user names the account, and a signed-out user can still send
    /// one.
    /// </summary>
    /// <returns>The server's id, or null when the report did not arrive. The caller keeps the report
    /// pending.</returns>
    public async Task<string?> ReportCrashAsync(CrashReport report, CancellationToken ct)
    {
        string? token;
        try { token = await accessTokenProvider(ct); }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            token = null;   // a token that will not refresh is the same as none here
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "errors/crash")
            {
                Content = new ByteArrayContent(CrashReportBody.For(report))
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" } },
                },
            };
            if (token is not null)
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return null;

            var answer = await resp.Content.ReadFromJsonAsync(RpmCloudJson.Default.ErrorReportResponse, ct);
            return answer?.ErrorId;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Puts one track in the cloud. <paramref name="canonical"/> is the tag-stripped blob (see
    /// <see cref="CanonicalAudio"/>), and <paramref name="sidecar"/> holds the user's own tags. The
    /// caller owns both, and keeps the canonical file alive until this returns, because answering a
    /// challenge needs to read it.
    /// </summary>
    public async Task<UploadOutcome> UploadAsync(
        string rpmId,
        CanonicalFile canonical,
        TagSidecar sidecar,
        long originalSizeBytes,
        CancellationToken ct = default)
    {
        var init = await SendAsync<UploadInitResponse>(HttpMethod.Post, "cloud/upload/init",
            JsonContent.Create(
                new UploadInitRequest(
                    rpmId,
                    canonical.Sha256,
                    canonical.SizeBytes,
                    originalSizeBytes,
                    Path.GetExtension(canonical.Path).ToLowerInvariant(),
                    sidecar.Title,
                    sidecar.Artist,
                    sidecar.Album,
                    sidecar.RelativePath),
                RpmCloudJson.Default.UploadInitRequest), ct);

        // The sidecar is ours alone, and it always goes up. It is what makes the shared, anonymous blob
        // this user's track again when it comes back down.
        await PutBytesAsync(init.SidecarUrl, System.Text.Encoding.UTF8.GetBytes(sidecar.ToJson()),
            "application/json", checksumBase64: null, ct);

        bool deduplicated = init.Status == "challenge";

        if (deduplicated)
        {
            // A challenge can only be answered with all three of its parts. If one is missing, the server
            // has sent an answer that this client cannot act on. So it is reported as that, and it is not
            // sent back up as a null. The server would then refuse it, for a reason that looks different.
            if (init is not { ChallengeId: { } challengeId, Nonce: { } sent, Ranges: { } asked })
                throw new CloudRejectedException(
                    "The upload challenge arrived without its id, nonce or ranges.");

            var nonce = Convert.FromBase64String(sent);
            var ranges = asked.Select(r => (r.Offset, r.Length)).ToList();
            var answers = await CanonicalAudio.AnswerChallengeAsync(canonical.Path, nonce, ranges, ct);

            await SendAsync<ProveResponse>(HttpMethod.Post, "cloud/upload/prove",
                JsonContent.Create(
                    new ProveRequest(challengeId, answers), RpmCloudJson.Default.ProveRequest), ct);
        }
        else
        {
            await PutFileAsync(init.BlobUrl!, canonical.Path, init.ChecksumSha256, ct);
        }

        // The catalogue fields are sent with complete, not init. Init can be answered from a blob that
        // somebody else uploaded. But complete is what writes this user's row, and these are their tags.
        var quota = await SendAsync<CloudQuota>(HttpMethod.Post, "cloud/upload/complete",
            JsonContent.Create(
                new UploadCompleteRequest(
                    rpmId,
                    canonical.Sha256,
                    sidecar.RelativePath,
                    sidecar.Title,
                    sidecar.Artist,
                    sidecar.Album,
                    TagsFrom(sidecar)),
                RpmCloudJson.Default.UploadCompleteRequest), ct);

        // The renditions come last, and they are best-effort. A track is in the cloud once complete has
        // answered. Everything below only makes the track start faster and stream smaller. A failure here
        // must not undo an upload that worked.
        var renditions = await MakeRenditionsAsync(rpmId, canonical.Path, ct);

        return new UploadOutcome(quota, deduplicated, renditions.Outcome == RenditionOutcome.Made);
    }

    /// <summary>
    /// The first seconds of each of these tracks, keyed by RPM id.
    ///
    /// <para>One call is made for a whole album, not one per track. The head start is only useful if it
    /// is already there when the track is chosen. Twenty round trips are too slow for that. A track with
    /// no clip yet is simply absent from the answer, and the server notes that it wants one.</para>
    /// </summary>
    public async Task<IReadOnlyDictionary<string, byte[]>> GetHeadClipsAsync(
        IReadOnlyList<string> rpmIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rpmIds);

        var clips = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (rpmIds.Count == 0) return clips;

        var answer = await SendAsync<HeadClipsResponse>(
            HttpMethod.Post, "cloud/heads",
            JsonContent.Create(new RpmIdsRequest(rpmIds), RpmCloudJson.Default.RpmIdsRequest), ct);

        foreach (var clip in answer.Clips ?? [])
            if (clip.Audio is { Length: > 0 } audio)
                clips[clip.RpmId] = Convert.FromBase64String(audio);

        return clips;
    }

    /// <summary>How many head clips may be asked for at once. This is the server's own cap.</summary>
    public const int MostHeadClips = 50;

    /// <summary>How many tracks one verify request may name. This is the server's own cap.</summary>
    public const int MostVerified = 200;

    /// <summary>The store's own word that these tracks can be rebuilt from it. The blob is there, its
    /// stored checksum matches the hash it is filed under, and the user's sidecar is beside it.
    ///
    /// <para>This is asked before the last local copy of anything is deleted. The catalogue listing is
    /// not enough for that. It is a database row that says a track was linked once. A client that
    /// offloads a track on the strength of that row is trusting a record, not the thing the record
    /// describes.
    /// </para>
    ///
    /// <para>An id that is missing from the answer means no. So does every id, when the server is too
    /// old to have this route. The caller then keeps the file. This is the safe choice when the answer is
    /// unknown, and the caller says so once.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyDictionary<string, CloudVerification>> VerifyAsync(
        IReadOnlyList<string> rpmIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rpmIds);

        var verdicts = new Dictionary<string, CloudVerification>(StringComparer.Ordinal);
        if (rpmIds.Count == 0) return verdicts;

        foreach (var chunk in rpmIds.Chunk(MostVerified))
        {
            var answer = await SendAsync<VerifyResponse>(
                HttpMethod.Post, "cloud/verify",
                JsonContent.Create(new RpmIdsRequest(chunk), RpmCloudJson.Default.RpmIdsRequest), ct);

            foreach (var file in answer.Files ?? [])
                verdicts[file.RpmId] = file;
        }

        return verdicts;
    }

    /// <summary>How many tracks one transcode request may name. This is the server's own cap. Longer lists
    /// are sent in pieces by <see cref="RequestTranscodeAsync"/>.</summary>
    public const int MostTranscodeTracks = 200;

    /// <summary>Asks the store to have these tracks ready at a given rate. This covers the whole record
    /// ahead of playback, rather than one request per track as each one is reached.
    ///
    /// <para>This is fire-and-forget in spirit. The server queues what is missing and answers at once. A
    /// stream that is requested in the meantime falls back in the same way as before. Nothing is made
    /// twice. A rendition that exists counts as ready. Nothing is stored beyond what was asked
    /// for.</para>
    /// </summary>
    /// <returns>How many can already be served at that rate, and how many were put on the work
    /// list.</returns>
    public async Task<(int Ready, int Queued)> RequestTranscodeAsync(
        IReadOnlyList<string> rpmIds, int kilobits, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rpmIds);

        var ready = 0;
        var queued = 0;

        foreach (var chunk in rpmIds.Chunk(MostTranscodeTracks))
        {
            var said = await SendAsync<TranscodeResponse>(
                HttpMethod.Post, "cloud/transcode",
                JsonContent.Create(
                    new TranscodeRequest(chunk, kilobits), RpmCloudJson.Default.TranscodeRequest), ct);

            ready += said.Ready;
            queued += said.Queued;
        }

        return (ready, queued);
    }

    private sealed record TranscodeResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("kilobits")] int Kilobits,
        [property: System.Text.Json.Serialization.JsonPropertyName("ready")] int Ready,
        [property: System.Text.Json.Serialization.JsonPropertyName("queued")] int Queued);

    private sealed record HeadClipsResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("clips")]
        IReadOnlyList<HeadClipEntry>? Clips);

    private sealed record HeadClipEntry(
        [property: System.Text.Json.Serialization.JsonPropertyName("rpmId")] string RpmId,
        [property: System.Text.Json.Serialization.JsonPropertyName("audio")] string? Audio);

    /// <summary>
    /// Makes and uploads the head clip and the default rendition for a track that has just landed.
    ///
    /// <para>This is done here, not on the server. The machine doing this already has ffmpeg, and it
    /// already has the audio decoded and hashed. It does not pay for each second of work. The same work
    /// on the server would cost a bill for every track.</para>
    ///
    /// <para>When the blob was deduplicated onto a file that somebody else uploaded, and the renditions
    /// already exist, the work is skipped. The server says so by refusing the slot. Remaking them would
    /// re-encode the same audio to the same bytes.</para>
    /// </summary>
    /// <returns>What happened. The caller acts on it. <see cref="RenditionOutcome.Made"/> is written down
    /// (LibraryDatabase.NoteRenditions), so the track is never encoded again. <see
    /// cref="RenditionOutcome.NotInTheCloud"/> means that a row has to be put right. See there. <see
    /// cref="RenditionOutcome.Failed"/> carries the words that were said, because a caller that decides
    /// whether to ask again has nothing else to decide on.</returns>
    public async Task<RenditionResult> MakeRenditionsAsync(
        string rpmId, string path, CancellationToken ct = default)
    {
        try
        {
            await SendRenditionAsync(rpmId, path, Rendition.Head, ct);
            await SendRenditionAsync(rpmId, path, AudioLadder.FreeKilobits, ct);
            return new(RenditionOutcome.Made);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // The account has no track under this id. Retrying cannot fix that, because the id is the
            // track's name in the store. So this is said plainly, and the caller corrects the row.
            // Otherwise the same file would be encoded and would hit the same 404 on every run, for as
            // long as the app is installed.
            Console.Error.WriteLine($"[cloud] renditions for {rpmId}: the account has no such track");
            return new(RenditionOutcome.NotInTheCloud);
        }
        // Cancellation is passed on, not caught. A sync that the user stopped is not a rendition that
        // failed, and the caller has nothing to write down about it.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A track with no head clip plays exactly as before, only slower to start. That is not worth
            // failing a sync over, and the server has already noted that it wants one.
            //
            // The message is returned with the answer, not only written to stderr. Then the caller can
            // classify it (CloudFailure.Classify, MarkCloudPartial). Without the message, every ffmpeg
            // refusal looks like a dropped connection, and a damaged file stays in the catch-up queue.
            Console.Error.WriteLine($"[cloud] renditions for {rpmId}: {ex.Message}");
            return new(RenditionOutcome.Failed, ex.Message, CloudFailure.InFull(ex));
        }
    }

    /// <summary>The head clip's stand-in rate, matching the server's. It is not on the ladder, because the
    /// head clip is three seconds long, not a rendition.</summary>
    public static class Rendition
    {
        public const int Head = -1;
    }

    private async Task SendRenditionAsync(string rpmId, string path, int rate, CancellationToken ct)
    {
        var slot = await SendAsync<RenditionUploadResponse>(
            HttpMethod.Get, $"cloud/rendition/{rpmId}/upload?rate={rate}", null, ct);

        var made = rate == Rendition.Head
            ? Transcoding.Head(path)
            : Transcoding.RenditionBytes(path, rate);

        await PutBytesAsync(slot.Url, made, "audio/mp4", checksumBase64: null, ct);

        // Only the server decides that a rendition exists. It checks the object with a HEAD request before
        // it believes the upload.
        await SendAsync<object>(HttpMethod.Post, $"cloud/rendition/{rpmId}?rate={rate}", null, ct);
    }

    private sealed record RenditionUploadResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("url")] string Url,
        [property: System.Text.Json.Serialization.JsonPropertyName("expiresInSeconds")] int ExpiresInSeconds,
        [property: System.Text.Json.Serialization.JsonPropertyName("kilobits")] int Kilobits);

    /// <summary>Where to play a cloud track from. The URL is handed straight to ffmpeg, which speaks
    /// HTTP and byte ranges. So a streamed track seeks like a local one.
    /// </summary>
    /// <param name="kilobits">A rate on the ladder, or <see cref="AudioLadder.Original"/> for the
    /// untranscoded file. What comes back may be lower. A free account is capped. A rendition that has not
    /// been made yet falls back, rather than making a listener wait for a transcode.</param>
    public async Task<string> GetStreamUrlAsync(
        CloudShelf shelf, string rpmId, int kilobits = AudioLadder.Original,
        CancellationToken ct = default)
        => (await GetStreamAsync(shelf, rpmId, kilobits, ct)).Url;

    /// <summary>Where to stream from, and the rate that is actually being served.
    ///
    /// <para>The second half matters, because the served rate is not always the rate that was asked for.
    /// A free account is capped. A rendition that has not been made yet falls back, rather than making
    /// anybody wait for a transcode. Without this value, the reason for a sound quality is only a
    /// guess.</para>
    /// </summary>
    /// <param name="shelf">Whose record it is. Somebody else's record is only available where they have
    /// shared it. The grant is checked at the moment the URL is signed, not once at pairing. So a share
    /// that is taken back stops the music at the next track, not at some later time.</param>
    public async Task<(string Url, int Kilobits)> GetStreamAsync(
        CloudShelf shelf, string rpmId, int kilobits = AudioLadder.Original,
        CancellationToken ct = default)
    {
        var said = await SendAsync<UrlResponse>(
            HttpMethod.Get, $"cloud/stream/{rpmId}?rate={kilobits}{Also(shelf)}", null, ct);

        return (said.Url, said.Kilobits);
    }

    // ── library sharing: what two computers cannot settle between themselves ──

    /// <summary>
    /// Tells the account that this install exists, so the user's other computers can recognise it
    /// without asking anybody anything.
    ///
    /// <para>The id is checked against the key on the far side. A pair that does not match is refused.
    /// This is called at sign-in, and whenever the machine is renamed.</para>
    /// </summary>
    public Task RegisterDeviceAsync(
        string deviceId, string publicKey, string name, CancellationToken ct = default)
        => SendAsync<object>(HttpMethod.Post, "cloud/devices",
            JsonContent.Create(
                new DeviceRequest(deviceId, publicKey, name), RpmCloudJson.Default.DeviceRequest), ct);

    /// <summary>Every install that this account has signed in on. Automatic trust between a person's own
    /// computers depends on this list.</summary>
    public async Task<IReadOnlyList<CloudDevice>> GetDevicesAsync(CancellationToken ct = default)
        => (await SendAsync<DevicesResponse>(HttpMethod.Get, "cloud/devices", null, ct)).Devices;

    /// <summary>Forgets one install. This is used when the user signs out on it, or gets rid of the
    /// computer.</summary>
    public Task ForgetDeviceAsync(string deviceId, CancellationToken ct = default)
        => SendAsync<object>(HttpMethod.Delete, $"cloud/devices/{deviceId}", null, ct);

    /// <summary>Lets another account hear this one's cloud-stored records. This is what keeps a friend's
    /// music playing once everybody has left the network it was arranged on.
    /// </summary>
    /// <returns>False when this collection already uses all five of its share places.</returns>
    public async Task<bool> GrantShareAsync(
        string granteeId, string granteeName, CancellationToken ct = default)
    {
        try
        {
            await SendAsync<object>(HttpMethod.Post, "cloud/shares",
                JsonContent.Create(
                    new ShareRequest(granteeId, granteeName), RpmCloudJson.Default.ShareRequest), ct);
            return true;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            return false;
        }
    }

    /// <summary>Who this account shares with, and who shares with this account.</summary>
    public Task<CloudShares> GetSharesAsync(CancellationToken ct = default)
        => SendAsync<CloudShares>(HttpMethod.Get, "cloud/shares", null, ct);

    /// <summary>Sends the word timing measured for some lines of a track's lyrics
    /// (<c>PUT cloud/lyric-timing/{rpmId}</c>). The store merges them, line by line, into what it keeps for
    /// the track.</summary>
    public Task PutLyricTimingAsync(string rpmId, IReadOnlyList<LyricTimingLineBody> lines, CancellationToken ct = default)
        => SendAsync<object>(HttpMethod.Put, $"cloud/lyric-timing/{Uri.EscapeDataString(rpmId)}",
            JsonContent.Create(new LyricTimingRequest(lines), RpmCloudJson.Default.LyricTimingRequest), ct);

    /// <summary>Keeps the lyric sheet the user chose for a track, as LRC text
    /// (<c>PUT cloud/lyric-sheet/{rpmId}</c>), so the user's other devices show the same sheet. The store
    /// also forgets the word timing kept for the track, which belonged to the sheet this one replaces.</summary>
    public Task PutLyricSheetAsync(string rpmId, string lrc, CancellationToken ct = default)
        => SendAsync<object>(HttpMethod.Put, $"cloud/lyric-sheet/{Uri.EscapeDataString(rpmId)}",
            JsonContent.Create(new LyricSheetRequest(lrc), RpmCloudJson.Default.LyricSheetRequest), ct);

    /// <summary>Forgets the lyric sheet the user chose for a track (<c>DELETE cloud/lyric-sheet/{rpmId}</c>).
    /// The other devices then use the sheet LRCLIB gives.</summary>
    public Task DeleteLyricSheetAsync(string rpmId, CancellationToken ct = default)
        => SendAsync<object>(HttpMethod.Delete, $"cloud/lyric-sheet/{Uri.EscapeDataString(rpmId)}", null, ct);

    /// <summary>Takes a share back.</summary>
    public Task RevokeShareAsync(string granteeId, CancellationToken ct = default)
        => SendAsync<object>(HttpMethod.Delete, $"cloud/shares/{granteeId}", null, ct);

    /// <summary>Stores an album's cover. There is one object per album, not one per track.
    ///
    /// <para>This is kept apart from the upload steps on purpose. The sidecar records the art embedded
    /// in the file, and a restore puts that art back. This is the art that the library <em>displays</em>.
    /// For most ripped albums it is the folder's cover.jpg, and it is in no file at all. Mixing the two
    /// would restore files with pictures that they never had.
    /// </para>
    /// </summary>
    /// <returns> False when there is nothing worth sending: no bytes, or a picture so large that it is a
    /// scan rather than a sleeve.
    /// </returns>
    /// <remarks>An upload that fails throws. The error is not caught and turned into a plain false. The
    /// caller already treats a failed cover as best-effort, so catching the error would only hide the
    /// reason. An album would then sit in the cloud with no artwork, and the only sign would be a
    /// first-chance exception in a debugger.
    /// </remarks>
    public async Task<bool> UploadAlbumArtAsync(
        string albumKey, AlbumCover cover, CancellationToken ct = default)
    {
        if (cover.Bytes.Length is 0 or > AlbumArt.MaxCoverBytes)
            return false;

        // One value is used both to ask for the slot and to fill the PUT header. It is already reduced to
        // what the server will sign for. The content type is part of the presigned signature. So if these
        // two differ, S3 refuses the upload.
        var mime = AlbumArt.NormalizeMime(cover.Mime);

        var slot = await SendAsync<UrlResponse>(
            HttpMethod.Post,
            $"cloud/art/{albumKey}?contentType={Uri.EscapeDataString(mime)}",
            null, ct);

        await PutBytesAsync(slot.Url, cover.Bytes, mime, checksumBase64: null, ct);
        return true;
    }

    /// <summary>Takes an album's stored picture away, so that "this record has no cover" is something
    /// that a machine can actually say.
    ///
    /// <para>Without this, removing a sleeve here only removed it on this machine. The store still held
    /// one. Every other device read the index, saw a picture that this machine did not have, and put it
    /// back. This is best-effort. An older server that has no such route answers 404 or 405. A picture
    /// that will not go is not worth failing a sync over.</para>
    /// </summary>
    public async Task<bool> DeleteAlbumArtAsync(string albumKey, CancellationToken ct = default)
    {
        try
        {
            await SendAsync<object?>(HttpMethod.Delete, $"cloud/art/{albumKey}", null, ct);
            return true;
        }
        catch (HttpRequestException ex) when (
            ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            return false;
        }
    }

    /// <summary>A URL for an album's cover, or null when the cloud has none for it.</summary>
    /// <param name="shelf">Whose cover: this account's own, or a collection that somebody has
    /// shared.</param>
    public async Task<string?> GetAlbumArtUrlAsync(
        CloudShelf shelf, string albumKey, CancellationToken ct = default)
        => (await GetAlbumArtAsync(shelf, albumKey, ct))?.Url;

    /// <summary>Where an album's cover is, and <b>which version of it</b> is behind that URL. Null when
    /// the cloud has none for it.
    ///
    /// <para>The stamp is what a cache is keyed on. A cover lives at an address derived from the album, so
    /// replacing the picture does not change the address. A client that cached the old picture has nothing
    /// else to tell it apart from the new one.</para>
    /// </summary>
    /// <param name="shelf">Whose cover. See <see cref="GetStreamAsync"/>, which uses the same
    /// grant.</param>
    public async Task<StoredCover?> GetAlbumArtAsync(
        CloudShelf shelf, string albumKey, CancellationToken ct = default)
    {
        try
        {
            var found = await SendAsync<AlbumArtUrlResponse>(
                HttpMethod.Get, $"cloud/art/{albumKey}{Owned(shelf)}", null, ct);

            return new StoredCover(albumKey, found.UpdatedAt, found.Url);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // An album with no stored cover is normal, not a failure. SendAsync turns 403, 400 and 402 into
            // CloudRejectedException, and it lets everything else go to EnsureSuccessStatusCode. So a 404
            // arrives here as an HttpRequestException.
            return null;
        }
    }

    /// <summary>The same index, with each cover's version on it: which albums have one, and when each
    /// was last written.
    ///
    /// <para>Both parts are used. "Which albums" stops a wall from asking for covers that are not there.
    /// "When" lets a client notice that a cover it already has was replaced. The address does not change
    /// when the picture does.</para>
    /// </summary>
    /// <param name="shelf">Whose covers: this account's own, or the listing that a borrowed shelf is drawn
    /// from.</param>
    /// <remarks>Throws when the listing cannot be read. Both callers act on "not stored". The art repair
    /// uploads this machine's cover for every album that is missing from the list. The cover cache
    /// uploads a sidecar's picture as the album's cover. If a failure came back as an empty list, both
    /// would upload over covers that the store really had.</remarks>
    public async Task<IReadOnlyList<StoredCover>> GetAlbumArtCatalogAsync(
        CloudShelf shelf, CancellationToken ct = default)
    {
        var index = await SendAsync<AlbumArtIndex>(
            HttpMethod.Get, "cloud/art" + Owned(shelf), null, ct);

        // The stamped list where the server sends one, and the bare keys where it does not: an API
        // that has not been deployed yet answers with the older shape, and a client that then drew
        // no covers at all would be a worse failure than a stale one.
        return index.Albums is { Count: > 0 }
            ? [.. index.Albums.Select(a => new StoredCover(a.AlbumKey, a.UpdatedAt))]
            : [.. (index.AlbumKeys ?? []).Select(k => new StoredCover(k, 0))];
    }

    /// <summary>The owner, as part of the whole query string. This is empty for this account's own storage,
    /// which is what every call uses, except for a borrowed shelf.</summary>
    private static string Owned(CloudShelf shelf) =>
        shelf.Owner is { } owner ? "?owner=" + Uri.EscapeDataString(owner) : "";

    /// <summary>The same, appended to a query that already has something in it.</summary>
    private static string Also(CloudShelf shelf) =>
        shelf.Owner is { } owner ? "&owner=" + Uri.EscapeDataString(owner) : "";

    private sealed record AlbumArtIndex(
        [property: JsonPropertyName("albumKeys")] IReadOnlyList<string>? AlbumKeys,
        [property: JsonPropertyName("albums")] IReadOnlyList<AlbumArtIndexEntry>? Albums);

    private sealed record AlbumArtIndexEntry(
        [property: JsonPropertyName("albumKey")] string AlbumKey,
        [property: JsonPropertyName("updatedAt")] long UpdatedAt);

    private sealed record AlbumArtUrlResponse(
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("updatedAt")] long UpdatedAt);

    /// <summary>The catalogue fields, taken from the sidecar that the uploader has already built.</summary>
    private static CloudTrackTags TagsFrom(TagSidecar sidecar) => new(
        sidecar.DurationSeconds,
        (int)sidecar.TrackNumber,
        (int)sidecar.TrackCount,
        (int)sidecar.DiscNumber,
        (int)sidecar.DiscCount,
        (int)sidecar.Year,
        sidecar.Genre,
        sidecar.AlbumArtist,
        sidecar.Extension,
        sidecar.Channels,
        sidecar.SampleRate,
        sidecar.BitsPerSample,
        sidecar.BitrateKbps,
        sidecar.CodecDescription,
        sidecar.Work,
        sidecar.MovementName,
        (int)sidecar.MovementNumber,
        sidecar.Composers);

    /// <summary>Fills in the catalogue fields on rows uploaded before they existed, using their own
    /// sidecars. The work runs on the server, one page at a time. Call this until a page returns no
    /// cursor. It is safe to run twice. A row that already has the fields is skipped, so a second run
    /// costs only a query.
    /// </summary>
    /// <returns>How many rows were examined and how many were written, summed over every page.</returns>
    public async Task<(int Examined, int Updated)> BackfillCatalogAsync(CancellationToken ct = default)
    {
        int examined = 0, updated = 0;
        string? cursor = null;

        do
        {
            var page = await SendAsync<BackfillResponse>(
                HttpMethod.Post,
                $"cloud/catalog/backfill{(cursor is null ? "" : $"?cursor={Uri.EscapeDataString(cursor)}")}",
                null, ct);

            examined += page.Examined;
            updated += page.Updated;
            cursor = page.Cursor;
        }
        while (cursor is not null);

        return (examined, updated);
    }

    private sealed record BackfillResponse(
        [property: JsonPropertyName("examined")] int Examined,
        [property: JsonPropertyName("updated")] int Updated,
        [property: JsonPropertyName("cursor")] string? Cursor);

    /// <summary>Writes catalogue fields that the caller already holds onto its own rows.
    ///
    /// <para>The backfill above cannot do this. It reads sidecars, and a sidecar written before a field
    /// existed does not have that field. So a library uploaded last year can never get the format line,
    /// however often the backfill runs. The desktop read the files themselves, so it has every one of
    /// these fields in its mirror. This sends that knowledge up, without uploading a byte of audio again.
    /// </para>
    ///
    /// <para>Only the caller's own rows are written, and only rows that exist. The server writes under an
    /// existence condition, so this cannot create a row for a track that was deleted.</para>
    /// </summary>
    /// <returns>How many rows the server actually wrote.</returns>
    public async Task<int> PushTagsAsync(
        IReadOnlyList<(string RpmId, CloudTrackTags Tags)> rows, CancellationToken ct = default)
        => (await PushCatalogAsync([.. rows.Select(r => new CatalogPush(r.RpmId, Tags: r.Tags))], ct))
            .Updated;

    /// <summary>Writes what the library knows onto its own cloud rows: what a track is called, where it
    /// is filed, and what the file is.
    ///
    /// <para>This is the only thing that changes a row after its first upload. The names are why it
    /// matters. They are written once, by the upload. An upload that dedups onto an encoding that
    /// somebody already had never reaches that write at all. A retag does not change the canonical audio,
    /// so it causes no upload in the first place. Without this method, correcting a misspelled artist, or
    /// re-filing a library under a new naming template, would change nothing anywhere except on this
    /// machine.
    /// </para>
    ///
    /// <para>This is a merge at the far end. Whatever a push does not mention is not touched. A blank value
    /// means "not being changed", not "make it empty".</para>
    /// </summary>
    /// <returns>How many rows the server wrote, and the stamp each of them now carries.</returns>
    public async Task<CatalogPushResult> PushCatalogAsync(
        IReadOnlyList<CatalogPush> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return CatalogPushResult.Nothing;

        var updated = 0;
        var stamps = new Dictionary<string, long>(rows.Count, StringComparer.Ordinal);

        // A page of 100 rows keeps each request inside the Lambda time limit, however slow the table is.
        foreach (var batch in rows.Chunk(100))
        {
            var response = await SendAsync<TagsPushResponse>(HttpMethod.Post, "cloud/catalog/tags",
                JsonContent.Create(
                    new CatalogTagsRequest(batch), RpmCloudJson.Default.CatalogTagsRequest), ct);

            updated += response.Updated;

            // The stamps come back when the server sends them. A server that answers with the count alone
            // leaves the map empty. The caller then does what it did before these existed.
            foreach (var row in response.Rows ?? [])
                stamps[row.RpmId] = row.UpdatedAt;
        }

        return new CatalogPushResult(updated, stamps);
    }

    /// <summary>Writes a track's sidecar again. The sidecar is the private blob that holds the user's
    /// full set of tags, and the cover embedded in their file.
    ///
    /// <para>It is written once, by the upload, and nothing rewrites it afterwards. Retagging a file
    /// does not change its canonical audio, so no upload happens. A track edited long after it went up
    /// would therefore be restored with the tags it had on the first day. The phone's fallback artwork
    /// would also stay as the file had it. This method fixes that.
    /// </para>
    /// </summary>
    /// <returns>False when the server will not hand out a slot. This happens for a row that is not there,
    /// or for an API that predates the route. Neither is worth failing a sync over.</returns>
    public async Task<bool> RefreshSidecarAsync(
        string rpmId, TagSidecar sidecar, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sidecar);

        try
        {
            var slot = await SendAsync<UrlResponse>(
                HttpMethod.Get, $"cloud/sidecar/{rpmId}/upload", null, ct);

            await PutBytesAsync(
                slot.Url, System.Text.Encoding.UTF8.GetBytes(sidecar.ToJson()),
                "application/json", checksumBase64: null, ct);

            return true;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private sealed record TagsPushRow(
        [property: JsonPropertyName("rpmId")] string RpmId,
        [property: JsonPropertyName("updatedAt")] long UpdatedAt);

    private sealed record TagsPushResponse(
        [property: JsonPropertyName("updated")] int Updated,
        [property: JsonPropertyName("rows")] IReadOnlyList<TagsPushRow>? Rows = null);

    /// <summary>The user's tags for a track, used to put a downloaded blob back together. Null when this
    /// account keeps no sidecar under that id.
    ///
    /// <para>Null is an ordinary answer, not a rare one. Anything that draws a record this machine does
    /// not hold asks here. That includes a borrowed record. Its id comes from somebody else's computer,
    /// so it was never in this account at all. If this threw an exception instead, each tile on a shelf
    /// would cause a first-chance throw. It would also skip past the place where "this album has none" is
    /// recorded. So every scroll would ask the network again.</para>
    /// </summary>
    public async Task<TagSidecar?> GetSidecarAsync(string rpmId, CancellationToken ct = default)
    {
        UrlResponse res;
        try
        {
            res = await SendAsync<UrlResponse>(HttpMethod.Get, $"cloud/sidecar/{rpmId}", null, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound)
        {
            return null;
        }

        var json = await http.GetStringAsync(res.Url, ct);
        return TagSidecar.FromJson(json);
    }

    /// <summary>Where a download is written while it is still arriving: beside its destination. Moving
    /// the file into place at the end is atomic, so an interrupted download never leaves half a track
    /// where the library expects a whole one.
    ///
    /// <para>Both parts of the name matter. It keeps the real extension. The user's tags are written
    /// onto this file before it is moved, and TagLib chooses its reader from the extension. A partial
    /// file called "…flac.part" is not read as a FLAC, so the tag step would fail. The name also starts
    /// with a dot, so that <see cref="Scanning.AudioFileScanner"/> skips it. Without the dot, the scanner
    /// would find the half-downloaded file, because it ends in .flac, and import it as a track.
    /// </para>
    /// </summary>
    public static string PartialPathFor(string targetPath) =>
        Path.Combine(
            Path.GetDirectoryName(targetPath) ?? "",
            $".rpm-part-{Path.GetFileName(targetPath)}");

    /// <summary>Rebuilds the file at <paramref name="targetPath"/>. The shared blob is used, with this
    /// user's tags written back onto it. The audio comes back bit-identical. The tag container is
    /// rebuilt.
    /// </summary>
    public async Task RestoreAsync(string rpmId, string targetPath, CancellationToken ct = default)
    {
        var sidecar = await GetSidecarAsync(rpmId, ct)
            ?? throw new CloudRejectedException($"No sidecar in the cloud for {rpmId}.");

        var url = await GetStreamUrlAsync(CloudShelf.Own, rpmId, ct: ct);

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        var partial = PartialPathFor(targetPath);
        try
        {
            await using (var source = await http.GetStreamAsync(url, ct))
            await using (var file = File.Create(partial))
                await source.CopyToAsync(file, ct);

            sidecar.ApplyTo(partial);
            File.Move(partial, targetPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(partial); } catch { }
            throw;
        }
    }

    /// <summary>Removes the user's copy. A 404 has the same result, and it is not treated as a failure.
    /// The caller asked for the file not to be there, and it is not there. Treating a 404 as an error
    /// would leave the local row in a "still in the cloud" state that nothing could ever clear.
    /// </summary>
    public async Task DeleteAsync(string rpmId, CancellationToken ct = default)
    {
        try
        {
            await SendAsync<object?>(HttpMethod.Delete, $"cloud/file/{rpmId}", null, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    // ── the generated lists ──

    /// <summary>Sends the whole set of mood lists, replacing whatever was there.
    ///
    /// <para><b>Whole, never list by list.</b> A set is one answer. Every list in it was taken from one
    /// reading of the collection, so no two lists disagree about which records are calm. A push in pieces
    /// would let a phone read half of one set and half of another. The desktop's own lock exists to prevent
    /// that disagreement.</para>
    ///
    /// <para>There is no precondition and no conflict check, unlike a playlist. Nobody edits these lists.
    /// They are a derived answer, and the newest reading of the collection is always the right one. The
    /// only writer is a computer that has the collection in front of it.</para>
    /// </summary>
    /// <returns>When the store stamped it, in unix milliseconds, from the server's clock.</returns>
    public async Task<long> PutMoodListsAsync(MoodListsPayload cut, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cut);

        var said = await SendAsync<MoodListsPutResponse>(HttpMethod.Put, "cloud/moodlists",
            JsonContent.Create(cut, RpmCloudJson.Default.MoodListsPayload), ct);

        return said.ComputedAt;
    }

    /// <summary>The set that a computer last pushed, or null when none was ever pushed.
    ///
    /// <para>Null is an ordinary answer. A collection whose computer has not pushed a set has no generated
    /// lists to show. A phone says so, and does not treat it as a failure.</para>
    /// </summary>
    public async Task<CloudMoodLists?> MoodListsAsync(CancellationToken ct = default)
    {
        try
        {
            return await SendAsync<CloudMoodLists>(HttpMethod.Get, "cloud/moodlists", null, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // ── playlists ──

    /// <summary>Writes a playlist to the cloud, replacing the copy that the caller says it is replacing.
    /// The tracks that it names are not uploaded here. The caller uploads them first. A client that meets
    /// an id it does not have skips that line, rather than failing the playlist.
    /// </summary>
    /// <param name="ifUnchangedSince"> The stamp of the copy that this write replaces. Zero means "there
    /// should be nothing stored under this id yet". Null means write regardless of what is there.
    ///
    /// <para><b>No default, deliberately.</b> Writing regardless is the choice that loses somebody's
    /// work. For example, a playlist is read on a phone, changed, and written back over a computer's edit
    /// that landed in between. So it must never be reached by leaving an argument off. Every call site
    /// says which of the three it means.</para>
    /// </param>
    /// <returns>The moment the store stamped it, in unix MILLISECONDS, from the server's clock. It tells
    /// a later read of the same playlist apart from this write coming back. A copy stamped later than
    /// this is somebody else's change. A copy stamped with exactly this value is our own echo. A store
    /// that is too old to answer with a stamp reports zero. Zero means "cannot tell", and the caller is
    /// left where it was before the stamp existed.</returns>
    /// <exception cref="PlaylistConflictException">The store's copy is not the one being replaced.
    /// Nothing was written, and the exception carries the copy that is there instead.</exception>
    public async Task<long> PutPlaylistAsync(
        CloudPlaylist playlist, long? ifUnchangedSince, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(playlist);

        // This has its own request, not SendAsync's, for one reason. A refusal here is an answer. It
        // carries the copy that is actually stored. SendAsync turns every non-success into an exception,
        // and it throws the body away.
        var token = await accessTokenProvider(ct)
            ?? throw new InvalidOperationException("Not signed in.");

        using var req = new HttpRequestMessage(
            HttpMethod.Put, $"cloud/playlist/{playlist.PlaylistId}")
        {
            Content = JsonContent.Create(
                new PlaylistPutRequest(
                    playlist.Name, playlist.TrackIds, playlist.FolderChain, ifUnchangedSince),
                RpmCloudJson.Default.PlaylistPutRequest),
        };

        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await http.SendAsync(req, ct);

        if (resp.StatusCode == HttpStatusCode.Conflict)
        {
            var current = await resp.Content.ReadFromJsonAsync(RpmCloudJson.Default.CloudPlaylist, ct);
            throw new PlaylistConflictException(
                current ?? playlist with { UpdatedAt = 0 },
                $"\"{playlist.Name}\" was changed somewhere else while this change was being made.");
        }

        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest
                            or HttpStatusCode.PaymentRequired)
            throw new CloudRejectedException(await resp.Content.ReadAsStringAsync(ct));

        resp.EnsureSuccessStatusCode();

        var stamped = await resp.Content.ReadFromJsonAsync(RpmCloudJson.Default.PlaylistPutResponse, ct);
        return stamped?.UpdatedAt ?? 0;
    }

    private sealed record PlaylistPutResponse(
        [property: JsonPropertyName("playlistId")] string? PlaylistId,
        [property: JsonPropertyName("updatedAt")] long UpdatedAt);

    /// <summary>Removes a playlist from the cloud. The tracks it named are left alone, because they may
    /// belong to an album the user picked, or to another playlist. A 404 has the same result.</summary>
    public async Task DeletePlaylistAsync(string playlistId, CancellationToken ct = default)
    {
        try
        {
            await SendAsync<object?>(HttpMethod.Delete, $"cloud/playlist/{playlistId}", null, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    /// <summary>Every playlist this user keeps in the cloud.</summary>
    /// <param name="shelf">Whose playlists. A borrowed shelf's running orders are part of that shelf. The
    /// answer names the account it came from. An answer from a different account is refused, for the reason
    /// that <see cref="CloudShelfNotServedException"/> gives.</param>
    public async Task<IReadOnlyList<CloudPlaylist>> ListPlaylistsAsync(
        CloudShelf shelf, CancellationToken ct = default)
    {
        var answer = await SendAsync<CloudPlaylistsResponse>(
            HttpMethod.Get, "cloud/playlists" + Owned(shelf), null, ct);

        if (!shelf.IsOwn && !string.Equals(answer.Owner, shelf.OwnerId, StringComparison.Ordinal))
            throw new CloudShelfNotServedException(shelf.OwnerId);

        return answer.Playlists;
    }

    /// <summary>Stores a playlist's chosen picture. There is one object per playlist, under its own
    /// prefix beside the album art, not inside it (see <c>CloudBlobStore.PlaylistArtKey</c>). The picture
    /// is drawn on the desktop, the only head that has Skia in it. This is that picture sent over the
    /// wire as flat bytes, exactly as <c>SharingService.ArtFor</c> serves it over the LAN.
    /// </summary>
    public async Task PutPlaylistArtAsync(string playlistId, byte[] bytes, CancellationToken ct = default)
    {
        if (bytes.Length == 0)
            return;

        // The type is sniffed, not assumed. A picture choice can keep the user's own bytes unchanged.
        // PlaylistArt.Bleed leaves an image that is already square and under the size cap as it is. Or the
        // picture is re-encoded as PNG when its alpha channel is not opaque, and the picker accepts .png
        // files. So the file on disk is not always a JPEG. UploadAlbumArtAsync above also does not assume
        // one.
        var mime = AlbumArt.SniffMime(bytes);

        var slot = await SendAsync<UrlResponse>(
            HttpMethod.Post,
            $"cloud/playlist-art/{playlistId}?contentType={Uri.EscapeDataString(mime)}",
            null, ct);

        await PutBytesAsync(slot.Url, bytes, mime, checksumBase64: null, ct);
    }

    /// <summary>Takes a playlist's stored picture away. This is the counterpart of <see
    /// cref="DeleteAlbumArtAsync"/>, for the same reason. A picture lives at an address derived from the
    /// playlist id. So going back to the automatic cover cannot be said by writing something else.
    /// Without this call, the last chosen picture keeps being served as current, on every other head, for
    /// as long as the account exists. This is best-effort, like the album route. From the caller's point
    /// of view, a 404 or 405 from an older server has the same result as a successful delete.
    /// </summary>
    public async Task<bool> DeletePlaylistArtAsync(string playlistId, CancellationToken ct = default)
    {
        try
        {
            await SendAsync<object?>(HttpMethod.Delete, $"cloud/playlist-art/{playlistId}", null, ct);
            return true;
        }
        catch (HttpRequestException ex) when (
            ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            return false;
        }
    }

    /// <summary>When a playlist's stored picture was written, in Unix milliseconds, or null when the
    /// cloud has none for it. The push asks this to replace pictures that were stored under an older
    /// rule.</summary>
    public async Task<long?> GetPlaylistArtStoredAtAsync(
        CloudShelf shelf, string playlistId, CancellationToken ct = default)
    {
        try
        {
            var found = await SendAsync<StoredAtResponse>(
                HttpMethod.Get, $"cloud/playlist-art/{playlistId}{Owned(shelf)}", null, ct);
            return found.UpdatedAt;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private sealed record StoredAtResponse([property: JsonPropertyName("updatedAt")] long UpdatedAt);

    /// <summary>A URL for a playlist's stored picture, or null when the cloud has none for it. Null is the
    /// ordinary answer for the automatic cover, which pushes nothing up.</summary>
    /// <param name="shelf">Whose picture: this account's own, or a collection that somebody has shared.
    /// This uses the same grant as <see cref="GetAlbumArtUrlAsync"/>.</param>
    public async Task<string?> GetPlaylistArtUrlAsync(
        CloudShelf shelf, string playlistId, CancellationToken ct = default)
    {
        try
        {
            var found = await SendAsync<UrlResponse>(
                HttpMethod.Get, $"cloud/playlist-art/{playlistId}{Owned(shelf)}", null, ct);
            return found.Url;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // ── the marks the user has put on whole records ──
    //
    // A favourite album is a statement about the whole record. So it is stored once for the record, not
    // spread over its tracks. That is one write per click, and one answer to read back. The track marks
    // are kept on the catalogue rows instead, because those rows already exist and are already listed in
    // one call.

    /// <summary>Marks a record as a favourite, or clears the mark. Answers with the stamp that the store
    /// wrote it under, from the store's own clock. This machine records that stamp as agreed.</summary>
    public async Task<long> PutAlbumMarkAsync(
        string albumKey, bool favorite, long favoritedUtcTicks, CancellationToken ct = default)
        => (await SendAsync<CloudAlbumMarkResponse>(
                HttpMethod.Put, $"cloud/album-mark/{albumKey}",
                JsonContent.Create(
                    new AlbumMarkRequest(favorite, favorite ? favoritedUtcTicks : 0),
                    RpmCloudJson.Default.AlbumMarkRequest), ct)).UpdatedAt;

    /// <summary>Marks a record as classical, or as not classical. Answers with the store's stamp, as <see
    /// cref="PutAlbumMarkAsync"/> does. A server that is older than this route answers 404. That throws,
    /// and it leaves the mark outstanding for the next pass.</summary>
    public async Task<long> PutAlbumClassicalAsync(
        string albumKey, bool classical, CancellationToken ct = default)
        => (await SendAsync<CloudAlbumMarkResponse>(
                HttpMethod.Put, $"cloud/album-mark/{albumKey}/classical",
                JsonContent.Create(
                    new AlbumClassicalRequest(classical),
                    RpmCloudJson.Default.AlbumClassicalRequest), ct)).UpdatedAt;

    /// <summary>Every record that this user has marked, in one call. A client with two thousand albums must
    /// not ask two thousand times which eleven of them are favourites.</summary>
    public async Task<IReadOnlyList<CloudAlbumMark>> ListAlbumMarksAsync(CancellationToken ct = default)
        => (await SendAsync<CloudAlbumMarksResponse>(
                HttpMethod.Get, "cloud/album-marks", null, ct)).Albums;

    // ── what this listener has played ──
    //
    // One row per track per device. This machine writes only its own rows, and it writes totals, not
    // counts to add on. See RPM.Lambda.Api.Services.TrackPlayStore for why a re-send has to be harmless.
    // A send that lands, but whose answer is lost, is simply made again on the next pass.

    /// <summary>Sends this machine's play totals for these tracks. Answers with the stamp that the store
    /// wrote them under. This machine records that stamp as agreed.</summary>
    public async Task<long> PutTrackPlaysAsync(
        string deviceId,
        IReadOnlyList<(string RpmId, int Plays, long LastPlayedAt)> rows,
        CancellationToken ct = default)
        => (await SendAsync<TrackPlaysResponse>(
                HttpMethod.Put, "cloud/track-plays",
                JsonContent.Create(
                    new TrackPlaysRequest(
                        deviceId,
                        [.. rows.Select(r => new TrackPlaysEntry(r.RpmId, r.Plays, r.LastPlayedAt))]),
                    RpmCloudJson.Default.TrackPlaysRequest), ct)).UpdatedAt;

    /// <summary>Every play row that this listener has on any device, or only the rows that the store has
    /// changed since a stamp that this machine has already taken in.</summary>
    public async Task<IReadOnlyList<CloudTrackPlays>> ListTrackPlaysAsync(
        long since, CancellationToken ct = default)
        => (await SendAsync<TrackPlaysListResponse>(
                HttpMethod.Get, $"cloud/track-plays?since={since}", null, ct)).Tracks;

    // ── what this listener has heard on the radio ──
    //
    // The same shape as the play counts: one row per station per device. This machine writes only its own
    // rows, as totals. See RPM.Lambda.Api.Services.StationListenStore.

    /// <summary>Sends this log's radio totals for these stations. Answers with the stamp that the store
    /// wrote them under.</summary>
    public async Task<long> PutStationListensAsync(
        string deviceId,
        IReadOnlyList<Radio.RadioListenRow> rows,
        CancellationToken ct = default)
        => (await SendAsync<StationListensResponse>(
                HttpMethod.Put, "cloud/station-listens",
                JsonContent.Create(
                    new StationListensRequest(
                        deviceId,
                        [.. rows.Select(r => new StationListensEntry(
                            r.Station.Uuid, r.HeardSeconds, r.Tunes, r.LastHeardAt,
                            CloudListenedStation.Of(r.Station)))]),
                    RpmCloudJson.Default.StationListensRequest), ct)).UpdatedAt;

    /// <summary>Every station row that this listener has on any device, or only the rows that the store has
    /// changed since a stamp that this log has already taken in.</summary>
    public async Task<IReadOnlyList<CloudStationListens>> ListStationListensAsync(
        long since, CancellationToken ct = default)
        => (await SendAsync<StationListensListResponse>(
                HttpMethod.Get, $"cloud/station-listens?since={since}", null, ct)).Stations;

    // ── the saved radio stations ──
    //
    // One row per station per account, holding the newest change to it from any device. See
    // RPM.Lambda.Api.Services.StationFavoriteStore and RPM.Core.Radio.FavoriteLedger.

    /// <summary>Sends this computer's changes to the saved stations. The store keeps each change where it
    /// is newer than what it holds. Answers with the stamp that it wrote them under.</summary>
    public async Task<long> PutStationFavoritesAsync(
        IReadOnlyCollection<Radio.FavoriteChange> changes,
        CancellationToken ct = default)
        => (await SendAsync<StationFavoritesResponse>(
                HttpMethod.Put, "cloud/station-favorites",
                JsonContent.Create(
                    new StationFavoritesRequest(
                        [.. changes.Select(c => new StationFavoriteEntry(
                            c.Station.Uuid, c.Saved, c.ChangedAt, CloudListenedStation.Of(c.Station)))]),
                    RpmCloudJson.Default.StationFavoritesRequest), ct)).UpdatedAt;

    /// <summary>Every change to the saved stations that the store has touched since a stamp that this
    /// computer has already taken in. Unsaves are included.</summary>
    public async Task<IReadOnlyList<Radio.FavoriteRow>> ListStationFavoritesAsync(
        long since, CancellationToken ct = default)
        => [.. (await SendAsync<StationFavoritesListResponse>(
                    HttpMethod.Get, $"cloud/station-favorites?since={since}", null, ct)).Stations
               .Select(r => new Radio.FavoriteRow(r.Uuid, r.Saved, r.ChangedAt, r.Station?.ToStation(), r.UpdatedAt))];

    /// <summary>Everything one collection has in the cloud, a page at a time.</summary>
    /// <param name="shelf">Whose collection. Somebody else's collection is only available where they have
    /// shared it. The grant is checked on every page. So a share that is taken back empties the shelf, and
    /// it does not leave a listing behind that no longer plays.</param>
    /// <param name="read">Told the running row count after every page. This is for a caller with a screen
    /// that shows progress. A library takes at most about twenty sequential round trips. The only
    /// progress that can be shown is how many rows have arrived.</param>
    public async Task<IReadOnlyList<CloudTrack>> ListAsync(
        CloudShelf shelf, CancellationToken ct = default, IProgress<int>? read = null)
    {
        var all = new List<CloudTrack>();
        string? cursor = null;

        do
        {
            // Asked for in the largest pages the API will serve. The server limits this, and DynamoDB
            // caps a page at a megabyte in any case. So a smaller answer is normal. The cursor decides
            // whether to ask again. The page size changes only how many sequential round trips a whole
            // library costs. An older API that still limits pages to 500 returns 500.
            var page = await SendAsync<CloudListResponse>(
                HttpMethod.Get,
                $"cloud/list?limit=2000{Also(shelf)}" +
                (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}"),
                null, ct);

            // Checked before a single row is kept: whose rows these are. See
            // CloudShelfNotServedException. A server that does not know about shared collections answers
            // a borrowed listing with the caller's own rows and a 200 status. Every symptom after that is
            // a client that quietly works on the wrong library.
            if (!shelf.IsOwn && !string.Equals(page.Owner, shelf.OwnerId, StringComparison.Ordinal))
                throw new CloudShelfNotServedException(shelf.OwnerId);

            all.AddRange(page.Files);
            cursor = page.Cursor;
            read?.Report(all.Count);
        }
        while (cursor is not null);

        return all;
    }

    /// <summary>Streams the file to S3 rather than reading it into memory. A lossless album track is
    /// tens of megabytes, and a library holds thousands of them.
    /// </summary>
    /// <remarks>Content-Length is set explicitly. Without a known length, .NET falls back to chunked transfer
    /// encoding, which macOS's SecureTransport handles badly (a Win32Exception 14 from SslStream). A
    /// FileStream has a length, so it can be streamed, but only if the length is stated.
    /// </remarks>
    private async Task PutFileAsync(string url, string path, string? checksumBase64, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);

        using var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = new StreamContent(file) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        req.Content.Headers.ContentLength = file.Length;

        // Part of the presigned signature. S3 checks the body against it and rejects a mismatch. So wrong
        // bytes can never land under a hash that other people will de-duplicate onto.
        if (checksumBase64 is not null)
            req.Headers.TryAddWithoutValidation("x-amz-checksum-sha256", checksumBase64);

        using var resp = await http.SendAsync(req, ct);
        await EnsureUploadedAsync(resp, ct);
    }

    private async Task PutBytesAsync(
        string url, byte[] bytes, string contentType, string? checksumBase64, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(bytes) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        req.Content.Headers.ContentLength = bytes.LongLength;

        if (checksumBase64 is not null)
            req.Headers.TryAddWithoutValidation("x-amz-checksum-sha256", checksumBase64);

        using var resp = await http.SendAsync(req, ct);
        await EnsureUploadedAsync(resp, ct);
    }

    private static async Task EnsureUploadedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;

        // S3 answers in XML. Its message is the only thing that tells a signature problem apart from a
        // checksum problem.
        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new CloudRejectedException($"S3 refused the upload ({(int)resp.StatusCode}): {body}");
    }

    /// <summary>One call to the API. The token is sent with it, the body is already written, and the
    /// answer is read back.
    /// </summary>
    /// <param name="payload">The request body, or null where the route takes none. The caller builds it
    /// with JsonContent.Create and a shape from <see cref="RpmCloudJson"/>. It is not handed over as an
    /// object. A published build is Native AOT, and an anonymous object has no metadata to serialize
    /// from.</param>
    private async Task<T> SendAsync<T>(
        HttpMethod method, string path, HttpContent? payload, CancellationToken ct)
    {
        var token = await accessTokenProvider(ct)
            ?? throw new InvalidOperationException("Not signed in.");

        using var req = new HttpRequestMessage(method, path);
        if (payload is not null) req.Content = payload;
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await http.SendAsync(req, ct);

        if (resp.StatusCode == HttpStatusCode.RequestEntityTooLarge)
        {
            var quota = await resp.Content.ReadFromJsonAsync(RpmCloudJson.Default.CloudQuota, ct);
            throw new CloudQuotaExceededException(quota?.UsedBytes ?? 0, quota?.QuotaBytes ?? 0);
        }

        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest
                            or HttpStatusCode.PaymentRequired)
            throw new CloudRejectedException(await resp.Content.ReadAsStringAsync(ct));

        resp.EnsureSuccessStatusCode();

        if (typeof(T) == typeof(object)) return default!;
        return await resp.Content.ReadFromJsonAsync(Answer<T>(), ct)
            ?? throw new InvalidOperationException($"Empty response from {path}.");
    }

    // ── request shapes ────────────────────────────────────────────────
    //
    // These are named types, not anonymous objects. A published build is Native AOT. It serializes from
    // metadata that is written at build time, and it cannot describe an anonymous type at all. The
    // property names are the API's. They come from the camelCase policy on RpmCloudJson below, so renaming
    // a property here renames a field on the wire.

    private sealed record LyricTimingRequest(IReadOnlyList<LyricTimingLineBody> Lines);

    private sealed record LyricSheetRequest(string Lrc);

    private sealed record KeyBundle(IReadOnlyList<string> Keys);

    private sealed record KeyBundlesRequest(IReadOnlyList<KeyBundle> Bundles);

    private sealed record FactsReportRequest(
        IReadOnlyList<string> Keys, IReadOnlyList<RecordingFactBody> Facts);

    private sealed record RpmIdsRequest(IReadOnlyList<string> RpmIds);

    private sealed record GridWindowRequest(int Frames, int Bands, string Mel, string Precision);

    private sealed record UploadInitRequest(
        string RpmId,
        string Sha256,
        long CanonicalSize,
        long OriginalSize,
        string Extension,
        string? Title,
        string? Artist,
        string? Album,
        string? RelativePath);

    private sealed record ProveRequest(string ChallengeId, IReadOnlyList<string> Answers);

    private sealed record UploadCompleteRequest(
        string RpmId,
        string Sha256,
        string? RelativePath,
        string? Title,
        string? Artist,
        string? Album,
        CloudTrackTags Tags);

    private sealed record TranscodeRequest(IReadOnlyList<string> RpmIds, int Kilobits);

    private sealed record DeviceRequest(string DeviceId, string PublicKey, string Name);

    private sealed record ShareRequest(string GranteeId, string GranteeName);

    private sealed record CatalogTagsRequest(IReadOnlyList<CatalogPush> Items);

    private sealed record PlaylistPutRequest(
        string Name,
        IReadOnlyList<string> TrackIds,
        IReadOnlyList<string> Folder,
        long? IfUnchangedSince);

    private sealed record AlbumMarkRequest(bool Favorite, long FavoritedAt);

    private sealed record AlbumClassicalRequest(bool Classical);

    private sealed record TrackPlaysEntry(string RpmId, long Plays, long LastPlayedAt);

    private sealed record TrackPlaysRequest(string DeviceId, IReadOnlyList<TrackPlaysEntry> Tracks);

    private sealed record StationListensEntry(
        string Uuid, double HeardSeconds, int Tunes, long LastHeardAt, CloudListenedStation Station);

    private sealed record StationListensRequest(string DeviceId, IReadOnlyList<StationListensEntry> Stations);

    private sealed record StationFavoriteEntry(
        string Uuid, bool Saved, long ChangedAt, CloudListenedStation Station);

    private sealed record StationFavoritesRequest(IReadOnlyList<StationFavoriteEntry> Stations);

    /// <summary>The shape of the answer, from the context below.
    /// </summary>
    /// <remarks>The shape is looked up here, rather than passed in at each of the three dozen call
    /// sites. The type argument is already there, and naming it twice is what goes stale. The cost is
    /// that a response type missing from <see cref="RpmCloudJson"/> is found when that route is first
    /// called, rather than at compile time. RpmCloudClientJsonTests covers this. It walks the call sites
    /// and fails the build instead.
    /// </remarks>
    private static JsonTypeInfo<T> Answer<T>() =>
        RpmCloudJson.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new InvalidOperationException(
            $"{typeof(T).Name} is not listed in {nameof(RpmCloudJson)}, so it cannot be read.");

    /// <summary>Every shape this client puts on the wire or reads back, as the source generator
    /// describes them.
    /// </summary>
    /// <remarks>camelCase is used because that is what the API speaks. The response records name their
    /// fields explicitly, so they are not affected by it. The request records above rely on it.</remarks>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(KeyBundlesRequest))]
    [JsonSerializable(typeof(FactsReportRequest))]
    [JsonSerializable(typeof(RpmIdsRequest))]
    [JsonSerializable(typeof(GridWindowRequest))]
    [JsonSerializable(typeof(UploadInitRequest))]
    [JsonSerializable(typeof(ProveRequest))]
    [JsonSerializable(typeof(UploadCompleteRequest))]
    [JsonSerializable(typeof(TranscodeRequest))]
    [JsonSerializable(typeof(DeviceRequest))]
    [JsonSerializable(typeof(ShareRequest))]
    [JsonSerializable(typeof(CatalogTagsRequest))]
    [JsonSerializable(typeof(PlaylistPutRequest))]
    [JsonSerializable(typeof(AlbumMarkRequest))]
    [JsonSerializable(typeof(LyricTimingRequest))]
    [JsonSerializable(typeof(LyricSheetRequest))]
    [JsonSerializable(typeof(AlbumClassicalRequest))]
    [JsonSerializable(typeof(ErrorReport))]
    [JsonSerializable(typeof(MoodListsPayload))]
    [JsonSerializable(typeof(CloudQuota))]
    [JsonSerializable(typeof(RecordingKeysResponse))]
    [JsonSerializable(typeof(RecordingLookUpResponse))]
    [JsonSerializable(typeof(RecordingReportResponse))]
    [JsonSerializable(typeof(AnalyseResponse))]
    [JsonSerializable(typeof(GridWindowResponse))]
    [JsonSerializable(typeof(ErrorReportResponse))]
    [JsonSerializable(typeof(UploadInitResponse))]
    [JsonSerializable(typeof(ProveResponse))]
    [JsonSerializable(typeof(HeadClipsResponse))]
    [JsonSerializable(typeof(VerifyResponse))]
    [JsonSerializable(typeof(TranscodeResponse))]
    [JsonSerializable(typeof(RenditionUploadResponse))]
    [JsonSerializable(typeof(UrlResponse))]
    [JsonSerializable(typeof(DevicesResponse))]
    [JsonSerializable(typeof(CloudShares))]
    [JsonSerializable(typeof(AlbumArtUrlResponse))]
    [JsonSerializable(typeof(AlbumArtIndex))]
    [JsonSerializable(typeof(BackfillResponse))]
    [JsonSerializable(typeof(TagsPushResponse))]
    [JsonSerializable(typeof(MoodListsPutResponse))]
    [JsonSerializable(typeof(CloudMoodLists))]
    [JsonSerializable(typeof(CloudPlaylistsResponse))]
    [JsonSerializable(typeof(CloudAlbumMarkResponse))]
    [JsonSerializable(typeof(CloudAlbumMarksResponse))]
    [JsonSerializable(typeof(TrackPlaysRequest))]
    [JsonSerializable(typeof(TrackPlaysResponse))]
    [JsonSerializable(typeof(TrackPlaysListResponse))]
    [JsonSerializable(typeof(CloudTrackPlays))]
    [JsonSerializable(typeof(StationListensRequest))]
    [JsonSerializable(typeof(StationListensResponse))]
    [JsonSerializable(typeof(StationListensListResponse))]
    [JsonSerializable(typeof(CloudStationListens))]
    [JsonSerializable(typeof(StationFavoritesRequest))]
    [JsonSerializable(typeof(StationFavoritesResponse))]
    [JsonSerializable(typeof(StationFavoritesListResponse))]
    [JsonSerializable(typeof(CloudListResponse))]
    [JsonSerializable(typeof(CloudPlaylist))]
    [JsonSerializable(typeof(PlaylistPutResponse))]
    [JsonSerializable(typeof(StoredAtResponse))]
    private sealed partial class RpmCloudJson : JsonSerializerContext;

    // ── wire shapes (mirrors RPM.Lambda.Api/Models/CloudContracts.cs) ──

    private sealed record UploadInitResponse(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("sidecarUrl")] string SidecarUrl,
        [property: JsonPropertyName("blobUrl")] string? BlobUrl,
        [property: JsonPropertyName("checksumSha256")] string? ChecksumSha256,
        [property: JsonPropertyName("challengeId")] string? ChallengeId,
        [property: JsonPropertyName("nonce")] string? Nonce,
        [property: JsonPropertyName("ranges")] IReadOnlyList<Range>? Ranges);

    private sealed record Range(
        [property: JsonPropertyName("offset")] long Offset,
        [property: JsonPropertyName("length")] int Length);

    private sealed record ProveResponse([property: JsonPropertyName("status")] string Status);

    private sealed record DevicesResponse(
        [property: JsonPropertyName("devices")] IReadOnlyList<CloudDevice> Devices);

    private sealed record UrlResponse(
        [property: JsonPropertyName("url")] string Url,
        /// <summary>The rate actually being served, or 0 for the original. This is not always the rate
        /// that was asked for. See the stream endpoint.</summary>
        [property: JsonPropertyName("kilobits")] int Kilobits = 0);

    private sealed record CloudListResponse(
        [property: JsonPropertyName("files")] IReadOnlyList<CloudTrack> Files,
        [property: JsonPropertyName("cursor")] string? Cursor,
        [property: JsonPropertyName("owner")] string? Owner = null);

    private sealed record CloudPlaylistsResponse(
        [property: JsonPropertyName("playlists")] IReadOnlyList<CloudPlaylist> Playlists,
        [property: JsonPropertyName("owner")] string? Owner = null);

    private sealed record CloudAlbumMarksResponse(
        [property: JsonPropertyName("albums")] IReadOnlyList<CloudAlbumMark> Albums);

    private sealed record CloudAlbumMarkResponse(
        [property: JsonPropertyName("updatedAt")] long UpdatedAt);

    private sealed record TrackPlaysResponse(
        [property: JsonPropertyName("updatedAt")] long UpdatedAt);

    private sealed record TrackPlaysListResponse(
        [property: JsonPropertyName("tracks")] IReadOnlyList<CloudTrackPlays> Tracks);

    private sealed record StationListensResponse(
        [property: JsonPropertyName("updatedAt")] long UpdatedAt);

    private sealed record StationListensListResponse(
        [property: JsonPropertyName("stations")] IReadOnlyList<CloudStationListens> Stations);

    private sealed record StationFavoritesResponse(
        [property: JsonPropertyName("updatedAt")] long UpdatedAt);

    private sealed record StationFavoritesListResponse(
        [property: JsonPropertyName("stations")] IReadOnlyList<CloudStationFavorite> Stations);

    /// <summary>The newest change to one saved station, as the store holds it.</summary>
    private sealed record CloudStationFavorite(
        [property: JsonPropertyName("uuid")] string Uuid,
        [property: JsonPropertyName("saved")] bool Saved,
        [property: JsonPropertyName("changedAt")] long ChangedAt,
        [property: JsonPropertyName("station")] CloudListenedStation? Station,
        [property: JsonPropertyName("updatedAt")] long UpdatedAt);
}

/// <summary>The time span of one word, in seconds from the start of the track.</summary>
public sealed record LyricTimingWordBody(
    [property: JsonPropertyName("from")] double From,
    [property: JsonPropertyName("to")] double To);

/// <summary>One measured lyric line: its position in the lyrics, the time and text there, and its words in
/// reading order.</summary>
public sealed record LyricTimingLineBody(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("seconds")] double Seconds,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("words")] IReadOnlyList<LyricTimingWordBody> Words);

/// <summary>One record's mark as the store holds it.</summary>
/// <param name="UpdatedAt">The store's own stamp (unix seconds). It is compared with the value that this
/// machine last accepted. Both are server clocks, so which mark is newer never depends on two computers
/// agreeing about the time. The stamp moves when either mark is written.</param>
/// <param name="Classical">Whether the record is classical. Null when no machine has said so, and on a
/// server that is older than this field.</param>
public sealed record CloudAlbumMark(
    [property: JsonPropertyName("albumKey")] string AlbumKey,
    [property: JsonPropertyName("favorite")] bool Favorite,
    [property: JsonPropertyName("favoritedAt")] long FavoritedAt,
    [property: JsonPropertyName("updatedAt")] long UpdatedAt,
    [property: JsonPropertyName("classical")] bool? Classical = null);

/// <summary>One device's radio totals for one station, as the store holds them.</summary>
/// <param name="DeviceId">Which log heard it. This log's own rows also come back in the listing. This is
/// how they are told apart, so they can be left alone.</param>
/// <param name="HeardSeconds">That device's own total, not everybody's. A station's total is the sum of its
/// rows.</param>
/// <param name="LastHeardAt">That device's own clock, in unix seconds.</param>
/// <param name="UpdatedAt">The store's own stamp. It is kept as this log's cursor.</param>
public sealed record CloudStationListens(
    [property: JsonPropertyName("uuid")] string Uuid,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("heardSeconds")] double HeardSeconds,
    [property: JsonPropertyName("tunes")] int Tunes,
    [property: JsonPropertyName("lastHeardAt")] long LastHeardAt,
    [property: JsonPropertyName("station")] CloudListenedStation? Station,
    [property: JsonPropertyName("updatedAt")] long UpdatedAt);

/// <summary>A station as the store carries it: the directory's own fields. That is enough to draw it, and
/// to play it on a device that has never tuned it.</summary>
public sealed record CloudListenedStation(
    [property: JsonPropertyName("uuid")] string Uuid,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("streamUrl")] string StreamUrl,
    [property: JsonPropertyName("homepage")] string? Homepage,
    [property: JsonPropertyName("favicon")] string? Favicon,
    [property: JsonPropertyName("country")] string? Country,
    [property: JsonPropertyName("countryCode")] string? CountryCode,
    [property: JsonPropertyName("language")] string? Language,
    [property: JsonPropertyName("tags")] IReadOnlyList<string>? Tags,
    [property: JsonPropertyName("codec")] string? Codec,
    [property: JsonPropertyName("bitrateKbps")] int BitrateKbps,
    [property: JsonPropertyName("isHls")] bool IsHls)
{
    public static CloudListenedStation Of(Radio.RadioStation station) => new(
        station.Uuid, station.Name, station.StreamUrl, station.Homepage, station.Favicon,
        station.Country, station.CountryCode, station.Language, station.Tags, station.Codec,
        station.BitrateKbps, station.IsHls);

    /// <summary>The station, or null where the row names nothing that can be played.</summary>
    public Radio.RadioStation? ToStation() =>
        string.IsNullOrWhiteSpace(Uuid) || string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(StreamUrl)
            ? null
            : new Radio.RadioStation
            {
                Uuid = Uuid,
                Name = Name,
                StreamUrl = StreamUrl,
                Homepage = Homepage,
                Favicon = Favicon,
                Country = Country,
                CountryCode = CountryCode,
                Language = Language,
                Tags = Tags ?? [],
                Codec = Codec,
                BitrateKbps = BitrateKbps,
                IsHls = IsHls,
            };
}

/// <summary>One device's play total for one track, as the store holds it.</summary>
/// <param name="DeviceId">Which device played them. This machine's own rows also come back in the listing.
/// This is how they are told apart from the other devices' rows, so they can be left alone.</param>
/// <param name="Plays">That device's own total, not everybody's. A track's total is the sum of its
/// rows.</param>
/// <param name="LastPlayedAt">That device's own clock, in unix seconds.</param>
/// <param name="UpdatedAt">The store's own stamp. It is compared with the value that this machine last
/// accepted. So which row is newer never depends on two computers agreeing about the time.</param>
public sealed record CloudTrackPlays(
    [property: JsonPropertyName("rpmId")] string RpmId,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("plays")] long Plays,
    [property: JsonPropertyName("lastPlayedAt")] long LastPlayedAt,
    [property: JsonPropertyName("updatedAt")] long UpdatedAt);
