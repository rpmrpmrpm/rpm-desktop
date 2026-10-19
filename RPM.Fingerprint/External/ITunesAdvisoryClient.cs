using System.Net.Http;
using System.Text.Json;

namespace RPM.Fingerprint.External;

/// <summary>
/// Where the explicit-lyrics flag comes from.
///
/// <para>No catalogue of <i>releases</i> carries it. Discogs has no field for it at all, and MusicBrainz
/// has none either. In both, "explicit" survives only as free text that somebody typed into a title. The
/// stores are the ones that know, because they are the ones legally obliged to. The iTunes Search API
/// answers <c>trackExplicitness</c> for any album, with no key and at no cost.</para>
///
/// <para>It is the right source rather than merely an available one. Its three values are the ones the
/// flag was made for. RPM writes the same numbers into the <c>rtng</c> atom that iTunes reads them from,
/// so nothing is translated and nothing is lost. Deezer's enum is richer, because it separates an edited
/// version from a clean one. It could join as a second pass, but it is not worth a second request until
/// somebody wants that distinction.</para>
/// </summary>
public sealed class ITunesAdvisoryClient
{
    private const string Search = "https://itunes.apple.com/search";
    private const string Lookup = "https://itunes.apple.com/lookup";

    /// <summary>
    /// The store to ask, always, whatever locale the user is in.
    ///
    /// <para>Storefronts differ in catalogue and in how completely they carry the flag, and the US one
    /// is the widest. The question here is what the lyrics are, which is a fact about the recording and
    /// the same in every country. Unlike availability, which is not, and which is the only thing
    /// picking a local storefront would buy.</para>
    /// </summary>
    private const string Storefront = "US";

    /// <summary>iTunes has no published rate limit and an undocumented one around twenty calls a
    /// minute per address. One call per release lookup, answered from the metadata cache thereafter,
    /// stays far under it. But a slow answer must never hold up a release listing that is otherwise
    /// complete, so the wait is short and a timeout is a miss.</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>What one album's tracks say about their own lyrics, keyed by (disc, track).</summary>
    /// <param name="ByPosition">Empty when no album matched confidently. That is a miss, not a
    /// claim that nothing is explicit.</param>
    public sealed record AlbumAdvisory(IReadOnlyDictionary<(uint Disc, uint Track), byte> ByPosition)
    {
        public static readonly AlbumAdvisory None = new(new Dictionary<(uint, uint), byte>());

        /// <summary>The flag for one slot, or 0 when this album said nothing about it.</summary>
        public byte For(uint disc, uint track)
            => ByPosition.TryGetValue((disc == 0 ? 1 : disc, track), out var value) ? value : (byte)0;

        public bool IsEmpty => ByPosition.Count == 0;
    }

    /// <summary>
    /// Looks up one album and returns its per-track flags.
    ///
    /// <para>Nothing is returned unless the album matches on both its name and its track count. The
    /// flag is a property of a <i>version</i> of a record, not of a song: the clean and the explicit
    /// cut of the same album share an artist, a title and very nearly a track list, and stamping the
    /// wrong one is worse than stamping nothing. So the gate is deliberately strict and a near miss
    /// is a miss.</para>
    /// </summary>
    /// <param name="trackCount">How many tracks the release being enriched has, across all its media.
    /// Zero skips the count check, which is only right when the caller genuinely cannot count them.</param>
    public async Task<AlbumAdvisory> ForAlbumAsync(
        string? artist, string? album, int trackCount, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(album))
            return AlbumAdvisory.None;

        try
        {
            var term = Uri.EscapeDataString($"{artist} {album}".Trim());
            using var found = await GetAsync(
                $"{Search}?term={term}&entity=album&limit=8&country={Storefront}", ct);
            if (found is null)
                return AlbumAdvisory.None;

            long? collectionId = null;
            foreach (var candidate in Results(found))
            {
                if (!Same(Str(candidate, "collectionName"), album))
                    continue;
                if (artist is { Length: > 0 } && !Same(Str(candidate, "artistName"), artist) &&
                    !Same(Str(candidate, "artistName"), "Various Artists"))
                    continue;
                if (trackCount > 0 && Int(candidate, "trackCount") is { } count && count != trackCount)
                    continue;
                if (candidate.TryGetProperty("collectionId", out var id) && id.TryGetInt64(out var value))
                {
                    collectionId = value;
                    break;
                }
            }

            if (collectionId is not { } album_)
                return AlbumAdvisory.None;

            using var songs = await GetAsync($"{Lookup}?id={album_}&entity=song&country={Storefront}", ct);
            if (songs is null)
                return AlbumAdvisory.None;

            var byPosition = new Dictionary<(uint, uint), byte>();
            foreach (var song in Results(songs))
            {
                if (Str(song, "wrapperType") != "track")
                    continue;   // the first result of a lookup is the album itself
                if (Int(song, "trackNumber") is not { } number || number <= 0)
                    continue;

                uint disc = (uint)Math.Max(Int(song, "discNumber") ?? 1, 1);
                byPosition[(disc, (uint)number)] = Flag(Str(song, "trackExplicitness"));
            }

            return byPosition.Count == 0 ? AlbumAdvisory.None : new AlbumAdvisory(byPosition);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return AlbumAdvisory.None;   // the client's own timeout: a miss, not a failure
        }
        catch (HttpRequestException)
        {
            return AlbumAdvisory.None;
        }
        catch (JsonException)
        {
            return AlbumAdvisory.None;
        }
    }

    /// <summary>
    /// iTunes' word for it as the number every container stores.
    ///
    /// <para><c>notExplicit</c> becomes 0 rather than 2 on purpose. "Clean" means <i>this is the
    /// edited version</i> of something that has an explicit one; a record that was never explicit in
    /// the first place has no advisory at all, and calling it clean would put a "clean" badge on the
    /// entire classical shelf.</para>
    /// </summary>
    private static byte Flag(string? explicitness) => explicitness switch
    {
        "explicit" => 1,
        "cleaned" => 2,
        _ => 0,
    };

    private static async Task<JsonDocument?> GetAsync(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            return null;
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static IEnumerable<JsonElement> Results(JsonDocument doc)
        => doc.RootElement.TryGetProperty("results", out var results) &&
           results.ValueKind == JsonValueKind.Array
            ? results.EnumerateArray()
            : [];

    /// <summary>Two album names are the same record when they differ only in the things a store adds:
    /// punctuation, case, and the "(Deluxe Edition)" the sleeve does not have.</summary>
    private static bool Same(string? a, string? b)
        => Normalise(a) is { Length: > 0 } left && Normalise(b) is { Length: > 0 } right &&
           (left == right || left.StartsWith(right, StringComparison.Ordinal) ||
            right.StartsWith(left, StringComparison.Ordinal));

    private static string Normalise(string? value)
        => value is null
            ? ""
            : new string([.. value.ToLowerInvariant().Where(char.IsLetterOrDigit)]);

    private static string? Str(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Int(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.TryGetInt32(out var n) ? n : null;
}
