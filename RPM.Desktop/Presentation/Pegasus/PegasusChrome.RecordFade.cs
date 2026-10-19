using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

using RPM.Desktop.Presentation.Common;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// The two ends of an open record's running order, dissolving instead of being cut off square.
///
/// <para>The list has no scrollbar, because a grey rail down the side of a record over a visualizer
/// does not suit the screen. So the dissolve does the bar's job as well as its own: it is the only
/// thing that shows there is more list than screen, and which end it is at.</para>
///
/// <para>A dissolve is not a dark gradient laid over the ends. This screen is washed in the colours
/// of the record's own sleeve, so there is no single colour to fade TO, and a gradient that picked one
/// would look like a shadow on a bright wash and not like the list fading out. (The same reasoning
/// applies to PegasusChrome.TopFade and to PhotoFootFade in the track info pane.)</para>
///
/// <para>So the pixels are taken away. A CompositionVisualSurface holds a live copy of the list's own
/// rendering, a CompositionMaskBrush multiplies that copy by a ramp, and a sprite paints the result back
/// in the band. The live list is cut out of the same band by an InsetClip on the wrapper around it. The
/// cut cannot go on the scroller being copied, because the copy is that visual's own rendering, clip
/// included. That is what RecordTrackCut is for.</para>
///
/// <para>Nothing runs until the list is actually scrolled, and nothing is built until the first time it
/// is: at rest both depths are zero, no band is cut, no copy is taken, and the screen is drawn
/// normally. A visual surface is a second rendering of the list on every frame it is up, and this pays
/// for it only while somebody is reading past the end of a record.</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>How deep either dissolve goes at full stretch, before the drawer's own scale, about the
    /// height of one track line. If it is deeper, the first title of a record is always half hidden;
    /// if it is shallower, the band looks like an edge of its own, which is what it exists to
    /// remove.</summary>
    private const double RecordFadeDepth = 30;

    private CompositionVisualSurface? _trackHeadCopy, _trackFootCopy;
    private SpriteVisual? _trackHeadSprite, _trackFootSprite;
    private InsetClip? _trackCut;

    /// <summary>Set when the composition this is built out of is not there. A machine without it keeps the
    /// square ends, which are still readable.</summary>
    private bool _noRecordFade;

    private void RecordScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        => UpdateRecordFades();

    /// <summary>The bands are as wide as the list, and the list width can change (see
    /// SizeRecord, which sets it).</summary>
    private void RecordScroll_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateRecordFades();

    /// <summary>Builds the two copies, the two ramps and the one cut, the first time a record is
    /// scrolled. One cut serves both ends: an InsetClip takes an inset on each side
    /// independently.</summary>
    private void EnsureRecordFades()
    {
        if (_trackHeadSprite is not null || _noRecordFade)
            return;

        try
        {
            var compositor = ElementCompositionPreview.GetElementVisual(RecordTracksTopFade).Compositor;
            var list = ElementCompositionPreview.GetElementVisual(RecordScroll);

            (_trackHeadCopy, _trackHeadSprite) = Band(RecordTracksTopFade, head: true);
            (_trackFootCopy, _trackFootSprite) = Band(RecordTracksFootFade, head: false);

            _trackCut = compositor.CreateInsetClip();
            ElementCompositionPreview.GetElementVisual(RecordTrackCut).Clip = _trackCut;

            (CompositionVisualSurface Copy, SpriteVisual Sprite) Band(UIElement host, bool head)
            {
                // Sourced from the scroller's own visual, so what it holds is the list already scrolled
                // and already clipped to its viewport: the band at the top of the copy is the band at the
                // top of the screen, and nothing here has to know where the list has got to.
                var copy = compositor.CreateVisualSurface();
                copy.SourceVisual = list;

                // Painted at its own size from its own top left corner. A surface brush centres and
                // scales what it is given by default, which for a band cut out of a taller list would show
                // the middle of the record, stretched.
                var pixels = compositor.CreateSurfaceBrush(copy);
                pixels.Stretch = CompositionStretch.None;
                pixels.HorizontalAlignmentRatio = 0;
                pixels.VerticalAlignmentRatio = 0;

                // Only the ALPHA of the ramp is read, and it runs out towards the edge the titles are
                // leaving by: nothing at the very top of the head's band, nothing at the very foot of the
                // foot's. Relative, so it stretches with the band as the dissolve comes in.
                var ramp = compositor.CreateLinearGradientBrush();
                ramp.MappingMode = CompositionMappingMode.Relative;
                ramp.StartPoint = new Vector2(0, 0);
                ramp.EndPoint = new Vector2(0, 1);
                ramp.ColorStops.Add(compositor.CreateColorGradientStop(
                    0f, head ? Microsoft.UI.Colors.Transparent : Microsoft.UI.Colors.White));
                ramp.ColorStops.Add(compositor.CreateColorGradientStop(
                    1f, head ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Transparent));

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
            // A backend without visual surfaces or mask brushes. Asked once and then left alone.
            _noRecordFade = true;
        }
    }

    /// <summary>
    /// How much of either end is dissolving just now: nothing until the list moves, then deeper over the
    /// first few points of the scroll until the band is at its full depth.
    ///
    /// <para>The depth grows with the scroll and is not switched on at once, because a record at its top
    /// has nothing running off it, and a dissolve over the first title of a list nobody has touched
    /// would make the record arrive already faded. The same applies at the other end in reverse: the foot
    /// goes once the last track has been reached, because there is nothing below it to continue
    /// into.</para>
    ///
    /// <para>The control is DEPTH and not opacity, and the difference matters: a mask at half strength
    /// is not half a dissolve. It is a veil over the whole band, and every title in it would go half
    /// transparent, including the ones nowhere near the edge.</para>
    /// </summary>
    private void UpdateRecordFades()
    {
        if (!RecordUp)
            return;

        EnsureRecordFades();
        if (_trackCut is null || _trackHeadCopy is null || _trackFootCopy is null
            || _trackHeadSprite is null || _trackFootSprite is null)
        {
            return;
        }

        var width = (float)RecordScroll.ActualWidth;
        var height = (float)RecordScroll.ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        var full = RecordFadeDepth * QueueScale;
        var above = RecordScroll.VerticalOffset;
        var below = RecordScroll.ScrollableHeight - above;

        // Neither band may be deeper than the list it is dissolving, and the two together may not be
        // either: in a panel barely taller than one band, the head would ask for pixels its
        // foot had already claimed, and every line between them would be masked twice.
        var head = (float)Math.Clamp(above, 0, Math.Min(full, height));
        var foot = (float)Math.Clamp(below, 0, Math.Min(full, Math.Max(0, height - head)));

        RecordTracksTopFade.Height = head;
        RecordTracksFootFade.Height = foot;

        // Each band overlaps its cut by a point rather than meeting it exactly. Two edges asked to land on
        // the same sub-pixel line will not, once a fractional display scale is involved, and what falls
        // between them is a hairline of undissolved titles that moves while the list is scrolled. The
        // point being overlapped is the opaque end of the ramp, so it costs nothing.
        _trackCut.TopInset = Math.Max(0, head - RecordFadeSeam);
        _trackCut.BottomInset = Math.Max(0, foot - RecordFadeSeam);

        // A surface renders the visual it was given from that visual's own origin, less whatever
        // SourceOffset says. So the head's band is asked for at zero, and the foot's by offsetting down
        // by everything above it.
        BandSurface.Fit(_trackHeadCopy, _trackHeadSprite, width, head, 0);
        BandSurface.Fit(_trackFootCopy, _trackFootSprite, width, foot, height - foot);
    }

    /// <summary>How far each band reaches back over its own cut. One point: enough that two edges rounded
    /// differently cannot leave a gap between them, small enough to fall inside the opaque end of the
    /// ramp.</summary>
    private const float RecordFadeSeam = 1f;
}
