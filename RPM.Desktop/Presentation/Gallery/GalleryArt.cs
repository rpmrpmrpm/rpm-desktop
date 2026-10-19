using SkiaSharp;

namespace RPM.Desktop.Presentation.Gallery;

/// <summary> Album covers, made up. The <c>--gallery</c> harness has no real art to show. Nothing in it
/// is tagged, and there is no network to fetch from. So it draws a cover per album instead: one of a
/// handful of alternating styles (split, rings, blocks, sunburst) over a palette picked from the
/// album's name, with the artist and title set on it. The result is deterministic, so the same album
/// always looks the same, and it is cheap enough to make on demand.
///
/// <para>It is used in two ways. <see cref="Services.MockArtworkService"/> gives the art display a
/// <c>file://</c> URI to one (written to a temp cache). The track-details demo gives the artwork tab
/// the raw PNG bytes.</para> </summary>
public static class GalleryArt
{
    private const int Size = 600;

    /// <summary>bg, mid, accent: three tones that sit together. One per album, by hash.</summary>
    private static readonly (string Bg, string Mid, string Accent, string Ink)[] Palettes =
    [
        ("#101826", "#1F6F8B", "#E8A33D", "#F4F6FB"),   // teal + amber, deep navy
        ("#1A1420", "#7A3B69", "#E85D75", "#F6EEF3"),   // plum + rose
        ("#0E1A14", "#2E7D5B", "#C6E86B", "#EAF6E4"),   // forest + lime
        ("#1C1410", "#B5541F", "#E8C547", "#F7EFE2"),   // rust + gold
        ("#141726", "#3B4E8C", "#6BD6E8", "#EAF0FA"),   // indigo + cyan
        ("#1E1414", "#8C3B3B", "#E8843D", "#F7EBE4"),   // brick + orange
        ("#0F0F12", "#4A4A55", "#D8D8DE", "#FAFAFC"),   // monochrome slate
        ("#160E1E", "#5B2E8C", "#B06BE8", "#F0E8F8"),   // violet
    ];

    /// <summary>PNG bytes for one album's cover. Style and palette are chosen from the name, so it
    /// is stable run to run.</summary>
    public static byte[] CoverPng(string? artist, string? album)
        => Render(artist, album, SKEncodedImageFormat.Png);

    /// <summary>The same cover as JPEG, for the wall's stress library. That library writes a thousand
    /// of these to disk, and full-weight PNGs would be too large.</summary>
    public static byte[] CoverJpeg(string? artist, string? album)
        => Render(artist, album, SKEncodedImageFormat.Jpeg);

    private static byte[] Render(string? artist, string? album, SKEncodedImageFormat format)
    {
        var key = $"{artist}|{album}".Trim();
        var seed = Fnv(key);
        var pal = Palettes[seed % (uint)Palettes.Length];
        var style = (int)((seed >> 8) % 4);

        var info = new SKImageInfo(Size, Size);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;

        var bg = SKColor.Parse(pal.Bg);
        var mid = SKColor.Parse(pal.Mid);
        var accent = SKColor.Parse(pal.Accent);
        var ink = SKColor.Parse(pal.Ink);

        canvas.Clear(bg);
        switch (style)
        {
            case 0: DrawSplit(canvas, mid, accent, seed); break;
            case 1: DrawRings(canvas, mid, accent, seed); break;
            case 2: DrawBlocks(canvas, mid, accent, seed); break;
            default: DrawSunburst(canvas, mid, accent, seed); break;
        }

        DrawLabel(canvas, artist, album, ink, bg);

        using var image = surface.Snapshot();
        using var data = image.Encode(format, format == SKEncodedImageFormat.Jpeg ? 88 : 100);
        return data.ToArray();
    }

    /// <summary>A <c>file://</c> URI to a cached cover PNG, for the art display, which loads a cover
    /// from a URL. The file is written once per album under the temp folder and then reused.</summary>
    public static string? CoverUri(string? artist, string? album)
    {
        if (string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(album))
            return null;

        var key = $"{artist}|{album}";
        var name = $"cover-{Fnv(key):x8}.png";
        var dir = Path.Combine(Path.GetTempPath(), "rpm-gallery-art");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        if (!File.Exists(path))
            File.WriteAllBytes(path, CoverPng(artist, album));
        return new Uri(path).AbsoluteUri;
    }

    // ── styles ──

    private static void DrawSplit(SKCanvas canvas, SKColor mid, SKColor accent, uint seed)
    {
        // A diagonal wedge of the mid tone, with a big off-centre "record" in the accent.
        using (var wedge = new SKPaint { Color = mid, IsAntialias = true })
        using (var path = new SKPath())
        {
            path.MoveTo(0, Size);
            path.LineTo(Size, 0);
            path.LineTo(Size, Size);
            path.Close();
            canvas.DrawPath(path, wedge);
        }
        using var disc = new SKPaint { Color = accent, IsAntialias = true };
        var cx = Size * (0.62f + Jit(seed, 0.06f));
        var cy = Size * 0.40f;
        canvas.DrawCircle(cx, cy, Size * 0.24f, disc);
        using var hole = new SKPaint { Color = mid, IsAntialias = true };
        canvas.DrawCircle(cx, cy, Size * 0.04f, hole);
    }

    private static void DrawRings(SKCanvas canvas, SKColor mid, SKColor accent, uint seed)
    {
        // Concentric grooves, centred: a record seen from above.
        var cx = Size * 0.5f;
        var cy = Size * 0.44f;
        for (var r = Size * 0.44f; r > Size * 0.06f; r -= Size * 0.035f)
        {
            using var ring = new SKPaint
            {
                Color = ((int)(r) % 2 == 0) ? mid : accent,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = Size * 0.012f,
            };
            canvas.DrawCircle(cx, cy, r, ring);
        }
        using var label = new SKPaint { Color = accent, IsAntialias = true };
        canvas.DrawCircle(cx, cy, Size * 0.08f, label);
    }

    private static void DrawBlocks(SKCanvas canvas, SKColor mid, SKColor accent, uint seed)
    {
        // Bauhaus: a bar, a square, a circle.
        using var barPaint = new SKPaint { Color = mid, IsAntialias = true };
        canvas.DrawRect(0, Size * 0.16f, Size, Size * 0.20f, barPaint);
        using var sq = new SKPaint { Color = accent, IsAntialias = true };
        var s = Size * 0.30f;
        canvas.DrawRect(Size * 0.12f, Size * 0.46f, s, s, sq);
        using var circ = new SKPaint { Color = mid, IsAntialias = true };
        canvas.DrawCircle(Size * 0.70f, Size * 0.58f, Size * 0.16f, circ);
    }

    private static void DrawSunburst(SKCanvas canvas, SKColor mid, SKColor accent, uint seed)
    {
        // A gradient sky and a half sun on the horizon.
        using (var grad = new SKPaint { IsAntialias = true })
        {
            grad.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(0, Size),
                new[] { mid, accent }, new[] { 0f, 1f }, SKShaderTileMode.Clamp);
            canvas.DrawRect(0, 0, Size, Size, grad);
        }
        using var sun = new SKPaint { Color = accent, IsAntialias = true };
        canvas.DrawCircle(Size * 0.5f, Size * 0.62f, Size * 0.22f, sun);
        using var line = new SKPaint { Color = new SKColor(0, 0, 0, 60), IsAntialias = true, StrokeWidth = Size * 0.01f };
        for (var i = 1; i <= 5; i++)
            canvas.DrawLine(0, Size * (0.62f + i * 0.05f), Size, Size * (0.62f + i * 0.05f), line);
    }

    // ── the artist + album text, bottom-left over a scrim ──

    private static void DrawLabel(SKCanvas canvas, string? artist, string? album, SKColor ink, SKColor bg)
    {
        using var scrim = new SKPaint { IsAntialias = true };
        scrim.Shader = SKShader.CreateLinearGradient(
            new SKPoint(0, Size * 0.60f), new SKPoint(0, Size),
            new[] { new SKColor(0, 0, 0, 0), new SKColor(0, 0, 0, 170) },
            new[] { 0f, 1f }, SKShaderTileMode.Clamp);
        canvas.DrawRect(0, Size * 0.60f, Size, Size * 0.40f, scrim);

        var normalFace = SKTypeface.FromFamilyName(null, SKFontStyle.Normal) ?? SKTypeface.Default;
        var boldFace = SKTypeface.FromFamilyName(null, SKFontStyle.Bold) ?? SKTypeface.Default;
        using var artistFont = new SKFont(normalFace, Size * 0.045f);
        using var albumFont = new SKFont(boldFace, Size * 0.075f);
        using var inkPaint = new SKPaint { Color = ink, IsAntialias = true };

        var margin = Size * 0.07f;
        if (!string.IsNullOrWhiteSpace(artist))
            canvas.DrawText(artist.ToUpperInvariant(), margin, Size * 0.82f, SKTextAlign.Left, artistFont, inkPaint);
        if (!string.IsNullOrWhiteSpace(album))
        {
            foreach (var line in Wrap(album, albumFont, Size - margin * 2))
            {
                canvas.DrawText(line, margin, Size * 0.90f, SKTextAlign.Left, albumFont, inkPaint);
                canvas.Translate(0, albumFont.Size * 1.15f);
            }
        }
    }

    /// <summary>Greedy word wrap so a long album title does not run off the sleeve.</summary>
    private static IEnumerable<string> Wrap(string text, SKFont font, float maxWidth)
    {
        var words = text.Split(' ');
        var line = "";
        foreach (var word in words)
        {
            var candidate = line.Length == 0 ? word : $"{line} {word}";
            if (font.MeasureText(candidate) > maxWidth && line.Length > 0)
            {
                yield return line;
                line = word;
            }
            else
            {
                line = candidate;
            }
        }
        if (line.Length > 0)
            yield return line;
    }

    // ── helpers ──

    /// <summary>A stable hash. <see cref="string.GetHashCode()"/> is randomised per process, so it
    /// would give the same album a different cover on every run.</summary>
    private static uint Fnv(string s)
    {
        uint hash = 2166136261;
        foreach (var c in s)
        {
            hash ^= c;
            hash *= 16777619;
        }
        return hash;
    }

    /// <summary>A small deterministic jitter in [-amount, +amount], from the seed.</summary>
    private static float Jit(uint seed, float amount) => ((seed % 1000) / 1000f - 0.5f) * 2f * amount;
}
