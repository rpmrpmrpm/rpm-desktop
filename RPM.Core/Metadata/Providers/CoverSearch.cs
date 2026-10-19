using RPM.Core.Net;
using RPM.Fingerprint.Catalog;

namespace RPM.Core.Metadata.Providers;

/// <summary>A picture's real size, measured by looking at the picture itself.</summary>
/// <param name="Bytes">How large the file is, from the length the host reported. 0 when the host does
/// not say. Some hosts do not report a length when they serve a byte range.</param>
public readonly record struct CoverMeasurement(int Width, int Height, long Bytes);

/// <summary>What the user is looking for. Either half can be given on its own, but at least one is
/// needed. An artist alone is a valid search ("everything by them"), and so is an album title alone.
/// With neither, there is nothing to search for.</summary>
/// <param name="Limit">Per source, not in total. Eight sources at 12 each already fill the window
/// about twice over, which is more than anyone scrolls before they retype the search.</param>
/// <param name="Sources">The sources to ask, by <see cref="CoverCandidate.Source"/>. Empty means all
/// of them. This is the default, and the window sends it until the user unticks a source.</param>
/// <param name="Kind">Which of the record's two pictures is wanted. A disc search is not a cover
/// search filtered afterwards. Only three sources hold disc art. The rest would return sleeves, which
/// look like results in the grid.</param>
public sealed record CoverSearchQuery(
    string? Album,
    string? Artist,
    int Limit = 12,
    IReadOnlyList<string>? Sources = null,
    CoverKind Kind = CoverKind.Cover)
{
    /// <summary>Whether there is anything to search for. This guards the call, not the display. An
    /// empty search would otherwise reach eight upstream services only to be told nothing, eight
    /// times.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Album) && string.IsNullOrWhiteSpace(Artist);
}

/// <summary>
/// Somewhere that can be asked for pictures of an album by name.
///
/// <para>There is one method because the fan-out across sources happens on the server. The app does
/// not hold the keys for some of the sources (fanart.tv, TheAudioDB, Discogs, Google). The free quota
/// on the web search is 100 queries a day in total, across all users. A cached answer serves everyone
/// who types the same words. The same reasons apply to the artwork lookup, which also goes through
/// the API.</para>
/// </summary>
public interface ICoverSearchApi
{
    /// <summary>Which sources can be asked at all right now. The list comes from the server, because
    /// whether a source is set up depends on the server's secrets. A filter that offers a source which
    /// silently returns nothing is worse than no filter. If the API does not answer, the result is
    /// unanswered. The window shows that separately from a server with no sources.</summary>
    Task<Asked<IReadOnlyList<string>>> SourcesAsync(CoverKind kind = CoverKind.Cover, CancellationToken ct = default);

    /// <summary>Every picture the sources have for those words, best first. If one source is down, the
    /// result is still an answer. A partial grid is useful, and the person choosing a cover cannot act
    /// on which source failed. If the API itself does not answer, the result is
    /// <see cref="Asked{T}.Unanswered"/>. The window then says the search could not be completed,
    /// rather than that there are no covers.</summary>
    Task<Asked<CoverSearchResponse>> SearchAsync(CoverSearchQuery query, CancellationToken ct = default);

    /// <summary>
    /// The picture itself, as bytes, or null when it cannot be had.
    ///
    /// <para>This call does not go through the API. The addresses are public CDN addresses, and the app
    /// fetches them directly. That saves a round trip through a Lambda for a megabyte of JPEG. Null is
    /// a normal outcome, not an error. A web result can be behind a hotlink block. The window then lets
    /// the user pick another picture.</para>
    /// </summary>
    Task<byte[]?> DownloadAsync(CoverCandidate candidate, CancellationToken ct = default);

    /// <summary>
    /// How big the picture really is, and how many bytes it is, for a source that did not say.
    ///
    /// <para>Only three of the eight sources state their dimensions. The rest, which include the record
    /// catalogues whose results are the most useful, give an address and nothing else. So the size has
    /// to be measured. It is measured, not downloaded. Every image format states its dimensions in a
    /// header near the start of the file. A ranged request for the first part of the file answers the
    /// question with a tiny fraction of the bytes.</para>
    ///
    /// <para>Null when it cannot be measured. This is a normal outcome. A host may refuse ranged
    /// requests, a format may not be parsed, or the picture may be gone. The caption stays blank.</para>
    /// </summary>
    Task<CoverMeasurement?> MeasureAsync(CoverCandidate candidate, CancellationToken ct = default);
}
