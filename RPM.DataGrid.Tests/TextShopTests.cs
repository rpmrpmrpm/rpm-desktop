using RPM.DataGrid.Text;
using SkiaSharp;

namespace RPM.DataGrid.Tests;

/// <summary>
/// The text stack against real fonts, drawn into a real surface and read back.
///
/// <para>A drawn grid does its own font fallback. The failure it guards against is a Japanese title
/// drawn as a row of identical boxes. That failure looks like working software, until someone with a
/// non-Latin library opens the app. So the test draws two different Japanese strings and asserts that
/// the pixels differ. Under .notdef they would not: every box is the same box.</para>
/// </summary>
public class TextShopTests
{
    /// <summary>A face that draws Latin and nothing else, which is the situation fallback exists
    /// for. Every platform this runs on has one of these.</summary>
    private static SKTypeface LatinOnly()
    {
        foreach (var name in new[] { "Helvetica", "Arial", "Liberation Sans", "DejaVu Sans", "Segoe UI" })
        {
            var face = SKTypeface.FromFamilyName(name);
            if (face is not null && face.FamilyName.Contains(name.Split(' ')[0], StringComparison.OrdinalIgnoreCase))
                return face;
            face?.Dispose();
        }

        return SKTypeface.Default;
    }

    private static (SkiaFaces Faces, TextShop Shop) Shop()
    {
        var faces = new SkiaFaces(LatinOnly());
        return (faces, new TextShop(faces));
    }

    /// <summary>Draws the text white on black and returns the raster. Black is the ground so "no ink"
    /// is unambiguous.</summary>
    private static byte[] Raster(TextShop shop, string text, float size, int w, int h)
    {
        using var bitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);

        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        shop.Get(text, size).Draw(canvas, 2, h - 6, paint);

        return bitmap.Bytes;
    }

    private static int Ink(byte[] raster)
    {
        var lit = 0;
        for (var i = 0; i < raster.Length; i += 4)
            if (raster[i] > 24 || raster[i + 1] > 24 || raster[i + 2] > 24)
                lit++;
        return lit;
    }

    [Fact]
    public void Latin_text_is_shaped_and_has_width()
    {
        var (faces, shop) = Shop();
        using var _ = faces;
        using var __ = shop;

        var text = shop.Get("Blue Monday", 12);

        Assert.False(text.IsEmpty);
        Assert.True(text.Width > 20, $"width was {text.Width}");
        Assert.False(text.Truncated);
    }

    [Fact]
    public void An_empty_string_draws_nothing()
    {
        var (faces, shop) = Shop();
        using var _ = faces;
        using var __ = shop;

        Assert.True(shop.Get("", 12).IsEmpty);
        Assert.True(shop.Get(null, 12).IsEmpty);
        Assert.Equal(0, shop.Get("", 12).Width);
    }

    /// <summary>
    /// The fallback is doing something: the primary face has no glyph for these and the resolver
    /// finds one that does.
    /// </summary>
    [Fact]
    public void A_face_is_found_for_characters_the_primary_cannot_draw()
    {
        using var faces = new SkiaFaces(LatinOnly());

        foreach (var cp in new[] { 0x65E5 /* 日 */, 0x0416 /* Ж */, 0x03A9 /* Ω */ })
        {
            var fallback = faces.Fallback(cp);
            Assert.True(fallback >= 0, $"nothing installed draws U+{cp:X4}");
            Assert.True(faces.Covers(fallback, cp), $"the face chosen for U+{cp:X4} does not draw it");
        }
    }

    /// <summary>
    /// The tofu test. Two Japanese strings of the same length draw different pixels. If the primary
    /// face were used for both, every character would be .notdef, the same box, and the two
    /// rasters would be identical.
    /// </summary>
    [Fact]
    public void Two_different_japanese_titles_do_not_draw_the_same_pixels()
    {
        var (faces, shop) = Shop();
        using var _ = faces;
        using var __ = shop;

        var a = Raster(shop, "日本語", 14, 90, 24);
        var b = Raster(shop, "音楽録", 14, 90, 24);

        Assert.True(Ink(a) > 0, "the first title drew no ink at all");
        Assert.True(Ink(b) > 0, "the second title drew no ink at all");
        Assert.False(a.AsSpan().SequenceEqual(b), "two different titles drew identical pixels, which is .notdef");
    }

    [Fact]
    public void Cyrillic_and_greek_draw_ink_too()
    {
        var (faces, shop) = Shop();
        using var _ = faces;
        using var __ = shop;

        Assert.True(Ink(Raster(shop, "Москва", 14, 120, 24)) > 0);
        Assert.True(Ink(Raster(shop, "Ωμέγα", 14, 120, 24)) > 0);
    }

    /// <summary>A mixed title is one string with two faces in it, and it is wider than either half
    /// alone.</summary>
    [Fact]
    public void A_mixed_script_title_is_wider_than_either_half()
    {
        var (faces, shop) = Shop();
        using var _ = faces;
        using var __ = shop;

        var latin = shop.Get("Kokoro ", 12).Width;
        var japanese = shop.Get("の声", 12).Width;
        var both = shop.Get("Kokoro の声", 12).Width;

        Assert.True(both > latin, $"{both} should exceed {latin}");
        Assert.True(both > japanese, $"{both} should exceed {japanese}");
    }

    [Fact]
    public void Text_that_fits_is_not_truncated()
    {
        var (faces, shop) = Shop();
        using var _ = faces;
        using var __ = shop;

        var text = shop.Get("Short", 12, 400);

        Assert.False(text.Truncated);
        Assert.True(text.Width <= 400);
    }

    [Fact]
    public void Text_that_does_not_fit_is_cut_to_the_width()
    {
        var (faces, shop) = Shop();
        using var _ = faces;
        using var __ = shop;

        const string title = "Everything In Its Right Place (Gigamesh Remix)";
        var full = shop.Get(title, 12).Width;
        var cut = shop.Get(title, 12, 80);

        Assert.True(full > 80, "the test string was not long enough to need cutting");
        Assert.True(cut.Truncated);
        Assert.True(cut.Width <= 80, $"cut to {cut.Width}, which is over the 80 asked for");
        Assert.True(cut.Width > 20, "cutting threw away more than it had to");
    }

    /// <summary>Cutting happens on graphemes, so a CJK title is cut between characters and not
    /// inside one.</summary>
    [Fact]
    public void A_japanese_title_is_cut_to_the_width_as_well()
    {
        var (faces, shop) = Shop();
        using var _ = faces;
        using var __ = shop;

        const string title = "東京の夜と朝と真昼の音";
        var cut = shop.Get(title, 12, 50);

        Assert.True(cut.Truncated);
        Assert.True(cut.Width <= 50, $"cut to {cut.Width}");
        Assert.False(cut.IsEmpty);
    }

    /// <summary>An emoji in a playlist name. It has to draw, and a colour font must not throw.</summary>
    [Fact]
    public void An_emoji_draws_ink()
    {
        var (faces, shop) = Shop();
        using var _ = faces;
        using var __ = shop;

        Assert.True(Ink(Raster(shop, "Party 🎧", 14, 120, 24)) > 0);
    }

    [Fact]
    public void The_same_request_comes_back_as_the_same_object()
    {
        var (faces, shop) = Shop();
        using var _ = faces;
        using var __ = shop;

        var first = shop.Get("Idioteque", 12, 120);
        var again = shop.Get("Idioteque", 12, 120);

        Assert.Same(first, again);
        Assert.Equal(1, shop.Held);
    }

    /// <summary>
    /// A different width is a different answer, because the ellipsis lands somewhere else. Two
    /// entries, not one overwritten.
    /// </summary>
    [Fact]
    public void The_same_string_at_two_widths_is_two_entries()
    {
        var (faces, shop) = Shop();
        using var _ = faces;
        using var __ = shop;

        shop.Get("Everything In Its Right Place", 12, 60);
        shop.Get("Everything In Its Right Place", 12, 120);

        Assert.Equal(2, shop.Held);
    }

    /// <summary>
    /// The cache is bounded and keeps working past its bound. The generation that is dropped takes
    /// its blobs with it, so this also proves nothing still in use is disposed underneath a caller.
    /// </summary>
    [Fact]
    public void The_cache_stays_bounded_and_keeps_answering()
    {
        var (faces, shop) = Shop();
        using var _ = faces;
        using var __ = new TextShop(faces, limit: 64);
        using var small = new TextShop(faces, limit: 64);

        for (var i = 0; i < 500; i++)
        {
            var text = small.Get($"Track {i:D4}", 12);
            Assert.False(text.IsEmpty);
            Assert.True(text.Width > 0);
        }

        Assert.True(small.Held <= 128, $"held {small.Held}, which is past two generations of 64");
    }

    [Fact]
    public void Clearing_lets_it_start_again()
    {
        var (faces, shop) = Shop();
        using var _ = faces;
        using var __ = shop;

        shop.Get("One", 12);
        shop.Get("Two", 12);
        Assert.Equal(2, shop.Held);

        shop.Clear();
        Assert.Equal(0, shop.Held);

        Assert.False(shop.Get("One", 12).IsEmpty);
    }

    /// <summary>Metrics come from the primary face, and a row's baseline is placed from them.</summary>
    [Fact]
    public void Metrics_describe_the_primary_face()
    {
        var (faces, shop) = Shop();
        using var _ = faces;
        using var __ = shop;

        var m = shop.Metrics(12);

        Assert.True(m.Ascent < 0, "ascent is measured upward from the baseline and should be negative");
        Assert.True(m.Descent > 0);
        Assert.True(m.Descent - m.Ascent > 8, "12px text should have a line box taller than 8px");
    }
}
