using System.Diagnostics;
using RPM.Core.Art;
using RPM.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using RPM.Core.Threading;


namespace RPM.Desktop.Presentation.NowPlaying;

/// <summary>
/// The backdrop. It loads the picture, decides which colour it stands on and where it sits in the slot,
/// crops it to the strip's current shape, and composites it with its gradients baked in. The old picture is
/// faded out under the new one. Every step checks a generation number. A load that was started for an older
/// state of the strip must not paint the header.
/// </summary>
public sealed partial class ArtDisplay
{
    // Backdrop: load (awaitable and cancellable), then show (synchronous and generation-guarded)

    // Backdrops are fetched and decoded through RemoteImage, the app's single downloader. The crop and the
    // blur need the raw bytes. For that reason BitmapImage's own URL loading cannot be used here.

    /// <summary>Decoded backdrop pairs, keyed by URL and by the shape they were cropped for. Flipping
    /// through an artist's backdrops then does not download, crop or blur them again. The whole cache is
    /// cleared at once when it grows large.</summary>
    private readonly Dictionary<string, (BitmapImage Composite, BitmapImage? Wash, Windows.UI.Color? Dominant, Windows.UI.Color? Average, CropRect Window, Windows.Foundation.Size WashWindow, PicturePlacement Place)> _backdropCache = new();

    /// <summary>
    /// The wash for each photograph, with the geometry it was registered against. It is kept apart from the
    /// tiles, because it does not need to be made again nearly as often.
    ///
    /// <para>The tile is a sharp picture in an exact slot. If the band moves, it must be cut again. The
    /// wash is the same photograph blurred past recognition. Registration matters there only so that the
    /// pixels under the picture's dissolving edge are the same pixels. A registration error much smaller
    /// than the blur's own radius cannot be seen, because the blur has already removed detail at that
    /// scale. So one wash can serve many bakes.</para>
    ///
    /// <para>So the placement it was drawn from, and the window it was drawn for, are kept with it, not the
    /// geometry. Those two are what the reuse test needs. The test is a measurement in pixels (see
    /// <see cref="Registers"/>), not a tolerance on numbers that only stand in for it.</para>
    /// </summary>
    private readonly Dictionary<string, (PicturePlacement Place, WashCanvas Canvas, BitmapImage? Wash, Windows.UI.Color? Average)> _washCache = new();

    /// <summary>What fraction of the blur's radius a kept wash may be out of register by. The value is one
    /// half. The blur has removed all detail at that scale, so an error of that size cannot be
    /// seen.</summary>
    private const double WashSlop = 0.5;

    /// <summary>
    /// Whether a wash already in hand still lands under the picture it is meant to be a blurred copy of.
    ///
    /// <para>Two conditions must both hold. First, the wash must REACH the window. A wash is drawn at the
    /// size of the window it was made for, and it is placed at that size, never stretched. So a wash made
    /// for a smaller window leaves the corners bare. Second, the wash must LAND under the picture. The
    /// sharp copy is placed for the new geometry. The error is the difference between where the two
    /// mappings send the same pixel of the photograph. The four corners of the picture are where that error
    /// is largest. Well inside the blur's own radius nothing can be seen, so the wash is kept.</para>
    ///
    /// <para>The reach test works in place of a scale. A wash is never stretched, so a change of width
    /// moves both copies by the same number of points. A picture that was fitted again (<see
    /// cref="RefitPicture"/>) comes out registered exactly. So a resize re-renders the tile and keeps the
    /// expensive copy.</para>
    /// </summary>
    internal static bool Registers(PicturePlacement was, WashCanvas wasCanvas,
                                   PicturePlacement now, double width, double height)
    {
        if (wasCanvas.Width <= 0 || wasCanvas.Height <= 0 || wasCanvas.Blur <= 0 || width <= 0 || height <= 0)
            return false;

        // Reach. A window that has grown past what this copy covers would show a strip with no blurred
        // picture in it. The copy is drawn for a canvas half again as large as its window (see WashReach),
        // so an ordinary resize is still inside it.
        if (wasCanvas.Width < width || wasCanvas.Height < height)
            return false;

        // How far the kept wash has moved. Every layer is placed with its right edge on the window's right
        // edge (see Stand). So a copy drawn for a window of another width is slid by the difference between
        // the two windows. The canvas reaches further than its window on the left. That overscan appears on
        // both sides of this subtraction, because it was added to the picture when the copy was drawn. So
        // it cancels out and does not appear here.
        var slid = width - wasCanvas.ForWidth;

        var worst = 0.0;
        for (var corner = 0; corner < 4; corner++)
        {
            var x = now.SrcX + ((corner & 1) == 0 ? 0 : now.SrcW);
            var y = now.SrcY + ((corner & 2) == 0 ? 0 : now.SrcH);

            // Where the blurred copy puts that pixel of the photograph, and where the sharp one does.
            var blurX = was.DestX + ((x - was.SrcX) * was.Scale) + slid;
            var blurY = was.DestY + ((y - was.SrcY) * was.Scale);
            var sharpX = now.DestX + ((x - now.SrcX) * now.Scale);
            var sharpY = now.DestY + ((y - now.SrcY) * now.Scale);

            worst = Math.Max(worst, Math.Max(Math.Abs(blurX - sharpX), Math.Abs(blurY - sharpY)));
        }

        // The blur's radius, in the window's own points, is carried on the canvas. It is stated as a
        // fraction of the window the wash was made for. The canvas now reaches past that window, so the
        // radius cannot be read from the canvas's own width.
        return worst <= wasCanvas.Blur * WashSlop;
    }

    /// <summary>What is on screen: which photo, and where it is cut. A reshape that lands on the same
    /// window shows the same picture. Dissolving a picture into itself would flicker for no
    /// reason.</summary>
    private string? _shownUrl;
    private CropRect _shownWindow;

    /// <summary>Where that cut sits in the band. This lets the next bake tell a re-fit of the same framing
    /// apart from a new framing. Null when there is nothing on the header.</summary>
    private PicturePlacement? _shownPlace;

    /// <summary>
    /// Downloads, crops, blurs and decodes a backdrop into a payload that is ready to show. It does not
    /// wait for anything visible. The manager checks its generation, and only then calls <see
    /// cref="ShowBackdrop"/>. The download uses the cancellation token, so a load that has been superseded
    /// stops downloading bytes for a track the user has already left. The shape of the crop is the current
    /// <see cref="Scene"/>, so the cache key includes it.
    /// </summary>
    /// <returns>Answered with the backdrop, or with null where there is no picture at the address. It is
    /// unanswered where it could not be fetched or baked. The caller then keeps whatever is on the header
    /// instead of clearing it. See <see cref="RemoteImage.BytesAsync"/>.</returns>
    /// <param name="framing">How the person placed this photograph by hand, or null when the chooser frames
    /// it. Nearly every picture is framed by the chooser. The value is stated at every call site and is
    /// never defaulted. A load that forgot it would quietly put the chooser's framing back over somebody's
    /// own. The compiler is the only thing that can be relied on to ask.</param>
    public async Task<Asked<LoadedBackdrop?>> LoadBackdropAsync(string? url, ArtFraming? framing, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return Asked.Answer<LoadedBackdrop?>(null);

        // Wait until the header stops moving. The pictures below are then baked once, for the place where
        // they will be seen (see SettledGeometryAsync).
        var baked = await SettledGeometryAsync(ct);
        var scene = Scene;
        var slotWidth = baked.SlotWidth;
        var band = baked.Band;
        var windowWidth = baked.WindowWidth;
        var windowHeight = baked.WindowHeight;
        var breath = PictureBreath;

        // The framing that this photograph already has on the header, when the band has not moved and the
        // framing can still fit a header this wide (see HeldFraming). A window that is dragged wider or
        // narrower does not need a new framing. The crop is requested for the shape that the framing was
        // made at. The placement is fitted again to the new room. What changes is how far the picture
        // reaches to the left, into its own wash. Null means that the chooser decides the framing, which is
        // what every first bake does.
        var held = HeldFraming(url!, framing, band, slotWidth);
        var aspect = held?.Aspect ?? BackdropAspect();

        // How far the picture's edges may dissolve. This is read from the SETTLED geometry, not from the
        // switch. The two say the same thing. The slot is narrower than the window exactly when a disc
        // reaches in from the right (see RecordInsetFor, which is the only thing that narrows it, and which
        // cannot return zero while the record is on). But they answer at different moments. The switch
        // answers for now. The geometry above answers for the picture that is being baked, and the ramp has
        // to fit that picture. Reading the snapshot means that a toggle which lands between the two reads
        // cannot cause a mismatch.
        var sideDissolve = SideDissolveCap(slotWidth, windowWidth);
        // Both copies are baked for this geometry. The tile is baked for the slot and the band. The wash is
        // baked for the whole window, and for where the picture lands in it. So all of these values are in
        // the key. A resize gives a new pair. Until the new pair is ready, the images on screen are scaled
        // to fit. The FRAMING is also in the key. A hand-placed window changes the tile, and nothing else
        // in this key does. Without it, a second visit to a re-framed photograph would get the composite
        // baked for the old window, and the change would not appear. That looks exactly like a save that
        // failed. The dissolve's ceiling is also in the key. It is the one input to the bake that the
        // numbers above do not already imply. It moves with the record, and so does the slot width. But
        // that link comes from arithmetic in another function. A cache that is correct only because of that
        // link goes stale when the function changes, so the ceiling is stated here. The framing the picture
        // is HELD at is also in the key, because the numbers above do not imply it. The same photograph in
        // the same slot is cut differently, depending on whether it was framed for this room or re-fitted
        // into it from another room. It is stated as what a re-fit holds (see Stamp). So a tile for a given
        // framing in a given slot has ONE key, however the header reached that width. This lets a window
        // that is dragged back to an earlier width get the tile it already had.
        var key = $"{url}|{slotWidth:F0}|{band:F0}|{windowWidth:F0}|{windowHeight:F0}|{sideDissolve:F0}"
                + $"|{framing?.Stamp() ?? "auto"}|{(held is { } at ? Stamp(at.Place) : "fresh")}";

        try
        {
            if (!_backdropCache.TryGetValue(key, out var pair))
            {
                if (!(await RemoteImage.BytesAsync(url, ct)).Answers(out var fetched, out var why))
                    return Asked.Failed<LoadedBackdrop?>(why);
                if (fetched is not { } bytes)
                    return Asked.Answer<LoadedBackdrop?>(null);   // There is no picture at that address.

                var (cropped, frame, sourceWidth, sourceHeight) = await RenderCropAsync(bytes, aspect, scene);

                // The chooser's answer is worked out whether or not it is used. It is the framing that the
                // manual window opens on, and the framing that a reset returns to. It costs nothing extra,
                // because the pass that produces it runs anyway. When the picture is already on the header
                // under a chooser framing, a wider or narrower header re-fits that framing. It does not
                // read the photograph again.
                var chosen = framing is null && held is { } fit
                    ? RefitPicture(fit.Place, frame, slotWidth, band, breath)
                    : PlacePicture(frame, sourceHeight, slotWidth, band, breath);

                // Which rule chose the zoom here: either the size that the band wants for the subject, or
                // the floor for how much of the header the picture covers. The next bake at another width
                // needs this. A zoom that the room chose is only correct at that width (see
                // FramedForThisRoom). By the time that bake asks, there is no frame left to ask. A hand
                // framing is neither of these. The person chose it, and it holds at every width.
                var byRoom = framing is null && ChooseZoom(frame, slotWidth, band).ByRoom;

                // Which of the four sources the picture on screen came from, for the ring line below. This
                // is worth recording. The sources can be told apart only by arithmetic. The first question
                // asked about an odd-looking banner is which rule drew it. The chooser used either the band
                // or the floor (MinBandCover). A re-fit keeps a zoom chosen earlier. A hand framing is not
                // held to any rule, because it is what the person said.
                var how = framing is not null ? "hand"
                        : held is not null ? "refit"
                        : byRoom ? "chosen, room" : "chosen, band";
                RememberAutoFraming(url!, chosen, sourceWidth, sourceHeight, PlacedAspect(slotWidth, band, breath));

                // Where the picture goes. This is worked out once. Both copies are drawn from it, and that
                // is what makes them register (see PicturePlacement).
                var place = chosen;
                if (framing is { } placed && sourceWidth > 0 && sourceHeight > 0)
                {
                    // The same applies when the header already holds the person's own framing. The stamp in
                    // HeldFraming makes this safe. A framing saved from the dialog has a different stamp,
                    // so the picture it asks for is placed, not re-fitted.
                    place = held is { } handFit
                        ? RefitPicture(handFit.Place, frame, slotWidth, band, breath)
                        : PlaceFramed(placed, sourceWidth, sourceHeight, slotWidth, band, breath);

                    // The compositor still gets the chooser's frame, not the hand-made window. This is
                    // deliberate. The compositor reads the frame to find where the people are (Content,
                    // Keep), so that its dissolves do not remove a face. Moving a photograph across the
                    // header tells nothing about where the faces are, so the detector gives the answer in
                    // both cases. The window is used in place of the frame only when the pass could not run
                    // at all. A frame of zero size would drop the picture, and the window is the better
                    // choice then (see RenderComposite's guard).
                    if (frame.Rect.Width <= 0 || frame.Rect.Height <= 0)
                    {
                        var box = new RPM.Core.Art.CropRect(
                            (int)Math.Round(place.SrcX), (int)Math.Round(place.SrcY),
                            (int)Math.Round(place.SrcW), (int)Math.Round(place.SrcH));
                        frame = new CropFrame(box, box, box, 0);
                    }
                }

                // The SECOND copy of the photograph has the same pixels in the same place. It covers the
                // whole window and is blurred past recognition. It is the ground that everything stands on,
                // and it is what the picture's own edges dissolve into. It is reused across bakes while it
                // is close enough to registered (see _washCache). This is the expensive copy, and it is the
                // one that needs rebuilding least.
                if (!_washCache.TryGetValue(url!, out var washing)
                    || !Registers(washing.Place, washing.Canvas, place, windowWidth, windowHeight))
                {
                    // Drawn for a canvas that reaches past this window (see WashReach). So if the window
                    // grows, the end of the wash is not uncovered.
                    var canvas = WashCanvasFor(windowWidth, windowHeight);
                    var blurred = await Task.Run(
                        () => RenderWash(cropped ?? bytes, place, windowWidth, windowHeight, canvas));
                    // The tone behind the wash, and how strongly it must be veiled so that the library's
                    // white text is readable. Both come from the blurred copy. That is the same photograph,
                    // averaged and already decoded small. They come only from the part that the WINDOW
                    // shows. The overscan is out of sight, and its edges are fading out.
                    washing = (place, canvas,
                               blurred is null ? null : await RemoteImage.DecodeAsync(blurred.Value.Wash),
                               await Task.Run(() => CoverColor.Average(blurred?.Shown ?? cropped ?? bytes)));

                    if (_washCache.Count > 8)
                        _washCache.Clear();
                    _washCache[url!] = washing;
                }

                var wash = washing.Wash;
                var average = washing.Average;
                var dominant = await Task.Run(() => DominantColour(cropped ?? bytes));

                // The FIRST copy is the framed picture for the band, with its edges dissolved.
                var compositeBytes = await Task.Run(
                    () => RenderComposite(cropped ?? bytes, frame, place, held?.Place ?? place,
                                          slotWidth, band, sideDissolve,
                                          RecordOnTheBand(slotWidth, windowWidth)));
                var composite = await RemoteImage.DecodeAsync(compositeBytes ?? cropped ?? bytes);

                // How big the subject came out. The header keeps this the same from track to track (see the
                // zoom in PlacePicture). It is logged for each bake, not for each swap, because it
                // describes the framing and not what is on screen.
                RPM.Core.Diagnostics.Scope.Said(
                    "art",
                    $"framed({how}) zoom {place.Scale:F3} subject {frame.Keep.Height * place.Scale:F0} "
                  + $"of band {band:F0}, drawn {place.DestW:F0} of room {Math.Max(8, slotWidth - breath):F0}");

                // What this bake FRAMED, so the next one at another width can be a re-fit of it rather than
                // a fresh reading of the photograph.
                //
                // Only when something was framed. A re-fit is not a framing, and it must not replace the
                // one it came from. Remembering the trimmed placement would make every trim permanent. A
                // window that was narrowed and then widened again would keep the picture cut down to the
                // narrow header's width.
                if (held is null)
                    RememberFraming(url!, framing, band, aspect, place,
                                    (place.SrcX + place.SrcW - frame.Rect.X) * place.Scale,
                                    byRoom, slotWidth);

                pair = (composite, wash, dominant, average, frame.Rect,
                        new Windows.Foundation.Size(washing.Canvas.Width, washing.Canvas.Height), place);

                // The limit is small on purpose. A composite is a slot-sized RGBA bitmap, tens of MB each,
                // and eight covers an artist's carousel. Past that, the whole cache is cleared. Dropping
                // only the oldest would need an order that this dictionary does not keep. A re-render takes
                // only a moment.
                if (_backdropCache.Count > 8)
                    _backdropCache.Clear();
                _backdropCache[key] = pair;
            }

            var groundColour = GroundColour(pair.Average);
            var scrimColour = pair.Dominant is { } scrim ? Dim(scrim, ScrimDim) : DefaultScrim;
            var veil = pair.Average is { } tone ? CoverColor.Veil(tone) : DefaultVeil;

            return Asked.Answer<LoadedBackdrop?>(new LoadedBackdrop(url!, pair.Composite, pair.Wash, veil,
                                      groundColour, scrimColour, pair.Window, baked, pair.WashWindow,
                                      pair.Place));
        }
        // Cancellation is not caught here. The pump treats it as cancellation, not as "no art". The filter
        // lets it pass, which avoids a rethrow, and the debugger reports a rethrow twice.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // This is a failure, not an answer about the picture. The caller keeps whatever is on screen.
            return Asked.Failed<LoadedBackdrop?>($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Crossfades a decoded backdrop in. This is synchronous by contract. The manager has already checked
    /// its generation, and there must be no await between that check and this swap. So two swaps can never
    /// interleave and land out of order. The outgoing layer is faded out and then emptied, so it holds
    /// nothing older.
    /// </summary>
    public void ShowBackdrop(LoadedBackdrop art)
    {
        LandPair(BackdropA, BackdropB);   // Land any pending pair before anything is read from it (see LandPair).

        // Same photo, new crop for a new shape. The same-window case returns early below. This is not a
        // dissolve between two pictures. It is a re-cut of the picture on screen, and that is handled
        // differently (below).
        var isReshape = art.Url == _shownUrl;

        // Whether the re-cut is only a RE-FIT of the framing already up. That means the same photograph at
        // the same zoom, on the same rows, meeting the record at the same place, and reaching further left
        // or less far (see <see cref="RefitPicture"/>). Both kinds now go up in the same way (the hand-over below), so
        // this value is used only by the ring line. "trim" means the resize moved no drawn pixel. "recut"
        // means the picture was framed again. The difference matters for a window that grew a little, and
        // for a framing that could not reach across it (HeldFraming).
        var isTrim = isReshape && _shownPlace is { } standing && Trims(standing, art.Place);

        var incoming = _backdropOnA ? BackdropB : BackdropA;
        var incomingMain = _backdropOnA ? BackdropImageB : BackdropImageA;
        var outgoing = _backdropOnA ? BackdropA : BackdropB;
        var outgoingMain = _backdropOnA ? BackdropImageA : BackdropImageB;

        // Same photo, same window, AND the tile on screen is the one being handed in: nothing moved.
        // Re-place for the current size and return.
        //
        // The window alone is not enough. A tile is baked for a slot of one width and a band of one height.
        // If the band moves and the subject does not, a reshape can land on the same crop window with a
        // tile that was rebuilt for the new band. So the test also compares the tile on screen with the one
        // being handed in.
        var shownMain = _backdropOnA ? BackdropImageA : BackdropImageB;
        if (art.Url == _shownUrl && art.Window == _shownWindow
            && ReferenceEquals(shownMain.Source, art.Composite))
        {
            _bakedFor = art.Baked;
            _shownPlace = art.Place;
            _backdropBakedFor[shownMain] = art.Baked;
            PlaceBackdrop();
            ScheduleRecrop();
            return;
        }

        // The framed picture, in one image with its dissolves baked in. So the dissolves can never fall out
        // of step with it.
        //
        // The layer is set to nothing BEFORE it is given the picture, never after. A layer that is given
        // its new composite while it still has opacity shows that composite, at its final size and place,
        // for as long as it takes to reach the line that hides it. If a frame is composed in between, that
        // frame is the destination. The result is one frame of the finished artwork, and then the dissolve
        // starts from the beginning. That is the flicker a Storyboard gives when its base value reverts,
        // reached from the other direction. The order is the fix, not the kind of animation.
        Hide(incoming);
        incomingMain.Source = art.Composite;
        _backdropBakedFor[incomingMain] = art.Baked;   // What it was made for. This is how it is placed.

        // The same photograph's blurred copy goes under the whole window. It is crossfaded on its own pair
        // of layers, so the ground moves with the art.
        ShowWash(art, handOver: isReshape);

        SyncScrimColour(art.Scrim);
        SyncDominant(art.Ground);   // The colour behind the wash, for when the wash is not up.

        _shownUrl = art.Url;
        _shownWindow = art.Window;
        _shownPlace = art.Place;   // Where the picture sits. The next swap is measured against this.
        // What the pictures were made for. This is not necessarily the current geometry, because the load
        // is asynchronous and the header can move while it runs. Recording the current geometry here would
        // leave a stale tile that nothing refreshes. See BakedGeometry.
        _bakedFor = art.Baked;

        // Only the incoming layer is placed. The one on screen keeps its geometry and dissolves out.
        PlaceLayer(incomingMain);

        _backdropFill[incoming] = ++_fillSeq;   // This layer now holds art.Url.
        _backdropOnA = !_backdropOnA;

        // One line per swap goes to the ring, not to a debugger. It records the kind of swap and the
        // numbers that decided it. A swap is rare, one per track or per settled resize, so this is a few
        // lines an hour. It is how a resize is read back afterwards.
        RPM.Core.Diagnostics.Scope.Said(
            "art",
            $"{(isTrim ? "trim" : isReshape ? "recut" : "dissolve")} slot {art.Baked.SlotWidth:F0} "
          + $"band {art.Baked.Band:F0} zoom {art.Place.Scale:F3} "
          + $"drawn {art.Place.DestW:F0}x{art.Place.DestH:F0} at {art.Place.DestX:F0}");

        if (isReshape)
        {
            // UNDER-DRAWN: the new cut goes up complete, underneath, and the old one is faded off the top
            // of it. One branch covers both kinds of re-cut: the trim that changed only the picture's far
            // end, and the re-framing that changed the zoom as well. The answer to both is the same. The
            // new picture is on screen, whole, from the first frame.
            //
            // THE TRIM is why it is drawn this way round. The two tiles are the same photograph, at the
            // same scale, in the same place. Where they agree, which is everywhere the picture is at full
            // strength, the one underneath shows through exactly as fast as the one on top leaves. So the
            // sum never moves off the picture's own colour, and there is no dip. If they were crossfaded,
            // two copies of an identical picture would take each other down to three quarters at the
            // half-way point (see the argument in ShowWash). If they were swapped in one step, the one
            // place they differ would be visible. A narrower header trims the picture's far end, so the new
            // tile has a soft edge where the old one ran off the side at full strength, and that edge would
            // appear from nothing.
            //
            // THE RE-FRAMING is drawn the same way. Two copies of the same photograph at different zooms
            // look like a double exposure. Every edge doubles, and every face goes soft, as in a badly
            // registered print. That is still true during the 400 ms that the hand-over takes, and it is
            // the price. What it buys is a header that is never empty and never waits. Fading out and then
            // in would take well over a second, during which the band defocuses into its own wash and then
            // comes back. On a maximize, where the whole window has just changed shape, that would read as
            // the artwork arriving late, not as the window resizing.
            Canvas.SetZIndex(incoming, 0);
            Canvas.SetZIndex(outgoing, 1);
            DissolveOf(incoming).Set(1);

            var handed = _backdropFill.TryGetValue(outgoing, out var f) ? f : 0;
            Fade(outgoing, 0, HandOverDuration, () =>
            {
                Canvas.SetZIndex(outgoing, 0);
                if (_backdropFill.TryGetValue(outgoing, out var now) && now == handed)
                    outgoingMain.Source = null;   // Unless it has been filled again as a newer incoming picture.
            });
        }
        else
        {
            // The picture always dissolves in on the slow 2 s photo fade, whether it comes from an empty
            // strip or from another artwork. The scrim uses the SAME clock (SyncStage below), so a fade-in
            // from nothing can never land bright and then get dimmed. Only the drawer, record and wash move
            // on the fast transition clock. The photograph keeps the gentle dissolve.
            Fade(incoming, 1);
            FadeOutAndClearBackdrop(outgoing, outgoingMain);
        }

        // The whole-window album ground follows the DRAWER, on the fast clock, so it never lags behind as a
        // dark band over the grid. From idle to art it fades in with the drawer. From art to art it is
        // already up, so this does nothing.
        Fade(DominantFill, 1, WashDuration);

        // The veil belongs to the wash and moves with it, on the wash's own slow clock. If the veil used
        // the fast clock, the new picture's exposure would be applied while the old picture was still most
        // of what was on screen. A dark album after a bright one would then dim the outgoing photograph
        // before its own picture arrived.
        Fade(WashVeil, art.Veil, FadeDuration);

        // The photo is now the picture on screen. The waves step aside as it comes up. The scrim comes with
        // it on the SAME slow clock as the picture, so its darkening arrives in step and never dims a
        // picture that has landed.
        SyncStage();

        // Now that something is up, check whether it was made for the geometry it has landed in.
        //
        // The recrop timer is normally started by a layout pass, and that is not enough on its own. On the
        // first track of a session, the load can be running before the control has been laid out. Then the
        // pictures are baked against a band of nothing and a record of no width. The crop is framed for the
        // whole window instead of the header, so the first header looks too soft and too small. The timer
        // that would catch this fires long before, while there is nothing on screen to check. The same gap
        // affects anything that settles during a load. The last layout pass is over before the artwork it
        // invalidates has arrived.
        ScheduleRecrop();

        BackdropChanged?.Invoke();   // The frost behind the cards is about to change for two seconds.
    }

    /// <summary>Nothing to show: fade the current backdrop out and let the waves have the strip back. The
    /// scrim goes with the photo, and the layer is emptied once it is gone.</summary>
    public void ClearBackdrop()
    {
        // A swap that is still in flight has TWO layers up (the over-draw). Fading only the layer marked
        // visible would leave the other one standing there for good.
        LandPair(BackdropA, BackdropB);

        var visible = _backdropOnA ? BackdropA : BackdropB;
        var visibleMain = _backdropOnA ? BackdropImageA : BackdropImageB;

        if (_shownUrl is null)
            return;   // Already clear. Nothing is on screen to fade.

        _shownUrl = null;
        _shownWindow = default;
        _shownPlace = null;
        // With no art, the picture, the wash and the ground all clear together on one fast duration. Then
        // nothing is left half-faded behind the grid as a stranded band.
        FadeOutAndClearBackdrop(visible, visibleMain, duration: WashDuration);
        ClearWash();
        Fade(DominantFill, 0, WashDuration);
        SyncStage(WashDuration);   // Clear the scrim WITH the picture, not on the slow clock.
        SyncScrimColour(DefaultScrim);
        SyncDominant(DefaultScrim);
        BackdropChanged?.Invoke();
    }

    // The wash: the same photograph, blurred, under the whole window

    /// <summary>How hard the wash is veiled when the picture's own tone could not be read.</summary>
    private const double DefaultVeil = 0.35;

    /// <summary>
    /// Brings the blurred copy in under the whole window, on the same slow clock as the picture above it,
    /// so the ground and the art arrive together.
    ///
    /// <para>This uses a per-frame tween, not a storyboard. An opacity storyboard in Uno HOLDS its end
    /// value, and a held value cannot be written over. If the track changes while a wash is still fading,
    /// the layer that must come in next is the one holding 1 from last time. The animation to 1 then has
    /// nothing to do, and the new wash arrives complete in a single frame, while the photograph above it is
    /// still about two seconds from landing. A plain number written every frame has no memory of what it
    /// was before.</para>
    ///
    /// <para>OVER-DRAWN, not crossfaded. The incoming copy is laid over the outgoing one, which is kept at
    /// full strength until it is hidden. Two layers dissolving into each other cover only three quarters of
    /// what one layer covers at the half-way point. The quarter that shows through is the flat ground. That
    /// is visible as a step at the picture's foot, because the picture stacks a second, dipping layer on
    /// top of the wash there. A full-window blur has no shape in it, so nothing can be seen to snap when
    /// the old one is finally dropped.</para>
    /// </summary>
    /// <param name="handOver">Whether this is the same photograph's ground, drawn again for a new window,
    /// rather than the ground for another record. If so, it goes up UNDERNEATH at full strength, and the
    /// old copy is faded off its top, as the picture above it is handed over (see <see cref="ShowBackdrop"/>). It is not
    /// dissolved in over two seconds.
    ///
    /// <para>This parameter exists because of what the two-second dissolve did on a resize. A wash is drawn
    /// for one window and is never stretched into another. So a window that was made WIDER has a strip down
    /// its far side that the copy on screen does not reach. That strip shows only the ground colour, where
    /// a blurred photograph belongs. Fading the new copy in from nothing left that strip empty for most of
    /// two seconds, after the resize had already finished. When it is handed over, the strip is covered the
    /// moment the pair lands. Only the old copy leaves, from over the top of a complete one.</para></param>
    private void ShowWash(LoadedBackdrop art, bool handOver)
    {
        if (art.Wash is null)
            return;   // No blurred copy. The ground colour under it carries the window on its own.

        LandPair(WashA, WashB);

        var showing = _washOnA ? WashA : WashB;
        if (ReferenceEquals(showing.Source, art.Wash) && showing.Opacity > 0.98)
            return;   // Already up. Dissolving a picture into itself would only dip for no reason.

        var incoming = _washOnA ? WashB : WashA;
        var outgoing = _washOnA ? WashA : WashB;
        _washOnA = !_washOnA;

        Hide(incoming);        // Set to nothing BEFORE it is given the wash. See the note in ShowBackdrop.
        incoming.Source = art.Wash;
        _washWindowFor[incoming] = art.WashWindow;
        PlaceWash(incoming);   // At the same scale as the picture that it is the ground for.

        // The ground gets its own line, beside the picture's line. The way it went up cannot be seen on a
        // dark photograph. A wash that is nearly black looks the same whether it fades in or not. The
        // window it was drawn for is the number that shows whether a resize left a strip of it bare.
        RPM.Core.Diagnostics.Scope.Said(
            "art",
            $"wash {(handOver ? "handed over" : "dissolved in")}, covers "
          + $"{art.WashWindow.Width:F0}x{art.WashWindow.Height:F0} of window "
          + $"{art.Baked.WindowWidth:F0}x{art.Baked.WindowHeight:F0}");

        if (handOver)
        {
            Canvas.SetZIndex(incoming, 0);
            Canvas.SetZIndex(outgoing, 1);
            DissolveOf(incoming).Set(1);

            Fade(outgoing, 0, HandOverDuration, () =>
            {
                Canvas.SetZIndex(outgoing, 0);
                DropWash(outgoing);
            });
            return;
        }

        Canvas.SetZIndex(incoming, 1);
        Canvas.SetZIndex(outgoing, 0);

        Fade(incoming, 1, FadeDuration, () =>
        {
            Canvas.SetZIndex(incoming, 0);
            DropWash(outgoing);
        });
    }

    /// <summary>Takes the wash and its veil down with the rest of the artwork.</summary>
    private void ClearWash()
    {
        LandPair(WashA, WashB);

        var showing = _washOnA ? WashA : WashB;
        var other = _washOnA ? WashB : WashA;
        Hide(other);
        other.Source = null;

        Fade(showing, 0, WashDuration, () => DropWash(showing));
        Fade(WashVeil, 0, WashDuration);
    }

    /// <summary>Which of the two wash layers is the visible one.</summary>
    private bool _washOnA;

    /// <summary>Empties a wash layer once it is no longer on screen. This keeps the rule that a hidden
    /// layer is empty. The check is made when the fade lands, not when it starts. A swap that happened in
    /// between may have made this layer visible again, and emptying it then would take the ground out from
    /// under the picture.</summary>
    private void DropWash(Image layer)
    {
        if (ReferenceEquals(layer, _washOnA ? WashA : WashB) && layer.Opacity > 0.02)
            return;

        Hide(layer);
        layer.Source = null;
    }

    /// <summary>Raised when the picture behind the whole window changes, when new artwork dissolves in or
    /// the last one fades out. It is not a geometry signal (<see cref="GeometryChanged"/> is that). This
    /// one says that the pixels behind the frosted cards are about to change for two seconds. The cached
    /// frost has to wait for that before it takes its plate again.</summary>
    public event Action? BackdropChanged;

    /// <summary>Crossfades the colour that the whole window stands on. This is the new backdrop's dominant
    /// colour. It travels from the old colour over the same fade as the pictures, so the colour arrives
    /// WITH the artwork. The composite's feathered edges dissolve into it, and the content below the band
    /// sits on it.</summary>
    private void SyncDominant(Windows.UI.Color colour)
    {
        // Only when the colour actually changes. This is the same reason SyncStage has a guard. A re-cut of
        // the picture on screen has the picture's own colours, so this method is often asked for a colour
        // it is already showing. Starting a new dependent animation over one that holds its end value makes
        // the property revert to its BASE for a frame. The base here is the shell's navy. That would show
        // one frame of navy across the whole window on every resize.
        if (colour == _groundColour)
            return;
        _groundColour = colour;

        // The colour arrives already darkened (GroundDim). That is one dimming step, in one place, so the
        // value the library stands on can be read from that constant and is not the product of two steps.

        // The colour fades are linear. Uno does not implement ColorAnimation.EasingFunction, so an easing
        // set here would be dropped. Over a fade this short the curve is hardly seen anyway.
        // DoubleAnimation's easing does work, which is why the opacity fades further down use it.
        var fade = new ColorAnimation
        {
            To = colour,
            Duration = new Duration(FadeDuration),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(fade, DominantBrush);
        Storyboard.SetTargetProperty(fade, "Color");

        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        Services.Shell.Storyboards.Play(storyboard);
    }

    // Invariant: a hidden layer is empty
    //
    // A layer that is not the visible one must hold nothing, so nothing older can be revealed behind its
    // transparency. Each fill stamps the layer with a rising id. When a fade-out finishes, the layer is
    // emptied only if it has not been filled again since (a newer swap made it the incoming one) and it is
    // at zero opacity. That guard is what makes a quick second swap safe.

    private int _fillSeq;
    private readonly Dictionary<UIElement, int> _backdropFill = new();
    private readonly Dictionary<UIElement, int> _coverFill = new();

    private void FadeOutAndClearBackdrop(FrameworkElement layer, Image main, TimeSpan? duration = null)
    {
        var expected = _backdropFill.TryGetValue(layer, out var f) ? f : 0;
        Fade(layer, 0, duration, () =>
        {
            if (_backdropFill.TryGetValue(layer, out var now) && now != expected)
                return;                       // Filled again as the new incoming layer. Leave it.
            if (layer.Opacity > 0.02)
                return;                       // Visible again. Not ours to clear.
            main.Source = null;
        });
    }

    private void FadeOutAndClearCover(Image layer)
    {
        var expected = _coverFill.TryGetValue(layer, out var f) ? f : 0;
        Animate(layer, 0, () =>
        {
            if (_coverFill.TryGetValue(layer, out var now) && now != expected)
                return;
            if (layer.Opacity > 0.02)
                return;
            layer.Source = null;
        });
    }

    // Colour: what the picture stands on

    /// <summary>
    /// The picture's dominant colour, as it is in the photograph: what the fill beside it fades into. The
    /// colours are counted in coarse buckets, and the biggest bucket wins. That is what a person would call
    /// "the colour of that photo". It is not darkened. The callers darken it as needed.
    /// </summary>
    private static Windows.UI.Color? DominantColour(byte[] imageBytes) => CoverColor.Dominant(imageBytes);

    /// <summary>Towards black, keeping the hue: <paramref name="amount"/> of 1 leaves it alone.</summary>
    private static Windows.UI.Color Dim(Windows.UI.Color colour, double amount) => CoverColor.Dim(colour, amount);

    /// <summary>How far the scrim's colour is taken down: it is a ground for white text to sit on, not a
    /// wash of the photo.</summary>
    private const double ScrimDim = 0.42;

    /// <summary>
    /// The colour everything stands on: the picture's own average, held to a ground (see
    /// <see cref="CoverColor.Ground"/>). The average is used rather than an edge colour. An edge colour of
    /// the crop would paint the whole library with whatever is behind the artist's shoulder. A bright sky
    /// there would light up the entire app.
    /// </summary>
    private static Windows.UI.Color GroundColour(Windows.UI.Color? average)
        => average is { } colour ? CoverColor.Ground(colour) : DefaultScrim;

    /// <summary>The colours last sent to the two colour fades. They are stored here, not read off the
    /// brushes, for the same reason as <see cref="_stageTarget"/>. A dependent animation in flight leaves
    /// the property at its old value, so the brush cannot say where it is going.</summary>
    private Windows.UI.Color _groundColour, _scrimColour;

    /// <summary>The dark ground used when a picture has no colour to give.</summary>
    // Idle ground: black, so with no artwork the band's foot and the scrim fade to black over the backdrop,
    // rather than to navy.
    private static readonly Windows.UI.Color DefaultScrim = Windows.UI.Color.FromArgb(255, 0, 0, 0);

    /// <summary>
    /// Paints the dissolve beside the picture in the picture's own colour, keeping each stop's alpha. It is
    /// crossfaded, not switched. The colour of the outgoing photo moves to the colour of the incoming one,
    /// over the same fade that the pictures use.
    /// </summary>
    private void SyncScrimColour(Windows.UI.Color colour)
    {
        // The colour is unchanged, so there is nothing to move to. This is the same guard as in
        // SyncDominant. It saves more than a storyboard here. The tail is a baked bitmap, and giving the
        // rectangle a freshly built brush changes the screen, even when the colour is the same.
        if (colour == _scrimColour)
            return;
        _scrimColour = colour;

        GradientStop[] stops = ScrimStops;

        // The tail is a baked bitmap, so it cannot follow a colour animation. It is baked again, at the
        // colour that the ramp is moving TO. It therefore leads the fade by its full two seconds. This is
        // hardly seen in practice. The tail is a soft dark gradient below the band, and the picture above
        // it dissolves at the same time.
        ScrimTail.Background = BuildTail(ScrimRamp, colour);

        var storyboard = new Storyboard();

        foreach (var stop in stops)
        {
            var fade = new ColorAnimation
            {
                To = Windows.UI.Color.FromArgb(stop.Color.A, colour.R, colour.G, colour.B),
                Duration = new Duration(FadeDuration),
                EnableDependentAnimation = true,
            };

            Storyboard.SetTarget(fade, stop);
            Storyboard.SetTargetProperty(fade, "Color");
            storyboard.Children.Add(fade);
        }

        Services.Shell.Storyboards.Play(storyboard);
    }

    // Placement: where the picture sits in its slot

    /// <summary>
    /// Sizes and places both copies of the artwork: the framed picture in the band and the blurred one
    /// under the whole window. At rest each is exactly the rectangle it was drawn for and this is nothing
    /// but standing them in the window's top-right corner.
    ///
    /// <para>Between a resize and the fresh pair that follows it, it is the one thing keeping the header
    /// intact, and it does it by moving them rather than by resizing them, see <see cref="RightEdge"/> and
    /// the note on <see cref="PlaceLayer"/>.</para>
    /// </summary>
    private void PlaceBackdrop()
    {
        PlaceLayer(BackdropImageA);
        PlaceLayer(BackdropImageB);
        PlaceWash(WashA);
        PlaceWash(WashB);
    }

    /// <summary>
    /// The picture, at the size it was baked, standing against the window's right edge.
    ///
    /// <para><b>Nothing here scales.</b> A layer is drawn at the size it was made and moved, and that is
    /// the whole of the live placement.</para>
    /// </summary>
    /// <remarks>
    /// The picture is not scaled to cover the window during a drag. The picture is the height of the BAND,
    /// and the band does not change when the window is resized. Scaling it would shrink it inside a band
    /// that has not moved, and a strip of ground would show under it.
    /// </remarks>
    private void PlaceLayer(Image image)
    {
        if (_size.Width <= 0 || _size.Height <= 0)
            return;

        if (image.Source is not BitmapImage tile || tile.PixelWidth <= 0 || tile.PixelHeight <= 0)
            return;   // Nothing is in this layer yet.

        var baked = _backdropBakedFor.TryGetValue(image, out var was) ? was : default;

        Stand(image, RightEdge(baked.WindowWidth), tile.PixelWidth, tile.PixelHeight);
    }

    /// <summary>The blurred copy, placed by the same rule as the picture above it. It is sized by the
    /// window it was DRAWN for, not by the window of the picture. A wash outlives several bakes while it is
    /// still in register (see <see cref="Registers"/>). Sizing it by the newer bake would undo the
    /// registration that let it be kept.</summary>
    private void PlaceWash(Image image)
    {
        if (_size.Width <= 0 || _size.Height <= 0 || image.Source is null)
            return;

        var window = _washWindowFor.TryGetValue(image, out var was) ? was : default;
        if (window.Width <= 1 || window.Height <= 1)
            return;

        Stand(image, RightEdge(window.Width), window.Width, window.Height);
    }

    /// <summary>
    /// Where a layer's left edge goes, so that the window it was drawn for ends where the window ends now.
    /// The composition is pinned to the window's right edge.
    /// </summary>
    /// <remarks>
    /// <para>Everything in the header is laid out from the right. The record's arc reaches in from the
    /// right, the picture is drawn right-aligned against the record, and the dissolves run leftwards, away
    /// from the record. So the right edge is the point the composition holds still on. If the layers were
    /// pinned by their LEFT edge, a window narrowed by 300 points would move the picture 300 points to the
    /// right of where it belongs. A face would slide under the record until the fresh bake arrived.</para>
    ///
    /// <para>ONE rule for every layer, off each layer's OWN drawn window: the sharp picture and its blurred
    /// ground are two halves of one image and whatever moves one has to move the other by the same amount,
    /// or the registration that hides the picture's edges is gone. Because nothing is scaled, that amount
    /// is the same number for both and they stay registered to the pixel through any change of
    /// width.</para>
    ///
    /// <para>Positive as readily as negative. A layer wider than the window is pulled LEFT until its right
    /// edge is the window's right edge. A layer narrower than the window, which a window made wider since
    /// the bake produces, is pushed RIGHT for the same reason. Clamping at zero would be wrong. A window
    /// dragged from 1000 to 2400 points would leave the tile at the window's LEFT edge. The photograph
    /// would then appear over the track's title, a thousand points from the record it belongs
    /// against.</para>
    ///
    /// <para>A layer pushed right leaves a strip bare at the far side. That is the cost of not scaling. The
    /// two layers pay it differently. The wash is drawn for a canvas that reaches well past its own window
    /// (<see cref="WashReach"/>), so an ordinary resize uncovers none of it, and the window is covered by a
    /// blurred photograph all the way across. The tile is the band's picture, and it stays the size it was.
    /// What stands beside it is the same wash, which is where its edges dissolve to anyway. So a window
    /// that has grown since the bake reads as a picture that has not been re-cut yet, not as an
    /// edge.</para>
    ///
    /// <para>Zero before there is anything to measure. A bake made before the control has been laid out
    /// records a window of nothing. <see cref="SettledGeometryAsync"/> gives up after a couple of seconds
    /// and bakes for whatever it has. Standing a layer against that would put it off the screen.</para>
    /// </remarks>
    private double RightEdge(double drawnFor)
        => drawnFor > 1 ? _size.Width - drawnFor : 0;

    /// <summary>Places a layer against the window's right edge, at an exact size, with its top at the
    /// window's top. The band starts there. Both copies are placed by the same rule (see <see
    /// cref="RightEdge"/>). That keeps the whole composition together.</summary>
    private static void Stand(Image image, double left, double width, double height)
    {
        image.Stretch = Stretch.Fill;   // Width and height are both set here, at one scale. There is no distortion.
        image.HorizontalAlignment = HorizontalAlignment.Left;
        image.VerticalAlignment = VerticalAlignment.Top;
        image.Width = width;
        image.Height = height;
        image.Margin = new Thickness(left, 0, 0, 0);
    }

    /// <summary>What each backdrop layer was baked for, so the layer on screen and the one dissolving out
    /// under it are each mapped by their OWN change rather than by the newest bake's.</summary>
    private readonly Dictionary<Image, BakedGeometry> _backdropBakedFor = new();

    /// <summary>The window that each wash layer was drawn for. For the wash, this plays the same role that
    /// the baked geometry plays for the picture.</summary>
    private readonly Dictionary<Image, Windows.Foundation.Size> _washWindowFor = new();

    // Recrop: the strip changed shape

    /// <summary>
    /// A resize can mean that the picture must be cut again. A drag can be a hundred resizes, and each
    /// recrop runs the model. So this waits for the drag to settle, and then asks the manager once.
    /// </summary>
    private void ScheduleRecrop()
    {
        _recropTimer ??= new DispatcherTimer { Interval = RecropSettle };

        _recropTimer.Stop();
        _recropTimer.Tick -= OnRecropTick;
        _recropTimer.Tick += OnRecropTick;
        _recropTimer.Start();
    }

    private void OnRecropTick(object? sender, object e)
    {
        _recropTimer?.Stop();

        // Only ask the manager when the geometry has drifted enough to change what the pictures should be.
        // A shape seen before is free anyway, because the cache is keyed by shape. A re-show that can be
        // skipped is still a request that must be serialized.
        if (_bakedFor.Band <= 0)
            return;
        if (!_bakedFor.Drifted(LiveGeometry, RecropThreshold))
            return;

        Controller?.NotifyReshaped();
    }

    private DispatcherTimer? _recropTimer;

    /// <summary>How long the strip has to hold still before the picture is cut again.</summary>
    private static readonly TimeSpan RecropSettle = TimeSpan.FromMilliseconds(250);

    /// <summary>How much the geometry has to change before the pictures are worth making again.</summary>
    private const double RecropThreshold = 0.02;

    /// <summary>
    /// The geometry as it is right now: what a bake would be made for, and what the pictures on screen are
    /// checked against.
    ///
    /// <para>Derived from the band the artwork is FRAMED for (see <see cref="PictureBand"/>) and the
    /// record's reach at that band, not from the live chrome. The chrome's band and its record move for
    /// reasons the picture has no business following: the drawer parks, the drawer slides, the disc grows
    /// with it. If any of these counted as a change in the artwork's geometry, each one would cost a
    /// re-bake, and the listener could see it happen.</para>
    /// </summary>
    private BakedGeometry LiveGeometry => new(
        Math.Max(1, _size.Width - RecordInsetFor(ArtBand)),
        PictureBand,
        Math.Max(1, _size.Width),
        Math.Max(1, _size.Height));

    /// <summary>Whether the geometry is worth baking against at all: the control laid out, and a band to
    /// frame to. Before the first layout pass there is neither, and a picture baked then is framed to the
    /// whole window.</summary>
    private bool GeometryReady => _size.Width > 1 && _size.Height > 1 && ArtBand > 1;

    /// <summary>How long the geometry has to hold still before it is worth baking a picture against. Same
    /// settle the recrop uses: one number for "stopped moving".</summary>
    private bool GeometrySettled => GeometryReady && _sinceLayout.Elapsed >= RecropSettle;

    /// <summary>Restarted on every layout pass, so a load can tell whether the header is still
    /// moving.</summary>
    private readonly Stopwatch _sinceLayout = Stopwatch.StartNew();

    /// <summary>
    /// Waits for the header to stop moving, then reports the geometry to bake against.
    ///
    /// <para>The artwork is drawn ONCE, when things have settled. If it were baked against the geometry at
    /// the moment the load started, and corrected afterwards, the picture would arrive and then visibly
    /// arrive again. The drawer animates open during the first 400 ms of every track. The window may also
    /// be dragged for much longer than that.</para>
    ///
    /// <para>The wait is bounded. A drag that goes on for a long time still gets a picture, baked for
    /// wherever the header is. The recrop timer corrects it when the drag ends.</para>
    /// </summary>
    private async Task<BakedGeometry> SettledGeometryAsync(CancellationToken ct)
    {
        for (var i = 0; i < SettleTries && !GeometrySettled; i++)
            if (!await Cancellable.DelayAsync(SettleTick, ct))
                break;

        return LiveGeometry;
    }

    private static readonly TimeSpan SettleTick = TimeSpan.FromMilliseconds(60);
    private const int SettleTries = 40;   // About 2.4 s, then bake regardless

    /// <summary>The band the ARTWORK is framed for, which is not always the one on screen, see
    /// <see cref="PictureBand"/>, which adds the library card's gap to it.</summary>
    private double ArtBand =>
        _idle && OpenBandHeight > 0 ? OpenBandHeight
        : _band > 0 ? _band
        : _size.Height;

    /// <summary>How far the record's arc reaches in from the right at a given band. It uses the same
    /// geometry that <see cref="Layout"/> uses for the live disc, as a pure function. So the artwork can be framed for a
    /// band that is not the one on screen.</summary>
    private double RecordInsetFor(double band)
    {
        // There is no disc on the band (see ShowRecord), so nothing reaches in from the right, and the slot
        // is the whole window. The answer is given here, not at each of the four places that need it. This
        // function knows what the record takes. A caller that subtracts zero itself would be a second copy
        // of the same rule.
        if (!RecordShown)
            return 0;

        var radius = Math.Min(MaxRecordDiameter, Math.Max(1, band * RecordScale)) / 2;
        var half = band / 2;
        var inset = radius > half ? Math.Sqrt((radius * radius) - (half * half)) : 0;
        return Math.Min(inset, _size.Width * 0.8);
    }

    /// <summary>The plain margin between the photograph's right edge and the window's, for a band with no
    /// record on it. It is not added on top of the disc's own reach. See <see
    /// cref="PictureBreath"/>.</summary>
    private const double RecordBreathGap = 26;

    /// <summary>
    /// How far short of the slot's right edge the photograph stops. It is nothing while there is a record
    /// on the band. The picture runs to the end of its slot, and the disc is drawn over the corner it
    /// reaches.
    /// </summary>
    /// <remarks>
    /// <para>Keeping a gap from the disc's widest reach would leave a wedge of empty ground beside the
    /// record, widest at the band's top and foot. The listener would see that wedge as wasted picture. So
    /// the picture is not cut back for the disc.</para>
    ///
    /// <para>The slot already ends where it should. <see cref="RecordInsetFor"/> puts its right edge at the
    /// x where the disc's arc crosses the band's TOP and FOOT. So a picture that fills the slot meets the
    /// disc exactly at those two corners, and passes behind it. At the middle it goes 28 points behind the
    /// disc, where the disc bulges furthest in. An opaque disc over a photograph is not crowding. It is
    /// what a record laid on a photograph looks like. The edge itself is hidden by the same disc, which is
    /// why the compositor stops dissolving it (see <see cref="RecordOnTheBand"/>).</para>
    ///
    /// <para>With no record there is nothing to lap over anything, so what is left is the plain margin off
    /// the window's right edge.</para>
    /// </remarks>
    private double PictureBreath => RecordShown ? 0 : RecordBreathGap;

    /// <summary>
    /// The shape of the backdrop's slot: everything left of the record, over the header band.
    ///
    /// <para>This is not capped. On a wide monitor the shape can be about eight to one. A crop that cut the
    /// photo to this shape would ruin it, because nothing survives being cut to a thin slit. So the frame
    /// is sized by the subject, and only opened out towards this shape (see <see
    /// cref="RPM.Core.Art.SaliencyCrop.Fit"/>). A wide slot then asks for as much of the photograph as
    /// there is around the subject. The wash fills the rest.</para>
    /// </summary>
    private double BackdropAspect()
    {
        // The picture's own room: the slot, less the record's breathing space. The frame is chosen for this
        // shape. If the tile's whole width were asked for, the frame would come back too wide for the room
        // it is drawn into. It would then have to be trimmed, or squashed.
        var geometry = LiveGeometry;
        var width = Math.Max(1, geometry.SlotWidth - PictureBreath);
        return width / geometry.Band;   // The subject is framed to the artwork's zone
    }

    /// <summary>
    /// How far past its own resolution a photograph may be enlarged to fill the band.
    ///
    /// <para>The frame is as tall as its subject. So a small photograph of a small subject is enlarged to
    /// fill a band 200 px tall. A backdrop is feathered and sits under a scrim, so it forgives a great deal
    /// of softness. A soft picture that fills the band is better than a sharp one with a strip of ground
    /// over it. This limit is there to stop absurd enlargement. It does not trade fill for
    /// sharpness.</para>
    /// </summary>
    private const double MaxBackdropUpscale = 3.0;

    /// <summary>The zoom that makes a framed window full height in the band. This is the main job. The
    /// upscale limit only stops absurd enlargement. It does not accept a short picture.</summary>
    private static double FillZoom(RPM.Core.Art.CropRect window, double band)
        => window.Height <= 0 ? 1 : Math.Min(band / window.Height, MaxBackdropUpscale);

    // The crop

    /// <summary>
    /// Frames the photo for the header: the window that holds the whole subject at the largest zoom the
    /// band can stand it at. The choice is <see cref="SaliencyCrop.Fit"/>'s. Here it is scaled back up to
    /// the source's own pixels for the compositor. Null on any decode failure, and the caller then falls
    /// back to the original bytes. The <paramref name="scene"/> tells the crop where the cover and the
    /// record obstruct, so that it can place the subject in the clear.
    /// </summary>
    private async Task<(byte[]? Bytes, CropFrame Frame, int SourceWidth, int SourceHeight)> RenderCropAsync(byte[] imageBytes, double aspect, ArtScene scene)
    {
        try
        {
            using var src = SkiaSharp.SKBitmap.Decode(imageBytes);
            if (src is null || src.Width < 8 || src.Height < 8)
                return (null, default, 0, 0);

            // From here on, the picture's own size is known, and it is returned whatever else fails. A
            // hand-made framing is stated in those two numbers and nothing else. So a saliency pass that
            // cannot run must not also remove the ability to honour a window that somebody placed.
            var (map, mapW, mapH) = await ScoreAsync(src);
            if (map is null)
                return (null, default, src.Width, src.Height);

            var faces = await DetectFacesAsync(src, mapW);

            var frame = SaliencyCrop.Fit(map, mapW, mapH, aspect, faces, scene);
            if (frame.Rect.Width <= 0 || frame.Rect.Height <= 0)
                return (null, default, src.Width, src.Height);

            // Scale back up to the source's own pixels. The photo is not cut to this size. The source bytes
            // are passed on untouched, and the compositor draws this window from them (see
            // RenderComposite). So the frame is all the stage needs.
            var scaleX = (double)src.Width / mapW;
            var scaleY = (double)src.Height / mapH;

            var rect = ToSource(frame.Rect, scaleX, scaleY, src.Width, src.Height);
            if (rect.Width < 8 || rect.Height < 8)
                return (null, default, src.Width, src.Height);

            return (imageBytes,
                    new CropFrame(rect,
                                  ToSource(frame.Content, scaleX, scaleY, src.Width, src.Height),
                                  ToSource(frame.Keep, scaleX, scaleY, src.Width, src.Height),
                                  (int)Math.Round(frame.EyeLine * scaleY)),
                    src.Width, src.Height);
        }
        catch
        {
            return (null, default, 0, 0);
        }
    }

    /// <summary>A box in the saliency map's pixels, in the source photograph's own.</summary>
    private static CropRect ToSource(CropRect box, double scaleX, double scaleY, int width, int height)
    {
        var rect = SkiaSharp.SKRectI.Intersect(
            SkiaSharp.SKRectI.Create(
                (int)Math.Round(box.X * scaleX),
                (int)Math.Round(box.Y * scaleY),
                (int)Math.Round(box.Width * scaleX),
                (int)Math.Round(box.Height * scaleY)),
            SkiaSharp.SKRectI.Create(width, height));
        return new CropRect(rect.Left, rect.Top, rect.Width, rect.Height);
    }

    /// <summary>
    /// The faces in a picture, in the saliency map's coordinates. Empty when the detector is missing or
    /// there is nobody in the photo, and the crop then falls back to framing the subject as a whole.
    /// </summary>
    private async Task<IReadOnlyList<FaceBox>> DetectFacesAsync(SkiaSharp.SKBitmap src, int mapSize)
    {
        if (_faces is not { } detector)
            return [];

        var (w, h) = FaceService.InputSize;

        var info = new SkiaSharp.SKImageInfo(w, h, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul);
        using var small = src.Resize(info, new SkiaSharp.SKSamplingOptions(SkiaSharp.SKFilterMode.Linear, SkiaSharp.SKMipmapMode.Linear));
        if (small is null)
            return [];

        // Only the boxes are used. This crop frames whatever it is given. A photograph with nobody in it
        // and a machine with no detector are the same case here. See FaceScan for where the two are told
        // apart.
        return (await detector.DetectAsync(small.GetPixelSpan().ToArray(), mapSize)).Faces;
    }

    /// <summary>
    /// The saliency map for a picture. It comes from the U2-Netp model when that model is present.
    /// Otherwise it uses local contrast. The fallback finds detail rather than the subject, so it is a
    /// lower-quality result, not an equal alternative.
    /// </summary>
    private async Task<(double[]? Map, int Width, int Height)> ScoreAsync(SkiaSharp.SKBitmap src)
    {
        const int size = SaliencyService.Size;

        var info = new SkiaSharp.SKImageInfo(size, size,
                                             SkiaSharp.SKColorType.Rgba8888,
                                             SkiaSharp.SKAlphaType.Unpremul);

        using var square = src.Resize(info, new SkiaSharp.SKSamplingOptions(SkiaSharp.SKFilterMode.Linear, SkiaSharp.SKMipmapMode.Linear));
        if (square is null)
            return (null, 0, 0);

        var rgba = square.GetPixelSpan().ToArray();

        if (_saliency is { } saliency && await saliency.ComputeAsync(rgba) is { } model)
            return (model, size, size);

        return (SaliencyCrop.ClassicalSaliency(rgba, size, size), size, size);
    }

    // Where the picture goes: worked out once, drawn twice

    /// <summary>
    /// The picture's placement: which pixels of the photograph are on show, at what scale, and where in the
    /// tile they land.
    ///
    /// <para>This is computed once and handed to BOTH renderers: the sharp picture in the band, and the
    /// blurred copy under the whole window. The two must be REGISTERED to the pixel. That registration is
    /// the whole edge strategy. Where the sharp picture dissolves away, it dissolves into the same
    /// photograph, at the same scale, in the same place, only out of focus. There is no colour difference
    /// across the boundary for a fade to show. So the fade becomes a change in SHARPNESS alone, not a
    /// change between two surfaces.</para>
    /// </summary>
    public readonly record struct PicturePlacement(
        double Scale, double SrcX, double SrcY, double SrcW, double SrcH,
        float DestX, float DestY, float DestW, float DestH);

    /// <summary>
    /// How big the picture is drawn, and which part of the frame that leaves on show.
    ///
    /// <para>ONE scale is used for both axes. A squashed face is worse than a small one. Three numbers
    /// decide the scale, in this order:</para>
    /// <list type="bullet">
    /// <item>the SUBJECT: the size a face is drawn at in the band, which is the same on every track. It is
    /// the rule wherever the photograph can carry the header at that size.</item>
    /// <item>fill HALF the header: the header is ten times wider than it is tall, and a photograph with no
    /// picture either side of its subject stops where the photograph stops, a small picture in a long empty
    /// room. Under <see cref="MinBandCover"/> the subject gives way and the picture is zoomed until it
    /// covers that much.</item>
    /// <item>fill the BAND, and no mush: the picture's height IS the band's, and no photograph is magnified
    /// past <see cref="MaxBackdropUpscale"/> to reach any of the above.</item>
    /// </list>
    ///
    /// <para>What the floor costs is the frame's edges and, on a face big enough to bind, the face's own
    /// clearance: the hair leaves by the top of the window and the chin goes down into the foot dissolve.
    /// See the comments on the zoom for the trade-off.</para>
    /// </summary>
    internal static PicturePlacement PlacePicture(CropFrame frame, int sourceHeight,
                                                  double slotWidth, double band, double breath)
    {
        var win = frame.Rect;
        var pictureRight = (float)Math.Max(8, slotWidth - breath);
        var pictureRoom = Math.Max(8.0, pictureRight);

    // How big the subject is drawn
    //
    // Both parts of that answer come from ChooseZoom. The rule is kept in its own method, because the bake
    // asks the same question again about a framing it already holds (see HeldFraming).
        var scale = ChooseZoom(frame, slotWidth, band).Scale;

        // Which part of the frame is on show at that scale. All of it is shown until the zoom passes the
        // point where it fits. After that, only a window into it is shown. The window is centred across the
        // people and hung on the eye line, going down.
        var shownH = Math.Min((double)win.Height, band / scale);
        var shownW = ShownWidth(frame, scale, shownH, pictureRoom, slotWidth, band);

        // Vertically: the eyes sit a little above the middle of the band. That is where a camera operator
        // usually puts them. Dead centre looks like a passport photograph, and lower puts too much forehead
        // in view. The cost is the top of the hair, which is the least important part of a photograph of a
        // person. The window never goes so far that a face leaves it, though. The keep box runs from brow
        // to chin, and both of its edges are held.
        var shownY = (double)win.Y;
        if (frame.EyeLine > 0)
        {
            var chin = frame.Keep.Y + frame.Keep.Height;

            // Two rules apply, and the one that puts the face HIGHER wins. A small face has room to spare,
            // so it takes the eye line. A big face is pushed up until its chin clears the dissolve. The
            // scale above has already made sure that this is possible without losing the brow.
            shownY = Math.Max(frame.EyeLine - (shownH * EyeLineInBand),
                              chin - (shownH * FaceFoot));

            // Held against the FACE, not the frame. The frame's top is the top of the hair. Clamping to it
            // kept the faces low in the band. The window could never start below the hairline, so the hair
            // stayed in, and the face sat down in the dissolve and went soft. The hair is allowed to leave
            // by the top of the window instead. What is held is the headroom above the brow, the chin
            // inside the window, and the photograph's own edges, because there is nothing outside them to
            // show.
            shownY = Math.Min(shownY, frame.Keep.Y - (shownH * Headroom));
            shownY = Math.Max(shownY, chin - shownH);
            shownY = Math.Clamp(shownY, 0, Math.Max(0, sourceHeight - shownH));
        }

        // Where that window sits. After the narrowing above, it is the middle of the face. The clamps
        // handle the cases that the narrowing does not reach.
        var middle = frame.EyeLine > 0
            ? frame.Keep.X + (frame.Keep.Width / 2.0)
            : frame.Content.X + (frame.Content.Width / 2.0);
        var shownX = Math.Clamp(middle - (shownW / 2), win.X, win.X + win.Width - shownW);
        // The trim must not reach a head. The scale already makes sure that the heads fit in shownW. This
        // only decides where the window sits.
        shownX = Math.Min(shownX, frame.Keep.X);
        shownX = Math.Max(shownX, frame.Keep.X + frame.Keep.Width - shownW);
        shownX = Math.Clamp(shownX, win.X, win.X + win.Width - shownW);

        var sw = (float)(shownW * scale);
        var sh = (float)(shownH * scale);
        // Top-aligned, not centred. A picture that fills the band lands in the same place either way. A
        // picture that cannot fill it, because it is too small to magnify that far, keeps its ground at the
        // foot. There the tile dissolves into the library anyway. Centring would split it into two strips,
        // with one of them across the top of the window.
        return new PicturePlacement(scale, shownX, shownY, shownW, shownH,
                                    pictureRight - sw, 0, sw, sh);
    }

    /// <summary>
    /// How big the subject is drawn, and which of the two rules said so: what the BAND asks for (the
    /// subject at the size it is drawn on every track) and what the ROOM asks for (the floor under how much
    /// of the header the picture covers).
    /// </summary>
    /// <remarks>
    /// Lifted out of <see cref="PlacePicture"/> so the bake can put the same question to a framing it
    /// already holds. A zoom the room chose is only that zoom at that width, and a resize has to frame the
    /// photograph again rather than re-fit it, see <see cref="PictureZoom.ByRoom"/> and
    /// <see cref="HeldFraming"/>.
    /// </remarks>
    internal static PictureZoom ChooseZoom(CropFrame frame, double slotWidth, double band)
    {
        var win = frame.Rect;

        // The subject stands at this size in the band. This size decides the zoom wherever the photograph
        // can carry the header at it. The choice is the same subject size on every track. The wash carries
        // whatever the photograph cannot reach.
        //
        // The two goals cannot both be met. A photograph has a fixed number of pixels. Covering a maximised
        // header at the size the band wants for the faces needs more pixels than most photographs have. So
        // either every picture fills the header, and the faces come out at a different size on every track.
        // Or the faces are the same size on every track, and some pictures stop short. The rule below sets
        // where that trade stops. A picture that would stop short of HALF the header gives up the subject
        // size, not the header (see the floor, and MinBandCover). Between the two, this is the rule.
        //
        // The arithmetic for a face is written down here, because it is not obvious that it pays. The
        // picture always fills the band. So its width and the rows visible down it are the same quantity,
        // seen twice. If the width is reduced by a factor k, the scale falls by k. The window then shows
        // 1/k as many rows of the photograph in the same space. Everything in the picture moves TOWARDS the
        // point where the placement is pinned, by its distance from that point times (1 - k). The chin
        // rises, and at the same time the hair comes down.
        //
        // Which two things are fixed is the whole design. If the eyes are fixed at EyeLineInBand and the
        // chin is fixed at the dissolve, the brow has no say at all. It lands at E - p(D - E)/(1 - p),
        // which is around a quarter of the band for a normal face. That leaves a lot of headroom over a
        // cropped subject. So the eye line gives way. The two values that are fixed are the HEADROOM above
        // the brow and the CHIN above the dissolve. The face's own height decides the rest.
        //
        //     rows on show   h = F / (D - h0)              F  the face, brow to chin
        //     scale          s = B (D - h0) / F            B  the band       D  where the dissolve starts
        //     window top     Y = brow - h0 · h             h0 the headroom
        //
        // The eyes then land at h0 + p(D - h0). That is about a third of the way down, which is where a
        // camera operator would put them. The value is arrived at, not declared.
        //
        // With nobody in the picture there is no face to hold and the subject fills the band instead, which
        // is the same rule with the whole of the keep box standing in for the face.
        var keepDeep = Math.Max(1, frame.Keep.Height);
        var subject = frame.EyeLine > 0
            ? band * (FaceFoot - Headroom) / keepDeep
            : band / keepDeep;

        // …and a FLOOR under how much of the header the picture ends up covering, which is the one thing
        // about the room the subject's size does not already answer.
        //
        // The subject size on its own is not enough. If the size is held fixed, the drawn width is the
        // band, times the subject's share of it, times the frame's width divided by the subject's height.
        // Three of those four values are fixed. So what is left is the shape of the photograph itself.
        // Across a maximised header, that varied widely from one photograph to the next. A tight portrait
        // has no picture on either side of its subject to reach with. So it stopped where the photograph
        // stopped, and it came out small in a corner.
        //
        // So below MinBandCover, the subject gives way, and the picture is zoomed until it covers that
        // much. The cost: on those photographs the subject comes out larger than on the photographs that
        // reach. A face that is big enough to bind is cropped, with the hair off the top and the chin down
        // in the foot dissolve. The subject rule exists to keep that clearance. The cost is paid only where
        // the alternative is a picture in the corner of an empty header.
        var cover = win.Width > 0 ? MinBandCover * slotWidth / win.Width : 0;

        // There are two ceilings. Both are about the photograph, not the header. The band has to be
        // covered. Below that size the frame is already fully on show, and a smaller picture moves nothing.
        // It only leaves ground above and below it. No picture is enlarged past MaxBackdropUpscale, because
        // a subject at the size the band wants would be blurred. The upscale ceiling outranks the floor.
        // This is the one place where the rules disagree. A frame too small to reach across the header,
        // however far it is enlarged, can only become a larger area of blur, and blur covers nothing. So it
        // stays under the floor, and it stays legible.
        return new PictureZoom(
            Math.Clamp(Math.Max(subject, cover), FillZoom(win, band), MaxBackdropUpscale), subject, cover);
    }

    /// <summary>
    /// The zoom, and the two numbers it was chosen from.
    /// </summary>
    /// <param name="Scale">The one that won, after the band-fill floor and the upscale ceiling.</param>
    /// <param name="Subject">What the band asks for: the size the subject is drawn at, which is the same
    /// number at every width of header.</param>
    /// <param name="Cover">What the room asks for: the zoom at which the picture covers
    /// <see cref="MinBandCover"/> of the header. It is a share of the ROOM, so it moves with it.</param>
    internal readonly record struct PictureZoom(double Scale, double Subject, double Cover)
    {
        /// <summary>Whether the ROOM chose this zoom: the floor won and survived the clamps. Such a framing
        /// is right only for a header of the width it was made for; a zoom the band chose is the same
        /// number at every width, which is what a re-fit holds.</summary>
        internal bool ByRoom => Cover > Subject && Alike(Scale, Cover, 0.0005);
    }

    /// <summary>
    /// How much of the frame is on show across the header: the second half of a placement, and the only
    /// half the room has a say in.
    /// </summary>
    /// <remarks>
    /// <para>Lifted out of <see cref="PlacePicture"/> so that a re-fit can ask it too (see
    /// <see cref="RefitPicture"/>). The size the subject is drawn at is a fact about the photograph and the
    /// band. A header of any width asks this one question: how much of the photograph to put beside the
    /// subject.</para>
    ///
    /// <para>ACROSS the frame, the face goes at the MIDDLE of what is drawn, and width is given up to put
    /// it there. Sliding the window is not enough, and the reason matters. The detector is not the missing
    /// part. It has already run on this picture, and Keep is its answer. What reaches here is usually the
    /// photograph's full width. The crop opens the content out to the header's shape, and the header is
    /// much wider than it is tall. So the frame runs out of photograph long before it runs out of slot, and
    /// it keeps every column there is. A window as wide as its own frame has nowhere to slide. So the face
    /// stays where the photographer put it. For a portrait with the subject to one side, that is off to one
    /// side of the header.</para>
    ///
    /// <para>So the window is NARROWED instead. A face can be centred in a window that is at most twice its
    /// distance from the nearer edge of the photograph. Any wider, and there is no picture on one side to
    /// balance the other. This costs picture at the far side of the photograph. The narrowing has two lower
    /// limits. It is never narrower than the share of the header that the picture must cover. It is also
    /// never so tight that the heads stop fitting. Between those two limits, the face decides the
    /// width.</para>
    /// </remarks>
    private static double ShownWidth(CropFrame frame, double scale, double shownH,
                                     double pictureRoom, double slotWidth, double band)
    {
        var win = frame.Rect;
        var shownW = Math.Min((double)win.Width, pictureRoom / scale);

        if (frame.EyeLine <= 0)
            return shownW;

        var faceCentre = frame.Keep.X + (frame.Keep.Width / 2.0);
        var toCentre = 2 * Math.Min(faceCentre - win.X, win.X + win.Width - faceCentre);

        var drawnH = shownH * scale;
        var keepsCover = drawnH > 0 && scale > 0
            ? MinBandCover * slotWidth * band / (drawnH * scale)
            : 0;

        shownW = Math.Max(Math.Min(shownW, toCentre), Math.Max(keepsCover, frame.Keep.Width));

        // Never wider than the room can draw. This is a limit of the floor, and it matters. Both numbers
        // above are floors: cover the header, and keep the heads. A header narrower than they ask for
        // cannot give either one. The picture would then be drawn wider than its own tile, and cut off at
        // the left. That is a hard edge, where every other edge dissolves. The subject's size is held now
        // (see the zoom above), so a narrow header gives up the far end of the photograph.
        return Math.Clamp(Math.Min(shownW, pictureRoom / scale), 1, win.Width);
    }

    // The same framing in a header of a different width

    /// <summary>
    /// The same picture in a slot of a different width: the placement it already has, let out or trimmed at
    /// its LEFT edge to the new room, with the zoom, the rows on show and the edge against the record all
    /// held.
    /// </summary>
    /// <remarks>
    /// <para>A window dragged wider or narrower changes every number that PlacePicture weighs. These are
    /// the room beside the record, the width a face can be centred in, and the area the picture has to
    /// cover. So asking for a fresh placement at the new width gives a different zoom on different rows.
    /// That is a different picture. Putting it up would mean dissolving the old one out and the new one in.
    /// That is a blink in the middle of a resize, for a header whose height never changed. The picture
    /// should keep its shape, fade, zoom and position while the window changes width.</para>
    ///
    /// <para>So the framing is decided once per band. The room only decides how much of it is on show. The
    /// picture is drawn right-aligned against the record (see the end of <see cref="PlacePicture"/>). The
    /// layers are placed against the window's right edge (<see cref="Stand"/>). So holding the window's
    /// RIGHT edge in the photograph holds every drawn pixel where it was. What changes is the far end. That
    /// end is inside the side dissolve, and it lands in the picture's own wash. There is nothing there for
    /// a fade to hide, so the swap has no fade. See
    /// <see cref="Trims"/>.</para>
    ///
    /// <para>How much is shown comes from <see cref="ShownWidth"/>, the same question a fresh placement
    /// asks. So a header reached by dragging shows as much of the photograph as one opened at that width
    /// would. What it does NOT take from a fresh placement is where the window sits across the frame. A
    /// fresh placement centres the face in what is drawn. The picture is placed against the record, so
    /// moving the window would slide the face across the header every time the window moved. The right edge
    /// is held instead. The extra room appears at the far end, where the picture is already dissolving into
    /// its own wash. So nothing the listener is looking at moves.</para>
    /// </remarks>
    internal static PicturePlacement RefitPicture(PicturePlacement was, CropFrame frame,
                                                  double slotWidth, double band, double breath)
    {
        if (was.Scale <= 0 || was.SrcW <= 0 || was.SrcH <= 0)
            return was;

        var pictureRight = (float)Math.Max(8, slotWidth - breath);
        var pictureRoom = Math.Max(8.0, pictureRight);

        var right = was.SrcX + was.SrcW;                        // Held: the edge that meets the record
        var want = ShownWidth(frame, was.Scale, was.SrcH, pictureRoom, slotWidth, band);
        var srcW = Math.Clamp(want, 1, right - frame.Rect.X);   // Also bounded by the photograph itself
        var sw = (float)(srcW * was.Scale);

        return was with { SrcX = right - srcW, SrcW = srcW, DestX = pictureRight - sw, DestW = sw };
    }

    /// <summary>
    /// Whether one placement is the other re-fitted: the same photograph at the same zoom, on the same
    /// rows, meeting the record at the same place, differing only in how far it reaches to the left.
    ///
    /// <para>What it allows is a swap with no dissolve at all. Every pixel the two share is the same pixel
    /// in the same place, so there is nothing to cross-fade. Cross-fading anyway would cause the blink that
    /// this check exists to prevent.</para>
    /// </summary>
    internal static bool Trims(PicturePlacement was, PicturePlacement now)
        => Alike(was.Scale, now.Scale, 0.001)
        && Alike(was.SrcY, now.SrcY, 0.5)
        && Alike(was.SrcH, now.SrcH, 0.5)
        && Alike(was.SrcX + was.SrcW, now.SrcX + now.SrcW, 0.5)
        && Alike(was.DestY, now.DestY, 0.5);

    private static bool Alike(double a, double b, double slack) => Math.Abs(a - b) <= slack;

    /// <summary>
    /// The framing this photograph is already held at, or null when the chooser has to be asked.
    /// </summary>
    /// <remarks>
    /// <para>Four things must hold. The BAND: the zoom is chosen against it, so a band that moved needs a
    /// new framing. The FRAMING ASKED FOR: saving a placement in the dialog, or taking one off, must put
    /// the new placement up. It must not re-fit the old one. The ROOM, twice, at the two places where it
    /// matters. These are a header this framing cannot reach across (<see cref="Carries"/>), and a header
    /// whose width this framing was not chosen for (<see cref="FramedForThisRoom"/>). Everything else about
    /// the room is answered by the re-fit.</para>
    ///
    /// <para>A re-fit holds the zoom and lets the picture out at its far end. So the widest it can ever be
    /// drawn is everything that was framed, at the zoom it was framed at. That is <see
    /// cref="FramedPicture.Reach"/>. When a window is maximised from a small one, the room more than
    /// doubles. Without a new framing, the re-fit would draw the small window's picture in a header that it
    /// covers only a quarter of. Under the floor, the picture is framed again for the new room. That puts
    /// it back to half the header.</para>
    ///
    /// <para>This is asked here, before the crop, and not on the placement afterwards. The crop is cut to
    /// the shape that the framing was made for. A frame opened out for a narrow header has fewer columns
    /// than one opened out for a wide header. So a re-framing decided after the crop would frame the wrong
    /// photograph. For that reason the reach is stored as a number.</para>
    /// </remarks>
    private FramedPicture? HeldFraming(string url, ArtFraming? framing, double band, double slotWidth)
    {
        if (!_framedAt.TryGetValue(url, out var was))
            return null;

        return was.Framing == (framing?.Stamp() ?? "auto")
            && SameSize(was.Band, band)
            && Carries(was.Reach, slotWidth)
            && FramedForThisRoom(was, slotWidth)
                ? was : null;
    }

    /// <summary>
    /// Whether a framing the ROOM chose is being asked for in the room it chose it for.
    /// </summary>
    /// <remarks>
    /// <para>The mirror of <see cref="Carries"/>. It is the same line, read from the other side. Carries
    /// catches a framing that the header has outgrown, which leaves too little picture to cover a wider
    /// room. This one catches a framing that the header has shrunk under. The floor is a share of the ROOM
    /// (<see cref="MinBandCover"/>). So a zoom that the floor chose is only that zoom at that width.
    /// Carrying it into a narrower header would draw the wide header's subject across a header that needs
    /// only half as much of it.</para>
    ///
    /// <para>A zoom the BAND chose is the same number at every width and is held by both questions: that is
    /// the whole point of a re-fit, and it is what keeps a drag from re-cutting the picture.</para>
    /// </remarks>
    internal static bool FramedForThisRoom(FramedPicture was, double slotWidth)
        => !was.ByRoom || SameSize(was.Room, slotWidth);

    /// <summary>Whether two laid-out numbers are the same number. A band and a room each come back a
    /// fraction different from one pass to the next; a picture is not re-framed for that.</summary>
    private static bool SameSize(double was, double now)
        => was > 0 && now > 0 && Math.Abs(now - was) / was < 0.005;

    /// <summary>What a re-fit holds, as one string for the bake's cache key: everything except the width,
    /// which is the one thing the room is allowed to change.</summary>
    private static string Stamp(PicturePlacement place)
        => $"{place.Scale:F3},{place.SrcY:F0},{place.SrcH:F0},{place.SrcX + place.SrcW:F0}";

    /// <summary>
    /// How a photograph is framed on the header right now, kept so a resize re-fits it instead of choosing
    /// again.
    /// </summary>
    /// <param name="Aspect">The shape the CROP was asked for. Held with the rest, because the placement's
    /// numbers are coordinates in the bitmap that crop produced: ask for another shape and the same numbers
    /// point somewhere else in a different picture.</param>
    /// <param name="Framing">The framing this was made under, a hand placement's stamp, or "auto".</param>
    /// <param name="Reach">The widest this framing can ever be drawn: everything the crop kept to the left
    /// of the picture's right edge, at the zoom it was framed at. A re-fit lets the picture out at its far
    /// end and never re-zooms, so this is the ceiling on what a re-fit can cover, and
    /// <see cref="HeldFraming"/> reads a header too wide for it as a re-framing.</param>
    /// <param name="ByRoom">Whether the ROOM chose the zoom rather than the band, see
    /// <see cref="PictureZoom.ByRoom"/>. Stated rather than worked out again later: the question is asked
    /// before the crop runs, and by then there is no frame to ask it of.</param>
    /// <param name="Room">The slot this was framed for, which is the width <see cref="ByRoom"/> is about.
    /// Meaningless when the band chose the zoom, and read by nothing in that case.</param>
    internal readonly record struct FramedPicture(
        double Band, double Aspect, PicturePlacement Place, string Framing, double Reach,
        bool ByRoom, double Room);

    private readonly Dictionary<string, FramedPicture> _framedAt = new(StringComparer.OrdinalIgnoreCase);

    private void RememberFraming(string url, ArtFraming? framing, double band, double aspect,
                                 PicturePlacement place, double reach, bool byRoom, double room)
    {
        if (place.Scale <= 0 || place.SrcW <= 0)
            return;

        // Small, and nothing in it is expensive: a handful of numbers per photograph. The whole dictionary
        // is cleared when it grows large, like the caches beside it. A clear costs one re-framing of
        // whatever is on screen the next time the window moves.
        if (_framedAt.Count > 64)
            _framedAt.Clear();

        _framedAt[url] = new FramedPicture(band, aspect, place, framing?.Stamp() ?? "auto", reach,
                                           byRoom, room);
    }

    // The placement that somebody made by hand

    /// <summary>
    /// Where a hand-made framing puts the picture. It is the counterpart of <see cref="PlacePicture"/>, but
    /// it does not weigh anything. The person has already looked at the header and said where the
    /// photograph goes. So only arithmetic is needed here.
    ///
    /// <para>It always FILLS the picture's room, because <see cref="ArtFraming"/>'s window is cut to the
    /// room's own shape (see its remarks for why). The one case that does not fill is a photograph too
    /// narrow to reach across at the zoom asked for. Then the window is capped by the picture's own width,
    /// and what is drawn stops short. It is right-aligned against the record, the same as the automatic
    /// placement, so the two look like the same header either way.</para>
    /// </summary>
    /// <summary>The shape a placement is made to: the picture's room over the band it stands in. Said once,
    /// because a framing read off a placement and a framing drawn back into one have to agree on it exactly
    /// or the two are not the same picture.</summary>
    internal static double PlacedAspect(double slotWidth, double band, double breath)
        => Math.Max(8.0, Math.Max(8, slotWidth - breath)) / Math.Max(1, band);

    internal static PicturePlacement PlaceFramed(ArtFraming framing, int sourceWidth, int sourceHeight,
                                                 double slotWidth, double band, double breath)
    {
        var pictureRight = (float)Math.Max(8, slotWidth - breath);

        var window = framing.Window(sourceWidth, sourceHeight, PlacedAspect(slotWidth, band, breath));
        var deep = Math.Max(1, band);

        // The band is filled by construction. The window is as tall as the framing asked for, and the band
        // is what it is drawn into. So there is one scale, and it is this one.
        var scale = deep / Math.Max(1, window.Height);
        var sw = (float)(window.Width * scale);
        var sh = (float)(window.Height * scale);

        return new PicturePlacement(scale, window.X, window.Y, window.Width, window.Height,
                                    pictureRight - sw, 0, sw, sh);
    }

    /// <summary>
    /// What the chooser framed each photograph as, the last time one was baked, remembered so the manual
    /// window can open on the app's own answer instead of on the middle of the picture, and so "use the
    /// app's placement" has something exact to go back to.
    ///
    /// <para>Written on every bake, INCLUDING the bakes that then go on to use a hand-made framing instead.
    /// That costs nothing (the crop pass runs either way, exactly as it always has) and it is what keeps
    /// the reset honest: a picture that has been hand-placed still knows what the chooser would have done
    /// with it.</para>
    /// </summary>
    private readonly Dictionary<string, ArtFraming> _autoFraming = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The chooser's framing of one photograph, or null when it has never been baked in this
    /// session. Null is a real answer, the window falls back to the whole picture, centred.</summary>
    public ArtFraming? AutomaticFraming(string? url)
        => url is not null && _autoFraming.TryGetValue(url, out var framing) ? framing : null;

    /// <param name="aspect">The picture's room over the band it was placed into, the same number the
    /// framing's own window is cut to. It is what turns the placement's width into a share of the header
    /// (see <see cref="ArtFraming.Cover"/>), and reading it off the geometry the placement was actually
    /// made for is what keeps the record exact.</param>
    private void RememberAutoFraming(string url, PicturePlacement place, int sourceWidth, int sourceHeight, double aspect)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || place.SrcW <= 0 || place.SrcH <= 0)
            return;

        // Small, and nothing in it is expensive: four numbers per photograph. The whole dictionary is
        // cleared when it grows large, like the caches beside it.
        if (_autoFraming.Count > 64)
            _autoFraming.Clear();

        _autoFraming[url] = ArtFraming.From(place.SrcX, place.SrcY, place.SrcW, place.SrcH,
                                            sourceWidth, sourceHeight, aspect);
    }

    /// <summary>
    /// The least of the header the drawn picture may cover, as a fraction of the whole band. Half.
    /// </summary>
    /// <remarks>
    /// <para>A floor, not a target: a photograph that covers more at the size the band wants its subject
    /// drawn at is left alone, and this is the line under which the picture stops carrying the header.
    /// Applied in two places, and they agree because the picture always fills the band's height, so the
    /// share of the header and the drawn WIDTH are the same quantity: as a floor under the zoom in
    /// <see cref="PlacePicture"/>, and as a floor under how much of the frame is on show in
    /// <see cref="ShownWidth"/>.</para>
    ///
    /// <para>The value is one half. It was chosen by looking at the result, not by a formula. Pictures that
    /// were judged correct were drawn at about half the room or more. The ones that looked small in a
    /// corner were drawn at about a fifth. The line falls between those two groups, with room on either
    /// side.</para>
    /// </remarks>
    private const double MinBandCover = 0.50;

    /// <summary>
    /// Whether a framing can still carry a header this wide: whether what it can reach across is at least
    /// <see cref="MinBandCover"/> of the room.
    /// </summary>
    /// <param name="reach">The widest that framing can ever be drawn, see
    /// <see cref="FramedPicture.Reach"/>.</param>
    /// <remarks>
    /// The same line as the floor under the zoom, asked of a framing that already exists rather than of a
    /// placement being made. It is what decides, before the crop runs, whether a resize is a re-fit of the
    /// picture on screen or a re-framing of the photograph (see <see cref="HeldFraming"/>).
    /// </remarks>
    internal static bool Carries(double reach, double slotWidth) => reach >= MinBandCover * slotWidth;

    // ── the compositor: the framed picture, dissolved at every edge ──

    /// <summary>
    /// Renders the header's picture into ONE bitmap the size of the slot: the framed photograph stood
    /// full-height in the band against the record, transparent everywhere else, and every edge dissolved so
    /// it has no drawn boundary anywhere. Because it is one image, the picture and its dissolves fade as a
    /// single unit and can never desync. Re-run when the slot geometry changes; between runs the on-screen
    /// image is simply scaled to fit.
    ///
    /// <para>It is NOT a background. The background is the wash: a blurred copy of the photograph,
    /// registered to the sharp picture (see <see cref="RenderWash"/>). Picking a flat colour out of a
    /// photograph is a rule that is wrong for some photographs. So no flat colour is used. The dissolves
    /// here melt the sharp picture into a blurred copy of itself.</para>
    /// </summary>
    /// <param name="maxSideDissolve">The longest either side edge may dissolve over. Handed in and never
    /// defaulted: it is the record's question, not the picture's, and the compositor cannot see whether
    /// there is a disc beside the slot it is drawing into.</param>
    /// <param name="framedAs">The placement whose margins set the side dissolve's length. It is not always
    /// the one being drawn. A re-fit of a framing into a narrower header (see <see cref="RefitPicture"/>)
    /// has less photograph beside the subject. It would measure a shorter ramp, so the edge the listener is
    /// looking at would go harder the moment the window moved. This is stated at the call, not defaulted. A
    /// bake that forgot it would have edges that do not match the tile it replaces.</param>
    /// <param name="rightUnderRecord">Whether the disc lies over the picture's right edge. If it does, that
    /// edge is not a visible edge. This is stated at the call rather than worked out here. It is a fact
    /// about the geometry this bake is for (see <see cref="RecordOnTheBand"/>). The compositor is given a
    /// picture and a slot, not a header.</param>
    private static byte[]? RenderComposite(byte[] imageBytes, CropFrame frame, PicturePlacement place,
                                           PicturePlacement framedAs,
                                           double slotWidth, double band, float maxSideDissolve,
                                           bool rightUnderRecord)
    {
        try
        {
            using var src = SkiaSharp.SKBitmap.Decode(imageBytes);
            if (src is null || src.Width < 8 || frame.Rect.Width <= 0 || frame.Rect.Height <= 0)
                return null;

            var w = (int)Math.Round(slotWidth);
            var h = (int)Math.Round(band);
            if (w < 8 || h < 8)
                return null;

            var info = new SkiaSharp.SKImageInfo(w, h, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul);
            using var surface = SkiaSharp.SKSurface.Create(info);
            if (surface is null)
                return null;
            var canvas = surface.Canvas;
            canvas.Clear(SkiaSharp.SKColors.Transparent);

            var sharp = SkiaSharp.SKRect.Create(place.DestX, place.DestY, place.DestW, place.DestH);
            // This is sampled the same way as the rest of the app's photographs. It is stated here because
            // SkiaSharp 3 draws a bitmap with NEAREST sampling unless the call names something else.
            // Nearest sampling of a 1920 px press photo, drawn into a band a few hundred points wide, keeps
            // only one source pixel in four or five. The rest are dropped unread. That causes grain on skin
            // and broken diagonals. ArtScaler.SamplingFor chooses by the scale factor. It uses mipmapped
            // linear sampling past a halving, and Mitchell's cubic below that. So the band is reduced by
            // the same rule as a wall tile and a home card. The bitmap is marked immutable first.
            // FromBitmap copies the pixels of a bitmap that could still change. This one cannot change, and
            // nothing below writes to it. Without this, the copy would be a second full-size photograph in
            // memory on every bake.
            src.SetImmutable();
            using var picture = SkiaSharp.SKImage.FromBitmap(src);
            canvas.DrawImage(picture,
                             SkiaSharp.SKRect.Create((float)place.SrcX, (float)place.SrcY,
                                                     (float)place.SrcW, (float)place.SrcH),
                             sharp,
                             Services.Art.ArtScaler.SamplingFor(place.SrcW, place.DestW));

            // Where the people are, in the tile.
            var across = place.DestW / (float)Math.Max(1, place.SrcW);
            float InTile(double sourceX) => sharp.Left + (float)((sourceX - place.SrcX) * across);

            // The margins are taken from the framing that the RAMP is measured from. That is not always the
            // framing being drawn. They are the same picture at the same scale, so the lengths are the
            // same. The exception is a narrower header that has trimmed the photograph's far end. The ramp
            // is measured from the framing, not from the trimmed picture. Measuring it from the trimmed
            // picture would make the edges harder the moment the window moved, on both sides, because the
            // ramp is shared (below).
            var framedLeft = (float)((frame.Content.X - framedAs.SrcX) * framedAs.Scale);
            var framedRight = (float)((framedAs.SrcX + framedAs.SrcW
                                       - (frame.Content.X + frame.Content.Width)) * framedAs.Scale);

            // ONE ramp is spent equally on every visible side. Its length is the longer of the margins.
            // With a record on the band, only the left edge counts, and the rule is the plain one: a
            // photograph's free edges all go the same way.
            //
            // Each side measuring its own margin would sound careful, but it looks wrong. The picture is
            // right-aligned against the record. A subject framed anywhere but dead centre leaves its margin
            // on ONE side only. The far side then got the long dissolve, and the near side got the floor.
            // The photograph faded away over a hundred and fifty points at one edge, and stopped in
            // twenty-six at the other. Nothing in the header shows which edge is which, so the result reads
            // as one soft side and one cut side of the same picture.
            //
            // The two edges of one photograph should go the same way. The longer margin sets the length.
            // Shortening the side that already reads well, to match the side that does not, would fix the
            // wrong end. Both sides then dissolve over that length.
            //
            // With the right edge under the disc, there is only ONE free edge to measure, so the margin on
            // the other side is not offered. The shared length exists so that two edges a listener can see
            // go the same way. Lengthening the visible one, because there is room beside an edge that
            // nobody can see, would spend the picture with no benefit.
            var ramp = SideDissolveRamp(framedLeft, rightUnderRecord ? 0 : framedRight, maxSideDissolve);

            // But never through a head, on either side. The shared ramp spent on the near edge would be
            // spent on whatever stands there. The rule for a zoom also applies to a dissolve: shoulders,
            // hands and the space beside people are cheap, and faces are not. So each side keeps the shared
            // length only as far as its own keep box (brow to chin), and it gives up the rest. The floor
            // still applies under that. A head that stands hard against the picture's edge still needs some
            // edge, and a soft one is the cheapest wrong answer.
            var headLeft = InTile(frame.Keep.X);
            var headRight = InTile(frame.Keep.X + frame.Keep.Width);

            DissolveInto(canvas, w, h, sharp.Left,
                         Math.Min(ramp, Math.Max(MinPictureDissolve, headLeft - sharp.Left)), toTheRight: true);

            // The right edge does not get the shared ramp when a record is on the band. That edge is not
            // free. The picture runs to the end of its slot (see <see cref="PictureBreath"/>), and the slot ends at the
            // disc's arc. So the edge is UNDER the disc. There is nothing there to soften. A long dissolve
            // leading up to it would only empty the strip beside the record, which is the space the picture
            // was given to fill.
            //
            // It keeps the floor, not nothing. The edge is not covered along its whole length. The picture
            // runs on below the band's foot, into the gap above the library card, and by then the disc has
            // curved away. What shows there is the end of a picture that is already fading out under its
            // own foot dissolve. The floor is enough that it fades rather than stops.
            var rightRamp = rightUnderRecord
                ? MinPictureDissolve
                : Math.Min(ramp, Math.Max(MinPictureDissolve, sharp.Right - headRight));

            DissolveInto(canvas, w, h, sharp.Right, rightRamp, toTheRight: false);

            // And the foot, into the library. This one is not clamped to the people the way the sides are.
            // The band's foot is where the header hands over to the rows, and a picture that stops dead on
            // that line draws it. The faces are kept out of it from the other end instead, by the placement
            // choosing a size at which they sit above it (see <see cref="PlacePicture"/>).
            //
            // The ramp is long. Everywhere else the sharp copy dissolves into a blurred copy of itself, and
            // the change is one of FOCUS. A viewer will not notice that at almost any length. Here it is also the
            // line where the app changes from header to library, and every other element that crosses that
            // line (the veil, the scrim's tail) is spread over a hundred pixels or more. A short ramp among
            // long ones is the one that gets seen.
            var foot = (float)Math.Min(sharp.Height * FootDissolveFraction, FootDissolveMax);
            FeatherEdge(canvas, w, h,
                        new SkiaSharp.SKPoint(0, sharp.Bottom - foot), new SkiaSharp.SKPoint(0, sharp.Bottom));

            using var snapshot = surface.Snapshot();
            using var data = snapshot.Encode(SkiaSharp.SKEncodedImageFormat.Png, 92);
            return data.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>How wide the wash is rendered before being stretched over the window. It is blurred past
    /// all recognition anyway; what this buys is a fine enough sampling that the JPEG's own blocking does
    /// not arrive magnified with it.</summary>
    private const int WashWidth = 640;

    /// <summary>The blur, as a fraction of the wash's width, stated as a fraction so the look survives the
    /// resolution being changed.</summary>
    private const float WashBlurFraction = 0.05f;

    /// <summary>
    /// How much wider and taller than the window the blurred copy is drawn.
    /// </summary>
    /// <remarks>
    /// <para>The wash is drawn for one window and is never stretched (see <see cref="PlaceWash"/>). So a
    /// window that was made wider has a strip down its far side that the copy on screen does not reach.
    /// That strip shows the ground colour, and the blurred photograph ends in a line across the header.
    /// This was seen when a small window was maximised. For about a second, until the new pair arrived, the
    /// line stood far into the header. The blur cut was very obvious when resizing from a smaller window.
    /// It should be smooth.</para>
    ///
    /// <para>So the copy is drawn for a window several times the size of the one it is for. The extra hangs
    /// off the LEFT and the FOOT, which are the two sides that a growing window uncovers. The layers are
    /// placed at the window's top-right (see <see cref="Stand"/>). It costs no memory and no time. The wash
    /// is rendered at a fixed
    /// <see cref="WashWidth"/> and stretched, so a larger canvas is the same bitmap covering more points.
    /// What it spends is resolution in the part on screen. A blur of a twentieth of the window has already
    /// spent that resolution.</para>
    ///
    /// <para>2.4 covers a small window maximised on this display, which is a little over double the size.
    /// <see cref="WashFade"/> covers anything past that.</para>
    /// </remarks>
    private const double WashReach = 2.4;

    /// <summary>How much of the wash's overscan is spent fading out rather than covering. The last of the
    /// copy dissolves into the ground colour instead of stopping at a line, so a window grown past even
    /// <see cref="WashReach"/> has no edge in it, only a blurred picture that thins away. Kept well under 1
    /// so the fade itself stays off screen while the window is the size the wash was drawn for.</summary>
    private const double WashFade = 0.35;

    /// <summary>
    /// The canvas one wash is drawn on: how much it covers, which is <see cref="WashReach"/> times the
    /// window it was drawn FOR, and that window.
    /// </summary>
    /// <param name="ForWidth">The window the copy was drawn for. Every other number about the copy is in
    /// this window's points. The placement it was drawn from is in those points, and the canvas is placed
    /// with its right edge on the window's right edge. So the overscan cancels, and a pixel of the
    /// photograph lands where
    /// <see cref="Registers"/> says it does. The value is carried, not worked back out of the canvas. The
    /// reach is one constant away, and a number that is correct only while a constant elsewhere holds would
    /// break as soon as that constant changed.</param>
    internal readonly record struct WashCanvas(double Width, double Height, double ForWidth, double ForHeight)
    {
        /// <summary>The blur's radius in the window's own points. Held to the WINDOW, not the canvas, so
        /// reaching further does not soften the ground; it is also what a kept wash's registration is
        /// measured against (<see cref="Registers"/>).</summary>
        internal double Blur => Math.Max(1, ForWidth) * WashBlurFraction;
    }

    /// <summary>The canvas a wash for this window is drawn on.</summary>
    internal static WashCanvas WashCanvasFor(double windowWidth, double windowHeight)
        => new(Math.Max(8, windowWidth) * WashReach, Math.Max(8, windowHeight) * WashReach,
               Math.Max(8, windowWidth), Math.Max(8, windowHeight));

    /// <summary>The blurred copy as it is drawn, and the part of it the window shows.</summary>
    /// <param name="Shown">Read only for its colour (see <c>CoverColor.Average</c>): the tone the library's
    /// cards stand on is the tone of what is on screen, and averaging the whole canvas would read the
    /// overscan and the fade at its edges instead.</param>
    private readonly record struct RenderedWash(byte[] Wash, byte[] Shown);

    /// <summary>
    /// The wash: the SAME photograph, at the SAME scale and in the SAME place as the picture in the band,
    /// carried out over the whole window and blurred past recognition.
    ///
    /// <para>This is the edge strategy, and it is worth being plain about why it is built this way rather
    /// than as "the photo, blurred, stretched over the window". A fade is a transition in VALUE: it
    /// interpolates between the picture's colour and whatever is underneath it, so where those two differ
    /// the difference is still there, spread over the ramp as a soft glow. That difference is low
    /// frequency, and blurring, which attacks high frequencies, cannot touch it. It is why a feathered edge
    /// over a mismatched ground stays visible however soft it is made.</para>
    ///
    /// <para>Other methods fix this. One is Poisson cloning, which forces the patch's boundary to equal its
    /// surroundings and solves for the interior. Another is multi-band blending, which blends low
    /// frequencies over a wide band and high frequencies over a narrow one. Both remove a difference that
    /// exists. Registration removes the difference itself. Under the picture's edge is the same photograph,
    /// so there is nothing for the ramp to expose. The dissolve becomes a change of FOCUS: sharp into
    /// blurred, of one image.</para>
    ///
    /// <para>Past the photograph's own edges the wash is MIRRORED rather than clamped or padded: a clamped
    /// edge draws streaks out to the corners of the window, and padding needs a colour, which is the thing
    /// this whole design is getting rid of. A mirror is the photograph's own material and carries no join
    /// once blurred this hard.</para>
    /// </summary>
    private static RenderedWash? RenderWash(byte[] imageBytes, PicturePlacement place,
                                            double windowWidth, double windowHeight, WashCanvas drawnFor)
    {
        try
        {
            using var src = SkiaSharp.SKBitmap.Decode(imageBytes);
            if (src is null || src.Width < 8 || windowWidth < 8 || windowHeight < 8
                || drawnFor.Width < windowWidth || drawnFor.Height < windowHeight)
                return null;

            // The wash is rendered small and stretched, so everything below is in "wash pixels".
            var k = WashWidth / drawnFor.Width;
            var w = WashWidth;
            var h = Math.Max(8, (int)Math.Round(drawnFor.Height * k));

            var info = new SkiaSharp.SKImageInfo(w, h, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul);
            using var surface = SkiaSharp.SKSurface.Create(info);
            if (surface is null)
                return null;
            var canvas = surface.Canvas;

            // Where the window sits on this canvas: against its top-right corner, which is where the layers
            // are placed (see Stand). So the extra is on the left and at the foot, the two sides that a
            // growing window uncovers. Everything the placement says is in the window's own points. So
            // moving the picture over by the overscan is all the reach costs here.
            var overscanX = drawnFor.Width - windowWidth;

            // The photograph, placed so that the pixel under any point of the sharp picture IS that point.
            // The tile sits at the window's top-left (the slot starts there), so tile coordinates and
            // window coordinates share an origin. Between them are the wash's own downscale k and the
            // overscan above.
            var scale = place.Scale * k;
            var offsetX = (((place.DestX + overscanX) * k) - (place.SrcX * scale));
            var offsetY = ((place.DestY * k) - (place.SrcY * scale));

            var matrix = SkiaSharp.SKMatrix.CreateScale((float)scale, (float)scale)
                                           .PostConcat(SkiaSharp.SKMatrix.CreateTranslation((float)offsetX, (float)offsetY));

            // The blur is the WINDOW's, not the canvas's: a wash drawn to reach further is the same bitmap
            // over more points, and reading the fraction off the wider canvas would soften the ground every
            // time the reach was changed.
            var blur = (float)Math.Max(1, drawnFor.Blur * k);

            using var shader = SkiaSharp.SKShader.CreateBitmap(
                src, SkiaSharp.SKShaderTileMode.Mirror, SkiaSharp.SKShaderTileMode.Mirror, matrix);
            using var paint = new SkiaSharp.SKPaint
            {
                Shader = shader,
                ImageFilter = SkiaSharp.SKImageFilter.CreateBlur(blur, blur, SkiaSharp.SKShaderTileMode.Clamp),
            };
            canvas.DrawRect(SkiaSharp.SKRect.Create(0, 0, w, h), paint);

            // The tone, off the part of the canvas the window shows. Taken before the fade below, which is
            // off screen at rest and would drag the average toward nothing if it were read.
            var shownX = Math.Clamp((int)Math.Round(overscanX * k), 0, w - 1);
            using var inWindow = surface.Snapshot(SkiaSharp.SKRectI.Create(
                shownX, 0, w - shownX, Math.Clamp((int)Math.Round(windowHeight * k), 1, h)));
            using var shownData = inWindow.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 92);

            // ...and the outer edges taken to nothing, so a window grown past the reach meets a picture
            // thinning into the ground colour rather than the end of a bitmap. Only the left and the foot:
            // the other two are the window's own edges and are never uncovered.
            var fadeX = (float)(overscanX * WashFade * k);
            FadeOutEdge(canvas, SkiaSharp.SKRect.Create(0, 0, fadeX, h),
                        gone: SkiaSharp.SKPoint.Empty, whole: new SkiaSharp.SKPoint(fadeX, 0));

            var fadeY = (float)((drawnFor.Height - windowHeight) * WashFade * k);
            FadeOutEdge(canvas, SkiaSharp.SKRect.Create(0, h - fadeY, w, fadeY),
                        gone: new SkiaSharp.SKPoint(0, h), whole: new SkiaSharp.SKPoint(0, h - fadeY));

            // PNG, because the fade is an alpha and a JPEG has none. It is a smooth picture of nothing in
            // particular, so it costs little more than a JPEG would.
            using var snapshot = surface.Snapshot();
            using var data = snapshot.Encode(SkiaSharp.SKEncodedImageFormat.Png, 92);
            return new RenderedWash(data.ToArray(), shownData.ToArray());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Takes one edge of the wash to nothing across <paramref name="area"/>: whole at
    /// <paramref name="whole"/> and gone at <paramref name="gone"/>. It multiplies the alpha of what is
    /// already drawn rather than painting over it, so where the two edges meet in the corner they simply
    /// compound.</summary>
    private static void FadeOutEdge(SkiaSharp.SKCanvas canvas, SkiaSharp.SKRect area,
                                    SkiaSharp.SKPoint gone, SkiaSharp.SKPoint whole)
    {
        if (area.Width < 1 || area.Height < 1)
            return;

        using var fade = new SkiaSharp.SKPaint
        {
            BlendMode = SkiaSharp.SKBlendMode.DstIn,
            Shader = SkiaSharp.SKShader.CreateLinearGradient(
                gone, whole,
                new[] { SkiaSharp.SKColors.Transparent, SkiaSharp.SKColors.Black },
                null, SkiaSharp.SKShaderTileMode.Clamp),
        };

        canvas.DrawRect(area, fade);
    }

    /// <summary>Where the eyes sit down the band when the face is small enough that there is room to
    /// choose: a little above the middle. A face that is big enough to be constrained does not use this
    /// value. See the arithmetic in PlacePicture, which puts the eyes about a third of the way down. The
    /// eyes land there as a result of the arithmetic, not by decree.</summary>
    private const double EyeLineInBand = 0.38;

    /// <summary>The air kept above the brow: a little, not none. With no headroom at all, the picture looks
    /// like a mistake rather than a close-up. The hair above the brow fills this space, and it is what gets
    /// spent when there is not enough room for both this and the chin.</summary>
    private const double Headroom = 0.06;

    /// <summary>The air kept below the chin, above where the foot dissolve begins. A chin exactly on that
    /// line is technically clear of the dissolve, but it does not look clear. The fade is already reaching
    /// for the jaw. Lowering D is also the cheapest way to raise the eyes. They land at h0 + p(D - h0), so
    /// every point taken off D lifts them by about 0.4 of it.</summary>
    private const double ChinClearance = 0.06;

    /// <summary>How much of the picture's height its foot dissolve takes, and the most it may take in
    /// pixels. Read by the compositor, which draws it, AND by the placement, which has to keep the faces
    /// out of it.</summary>
    private const double FootDissolveFraction = 0.30;
    private const double FootDissolveMax = 78;

    /// <summary>Where the lowest a face may sit is: above the dissolve, and clear of it by
    /// <see cref="ChinClearance"/>. The D of the placement arithmetic.</summary>
    private const double FaceFoot = 1 - FootDissolveFraction - ChinClearance;

    /// <summary>The shortest dissolve a picture's side edge may have: below this it stops reading as a soft
    /// edge and starts reading as a cut. Spent on the content itself when there is no air left, a hand or
    /// a shoulder going slightly soft, which is the cheapest thing on offer.</summary>
    private const float MinPictureDissolve = 26;

    /// <summary>And the longest: past this a side dissolve is not an edge treatment. It is a third of the
    /// photograph turned into a blur.</summary>
    private const float MaxPictureDissolve = 150;

    /// <summary>
    /// The longest a side dissolve may be when there is no record on the band. It is longer because the
    /// room is there. With no disc, the space that the disc and its clearance would take belongs to the
    /// picture. A photograph with that much more air around it may spend more of it on its edges before the
    /// dissolve stops being an edge and starts being the picture.
    ///
    /// <para>It is still well under the room gained, for the reason that <see cref="MaxPictureDissolve"/>
    /// exists. It is a CEILING, not a length. The ramp is measured from the subject's own air either way,
    /// so a picture with nothing to spare is dissolved exactly as it was.</para>
    /// </summary>
    private const float WidePictureDissolve = 240;

    /// <summary>
    /// Whether a disc is on the band, asked of the geometry a bake is FOR rather than of the switch: the
    /// slot is narrower than the window exactly when a disc is reaching in from the right, because
    /// <see cref="RecordInsetFor"/> is the only thing that narrows it and it cannot return zero while the
    /// record is on.
    ///
    /// <para>Two things read it, how far the edges may dissolve, and whether the right edge is an edge at
    /// all, and they read it here rather than each deriving it, so they cannot come to different answers
    /// about the same bake.</para>
    /// </summary>
    internal static bool RecordOnTheBand(double slotWidth, double windowWidth) => slotWidth < windowWidth;

    /// <summary>Which of the two ceilings this bake works under.</summary>
    internal static float SideDissolveCap(double slotWidth, double windowWidth)
        => RecordOnTheBand(slotWidth, windowWidth) ? MaxPictureDissolve : WidePictureDissolve;

    /// <summary>
    /// The one ramp that both side edges of a photograph dissolve over: the longer of its two margins. It
    /// has a floor, so it never reads as a cut, and a cap, so it never eats the picture.
    ///
    /// <para>The cap is a ceiling, not a length. That is why it is safe to raise it for a header with no
    /// record. A photograph with nothing to spare is measured the same way, and it has the same floor. It
    /// comes out the same whichever cap applies. Only a photograph with margin to spare can use more of the
    /// cap.</para>
    /// </summary>
    internal static float SideDissolveRamp(float leftAir, float rightAir, float cap)
        => Math.Clamp(Math.Max(leftAir, rightAir), MinPictureDissolve, cap);

    /// <summary>
    /// Dissolves one vertical side of the picture into the wash beside it: clear at
    /// <paramref name="edge"/>, its own edge, and opaque <paramref name="ramp"/> points inward.
    ///
    /// <para>The length is passed in, not worked out here. It is not a property of this side alone. It is
    /// shared with the other side, so that the two edges of one photograph go the same way. See the note at
    /// the call.</para>
    /// </summary>
    private static void DissolveInto(SkiaSharp.SKCanvas canvas, int w, int h, float edge, float ramp, bool toTheRight)
    {
        var opaqueAt = toTheRight ? edge + ramp : edge - ramp;

        FeatherEdge(canvas, w, h, new SkiaSharp.SKPoint(opaqueAt, 0), new SkiaSharp.SKPoint(edge, 0));
    }

    /// <summary>Ramps the composite's alpha down to zero across one edge band (opaque inward → clear at the
    /// edge) via a clamped gradient under DstIn, drawn over the whole canvas, so only the band it describes
    /// is affected while the rest stays fully opaque.</summary>
    private static void FeatherEdge(SkiaSharp.SKCanvas canvas, int w, int h, SkiaSharp.SKPoint opaqueAt, SkiaSharp.SKPoint clearAt)
    {
        using var shader = SkiaSharp.SKShader.CreateLinearGradient(
            opaqueAt, clearAt, RampColours, RampStops, SkiaSharp.SKShaderTileMode.Clamp);
        using var paint = new SkiaSharp.SKPaint { Shader = shader, BlendMode = SkiaSharp.SKBlendMode.DstIn };
        canvas.DrawRect(SkiaSharp.SKRect.Create(0, 0, w, h), paint);
    }

    /// <summary>
    /// The shape every dissolve in this file has: a smoothstep, sampled into stops.
    ///
    /// <para>Not a straight line, and this is the difference between a soft edge and a visible one. A
    /// linear ramp leaves BOTH of its ends with the slope still on them, the alpha is falling at full rate
    /// right up to the moment it stops, and a discontinuity in slope is precisely what the eye's edge
    /// detectors fire on. It shows up as a pale line just inside the picture and another where the ramp
    /// lands: Mach bands, drawn by the very fade meant to prevent one. A smoothstep leaves and arrives with
    /// no slope at all, so neither end of the dissolve has a place anyone can point to.</para>
    ///
    /// <para>Nine stops: enough that the curve is a curve rather than a polygon, and cheap, since Skia
    /// interpolates between them on the GPU anyway.</para>
    /// </summary>
    private static readonly float[] RampStops = [0f, 0.125f, 0.25f, 0.375f, 0.5f, 0.625f, 0.75f, 0.875f, 1f];

    private static readonly SkiaSharp.SKColor[] RampColours = BuildRamp();

    private static SkiaSharp.SKColor[] BuildRamp()
    {
        var colours = new SkiaSharp.SKColor[RampStops.Length];
        for (var i = 0; i < RampStops.Length; i++)
        {
            var t = RampStops[i];
            var smooth = t * t * (3 - (2 * t));            // 0 at both ends of its slope
            colours[i] = new SkiaSharp.SKColor(255, 255, 255, (byte)Math.Round(255 * (1 - smooth)));
        }
        return colours;
    }

    // The fades

    // ContentLayer opacity is driven by a manual per-frame tween, not by a Storyboard. Overlapping opacity
    // storyboards in Uno each hold their own end value and reassert it on completion. A slow fade-in that
    // finishes late can then snap the empty-cover tile back over the parked rpm mark. Stopping the earlier
    // storyboard reverts the property to its base value in the middle of a fade, which causes a flicker. A
    // single tween always starts from the true current opacity. It has neither problem: there is no second
    // animation to fight, and nothing to revert.
    private readonly System.Diagnostics.Stopwatch _contentClock = new();
    private double _contentFrom, _contentTo, _contentCur;
    private TimeSpan _contentDur;
    private Action? _contentDone;
    private bool _contentTweening;

    private void AnimateContent(double to, TimeSpan? duration = null, Action? onCompleted = null)
    {
        _contentFrom = _contentCur;   // Start from where the opacity is now, so there is no jump or flicker.
        _contentTo = to;
        _contentDur = duration ?? FadeDuration;
        _contentDone = onCompleted;   // A new tween replaces the old completion. An interrupted park is simply dropped.
        _contentClock.Restart();
        if (!_contentTweening)
        {
            _contentTweening = true;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnContentTween;
        Services.Shell.FrameWatch.Began("art-content-tween");
        }
    }

    private void OnContentTween(object? sender, object e)
    {
        var secs = _contentDur.TotalSeconds;
        var t = secs <= 0 ? 1 : Math.Clamp(_contentClock.Elapsed.TotalSeconds / secs, 0, 1);
        var eased = t * t * (3 - 2 * t);   // smoothstep, ease in-out
        _contentCur = _contentFrom + (_contentTo - _contentFrom) * eased;
        ContentLayer.Opacity = _contentCur;
        if (t < 1)
            return;
        _contentCur = _contentTo;
        ContentLayer.Opacity = _contentCur;
        _contentTweening = false;
        _contentClock.Stop();
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnContentTween;
        Services.Shell.FrameWatch.Ended("art-content-tween");
        var done = _contentDone;
        _contentDone = null;
        done?.Invoke();
    }

    // The crossfades: a number written every frame, never a Storyboard The four layers that carry the
    // artwork (the two pictures and the two washes) are faded by the tween below and by nothing else. A
    // Storyboard cannot do this job.
    //
    // An Uno dependent animation HOLDS its end value when it finishes. A held value outranks anything that
    // is written to the property afterwards. Both halves of a crossfade run into that:
    //
    //   * the layer coming in is the one that held 1 the last time it was used. So the animation to 1
    //     has nothing to do, and the picture arrives complete in a single frame, with no dissolve at all;
    //   * the layer going out is told `Opacity = 0` when the incoming one lands. That assignment is lost,
    //     so the old picture stays up underneath at full strength.
    //
    // This is what a resize looked like. The new composition jumped into place, with the old one still on
    // screen around it. The first swap after a track change was fine, because that layer's last storyboard
    // had held 0. Every swap after it was not. That made the fault look like a problem in the re-bake, not
    // in the fade.
    //
    // A plain number written every frame has no memory of its previous value. Each layer has one tween, so
    // a crossfade is two tweens, and neither needs to know that the other exists.

    private readonly Dictionary<UIElement, Dissolve> _dissolves = new();

    private Dissolve DissolveOf(UIElement layer)
    {
        if (!_dissolves.TryGetValue(layer, out var tween))
            _dissolves[layer] = tween = new Dissolve(layer, "art-backdrop");
        return tween;
    }

    /// <summary>Takes a layer to an opacity over a duration, from wherever it actually is.</summary>
    private void Fade(UIElement layer, double to, TimeSpan? over = null, Action? landed = null)
        => DissolveOf(layer).To(to, over ?? FadeDuration, landed);

    /// <summary>Puts a layer out at once, cancelling any fade still pointing at it.</summary>
    private void Hide(UIElement layer) => DissolveOf(layer).Set(0);

    /// <summary>
    /// Finishes whatever a pair has in flight, so that the next swap starts from a settled pair.
    ///
    /// <para>Every swap here reads the pair's state and assumes it: one layer up with a picture in it, and
    /// the other empty. If a swap is interrupted, that is no longer true. The layer may be half-way in, or
    /// a wash may be held at full strength while it waits to be dropped. The next swap then fades a layer
    /// that is already where it is being sent. That is no fade at all, and the new artwork appears in a
    /// single frame. A drag settles every quarter second, so this happens constantly during a
    /// resize.</para>
    ///
    /// <para>Landing first puts the pair back to one layer up and one empty. The cost is that an
    /// interrupted swap completes at once, instead of where it was. On a re-cut of the same photograph,
    /// nobody can see that.</para>
    /// </summary>
    private void LandPair(UIElement first, UIElement second)
    {
        // Do this twice. Landing one layer can START the other. A sequential dissolve hands over inside its
        // own completion. The pair is settled only when neither layer has anything left to arrive.
        for (var pass = 0; pass < 2; pass++)
        {
            DissolveOf(first).Land();
            DissolveOf(second).Land();
        }
    }

    private static void Animate(UIElement element, double to, Action? onCompleted = null, TimeSpan? duration = null)
    {
        var fade = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(duration ?? FadeDuration),
            EnableDependentAnimation = true,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        if (onCompleted is not null)
            storyboard.Completed += (_, _) => onCompleted();
        Services.Shell.Storyboards.Play(storyboard);
    }

    /// <summary>Opacity tween with a duration chosen by the caller. The record's exit is quicker than the
    /// slow 2 s art crossfade that <see cref="Animate"/> uses when no duration is given.</summary>
    private static void AnimateOpacity(UIElement element, double to, TimeSpan duration)
    {
        var fade = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(duration),
            EnableDependentAnimation = true,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        Services.Shell.Storyboards.Play(storyboard);
    }

    /// <summary>Scales both axes of a transform together. This is used for the record's fly-out and
    /// fly-in.</summary>
    private static void AnimateScale(ScaleTransform transform, double to)
    {
        var storyboard = new Storyboard();
        foreach (var prop in new[] { "ScaleX", "ScaleY" })
        {
            var anim = new DoubleAnimation
            {
                To = to,
                Duration = new Duration(RecordFlyDuration),
                EnableDependentAnimation = true,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTarget(anim, transform);
            Storyboard.SetTargetProperty(anim, prop);
            storyboard.Children.Add(anim);
        }
        Services.Shell.Storyboards.Play(storyboard);
    }
}
