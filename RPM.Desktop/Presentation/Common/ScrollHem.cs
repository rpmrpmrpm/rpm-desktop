using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace RPM.Desktop.Presentation.Common;

/// <summary>A scroller whose two edges fade away by as much as there is left to scroll. Where the
/// whole list fits, nothing fades. Where there is more above or below, a full soft edge shows at that
/// end.
///
/// <para>This works on either axis. A list scrolls down, and has its soft edges at the top and the
/// foot. A row of records scrolls sideways, and has them at its left and right ends. The arithmetic is
/// the same both ways. See <see cref="Depths"/>, which takes an offset and a distance. It does not know
/// which axis it answers for.</para>
///
/// <para>The soft edge tells the reader something about the list. That is why it is measured, not just
/// decoration. A permanent gradient at the foot says "there is more" even on a record with three songs.
/// A hard edge says nothing at all on a record with thirty. A soft edge that grows from the scroll that
/// is actually available says the one thing the reader needs to know. It stops showing when that is no
/// longer true.</para>
///
/// <para>The edge uses depth, not opacity. That is the main reason it looks right. A mask at half
/// strength is not half a fade. It is a veil over the whole band, and every word in it is half
/// transparent, including the words far from the edge. So an edge that is not needed yet is SHORT, not
/// faint. It grows to its full depth as the list is scrolled.</para>
///
/// <para>XAML cannot do this directly. An OpacityMask over the scroller would fade the SCROLLED
/// CONTENT. What is wanted is a fixed band at a fixed place on the viewport, with the live content
/// running through it. So each edge is a copy of the scroller's own visual, cut to its band and painted
/// back through a gradient ramp. The band that is copied is also taken out of the live scroller, by one
/// inset clip. Otherwise the reader would see the songs twice, once solid and once faded.</para>
/// </summary>
internal sealed class ScrollHem
{
    /// <summary>How deep the soft edge at the end of a LIST is at full stretch, and the distance that
    /// end has to be scrolled to reach it. This is one list line. The edge then tells the reader about
    /// a row they cannot see, which is the only thing it has to say.
    ///
    /// <para>A row of records that scrolls sideways has a deeper edge of its own. A sleeve is much
    /// wider than a list line is tall, and an edge that looks right beside a list line looks like a
    /// smudge beside a sleeve. See HomeView.RowHemDepth.</para></summary>
    public const double Depth = 22;

    /// <summary>How far each edge reaches back over the live rows, and why the two axes give different
    /// answers.
    ///
    /// <para>On a vertical list it is one point. Two edges that meet on the same sub-pixel line do not
    /// meet exactly when the display scale is fractional. The gap between them is a thin line of row
    /// that is not faded. It shows as a moving line while the list scrolls.</para>
    ///
    /// <para>On a horizontal row it is nought, because the overlap there is not free. Both the live row
    /// and the band's copy are drawn in that spot. What they draw is a card's plate: translucent white
    /// over the page's glass. So the plate is laid down twice, and the join shows as a bright vertical
    /// line beside the key at that end. Over a list's text, two copies of the same text 22 points deep
    /// are not visible. Over a sleeve's plate at 48 points deep, the doubling is the first thing the
    /// reader sees. Meeting the clip exactly can leave a sub-pixel line of the page's own ground
    /// instead. That line is thinner and dimmer than the line it replaces.</para>
    /// </summary>
    private static float SeamFor(Orientation axis) => axis == Orientation.Vertical ? 1f : 0f;

    private readonly Orientation _axis;
    private readonly double _depth;
    private readonly ScrollViewer _live;
    private readonly FrameworkElement _head;
    private readonly FrameworkElement _foot;
    private readonly CompositionVisualSurface _headCopy;
    private readonly CompositionVisualSurface _footCopy;
    private readonly SpriteVisual _headSprite;
    private readonly SpriteVisual _footSprite;
    private readonly InsetClip _cut;

    private ScrollHem(
        Orientation axis, double depth, ScrollViewer live, FrameworkElement head, FrameworkElement foot,
        CompositionVisualSurface headCopy, SpriteVisual headSprite,
        CompositionVisualSurface footCopy, SpriteVisual footSprite, InsetClip cut)
    {
        _axis = axis;
        _depth = depth;
        _live = live;
        _head = head;
        _foot = foot;
        _headCopy = headCopy;
        _headSprite = headSprite;
        _footCopy = footCopy;
        _footSprite = footSprite;
        _cut = cut;
    }

    /// <summary>Builds the two copies, two ramps and one cut for one scroller. Returns null where the
    /// machine's composition stack cannot do it. The caller's list then keeps hard edges, which is the
    /// same as having no edge at all.
    /// </summary>
    /// <param name="live">The scroller being hemmed.</param>
    /// <param name="cut">The element that the inset clip is set on. It must contain <paramref
    /// name="live"/>, and nothing in it should stay unclipped. One clip serves both ends, because an
    /// InsetClip takes an inset on each side separately.
    ///
    /// <para>It must also be at least as big as <paramref name="live"/> on the other axis. An InsetClip
    /// clips to the element's own box. So a scroller that hangs past its cut, for example with a
    /// negative margin to put its bar in the air beside the column, has everything past that box cut
    /// away, including the bar. Nothing reports this. The album page did exactly that and drew no
    /// scrollbar on its tracklist. The fix was to write the overhang on the CUT, and let the scroller
    /// fill it. See AlbumDetailView.ApplyStacking.</para></param>
    /// <param name="head">The band at the start of the scroll: the top of a list, or the left end of a
    /// row. Its depth is set here, as its Height going down and as its Width going across.</param>
    /// <param name="foot">The band at the other end, set in the same way.</param>
    /// <param name="axis">Which way the scroller scrolls. The caller states it. The two bands are laid
    /// out in the caller's markup, aligned Top and Bottom, or Left and Right. An edge that read the
    /// axis from the scroller could disagree with the markup it is painted into.</param>
    /// <param name="depth">Either edge at full stretch. This is stated too, because how deep a soft end
    /// must be to look right depends on what it is fading. <see cref="Depth"/> is the right value for a
    /// list of lines, and too small for a row of sleeves.</param>
    public static ScrollHem? Attach(
        ScrollViewer live, UIElement cut, FrameworkElement head, FrameworkElement foot,
        Orientation axis, double depth)
    {
        try
        {
            var compositor = ElementCompositionPreview.GetElementVisual(head).Compositor;
            var source = ElementCompositionPreview.GetElementVisual(live);

            var clip = compositor.CreateInsetClip();
            ElementCompositionPreview.GetElementVisual(cut).Clip = clip;

            var (headCopy, headSprite) = Band(head, top: true);
            var (footCopy, footSprite) = Band(foot, top: false);

            return new ScrollHem(
                axis, depth, live, head, foot, headCopy, headSprite, footCopy, footSprite, clip);

            (CompositionVisualSurface Copy, SpriteVisual Sprite) Band(UIElement host, bool top)
            {
                var copy = compositor.CreateVisualSurface();
                copy.SourceVisual = source;

                // The alignment is set to zero here, and is not left to the brush. The offset alone
                // says which band is wanted. An alignment ratio would be a second way of saying the
                // same thing, and the two would add up.
                var pixels = compositor.CreateSurfaceBrush(copy);
                pixels.Stretch = CompositionStretch.None;
                pixels.HorizontalAlignmentRatio = 0;
                pixels.VerticalAlignmentRatio = 0;

                // Only the alpha of the ramp is used. It runs towards the edge that the rows are
                // leaving by. The head's ramp is transparent at the top, and the foot's is transparent
                // at the bottom. On a row the same rule applies sideways: the left band is transparent
                // at its left edge.
                var ramp = compositor.CreateLinearGradientBrush();
                ramp.MappingMode = CompositionMappingMode.Relative;
                ramp.StartPoint = new System.Numerics.Vector2(0, 0);
                ramp.EndPoint = axis == Orientation.Vertical
                    ? new System.Numerics.Vector2(0, 1)
                    : new System.Numerics.Vector2(1, 0);
                ramp.ColorStops.Add(compositor.CreateColorGradientStop(
                    0f, top ? Microsoft.UI.Colors.Transparent : Microsoft.UI.Colors.White));
                ramp.ColorStops.Add(compositor.CreateColorGradientStop(
                    1f, top ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Transparent));

                var mask = compositor.CreateMaskBrush();
                mask.Source = pixels;
                mask.Mask = ramp;

                var sprite = compositor.CreateSpriteVisual();
                sprite.Brush = mask;
                ElementCompositionPreview.SetElementChildVisual(host, sprite);
                return (copy, sprite);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Shows both edges, each as deep as the scroll allows at that end. The head appears once
    /// the list has been scrolled off its first row. The foot goes once the last row has come into
    /// view. A list that fits shows neither edge. That is what gives the edges their meaning.
    /// </summary>
    public void Show()
    {
        var (head, foot) = _axis == Orientation.Vertical
            ? Depths(_live.VerticalOffset, _live.ScrollableHeight, _depth)
            : Depths(_live.HorizontalOffset, _live.ScrollableWidth, _depth);
        Show(head, foot);
    }

    /// <summary>How deep each edge is for a scroller at <paramref name="offset"/>, with <paramref
    /// name="scrollable"/> left to travel in total. This gives what is above, and what is below, each
    /// capped at <paramref name="depth"/>.
    ///
    /// <para>Both are nought where the whole list fits. An edge says there is more, and there is no
    /// more. Both values are read from the scroller's own numbers. They are not read from a flag that
    /// was set when rows were added, so the answer is true whenever it is asked.</para>
    /// </summary>
    public static (double Head, double Foot) Depths(double offset, double scrollable, double depth)
        => (Math.Clamp(offset, 0, depth), Math.Clamp(scrollable - offset, 0, depth));

    /// <summary>Points each copy at its own band of the scroller, and cuts both bands out of the live
    /// one.
    ///
    /// <para>A surface draws the visual it was given from that visual's own origin, less the
    /// SourceOffset. So the head's band is asked for at zero. The foot's band is asked for by
    /// offsetting down by everything above it.</para>
    /// </summary>
    public void Show(double top, double bottom)
    {
        var width = (float)_live.ActualWidth;
        var height = (float)_live.ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        // How much of the scroller lies along the axis it scrolls on: its height going down, its width
        // going across. Everything below is that one number.
        var span = _axis == Orientation.Vertical ? height : width;

        // Neither edge may be deeper than the scroller it is fading, and the two together may not be
        // either. A list only a little taller than one edge would otherwise have its head asking for
        // pixels that its foot had already claimed. Then every row between them would be masked twice.
        var head = (float)Math.Clamp(top, 0, span);
        var foot = (float)Math.Clamp(bottom, 0, Math.Max(0, span - head));

        if (_axis == Orientation.Vertical)
        {
            _head.Height = head;
            _foot.Height = foot;

            _cut.TopInset = Math.Max(0, head - SeamFor(_axis));
            _cut.BottomInset = Math.Max(0, foot - SeamFor(_axis));

            BandSurface.Fit(_headCopy, _headSprite, width, head, 0);
            BandSurface.Fit(_footCopy, _footSprite, width, foot, height - foot);
            return;
        }

        _head.Width = head;
        _foot.Width = foot;

        _cut.LeftInset = Math.Max(0, head - SeamFor(_axis));
        _cut.RightInset = Math.Max(0, foot - SeamFor(_axis));

        BandSurface.Across(_headCopy, _headSprite, height, head, 0);
        BandSurface.Across(_footCopy, _footSprite, height, foot, width - foot);
    }
}
