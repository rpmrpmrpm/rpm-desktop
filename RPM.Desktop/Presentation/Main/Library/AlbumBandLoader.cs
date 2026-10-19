using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Core.Models;
using RPM.Core.Threading;
using RPM.Desktop.Presentation.Main.Library.Models;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// Which of a record's songs the drawer is about.
///
/// <para>This is a type and not an optional filter, because a forgotten filter fails silently. A
/// compilation opened from a guest artist's page is there because ONE of its hundred songs is by that
/// artist. A drawer that listed the other ninety-nine would be about a record the artist is barely on.
/// The default "all of them" looks right on the album wall and wrong on an artist's page, so the
/// compiler would never ask, and nothing would look broken enough to chase.</para>
///
/// <para>So <see cref="Whole"/> is a stated answer, not a missing one.</para>
/// </summary>
public sealed class BandTracks
{
    private readonly Func<TrackInfo, bool>? _keep;

    private BandTracks(Func<TrackInfo, bool>? keep) => _keep = keep;

    /// <summary>The record as it stands: every song on it. A shelf of an artist's own records wants
    /// this, and so does the album wall, where the record is the subject.</summary>
    public static readonly BandTracks Whole = new(null);

    /// <summary>Only the songs whose credit names this artist. The test is made per track, not from
    /// the folder, because that is the question a guest credit raises. The artist's wall uses the same
    /// test, so a record is on their page and its songs are in it for one reason.</summary>
    public static BandTracks CreditedTo(string artist) => new(t => ArtistWall.Names(t.Artist, artist));

    /// <summary>The songs this narrowing keeps, in the order they arrived.</summary>
    public IReadOnlyList<TrackInfo> Of(IReadOnlyList<TrackInfo> tracks)
        => _keep is null ? tracks : tracks.Where(_keep).ToList();
}

/// <summary>
/// Where a drawer's contents come from. Each source is the surface's own. The wall reads through its
/// priority queue, and an artist's page reads two shelves off the mirror. None of them is optional by
/// accident: a null here means "this surface has no such thing". That is why there are no defaults,
/// and the compiler names every caller.
/// </summary>
/// <param name="Sleeve">What the surface does with the cover's own bytes, beyond drawing them. The
/// wall reads the record's ground out of them and crosses the drawer into it. A surface with no such
/// step passes nothing. This is a hook, not a fixed step of the fill, because the bytes are the only
/// thing the two surfaces share. The reading is shared. The cache it goes into and the cross-fade
/// away from the last record belong to the wall.</param>
/// <param name="RasterScale">The display's scale, used to decide how many pixels a sleeve is decoded
/// at. It comes from the surface, because it depends on the window the drawer is in.</param>
public sealed record AlbumBandSources(
    Func<string, int, CancellationToken, Task<byte[]?>>? Cover,
    Func<AlbumBand, byte[], CancellationToken, Task>? Sleeve,
    Func<string, CancellationToken, Task<IReadOnlyList<TrackInfo>>>? Tracks,
    Func<string, bool>? Classical,
    Func<bool>? UploadsStopped,
    Func<ShelfReading>? Shelf,
    double RasterScale);

/// <summary>
/// Fills one open record: its sleeve at the size the drawer draws it, its tracklist cut into lines,
/// and the two lines of text above them.
///
/// <para><b>This is here, not in the wall, because two surfaces open records now.</b> The wall and an
/// artist's page show the SAME drawer (see <see cref="AlbumBandView"/>). A drawer filled in two
/// different ways is two drawers, however alike they look. One would learn about disc dividers, the
/// stream's own format line, or the size a sleeve must be decoded at, and the other would not.</para>
///
/// <para>Nothing here touches an element. It writes the answers onto the band, and the drawer that
/// watches the band draws them. This lets the same fill serve a surface whose drawer is not on screen
/// yet.</para>
/// </summary>
public static class AlbumBandLoader
{
    /// <summary>
    /// Reads a record's ground off its sleeve: the mean colour that goes under everything, the sleeve
    /// blurred over it, and how much veil the picture needs inside the blur. The blur is already
    /// decoded, so the caller gets the whole answer in one piece.
    ///
    /// <para><b>Read here, applied by the caller.</b> The reading is a property of the sleeve. Every
    /// surface must get the same answer from it, or the same record would show two colours in two
    /// views. What is done WITH the answer belongs to each surface. The wall keeps a memory of grounds
    /// it has already read, and it crosses out of whatever its drawer was standing on. An artist's
    /// page crosses only when its drawer is on screen to be watched. Neither of those belongs to the
    /// other.</para>
    ///
    /// <para>The blur is decoded BEFORE anything is returned, because the layers change together.
    /// Setting the colour now and the picture one dispatcher turn later would be two changes to
    /// watch, where the record should only change once. The result is null where the read failed or
    /// the caller lost interest during it. <c>SetSourceAsync</c> is another dispatcher turn, so the
    /// drawer can be closed or re-pointed during it.</para>
    /// </summary>
    public static async Task<(SleeveGround Ground, BitmapImage? Wash)?> ReadGroundAsync(
        byte[] sleeve, CancellationToken ct)
    {
        if (await Cancellable.RunAsync(() => SleeveGround.Read(sleeve), ct)
                is not { Ran: true, Value: { } ground }
            || ct.IsCancellationRequested)
            return null;

        BitmapImage? wash = null;
        if (ground.Wash is { Length: > 0 } blurred)
        {
            wash = new BitmapImage();
            var stream = Presentation.Common.BitmapBytes.Stream(blurred);
            await wash.SetSourceAsync(stream);

            if (ct.IsCancellationRequested)
                return null;
        }

        return (ground, wash);
    }

    /// <summary>
    /// Fills the band, and returns the lines it built, so the caller can mark whichever of them is
    /// playing. Returns empty where the read was cancelled or the folder could not be opened.
    /// </summary>
    /// <param name="loadCover">False where the caller has already put the right picture on the band and
    /// only wants the text. This is the case for a drawer pointed at a neighbour already read.</param>
    /// <param name="which">Which of the record's songs this drawer is about. There is no default. See
    /// <see cref="BandTracks"/>.</param>
    public static async Task<IReadOnlyList<AlbumTrackItem>> FillAsync(
        AlbumBand band, AlbumBandSources sources, bool loadCover, BandTracks which, CancellationToken ct)
    {
        try
        {
            // The drawer always reads its own sleeve. The card may hold a seed picture, which fills the
            // drawer on the frame it opens. That picture is cut for a small tile (about an inch across),
            // so the sleeve is read again at the size the drawer draws it (BandArtMax). The seed is
            // never used in its place.
            var edge = ArtScaler.DecodeEdgeFor(AlbumBandView.BandArtMax, sources.RasterScale);
            if (loadCover && sources.Cover is { } load
                && await load(band.Card.Folder, edge, ct) is { Length: > 0 } bytes)
            {
                if (ct.IsCancellationRequested)
                    return [];

                band.Cover = await AlbumWallView.DecodeAsync(bytes, edge);

                // Then whatever else this surface makes of the same bytes. The wall reads the record's
                // ground here. See AlbumBandSources.Sleeve.
                if (sources.Sleeve is { } sleeve)
                {
                    await sleeve(band, bytes, ct);
                    if (ct.IsCancellationRequested)
                        return [];
                }
            }

            var whole = sources.Tracks is { } loadTracks
                ? await loadTracks(band.Card.Folder, ct)
                : (IReadOnlyList<TrackInfo>)Array.Empty<TrackInfo>();
            if (ct.IsCancellationRequested)
                return [];

            // Narrowed here, not at the shelf, so the numbers on the lines stay the record's own. A
            // guest's one song on a hundred-track compilation is track 37 of that record, and saying so
            // is most of what the drawer is for.
            var tracks = which.Of(whole);

            // Disc order, with a divider per disc (multi-disc only). The band cuts these into its one or
            // two columns and breaks at a disc boundary where it can. A double album reads disc 1 | disc 2.
            var items = AlbumTrackItem.Build(
                tracks, sources.Classical?.Invoke(band.Card.Folder) == true,
                sources.UploadsStopped?.Invoke() == true,
                sources.Shelf?.Invoke() ?? ShelfReading.Own);

            // The columns are decided by the drawer. It is the only thing that knows how wide it is, and
            // it sets the mode when it is first shown or resized. Setting the columns here from the
            // surface's own guess gives a stacked drawer a two-column cut.
            band.SetLines(items);

            // Real tags replace the folder-name guess once they are read. Use the WHOLE record, not the
            // narrowed list, because the year and the encoding belong to the record, not to this
            // reader's share of it.
            if (whole.FirstOrDefault() is { } first)
            {
                if (!string.IsNullOrWhiteSpace(first.Album))
                    band.Title = first.Album!;
                // artist · year · FLAC · 24/96 · 5.1: each part only when there is one. The format
                // comes from the stream, not the tags (see RPM.Core/Metadata/AudioFormat). It is taken
                // from the album's first track, since a release uses one encoding. If a folder mixes
                // encodings, fix the folder. The line does not try to show both.
                var artist = first.AlbumArtist ?? first.Artist;
                var line = new List<string>(5);
                if (!string.IsNullOrWhiteSpace(artist)) line.Add(artist!);
                if (first.Year > 0) line.Add(first.Year.ToString());
                line.AddRange(RPM.Core.Metadata.AudioFormat.Describe(
                    first.Extension, first.Channels, first.CodecDescription,
                    first.BitsPerSample, first.SampleRate, first.BitrateKbps));

                if (line.Count > 0)
                    band.Subtitle = string.Join("  ·  ", line);
            }

            band.Loading = false;
            return items;
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch
        {
            band.Loading = false;   // A folder that cannot be read shows an empty band, not a spinner
            return [];
        }
    }
}
