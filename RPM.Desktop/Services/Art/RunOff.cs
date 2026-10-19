using RPM.Core.Threading;
using SkiaSharp;

namespace RPM.Desktop.Services.Art;

/// <summary>
/// A wide artist photograph drawn with a reflection under it. It is used by the two surfaces that show one
/// artist: the band on the artist page, and the photo strip in the track info pane.
///
/// <para><b>When it is used.</b> Both surfaces show the artist's square photos (fanart.tv's
/// <c>artistthumb</c>) when there are any, cropped to fill the box. Only an artist with no square photo gets a
/// wide one, such as a backdrop, usually 1920 by 1080. A wide photo cropped to fill a 4:3 or upright box loses
/// most of its width. So a wide photo is scaled to the box's width and placed at the top. The space left under
/// it is filled with the photo mirrored and blurred.</para>
///
/// <para><b>What is drawn.</b> This follows the phone's <c>RunOff</c> (RPMUI/ArtGround.swift), with one
/// difference. The photo is shown, then the photo turned over under itself and blurred, then a blur over the
/// seam where the two meet. The radii match the phone's. The canvas is the photo's width and twice its height.
/// The phone fades the mirror out with its own mask. Here, the surface's foot dissolve does that, with its depth
/// set to the space under the photo (see <see cref="FadeDepth"/>). That space depends on the box's width, which
/// the user can change. The canvas does not depend on it, so a resize never needs a new bake.</para>
///
/// <para><b>The difference.</b> The phone also blurs the photo itself from 60% of its height down. The phone
/// writes the artist's name over the lower part of the photo. On the desktop, the name sits on the reflection
/// under the photo instead. So that blur would only remove the lower part of the picture, and it is left out.
/// The photo is sharp down to the seam. The mirror is blurred with the phone's heavier foot radius instead. That
/// is about what the phone's mirror shows, because it mirrors the photo's already blurred foot.</para>
///
/// <para><b>How a surface shows it.</b> As an <c>ImageBrush</c> background with <c>Stretch="UniformToFill"</c> and
/// <c>AlignmentY="Top"</c>. The canvas is taller than the boxes it is drawn in are wide. So the brush scales it to the
/// box's width, and the mirror runs off the bottom. Uno's Skia renderer honours the brush's alignment.</para>
/// </summary>
public static class RunOff
{
    /// <summary>A photo baked with its reflection.</summary>
    /// <param name="Canvas">The JPEG: the photo at the top, its mirror under it, twice the photo's
    /// height.</param>
    /// <param name="Aspect">The PHOTO's width over its height, not the canvas's. What the surface needs to work
    /// out how tall the photo is drawn at a given box width.</param>
    public sealed record Baked(byte[] Canvas, double Aspect);

    /// <summary>Whether a photo this size is drawn with a reflection. It must be wider than it is tall. A square
    /// photo is not, so an artist whose square photos are shown never gets one.</summary>
    public static bool Applies(int width, int height) => height > 0 && width > height;

    /// <summary>
    /// Bakes the canvas off the UI thread. It returns null for a photo that is not wide, for a photo that Skia
    /// cannot read, and when the token fired before the work started. A null means "show the photo as it is".
    /// </summary>
    public static async Task<Baked?> BakeAsync(byte[] photo, CancellationToken ct)
    {
        var (width, height) = ArtScaler.BoundsOf(photo);
        if (!Applies(width, height))
            return null;

        var run = await Cancellable.RunAsync(() => Bake(photo), ct);
        return run is { Ran: true, Value: { } canvas }
            ? new Baked(canvas, (double)width / height)
            : null;
    }

    // ── the layout ──

    /// <summary>The shortest foot dissolve a surface uses over a reflected photo, in points. A box a little wider
    /// than the photo leaves only a few points under it. A dissolve that short reads as a hard edge. With this
    /// minimum, the dissolve reaches into the photo's own blurred foot instead.</summary>
    public const double MinFade = 48;

    /// <summary>
    /// How tall the photo is drawn in a box of this size, in points: the box's width over the photo's aspect. The
    /// canvas is scaled to cover the box. For a box narrower than half the photo's aspect, the canvas is scaled to
    /// the box's height instead. Then the photo is half the box's height and wider than the box.
    /// </summary>
    public static double PhotoHeight(double boxWidth, double boxHeight, double aspect)
        => aspect > 0 ? Math.Max(boxWidth / aspect, boxHeight / 2) : boxHeight;

    /// <summary>
    /// How deep the surface's foot dissolve runs over a reflected photo: from the photo's foot to the bottom of the
    /// box. This fades the mirror out, in the same way as the phone's <c>mirrorFade</c>. It is never less than
    /// <see cref="MinFade"/>, and never deeper than the box. That case happens while the track pane's strip is
    /// sliding open. The value changes smoothly with the box's height, so the slide shows no jump in the dissolve.
    /// </summary>
    public static double FadeDepth(double boxWidth, double boxHeight, double aspect)
    {
        if (boxHeight <= 0)
            return 0;

        var under = boxHeight - PhotoHeight(boxWidth, boxHeight, aspect);
        return Math.Min(boxHeight, Math.Max(under, MinFade));
    }

    // ── the drawing ──

    /// <summary>The phone's band width in points. The phone's blur radii and seam reach are in points for a band
    /// this wide. So each one is converted here to a share of the photo's width.</summary>
    private const double PhoneWidth = 393;

    /// <summary>The blur radius of the mirror, the blur radius of the seam, and the reach of the seam, in the
    /// phone's points. 32 is the heavier of the two radii the phone uses to blur the photo's foot. 20 and 50 are
    /// the phone's seam values.</summary>
    private const double MirrorBlur = 32;
    private const double SeamBlur = 20;
    private const double SeamReach = 50;

    /// <summary>
    /// The canvas as JPEG bytes. Returns null where the photo is not wide, or where it will not decode. This is
    /// synchronous and CPU-bound. Call <see cref="BakeAsync"/> from a surface.
    /// </summary>
    public static byte[]? Bake(byte[] photo)
    {
        try
        {
            using var decoded = SKBitmap.Decode(photo);
            if (decoded is null || !Applies(decoded.Width, decoded.Height))
                return null;

            var w = decoded.Width;
            var h = decoded.Height;
            using var sharp = SKImage.FromBitmap(decoded);

            float Points(double phonePoints) => (float)(w * phonePoints / PhoneWidth);

            // The photo as it is, at the top. Under it, the same picture turned over about its foot and blurred.
            // Row y of the photo lands on row 2h - 1 - y, so the two meet on row h. The blur is clamped at the
            // photo's edges, so the mirror stays opaque at the seam.
            using var mirrored = Draw(w, 2 * h, canvas =>
            {
                canvas.DrawImage(sharp, 0, 0);

                using var blur = new SKPaint
                {
                    ImageFilter = SKImageFilter.CreateBlur(Points(MirrorBlur), Points(MirrorBlur), SKShaderTileMode.Clamp),
                };
                canvas.Save();
                canvas.ClipRect(SKRect.Create(0, h, w, h));
                canvas.Scale(1, -1, 0, h);
                canvas.DrawImage(sharp, 0, 0, new SKSamplingOptions(SKFilterMode.Linear), blur);
                canvas.Restore();
            });
            using var mirroredImage = SKImage.FromBitmap(mirrored);

            // A blur over the seam. It has no effect further than the reach from the foot. It is at full strength
            // within a third of that reach.
            var reach = Points(SeamReach);
            using var finished = Draw(w, 2 * h, canvas =>
            {
                canvas.DrawImage(mirroredImage, 0, 0);
                BlurInto(canvas, mirroredImage, Points(SeamBlur), w, 2 * h, Seam(h, reach));
            });

            return ArtScaler.Jpeg(finished);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A bitmap of this size, with whatever <paramref name="paint"/> draws on it.</summary>
    private static SKBitmap Draw(int width, int height, Action<SKCanvas> paint)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        paint(canvas);
        canvas.Flush();
        return bitmap;
    }

    /// <summary>
    /// Draws a blurred copy of <paramref name="source"/> over the canvas. It shows only where the mask is opaque.
    /// The blur is clamped at the edges, so the picture's border does not become transparent. This matches the
    /// phone's <c>opaque: true</c>.
    /// </summary>
    private static void BlurInto(SKCanvas canvas, SKImage source, float sigma, int width, int height, SKShader mask)
    {
        canvas.SaveLayer();

        using (var blur = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp) })
            canvas.DrawImage(source, 0, 0, new SKSamplingOptions(SKFilterMode.Linear), blur);

        // DstIn keeps the blurred layer only as far as the mask is opaque.
        using (mask)
        using (var keep = new SKPaint { Shader = mask, BlendMode = SKBlendMode.DstIn })
            canvas.DrawRect(SKRect.Create(width, height), keep);

        canvas.Restore();
    }

    /// <summary>A vertical mask around row <paramref name="foot"/>. It is clear further than
    /// <paramref name="reach"/> away. It is opaque within a third of that reach, with a ramp in between.</summary>
    private static SKShader Seam(double foot, float reach)
        => SKShader.CreateLinearGradient(
            new SKPoint(0, (float)foot - reach), new SKPoint(0, (float)foot + reach),
            [SKColors.Transparent, SKColors.White, SKColors.White, SKColors.Transparent],
            [0f, 1f / 3, 2f / 3, 1f], SKShaderTileMode.Clamp);
}
