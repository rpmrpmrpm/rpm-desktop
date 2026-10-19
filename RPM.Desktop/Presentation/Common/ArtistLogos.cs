using RPM.Core.Threading;
using RPM.Desktop.Services.Art;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// The artist's own lettering, worked out once per artist and kept.
///
/// <para>A logo is the artist's name as they set it: the mark from the record sleeve, with no background.
/// It is a better title for their page than the name in their tags. The tags hold whatever the person who
/// ripped the disc typed. Where a catalogue has a logo, it is shown where the name would be. Where there is
/// none, the name is shown. That is the normal case, not an error.</para>
///
/// <para>The logos are kept here, not fetched by each surface that shows them. The reason is the same as
/// for photographs, see <see cref="ArtistPortraits"/>. Nothing between a surface and the network keeps
/// anything. So an artist opened, closed and opened again would download the same picture twice. What is
/// kept is small: one transparent PNG per artist. Artists who have no logo are kept too. Otherwise a second
/// look would ask two catalogues the same question again.</para>
/// </summary>
public static class ArtistLogos
{
    /// <summary>The logo bytes for each artist. A null entry means that the artist was asked about and has
    /// no logo. Both answers are worth keeping, and only one of them is a picture.</summary>
    private static readonly Dictionary<string, byte[]?> Marks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The number of artists kept before the whole cache is cleared. Each entry is one logo, at
    /// tens of kilobytes. Any entry can be rebuilt with one fetch, so the cache is cleared when it is full,
    /// not trimmed entry by entry. <see cref="ArtistPortraits"/> uses the same rule.</summary>
    private const int MarksKept = 128;

    /// <summary>
    /// One artist's logo, from the cache or from the art sources, or null where nobody has one for them.
    /// </summary>
    /// <param name="album">A record of theirs to ask about. The artwork service answers better on an
    /// artist with one of their albums beside them than on a bare name.</param>
    public static async Task<byte[]?> ForAsync(string artist, string? album, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return null;

        lock (Marks)
        {
            if (Marks.TryGetValue(artist, out var kept))
                return kept;
        }

        byte[]? bytes = null;
        try
        {
            // If the lookup gives no answer, the result is not remembered as "no logo". This is the same
            // as for a logo that cannot be fetched, below.
            if (!(await App.Services.GetRequiredService<ArtworkService>().GetAsync(artist, album, ct: ct))
                    .Answers(out var art, out _))
                return null;

            if (string.IsNullOrWhiteSpace(art.Logo))
            {
                // No logo named: bytes stays null, which is remembered below.
            }
            else if (!(await RemoteImage.BytesAsync(art.Logo, ct)).Answers(out var published, out _))
            {
                // The logo is named but could not be fetched. This is not remembered as "no logo". If it
                // were, the artist's page would show the name in text for the rest of the session, even
                // though a logo exists.
                return null;
            }
            else if (published is not null)
            {
                // The logo is cropped to the lettering before it is shown, and the crop is kept. The
                // catalogues publish every logo on one 800 by 310 canvas, whether the mark is one long word
                // or two short stacked words. A picture drawn flush with the left of a page would then have
                // its lettering a third of the way across, with most of its height empty. See
                // ArtScaler.Trim.
                //
                // This is done here, not by the surface that draws it, because the crop belongs to the
                // picture and not to the surface. Another surface would otherwise repeat the crop, or show
                // the mark in a different place.
                var (trimmed, cut) = await Cancellable.RunAsync(() => ArtScaler.Trim(published), ct);
                if (!trimmed)
                    return null;   // The caller stopped waiting, so the result is not remembered as "no logo"

                bytes = cut;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;   // A failing source does not answer for this artist. Ask again later.
        }

        lock (Marks)
        {
            if (Marks.Count >= MarksKept)
                Marks.Clear();

            Marks[artist] = bytes;
        }

        return bytes;
    }
}
