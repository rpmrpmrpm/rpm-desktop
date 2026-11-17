using RPM.Core.Library;
using RPM.Desktop.Presentation.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// Opening a record on the wall: the drawer that opens under its row, which element shows it, how far it
/// is open, and the one clock that moves it. The ORDER of an open is in <see cref="BandConductor"/>. This
/// file holds the steps it is made of.
/// </summary>
public sealed partial class AlbumWallView
{
    // ── expand / collapse: the order is in BandConductor ──

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

    /// <summary>Whether the drawers are wide enough for two track columns. There is one value for the
    /// whole wall, because every drawer is the same width. A band realized between resizes (a recycled
    /// container, or a fresh open) starts in the mode the window is already in. It does not guess and
    /// correct a frame later. The drawer works out the mode, and this field is where the wall remembers
    /// it.</summary>
    private bool _bandTwoColumns = true;

    // ── which element shows the drawer ──

    /// <summary>
    /// Puts <paramref name="band"/> into a row element's drawer slot, or empties the slot when it is
    /// null.
    ///
    /// <para>The slot is filled from here, never bound to <see cref="AlbumRow.Band"/>. The repeater keeps a
    /// recycled row element in the tree, still bound to the row it last showed, so a binding would give
    /// every stale copy of that row a drawer as well: two or three drawers for one open, and the copy
    /// realized first, often one off screen, would take the unfold while the one on screen appeared already
    /// open. So the slot is written for the element the repeater has PREPARED for the row (<see
    /// cref="RowPrepared"/>), and emptied when that element is cleared (<see cref="RowClearing"/>).</para>
    /// </summary>
    private static void ShowBandIn(FrameworkElement rowElement, AlbumBand? band)
    {
        if (rowElement.FindName("BandSlot") is not ContentControl slot || ReferenceEquals(slot.Content, band))
            return;
        slot.DataContext = band;
        slot.Content = band;
        if (rowElement.FindName("BandSlotHost") is FrameworkElement host)
            host.Visibility = band is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Shows row <paramref name="index"/>'s drawer, or none, in the element the repeater holds for
    /// it. A row that is not realized has nothing to show it in. It is given its drawer when it is
    /// prepared.</summary>
    private void ShowBandInRow(int index)
    {
        if (index >= 0 && index < _rows.Count && RowsRepeater.TryGetElement(index) is FrameworkElement element)
            ShowBandIn(element, _rows[index].Band);
    }

    /// <summary>The row <paramref name="band"/> is attached to, or -1.</summary>
    private int RowHolding(AlbumBand band)
    {
        for (var i = 0; i < _rows.Count; i++)
            if (ReferenceEquals(_rows[i].Band, band))
                return i;
        return -1;
    }

    /// <summary>The element showing <paramref name="band"/> now: the drawer template's root in the element
    /// the repeater holds for the band's row. It is asked for each time, never kept, because the repeater
    /// can hand the row a different element between two frames.</summary>
    private FrameworkElement? LiveBandRoot(AlbumBand band)
    {
        var index = RowHolding(band);
        if (index < 0 || RowsRepeater.TryGetElement(index) is not FrameworkElement row
            || row.FindName("BandSlot") is not ContentControl { Content: AlbumBand shown } slot
            || !ReferenceEquals(shown, band))
            return null;

        // Found by name under the slot. ContentTemplateRoot is not used: on the Skia head the template is
        // drawn by the presenter inside the control's own template, and the property stays null.
        return BandRootUnder(slot, depth: 0) is { } root && ReferenceEquals(root.DataContext, band) ? root : null;
    }

    /// <summary>The drawer template's root (<c>BandRoot</c>) under <paramref name="parent"/>. It is a few
    /// levels down: the slot, its template's presenter, then the root.</summary>
    private static FrameworkElement? BandRootUnder(DependencyObject parent, int depth)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement { Name: "BandRoot" } root)
                return root;
            if (depth < 4 && BandRootUnder(child, depth + 1) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>Whether any part of <paramref name="band"/>'s drawer is inside the viewport.</summary>
    private bool OnScreen(AlbumBand band)
    {
        if (LiveBandRoot(band) is not { ActualHeight: > 1 } root)
            return false;
        try
        {
            var top = root.TransformToVisual(Scroller).TransformPoint(default).Y;
            return top < Scroller.ViewportHeight && top + root.ActualHeight > 0;
        }
        catch (Exception)
        {
            return false;   // an element being recycled has no place in the tree to transform from
        }
    }

    // ── how far the drawer is open: read from the band, written by one motion ──

    private void BandRoot_Loaded(object sender, RoutedEventArgs e) => ApplyBandVisual(sender);

    private void BandRoot_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        => ApplyBandVisual(sender);

    /// <summary>An element has been given a band: wire what a press on it means, and show it as far open
    /// as the band is. It decides nothing. An element realized in the middle of a move shows the move
    /// where it has got to.</summary>
    private void ApplyBandVisual(object sender)
    {
        if (sender is not FrameworkElement root || root.DataContext is not AlbumBand band)
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

            // A drawer that changes size shows a different part of its sleeve under the triangle.
            view.SizeChanged -= BandView_SizeChanged;
            view.SizeChanged += BandView_SizeChanged;
        }

        if (WallDiag.On)
            WallDiag.Open($"realize {band.Card.Title} on element {root.GetHashCode():x}"
                + $", loaded={root.IsLoaded} height={band.DrawerHeight:0.#} opacity={band.DrawerOpacity:0.##}");

        ApplyDrawer(root, band);
    }

    private void BandView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AlbumBand band)
            PaintPointer(band);
    }

    /// <summary>
    /// Writes how far <paramref name="band"/>'s drawer is open onto <paramref name="root"/>. This is the
    /// only writer of the element's height, opacity and clip.
    ///
    /// <para>Height, not a render transform: growing the height pushes the rows below down as the drawer
    /// opens, so the room and the drawer arrive together. The clip comes with it, because the content is
    /// laid out at its full length and would otherwise spill out of a box that is still short. At NaN the
    /// drawer lays out at the height its content asks for, with no clip.</para>
    /// </summary>
    private void ApplyDrawer(FrameworkElement root, AlbumBand band)
    {
        root.Opacity = band.DrawerOpacity;
        var height = band.DrawerHeight;
        if (double.IsNaN(height))
        {
            root.Height = double.NaN;
            root.Clip = null;
            return;
        }

        root.Height = height;
        // The scroller's width stands in for a root that has not been laid out yet. A clip wider than the
        // element costs nothing, because its job is the cut across the bottom.
        var width = root.ActualWidth > 0 ? root.ActualWidth : Scroller.ViewportWidth;
        var rect = new Windows.Foundation.Rect(0, 0, Math.Max(0, width), Math.Max(0, height));
        if (root.Clip is RectangleGeometry clip)
            clip.Rect = rect;
        else
            root.Clip = new RectangleGeometry { Rect = rect };
    }

    /// <summary>How long the drawer takes to open. The album page and home's drawers open at the same
    /// speed (<see cref="BandUnfold"/>).</summary>
    private const double UnfoldMs = 200;

    /// <summary>How long it takes when the wall scrolls with it. Longer, because the drawer's foot travels
    /// further down the screen and the wall moves too.</summary>
    private const double UnfoldScrollingMs = 280;

    private const double FoldMs = 160;

    /// <summary>The most one frame moves a motion on: two frames at 60 a second. See <see cref="Move"/>.</summary>
    private const double MaxStepMs = 1000.0 / 30;

    /// <summary>The room kept between an opening drawer's foot and the bottom of the viewport.</summary>
    private const double OpeningAir = 16;

    /// <summary>Which motion is the current one. A motion started after this one stops this one where it
    /// is, and the newer one starts from there.</summary>
    private int _motion;

    /// <summary>When the reader last turned the wheel over the wall. A motion that scrolls the wall stops
    /// scrolling it from that moment, and leaves the wall to the reader.</summary>
    private long _wheelAt;

    private void Scroller_WheelSeen(object sender, PointerRoutedEventArgs e) => _wheelAt = Environment.TickCount64;

    /// <summary>
    /// Moves a drawer to <paramref name="toHeight"/> and <paramref name="toOpacity"/>, and, where
    /// <paramref name="scroll"/> is given, the wall with it, on ONE clock. The task completes when the
    /// motion lands, when <paramref name="ct"/> is cancelled, or when a newer motion takes over.
    ///
    /// <para>One clock, so the drawer and the wall cannot drift apart: on every frame the drawer's height
    /// and the wall's offset are the same fraction of the way to where they end. The foot of an opening
    /// drawer then moves steadily down the screen to where it stops and never leaves the screen on the way.
    /// The offset and the extent stay in step as well: the wall is never asked for an offset past an end it
    /// does not have yet.</para>
    ///
    /// <para>One step per frame drawn, not the clock. A motion read off the clock skips ahead after a
    /// frame the drawing thread spent elsewhere: a stall of 100 ms in an unfold of 200 ms moves the drawer
    /// half way in one frame. ChangeView's own animation is read off the clock in this way. So each frame
    /// moves the motion on by the time since the last frame, but never by more than <see
    /// cref="MaxStepMs"/>. At the usual frame rates that is the clock. After a stall the motion carries on
    /// from where it stood, a little later, rather than arriving at once. ChangeView's animation also has no
    /// completion to wait for. This one does.</para>
    ///
    /// <para>The element is looked up on every frame, not held. The band's own two numbers are the
    /// motion's state, so an element realized during it shows it where it has got to.</para>
    /// </summary>
    private Task Move(AlbumBand band, double toHeight, double toOpacity, double ms, bool opening,
                      (double From, double To)? scroll, CancellationToken ct)
    {
        var turn = ++_motion;
        var fromHeight = double.IsNaN(band.DrawerHeight)
            ? LiveBandRoot(band)?.ActualHeight ?? toHeight
            : band.DrawerHeight;
        var fromOpacity = band.DrawerOpacity;
        var startedAt = Environment.TickCount64;

        if (WallDiag.On)
            WallDiag.Open($"move {band.Card.Title} {fromHeight:0.#} → {toHeight:0.#} over {ms:0} ms"
                + (scroll is { } s0 ? $", wall {s0.From:0.#} → {s0.To:0.#}" : ""));

        var done = new TaskCompletionSource();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var lastMs = -1d;    // the first frame draws the start, so the motion starts on a real frame
        var spentMs = 0d;
        EventHandler<object>? tick = null;
        tick = (_, _) =>
        {
            if (ct.IsCancellationRequested || turn != _motion)
            {
                CompositionTarget.Rendering -= tick;
                done.TrySetResult();
                return;
            }

            var now = clock.Elapsed.TotalMilliseconds;
            if (lastMs >= 0)
                spentMs += Math.Min(now - lastMs, MaxStepMs);
            lastMs = now;
            var t = Math.Min(1, spentMs / ms);
            var e = BandUnfold.Ease(t, opening);

            band.DrawerHeight = fromHeight + ((toHeight - fromHeight) * e);
            band.DrawerOpacity = fromOpacity + ((toOpacity - fromOpacity) * e);
            if (LiveBandRoot(band) is { } root)
                ApplyDrawer(root, band);

            // Written after the height, in the same frame, so the layout pass that follows lengthens the
            // wall before the offset is placed against its end.
            if (scroll is { } s && _wheelAt < startedAt)
                Scroller.ChangeView(null, s.From + ((s.To - s.From) * e), null, disableAnimation: true);

            if (t < 1)
                return;
            CompositionTarget.Rendering -= tick;
            done.TrySetResult();
        };
        CompositionTarget.Rendering += tick;
        return done.Task;
    }

    /// <summary>Completes on the next frame.</summary>
    private static Task NextFrame()
    {
        var done = new TaskCompletionSource();
        EventHandler<object>? tick = null;
        tick = (_, _) =>
        {
            CompositionTarget.Rendering -= tick;
            done.TrySetResult();
        };
        CompositionTarget.Rendering += tick;
        return done.Task;
    }

    /// <summary>
    /// How tall <paramref name="band"/>'s drawer is once open, measured on the element that shows it,
    /// after it has been drawn. Zero where it could not be measured.
    ///
    /// <para>Not on the frame the element is realized. Its content is not bound yet then, so a measure
    /// answers with an empty drawer, short by the whole tracklist, and the unfold would stop short and the
    /// drawer jump to its length when the motion lets go. The sleeve also sizes itself from the
    /// tracklist's height on a later pass. So the measure is taken once a frame until two frames
    /// agree.</para>
    ///
    /// <para><c>Measure</c>, not <c>UpdateLayout</c>. A synchronous pass over the wall from inside the
    /// drawer it is about to resize does not settle. See <see cref="BandUnfold.Wanted"/>.</para>
    /// </summary>
    private async Task<double> MeasureOpen(AlbumBand band, CancellationToken ct)
    {
        var last = -1d;
        for (var frame = 0; frame < 8; frame++)
        {
            await NextFrame();
            if (ct.IsCancellationRequested)
                return 0;
            if (LiveBandRoot(band) is not { IsLoaded: true } root)
            {
                if (WallDiag.On)
                    WallDiag.Open($"measure {band.Card.Title} frame {frame}: no element showing it yet");
                continue;
            }

            // The height goes to Auto for the measure and straight back, inside one call, so no frame is
            // drawn with the drawer at its full length.
            root.Height = double.NaN;
            var full = BandUnfold.Wanted(root, fallbackWidth: Scroller.ViewportWidth);
            ApplyDrawer(root, band);

            if (WallDiag.On)
                WallDiag.Open($"measure {band.Card.Title} frame {frame}: {full:0.#}");
            if (full > 1 && Math.Abs(full - last) < 0.5)
                return full;
            last = full;
        }
        return Math.Max(0, last);
    }

    /// <summary>Where the wall scrolls while <paramref name="band"/> opens to <paramref name="full"/>, from
    /// where it is now. Null where it stays. See <see cref="AlbumGridLayout.OffsetForOpening"/>.</summary>
    private (double From, double To)? ScrollForOpening(AlbumBand band, double full)
    {
        var index = RowHolding(band);
        if (index < 0 || RowsRepeater.TryGetElement(index) is not FrameworkElement row)
            return null;

        double top;
        try
        {
            top = row.TransformToVisual(Scroller).TransformPoint(default).Y;
        }
        catch (Exception)
        {
            return null;
        }

        // The row element holds the cards and the drawer as it stands now: nought for a fresh open, the
        // last record's length for a switch in the same row.
        var drawerNow = (row.FindName("BandSlotHost") as FrameworkElement)?.ActualHeight ?? 0;
        var cards = row.ActualHeight - drawerNow;
        var from = Scroller.VerticalOffset;
        var end = Scroller.ExtentHeight + (full - drawerNow) - Scroller.ViewportHeight;
        var to = AlbumGridLayout.OffsetForOpening(
            from, top, cards, full, Scroller.ViewportHeight, end, OpeningAir);

        if (WallDiag.On)
            WallDiag.Open($"scroll for {band.Card.Title}: row top {top:0.#}, cards {cards:0.#}, drawer {drawerNow:0.#} → {full:0.#},"
                + $" viewport {Scroller.ViewportHeight:0.#}, offset {from:0.#} → {to:0.#}");
        return Math.Abs(to - from) < 1 ? null : (from, to);
    }

    /// <summary>
    /// Paints the pointer triangle in the colour the drawer's ground shows where the triangle meets it
    /// (<see cref="AlbumBandView.EdgeColourAt"/>). Left as it is until the sleeve has been read, the
    /// ground is fully in, and the drawer has a size. The position is worked out from the band, not read
    /// off the triangle, because the triangle has not been moved yet when its card moves: the layout pass
    /// that moves it comes after this.
    /// </summary>
    private void PaintPointer(AlbumBand band)
    {
        if (band.Sleeve is null || band.GroundOn < 1
            || LiveBandRoot(band)?.FindName("BandView") is not AlbumBandView view)
        {
            if (WallDiag.On)
                WallDiag.Open($"pointer {band.Card.Title} not painted: sleeve {band.Sleeve is not null},"
                    + $" groundOn {band.GroundOn:0.##}, shown {LiveBandRoot(band) is not null}");
            return;
        }
        var x = band.PointerMargin.Left + BandPointer.TriangleHalf - view.Margin.Left;
        if (view.EdgeColourAt(x) is { } colour)
            band.PointerBrush = BandPointer.Fill(colour);
        if (WallDiag.On)
            WallDiag.Open($"pointer {band.Card.Title} at {x:0.#} of {view.ActualWidth:0.#}x{view.ActualHeight:0.#}:"
                + $" edge {view.EdgeColourAt(x)?.ToString() ?? "none"}, flat {band.Sleeve.Flat}");
    }

    // ── the ground ──

    /// <summary>
    /// The grounds already read, by folder. Each ground is the sleeve's three layers, with the blur
    /// decoded.
    ///
    /// <para>A record opened a second time is dressed from here on the frame it is pressed, with nothing
    /// to read. A first opening reads its ground before its drawer is attached (see
    /// <see cref="BandConductor"/>), so neither one is seen without it.</para>
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

    /// <summary>How long the sleeve's ground takes to cross over the colour the drawer was showing. This
    /// is the album page's own figure. The two surfaces dress themselves in the same sleeve at the same
    /// speed.</summary>
    private const double BandGroundFadeMs = 260;

    /// <summary>The one live ground cross-fade. There is at most one open drawer, so there is at most one
    /// of these.</summary>
    private ChromeFade? _groundFade;

    /// <summary>
    /// Puts the sleeve's ground on the drawer: the colour, the blur, the veil, the pointer triangle and
    /// the open card's ring.
    ///
    /// <para>Whether it crosses in is read from the drawer, not stated by the caller: a drawer on screen
    /// crosses over 260 ms, and a drawer that is not on screen is simply given it. A fresh open reads its
    /// ground before its drawer is attached, so it opens already wearing it. A cross started there would
    /// run on behind the unfold, and the drawer would open in the plate's grey with its colours arriving
    /// after it. A drawer whose cover is replaced while it is open is on screen, and crosses. The ring is
    /// always on screen, so it always crosses.</para>
    /// </summary>
    private void ShowBandGround(AlbumBand band, SleeveGround ground, BitmapImage? wash)
    {
        band.Ground = new SolidColorBrush(ground.Ground);
        band.Veil = ground.Veil;
        band.Wash = wash;
        band.Sleeve = ground;

        var seen = LiveBandRoot(band) is { ActualHeight: > 1, Opacity: > 0 };

        // The triangle and the open card's ring cannot carry three layers. So they take one colour, and
        // they meet the drawer in its own tone. Neither is crossed by a second copy fading over the first:
        // two copies at part strength let the row show through the middle of the cross. Both cross by
        // being REPAINTED, on the same number that the layers use.
        var wasPoint = BandPointer.ColourOf(band.PointerBrush) ?? ground.Flat;
        var wasRing = (band.Card.Ring as SolidColorBrush)?.Color ?? ground.Flat;

        // The ring's brush is made once, and its colour is written a frame at a time. The triangle's
        // brush is replaced instead, because it is the band's PointerBrush, and a new brush is what the
        // binding hears.
        var ring = new SolidColorBrush(wasRing);
        band.Card.Ring = ring;

        _groundFade?.Stop();
        if (!seen)
        {
            band.GroundOn = 1;
            band.PointerBrush = BandPointer.Fill(ground.Flat);
            PaintPointer(band);
        }
        _groundFade = new ChromeFade(
            v =>
            {
                ring.Color = BandPointer.Blend(wasRing, ground.Flat, v);
                if (!seen)
                    return;
                band.GroundOn = v;
                band.PointerBrush = BandPointer.Fill(BandPointer.Blend(wasPoint, ground.Flat, v));
                if (v >= 1)
                    PaintPointer(band);
            },
            0);
        _groundFade.To(1, BandGroundFadeMs);
    }

    /// <summary>
    /// Fills one record's band: its sleeve, its ground, its tracklist and its two lines of text. The
    /// reading itself is in <see cref="AlbumBandLoader"/>, which is shared with every other surface that
    /// opens records. This keeps what is true of THIS wall: the ground it remembers, the breathing clock
    /// that drives the cloud marks, and which line is playing.
    /// </summary>
    private async Task<IReadOnlyList<AlbumTrackItem>> LoadBandAsync(AlbumBand band, CancellationToken ct)
    {
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
            loadCover: true,
            // The wall is a wall of records. A record opened on it is the subject, so all of its tracks are
            // loaded.
            BandTracks.Whole,
            ct);

        if (items.Count == 0 || ct.IsCancellationRequested)
            return items;

        // A record opened while some of its tracks are uploading starts breathing straight away. The
        // marks were given to the lines when they were built.
        _lineBreath.Sync();
        ApplyPlaying(items);
        return items;
    }

    /// <summary>
    /// The drawer standing on the record's own sleeve, exactly as the album page does it. The mean colour
    /// is under everything. The sleeve is blurred over it, and the veil sits inside the blur. Both
    /// surfaces use the same reader (<see cref="SleeveGround"/>). It runs on a worker thread, because a
    /// blur and an average are two decodes.
    ///
    /// <para>This is skipped outright where the record's ground is already remembered. The seed has
    /// already put it up, whole, on the frame the record was pressed.</para>
    ///
    /// <para>The wall owns this method, not the loader. The READING is shared. It is a property of the
    /// sleeve, and <see cref="AlbumBandLoader.ReadGroundAsync"/> is where it lives. But the cache it is
    /// stored in belongs to this wall.</para>
    /// </summary>
    private async Task GroundAsync(AlbumBand band, byte[] bytes, CancellationToken ct)
    {
        if (_grounds.ContainsKey(band.Card.Folder)
            || await AlbumBandLoader.ReadGroundAsync(bytes, ct) is not { } read
            || ct.IsCancellationRequested)
            return;

        RememberGround(band.Card.Folder, read.Ground, read.Wash);
        ShowBandGround(band, read.Ground, read.Wash);
    }
}
