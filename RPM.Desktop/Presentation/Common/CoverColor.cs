using Windows.UI;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// The colour of a cover, for tinting UI that sits beside it. The dominant hue is read the way the eye
/// would name it. The picture is reduced to a small copy, and coarse colour buckets are counted. The biggest
/// bucket wins. Background extremes (near-white paper and near-black) are thrown out, so a photo takes its
/// colour from the subject, not from the wall behind it. This is shared by the art display's backdrop scrim
/// and the album grid's flip-down band.
/// </summary>
public static class CoverColor
{
    /// <summary>
    /// The colour to tint a surface beside this cover with. It is its <see cref="Highlight"/>, or its
    /// <see cref="Dominant"/> where the sleeve has no vivid colour at all.
    ///
    /// <para>Callers usually want both, and asking for them one after the other costs twice. Each call
    /// decodes and resizes the picture. On a muted sleeve, which is the case the fallback is for, that
    /// happens twice over. Here the picture is decoded once, and both answers are read from the same
    /// 48-pixel square. On a list that reads a record for each row, this is the difference between one Skia
    /// decode per record and two.</para>
    /// </summary>
    public static Color? Tint(byte[] imageBytes)
    {
        try
        {
            using var small = Small(imageBytes);
            if (small is null)
                return null;
            return HighlightOf(small.GetPixelSpan()) ?? DominantOf(small.GetPixelSpan());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The 48-pixel square that every reading below is taken from. It is null where the picture will
    /// not decode, or is too small to say anything.</summary>
    private static SkiaSharp.SKBitmap? Small(byte[] imageBytes)
    {
        using var src = SkiaSharp.SKBitmap.Decode(imageBytes);
        if (src is null || src.Width < 4)
            return null;

        const int size = 48;
        var info = new SkiaSharp.SKImageInfo(size, size, SkiaSharp.SKColorType.Rgba8888,
                                             SkiaSharp.SKAlphaType.Unpremul);
        return src.Resize(info, new SkiaSharp.SKSamplingOptions(
            SkiaSharp.SKFilterMode.Linear, SkiaSharp.SKMipmapMode.Linear));
    }

    /// <summary>The dominant colour of an encoded image. It is null when there is none to give: the picture
    /// cannot be decoded, or it is all background. The colour is not darkened. Callers darken it as they
    /// need.</summary>
    public static Color? Dominant(byte[] imageBytes)
    {
        try
        {
            using var small = Small(imageBytes);
            return small is null ? null : DominantOf(small.GetPixelSpan());
        }
        catch
        {
            return null;
        }
    }

    private static Color? DominantOf(ReadOnlySpan<byte> pixels)
    {
        var buckets = new Dictionary<int, (int Count, int R, int G, int B)>();

        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            int r = pixels[i], g = pixels[i + 1], b = pixels[i + 2];

            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            if (max < 24 || min > 232)
                continue;

            var key = (r >> 5 << 10) | (g >> 5 << 5) | (b >> 5);   // 32 levels on each axis
            var bucket = buckets.TryGetValue(key, out var have) ? have : default;
            buckets[key] = (bucket.Count + 1, bucket.R + r, bucket.G + g, bucket.B + b);
        }

        if (buckets.Count == 0)
            return null;

        var (count, sumR, sumG, sumB) = buckets.Values.OrderByDescending(v => v.Count).First();

        return Color.FromArgb(
            255,
            (byte)Math.Clamp(sumR / count, 0, 255),
            (byte)Math.Clamp(sumG / count, 0, 255),
            (byte)Math.Clamp(sumB / count, 0, 255));
    }

    /// <summary>The most vivid colour in the cover. This is the highlight accent, not the muted dominant
    /// colour. It uses the same coarse buckets, but each bucket is scored by how saturated and bright it
    /// is. It also gives some weight to how much of it there is. So a small splash of vivid colour beats a
    /// large field of grey or brown. It is null when the cover has no real colour to give.</summary>
    public static Color? Highlight(byte[] imageBytes)
    {
        try
        {
            using var small = Small(imageBytes);
            return small is null ? null : HighlightOf(small.GetPixelSpan());
        }
        catch
        {
            return null;
        }
    }

    private static Color? HighlightOf(ReadOnlySpan<byte> pixels)
    {
        var buckets = new Dictionary<int, (int Count, int R, int G, int B)>();

        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            int r = pixels[i], g = pixels[i + 1], b = pixels[i + 2];
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            if (max < 40 || max - min < 24)   // too dark, or too grey to be a highlight
                continue;

            var key = (r >> 5 << 10) | (g >> 5 << 5) | (b >> 5);
            var bucket = buckets.TryGetValue(key, out var have) ? have : default;
            buckets[key] = (bucket.Count + 1, bucket.R + r, bucket.G + g, bucket.B + b);
        }

        if (buckets.Count == 0)
            return null;

        var best = -1.0;
        (int Count, int R, int G, int B) win = default;
        foreach (var v in buckets.Values)
        {
            double r = (double)v.R / v.Count, g = (double)v.G / v.Count, b = (double)v.B / v.Count;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            var sat = max <= 0 ? 0 : (max - min) / max;
            var score = sat * Math.Sqrt(v.Count) * (max / 255.0);   // vivid, present, and bright
            if (score > best)
            {
                best = score;
                win = v;
            }
        }

        return Color.FromArgb(
            255,
            (byte)Math.Clamp(win.R / win.Count, 0, 255),
            (byte)Math.Clamp(win.G / win.Count, 0, 255),
            (byte)Math.Clamp(win.B / win.Count, 0, 255));
    }

    /// <summary>
    /// The colours that a cover is actually made of, most telling first. No two of them are the same colour.
    ///
    /// <para>It is scored the way <see cref="Highlight"/> scores: vivid, present and bright. So a small
    /// splash of real colour beats a large field of mud. But the result is a LIST rather than a single
    /// winner. Each new colour must differ from the colours already chosen. Distance is measured between
    /// the colours' DIRECTIONS, not their values. So a light red and a dark red count as one colour, and
    /// only one of them is returned. Brightness is not what makes two colours different.</para>
    ///
    /// <para>This is for anything that paints a whole surface in a record's colours. If one colour were
    /// asked for twice, the second would have to be invented, for example as a hue turned some number of
    /// degrees away. That reliably puts a colour on screen that is nowhere on the sleeve. That is worse
    /// than returning fewer colours.</para>
    /// </summary>
    /// <returns>Up to <paramref name="count"/> colours. There may be fewer, or none at all, when the cover
    /// has no more to give.</returns>
    public static IReadOnlyList<Color> Palette(byte[] imageBytes, int count)
    {
        if (count < 1)
            return [];

        try
        {
            using var src = SkiaSharp.SKBitmap.Decode(imageBytes);
            if (src is null || src.Width < 4)
                return [];

            const int size = 48;
            var info = new SkiaSharp.SKImageInfo(size, size, SkiaSharp.SKColorType.Rgba8888,
                                                 SkiaSharp.SKAlphaType.Unpremul);
            using var small = src.Resize(info, new SkiaSharp.SKSamplingOptions(
                SkiaSharp.SKFilterMode.Linear, SkiaSharp.SKMipmapMode.Linear));
            if (small is null)
                return [];

            var pixels = small.GetPixelSpan();
            var buckets = new Dictionary<int, (int Count, int R, int G, int B)>();

            for (var i = 0; i + 3 < pixels.Length; i += 4)
            {
                int r = pixels[i], g = pixels[i + 1], b = pixels[i + 2];
                var max = Math.Max(r, Math.Max(g, b));
                var min = Math.Min(r, Math.Min(g, b));
                if (max < 40 || max - min < 24)   // too dark, or too grey to be one of a cover's colours
                    continue;

                var key = (r >> 5 << 10) | (g >> 5 << 5) | (b >> 5);
                var bucket = buckets.TryGetValue(key, out var have) ? have : default;
                buckets[key] = (bucket.Count + 1, bucket.R + r, bucket.G + g, bucket.B + b);
            }

            var scored = new List<(double Score, Color Colour)>(buckets.Count);
            foreach (var v in buckets.Values)
            {
                double r = (double)v.R / v.Count, g = (double)v.G / v.Count, b = (double)v.B / v.Count;
                double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                var sat = max <= 0 ? 0 : (max - min) / max;
                scored.Add((sat * Math.Sqrt(v.Count) * (max / 255.0),
                            Color.FromArgb(255, (byte)Math.Clamp(r, 0, 255),
                                                (byte)Math.Clamp(g, 0, 255),
                                                (byte)Math.Clamp(b, 0, 255))));
            }

            scored.Sort((x, y) => y.Score.CompareTo(x.Score));

            var chosen = new List<Color>(count);
            foreach (var (_, colour) in scored)
            {
                if (chosen.Count >= count)
                    break;
                if (!chosen.Any(taken => SameColour(taken, colour)))
                    chosen.Add(colour);
            }
            return chosen;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Whether two colours would be called the same colour. They are compared as directions, so how
    /// light or dark each one is does not matter.</summary>
    private static bool SameColour(Color a, Color b)
    {
        var (ar, ag, ab) = Unit(a);
        var (br, bg, bb) = Unit(b);
        var d = Math.Sqrt((ar - br) * (ar - br) + (ag - bg) * (ag - bg) + (ab - bb) * (ab - bb));
        return d < 0.22;

        static (double R, double G, double B) Unit(Color c)
        {
            var len = Math.Sqrt(c.R * (double)c.R + c.G * (double)c.G + c.B * (double)c.B);
            return len < 1 ? (0, 0, 0) : (c.R / len, c.G / len, c.B / len);
        }
    }

    /// <summary>Darkens the colour towards black, keeping its hue. An <paramref name="amount"/> of 1 leaves
    /// it unchanged.</summary>
    public static Color Dim(Color colour, double amount) => Color.FromArgb(
        colour.A,
        (byte)Math.Clamp(colour.R * amount, 0, 255),
        (byte)Math.Clamp(colour.G * amount, 0, 255),
        (byte)Math.Clamp(colour.B * amount, 0, 255));

    // The app's own family of colours.

    /// <summary> The ten tints of <c>Assets/RpmPalette.xaml</c>. That is the app's colour family. None of
    /// them was picked by eye. Each one is a real album's drawer tint, kept because it is the clearest of
    /// its hue.
    ///
    /// <para>The values are copied rather than read from that dictionary. The dictionary is a reference
    /// sheet. It is not merged into the app, and the places that use a palette colour quote it. A colour
    /// added there must also be added here. There are ten of them, and they change rarely.</para>
    /// </summary>
    private static readonly Color[] PaletteTints =
    [
        Color.FromArgb(0xFF, 0xD2, 0x31, 0x0E),   // Vermilion
        Color.FromArgb(0xFF, 0xCC, 0x6E, 0x11),   // Rust
        Color.FromArgb(0xFF, 0xEF, 0xAF, 0x12),   // Gold
        Color.FromArgb(0xFF, 0xAD, 0xD0, 0x38),   // Lime
        Color.FromArgb(0xFF, 0x13, 0x4C, 0x27),   // Forest
        Color.FromArgb(0xFF, 0x03, 0x71, 0xD0),   // Azure
        Color.FromArgb(0xFF, 0x47, 0x50, 0xAE),   // Indigo
        Color.FromArgb(0xFF, 0xAC, 0x52, 0x8D),   // Magenta
        Color.FromArgb(0xFF, 0xEA, 0x15, 0x4E),   // Crimson
        Color.FromArgb(0xFF, 0xEE, 0x07, 0x09),   // Red
    ];

    /// <summary>How far a photograph's colour is carried towards its nearest relative in the palette. It is
    /// more than half, so the answer is clearly the app's colour. It is less than all of it, so that two
    /// records of the same broad hue do not come out identical.</summary>
    private const double FamilyPull = 0.62;

    /// <summary>Below this much saturation there is no hue to move. A grey is a grey. Pulling it towards a
    /// palette entry would not tint it. It would paint it.</summary>
    private const double FamilyFloor = 0.10;

    /// <summary>
    /// A colour read off a photograph, brought into the app's own family.
    ///
    /// <para>A dominant tone is whatever the biggest thing in the frame happened to be. Across a library,
    /// that is every hue at every strength. A surface painted straight from it belongs to the photograph,
    /// not to the app. Carried most of the way to the nearest of the ten palette tints, it keeps the
    /// photograph's own hue family. A red sleeve still gives a red pane. It loses the arbitrariness of the
    /// exact angle.</para>
    ///
    /// <para>Hue and saturation move. LIGHTNESS does not. That number is the picture's exposure, and it is
    /// what the callers dim against. A colour that arrives here dark should leave here dark.</para>
    /// </summary>
    public static Color Family(Color colour)
    {
        var (hue, saturation, lightness) = ToHsl(colour);
        if (saturation < FamilyFloor)
            return colour;

        var nearest = PaletteTints[0];
        var shortest = double.MaxValue;
        foreach (var tint in PaletteTints)
        {
            var (kin, _, _) = ToHsl(tint);
            var apart = Math.Abs(Arc(hue, kin));
            if (apart < shortest)
                (shortest, nearest) = (apart, tint);
        }

        var (kinHue, kinSaturation, _) = ToHsl(nearest);
        var moved = hue + (Arc(hue, kinHue) * FamilyPull);

        return FromHsl(((moved % 360) + 360) % 360,
                       saturation + ((kinSaturation - saturation) * FamilyPull),
                       lightness);

        // The short way round the wheel: red at 358 and red at 2 are four degrees apart, not 356.
        static double Arc(double from, double to)
        {
            var apart = ((to - from) % 360 + 540) % 360 - 180;
            return apart;
        }
    }

    // A ground to stand a window of content on.

    /// <summary>How much of the picture's own lightness the ground keeps: nearly all of it. A ground taken
    /// much below the picture stops being the picture's colour, and becomes a shadow of it.</summary>
    private const double GroundLift = 0.85;

    /// <summary>The darkest and lightest ground a picture can ask for. The floor is low on purpose. A
    /// photograph shot at night SHOULD leave the app dark, and the app's own surface is darker still. The
    /// ceiling stops a snowfield turning the library into paper.</summary>
    private const double GroundFloor = 0.04;
    private const double GroundCeiling = 0.42;

    /// <summary>A backstop, and only that. An average is already muted before it reaches this (see <see
    /// cref="Average"/>). So this only applies to a picture shot under one colour of light. Past it, a
    /// ground stops tinting the app and starts looking like paint.</summary>
    private const double GroundSaturation = 0.28;

    /// <summary>
    /// A ground tone for a photograph, made from its <paramref name="average"/> colour.
    ///
    /// <para>Dimming the DOMINANT colour by a factor has no floor. So a photograph of dark jackets takes
    /// the app to black. Keeping the dominant's hue and taking the weight from the picture's brightness
    /// fixes the weight, but not the colour. The dominant is the bucket that the eye would NAME. By
    /// construction it is the most saturated thing in the frame. A whole window painted in it looks like
    /// poster paint, however well judged its lightness is.</para>
    ///
    /// <para>The average is neither of those. Averaging cancels hues against each other. So the result is
    /// already the muted tone that a ground needs, slightly toward grey. It carries the photograph's
    /// daylight or its night in the same number, so there is no second signal to reconcile. It is what the
    /// eye reports when you unfocus on a picture, which is exactly the question being asked.</para>
    /// </summary>
    public static Color Ground(Color average)
    {
        var (hue, saturation, lightness) = ToHsl(average);
        return FromHsl(hue,
                       Math.Min(saturation, GroundSaturation),
                       Math.Clamp(lightness * GroundLift, GroundFloor, GroundCeiling));
    }

    /// <summary>The lightest and heaviest veil that a wash can be given. It is never nothing. Even a night
    /// shot needs a little, so the library's cards have something to be brighter than. It is never so much
    /// that the picture under it stops being a picture.</summary>
    private const double VeilFloor = 0.14;
    private const double VeilCeiling = 0.62;

    /// <summary>Where the veil starts to bite. Below this, the picture is already dark enough to write on.
    /// Above it, every point of lightness has to be paid back.</summary>
    private const double VeilFrom = 0.18;

    /// <summary> How hard a wash made from this picture has to be veiled, from 0 to 1, so that white text
    /// can sit on it.
    ///
    /// <para>A background taken from a photograph carries the photograph's exposure. A bright one leaves
    /// the library's white text on a bright ground, which is unreadable. It is not a matter of taste. So
    /// the veil depends on the picture. A night shot is barely touched, and a snowfield is taken well down.
    /// The veil is black, not the album's colour. It is about exposure, and the tint is already in the wash
    /// underneath it.</para>
    /// </summary>
    public static double Veil(Color average)
    {
        var (_, _, lightness) = ToHsl(average);
        return Math.Clamp(VeilFloor + ((lightness - VeilFrom) * 0.9), VeilFloor, VeilCeiling);
    }

    /// <summary>
    /// Whether BLACK writing reads better than white on this ground.
    ///
    /// <para>It uses perceived brightness, not the average of the three channels. The eye is far more
    /// sensitive to green than to blue. So a saturated blue and a saturated green with the same arithmetic
    /// mean are nowhere near equally bright. An average would give them the same answer. These are the sRGB
    /// luminance weights, the same sum that a contrast ratio is built on.</para>
    ///
    /// <para>The threshold is at the midpoint, not above it. The ground is an AVERAGE of a picture that has
    /// bright parts and dark parts. At a mid reading, the writing will cross both. No margin is left to
    /// favour one side.</para>
    /// </summary>
    public static bool PrefersBlackInk(Color ground)
        => ((0.2126 * ground.R) + (0.7152 * ground.G) + (0.0722 * ground.B)) / 255.0 > 0.5;

    /// <summary> The mean colour of a picture. Every pixel of a downsampled copy is averaged. It is null
    /// when the picture cannot be read. Unlike <see cref="Dominant"/>, nothing is thrown out and nothing is
    /// bucketed. The point of this one is that it involves no choice.
    /// </summary>
    public static Color? Average(byte[] imageBytes)
    {
        try
        {
            using var src = SkiaSharp.SKBitmap.Decode(imageBytes);
            if (src is null || src.Width < 2)
                return null;

            const int size = 48;
            var info = new SkiaSharp.SKImageInfo(size, size, SkiaSharp.SKColorType.Rgba8888,
                                                 SkiaSharp.SKAlphaType.Unpremul);
            using var small = src.Resize(info, new SkiaSharp.SKSamplingOptions(
                SkiaSharp.SKFilterMode.Linear, SkiaSharp.SKMipmapMode.Linear));
            if (small is null)
                return null;

            var pixels = small.GetPixelSpan();
            long r = 0, g = 0, b = 0;
            var n = 0;

            for (var i = 0; i + 3 < pixels.Length; i += 4)
            {
                r += pixels[i];
                g += pixels[i + 1];
                b += pixels[i + 2];
                n++;
            }

            if (n == 0)
                return null;

            return Color.FromArgb(255, (byte)(r / n), (byte)(g / n), (byte)(b / n));
        }
        catch
        {
            return null;
        }
    }

    /// <summary> The mean of a picture AND the mean of one horizontal band of it. Both come from one
    /// decode. A surface that crops a picture, rather than showing all of it, needs both. It needs the
    /// whole picture for its tone, and the band for the part that is on screen.
    ///
    /// <para><paramref name="from"/> and <paramref name="to"/> are fractions of the height, counted from
    /// the top. It is null for a picture that will not read, as above.</para>
    /// </summary>
    public static (Color Whole, Color Band)? Average(byte[] imageBytes, double from, double to)
    {
        try
        {
            using var src = SkiaSharp.SKBitmap.Decode(imageBytes);
            if (src is null || src.Width < 2)
                return null;

            const int size = 48;
            var info = new SkiaSharp.SKImageInfo(size, size, SkiaSharp.SKColorType.Rgba8888,
                                                 SkiaSharp.SKAlphaType.Unpremul);
            using var small = src.Resize(info, new SkiaSharp.SKSamplingOptions(
                SkiaSharp.SKFilterMode.Linear, SkiaSharp.SKMipmapMode.Linear));
            if (small is null)
                return null;

            var top = Math.Clamp((int)Math.Round(from * size), 0, size - 1);
            var bottom = Math.Clamp((int)Math.Round(to * size), top + 1, size);

            var pixels = small.GetPixelSpan();
            long r = 0, g = 0, b = 0, br = 0, bg = 0, bb = 0;
            int n = 0, bn = 0;

            for (var row = 0; row < size; row++)
            {
                var inBand = row >= top && row < bottom;
                for (var col = 0; col < size; col++)
                {
                    var i = ((row * size) + col) * 4;
                    if (i + 3 >= pixels.Length)
                        break;

                    r += pixels[i];
                    g += pixels[i + 1];
                    b += pixels[i + 2];
                    n++;

                    if (!inBand)
                        continue;
                    br += pixels[i];
                    bg += pixels[i + 1];
                    bb += pixels[i + 2];
                    bn++;
                }
            }

            if (n == 0 || bn == 0)
                return null;

            return (Color.FromArgb(255, (byte)(r / n), (byte)(g / n), (byte)(b / n)),
                    Color.FromArgb(255, (byte)(br / bn), (byte)(bg / bn), (byte)(bb / bn)));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Hue (0 to 360), saturation and lightness (0 to 1). This is plain HSL, not a perceptual
    /// colour space. When saturation is as low as a ground's is, the two agree closely enough, and this one
    /// is easier to read.</summary>
    private static (double Hue, double Saturation, double Lightness) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var lightness = (max + min) / 2;

        var span = max - min;
        if (span <= 0)
            return (0, 0, lightness);

        var saturation = lightness > 0.5 ? span / (2 - max - min) : span / (max + min);

        var hue = max == r ? ((g - b) / span) + (g < b ? 6 : 0)
                : max == g ? ((b - r) / span) + 2
                           : ((r - g) / span) + 4;

        return (hue * 60, saturation, lightness);
    }

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        if (saturation <= 0)
        {
            var grey = (byte)Math.Round(Math.Clamp(lightness, 0, 1) * 255);
            return Color.FromArgb(255, grey, grey, grey);
        }

        var q = lightness < 0.5 ? lightness * (1 + saturation) : lightness + saturation - (lightness * saturation);
        var p = (2 * lightness) - q;

        byte Channel(double shift)
        {
            var t = ((hue / 360) + shift) % 1;
            if (t < 0)
                t += 1;

            var v = t < 1 / 6.0 ? p + ((q - p) * 6 * t)
                  : t < 1 / 2.0 ? q
                  : t < 2 / 3.0 ? p + ((q - p) * ((2 / 3.0) - t) * 6)
                                : p;
            return (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        }

        return Color.FromArgb(255, Channel(1 / 3.0), Channel(0), Channel(-1 / 3.0));
    }
}
