using SkiaSharp;

namespace RPM.Desktop.Presentation.Common;

/// <summary>The app's mark, stamped on every cover the app makes for itself.
///
/// <para><b>What it is for.</b> A generated playlist's cover is a picture the app chose or drew. It is
/// not artwork that someone released. When these covers sit next to real album sleeves, the mark shows
/// which is which, without a label. Every automatically generated list wears the mark. That covers the
/// lists with a photograph (<c>GeneratedCoverService.Framed</c>) and the lists drawn from their own two
/// numbers (<see cref="PlaylistArt.Figure"/>). So the mark is on every generated cover, or on
/// none.</para>
///
/// <para><b>The colour is the complement of the mood. This is the same as the complement of the
/// background.</b> A cover with a photograph is the photograph washed with the list's own colour, at
/// <c>GeneratedCoverService.TintStrength</c>, through <see cref="SKBlendMode.Color"/>. That blend mode
/// takes hue and saturation from the paint, and lightness from the picture. So the hue of the composed
/// background is the mood's hue. Taking the complement of the mood therefore takes the complement of
/// the background, with no need to decode any pixels. There is no read-back, and the result is the same
/// on every machine.
/// <b>This only holds while the wash is strong.</b> With a weaker wash, the picture's own hue shows
/// through, and the colour would have to be measured instead.</para>
///
/// <para><b>Lightness is measured, because hue alone does not make a mark readable.</b> A complementary
/// hue with the same lightness as the ground is hard to read. The eye tells shapes apart by lightness
/// first, and two colours of equal lightness and opposite hue shimmer rather than separate. So the hue
/// is the complement of the mood, and the lightness is moved away from the ground under the mark. That
/// ground is sampled from the surface at the moment of stamping. A bright sky gets a dark mark, and a
/// dark night photo gets a light one.</para>
///
/// <para><b>No file is an ordinary case.</b> If there is no file at <c>Assets/PlaylistMark.png</c>,
/// nothing is drawn and every cover stays as it is. The asset is optional in the project file for the
/// same reason. A build without it still builds, and the tests do not need a logo to test the
/// artwork.</para>
/// </summary>
public static class PlaylistMark
{
    /// <summary>The share of the cover's shorter side that the mark's longest side takes. The mark is
    /// fitted, not stretched, so a square logo and a wide one both keep their own shape.</summary>
    public const double Share = 0.42;

    /// <summary>How solid the mark is drawn. It is not fully opaque, because the mark sits ON a
    /// picture, and a flat cutout looks like a sticker.</summary>
    public const float Solidity = 0.92f;

    /// <summary>The lightness of the mark, from 0 to 1. <see cref="Light"/> is used on a dark ground
    /// and
    /// <see cref="Dark"/> on a light one. The values are far enough from the ground to separate the
    /// mark on any picture, and short of black and white, which would look like a cutout rather than a
    /// colour.</summary>
    public const float Light = 0.88f, Dark = 0.18f;

    /// <summary>Above this the ground counts as light and the mark goes dark. The midpoint of
    /// perceived luminance rather than of the byte range.</summary>
    public const float GroundIsLight = 0.55f;

    private static SKBitmap? _mark;
    private static bool _looked;
    private static readonly object Gate = new();

    /// <summary>The mark itself, read once and kept.
    /// </summary>
    /// <remarks>Read from the output folder, as every other bundled asset here is
    /// (<c>SaliencyService</c>,
    /// <c>FaInk</c>). This runs on a worker thread that has no XamlRoot to resolve an <c>ms-appx:</c>
    /// URI against. A cover is composed off the UI thread by design.
    ///
    /// <para>The file is looked for once, whether or not it is found. A missing asset is normal on a
    /// head that does not ship it. If a miss were not remembered, every cover would open the file
    /// again.</para>
    /// </remarks>
    private static SKBitmap? Loaded()
    {
        lock (Gate)
        {
            if (_looked)
                return _mark;

            _looked = true;
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", "PlaylistMark.png");
                if (File.Exists(path))
                    _mark = SKBitmap.Decode(path);
            }
            catch
            {
                _mark = null;   // a file that cannot be decoded is treated as missing
            }

            return _mark;
        }
    }

    /// <summary>The colour to draw the mark in: the mood's complement, at a lightness that clears the
    /// ground.
    /// </summary>
    /// <param name="mood">The list's own colour, <c>SmartPlaylistFace.Tint</c>. Its HUE is what the
    /// background was washed with, so its opposite is the opposite of the background.</param>
    /// <param name="groundLuminance">How light the cover is where the mark will sit, from 0 to 1. This
    /// is measured, not assumed. See the note on the class.</param>
    public static SKColor Complement(SKColor mood, float groundLuminance)
    {
        mood.ToHsl(out var hue, out var saturation, out _);

        // Saturation is kept, not raised. The mark should use the same kind of colours as the rest of
        // the app. A fully saturated complement of a muted mood would be a colour from somewhere else.
        var opposite = (hue + 180f) % 360f;

        var lightness = groundLuminance >= GroundIsLight ? Dark : Light;
        return SKColor.FromHsl(opposite, saturation, lightness * 100f);
    }

    /// <summary>How light the surface is inside <paramref name="box"/>, 0..1, so the mark can be drawn
    /// to clear it.
    /// </summary>
    /// <remarks>Read off the surface as it is now, not off what was drawn on it. Both callers have
    /// composed several layers by the time they stamp: a photograph and a wash in one, a figure of
    /// three palette colours in the other. What matters is what the mark will actually sit on.
    ///
    /// <para>Uses Rec. 709 luma. It samples a grid of pixels, not every pixel. The result only has to
    /// choose between two lightness values, and a few hundred samples do that as well as four hundred
    /// thousand would.</para>
    /// </remarks>
    public static float GroundUnder(SKSurface surface, SKRect box)
    {
        ArgumentNullException.ThrowIfNull(surface);

        try
        {
            var rect = SKRectI.Round(box);
            using var shot = surface.Snapshot(rect);
            if (shot is null)
                return 0;

            using var pixels = shot.PeekPixels();
            if (pixels is null)
                return 0;

            double total = 0;
            var taken = 0;
            var step = Math.Max(1, Math.Min(pixels.Width, pixels.Height) / 16);

            for (var y = 0; y < pixels.Height; y += step)
            {
                for (var x = 0; x < pixels.Width; x += step)
                {
                    var p = pixels.GetPixelColor(x, y);
                    total += ((0.2126 * p.Red) + (0.7152 * p.Green) + (0.0722 * p.Blue)) / 255.0;
                    taken++;
                }
            }

            return taken == 0 ? 0 : (float)(total / taken);
        }
        catch
        {
            return 0;   // unreadable surface: treated as dark, which gives the light mark
        }
    }

    /// <summary>Stamps the mark in the middle of <paramref name="box"/>, or does nothing where there
    /// is no mark to stamp.
    /// </summary>
    /// <param name="surface">The cover as it is now. It is read to choose the mark's lightness, and
    /// then the mark is drawn on it. So this is called LAST, after everything the mark has to be
    /// readable against.</param>
    /// <param name="box">The whole cover. The mark is fitted inside a share of it.</param>
    /// <param name="mood">The list's colour, which the complement is taken from.</param>
    /// <returns>Whether a mark was drawn, so a caller can say so in what it encodes.</returns>
    public static bool Stamp(SKSurface surface, SKRect box, SKColor mood)
    {
        ArgumentNullException.ThrowIfNull(surface);

        if (Loaded() is not { Width: > 0, Height: > 0 } mark)
            return false;

        // The mark is fitted by its longer side, so it keeps its own shape. This code does not need
        // to know whether the mark is a monogram or a wordmark.
        var longest = (float)(Math.Min(box.Width, box.Height) * Share);
        var scale = longest / Math.Max(mark.Width, mark.Height);
        var width = mark.Width * scale;
        var height = mark.Height * scale;

        var target = SKRect.Create(
            box.MidX - (width / 2f), box.MidY - (height / 2f), width, height);

        var ink = Complement(mood, GroundUnder(surface, target));

        // Use SrcIn with the mark's own alpha. The file is a shape with nothing behind it, so the
        // wanted result is its silhouette in the chosen colour, not its own pixels. The mark is drawn
        // through a layer, because a colour filter on the paint alone would also tint whatever is UNDER
        // the mark.
        using var paint = new SKPaint
        {
            ColorFilter = SKColorFilter.CreateBlendMode(ink, SKBlendMode.SrcIn),
            Color = SKColors.White.WithAlpha((byte)(Solidity * 255)),
        };

        using var image = SKImage.FromBitmap(mark);
        surface.Canvas.DrawImage(
            image, SKRect.Create(mark.Width, mark.Height), target,
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);

        return true;
    }
}
