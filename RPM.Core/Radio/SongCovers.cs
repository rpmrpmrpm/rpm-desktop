using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using RPM.Core.Metadata.Providers;
using RPM.Fingerprint.Catalog;

namespace RPM.Core.Radio;

/// <summary>
/// The cover of the song a station says it is playing, so the player can show the song and not only the
/// station's logo. It is asked of the rpm API (<c>metadata/song</c>), which looks the song up and keeps the
/// answer for every listener. A station repeats its songs, so most asks are answered from there at once.
/// </summary>
/// <remarks>
/// <para><b>One ask per song while the app runs.</b> The key is the artist and the title folded (case,
/// accents, spacing), so two spellings of one announcement share one answer. Two asks while the first is
/// still running share its answer. A song the API found no cover for is kept as having none. An ask that
/// failed (offline, refused, busy) is forgotten, so the song is asked again the next time it plays.</para>
///
/// <para><b>An ask is not cancelled with its caller.</b> The caller is usually the header, and it moves on
/// when the station announces the next song. The answer is still worth having, because the same song comes
/// round again, so the request runs to the end (the client has its own time limit) and only the wait is
/// cancelled.</para>
///
/// <para><b>What is not a song is not asked about.</b> An advert, a jingle or the station's own name reaches
/// here without an artist (see <see cref="RadioStreamSource.NowPlayingTrack"/>), and a half shorter than
/// <see cref="Shortest"/> is not a name. These give no cover, and the caller shows the station's logo.</para>
///
/// <para>This mirrors <c>SongCovers</c> in the Android app, which asks the same route at the same size.</para>
/// </remarks>
public sealed class SongCovers
{
    /// <summary>
    /// The size the cover is asked for, in pixels. The server keeps one answer per song AND size, so this
    /// is the size the Android app asks for too. One listener's ask then answers both apps.
    /// </summary>
    public const int CoverSize = 600;

    /// <summary>An announcement this short on either side is not a song worth asking about ("-", "X").</summary>
    public const int Shortest = 2;

    private readonly Func<string, string, CancellationToken, Task<Asked<CatalogSongCover>>> _ask;

    /// <summary>The asks made so far, answered or still running, by <see cref="KeyOf"/>.</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _known = new(StringComparer.Ordinal);

    /// <summary>Asks the rpm API.</summary>
    public SongCovers(IRpmMetadataApi api)
        : this((artist, title, ct) => api.GetSongCoverAsync(artist, title, CoverSize, ct))
    {
        ArgumentNullException.ThrowIfNull(api);
    }

    /// <summary>Asks <paramref name="ask"/>. For the tests.</summary>
    public SongCovers(Func<string, string, CancellationToken, Task<Asked<CatalogSongCover>>> ask)
    {
        ArgumentNullException.ThrowIfNull(ask);
        _ask = ask;
    }

    /// <summary>Whether this artist and title are worth asking about at all.</summary>
    public static bool Asks(string? artist, string? title)
        => artist?.Trim().Length >= Shortest && title?.Trim().Length >= Shortest;

    /// <summary>
    /// The address of this song's cover. Null where the song has none, where it is not a song
    /// (<see cref="Asks"/>), or where the API could not be asked.
    /// </summary>
    /// <param name="ct">Stops the wait, not the ask. See the remarks on the class.</param>
    public Task<string?> CoverAsync(string? artist, string? title, CancellationToken ct = default)
    {
        if (!Asks(artist, title))
            return Task.FromResult<string?>(null);

        var key = KeyOf(artist!, title!);
        var asking = _known.GetOrAdd(key, k => new Lazy<Task<string?>>(() => AskAsync(k, artist!.Trim(), title!.Trim())));
        return asking.Value.WaitAsync(ct);
    }

    private async Task<string?> AskAsync(string key, string artist, string title)
    {
        Asked<CatalogSongCover> asked;
        try
        {
            asked = await _ask(artist, title, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            asked = Asked.Failed<CatalogSongCover>($"{ex.GetType().Name}: {ex.Message}");
        }

        if (!asked.Answers(out var song, out _))
        {
            // Not an answer about the song. It is forgotten, so the next time the song plays it is asked again.
            _known.TryRemove(key, out _);
            return null;
        }

        // Only an https address is a picture to fetch. Anything else is treated as no cover.
        return song.Cover is { } cover && cover.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? cover
            : null;
    }

    /// <summary>The artist and title folded, so two spellings of one announcement share an answer.</summary>
    public static string KeyOf(string artist, string title) => Fold(artist) + "\0" + Fold(title);

    /// <summary>Lower case, without accents, and with every run of white space made one space.</summary>
    private static string Fold(string text)
    {
        var parts = text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var folded = new StringBuilder(parts.Length);
        var space = false;
        foreach (var c in parts)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }
            if (space && folded.Length > 0)
                folded.Append(' ');
            space = false;
            folded.Append(c);
        }
        return folded.ToString();
    }
}
