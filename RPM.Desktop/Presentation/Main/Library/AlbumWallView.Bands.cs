using System.Collections.ObjectModel;
using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Core.Models;
using RPM.Desktop.Strings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// Opening a record on the wall. This covers the band that slides out under its row, the drawer's own
/// visual, and the state machine. The state machine keeps exactly one drawer open while the wall is
/// rebuilt, recycled and re-seated under it.
/// </summary>
public sealed partial class AlbumWallView
{
    // ── expand / collapse: the state machine is in BandConductor ──

    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AlbumCard card)
            _band.Toggle(card);
    }

    private static int ColumnOf(AlbumRow row, AlbumCard card)
    {
        for (var i = 0; i < row.Cards.Count; i++)
            if (ReferenceEquals(row.Cards[i], card)) return i;
        return 0;
    }

    /// <summary>Finds the realized drawer root that shows <paramref name="band"/>. This is the Grid with
    /// the expand ScaleTransform, whose DataContext is the band. It is reliable across Uno's per-row
    /// element recycling, unlike a cached reference. It is only called while that drawer is on
    /// screen.</summary>
    private FrameworkElement? FindBandRoot(AlbumBand band) => FindByDataContext(this, band);

    private static FrameworkElement? FindByDataContext(DependencyObject parent, object dataContext)
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement fe && fe.RenderTransform is ScaleTransform &&
                ReferenceEquals(fe.DataContext, dataContext))
                return fe;
            if (FindByDataContext(child, dataContext) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>Whether the drawers are wide enough for two track columns. There is one value for the
    /// whole wall, because every drawer is the same width. A band realized between resizes (a recycled
    /// container, or a fresh open) starts in the mode the window is already in. It does not guess and
    /// correct a frame later. The drawer works out the mode, and this field is where the wall remembers
    /// it.</summary>
    private bool _bandTwoColumns = true;

    // ── drawer visual: applied from the band model on realize AND on rebind, so a recycled or re-seated
    //    element always shows the current state ──

    private void BandRoot_Loaded(object sender, RoutedEventArgs e) => ApplyBandVisual(sender);

    private void BandRoot_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        => ApplyBandVisual(sender);

    private void ApplyBandVisual(object sender)
    {
        if (sender is not FrameworkElement root
            || root.DataContext is not AlbumBand band
            || root.RenderTransform is not ScaleTransform scale)
            return;

        // The drawer is a control, and this is where it is handed what a press on it means. The hooks are
        // wired on every realize, not once. A container is recycled onto other records. A drawer whose
        // hooks were set only on its first band would stop working as soon as it was reused.
        if (root.FindName("BandView") is AlbumBandView view)
        {
            view.PlayTrack = PlayTrack;
            view.PlayTracks = PlayTracks;
            view.StartDj = StartDj;
            view.MenuInvoked = MenuInvoked;
            view.SetTrackFavorite = SetTrackFavorite;
            view.TrackMenuHooks = new TrackMenuHooks(
                TrackMenuInvoked, PlaylistEntriesProvider, AddTrackToPlaylist,
                NewPlaylistFromTrack, TrackMenuStateProvider);
            view.ColumnsChanged = two => _bandTwoColumns = two;

            // A realized or re-seated drawer takes the column mode the window is already in. Its own
            // SizeChanged only fires when the width actually changes.
            view.SetColumns(_bandTwoColumns);
        }

        if (band.IsClosing)
            return;   // the fold-shut animation controls the element right now

        if (band.AnimateOpen)
        {
            // Take up NO room now, whichever of Loaded or DataContextChanged fires first. The template
            // must never paint a frame fully drawn before its own unfold starts. The row below must not
            // be pushed down before it either.
            scale.ScaleY = 1;   // the drawer opens by growing; nothing is squashed into reserved space
            root.Height = 0;
            root.Opacity = 0;
            if (!root.IsLoaded)
                return;   // keep the one-shot. Loaded fires next and starts the unfold in the tree.
            band.AnimateOpen = false;   // one-shot: a fresh open unfolds once
            _ = PlayOpen(root);
        }
        else if (_bandAnim.Owns(root))
        {
            // An unfold is running on this element, and the other realize event (Loaded and
            // DataContextChanged both call in here) has just arrived. Snapping open now would cause a
            // flash.
        }
        else
        {
            scale.ScaleY = 1;           // a re-seat or a recycle snaps straight to open
            root.Height = double.NaN;
            root.Clip = null;
            root.Opacity = 1;
        }
    }

    /// <summary>The wall's one live fold or unfold. There is at most one drawer here, so there is at most
    /// one of these. A new walk cancels the old one. The machinery is <see cref="BandUnfold"/>, which
    /// every other surface that opens records also uses. An artist's page unfolds its drawer the same
    /// way, at the same speed. Its own walks are its own.</summary>
    private readonly BandUnfold _bandAnim = new();

    /// <summary>The drawer's unfold: measure the length it wants, then grow into it from nothing. The
    /// measure is taken, and the height is put back to zero, inside the one call. So no frame is painted
    /// with the full gap open.</summary>
    private Task PlayOpen(FrameworkElement root)
    {
        root.Height = double.NaN;
        var full = LengthWanted(root);
        root.Height = 0;
        if (full <= 1)
        {
            root.Height = double.NaN;   // nothing to measure: let it lay out and be seen
            root.Opacity = 1;
            return Task.CompletedTask;
        }
        return _bandAnim.To(root, toHeight: full, toOpacity: 1, durMs: 200, easeOut: true, releaseToAuto: true);
    }

    /// <summary>How long the drawer wants to be. This is measured, not laid out. See
    /// <see cref="BandUnfold.Wanted"/>. The scroller's width stands in for an element that has not been
    /// given one yet. That happens when a drawer is opened into a row that has only just been
    /// realized.</summary>
    private double LengthWanted(FrameworkElement root)
        => BandUnfold.Wanted(root, fallbackWidth: Scroller.ViewportWidth);

    /// <summary>The drawer's fold-shut. This is the reverse of the unfold, and the rows below close back
    /// up with it. The caller (BandConductor) waits for the tween. Then it detaches the band and resets
    /// the element for its next band. That reset puts the height back to Auto.</summary>
    private Task PlayClose(FrameworkElement root)
        => _bandAnim.To(root, toHeight: 0, toOpacity: 0, durMs: 160, easeOut: false, releaseToAuto: false);

    /// <summary>
    /// Whether a click on this row moves the wall. It does so only when the record is below the middle of
    /// the viewport. The drawer opens underneath the record, and down there it would open into what
    /// little screen is left. At or above the middle, the wall stays where it is, and the drawer simply
    /// opens.
    ///
    /// <para>This is asked BEFORE the drawer is attached. Then the row's height is only its cards. An
    /// attached drawer hangs below them, and would move the row's middle past the halfway line. Every
    /// record on screen would then look as if it were below the centre.</para>
    /// </summary>
    private bool WantsLift(AlbumRow row)
    {
        var index = _rows.IndexOf(row);
        if (index < 0 || RowsRepeater.TryGetElement(index) is not FrameworkElement element)
            return false;
        var viewport = Scroller.ViewportHeight;
        if (viewport <= 1 || element.ActualHeight <= 0)
            return false;
        try
        {
            var top = element.TransformToVisual(Scroller).TransformPoint(default).Y;
            return AlbumGridLayout.LiftsBeforeOpening(top, element.ActualHeight, viewport);
        }
        catch (Exception)
        {
            // An element in the middle of being recycled has no place in the tree to transform from.
            // Leave the wall alone.
            return false;
        }
    }

    /// <summary>
    /// Glides <paramref name="row"/> up to the top of the viewport, and returns once it has landed.
    ///
    /// <para>This is called with nothing opened under the row yet. The wall the reader is looking at is
    /// the wall that moves, and the drawer is attached afterwards. The last rows on the wall cannot reach
    /// the top, because there is not that much wall below them. They go as far as there is. A scroller
    /// does the same with any offset past its end.</para>
    ///
    /// <para>ChangeView's animation has no completion to await. So the landing is watched on the offset
    /// itself. The wait is done when the offset arrives, or when it stops moving. That happens when the
    /// wall is clamped at its end, or when the animation never runs. Either way the wait is capped, so
    /// the unfold is never held up.</para>
    /// </summary>
    private async Task GlideRowToTop(AlbumRow row)
    {
        var index = _rows.IndexOf(row);
        if (index < 0 || RowsRepeater.TryGetElement(index) is not FrameworkElement element)
            return;
        double top;
        try
        {
            top = element.TransformToVisual(Scroller).TransformPoint(default).Y;
        }
        catch (Exception)
        {
            return;
        }
        if (top <= 1)
            return;   // already at the top, or above it

        var to = Scroller.VerticalOffset + top;
        if (WallDiag.On)
            WallDiag.Open($"glide row {index} — top {top:0.#}, offset {Scroller.VerticalOffset:0.#} → {to:0.#}"
                + $" | extent {Scroller.ExtentHeight:0.#}, viewport {Scroller.ViewportHeight:0.#},"
                + $" end {Math.Max(0, Scroller.ExtentHeight - Scroller.ViewportHeight):0.#}");
        Scroller.ChangeView(null, to, null, disableAnimation: false);

        var last = Scroller.VerticalOffset;
        var still = 0;
        for (var i = 0; i < 30; i++)   // about 480 ms at most
        {
            await Task.Delay(16);
            var now = Scroller.VerticalOffset;
            if (Math.Abs(now - to) < 1.5)
            {
                if (WallDiag.On)
                    WallDiag.Open($"glide landed at {now:0.#} | extent {Scroller.ExtentHeight:0.#}");
                return;
            }
            still = Math.Abs(now - last) < 0.5 ? still + 1 : 0;
            if (still >= 3)
            {
                if (WallDiag.On)
                    WallDiag.Open($"glide stopped short at {now:0.#} of {to:0.#}"
                        + $" | extent {Scroller.ExtentHeight:0.#}, end {Math.Max(0, Scroller.ExtentHeight - Scroller.ViewportHeight):0.#}");
                return;
            }
            last = now;
        }
        if (WallDiag.On)
            WallDiag.Open($"glide ran out of clock at {Scroller.VerticalOffset:0.#} of {to:0.#}");
    }

    /// <summary>
    /// The grounds already read, by folder. Each ground is the sleeve's three layers, with the blur
    /// decoded.
    ///
    /// <para>Why a drawer crosses INTO its ground at all, rather than opening on it: the ground needs a
    /// disk read, a blur and an average. None of that can happen on the frame the reader clicks. A second
    /// opening is a different case. The answer is already in hand. A drawer that has to cross into a
    /// colour it was given a minute ago is doing extra work for nothing. So the read is kept, and only a
    /// record's FIRST opening crosses.</para>
    ///
    /// <para>Only the last few are kept. The decoded blur is about a third of a megabyte. A session that
    /// opens many records would otherwise keep every one of them until it ends. A few is enough for
    /// normal use: open a record, close it, open it again, or step to the one beside it and back.</para>
    /// </summary>
    private readonly Dictionary<string, (SleeveGround Ground, BitmapImage? Wash)> _grounds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _groundOrder = [];
    private const int GroundsKept = 8;

    private void RememberGround(string folder, SleeveGround ground, BitmapImage? wash)
    {
        if (_grounds.ContainsKey(folder))
            _groundOrder.Remove(folder);
        _grounds[folder] = (ground, wash);
        _groundOrder.Add(folder);

        while (_groundOrder.Count > GroundsKept)
        {
            _grounds.Remove(_groundOrder[0]);
            _groundOrder.RemoveAt(0);
        }
    }

    /// <summary>Forgets a record's ground. Its cover has been replaced, so everything read from the old
    /// picture is out of date. See <see cref="RefreshAlbum"/>.</summary>
    internal void ForgetGround(string folder)
    {
        _grounds.Remove(folder);
        _groundOrder.Remove(folder);
    }

    /// <summary>Forgets all of them. The wall now shows a different collection.</summary>
    private void ForgetGrounds()
    {
        _grounds.Clear();
        _groundOrder.Clear();
    }

    /// <summary>How long the sleeve's ground takes to cross over the colour the drawer opened with. This
    /// is the album page's own figure. The two surfaces dress themselves in the same sleeve at the same
    /// speed.</summary>
    private const double BandGroundFadeMs = 260;

    /// <summary>The one live ground cross-fade. There is at most one open drawer, so there is at most one
    /// of these. A drawer that is opened, or re-pointed, while a fade is running stops it where it
    /// stands. The new band starts its own from zero. The old band is moving off screen, and nobody is
    /// watching it finish.</summary>
    private ChromeFade? _groundFade;

    /// <summary>
    /// Puts the sleeve's ground on the drawer: the colour, the blur, the veil, the pointer triangle and
    /// the open card's ring. All are written at once, and then crossed in over what the drawer opened
    /// with.
    ///
    /// <para><paramref name="cross"/> is the difference between the two ways this happens. The caller
    /// states it, because only the caller knows which case it is. A record opened for the FIRST time has
    /// its ground read from disk after the drawer is already up. So it opens on the card's tint, which is
    /// the only colour it has on the frame the reader clicks. The sleeve then crosses over it. A record
    /// opened AGAIN was already dressed from the seed on that same frame. Crossing into a colour already
    /// on screen would animate something that did not happen.</para>
    /// </summary>
    private void ShowBandGround(AlbumBand band, SleeveGround ground, BitmapImage? wash, bool cross)
    {
        band.Ground = new SolidColorBrush(ground.Ground);
        band.Veil = ground.Veil;
        band.Wash = wash;

        // The triangle and the open card's ring cannot carry three layers. So they take the colour that
        // the three layers flatten to, and they meet the drawer in its own tone. Neither can be crossed
        // by a second copy fading over the first. The triangle is a ramp, and two ramps stacked give one
        // alpha over another, not the ramp itself. The mark came out darker, and off-hue from the drawer
        // it points at. Both cross by being REPAINTED. Their colour moves from what the drawer opened
        // with to what its sleeve turned out to be, using the same number that the layers use.
        var wasPoint = BandPointer.ColourOf(band.PointerBrush) ?? ground.Flat;
        var wasRing = (band.Card.Ring as SolidColorBrush)?.Color ?? ground.Flat;

        // The ring's brush is made once, and its colour is written a frame at a time. The triangle's
        // brush is rebuilt, because a gradient is its stops, and rewriting the stops in place is a
        // repaint that the binding never sees. That is three stops rebuilt over a quarter of a second.
        var ring = new SolidColorBrush(wasRing);
        band.Card.Ring = ring;

        _groundFade?.Stop();
        _groundFade = new ChromeFade(
            v =>
            {
                band.GroundOn = v;
                band.PointerBrush = BandPointer.Ramp(BandPointer.Blend(wasPoint, ground.Flat, v));
                ring.Color = BandPointer.Blend(wasRing, ground.Flat, v);
            },
            band.GroundOn);
        _groundFade.To(1, cross ? BandGroundFadeMs : 0);
    }

    /// <summary>A same-row switch keeps the SAME drawer element but swaps in a neighbour's tracks. Its
    /// height would then jump to the new album's length. This tweens the height change (the expand or
    /// collapse) from the old height to the new one, so the switch reads as a morph, not a pop. The
    /// drawer's height is set by its content (Auto). So the caller pins the height while the tracks load
    /// asynchronously. This method pins it again, and hands it to the same tween that the unfold uses.
    /// That tween clips the overflow while it grows, and releases to Auto at the end. Because it shares
    /// the tween, it is cancelled by whatever opens or closes next.</summary>
    private void MorphHeight(FrameworkElement root, double from, double to)
    {
        if (Math.Abs(to - from) < 1)
        {
            root.Height = double.NaN;   // nothing to morph: release straight to Auto
            root.Clip = null;
            return;
        }
        root.Height = from;   // the pin the walk starts from; BandUnfold reads it and clips to it
        _ = _bandAnim.To(root, to, root.Opacity, durMs: 200, easeOut: true, releaseToAuto: true);
    }

    /// <summary>
    /// Fills one open record. The reading itself is in <see cref="AlbumBandLoader"/>, which is shared
    /// with every other surface that opens records. This method keeps what is true of THIS wall: the one
    /// cancellation that replaces the last drawer, the ground it crosses into, the breathing clock that
    /// drives the cloud marks, and which line is playing.
    /// </summary>
    private async Task LoadBandAsync(AlbumBand band, bool loadCover = true)
    {
        _bandCts?.Cancel();
        var cts = _bandCts = new CancellationTokenSource();

        // Whatever ground was still crossing in belonged to the record this drawer was showing a moment
        // ago. The seed has already put the new record's opening colour up at zero. A cross left running
        // would carry on from where it was, and bring the last album's sleeve back with it.
        _groundFade?.Stop();

        var items = await AlbumBandLoader.FillAsync(
            band,
            new AlbumBandSources(
                Cover: CoverLoader,
                Sleeve: GroundAsync,
                Tracks: TracksLoader,
                Classical: ClassicalLoader,
                UploadsStopped: CloudUploadsStopped,
                Shelf: ShelfReading,
                RasterScale: XamlRoot?.RasterizationScale ?? 1),
            loadCover,
            // The wall is a wall of records. A record opened on it is the subject, so all of its tracks are
            // loaded.
            BandTracks.Whole,
            cts.Token);

        if (items.Count == 0)
            return;

        // A record opened while some of its tracks are uploading starts breathing straight away. The
        // marks were given to the lines when they were built.
        _lineBreath.Sync();
        ApplyPlaying(items);
    }

    /// <summary>
    /// The drawer standing on the record's own sleeve, exactly as the album page does it. The mean colour
    /// is under everything. The sleeve is blurred over it, and the veil sits inside the blur. Both
    /// surfaces use the same reader (<see cref="SleeveGround"/>). It runs on a worker thread, because a
    /// blur and an average are two decodes. This is the thread the drawer is unfolding on.
    ///
    /// <para>This is skipped outright where the record's ground is already remembered. The seed has
    /// already put it up, whole, on the frame the drawer opened. Only a record opened for the first time
    /// pays for its own ground, and only that one crosses into it.</para>
    ///
    /// <para>The wall owns this method, not the loader. The READING is shared. It is a property of the
    /// sleeve, and <see cref="AlbumBandLoader.ReadGroundAsync"/> is where it lives. But the cache it is
    /// stored in, and the cross-fade out of the last record, belong to this wall. A surface that opens
    /// one record at a time has neither.</para>
    /// </summary>
    private async Task GroundAsync(AlbumBand band, byte[] bytes, CancellationToken ct)
    {
        if (_grounds.ContainsKey(band.Card.Folder)
            || await AlbumBandLoader.ReadGroundAsync(bytes, ct) is not { } read
            || ct.IsCancellationRequested)
            return;

        RememberGround(band.Card.Folder, read.Ground, read.Wash);
        ShowBandGround(band, read.Ground, read.Wash, cross: true);
    }
}
