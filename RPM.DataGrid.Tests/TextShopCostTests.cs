using System.Diagnostics;
using RPM.DataGrid.Text;
using SkiaSharp;
using Xunit.Abstractions;

namespace RPM.DataGrid.Tests;

/// <summary>
/// What the text stack costs, at the scale a scroll asks for.
///
/// <para>A viewport is about forty rows of twelve visible columns, so a jump to a part of the list
/// nobody has looked at shapes about five hundred strings in one frame. That is the worst case and
/// it has to fit in a frame with room to spare. Every frame after it is a hit in the cache.</para>
///
/// <para>The bounds here are deliberately loose: ten times the measured figure. So they catch a
/// regression in kind rather than a slow machine. The numbers the run prints are the real answer.
/// Read those, not the bound.</para>
/// </summary>
public class TextShopCostTests(ITestOutputHelper output)
{
    private const int Rows = 40;
    private const int Columns = 12;

    private static SKTypeface LatinOnly()
    {
        foreach (var name in new[] { "Helvetica", "Arial", "Liberation Sans", "DejaVu Sans", "Segoe UI" })
        {
            var face = SKTypeface.FromFamilyName(name);
            if (face is not null)
                return face;
        }

        return SKTypeface.Default;
    }

    /// <summary>A viewport's worth of cells, none of them repeating, the way a library's rows do
    /// not repeat.</summary>
    private static string[] Viewport(int seed)
    {
        var rand = new Random(seed);
        var cells = new string[Rows * Columns];
        string[] words = ["Kokoro", "Idioteque", "Bloc", "Party", "Remaster", "Live", "1997", "Mix",
                          "Dub", "Reprise", "Interlude", "Untitled"];

        for (var i = 0; i < cells.Length; i++)
            cells[i] = $"{words[rand.Next(words.Length)]} {words[rand.Next(words.Length)]} {i}";

        return cells;
    }

    [Fact]
    public void A_cold_viewport_shapes_inside_a_frame()
    {
        using var faces = new SkiaFaces(LatinOnly());
        using var shop = new TextShop(faces);

        var cells = Viewport(1);

        // The first pass warms the fonts and the shapers, which a running app has done by the time
        // anybody scrolls. Measuring it here would be measuring startup.
        foreach (var c in Viewport(0))
            shop.Get(c, 12, 150);

        var clock = Stopwatch.StartNew();
        foreach (var c in cells)
            shop.Get(c, 12, 150);
        clock.Stop();

        var ms = clock.Elapsed.TotalMilliseconds;
        output.WriteLine($"cold: {cells.Length} cells in {ms:F2}ms ({ms / cells.Length * 1000:F1}us each)");

        Assert.True(ms < 160, $"a cold viewport took {ms:F1}ms, which is past ten frames");
    }

    [Fact]
    public void A_viewport_already_shaped_costs_almost_nothing()
    {
        using var faces = new SkiaFaces(LatinOnly());
        using var shop = new TextShop(faces);

        var cells = Viewport(2);
        foreach (var c in cells)
            shop.Get(c, 12, 150);

        var clock = Stopwatch.StartNew();
        for (var pass = 0; pass < 10; pass++)
            foreach (var c in cells)
                shop.Get(c, 12, 150);
        clock.Stop();

        var perFrame = clock.Elapsed.TotalMilliseconds / 10;
        output.WriteLine($"warm: {cells.Length} cells in {perFrame:F3}ms a frame");

        Assert.True(perFrame < 5, $"a warm viewport took {perFrame:F2}ms, which is not a dictionary lookup");
    }

    /// <summary>
    /// Cells that overflow their column cost a binary search of re-shapes. Most columns in the
    /// library grid are narrow enough that most cells do, so this is the case that actually runs.
    /// </summary>
    [Fact]
    public void A_cold_viewport_of_cells_that_all_need_cutting()
    {
        using var faces = new SkiaFaces(LatinOnly());
        using var shop = new TextShop(faces);

        var cells = Viewport(3);
        foreach (var c in Viewport(0))
            shop.Get(c, 12, 40);

        var clock = Stopwatch.StartNew();
        foreach (var c in cells)
            shop.Get(c, 12, 40);
        clock.Stop();

        var ms = clock.Elapsed.TotalMilliseconds;
        output.WriteLine($"cold, all cut: {cells.Length} cells in {ms:F2}ms ({ms / cells.Length * 1000:F1}us each)");

        Assert.True(shop.Get(cells[0], 12, 40).Truncated, "the width was not narrow enough to cut");
        Assert.True(ms < 500, $"cutting a cold viewport took {ms:F1}ms");
    }

    /// <summary>Mixed scripts pay for a fallback lookup the first time they meet a character and
    /// nothing after that.</summary>
    [Fact]
    public void A_cold_viewport_of_mixed_script_cells()
    {
        using var faces = new SkiaFaces(LatinOnly());
        using var shop = new TextShop(faces);

        string[] words = ["東京", "夜", "Москва", "Ωμέγα", "Tokyo", "の声", "Мир"];
        var rand = new Random(4);
        var cells = new string[Rows * Columns];
        for (var i = 0; i < cells.Length; i++)
            cells[i] = $"{words[rand.Next(words.Length)]} {words[rand.Next(words.Length)]} {i}";

        shop.Get("東京 Москва Ωμέγα", 12, 150);

        var clock = Stopwatch.StartNew();
        foreach (var c in cells)
            shop.Get(c, 12, 150);
        clock.Stop();

        var ms = clock.Elapsed.TotalMilliseconds;
        output.WriteLine($"cold, mixed script: {cells.Length} cells in {ms:F2}ms ({ms / cells.Length * 1000:F1}us each)");

        Assert.True(ms < 500, $"a cold mixed-script viewport took {ms:F1}ms");
    }
}
