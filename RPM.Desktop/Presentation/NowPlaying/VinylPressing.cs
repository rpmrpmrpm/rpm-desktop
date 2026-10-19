using System.Collections.Concurrent;

using RPM.Core.Art;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace RPM.Desktop.Presentation.NowPlaying;

/// <summary>
/// The record on the turntable, pressed in a colour.
///
/// <para>
/// One render, tinted. <c>Assets/vinyl-record.png</c> is the same pressing the background is made of. It is
/// rendered by scripts/make_vinyl_table.py in plain black vinyl, top-down, with its own lead-in and
/// run-out. Every colour is that one image multiplied by a hue, so the disc and the background stay in
/// step.
/// </para>
///
/// <para>
/// The colour is the album art's: see <see cref="HueFromArt"/>. The record on the deck belongs to the
/// sleeve on the shelf, so a sleeve with no colour in it presses black, as a record is.
/// </para>
///
/// <para>
/// The tint keeps the look of a pressing, not of a photograph with a filter over it. The hue is scaled to
/// UNIT LUMINANCE, so multiplying by it changes the colour of a pixel and not its brightness. The sweep of
/// light across the disc, the grain of the grooves and the roll of the rim are all brightness, so they stay
/// intact. Nothing here touches brightness.
/// </para>
/// </summary>
internal static class VinylPressing
{
    /// <summary>
    /// How far a hue is applied, against leaving the vinyl black. Not all the way, so the record still
    /// takes the light of the screen around it. A full-strength colour would look like a sticker on the
    /// header rather than a record in it.
    /// </summary>
    private const double Saturation = 0.88;

    /// <summary>
    /// The most any one channel may reach. A saturated hue at unit luminance has one channel several times
    /// its own brightness. Pure red, for example, is almost all red. Multiplied by the render's sweep of
    /// light, such a channel clips. That flattens the highlight into a patch of colour, where the grooves
    /// should still be readable.
    /// </summary>
    private const double Ceiling = 3.2;

    private static readonly double[] Luma = [0.2126, 0.7152, 0.0722];

    /// <summary>
    /// One press per colour. The cache holds the press itself, so a colour is pressed once.
    ///
    /// <para>A cache written only when the work finishes cannot answer callers who ask while it is still
    /// running. Each of them would start its own press: the same asset decoded again, the same Skia pass
    /// run again, the same PNG encoded again, and all but the last result thrown away. A record is asked
    /// for its colour several times during one track change. The sleeve arrives, then the photographed cd,
    /// then the deck is cued. So the usual case was three presses of one disc, all at once, at the busiest
    /// moment in the app.</para>
    ///
    /// <para>This is a <see cref="Lazy{T}"/> over the task rather than the task itself, so the press starts
    /// exactly once however many callers arrive together. A press that fails removes its own entry on the
    /// way out, because a failure is not worth remembering.</para>
    /// </summary>
    private static readonly ConcurrentDictionary<uint, Lazy<Task<ImageSource?>>> Cache = new();

    /// <summary>The render off disk, read once and shared the same way, for the same reason.</summary>
    private static readonly Lazy<Task<byte[]?>> Neutral = new(LoadNeutralAsync);

    /// <summary>
    /// The colour this sleeve presses in, or null for black.
    ///
    /// <para>The vivid highlight if the cover has one, its dominant tone otherwise, and nothing at all when
    /// it has neither, see <see cref="VinylHue"/>, which owns that judgement. So the disc on the deck
    /// belongs to the record being played: a sleeve of orange and black presses orange, and a
    /// black-and-white photograph presses black, because that is what a record is.</para>
    /// </summary>
    public static Color? HueFromArt(byte[]? artBytes) => PaletteFromArt(artBytes).Hue;

    /// <summary>
    /// Everything a sleeve has to say about its own colour, in one call: the hue its record presses in, its
    /// dominant tone, and its vivid accent.
    ///
    /// <para>The pressing only needs the first, but the two colours it is chosen from are also used by
    /// anything that paints beside the record. Pegasus washes the whole screen in them. Returning all three
    /// means they are read on the one pass that already has the bytes open, so the cover is not decoded a
    /// second time.</para>
    /// </summary>
    public static (Color? Hue, Color? Tint, Color? Glow, Color? Shade) PaletteFromArt(byte[]? artBytes)
    {
        if (artBytes is not { Length: > 0 })
            return (null, null, null, null);

        // Use two of the sleeve's own colours, not one colour and a guessed one. Anything that paints a
        // surface in a record's colours needs more than one accent. Turning one accent by some number of
        // degrees to make up the difference gives a colour that is not on the cover.
        var vivid = CoverColor.Palette(artBytes, 2);
        var glow = vivid.Count > 0 ? vivid[0] : CoverColor.Highlight(artBytes);
        var shade = vivid.Count > 1 ? vivid[1] : (Color?)null;

        var tint = CoverColor.Dominant(artBytes);
        var chosen = VinylHue.Choose(Plain(glow), Plain(tint));

        return (chosen is { } c ? Color.FromArgb(0xFF, c.R, c.G, c.B) : null, tint, glow, shade);
    }

    private static Rgb? Plain(Color? colour) =>
        colour is { } c ? new Rgb(c.R, c.G, c.B) : null;

    /// <summary>
    /// The house pressing: the colour the deck uses when nothing else is set. That is an empty deck, or a
    /// sleeve with no colour in it. It is the app's azure (#0371D0) at alpha 40 over pure black, which is
    /// the 40 step of Assets/azure-over-black-ramp.png. The rest of the app's dark blues come from the same
    /// ramp, so the disc matches them rather than having a blue of its own.
    /// </summary>
    public static readonly Color DefaultHue = Color.FromArgb(0xFF, 0x01, 0x1C, 0x34);

    /// <summary>
    /// Black vinyl: what a sleeve with no colour in it gets on the full-screen visualizer, where the record
    /// is the whole picture rather than the app's own mark.
    ///
    /// <para>The render is desaturated before use. It is not neutral: its body measures about (34, 32, 50),
    /// a blue-black. That is a good base for a coloured record, but it is wrong for black. Tints multiply
    /// the blue cast, so it is hidden there. Pure black needs the cast removed explicitly.</para>
    ///
    /// <para>Saturation is set to zero using the luma weights. This is the same approach the tint uses,
    /// from the other side. The sweep of light, the grain of the grooves and the roll of the rim are
    /// brightness, so they stay. Only the colour goes.</para>
    /// </summary>
    public static Task<ImageSource?> PressBlackAsync() => Black.Value;

    /// <summary>Every channel becomes the pixel's own luminance, so the colour goes and the light stays.
    /// This must be declared above <see cref="Black"/>, which reads it. Static fields are set in the order
    /// they are written. Declared below, <see cref="Black"/> would read null here.</summary>
    private static readonly float[] Greyscale =
    [
        (float)Luma[0], (float)Luma[1], (float)Luma[2], 0, 0,
        (float)Luma[0], (float)Luma[1], (float)Luma[2], 0, 0,
        (float)Luma[0], (float)Luma[1], (float)Luma[2], 0, 0,
        0, 0, 0, 1, 0,   // alpha untouched: the disc has to stay a disc, not become a square
    ];

    private static readonly Lazy<Task<ImageSource?>> Black = new(async () =>
    {
        try
        {
            if (await Neutral.Value is not { } neutral)
                return null;

            var bytes = await Task.Run(() => Recolour(neutral, Greyscale));
            if (bytes is null)
                return null;

            var image = new BitmapImage();
            var stream = Common.BitmapBytes.Stream(bytes);
                await image.SetSourceAsync(stream);

            return image;
        }
        catch
        {
            return null;
        }
    });

    /// <summary>
    /// The record in <paramref name="hue"/>, ready to show. Null if the asset will not load. The caller
    /// then leaves whatever is on screen alone, because keeping the old picture is better than a hole.
    /// </summary>
    public static Task<ImageSource?> PressAsync(Color hue) => PressAsync(hue, exact: false);

    /// <summary>
    /// The record in exactly <paramref name="body"/>: the vinyl's own flat surface comes out AT that
    /// colour, rather than in its direction.
    ///
    /// <para>The difference matters. Tint normalises the hue to unit luminance, so a colour taken from a
    /// sleeve keeps the render's light whatever its brightness. Given a near-black navy, it presses a
    /// bright blue. That is right for an album's colour, but wrong for a colour a person picked. Body
    /// scales each channel so the body lands on the number asked for. The sweep, the grooves and the rim
    /// keep their light relative to it.</para>
    /// </summary>
    public static Task<ImageSource?> PressBodyAsync(Color body) => PressAsync(body, exact: true);

    /// <summary>
    /// The record in <paramref name="hue"/> as the pressed PNG's bytes rather than a picture, for a surface
    /// that cuts it to the exact size it is drawn at (the album page; see
    /// <c>RemoteImage.DecodeArtAsync</c>). The pictures above are drawn at whatever size their surface is
    /// and scaled by the renderer, which is a 1024-pixel render shrunk three and a half times on that page.
    /// Null where the asset will not load.
    ///
    /// <para>Pressed again rather than read out of <see cref="Cache"/>, which holds pictures and not bytes.
    /// It is asked once per record opened, off the UI thread.</para>
    /// </summary>
    public static async Task<byte[]?> PressBytesAsync(Color hue)
    {
        try
        {
            return await Neutral.Value is { } neutral ? await Task.Run(() => Tint(neutral, hue)) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Black vinyl as the pressed PNG's bytes. See <see cref="PressBlackAsync"/> for what it is
    /// and <see cref="PressBytesAsync"/> for why bytes. Kept once, like the picture form.</summary>
    public static Task<byte[]?> PressBlackBytesAsync() => BlackBytes.Value;

    private static readonly Lazy<Task<byte[]?>> BlackBytes = new(async () =>
    {
        try
        {
            return await Neutral.Value is { } neutral ? await Task.Run(() => Recolour(neutral, Greyscale)) : null;
        }
        catch
        {
            return null;
        }
    });

    /// <summary>The render's own average colour, measured off Assets/vinyl-record.png over the vinyl itself
    /// (label and rim excluded): (34.4, 31.9, 50.3), the blue-black it is drawn in. An exact press divides
    /// by this, so that the disc averages the colour it was given, which is the colour a person reads off
    /// it. The brightest pixel in that same render is (236, 196, 181), the sweep of light, and it scales
    /// with everything else instead of being flattened.</summary>
    private static readonly double[] NeutralBody = [34.37, 31.9, 50.34];

    private static Task<ImageSource?> PressAsync(Color hue, bool exact)
    {
        // Exact presses share the cache with tints, one bit apart: they are two different discs for the
        // same three numbers, and a shared key would hand back whichever was asked for first.
        var key = ((uint)hue.R << 16) | ((uint)hue.G << 8) | hue.B | (exact ? 0x0100_0000u : 0);
        return Cache.GetOrAdd(key, k => new Lazy<Task<ImageSource?>>(() => Press(k))).Value;

        async Task<ImageSource?> Press(uint k)
        {
            try
            {
                if (await Neutral.Value is not { } neutral)
                    return Failed(k);

                var bytes = await Task.Run(() => exact ? Body(neutral, hue) : Tint(neutral, hue));
                if (bytes is null)
                    return Failed(k);

                var image = new BitmapImage();
                var stream = Common.BitmapBytes.Stream(bytes);
                    await image.SetSourceAsync(stream);

                return image;
            }
            catch
            {
                return Failed(k);   // the disc keeps whatever colour it already had
            }
        }

        // Out of the cache again, so a colour that failed once is asked again rather than answered with the
        // same nothing for the rest of the session.
        static ImageSource? Failed(uint k)
        {
            Cache.TryRemove(k, out _);
            return null;
        }
    }

    private static async Task<byte[]?> LoadNeutralAsync()
    {
        try
        {
#if RPM_WINUI
            // Read from disk by path, because this head has no package identity to ask about.
            //
            // GetFileFromApplicationUriAsync resolves ms-appx:/// through the app package. This head is not
            // packaged (WindowsPackageType=None, so rpm ships as a folder, not an MSIX), so that call
            // throws. The catch below then returns null, and every pressing fails. The record never
            // arrives: the disc waits for its vinyl before it drops, so the header shows no record and
            // nothing says why.
            //
            // The asset sits beside the binary in both cases. The ms-appx URI is only a way of naming it.
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "vinyl-record.png");
            return File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
#else
            var file = await Windows.Storage.StorageFile.GetFileFromApplicationUriAsync(
                new Uri("ms-appx:///Assets/vinyl-record.png"));

            // Read as a stream rather than through IBuffer: the buffer's ToArray extension lives in
            // System.Runtime.InteropServices.WindowsRuntime, which this head does not carry.
            using var stream = await file.OpenStreamForReadAsync();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            return memory.ToArray();
#endif
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Multiplies the render by a unit-luminance hue. See the class note on why that keeps the
    /// grooves.</summary>
    private static byte[]? Tint(byte[] neutral, Color hue)
    {
        double r = hue.R / 255.0, g = hue.G / 255.0, b = hue.B / 255.0;
        double brightness = r * Luma[0] + g * Luma[1] + b * Luma[2];
        if (brightness < 1e-6)
            return null;

        double[] tint = [r / brightness, g / brightness, b / brightness];

        // Pulled back towards neutral until no channel is over the ceiling. It is moved towards 1, not
        // scaled down, because scaling would remove the brightness that unit luminance just added.
        double peak = Math.Max(tint[0], Math.Max(tint[1], tint[2]));
        if (peak > Ceiling)
            for (int i = 0; i < 3; i++)
                tint[i] = 1 + (tint[i] - 1) * ((Ceiling - 1) / (peak - 1));

        // How much of the hue is applied. One is the full colour; zero leaves the vinyl black.
        for (int i = 0; i < 3; i++)
            tint[i] = 1 + (tint[i] - 1) * Saturation;

        return Recolour(neutral, new float[]
        {
            (float)tint[0], 0, 0, 0, 0,
            0, (float)tint[1], 0, 0, 0,
            0, 0, (float)tint[2], 0, 0,
            0, 0, 0, 1, 0,   // alpha untouched: the disc has to stay a disc, not become a square
        });
    }

    /// <summary>Scales each channel so the render's average lands on the colour asked for. This is the
    /// exact press. There is no luminance normalising and no saturation pull-back, because the point here
    /// is the exact number. Everything the render does around its average (the sweep, the grooves, the roll
    /// of the rim) keeps the same ratio.</summary>
    private static byte[]? Body(byte[] neutral, Color body)
    {
        double[] scale =
        [
            body.R / NeutralBody[0],
            body.G / NeutralBody[1],
            body.B / NeutralBody[2],
        ];

        return Recolour(neutral, new float[]
        {
            (float)scale[0], 0, 0, 0, 0,
            0, (float)scale[1], 0, 0, 0,
            0, 0, (float)scale[2], 0, 0,
            0, 0, 0, 1, 0,   // alpha untouched: the disc has to stay a disc, not become a square
        });
    }

    /// <summary>Runs a colour matrix over the render and hands back a PNG.</summary>
    private static byte[]? Recolour(byte[] source, float[] matrix)
    {
        using var src = SkiaSharp.SKBitmap.Decode(source);
        if (src is null)
            return null;

        var info = new SkiaSharp.SKImageInfo(
            src.Width, src.Height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul);
        using var surface = SkiaSharp.SKSurface.Create(info);
        if (surface is null)
            return null;

        surface.Canvas.Clear(SkiaSharp.SKColors.Transparent);
        using (var paint = new SkiaSharp.SKPaint
        {
            ColorFilter = SkiaSharp.SKColorFilter.CreateColorMatrix(matrix),
            IsAntialias = true,
        })
        {
            surface.Canvas.DrawBitmap(src, 0, 0, paint);
        }

        using var snapshot = surface.Snapshot();
        using var encoded = snapshot.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return encoded?.ToArray();
    }
}
