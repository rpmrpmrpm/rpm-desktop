using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RPM.Desktop.Presentation.Main.Library.Models;

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// The cards slide to their new places when a pane move changes the wall's column count.
/// </summary>
/// <remarks>
/// <para><b>What it fixes.</b> A change of column count deals the records into rows again, and nearly every
/// card on screen changes row and column in one frame. The wall seems to jump all over the place, however
/// cheap the frame is. A pane move holds one column count for its whole length (see
/// <c>AlbumWallView.ExpectLiveWidth</c>), so there is one change per move: at its start when the wall will be
/// wider, at its landing when it will be narrower. This is what that one change looks like.</para>
///
/// <para><b>How.</b> Just before the change, the place each card in view is drawn at is written down
/// (<see cref="PrepareGlide"/>). It is worked out, not read from the tree: a card's place is its column times
/// the card width and its row times the row pitch, both as they are drawn. The rows in view are dealt at once
/// (see <c>Regroup</c>). After the layout pass that gives them their new elements, each element is moved back
/// by the compositor to where its card was, and slides from there to its slot (<see cref="StartGlides"/>). A
/// card that was not in view fades in. Nothing here is layout: the slide is a <c>Translation</c> on the
/// element's visual.</para>
///
/// <para><b>A second change during a slide.</b> A card that is still on its way is drawn short of its slot.
/// Its place is worked out from the clock (<see cref="Remaining"/>), not read back from the compositor,
/// because reading an animated property returns the last value that was set, not the one on screen.</para>
///
/// <para><b>Elements are recycled.</b> A slide belongs to a card, but it runs on an element, and the repeater
/// hands that element to other cards. So an element that is given another card stops its slide
/// (<see cref="StopGlide"/>, called from <c>Card_DataContextChanged</c>), and a new slide is always started
/// from a value worked out for the card the element shows now.</para>
///
/// <para><b>An open drawer.</b> The drawer hangs under its record's row, so a column change moves it to
/// another row, and its pointer to another column. It slides there with the cards (<c>SlideBand</c>).
/// Its slot in the row carries it up or down, and the pointer inside it slides across.</para>
///
/// <para><b>Both heads.</b> The slides are composition animations on each element's visual (see
/// <see cref="Common.TileGlide"/>, which also says where each head puts the shift). On WinUI the system
/// compositor runs them. On the Skia head Uno's compositor runs them on the drawing thread, frame by frame, but
/// they still cause no layout.</para>
/// </remarks>
public sealed partial class AlbumWallView
{
    /// <summary>How long a card takes to reach its new place. See <see cref="Common.TileGlide.SlideMs"/>.</summary>
    private const double GlideMs = Common.TileGlide.SlideMs;

    /// <summary>Where each card in view was drawn just before the column change, measured from the top of the
    /// viewport, and how wide it was drawn. Null when no slide is waiting to start.</summary>
    private Dictionary<AlbumCard, (Vector2 At, float Width)>? _glideFrom;

    /// <summary>The slides that are running, by card: how far the card started from its slot, at what size
    /// against its slot's, and when. Used to work out where a card is in the middle of its slide.</summary>
    private readonly Dictionary<AlbumCard, (Vector2 Offset, float Size, long Started)> _glides = new();

    /// <summary>The slides and fades on the cards' elements, and on the drawer's slot and pointer.</summary>
    private readonly Common.TileGlide _tileGlide = new();

    private bool _glideWaiting;
    private int _glideTries;

    /// <summary>Where the open drawer was drawn just before the column change: the top of its slot, measured
    /// from the top of the viewport, and the middle of the card its pointer points at, measured from the left
    /// of its row. Null when no drawer is open, or no slide is waiting to start.</summary>
    private BandPlace? _bandFrom;

    private readonly record struct BandPlace(AlbumBand Band, float Top, float PointerX);

    /// <summary>The drawer's slide while it runs: how far it started from its place (across for the pointer,
    /// up or down for the drawer), and when. Null otherwise.</summary>
    private (AlbumBand Band, Vector2 Offset, long Started)? _bandGlide;

    /// <summary>The drawer's slot and its pointer, while they have a slide, so that it can be stopped.</summary>
    private FrameworkElement? _bandSlot, _bandPointer;

    /// <summary>Writes down where each card in view is drawn now, measured from the top of the viewport.
    /// <paramref name="drawnStride"/> is the card width as it is drawn, scale included, before the pass that
    /// changes it.</summary>
    private void PrepareGlide(double drawnStride)
    {
        _glideFrom = null;
        _bandFrom = null;
        if (Freeze != WallFreeze.Scale || drawnStride <= 0 || _rows.Count == 0)
            return;

        var (top, bottom) = ViewNow();
        var first = Math.Max(0, RowsLayout.RowDrawnAt(top, _rows.Count));
        var last = Math.Max(first, RowsLayout.RowDrawnAt(bottom, _rows.Count));
        var now = System.Diagnostics.Stopwatch.GetTimestamp();

        var from = new Dictionary<AlbumCard, (Vector2, float)>();
        for (var r = first; r <= last; r++)
        {
            var cards = _rows[r].Cards;
            var y = (float)(RowsLayout.TopOf(r) - top);
            for (var j = 0; j < cards.Count; j++)
            {
                var (offset, size) = Remaining(cards[j], now);
                from[cards[j]] = (new Vector2((float)(j * drawnStride), y) + offset, (float)drawnStride * size);
            }
        }
        _glideFrom = from;

        if (AttachedBand() is { } band && RowsLayout.BandRow is var bandRow)
        {
            var left = BandRemaining(band, now);
            _bandFrom = new BandPlace(band,
                (float)(BandTop(bandRow) - top) + left.Y,
                (float)((band.ColumnIndex + 0.5) * drawnStride) + left.X);
        }
    }

    /// <summary>Where the drawer under row <paramref name="row"/> is drawn: below the row's cards, which are
    /// one row height tall before the scale.</summary>
    private double BandTop(int row) => RowsLayout.TopOf(row) + (RowsLayout.Pitch - RowsLayout.Spacing) * RowsLayout.Scale;

    /// <summary>How far the drawer still is from its place on screen at <paramref name="now"/>. Nothing when
    /// it is not sliding.</summary>
    private Vector2 BandRemaining(AlbumBand band, long now)
    {
        if (_bandGlide is not { } glide || !ReferenceEquals(glide.Band, band))
            return Vector2.Zero;
        var t = System.Diagnostics.Stopwatch.GetElapsedTime(glide.Started, now).TotalMilliseconds / GlideMs;
        return t >= 1 ? Vector2.Zero : glide.Offset * (float)(1 - PaneGlide.EaseAt(t));
    }

    /// <summary>How far a card still is from its slot on screen at <paramref name="now"/>, and its size
    /// against its slot's. Nothing and one for a card that is not sliding.</summary>
    private (Vector2 Offset, float Size) Remaining(AlbumCard card, long now)
    {
        if (!_glides.TryGetValue(card, out var glide))
            return (Vector2.Zero, 1f);
        var t = System.Diagnostics.Stopwatch.GetElapsedTime(glide.Started, now).TotalMilliseconds / GlideMs;
        if (t >= 1)
        {
            _glides.Remove(card);
            return (Vector2.Zero, 1f);
        }
        var left = (float)(1 - PaneGlide.EaseAt(t));
        return (glide.Offset * left, 1 + (glide.Size - 1) * left);
    }

    /// <summary>Starts the slides once the layout pass after the re-deal has given the cards their
    /// elements.</summary>
    private void StartGlidesAfterLayout()
    {
        _glideTries = 0;
        if (_glideWaiting)
            return;
        _glideWaiting = true;
        RowsRepeater.LayoutUpdated += OnGlideLayout;
    }

    private void OnGlideLayout(object? sender, object e)
    {
        if (!StartGlides() && ++_glideTries < 4)
            return;   // the rows in view have not all got their new elements yet; the next pass has them
        StopWaitingForGlides();
    }

    private void StopWaitingForGlides()
    {
        RowsRepeater.LayoutUpdated -= OnGlideLayout;
        _glideWaiting = false;
        _glideFrom = null;
        _bandFrom = null;
    }

    /// <summary>Moves each card in view back to where it was drawn, and slides it to its slot. Returns false,
    /// and does nothing, while any row in view is still showing elements that were bound to other
    /// cards.</summary>
    private bool StartGlides()
    {
        if (_glideFrom is not { } from)
            return true;
        // During a move the cards are laid out at the stride the move began with and drawn through the scale.
        // At rest they are laid out at their own stride, at a scale of one.
        var scale = RowsLayout.Scale;
        var drawnStride = (_liveStride > 0 ? _liveStride : _metrics.Stride) * scale;
        if (RowsLayout.Pitch <= 0 || drawnStride <= 0 || _rows.Count == 0)
            return true;

        // Both ends are measured from the top of the viewport, so a scroll that holds the reader (see
        // HoldReader) is part of the slide rather than a jump.
        var view = ViewNow();
        var first = Math.Max(0, RowsLayout.RowDrawnAt(view.Top, _rows.Count) - 1);
        var last = Math.Min(_rows.Count - 1, RowsLayout.RowDrawnAt(view.Bottom, _rows.Count) + 1);

        var work = new List<(FrameworkElement Element, AlbumCard Card, Vector2 Slot)>();
        for (var r = first; r <= last; r++)
        {
            if (!InView(r, view))
                continue;
            if (RowsRepeater.TryGetElement(r) is not Panel { Children.Count: > 0 } row
                || row.Children[0] is not ItemsRepeater inner)
                return false;
            var cards = _rows[r].Cards;
            for (var j = 0; j < cards.Count; j++)
            {
                if (inner.TryGetElement(j) is not FrameworkElement element
                    || !ReferenceEquals(element.DataContext, cards[j]))
                    return false;
                work.Add((element, cards[j], new Vector2((float)(j * drawnStride), (float)(RowsLayout.TopOf(r) - view.Top))));
            }
        }

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        int slid = 0, faded = 0;
        foreach (var (element, card, slot) in work)
        {
            if (from.TryGetValue(card, out var was))
            {
                var offset = was.At - slot;
                // The card also starts at the size it was drawn at. A change to more columns makes every card
                // smaller in the same frame, and without this the cards shrink in their old places first.
                var size = was.Width / (float)drawnStride;
                if (offset.LengthSquared() < 0.25f && Math.Abs(size - 1) < 0.005f)
                {
                    StopGlide(element);
                    _glides.Remove(card);
                    continue;
                }
                // The element sits inside its row, which is drawn at the scale. A translation on the element
                // is in the row's own units, so it is the distance on screen divided by the scale.
                Slide(element, offset / (float)scale, size);
                _glides[card] = (offset, size, now);
                slid++;
            }
            else
            {
                Fade(element);
                faded++;
            }
        }
        var drawer = SlideBand(view, drawnStride, scale, now);

        // A move's own slides are in its line on the panes channel. The landing's come after that line is said.
        if (_liveDeals.Counting)
        {
            _liveDeals.Slid += slid;
            _liveDeals.Faded += faded;
            _liveDeals.Drawer |= drawer;
        }
        else
        {
            RPM.Core.Diagnostics.Scope.Said("panes",
                $"wall slid {slid} cards{(drawer ? " and the drawer" : "")} and faded in {faded} after the landing");
        }
        return true;
    }

    /// <summary>
    /// Slides the open drawer from where it was drawn to the row its record is in now, and its pointer to the
    /// record's new column. Returns whether anything was given a slide.
    /// </summary>
    /// <remarks>
    /// The drawer's slot in the row (<c>BandSlotHost</c>, see <c>RowTemplate</c>) carries the drawer up or
    /// down. The pointer is inside the slot, so it moves up or down with it, and slides across by
    /// itself. Both are in the row's own units, which the row draws at the scale.
    /// </remarks>
    private bool SlideBand((double Top, double Bottom) view, double drawnStride, double scale, long now)
    {
        StopBandGlide();
        if (_bandFrom is not { } was || !ReferenceEquals(AttachedBand(), was.Band) || RowsLayout.BandRow is not (var bandRow and >= 0)
            || RowsRepeater.TryGetElement(bandRow) is not FrameworkElement row
            || row.FindName("BandSlotHost") is not FrameworkElement slot)
            return false;

        var offset = new Vector2(
            was.PointerX - (float)((was.Band.ColumnIndex + 0.5) * drawnStride),
            was.Top - (float)(BandTop(bandRow) - view.Top));
        var slid = false;
        if (Math.Abs(offset.Y) >= 0.5f)
        {
            Slide(slot, new Vector2(0, offset.Y / (float)scale), 1);
            _bandSlot = slot;
            slid = true;
        }
        if (Math.Abs(offset.X) >= 0.5f
            && LiveBandRoot(was.Band)?.FindName("BandPoint") is FrameworkElement pointer)
        {
            Slide(pointer, new Vector2(offset.X / (float)scale, 0), 1);
            _bandPointer = pointer;
            slid = true;
        }
        if (slid)
            _bandGlide = (was.Band, offset, now);
        return slid;
    }

    /// <summary>Puts the drawer's slot and pointer back where layout put them.</summary>
    private void StopBandGlide()
    {
        if (_bandSlot is { } slot)
            StopGlide(slot);
        if (_bandPointer is { } pointer)
            StopGlide(pointer);
        _bandSlot = null;
        _bandPointer = null;
        _bandGlide = null;
    }

    /// <summary>Starts <paramref name="element"/> <paramref name="offset"/> away from its slot, in its row's
    /// units, at <paramref name="size"/> times its own size, and slides it home.</summary>
    private void Slide(FrameworkElement element, Vector2 offset, float size) => _tileGlide.Slide(element, offset, size);

    private void Fade(FrameworkElement element) => _tileGlide.Fade(element);

    /// <summary>Puts an element that was given a slide or a fade back where layout put it, at full
    /// opacity. Does nothing for an element that was never given one.</summary>
    private void StopGlide(FrameworkElement element) => _tileGlide.Stop(element);
}
