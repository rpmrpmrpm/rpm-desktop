using RPM.Fingerprint.External;
using System.Net.Http;
using System.Linq;
using System.Text.Json;
using RPM.Core.Metadata.Providers;

namespace RPM.Core.Lyrics;

/// <summary>
/// The source of synced lyrics: "what is sung, and when". LRCLIB is at <see href="https://lrclib.net"/>.
///
/// <para>It was chosen because it needs nothing: no key, no account, no quota. In return it asks for an
/// honest User-Agent. MusicBrainz asks for the same, and this app follows the same rule.</para>
///
/// <para><b>The duration is what makes this accurate.</b> Two recordings can share an artist and a title
/// and still differ by four minutes. Examples are a single edit and an album cut, or a live take and the
/// studio take. A sheet written for one of them drifts badly against the other. LRCLIB matches on length
/// as well as name. So the exact lookup is tried first, and its result is trusted. Only when it finds
/// nothing does this fall back to a search. A search result is accepted only when its length is close
/// enough for the words to be expected to line up.</para>
///
/// <para>Everything here is an optional extra, but a failure is not an answer. No match, or words without
/// timings, comes back as no sheet. A failure to ask (no network, a 5xx or 429 response, or the app being
/// offline) and a body that cannot be parsed both throw. The caller writes "no sheet" down, so it must
/// never write down a failure.</para>
/// </summary>
public sealed class LrcLibLookup(FetchJson fetch)
{
    private const string Base = "https://lrclib.net/api";

    /// <summary>How far a candidate's length may differ from the recording's before its timings are assumed
    /// to belong to a different cut. LRCLIB's own matching works to about this.</summary>
    private const double DurationSlackSeconds = 2.5;

    /// <summary>The live service. <paramref name="userAgent"/> should name the app and a way to reach its
    /// author. LRCLIB asks for that, and a request without it is considered rude.</summary>
    public static LrcLibLookup Live(HttpClient http, string userAgent) => new(
        async (url, ct) =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(userAgent);
            using var response = await http.SendAsync(request, ct);

            // 404 is the normal answer for "nobody has written this one down". It is not a fault, and it is
            // the one result worth remembering as an answer in its own right.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;

            // Every other failure status throws. That includes 5xx and 429 responses, and the app's own offline
            // gate, which returns a made-up 503. A failure must never be treated as "no sheet", because the
            // caller would store that answer.
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct);
        });

    // Nothing is caught here, on purpose. A refusal and a failure to ask look the same once both become
    // "no sheet". A caller that stores answers must be able to tell them apart. Storing "this record has no
    // words" because the network was down would keep a wrong answer for a long time. So a transport failure
    // throws, and only a real answer returns.

    /// <summary>
    /// The synced sheet for one recording, or <see cref="LyricSheet.Empty"/> when there is not one.
    /// </summary>
    /// <param name="durationSeconds">The recording's length. Zero or less skips the exact lookup and widens
    /// the fallback. A stream with no known length has to work this way.</param>
    public async Task<LyricSheet> FindAsync(
        string? artist, string? title, string? album, double durationSeconds, CancellationToken ct = default)
    {
        // A title is the one thing that cannot be guessed. An artist can be missing on a loose file, and the
        // search will still find it. A track with no title cannot be looked up at all.
        if (string.IsNullOrWhiteSpace(title))
            return LyricSheet.Empty;

        if (durationSeconds > 0)
        {
            var exact = await GetAsync(artist, title, album, durationSeconds, ct);
            if (exact.IsSynced)
                return exact;
        }

        return await SearchAsync(artist, title, durationSeconds, ct);
    }

    /// <summary>
    /// Every synced sheet this recording has, best timing first. The exact lookup's answer comes first when
    /// there is one. Then come the search's candidates, ordered by how close their length is to the
    /// recording's.
    ///
    /// <para>Ordinary playback does not need this. <see cref="FindAsync"/> takes the first sheet that fits and
    /// stops there. This is for a reader who can see that a sheet does not fit. An example is words that run a
    /// beat ahead of the singer all the way through. That is what a sheet timed for a different cut of the same
    /// song looks like. LRCLIB holds several sheets for songs with many releases, and the only way to tell which
    /// one is right is to show it on screen. So the whole list is returned, and the caller steps through it.</para>
    ///
    /// <para>The list is ordered by the difference between each candidate's length and the recording's. It is
    /// not filtered by that difference. A candidate outside the slack that the automatic path requires is
    /// exactly what a reader looks for when the one inside it is wrong. Nothing is dropped for being a poor fit.
    /// Poor fits simply come last.</para>
    /// </summary>
    public async Task<IReadOnlyList<LyricSheet>> FindAllAsync(
        string? artist, string? title, string? album, double durationSeconds, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return [];

        var found = new List<LyricSheet>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Keep(LyricSheet sheet)
        {
            // Two catalogue entries for one recording, such as a single and the album it appeared on, can carry
            // the same sheet. A reader stepping through alternatives must not see the same words twice and be told
            // they are different answers. So sheets are compared by their timings and words.
            if (sheet.IsSynced && seen.Add(Fingerprint(sheet)))
                found.Add(sheet);
        }

        if (durationSeconds > 0)
            Keep(await GetAsync(artist, title, album, durationSeconds, ct));

        var url = $"{Base}/search?track_name={Esc(title)}&artist_name={Esc(artist)}";
        var json = await fetch(url, ct);
        if (json is null)
            return found;

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return found;

        var candidates = doc.RootElement.EnumerateArray()
            .Select(c => (Sheet: Read(c), Off: Distance(c, durationSeconds)))
            .Where(c => c.Sheet.IsSynced)
            .OrderBy(c => c.Off)
            .ToList();

        foreach (var candidate in candidates)
            Keep(candidate.Sheet);

        return found;
    }

    /// <summary>
    /// How far a candidate's own length is from the recording's. Returns <c>double.MaxValue</c> when either
    /// length is unknown, for example a stream with no duration, or an entry that does not state one. That
    /// makes such a candidate sort last.
    /// </summary>
    private static double Distance(JsonElement candidate, double duration)
        => duration > 0
           && candidate.ValueKind == JsonValueKind.Object
           && candidate.TryGetProperty("duration", out var d)
           && d.TryGetDouble(out var seconds)
            ? Math.Abs(seconds - duration)
            : double.MaxValue;

    /// <summary>
    /// A sheet's identity, used to tell two answers apart. It uses the timings and the words. Genuinely
    /// different transcriptions differ in these, and copies of one transcription match.
    /// </summary>
    private static string Fingerprint(LyricSheet sheet)
        => string.Join('|', sheet.Lines.Select(l => $"{l.Seconds:F2}:{l.Text}"));

    /// <summary>The exact lookup, using name and length together. This is the one that can be trusted
    /// outright.</summary>
    private async Task<LyricSheet> GetAsync(
        string? artist, string? title, string? album, double duration, CancellationToken ct)
    {
        var url = $"{Base}/get?track_name={Esc(title)}&artist_name={Esc(artist)}"
                + $"&album_name={Esc(album)}&duration={(int)Math.Round(duration)}";

        var json = await fetch(url, ct);
        return json is null ? LyricSheet.Empty : Read(json);
    }

    /// <summary>
    /// The fallback. It cannot be trusted outright. A search returns everything that resembles the name, so
    /// each candidate is checked against the recording's length before its timings are believed.
    /// </summary>
    private async Task<LyricSheet> SearchAsync(
        string? artist, string? title, double duration, CancellationToken ct)
    {
        var url = $"{Base}/search?track_name={Esc(title)}&artist_name={Esc(artist)}";
        var json = await fetch(url, ct);
        if (json is null)
            return LyricSheet.Empty;

        // A body that will not parse throws. See the class remarks.
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return LyricSheet.Empty;

        foreach (var candidate in doc.RootElement.EnumerateArray())
        {
            if (duration > 0
                && candidate.TryGetProperty("duration", out var d)
                && d.TryGetDouble(out var seconds)
                && Math.Abs(seconds - duration) > DurationSlackSeconds)
                continue;

            var sheet = Read(candidate);
            if (sheet.IsSynced)
                return sheet;
        }

        return LyricSheet.Empty;
    }

    /// <summary>
    /// One record's body. A body that cannot be parsed throws. It says nothing about the recording, so the
    /// caller must not store it as "no sheet".
    /// </summary>
    private static LyricSheet Read(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Read(doc.RootElement);
    }

    /// <summary>One LRCLIB record. Only <c>syncedLyrics</c> is read. <c>plainLyrics</c> is words without a
    /// clock, and a view that scrolls in time cannot use them.</summary>
    private static LyricSheet Read(JsonElement record)
        => record.ValueKind == JsonValueKind.Object
           && record.TryGetProperty("syncedLyrics", out var synced)
           && synced.ValueKind == JsonValueKind.String
            ? LyricSheet.Parse(synced.GetString())
            : LyricSheet.Empty;

    private static string Esc(string? value) => Uri.EscapeDataString(value ?? "");
}
