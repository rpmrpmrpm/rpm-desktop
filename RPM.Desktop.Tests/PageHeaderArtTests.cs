using System.Linq;
using RPM.Desktop.Presentation.Pegasus;
using SkiaSharp;
using Xunit;

namespace RPM.Desktop.Tests;

/// <summary>
/// The photograph in the band across the top of a Pegasus artist or genre page (PageHeaderArt): its size,
/// and the dissolves at its left and its foot. The dissolves are drawn into the pixels, not masked live, so
/// a mistake in them shows as a line across the page that no layout check would catch.
/// </summary>
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
    public void The_left_and_the_foot_dissolve_and_the_rest_is_whole()
    {
        var drawn = PageHeaderArt.Draw(Photo(64, 64), 60, 40, down: 0.5);

        Assert.NotNull(drawn);
        Assert.Equal(60 * 40 * 4, drawn!.Pixels.Length);

        // Right of the left dissolve (45% across) and above the foot's (the last 30%): whole.
        Assert.Equal(255, AlphaAt(drawn, 50, 0));
        Assert.Equal(255, AlphaAt(drawn, 50, 26));

        // The left edge and the last row are next to nothing.
        Assert.True(AlphaAt(drawn, 0, 10) < 8, $"the left edge is at {AlphaAt(drawn, 0, 10)}");
        Assert.True(AlphaAt(drawn, 50, 39) < 8, $"the last row is at {AlphaAt(drawn, 50, 39)}");
    }

    [Fact]
    public void The_dissolves_never_step_back()
    {
        var drawn = PageHeaderArt.Draw(Photo(64, 64), 60, 40, down: 0.5)!;

        // Down the foot, never back up, and across from the left, never back down: a step would read as a line.
        var column = Enumerable.Range(0, 40).Select(y => AlphaAt(drawn, 50, y)).ToList();
        for (var y = 1; y < column.Count; y++)
            Assert.True(column[y] <= column[y - 1], $"row {y} is at {column[y]}, the row above at {column[y - 1]}");

        var row = Enumerable.Range(0, 60).Select(x => AlphaAt(drawn, x, 10)).ToList();
        for (var x = 1; x < row.Count; x++)
            Assert.True(row[x] >= row[x - 1], $"column {x} is at {row[x]}, the column before at {row[x - 1]}");
    }

    [Fact]
    public void Nothing_to_draw_draws_nothing()
    {
        Assert.Null(PageHeaderArt.Draw(null, 100, 20, down: 0.5));
        Assert.Null(PageHeaderArt.Draw([1, 2, 3], 100, 20, down: 0.5));
        Assert.Null(PageHeaderArt.Draw(Photo(8, 8), 0, 20, down: 0.5));
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
