using System.Net.Http;
using System.Text.Json;

namespace RPM.Fingerprint.External;

/// <summary>
/// Thin client for the AcoustID public lookup API.
///
/// AcoustID is the open MusicBrainz fingerprint database (~50M recordings, CC0). The API
/// needs an application key: a free one from acoustid.org for non-commercial use, or a key from a
/// paid acoustid.biz plan, which is what the rpm server uses.
/// It returns MusicBrainz recording IDs that we can then look up via MusicBrainzClient for
/// full metadata.
///
/// This client expects the Chromaprint fingerprint as the base64 compressed string emitted
/// by `fpcalc` (without -raw). Use FpcalcRunner.RunCompressed to get that.
/// </summary>
public sealed class AcoustIdClient
{
    private const string Endpoint = "https://api.acoustid.org/v2/lookup";
    private static readonly HttpClient Http = new();

    private readonly string _apiKey;
    private readonly string _userAgent;

    public AcoustIdClient(string apiKey, string userAgent = "RPM/0.1 (validation)")
    {
        _apiKey = apiKey;
        _userAgent = userAgent;
    }

    public sealed record RecordingMatch(
        string RecordingId,
        string Title,
        string[] Artists,
        double Score);

    /// <summary>
    /// Looks up a fingerprint and returns the best-scoring recording match, or null if no match.
    /// Throws on HTTP / parse errors.
    /// </summary>
    public async Task<RecordingMatch?> LookupAsync(string base64Fingerprint, double durationSeconds, CancellationToken ct = default)
    {
        // AcoustID's `meta` parameter expects literal `+` separators in the URL, NOT %2B.
        // Send each meta value as a separate form field so urlencode doesn't conflate them.
        var fields = new List<KeyValuePair<string, string>>
        {
            new("client", _apiKey),
            new("format", "json"),
            new("duration", ((int)Math.Round(durationSeconds)).ToString()),
            new("fingerprint", base64Fingerprint),
            new("meta", "recordings"),
        };
        using var content = new FormUrlEncodedContent(fields);
        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = content };
        req.Headers.UserAgent.ParseAdd(_userAgent);

        using var resp = await Http.SendAsync(req, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"AcoustID HTTP {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.GetProperty("status").GetString() != "ok")
            throw new InvalidOperationException($"AcoustID error: {body[..Math.Min(200, body.Length)]}");

        if (!root.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
            return null;

        // Pick the highest-scoring result that has at least one recording
        RecordingMatch? best = null;
        foreach (var r in results.EnumerateArray())
        {
            double score = r.TryGetProperty("score", out var s) ? s.GetDouble() : 0.0;
            if (!r.TryGetProperty("recordings", out var recs) || recs.GetArrayLength() == 0)
                continue;
            foreach (var rec in recs.EnumerateArray())
            {
                string id = rec.GetProperty("id").GetString() ?? "";
                string title = rec.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var artists = new List<string>();
                if (rec.TryGetProperty("artists", out var arts))
                {
                    foreach (var a in arts.EnumerateArray())
                        if (a.TryGetProperty("name", out var n))
                            artists.Add(n.GetString() ?? "");
                }
                if (best == null || score > best.Score)
                    best = new RecordingMatch(id, title, artists.ToArray(), score);
            }
        }
        return best;
    }

    /// <summary>A release this recording appears on, with the track slot it occupies.</summary>
    public sealed record ReleaseHit(string ReleaseId, string? Title, uint TrackPosition, uint MediumTrackCount);

    /// <summary>Best recording match plus every release it appears on. This is the raw material for
    /// figuring out WHICH album a segment of a long rip belongs to.</summary>
    /// <param name="Album">The album the recording is on, by its release group's title: a studio album
    /// where there is one, then any album, then whatever group came first. Null when AcoustID named no
    /// group.</param>
    /// <param name="Year">The earliest year any of its releases came out, which is the recording's first
    /// release. 0 when no release carried a date.</param>
    public sealed record DetailedMatch(
        string RecordingId,
        string Title,
        string[] Artists,
        double Score,
        IReadOnlyList<ReleaseHit> Releases,
        string? Album = null,
        uint Year = 0);

    /// <summary>
    /// Like <see cref="LookupAsync"/> but also requests the release groups, the releases and the track
    /// positions (`meta=recordings releasegroups releases tracks`). Returns the best-scoring recording,
    /// or null.
    ///
    /// <para>The release groups make a MusicBrainz lookup afterwards unnecessary. They carry the album's
    /// title and kind, and the releases under them carry their dates. Without them, a second request to
    /// MusicBrainz is needed for each match, only to learn those two things.</para>
    /// </summary>
    public async Task<DetailedMatch?> LookupDetailedAsync(string base64Fingerprint, double durationSeconds, CancellationToken ct = default)
    {
        // One meta value with spaces: form-urlencoding turns each space into a literal `+`,
        // which is exactly the separator AcoustID documents. Repeated `meta` fields do NOT
        // accumulate server-side (only one survives). It was verified against the live API.
        var fields = new List<KeyValuePair<string, string>>
        {
            new("client", _apiKey),
            new("format", "json"),
            new("duration", ((int)Math.Round(durationSeconds)).ToString()),
            new("fingerprint", base64Fingerprint),
            new("meta", "recordings releasegroups releases tracks"),
        };
        using var content = new FormUrlEncodedContent(fields);
        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = content };
        req.Headers.UserAgent.ParseAdd(_userAgent);

        using var resp = await Http.SendAsync(req, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"AcoustID HTTP {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");

        return ParseDetailed(body);
    }

    /// <summary>The best-scoring recording in a detailed answer. Public and static so the reading can be
    /// tested without the network: the answer's layout changes with the meta asked for, and that is the
    /// part that can go wrong quietly.</summary>
    public static DetailedMatch? ParseDetailed(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.GetProperty("status").GetString() != "ok")
            throw new InvalidOperationException($"AcoustID error: {body[..Math.Min(200, body.Length)]}");

        if (!root.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
            return null;

        DetailedMatch? best = null;
        foreach (var r in results.EnumerateArray())
        {
            double score = r.TryGetProperty("score", out var s) ? s.GetDouble() : 0.0;
            if (!r.TryGetProperty("recordings", out var recs) || recs.GetArrayLength() == 0)
                continue;
            foreach (var rec in recs.EnumerateArray())
            {
                if (best is not null && score <= best.Score)
                    continue;

                string id = rec.GetProperty("id").GetString() ?? "";
                string title = rec.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var artists = new List<string>();
                if (rec.TryGetProperty("artists", out var arts))
                {
                    foreach (var a in arts.EnumerateArray())
                        if (a.TryGetProperty("name", out var n))
                            artists.Add(n.GetString() ?? "");
                }

                // With release groups asked for, the releases sit under their group; without, under the
                // recording. Both are read, so a change of meta cannot quietly empty the vote.
                var releases = new List<ReleaseHit>();
                var groups = new List<(string Title, string? Type, bool Plain)>();
                uint year = 0;

                if (rec.TryGetProperty("releasegroups", out var rgs) && rgs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var rg in rgs.EnumerateArray())
                    {
                        if (rg.TryGetProperty("title", out var gt) && gt.GetString() is { Length: > 0 } groupTitle)
                            groups.Add((groupTitle,
                                        rg.TryGetProperty("type", out var ty) ? ty.GetString() : null,
                                        !rg.TryGetProperty("secondarytypes", out var st) || st.GetArrayLength() == 0));
                        if (rg.TryGetProperty("releases", out var inGroup))
                            ReadReleases(inGroup, releases, ref year);
                    }
                }
                else if (rec.TryGetProperty("releases", out var rels))
                {
                    ReadReleases(rels, releases, ref year);
                }

                var album = groups.FirstOrDefault(g => g.Type == "Album" && g.Plain).Title
                            ?? groups.FirstOrDefault(g => g.Type == "Album").Title
                            ?? groups.FirstOrDefault().Title;

                best = new DetailedMatch(id, title, artists.ToArray(), score, releases, album, year);
            }
        }
        return best;
    }

    private static void ReadReleases(JsonElement rels, List<ReleaseHit> releases, ref uint earliest)
    {
        if (rels.ValueKind != JsonValueKind.Array)
            return;

        foreach (var rel in rels.EnumerateArray())
        {
            string relId = rel.TryGetProperty("id", out var ri) ? ri.GetString() ?? "" : "";
            if (relId.Length == 0) continue;
            string? relTitle = rel.TryGetProperty("title", out var rt) ? rt.GetString() : null;

            if (rel.TryGetProperty("date", out var date) && date.ValueKind == JsonValueKind.Object
                && date.TryGetProperty("year", out var y) && y.TryGetUInt32(out var yv) && yv > 0
                && (earliest == 0 || yv < earliest))
                earliest = yv;

            uint trackPos = 0, trackCount = 0;
            if (rel.TryGetProperty("mediums", out var mediums))
            {
                foreach (var med in mediums.EnumerateArray())
                {
                    if (med.TryGetProperty("track_count", out var tc) && tc.TryGetUInt32(out var tcv))
                        trackCount = tcv;
                    if (med.TryGetProperty("tracks", out var tracks))
                    {
                        foreach (var trk in tracks.EnumerateArray())
                            if (trk.TryGetProperty("position", out var tp) && tp.TryGetUInt32(out var tpv))
                            { trackPos = tpv; break; }
                    }
                    if (trackPos != 0) break;
                }
            }
            releases.Add(new ReleaseHit(relId, relTitle, trackPos, trackCount));
        }
    }
}
