using System;
using System.Linq;
using RPM.Desktop.Presentation.Main.Genres;
using RPM.Desktop.Presentation.Pegasus;
using SkiaSharp;
using Windows.UI;
using Xunit;

namespace RPM.Desktop.Tests;

/// <summary>
/// The picture across the head of a Pegasus genre or artist page (PageHeaderArt): its size, and the
/// foot that fades into the page. The fade is drawn into the pixels, not masked live, so a mistake in
/// it shows as a line across the page that no layout check would catch.
/// </summary>
[Collection(PaletteStandInCollection.Name)]
public class PageHeaderArtTests
{
    [Fact]
    public void A_header_is_drawn_at_its_own_pixels_up_to_the_widest()
    {
        Assert.Equal((1440, 400), PageHeaderArt.PixelsFor(720, 200, 2));

        // Wider than the source photographs. The width is capped at 1920, and the header's shape is kept.
        var (wide, tall) = PageHeaderArt.PixelsFor(1440, 360, 2);
        Assert.Equal(1920, wide);
        Assert.Equal(480, tall);
    }

    [Fact]
    public void The_foot_fades_to_nothing_and_the_top_is_whole()
    {
        var drawn = PageHeaderArt.Draw(Photo(64, 64), 60, 40, down: 0.5, foot: 0.5, across: null);

        Assert.NotNull(drawn);
        Assert.Equal(60 * 40 * 4, drawn!.Pixels.Length);
        Assert.Equal(255, AlphaAt(drawn, 30, 0));
        Assert.Equal(255, AlphaAt(drawn, 30, 18));   // above where the fade starts (row 20)
        Assert.True(AlphaAt(drawn, 30, 39) < 8, $"the last row is at {AlphaAt(drawn, 30, 39)}");

        // Down the fade, never back up: a step would read as a line.
        var column = Enumerable.Range(0, 40).Select(y => AlphaAt(drawn, 30, y)).ToList();
        for (var y = 1; y < column.Count; y++)
            Assert.True(column[y] <= column[y - 1], $"row {y} is at {column[y]}, the row above at {column[y - 1]}");
    }

    [Fact]
    public void A_header_with_no_photograph_draws_its_colour_alone()
    {
        var deep = Color.FromArgb(0xFF, 0x20, 0x40, 0x80);
        var drawn = PageHeaderArt.Draw(null, 100, 20, down: 0.5, foot: 0.4,
                                       [(0, deep), (0.5, deep), (1, Color.FromArgb(0, deep.R, deep.G, deep.B))]);

        Assert.NotNull(drawn);
        Assert.Equal(255, AlphaAt(drawn!, 2, 2));
        Assert.True(AlphaAt(drawn, 98, 2) < 16, $"the right edge is at {AlphaAt(drawn, 98, 2)}");
    }

    [Fact]
    public void Nothing_to_draw_draws_nothing()
    {
        Assert.Null(PageHeaderArt.Draw(null, 100, 20, down: 0.5, foot: 0.4, across: null));
        Assert.Null(PageHeaderArt.Draw([1, 2, 3], 100, 20, down: 0.5, foot: 0.4, across: null));
    }

    [Fact]
    public void The_genre_colour_is_solid_to_the_name_and_then_lets_go()
    {
        var mark = GenreWordmarks.For("Rock")!;
        var stops = GenreWordmarks.OverTheBandStops(GenreAccent.Of(mark), 0.4);

        Assert.Equal(0, stops[0].Offset);
        Assert.Equal(255, stops[0].Colour.A);
        Assert.Equal(0.4, stops[1].Offset, 6);
        Assert.Equal(255, stops[1].Colour.A);
        Assert.Equal(1, stops[^1].Offset, 6);
        Assert.Equal(0, stops[^1].Colour.A);
        for (var i = 1; i < stops.Count; i++)
            Assert.True(stops[i].Offset >= stops[i - 1].Offset);
    }

    private static byte AlphaAt(PageHeaderArt.Drawn drawn, int x, int y) =>
        drawn.Pixels[(((y * drawn.Width) + x) * 4) + 3];

    /// <summary>A plain opaque photograph, encoded as a file would be.</summary>
    private static byte[] Photo(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(new SKColor(200, 120, 40));
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}
