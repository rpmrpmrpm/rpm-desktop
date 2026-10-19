using RPM.DataGrid.Model;
using RPM.DataGrid.Paint;
using SkiaSharp;

namespace RPM.DataGrid.Tests;

/// <summary>
/// The art cell: a picture the host answers for by key, and a well while it has not.
/// </summary>
public class ArtCellTests : IDisposable
{
    private sealed record Row(string Album);

    private readonly List<SKImage> _made = [];

    public void Dispose()
    {
        foreach (var image in _made)
            image.Dispose();
    }

    /// <summary>A solid square, so a drawn cover is unmistakable against the grid's dark ground.</summary>
    private SKImage Square(SKColor colour, int side = 64)
    {
        var info = new SKImageInfo(side, side, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(colour);
        var image = surface.Snapshot();
        _made.Add(image);
        return image;
    }

    private static PaintHarness Grid(IEnumerable<string> albums, Func<string, SKImage?>? art)
    {
        var harness = new PaintHarness(
            [
                GridColumn.Art("art", 34, r => ((Row)r).Album),
                GridColumn.Text("album", 200, r => ((Row)r).Album, header: "Album"),
            ],
            new ListSource([.. albums.Select(object? (a) => new Row(a))]),
            width: 300, height: 200);

        harness.Frame.Art = art;
        harness.Frame.Metrics = new GridMetrics { RowHeight = 34, HeaderHeight = 22 };
        harness.Frame.Rows = new RowStrip(34);
        return harness;
    }

    [Fact]
    public void A_cover_the_host_has_is_drawn_in_its_cell()
    {
        using var h = Grid(["Absolution"], _ => Square(SKColors.Red));

        using var shot = h.Render();

        // Somewhere in the first row's art column there is red, which only the picture can have put
        // there: nothing in the palette is red.
        var found = false;
        for (var y = 24; y < 54 && !found; y++)
            for (var x = 0; x < 34 && !found; x++)
                found = shot.At(x, y).Red > 200 && shot.At(x, y).Green < 80;

        Assert.True(found);
    }

    /// <summary>The ink in the first row's art column, which is what the well and the cover both
    /// land in. Measured against a row with no key at all, because the row itself draws a rule
    /// through that band whether or not the cell puts anything there.</summary>
    private static int ArtInk(Shot shot) => shot.InkIn(0, 24, 34, 54);

    [Fact]
    public void A_cover_that_has_not_arrived_leaves_the_well_and_not_a_hole()
    {
        // Null is the answer while a decode is still running. The cell must still draw something, or
        // the column appears to fill itself in at random as covers land.
        using var waiting = Grid(["Absolution"], _ => null);
        using var nothing = Grid([""], _ => null);

        using var withWell = waiting.Render();
        using var without = nothing.Render();

        Assert.True(ArtInk(withWell) > ArtInk(without));
    }

    [Fact]
    public void A_grid_with_no_art_source_at_all_draws_the_well_and_does_not_throw()
    {
        using var h = Grid(["Absolution"], art: null);
        using var bare = Grid([""], art: null);

        using var shot = h.Render();
        using var without = bare.Render();

        Assert.Equal(1, h.Painter.RowsDrawn);
        Assert.True(ArtInk(shot) > ArtInk(without));
    }

    [Fact]
    public void Every_row_of_one_album_asks_for_the_same_picture()
    {
        // The whole reason the cell carries a key and not pixels: a twelve-track record decodes one
        // cover, not twelve.
        var asked = new List<string>();
        using var h = Grid(
            ["Absolution", "Absolution", "Absolution"],
            key => { asked.Add(key); return Square(SKColors.Red); });

        using var shot = h.Render();

        Assert.Equal(3, asked.Count);
        Assert.All(asked, key => Assert.Equal("Absolution", key));
        Assert.Single(asked.Distinct());
    }

    [Fact]
    public void A_row_with_no_album_to_key_on_draws_no_well_and_no_picture()
    {
        using var keyed = Grid(["Absolution"], _ => Square(SKColors.Red));
        using var bare = Grid([""], _ => Square(SKColors.Red));

        using var withArt = keyed.Render();
        using var without = bare.Render();

        Assert.True(ArtInk(withArt) > ArtInk(without));

        // And no red anywhere: a blank key must not be answered with somebody else's cover.
        for (var y = 24; y < 54; y++)
            for (var x = 0; x < 34; x++)
                Assert.False(without.At(x, y).Red > 200 && without.At(x, y).Green < 80);
    }
}
