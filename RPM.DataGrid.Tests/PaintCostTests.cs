using System.Diagnostics;
using RPM.DataGrid.Model;
using SkiaSharp;
using Xunit.Abstractions;

namespace RPM.DataGrid.Tests;

/// <summary>
/// What one frame of the grid costs to draw, at the size the library table actually runs at:
/// twenty-five columns over a million rows, in a window about 1600 by 900.
///
/// <para>The bounds are set against measured figures. On an earlier version of the grid, the worst
/// frame of a fast scroll took 774 ms. With the light-row path and the pacer, the worst frame took
/// 64 ms. Those figures were measured in the running app and include its paint. These tests measure
/// only the grid's own work into a raster surface.</para>
///
/// <para>The bounds are loose on purpose. The numbers the run prints are the answer.</para>
///
/// <para>The tests check the median frame, not the mean. A shared CI machine can stop the test
/// thread for a while, and a pause like that lands in a few frames. A grid that is slow to draw is
/// slow in every frame, so the median still shows it. The class runs on its own, not beside the
/// other tests in this project, for the same reason.</para>
/// </summary>
[Collection(nameof(PaintCostTests))]
public class PaintCostTests(ITestOutputHelper output)
{
    private sealed record Track(string Title, string Artist, string Album, int Year);

    private static List<object?> Tracks(int n)
    {
        var rand = new Random(11);
        string[] words = ["Kokoro", "Idioteque", "Everything", "In Its Right Place", "Reprise",
                          "Live at Wembley", "Remaster", "Dub", "Interlude", "東京の夜", "Москва"];

        var rows = new List<object?>(n);
        for (var i = 0; i < n; i++)
            rows.Add(new Track(
                $"{words[rand.Next(words.Length)]} {i}",
                $"{words[rand.Next(words.Length)]}",
                $"{words[rand.Next(words.Length)]} LP",
                1960 + i % 65));

        return rows;
    }

    /// <summary>The library table's shape: twenty-five columns, two of them marks.</summary>
    private static List<GridColumn> Columns()
    {
        var columns = new List<GridColumn>
        {
            GridColumn.PlayingBars("playing", 30, r => ((Track)r!).Year % 500 == 0),
            GridColumn.TitleBadge("title", 190, r => new TitleCell(((Track)r!).Title, ((Track)r!).Year % 7 == 0),
                header: "Title", canSort: true),
        };

        string[] names = ["artist", "album", "albumartist", "track", "disc", "disctitle", "year",
                          "genre", "work", "composer", "length", "plays", "lastplayed", "added",
                          "kind", "codec", "bitrate", "samplerate", "bitdepth", "channels", "size",
                          "cloud", "folder"];

        foreach (var name in names)
        {
            var n = name;
            columns.Add(GridColumn.Text(n, 130, r =>
            {
                var t = (Track)r!;
                return n switch
                {
                    "artist" => t.Artist,
                    "album" => t.Album,
                    "year" => t.Year.ToString(),
                    _ => $"{t.Album} {t.Year}",
                };
            }, header: n, canSort: true));
        }

        return columns;
    }

    private static PaintHarness Library(int rows) =>
        new(Columns(), new ListSource(Tracks(rows)), 1600, 900);

    /// <summary>The median time of <paramref name="frames"/> calls of <paramref name="paint"/>, in ms.
    /// Garbage is collected first, so a collection of the set-up's work does not land in a frame.</summary>
    private static double MedianFrame(int frames, Action paint)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var times = new double[frames];
        for (var i = 0; i < frames; i++)
        {
            var clock = Stopwatch.StartNew();
            paint();
            times[i] = clock.Elapsed.TotalMilliseconds;
        }

        Array.Sort(times);
        return times[frames / 2];
    }

    [Fact]
    public void A_cold_frame_of_the_library_table()
    {
        using var h = Library(200_000);

        using var bitmap = new SKBitmap(h.Width, h.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);

        // Warm the fonts and the shapers. A running app has done this before anybody scrolls.
        h.Painter.Paint(canvas, h.Frame);

        // The best of three cold frames: each one starts from an empty shop, and a pause of the
        // machine in one of them does not decide the result.
        var ms = double.MaxValue;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            h.Shop.Clear();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            var clock = Stopwatch.StartNew();
            h.Painter.Paint(canvas, h.Frame);
            ms = Math.Min(ms, clock.Elapsed.TotalMilliseconds);
        }

        output.WriteLine(
            $"cold frame: {h.Painter.RowsDrawn} rows x {h.Painter.CellsDrawn / Math.Max(1, h.Painter.RowsDrawn)} columns " +
            $"= {h.Painter.CellsDrawn} cells in {ms:F2}ms");

        Assert.True(ms < 200, $"a cold frame took {ms:F1}ms");
    }

    /// <summary>
    /// A fast scroll: every frame lands on rows nobody has seen, so every cell is shaped for the
    /// first time. This is the gesture that took 774 ms a frame on the earlier grid.
    /// </summary>
    [Fact]
    public void A_second_of_fast_scrolling()
    {
        using var h = Library(200_000);

        using var bitmap = new SKBitmap(h.Width, h.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);

        h.Painter.Paint(canvas, h.Frame);

        // The 200,000 rows above are garbage-collected work waiting to happen. Collect it now, so a
        // collection does not land inside a timed frame.
        GC.Collect();
        GC.WaitForPendingFinalizers();

        const int frames = 60;
        var times = new double[frames];

        for (var i = 0; i < frames; i++)
        {
            // A row and a half a frame is a brisk flick; the rows are never the same twice.
            h.Frame.ScrollY = i * 37 * 24;

            var clock = Stopwatch.StartNew();
            h.Painter.Paint(canvas, h.Frame);
            clock.Stop();

            times[i] = clock.Elapsed.TotalMilliseconds;
        }

        Array.Sort(times);
        var worst = times[^1];
        var nextWorst = times[^2];
        output.WriteLine(
            $"{frames} frames of fast scroll: worst {worst:F2}ms, next {nextWorst:F2}ms, mean {times.Average():F2}ms");

        // One frame may be slow: on a shared CI machine another process or the scheduler can stop this
        // thread for a while. A grid that is slow to draw makes every frame slow, so the second worst
        // frame still shows it.
        Assert.True(nextWorst < 100, $"the second worst frame of a fast scroll took {nextWorst:F1}ms");
    }

    [Fact]
    public void A_still_frame_costs_almost_nothing()
    {
        using var h = Library(200_000);

        using var bitmap = new SKBitmap(h.Width, h.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);

        for (var i = 0; i < 5; i++)
            h.Painter.Paint(canvas, h.Frame);

        var ms = MedianFrame(30, () => h.Painter.Paint(canvas, h.Frame));
        output.WriteLine($"still frame: {ms:F2}ms (median)");

        Assert.True(ms < 40, $"a still frame took {ms:F1}ms");
    }

    /// <summary>
    /// The list's length must not appear in the cost anywhere. A hundred rows and two hundred
    /// thousand draw the same frame.
    /// </summary>
    [Fact]
    public void The_length_of_the_list_does_not_change_the_cost()
    {
        var times = new List<double>();

        foreach (var count in new[] { 100, 200_000 })
        {
            using var h = Library(count);
            using var bitmap = new SKBitmap(h.Width, h.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);

            for (var i = 0; i < 5; i++)
                h.Painter.Paint(canvas, h.Frame);

            times.Add(MedianFrame(20, () => h.Painter.Paint(canvas, h.Frame)));
        }

        output.WriteLine($"100 rows: {times[0]:F2}ms, 200,000 rows: {times[1]:F2}ms");

        Assert.True(times[1] < times[0] * 3 + 2, $"200,000 rows cost {times[1]:F2}ms against {times[0]:F2}ms for 100");
    }
}

/// <summary>Runs <see cref="PaintCostTests"/> on its own, so other tests do not share the CPU with the
/// frames it times.</summary>
[CollectionDefinition(nameof(PaintCostTests), DisableParallelization = true)]
public sealed class PaintCostCollection;
