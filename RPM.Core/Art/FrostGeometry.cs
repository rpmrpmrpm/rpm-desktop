namespace RPM.Core.Art;

/// <summary>The slice of the frost plate one panel stands on: where to read it (plate pixels, with
/// fractions kept) and how many pixels the panel's own copy is.</summary>
public readonly record struct FrostCrop(float X, float Y, float Width, float Height, int PixelWidth, int PixelHeight);

/// <summary>
/// A plate, and what one of its pixels is worth in DIPs.
/// </summary>
/// <remarks>
/// The scale is CARRIED rather than assumed, and that is the whole reason this type exists. The plate is
/// asked for at <see cref="FrostGeometry.PlateDip"/> DIPs a pixel, and the picture does not always come
/// back at the size it was asked for. For example, a window snapped to the side of the screen can be
/// photographed one row taller than the arithmetic says (426x465 against 426x464). Everything that
/// reads the plate goes through this type, so a picture of another size is read correctly instead of
/// being read at three DIPs a pixel and sliding under the panels.
/// </remarks>
public readonly record struct FrostPlateMap(int Width, int Height, double DipPerPixelX, double DipPerPixelY)
{
    /// <summary>The map for a plate of this size photographed from a window of that size.</summary>
    public static FrostPlateMap For(int width, int height, double sourceWidthDip, double sourceHeightDip) =>
        new(width, height,
            width > 0 && sourceWidthDip > 0 ? sourceWidthDip / width : FrostGeometry.PlateDip,
            height > 0 && sourceHeightDip > 0 ? sourceHeightDip / height : FrostGeometry.PlateDip);

    /// <summary>The map for a plate that came back exactly the size it was asked for.</summary>
    public static FrostPlateMap Nominal(int width, int height) =>
        new(width, height, FrostGeometry.PlateDip, FrostGeometry.PlateDip);
}

/// <summary>
/// The arithmetic behind the cached frost: one blurred picture of everything BEHIND the glass, cut up
/// and handed to the panels standing on it.
///
/// <para>A live backdrop blur re-reads and re-blurs the pixels under every frosted panel on every
/// frame, and nothing behind those panels moves (the record and the seek bar are drawn on TOP of
/// them). So the same image would be computed sixty times a second. The plate is that image,
/// computed once and kept until something behind it actually changes.</para>
///
/// <para>The plate is deliberately SMALL: one pixel per <see cref="PlateDip"/> DIPs. A Gaussian at
/// radius 30 has already destroyed everything finer than that, so a third-scale plate blurred at a
/// third of the radius and stretched back up gives the same picture, with a ninth of the pixels to
/// read back from the GPU, blur, and carry around.</para>
/// </summary>
public static class FrostGeometry
{
    /// <summary>How many DIPs one plate pixel stands for.</summary>
    public const double PlateDip = 3.0;

    /// <summary>
    /// The blur radius to bake the plate at, given the radius the live glass would have used. The
    /// composition effect's BlurAmount is a Gaussian sigma in DIPs (Uno hands it to Skia unchanged), so
    /// on a plate whose pixels are <see cref="PlateDip"/> DIPs wide the same blur is that many times
    /// smaller. The result is never quite zero: a sigma of 0 is not a blur at all.
    /// </summary>
    public static double SigmaFor(double blurAmount) => Math.Max(0.1, blurAmount / PlateDip);

    /// <summary>The plate's own size for a window of this size in DIPs. At least one pixel each way:
    /// a window can be reported as zero-sized during teardown, and a zero-pixel surface throws an
    /// exception.</summary>
    public static (int Width, int Height) PlateSize(double widthDip, double heightDip) =>
        (Math.Max(1, (int)Math.Ceiling(widthDip / PlateDip)),
         Math.Max(1, (int)Math.Ceiling(heightDip / PlateDip)));

    /// <summary>
    /// Where a panel at (<paramref name="xDip"/>, <paramref name="yDip"/>) reads its frost from.
    ///
    /// <para>The source rectangle keeps its fractions: the panel's copy is drawn from it with the
    /// sampler, not snapped to a plate pixel. A plate pixel is three DIPs, so snapping would slide the
    /// frost under the panel by up to one and a half DIPs every time it was re-cut.</para>
    ///
    /// <para>The rectangle is clamped to the plate: a panel hanging off the window's edge reads the
    /// part of it that is on the plate rather than nothing at all. The result is null when there is no
    /// rectangle to read (a collapsed panel, or one entirely outside the window).</para>
    /// </summary>
    public static FrostCrop? CropFor(double xDip, double yDip, double widthDip, double heightDip, FrostPlateMap plate)
    {
        if (widthDip <= 0 || heightDip <= 0 || plate.Width <= 0 || plate.Height <= 0)
            return null;
        if (plate.DipPerPixelX <= 0 || plate.DipPerPixelY <= 0)
            return null;

        var (plateWidth, plateHeight) = (plate.Width, plate.Height);

        var left = Math.Clamp(xDip / plate.DipPerPixelX, 0, plateWidth);
        var top = Math.Clamp(yDip / plate.DipPerPixelY, 0, plateHeight);
        var right = Math.Clamp((xDip + widthDip) / plate.DipPerPixelX, 0, plateWidth);
        var bottom = Math.Clamp((yDip + heightDip) / plate.DipPerPixelY, 0, plateHeight);

        var width = right - left;
        var height = bottom - top;
        if (width <= 0 || height <= 0)
            return null;

        return new FrostCrop(
            (float)left, (float)top, (float)width, (float)height,
            Math.Max(1, (int)Math.Ceiling(width)),
            Math.Max(1, (int)Math.Ceiling(height)));
    }
}
