using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RPM.Core.Art;
using RPM.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;


namespace RPM.Desktop.Presentation.NowPlaying;

/// <summary>
/// The album cover and the disc on the turntable. Both come from the album folder, not from the network, so
/// they are found and shown without waiting for a lookup.
/// </summary>
public sealed partial class ArtDisplay
{
    // The local cover

    /// <summary>Reads the track's own cover off the UI thread. This is best effort. A broken file does not
    /// clear the display.</summary>
    public Task<byte[]?> LoadLocalCoverAsync(string filePath, CancellationToken ct)
        => Task.Run(() => LoadLocalCover(filePath));

    /// <summary>
    /// Cover art that lives at a URL instead of in a file, such as a radio station's logo. Directory
    /// entries often outlive the servers behind them, so a dead link is normal. It returns null, which
    /// leaves the header on its placeholder, the same as for a station with no logo.
    /// </summary>
    /// <summary>
    /// The tuned station's mark, through the one walk every other surface uses. Null when nothing is tuned,
    /// which is what sends the caller back to the address it was given.
    /// </summary>
    public Task<Asked<byte[]?>> TunedStationMarkAsync(CancellationToken ct)
        => Tuned is { } station
            ? Main.LeftPane.Models.RadioStationItem.LogoBytesAsync(station, ct)
            : Task.FromResult(Asked.Answer<byte[]?>(null));

    /// <returns>Answered with the picture's bytes, or with null where the address is not one this fetches
    /// or holds no picture; unanswered where it could not be fetched.</returns>
    public async Task<Asked<byte[]?>> LoadRemoteCoverAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return Asked.Answer<byte[]?>(null);

        // The scheme check above is this method's own. Anyone can write to a station directory, so a
        // file:// or data: URL there is not a picture to fetch.
        if (!(await RemoteImage.BytesAsync(url, ct)).Answers(out var bytes, out var why))
            return Asked.Failed<byte[]?>(why);

        // An ICO file is a set of frames, not a picture, and nothing downstream opens one. So it is
        // unwrapped here, as the station rows do. Without this, a station whose logo the list shows would
        // leave this square empty. The check uses the URL's extension, not the bytes. Reading an ICO out of
        // data that is not one would turn a valid JPEG into nothing.
        return Asked.Answer(
            bytes is { Length: > 0 } && uri.AbsolutePath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)
                ? RPM.Core.Radio.IcoImage.LargestFrame(bytes) ?? bytes
                : bytes);
    }

    /// <summary>The track's own cover: the embedded art first, then the folder's cover image. Import writes
    /// cover.jpg into each album folder.</summary>
    private static byte[]? LoadLocalCover(string filePath)
    {
        try
        {
            if (RPM.Core.Metadata.TagReader.LoadArt(filePath) is { } art)
                return art.Bytes;
            if (Path.GetDirectoryName(filePath) is { } folder
                && RPM.Core.Import.FolderArtWriter.FindFolderImage(folder) is { } image)
                return File.ReadAllBytes(image);
            return null;
        }
        catch
        {
            return null;
        }
    }

    // Cover: decode (awaitable), then show (a synchronous swap)

    public async Task<LoadedCover?> DecodeCoverBytesAsync(byte[] bytes, bool mark, CancellationToken ct)
    {
        try
        {
            // Read here, on the UI thread that can read the XamlRoot, and before anything goes to a worker
            // thread.
            var scale = XamlRoot?.RasterizationScale ?? 1;

            // Which record this cover is for, read here with the scale. The field is written on the UI
            // thread when the record changes. The worker may run after the next record has been set, so the
            // value is read now, not on the worker.
            var identity = _pressingAlbum ?? "";

            // Downscale first. The cover pane and the record label are the largest sizes this picture is
            // drawn at. The label spins its bitmap. Drawing a full 3000 px scan, resampled on every frame,
            // is slow on weak machines. The scaling goes through the disk cache, because it runs on every
            // selection move. Arrowing down an album must shrink its scan once only, not once per row. The
            // pressing colour is read from the scaled bytes on the same worker pass. The hue does not
            // depend on the pixels that the scale removes.
            //
            // A mark with nothing behind it gets a ground drawn for it, and a halo drawn into it, on the
            // same pass. See Mark. Alpha is read from the SCALED bytes, because those are the picture that
            // is shown. A scan that loses its alpha on the way down is no longer transparent.
            var (scaled, palette, ground, badge) = await Task.Run(() =>
            {
                var s = ThumbCache.Get(bytes, ArtScaler.DisplayEdge);
                var p = VinylPressing.PaletteFromArt(s);

                // A sleeve is its own background, whatever it is made of, so nothing is drawn for it.
                //
                // A station's logo always gets a ground, not only when it is transparent. An opaque logo
                // still has margins of its own. Filled into a rounded tile, it would lose its edges. Fitted
                // with space round it on nothing, it would float in the app's dark. Both cases need the
                // same thing: a ground to stand on.
                if (!mark)
                    return (s, p, (byte[]?)null, (byte[]?)null);

                var (marked, made) = Mark(s, identity);

                // The two are then combined into one picture for the label of the record. This is done on
                // this pass, because both are available here. See TransparentMark.Badge.
                return (marked, p, made, made is null ? null : TransparentMark.Badge(marked, made, BadgeEdge));
            });

            var (width, height) = ArtScaler.BoundsOf(scaled);

            // Decode the picture twice, because the two parts of the app need different sizes. The stage's
            // sleeve is half the window high, and it needs all the pixels of the display. The header's
            // square is 156 points, and the visualizer's badge is smaller still. A picture reduced
            // five-fold at DRAW time is sampled, not resized. Each output pixel then takes four samples
            // with no averaging, and the sleeve shows grain and gravel. So the small size is decoded here,
            // once, on a pass that is already running.
            var square = ArtScaler.DecodeEdgeFor(MaxCoverSide, scale);

            return new LoadedCover(await RemoteImage.DecodeAsync(scaled),
                                   palette.Hue, palette.Tint, palette.Glow, palette.Shade,
                                   ground is null ? null : await RemoteImage.DecodeAsync(ground),
                                   width, height,
                                   Mark: mark,
                                   // A station's mark is lettering. The header's square is a two-fold
                                   // reduction of it. Handed to the bitmap decoder, that reads sixteen
                                   // source pixels for each output pixel and drops the rest. On a wordmark
                                   // that leaves gravel down every stroke. See RemoteImage.DecodeMarkAsync.
                                   // The cap is on the LONG side, because a mark never fills this square.
                                   // LoadedCover.Fills is false whenever Mark is true, so a mark is always
                                   // fitted whole.
                                   Square: mark
                                       ? await RemoteImage.DecodeMarkAsync(scaled, square, ArtScaler.MarkSide.Long)
                                       : await RemoteImage.DecodeAsync(scaled, square),
                                   Badge: badge is null ? null : await RemoteImage.DecodeAsync(badge));
        }
        catch
        {
            return null;   // Art that cannot be decoded. The caller keeps what is on screen.
        }
    }

    /// <summary>
    /// A transparent mark and the ground made for it: the picture with a halo drawn round it, and the
    /// figure it is to stand on.
    ///
    /// <para><b>The ground is pushed away from the mark's tone.</b> Drawing the ground from the picture's
    /// own colours seems right, for example a badge in the brand colours. It is wrong. A mark and a ground
    /// of one tone do not separate, so the mark disappears. A near-white logo and a dark blue logo both
    /// vanished into grounds drawn from themselves. So the hues come from the picture, and the value is
    /// pushed the other way from the ink: a dark ground under a light mark, and a pale ground under a dark
    /// mark.</para>
    ///
    /// <para><b>The ground does all of the separation.</b> A keyline (an outline round the mark) is not
    /// used. It would change the broadcaster's logo, and an outline the broadcaster never drew looks like a
    /// mistake, even when the mark is readable. So the separation comes entirely from value. The ground is
    /// far enough from the ink that the mark reads on it untouched. The mark is passed through exactly as
    /// it arrived.</para>
    /// </summary>
    private static (byte[] Mark, byte[]? Ground) Mark(byte[] bytes, string identity)
        // The mark itself is passed through exactly as its broadcaster drew it. Nothing is added to a logo.
        // An outline round it would change the broadcaster's mark. The ground belongs to this app, so the
        // app may change the ground as it likes. The ground comes from TransparentMark, which the station
        // rows also use, so one station's badge is the same badge in both places.
        => (bytes, TransparentMark.Ground(bytes, identity));

    /// <summary>How far a mark is held off the square's edges. Enough that the ground reads as a ground on
    /// all four sides of even a mark that fills the tile; small enough that a small logo is not made
    /// smaller for nothing.</summary>
    private const double MarkInset = 12;

    /// <summary>The badge's own pixels. The record's label is 66 points on a 200-point disc and the disc is
    /// drawn at a few hundred at most, so this is generous at every size it is shown and small enough that
    /// composing it is a few milliseconds on a worker.</summary>
    private const int BadgeEdge = 512;

    /// <summary>Crossfades a new cover in. With null, it fades the current cover out to the placeholder.
    /// The outgoing layer is emptied when its fade finishes, so nothing older stays behind it.</summary>
    public void ShowCover(LoadedCover? cover)
    {
        var incoming = _coverOnA ? CoverB : CoverA;
        var outgoing = _coverOnA ? CoverA : CoverB;

        TrackCover = cover?.Bitmap;
        TrackCoverSquare = cover?.Square ?? cover?.Bitmap;
        TrackGround = cover?.Ground;
        TrackCoverWidth = cover?.Width ?? 0;
        TrackCoverHeight = cover?.Height ?? 0;
        TrackCoverFills = cover?.Fills ?? true;
        TrackPressing = cover?.Pressing;
        TrackTint = cover?.Tint;
        TrackGlow = cover?.Glow;
        TrackShade = cover?.Shade;
        ChromeChanged?.Invoke();

        if (cover is null)
        {
            // No cover for this track. Fade the current one out to the placeholder, and let the record show
            // the rpm mark again.
            FadeOutAndClearCover(outgoing);
            ShowEmptyMark();

            // No sleeve: the record gets the house pressing and the rpm stamp on its label.
            WearFace(FaceFor(null));
            return;
        }

        incoming.Source = cover.Square ?? cover.Bitmap;
        Frame(incoming, cover);
        _coverFill[incoming] = ++_fillSeq;
        _coverOnA = !_coverOnA;

        CoverPlaceholder.Opacity = 0;
        Animate(incoming, 1);
        FadeOutAndClearCover(outgoing);

        WearFace(FaceFor(cover));
    }

    /// <summary>
    /// The record this sleeve makes: its album, the colour it presses in, and the art for its label.
    ///
    /// <para>The cd is carried over only while it still belongs to THIS album. The sleeve shows that the
    /// record has changed, so the previous album's photographed disc is dropped here. The new album's disc
    /// arrives a moment later from the same folder, and it replaces this one.</para>
    /// </summary>
    private RecordFace FaceFor(LoadedCover? cover)
    {
        var album = (_pressingAlbum ?? "").Trim();
        var cd = string.Equals(album, _face.Album, StringComparison.OrdinalIgnoreCase) ? _face.Cd : null;

        // Use the badge where there is one. A badge is a station's mark on its made ground. The label is a
        // single picture and cannot layer the two for itself, so without a badge it would show the mark
        // alone. A sleeve has no badge, so it uses its own picture.
        return new RecordFace(album, cover?.Pressing, cover?.Badge ?? cover?.Bitmap, cd);
    }

    /// <summary>
    /// How a cover is fitted to the square, which is not the same question for a sleeve and for a mark.
    ///
    /// <para><b>A sleeve fills it.</b> It is a rectangle of picture from edge to edge. Cropping it loses
    /// only margin. Anything less than filling leaves bars down the sides of the one square on screen that
    /// is meant to be a record.</para>
    ///
    /// <para><b>A mark is drawn at its own size on the ground below.</b> A station publishes its logo as a
    /// small PNG (for example 50 px) with nothing behind it. Filling the square would enlarge it about
    /// three times, and the picture would go soft and cropped. The mark is capped at its own pixel size,
    /// not set to <c>Stretch.None</c>. So a mark is never drawn bigger than its pixels. A mark larger than
    /// the square is still scaled down to fit, rather than clipped.</para>
    ///
    /// <para><b>A station logo that is not the square's shape is fitted whole as well.</b> Filling is a
    /// crop. On a wide lockup, the crop removes the station's name, for example from the right-hand edge.
    /// Which pictures are fitted is decided by <see cref="LoadedCover.Fills"/>, which uses the rule the
    /// station rows already use. So one station is one badge in both places.</para>
    /// </summary>
    private void Frame(Image layer, LoadedCover cover)
    {
        if (cover.Fills)
        {
            layer.Stretch = Stretch.UniformToFill;
            layer.MaxWidth = double.PositiveInfinity;
            layer.MaxHeight = double.PositiveInfinity;
            layer.Margin = new Thickness(0);
            ClearGround();
            return;
        }

        layer.Stretch = Stretch.Uniform;

        // Held off the edges. A mark that fills the square would touch all four sides. A logo that runs to
        // the corners of a rounded tile reads as a cropped picture, not as a badge. The ground behind the
        // mark is NOT inset: it is the background, and a background with a border round it looks like a
        // panel.
        layer.Margin = new Thickness(MarkInset);

        // Use the size from the bytes, not the bitmap. PixelWidth is only set after the platform has opened
        // the image. A cap of zero would lay the mark out at nothing, and the ground would show an empty
        // square.
        layer.MaxWidth = cover.Width > 0 ? cover.Width : double.PositiveInfinity;
        layer.MaxHeight = cover.Height > 0 ? cover.Height : double.PositiveInfinity;

        // A logo that is its own background keeps the ground the square already has, which is the made
        // ground from the time the station had no picture. Such a logo is opaque and needs nothing behind
        // it. Clearing the ground would put it in the middle of the app's dark background, with empty
        // sides, and it would look like a picture that failed to load.
        if (cover.Ground is null)
            return;

        // A real ground, drawn for the picture that goes on it, replaces the stand-in made from the
        // palette. Clearing _groundFor here stops the stand-in from landing on top of this ground when it
        // finishes. The stand-in starts as soon as the square finds no art, and it may still be in
        // progress.
        _groundFor = null;
        ShowGround(cover.Ground);
    }

    /// <summary>
    /// The square with nothing in it: a record's disc, or a station's antenna on a ground of its own.
    ///
    /// <para><b>A station with no logo is not a record with no sleeve.</b> The disc mark says "this is an
    /// album whose art is missing", which is the wrong sentence over a radio stream. The app already has
    /// the right symbol: the antenna the station rows draw. So the square above the list and the row in it
    /// show the same picture for a station with no logo. The ground goes under it for the same reason it
    /// goes under a mark. A symbol alone on the window's dark looks like a hole, not a placeholder.</para>
    ///
    /// <para>Whether a station is on is asked of the tuner, not guessed from the track. Starting a station
    /// is something the app does, and the tuner is the part that does it.</para>
    /// </summary>
    private void ShowEmptyMark()
    {
        var station = Tuned;
        if (station is null)
        {
            CoverPlaceholder.Text = RecordMark;
            ClearGround();
            CoverPlaceholder.Opacity = 1;
            return;
        }

        // The logo for this station was already found, so it goes straight back up without showing the
        // antenna first. A station announces a track every few minutes, and this runs on each one. Going
        // through the placeholder would make the square blink.
        if (_logoWalked == station.Uuid && _logoCover is { } known)
        {
            ShowCover(known);
            return;
        }

        CoverPlaceholder.Text = StationMark;
        CoverPlaceholder.Opacity = 1;

        // The station gets its own made ground, from the palette, because there is no picture yet. It is
        // seeded by the station's name, so the same station always gets the same ground.
        if (_groundFor != station.Name)
        {
            _groundFor = station.Name;
            _ = ShowMadeGroundAsync(station.Name);
        }

        // Meanwhile, ask the station itself. The square gets only the one address that the directory files
        // under a station, and many stations publish no address. So a station can have a logo that this
        // square would never find. The walk here is the same one that the station rows use. It tries
        // everything the station lists, and then everything its front page declares.
        _ = FindStationLogoAsync(station);
    }

    /// <summary>
    /// The tuned station's own logo, found the long way, and put up as the cover when it turns up.
    ///
    /// <para>Guarded on the station's id both before and after. The walk can take a page fetch, and an
    /// answer about a station that is no longer tuned must not land on the square of the station now
    /// playing.</para>
    /// </summary>
    private async Task FindStationLogoAsync(RPM.Core.Radio.RadioStation station)
    {
        if (_logoWalked != station.Uuid)
        {
            _logoWalked = station.Uuid;
            _logoCover = null;

            var answer = await Main.LeftPane.Models.RadioStationItem.LogoBytesAsync(station);

            // Not answered: the walk met a failure and found nothing. The station is not marked as walked,
            // so the next track announcement asks again.
            if (answer is Asked<byte[]?>.Unanswered && _logoWalked == station.Uuid)
                _logoWalked = null;

            // The caller marks this as a station's logo. The bytes cannot say so: an opaque logo and an
            // opaque sleeve are the same picture, and only the caller knows which one it asked for. The
            // mark is what keeps a wide lockup from being cropped. See LoadedCover.Fills.
            if (answer is Asked<byte[]?>.Answered { Value: { } bytes }
                && _logoWalked == station.Uuid)
            {
                if (Main.LeftPane.Models.RadioStationItem.LogoDiag)
                {
                    var (w, h) = Services.Art.ArtScaler.BoundsOf(bytes);
                    Main.LeftPane.Models.RadioStationItem.Walked(
                        $"cover {station.Name}: got {bytes.Length}B {w}x{h} "
                      + $"alpha={Services.Art.DiscStamp.HasAlpha(bytes)}");
                }

                _logoCover = await DecodeCoverBytesAsync(bytes, mark: true, CancellationToken.None);

                if (Main.LeftPane.Models.RadioStationItem.LogoDiag)
                    Main.LeftPane.Models.RadioStationItem.Walked(
                        $"cover {station.Name}: ground={(_logoCover?.Ground is null ? "none" : "made")}");
            }
        }

        // The logo is kept, not only asked for once. A station announces a track every few minutes, and the
        // square is told each time that there is no art. The logo must go back up each time. Only the fetch
        // must not happen twice.
        if (_logoCover is { } found && Tuned?.Uuid == station.Uuid)
            ShowCover(found);
    }

    /// <summary>Which station has been asked the long way, so its page is fetched once rather than on every
    /// track it announces.</summary>
    private string? _logoWalked;

    /// <summary>The result of that walk, kept so that it can be shown again straight away.</summary>
    private LoadedCover? _logoCover;

    private async Task ShowMadeGroundAsync(string name)
    {
        // Use the cache, as the artist wall does. A station announces a track every few minutes, and its
        // ground is the same picture every time. See FigureCache.
        var made = await Task.Run(() => Services.Art.FigureCache.Sleeve(name));
        if (made is null || _groundFor != name)
            return;

        var ground = await RemoteImage.DecodeAsync(made);
        if (_groundFor != name)
            return;   // Another station was tuned while this was decoding.

        ShowGround(ground);

        // The antenna is drawn to contrast with the picture behind it. It is not drawn at the fixed
        // faintness that a record's disc uses over the window's dark.
        CoverPlaceholder.Foreground = new SolidColorBrush(
            CoverColor.Average(made) is { } tone && ArtistPortraits.PrefersBlackInk(tone)
                ? Windows.UI.Color.FromArgb(0x66, 0, 0, 0)
                : Windows.UI.Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF));
    }

    /// <summary>The station that the made ground was drawn for. This stops it being drawn again on every
    /// track announcement. A station announces a track every few minutes, and the square must not
    /// flicker.</summary>
    private string? _groundFor;

    /// <summary>The station on, or null. Asked of the tuner each time rather than remembered.</summary>
    private static RPM.Core.Radio.RadioStation? Tuned
    {
        get
        {
            try
            {
                return App.Services.GetService<Services.Audio.RadioService>()?.Current;
            }
            catch
            {
                return null;   // No services yet: a design-time surface, or the app is closing.
            }
        }
    }

    /// <summary>An album whose art is missing. This is the glyph the square draws.</summary>
    private const string RecordMark = "";

    /// <summary>A station with no picture: the same antenna the station rows draw, so the square above the
    /// list and the row in it say the same thing.</summary>
    private const string StationMark = "";

    /// <summary>
    /// Fades one made ground over another, in the same way that covers fade over the grounds.
    ///
    /// <para>Two layers are used, not one layer with its source changed. If the source were changed while a
    /// ground is showing, the picture would swap in one frame. That would be the only abrupt change in a square
    /// where everything else fades.</para>
    /// </summary>
    private void ShowGround(BitmapImage? ground)
    {
        if (ground is null)
        {
            ClearGround();
            return;
        }

        var incoming = _groundOnA ? CoverGroundB : CoverGroundA;
        var outgoing = _groundOnA ? CoverGroundA : CoverGroundB;

        if (ReferenceEquals(incoming.Source, ground) || ReferenceEquals(outgoing.Source, ground))
            return;   // The same picture, already up or on its way up.

        incoming.Source = ground;
        _groundFill[incoming] = ++_fillSeq;   // This layer now holds this picture.
        _groundOnA = !_groundOnA;

        Animate(incoming, 1);
        FadeOutAndClearGround(outgoing);
    }

    /// <summary>Takes the made ground down. It fades rather than dropping at once. It is behind a cover
    /// that is also fading, and a background that vanished in one frame would look like a flash under the
    /// cover.</summary>
    private void ClearGround()
    {
        // The made ground now belongs to no station. Tuning back to the station it was drawn for draws it
        // again, rather than finding a stale name and leaving the square empty.
        _groundFor = null;

        FadeOutAndClearGround(CoverGroundA);
        FadeOutAndClearGround(CoverGroundB);
    }

    /// <summary>
    /// Fades a ground layer out and empties it, unless something has claimed the layer in the meantime.
    ///
    /// <para><b>Each fill is stamped, as the covers above are.</b> Two grounds can arrive for one station.
    /// The square shows a made ground as soon as it finds no art. It replaces that ground when the
    /// station's own logo arrives from the walk. The second crossfade reuses the layer that the first is
    /// still fading out. Without the stamp, the first fade's completion runs later and empties the layer,
    /// although the layer now holds the new ground. The result is that the background disappears while the
    /// new one fades in.</para>
    /// </summary>
    private void FadeOutAndClearGround(Image layer)
    {
        if (layer.Source is null)
            return;

        var expected = _groundFill.TryGetValue(layer, out var fill) ? fill : 0;
        Animate(layer, 0, onCompleted: () =>
        {
            if (_groundFill.TryGetValue(layer, out var now) && now != expected)
                return;   // Filled again since. This fade does not clear it.
            if (layer.Opacity > 0.02)
                return;   // It is fading back up, so leave it.

            layer.Source = null;
        });
    }

    /// <summary>Which of the two ground layers is showing, so the next one comes up on the other.</summary>
    private bool _groundOnA;

    /// <summary>Which picture each ground layer is holding. A fade started for one layer cannot empty it
    /// after another fill has claimed it. This shares the covers' counter. The two run in step, so a second
    /// sequence is not needed.</summary>
    private readonly Dictionary<UIElement, int> _groundFill = new();

    // Disc: the album's own record on the turntable

    /// <summary>
    /// Loads the album's disc art, when there is one. This is a transparent PNG of the real record, with
    /// grooves, sheen, label and all. It replaces the whole drawn disc, and does not sit on it as a label.
    /// Most albums have no disc art. Then the drawn record is used.
    /// </summary>
    public async Task<LoadedDisc?> LoadLocalDiscAsync(string filePath, CancellationToken ct)
    {
        try
        {
            // TileEdge: the disc is a 200 px control. Even at 2x, a 640 px decode leaves headroom, and disc
            // scans are as big as sleeves. The scale keeps the PNG's alpha, so the disc stays round and
            // does not become a square. The disk cache means one shrink per disc.
            //
            // Then cut the picture round. The folder may hold disc.jpg as well as disc.png. A JPEG has no
            // transparency, so it is usually a photograph of a CD on a table. Without the cut, its corners
            // would show as a white ring, not a record. See DiscStamp.Round, which the album page uses too.
            // The read probes the folder for three names and then reads the file. It runs off the UI
            // thread, which is the thread that draws the window.
            var raw = await Task.Run(() => LoadLocalDiscBytes(filePath));
            return await DecodeDiscBytesAsync(raw, ct);
        }
        catch
        {
            return new LoadedDisc(null);
        }
    }

    /// <summary>
    /// A record's disc art, from wherever it was read (the folder here, or the computer that holds a
    /// borrowed record), cut round and decoded.
    ///
    /// <para>This is the one place where the bytes become a picture. So a borrowed disc is the same as an
    /// owned one: it is rounded the same way and checked the same way. Whether the disc covers the whole
    /// record is read from the bytes and kept with the picture. A decoded bitmap cannot say this. The
    /// surface that draws the disc needs to know whether the pressing still shows underneath. See
    /// <see cref="DiscStamp.Covers"/>.</para>
    ///
    /// <para>The decoding runs off the UI thread as far as possible.</para>
    /// </summary>
    public async Task<LoadedDisc?> DecodeDiscBytesAsync(byte[]? bytes, CancellationToken ct)
    {
        _ = ct;

        try
        {
            if (bytes is not { Length: > 0 })
                return new LoadedDisc(null);

            var cut = await Task.Run(
                () => DiscStamp.Round(ThumbCache.Get(bytes, ArtScaler.TileEdge), vinyl: false));

            return await RemoteImage.DecodeAsync(cut.Bytes) is { } bitmap
                ? new LoadedDisc(new ScannedDisc(bitmap, cut.CoversRecord))
                : new LoadedDisc(null);
        }
        catch
        {
            return new LoadedDisc(null);
        }
    }

    /// <summary>The album folder's disc art (disc.png), read off the UI thread. It is not embedded. The
    /// disc is a file in the folder, written at import. Most albums have none, and then the drawn record is
    /// used.</summary>
    private static byte[]? LoadLocalDiscBytes(string filePath)
    {
        try
        {
            if (Path.GetDirectoryName(filePath) is { } folder
                && RPM.Core.Import.FolderArtWriter.FindDiscImage(folder) is { } image)
                return File.ReadAllBytes(image);
            return null;
        }
        catch
        {
            return null;
        }
    }

    public void ShowDisc(LoadedDisc disc)
    {
        // Published first. Pegasus presses its own disc from this, whether or not the header has one to
        // show. The record may not be attached yet, because the shell hosts it.
        TrackDisc = disc.Disc;
        ChromeChanged?.Invoke();

        WearFace(_face with { Cd = disc.Disc });
    }
}
