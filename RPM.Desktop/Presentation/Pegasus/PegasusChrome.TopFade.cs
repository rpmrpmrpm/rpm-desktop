using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

using RPM.Desktop.Presentation.Common;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// The top of the open album, fading out as it goes under the head of the drawer.
///
/// <para>A list is clipped square where its panel ends. Under the head bar that square edge looks like a
/// slice cut out of the album. The last few points of the list should fade instead. A dark gradient
/// laid over the top does not do this. Over the near-black queue it only works because the panel behind
/// is the same colour as the gradient. Over an album, whose drawer is tinted with the colours of its own
/// cover, it looks like a shadow on a bright picture.</para>
///
/// <para>So the pixels are really removed, which XAML cannot do: WinUI has no OpacityMask. Composition
/// can. A CompositionVisualSurface holds a live copy of the album screen's own rendering, a
/// CompositionMaskBrush multiplies that copy by a transparent-to-white ramp, and a sprite paints the
/// result in the band under the head. Whatever is behind the drawer (the tint, the frosted glass, the
/// room) shows through, because nothing is painted over it.</para>
///
/// <para>The live list has to be cut away in the same band, or it would still show there at full
/// opacity. The cut cannot go on the screen that is copied: the copy is that visual's own rendering,
/// clip included, so a cut there would also cut the copy. It goes on the wrapper around the screen (see
/// AlbumScreen in the XAML), which the copy does not include.</para>
///
/// <para>None of this runs until the album is scrolled. At rest the depth is zero, no band is cut, no
/// copy is taken, and the screen is drawn normally. This also limits the cost: a surface is a second
/// rendering of the list on every frame, so it exists only while an album is being scrolled.</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>How deep the fade goes, in points before the drawer's scale. About the height of one
    /// row. A deeper fade leaves the first row of an album half hidden all the time. A shallower fade
    /// looks like an edge itself, which is what the fade is meant to remove.</summary>
    private const double TopFadeDepth = 26;

    /// <summary>The live copy of the album screen, the sprite that paints it back with the mask, and the
    /// cut that removes the original from the same band. Built once, on the first scroll.</summary>
    private CompositionVisualSurface? _topFadeCopy;
    private SpriteVisual? _topFadeSprite;
    private InsetClip? _albumScreenCut;

    /// <summary>Set when the composition features used here are not available. The drawer then keeps
    /// its square edge, and the album is still readable.</summary>
    private bool _noTopFade;

    /// <summary>Builds the copy, the ramp and the cut, the first time an album is scrolled.</summary>
    private void EnsureTopFade()
    {
        if (_topFadeSprite is not null || _noTopFade)
            return;

        try
        {
            var compositor = ElementCompositionPreview.GetElementVisual(DrawerTopFade).Compositor;

            // The copy. Its source is the ScrollViewer's own visual, so it holds the list already
            // scrolled and already clipped to its viewport. The band at the top of the copy is the band
            // at the top of the screen, so nothing here needs to know the scroll position.
            var copy = compositor.CreateVisualSurface();
            copy.SourceVisual = ElementCompositionPreview.GetElementVisual(AlbumScroll);
            copy.SourceOffset = Vector2.Zero;

            // Painted at its own size from its own top left corner. By default a surface brush centres
            // and scales what it is given, and for a band cut out of a taller screen that would show the
            // middle of the album, stretched.
            var pixels = compositor.CreateSurfaceBrush(copy);
            pixels.Stretch = CompositionStretch.None;
            pixels.HorizontalAlignmentRatio = 0;
            pixels.VerticalAlignmentRatio = 0;

            // The ramp is the mask, and only its ALPHA is read: nothing at the head, everything at the
            // bottom of the band. It uses relative mapping, so it stretches with the band.
            var ramp = compositor.CreateLinearGradientBrush();
            ramp.MappingMode = CompositionMappingMode.Relative;
            ramp.StartPoint = new Vector2(0, 0);
            ramp.EndPoint = new Vector2(0, 1);
            ramp.ColorStops.Add(compositor.CreateColorGradientStop(0f, Microsoft.UI.Colors.Transparent));
            ramp.ColorStops.Add(compositor.CreateColorGradientStop(1f, Microsoft.UI.Colors.White));

            var mask = compositor.CreateMaskBrush();
            mask.Source = pixels;
            mask.Mask = ramp;

            var sprite = compositor.CreateSpriteVisual();
            sprite.Brush = mask;
            ElementCompositionPreview.SetElementChildVisual(DrawerTopFade, sprite);

            // The cut that removes the live list from the same band. It goes on the wrapper, never on
            // the screen that is copied.
            var cut = compositor.CreateInsetClip();
            ElementCompositionPreview.GetElementVisual(AlbumScreen).Clip = cut;

            _topFadeCopy = copy;
            _topFadeSprite = sprite;
            _albumScreenCut = cut;
        }
        catch
        {
            // A backend without visual surfaces or mask brushes. This is tried once and not repeated.
            _noTopFade = true;
        }
    }

    /// <summary>
    /// How much of the album is fading now: nothing until it is scrolled, then deeper over the first
    /// few points of scroll until the band reaches its full depth.
    ///
    /// <para>The depth grows with the scroll instead of switching on, because an album at its top has
    /// nothing passing under the head. A fade over the first row of an unscrolled list would show the
    /// album already faded.</para>
    /// </summary>
    private void UpdateTopFade()
    {
        EnsureTopFade();
        if (_topFadeSprite is null || _topFadeCopy is null || _albumScreenCut is null)
            return;

        // The album screen only. The queue and the shelf are drawn on the panel's near-black, where
        // the square edge is not visible, and a second rendering of a shelf with 500 rows on every frame
        // costs too much.
        var depth = _screen == DrawerScreen.Album
            ? (float)Math.Clamp(AlbumScroll.VerticalOffset, 0, TopFadeDepth * DrawerScale)
            : 0f;

        var width = (float)DrawerTopFade.ActualWidth;
        if (width <= 0)
            return;

        _albumScreenCut.TopInset = depth;
        BandSurface.Fit(_topFadeCopy, _topFadeSprite, width, depth, 0);
    }

    /// <summary>The band is as wide as the drawer, so it is updated when the drawer changes width.</summary>
    private void DrawerTopFade_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTopFade();


    // ── the same fade at the foot of the collection ──
    //
    // The wall of covers runs off the bottom of the screen with nothing under it. Cut square there, it
    // looks like the drawer's list did: a row of covers with the bottom cut off. This uses the same three
    // pieces as above (a copy, a ramp, a cut), turned upside down.
    //
    // There is no band at the head. The wall runs up under the mini player to the top of the screen, as
    // on the home screen, with nothing over it, so the head is the screen's own edge.
    //
    // A surface renders the visual it was given from that visual's own origin, minus whatever
    // SourceOffset says (Uno subtracts it: see Visual.RenderRootVisual). So the last band of the wall is
    // requested by offsetting DOWN by everything above it. The brush's own alignment is left alone: it
    // would be a second way to set the same offset, and the two would add together if both were set.

    /// <summary>How deep the wall fades at its foot, in points before the drawer's scale. Deeper than the
    /// drawer's fade: this band lies over rows of covers about 200 points tall, where the drawer's lies
    /// over one line of a list. A fade shallower than the thing it fades looks like an edge with a
    /// gradient, not a fade.</summary>
    private const double LibraryFootDepth = 96;

    private CompositionVisualSurface? _libraryFootCopy;
    private SpriteVisual? _libraryFootSprite;
    private InsetClip? _libraryScreenCut;
    private bool _noLibraryFade;

    /// <summary>What the band was last set to. Scrolling through the middle of the wall, where the foot
    /// stays at its full depth, then costs nothing: the handler runs on every wheel event, and most of
    /// those events change nothing about the band.</summary>
    private float _libraryFoot = -1, _libraryFadeWidth, _libraryFadeHeight;

    /// <summary>Builds the band and the cut that removes the live wall from it.</summary>
    private void EnsureLibraryFade()
    {
        if (_libraryFootSprite is not null || _noLibraryFade)
            return;

        try
        {
            var compositor = ElementCompositionPreview.GetElementVisual(LibraryBottomFade).Compositor;

            var copy = compositor.CreateVisualSurface();
            copy.SourceVisual = ElementCompositionPreview.GetElementVisual(LibraryScroll);

            var pixels = compositor.CreateSurfaceBrush(copy);
            pixels.Stretch = CompositionStretch.None;
            pixels.HorizontalAlignmentRatio = 0;
            pixels.VerticalAlignmentRatio = 0;

            // Only the ALPHA of the ramp is read: fully opaque at the inner edge of the band, fully
            // transparent at the foot of the screen.
            var ramp = compositor.CreateLinearGradientBrush();
            ramp.MappingMode = CompositionMappingMode.Relative;
            ramp.StartPoint = new Vector2(0, 0);
            ramp.EndPoint = new Vector2(0, 1);
            ramp.ColorStops.Add(compositor.CreateColorGradientStop(0f, Microsoft.UI.Colors.White));
            ramp.ColorStops.Add(compositor.CreateColorGradientStop(1f, Microsoft.UI.Colors.Transparent));

            var mask = compositor.CreateMaskBrush();
            mask.Source = pixels;
            mask.Mask = ramp;

            var sprite = compositor.CreateSpriteVisual();
            sprite.Brush = mask;
            ElementCompositionPreview.SetElementChildVisual(LibraryBottomFade, sprite);

            var cut = compositor.CreateInsetClip();
            ElementCompositionPreview.GetElementVisual(LibraryScreen).Clip = cut;

            _libraryFootCopy = copy;
            _libraryFootSprite = sprite;
            _libraryScreenCut = cut;
        }
        catch
        {
            _noLibraryFade = true;
        }
    }

    /// <summary>
    /// How much of the wall is fading at its foot now.
    ///
    /// <para>The depth grows with the scroll instead of switching on. It is zero once the last row is on
    /// the screen, because a fade at the end of a collection, with nothing below it, makes the wall look
    /// unfinished.</para>
    /// </summary>
    private void UpdateLibraryFade()
    {
        if (!WallUp)
            return;

        EnsureLibraryFade();
        if (_libraryFootSprite is null || _libraryScreenCut is null || _libraryFootCopy is null)
            return;

        var width = (float)LibraryScroll.ActualWidth;
        var height = (float)LibraryScroll.ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        var footRoom = (float)(LibraryFootDepth * QueueScale);
        var foot = (float)Math.Clamp(LibraryScroll.ScrollableHeight - LibraryScroll.VerticalOffset,
                                     0, footRoom);

        // The host always has its FULL depth, whatever the band is doing, and the sprite moves inside
        // it. Setting the host height to the band depth would cause a measure and an arrange of the whole
        // screen inside a scroll handler, at the end of the wall where the band grows and the scroll
        // can least afford it. The height depends only on the size and the scale, which change when the
        // window changes and never while the wheel turns. The guard is still needed, because writing a
        // height from inside a size-changed handler could otherwise loop.
        if (Math.Abs(LibraryBottomFade.Height - footRoom) > 0.5)
            LibraryBottomFade.Height = footRoom;

        // Most wheel events happen in the middle of the wall, where the band is already at its depth
        // and there is nothing to write.
        if (Same(foot, _libraryFoot) && Same(width, _libraryFadeWidth) && Same(height, _libraryFadeHeight))
            return;

        _libraryFoot = foot;
        (_libraryFadeWidth, _libraryFadeHeight) = (width, height);

        _libraryScreenCut.BottomInset = foot;

        // Request the band by offsetting down the copy past everything above it, then place the sprite
        // along the bottom of a host that is deeper than the band until the wall is scrolled to its end.
        BandSurface.Fit(_libraryFootCopy, _libraryFootSprite, width, foot, Math.Max(0, height - foot));
        _libraryFootSprite.Offset = new Vector3(0, footRoom - foot, 0);

        static bool Same(float a, float b) => Math.Abs(a - b) < 0.5f;
    }

    private void LibraryScroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        UpdateLibraryFade();

        // The mini player hides on a move down, and a reveal of it ends on any move, as on home (see
        // PlayerAway). Only a real move counts: ViewChanged also arrives for a view that ended where it
        // started.
        var moved = LibraryScroll.VerticalOffset - _wallScrolledAt;
        if (Math.Abs(moved) > 0.5)
        {
            _wallScrolledAt = LibraryScroll.VerticalOffset;
            if (WallUp)
                Collection_Scrolled(moved);
        }

        // A settled scroll is when the wall can have lost its position: the user scrolls to the top, the
        // scrollbar says they are there, and half a row is showing. See CheckAnchor.
        //
        // Not while the wheel is still moving the wall: every frame of that movement settles, and the
        // check belongs at the end of it (see StopWheel), not on all twelve frames.
        if (!e.IsIntermediate && !_wheeling)
            CheckAnchorLater();
    }

    /// <summary>The wall's offset when the mini player was last told. See LibraryScroll_ViewChanged.</summary>
    private double _wallScrolledAt;

    private void LibraryBottomFade_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateLibraryFade();
}
