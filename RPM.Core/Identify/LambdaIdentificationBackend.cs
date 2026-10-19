using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using RPM.Core.Net;

namespace RPM.Core.Identify;

/// <summary>
/// Production backend: the RPM Lambda. The Lambda resolves the fingerprint through its own DynamoDB
/// fingerprint store first, then free AcoustID, then AudD (which consumes supporters only). It stores
/// every advanced hit back into its own database. The request and response shapes below are the
/// Lambda's wire format.
/// </summary>
public sealed partial class LambdaIdentificationBackend(
    HttpClient http,
    Func<CancellationToken, Task<string?>> accessTokenProvider) : IClipIdentificationBackend, ISegmentIdentificationBackend
{
    /// <summary>Thrown when the AudD layer is needed and the account does not support rpm.</summary>
    public sealed class SupportersOnlyException(string message) : InvalidOperationException(message);

    /// <summary>
    /// The service could not look the track up at all. It is missing a key, or the layer it depends
    /// on is down. This is deliberately distinct from "no match": a track that cannot be looked up has
    /// not been ruled out, and telling the user it was not found would be a lie they cannot act on.
    /// </summary>
    public sealed class IdentificationUnavailableException(string message) : InvalidOperationException(message);

    private sealed record IdentifyRequest(
        [property: JsonPropertyName("fingerprint")] string Fingerprint,
        [property: JsonPropertyName("rawFingerprint")] long[]? RawFingerprint,
        [property: JsonPropertyName("durationSeconds")] double DurationSeconds);

    private sealed record IdentifyClipRequest(
        [property: JsonPropertyName("clipBase64")] string ClipBase64,
        [property: JsonPropertyName("rawFingerprint")] long[]? RawFingerprint,
        [property: JsonPropertyName("durationSeconds")] double DurationSeconds);

    /// <summary>One library track's fingerprint, offered with the tags the user files it under.
    /// The server treats it as a report ("this fingerprint is this song") and keeps count of
    /// how many reports agree.</summary>
    public sealed record LibraryFingerprintReport(
        string Title,
        string Artist,
        string? Album,
        uint Year,
        double DurationSeconds,
        uint[] RawFingerprint);

    private sealed record SubmitFingerprintItem(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("artist")] string Artist,
        [property: JsonPropertyName("album")] string? Album,
        [property: JsonPropertyName("year")] uint Year,
        [property: JsonPropertyName("durationSeconds")] double DurationSeconds,
        [property: JsonPropertyName("rawFingerprint")] long[] RawFingerprint);

    private sealed record SubmitFingerprintsRequest(
        [property: JsonPropertyName("items")] IReadOnlyList<SubmitFingerprintItem> Items);

    private sealed record IdentifyResponse(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("source")] string? Source,
        [property: JsonPropertyName("track")] IdentifiedTrack? Track);

    private sealed record WireRelease(
        [property: JsonPropertyName("releaseId")] string ReleaseId,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("trackNo")] uint TrackNo,
        [property: JsonPropertyName("trackCount")] uint TrackCount);

    private sealed record IdentifiedTrack(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("artist")] string Artist,
        [property: JsonPropertyName("album")] string? Album,
        [property: JsonPropertyName("year")] uint Year,
        [property: JsonPropertyName("score")] double Score,
        [property: JsonPropertyName("mbRecordingId")] string? MbRecordingId,
        [property: JsonPropertyName("releases")] IReadOnlyList<WireRelease>? Releases = null);

    public async Task<IdentificationResult?> IdentifyAsync(FingerprintQuery query, CancellationToken ct = default)
    {
        var raw = ToSigned(query.RawFingerprint);
        var body = await PostAsync("identify",
            new IdentifyRequest(query.CompressedFingerprint, raw, query.DurationSeconds),
            IdentifyJson.Default.IdentifyRequest, ct);
        return ToResult(body);
    }

    /// <summary>
    /// The advanced layer. Nothing is metered here. The server either allows the lookup because the
    /// account supports rpm, or refuses it. See <see cref="SupportersOnlyException"/>.
    /// </summary>
    public async Task<ClipAnswer> IdentifyClipAsync(FingerprintQuery query, byte[] clipMp3, CancellationToken ct = default)
    {
        var raw = ToSigned(query.RawFingerprint);
        var body = await PostAsync("identify-clip",
            new IdentifyClipRequest(Convert.ToBase64String(clipMp3), raw, query.DurationSeconds),
            IdentifyJson.Default.IdentifyClipRequest, ct);
        return ToClipAnswer(body);
    }

    /// <summary>
    /// Who answered the clip. The endpoint answers 200 in exactly three ways: a match from the rpm
    /// database (source "own-db", AudD not asked), a match from AudD (source "audd"), and "unknown"
    /// with source "audd" after AudD did not know it. Every refusal and failure is an error status,
    /// which <see cref="PostAsync"/> throws on. So an answer that is not a match is an AudD miss.
    /// </summary>
    private static ClipAnswer ToClipAnswer(IdentifyResponse body) => ToResult(body) switch
    {
        { Source: "audd" } match => new ClipAnswer.AudDIdentified(match),
        { } match => new ClipAnswer.FromOwnDb(match),
        null => new ClipAnswer.AudDNotIdentified(),
    };

    /// <summary>Segment lookup for album-file splitting: the same `/identify` call. The Lambda's
    /// AcoustID layer returns release and track-position candidates. This is an additive wire field.
    /// Own-DB hits carry none and just contribute a title.</summary>
    public async Task<SegmentIdentification?> IdentifySegmentAsync(FingerprintQuery query, CancellationToken ct = default)
    {
        var raw = ToSigned(query.RawFingerprint);
        var body = await PostAsync("identify",
            new IdentifyRequest(query.CompressedFingerprint, raw, query.DurationSeconds),
            IdentifyJson.Default.IdentifyRequest, ct);
        if (body.Status != "matched" || body.Track is null)
            return null;

        return new SegmentIdentification(
            body.Track.MbRecordingId,
            body.Track.Title,
            body.Track.Artist,
            body.Track.Score,
            body.Track.Releases?.Select(r => new ReleaseRef(r.ReleaseId, r.Title, r.TrackNo, r.TrackCount)).ToList()
                ?? (IReadOnlyList<ReleaseRef>)Array.Empty<ReleaseRef>());
    }

    /// <summary>
    /// Offers a batch of library fingerprints to the service. All-or-nothing on the wire: a failed
    /// call leaves every report unsubmitted, and the caller simply tries again on a later pass.
    /// </summary>
    /// <returns>
    /// What the service calls each of these recordings, in the order they were sent. Zero where it
    /// could not say.
    ///
    /// <para>The id is kept, not just counted. It is the only name two independent rips of one CD can
    /// share. So it is the only way that a second computer which meets a record can find what the first
    /// one worked out about it. See <see cref="RPM.Fingerprint.RecordingKey.Song"/>.</para>
    /// </returns>
    public async Task<IReadOnlyList<long>> SubmitFingerprintsAsync(
        IReadOnlyList<LibraryFingerprintReport> reports, CancellationToken ct = default)
    {
        if (reports.Count == 0)
            return [];

        string? token = await accessTokenProvider(ct)
            ?? throw new InvalidOperationException("not signed in");

        var items = reports
            .Select(r => new SubmitFingerprintItem(r.Title, r.Artist, r.Album, r.Year, r.DurationSeconds,
                ToSigned(r.RawFingerprint) ?? []))
            .ToList();

        using var req = new HttpRequestMessage(HttpMethod.Post, "fingerprints")
        {
            Content = JsonContent.Create(
                new SubmitFingerprintsRequest(items), IdentifyJson.Default.SubmitFingerprintsRequest),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var resp = await http.SendAsync(req, ct);

        // The same distinction the lookup path makes: the service being briefly unwell is not a
        // fault in what was sent. The retry handler has already tried a second and third time, so
        // reaching here means it is still down. The caller keeps the batch and says so quietly.
        if (TransientHttp.SaysNotNow(resp.StatusCode))
            throw new IdentificationUnavailableException(
                await ReadErrorAsync(resp, ct) ?? $"The recognition service is unavailable ({(int)resp.StatusCode}).");

        resp.EnsureSuccessStatusCode();

        var said = await resp.Content.ReadFromJsonAsync(IdentifyJson.Default.SubmitFingerprintsResponse, ct);
        return said?.SongIds ?? [];
    }

    private sealed record SubmitFingerprintsResponse(
        [property: JsonPropertyName("songIds")] IReadOnlyList<long>? SongIds);

    private static IdentificationResult? ToResult(IdentifyResponse body)
    {
        if (body.Status != "matched" || body.Track is null)
            return null;

        return new IdentificationResult(
            body.Track.Title,
            body.Track.Artist,
            body.Track.Album,
            body.Track.Year,
            body.Track.Score,
            body.Source ?? "own-db");
    }

    private static long[]? ToSigned(uint[]? raw)
        => raw is { Length: > 0 } ? Array.ConvertAll(raw, v => unchecked((long)(int)v)) : null;

    /// <summary>One POST to the recognition service.</summary>
    /// <param name="shape">The body's shape. A parameter rather than an inferred type argument,
    /// because a published build is Native AOT, which serialises from generated metadata only.</param>
    private async Task<IdentifyResponse> PostAsync<TBody>(
        string path, TBody payload, JsonTypeInfo<TBody> shape, CancellationToken ct)
    {
        string? token = await accessTokenProvider(ct)
            ?? throw new InvalidOperationException("not signed in");

        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload, shape),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var resp = await http.SendAsync(req, ct);

        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.PaymentRequired)
            throw new SupportersOnlyException("Advanced recognition is for supporters.");

        // The service is missing a key or its upstream is down. This is not "no match", because
        // nothing was matched against. The two are told apart, so the user hears "recognition is
        // unavailable" instead of "this track is not in the database", which would send them
        // looking in the wrong place.
        if (resp.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway)
            throw new IdentificationUnavailableException(
                await ReadErrorAsync(resp, ct) ?? "The recognition service is unavailable.");

        resp.EnsureSuccessStatusCode();

        return await resp.Content.ReadFromJsonAsync(IdentifyJson.Default.IdentifyResponse, ct)
            ?? throw new InvalidOperationException("empty identify response");
    }

    /// <summary>The server's own explanation, when it gave one. It knows why better than we do.</summary>
    private static async Task<string?> ReadErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
    /// <summary>Every shape this backend sends or reads, as the source generator describes them.
    /// The records name their own fields, so no naming policy is set here.</summary>
    [JsonSerializable(typeof(IdentifyRequest))]
    [JsonSerializable(typeof(IdentifyClipRequest))]
    [JsonSerializable(typeof(IdentifyResponse))]
    [JsonSerializable(typeof(SubmitFingerprintsRequest))]
    [JsonSerializable(typeof(SubmitFingerprintsResponse))]
    private sealed partial class IdentifyJson : JsonSerializerContext;

}
