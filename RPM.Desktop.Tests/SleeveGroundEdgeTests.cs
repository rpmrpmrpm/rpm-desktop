using RPM.Desktop.Presentation.Common;
using Windows.UI;

namespace RPM.Desktop.Tests;

/// <summary>
/// <see cref="SleeveGround.FlatAt"/> and <see cref="WashGrid"/>: the colour a drawer shows at the one place
/// its pointer triangle meets it.
///
/// <para>The fault these guard against: the triangle wore one colour for the whole top edge, averaged
/// across the full width of the sleeve. On a sleeve with a dark border and a bright middle, a triangle over
/// the middle came out darker than the drawer under it, and the join showed as a hard line.</para>
/// </summary>
public class SleeveGroundEdgeTests
{
    private static readonly Color Red = Color.FromArgb(255, 200, 0, 0);
    private static readonly Color Blue = Color.FromArgb(255, 0, 0, 200);

    /// <summary>A square sleeve, red on its left half and blue on its right, as a two-cell grid.</summary>
    private static WashGrid RedThenBlue() => new(2, 1, [200, 0, 0, 0, 0, 200], aspect: 1);

    /// <summary>A ground with no colour of its own and no veil, so what it shows is the wash at half.</summary>
    private static SleeveGround Over(WashGrid grid) =>
        new(Mean: Color.FromArgb(255, 100, 0, 100), Edge: Color.FromArgb(255, 100, 0, 100),
            Ground: Color.FromArgb(255, 0, 0, 0), Wash: [1, 2, 3], Veil: 0)
        { Grid = grid };

    [Fact]
    public void A_wide_box_shows_the_middle_band_of_a_square_picture()
    {
        // 400 by 100: the square is scaled to 400 across, so 400 tall, and centred. The top edge of the box
        // is 150 down the picture, which is 0.375 of it.
        var (u, v) = RedThenBlue().Place(0, 0, 400, 100);

        Assert.Equal(0, u, 6);
        Assert.Equal(0.375, v, 6);
    }

    [Fact]
    public void A_tall_box_shows_the_middle_of_its_width()
    {
        // 100 by 400: the square is scaled to 400 tall, so 400 across, and centred. The box's left edge is
        // 150 in.
        var (u, v) = RedThenBlue().Place(0, 0, 100, 400);

        Assert.Equal(0.375, u, 6);
        Assert.Equal(0, v, 6);
    }

    [Fact]
    public void The_colour_follows_the_picture_across_the_drawer()
    {
        var ground = Over(RedThenBlue());

        var left = ground.FlatAt(10, 1, 400, 100);
        var right = ground.FlatAt(390, 1, 400, 100);

        // Half the wash over a black ground: the red side reads red, the blue side blue.
        Assert.Equal(100, left.R);
        Assert.Equal(0, left.B);
        Assert.Equal(0, right.R);
        Assert.Equal(100, right.B);
    }

    [Fact]
    public void Between_two_cells_the_colour_is_their_blend()
    {
        // The middle of the box is half way between the two cell centres.
        var middle = Over(RedThenBlue()).FlatAt(200, 1, 400, 100);

        Assert.Equal(50, middle.R);
        Assert.Equal(50, middle.B);
    }

    /// <summary>The flat colour is one colour for the whole edge. Where the sleeve differs from side to
    /// side, the colour at a point is not it, and that difference is the line the reader saw.</summary>
    [Fact]
    public void A_point_is_not_the_average_where_the_sleeve_changes()
    {
        var ground = Over(RedThenBlue());

        Assert.NotEqual(ground.Flat, ground.FlatAt(10, 1, 400, 100));
    }

    [Fact]
    public void With_nothing_to_read_it_is_the_flat_colour()
    {
        var noGrid = Over(RedThenBlue()) with { Grid = null };
        var unmeasured = Over(RedThenBlue());

        Assert.Equal(noGrid.Flat, noGrid.FlatAt(10, 1, 400, 100));
        Assert.Equal(unmeasured.Flat, unmeasured.FlatAt(10, 1, 0, 0));
    }

    [Fact]
    public void A_point_past_the_edge_reads_the_edge_cell()
    {
        var grid = RedThenBlue();

        Assert.Equal(Red, grid.At(-1, 0.5));
        Assert.Equal(Blue, grid.At(2, 0.5));
    }

    /// <summary>One real drawer on the Skia head: the three layers as they were read for it, and the colour
    /// it drew under the triangle, (126, 117, 119). The head draws the veil over the ground and the wash
    /// together, not over the wash alone, and the triangle has to match what is drawn. See
    /// <c>SleeveGround.Compose</c>.</summary>
    [Fact]
    public void The_point_colour_is_the_colour_the_drawer_draws()
    {
        var sleeve = new SleeveGround(
            Mean: Color.FromArgb(255, 100, 90, 95), Edge: Color.FromArgb(255, 100, 90, 95),
            Ground: Color.FromArgb(255, 104, 86, 98), Wash: [1, 2, 3], Veil: 0.372)
        { Grid = new WashGrid(1, 1, [205, 200, 194], aspect: 1) };

        var at = sleeve.FlatAt(271, 1, 728, 260);

        Assert.InRange(at.R, 125, 127);
        Assert.InRange(at.G, 116, 118);
        Assert.InRange(at.B, 118, 120);
    }

    [Fact]
    public void A_grid_is_read_from_a_real_wash()
    {
        // A 64 by 32 picture, encoded as the wash is: the grid keeps its shape.
        using var bitmap = new SkiaSharp.SKBitmap(64, 32);
        bitmap.Erase(new SkiaSharp.SKColor(10, 120, 230));
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);

        var grid = WashGrid.Read(encoded.ToArray());

        Assert.NotNull(grid);
        Assert.Equal(2, grid!.Aspect, 6);
        Assert.Equal(2 * grid.Height, grid.Width);
        var at = grid.At(0.5, 0.5);
        Assert.Equal(10, at.R);
        Assert.Equal(120, at.G);
        Assert.Equal(230, at.B);
    }
}
