using System.Numerics;
using Microsoft.UI.Composition;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// Points one dissolve band's copy at its slice of the thing it is dissolving, and sizes the sprite that
/// paints it.
///
/// <para>Several places in this app cut a band out of a live visual and paint it back through a ramp.
/// These include the foot of the sheet, the foot of the photograph, the head of the drawer, the two
/// edges of its track list, the library wall, and the ends of the home screen's rows. Each of them
/// does the same three writes, and each has the same flaw.</para>
/// </summary>
internal static class BandSurface
{
    /// <summary>
    /// Fits a band across the full width, at <paramref name="from"/> down the source.
    ///
    /// <para><b>A depth of zero is the reason this method exists.</b> A depth of zero is not an empty
    /// draw. It is a SURFACE WITH NO PIXELS. Giving one of those to a mask brush and asking the compositor
    /// to draw it causes a segmentation fault inside Skia. It is a memmove from zero, in the middle of the
    /// Metal view's draw, and it takes the window down with it. Every one of these bands reaches zero in
    /// ordinary use. A scroller shorter than its viewport has neither hem. A scroller scrolled to its end
    /// has no foot. A strip in the middle of a slide has no depth yet. So this is a common case, not an
    /// edge case.</para>
    ///
    /// <para>When the depth is zero, the sprite is hidden instead of being sized to zero. Nothing is
    /// written to the surface in that state. Its last good size stays on it, unread, until the band comes
    /// back.</para>
    /// </summary>
    /// <param name="from">Where the band starts, in the source visual's own coordinates. The surface draws
    /// the source from its origin, minus this offset.</param>
    public static void Fit(CompositionVisualSurface copy, SpriteVisual sprite, float width, float depth, float from)
        => Band(copy, sprite, new Vector2(width, depth), new Vector2(0, from));

    /// <summary>
    /// The same band turned on its side: full height, <paramref name="depth"/> wide, starting
    /// <paramref name="from"/> across the source. What a row that scrolls sideways hems its two ends
    /// with.
    ///
    /// <para>A depth of zero hides the sprite here, for the same reason as in <see cref="Fit"/>. It happens
    /// often. A row whose records all fit has neither band.</para>
    ///
    /// <para><b>The surface is made square.</b> On Uno, the visual surface clips what it renders to
    /// <c>(0, 0, Size.X, Size.X)</c>. That is, the surface's width is used on both axes (see
    /// CompositionVisualSurface.skia.cs). A band across the top of a list is wider than it is deep, so the
    /// square covers it and nothing goes wrong. A band down the side of a row is 22 points wide and as tall
    /// as the row. The same square would cut it to its top 22 points. The dissolve would be drawn in a
    /// corner nobody can see, and the rest of the band would be empty. So the surface is given the longer
    /// side as its width. The sprite is the only thing that reaches the screen, and it keeps the true
    /// depth.</para>
    /// </summary>
    public static void Across(CompositionVisualSurface copy, SpriteVisual sprite, float height, float depth, float from)
        => Band(copy, sprite, new Vector2(depth, height), new Vector2(from, 0), surfaceWidth: Math.Max(depth, height));

    /// <param name="surfaceWidth">The width the surface is given. It is used only when it must be more
    /// than the band's own width (see <see cref="Across"/>). Zero means the band's own width. That is the
    /// case for every band that lies across the thing it dissolves.</param>
    private static void Band(
        CompositionVisualSurface copy, SpriteVisual sprite, Vector2 size, Vector2 from, float surfaceWidth = 0)
    {
        if (size.X <= 0 || size.Y <= 0)
        {
            sprite.IsVisible = false;
            return;
        }

        copy.SourceOffset = from;
        copy.SourceSize = new Vector2(surfaceWidth > 0 ? surfaceWidth : size.X, size.Y);
        sprite.Size = size;
        sprite.IsVisible = true;
    }
}
