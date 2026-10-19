using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using RPM.Desktop.Presentation.Main.Genres;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace RPM.Desktop.Tests;

/// <summary>
/// What a genre's name costs to DRAW, as opposed to what it costs to parse.
///
/// <para>The wall draws its names as outlines, not as text. Drawing them has two costs that are very
/// different. This test measures only one of them, and that one is cheap. So read the result
/// narrowly.</para>
///
/// <para><b>What is measured: the raster.</b> This fills the path the way the renderer does, at the size a
/// card draws it. A Skia scene has no dirty rectangles, so a wall that is scrolled repaints every visible
/// name in full. This measurement shows that the raster is not the costly part. The heaviest name fills in
/// under half a millisecond. The worst six names that can share a screen take about a fifth of one frame
/// together.</para>
///
/// <para><b>What is NOT measured: building the geometry.</b> This is the expensive half. Every command in
/// the data becomes a XAML object. Building the geometry for the largest names, which have about four
/// thousand commands each, takes about 50 ms on the thread that draws the wall. This cost cannot be
/// measured here, because a PathGeometry needs a dispatcher and a test host has none. That is why
/// <c>GenreWordmarks.Read</c> is a separate pure reader. The cost is checked in the running app instead,
/// under RPM_WALL_DIAG, as <c>wordmark:&lt;genre&gt;</c>.</para>
/// </summary>
public class GenreWordmarkCostTests(ITestOutputHelper output)
{
    /// <summary>The height of a name on a card, in physical pixels. A card about 230 points tall gives its
    /// name a little over a quarter of that height. This assumes a display with two pixels per
    /// point.</summary>
    private const int NameHeight = 130;

    /// <summary>How many names are on screen at once. A paired wall shows about three rows of two. The cost
    /// per frame is this many names, and the frame must fit in 16.7 ms.</summary>
    private const int OnScreen = 6;

    private static SKPath Built(string data, double aspect)
    {
        var path = new SKPath { FillType = SKPathFillType.Winding };

        foreach (var contour in GenreWordmarks.Read(data))
        {
            path.MoveTo((float)contour.Start.X, (float)contour.Start.Y);
            foreach (var step in contour.Steps)
            {
                if (step.Curve)
                    path.CubicTo(
                        (float)step.One.X, (float)step.One.Y,
                        (float)step.Two.X, (float)step.Two.Y,
                        (float)step.End.X, (float)step.End.Y);
                else
                    path.LineTo((float)step.End.X, (float)step.End.Y);
            }
            if (contour.Closed)
                path.Close();
        }

        // This matches Stretch="Fill": the drawing is sized to its box. The outlines are normalised to a
        // thousand units tall, so this is the scale a card really uses.
        var bounds = path.Bounds;
        if (bounds.Width > 0 && bounds.Height > 0)
        {
            var box = SKMatrix.CreateScale(
                (float)(NameHeight * aspect) / bounds.Width, NameHeight / bounds.Height);
            path.Transform(box.PreConcat(SKMatrix.CreateTranslation(-bounds.Left, -bounds.Top)));
        }

        return path;
    }

    /// <summary>Fills one name the way the renderer does. It repeats the fill enough times to get a stable
    /// reading from a millisecond clock.</summary>
    private static double MsPerFill(SKPath path, int width)
    {
        using var surface = SKSurface.Create(new SKImageInfo(Math.Max(1, width), NameHeight));
        using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White };
        var canvas = surface.Canvas;

        for (var warm = 0; warm < 20; warm++)
            canvas.DrawPath(path, paint);
        surface.Flush();

        const int Runs = 60;
        var clock = Stopwatch.StartNew();
        for (var run = 0; run < Runs; run++)
            canvas.DrawPath(path, paint);
        surface.Flush();

        return clock.Elapsed.TotalMilliseconds / Runs;
    }

    /// <summary>
    /// The whole table is drawn, and the heaviest names are reported.
    ///
    /// <para>The result is written out, not only asserted. The useful answer is not whether a name is under
    /// the limit, but which names are over it, and by how much. The faces have very different amounts of
    /// detail. A distressed face has hundreds of speckle contours, and a plain face has a dozen letters. A
    /// wall that stalls on three cards out of thirty-six points to a problem in the data for those three
    /// rows.</para>
    /// </summary>
    [Fact]
    public void What_every_name_costs_to_fill()
    {
        var costs = new List<(string Genre, string Face, int Commands, double Ms)>();

        foreach (var genre in GenreWordmarks.Everything)
        {
            var data = genre.On(GenreFlush.Left);
            using var path = Built(data, genre.Aspect);
            costs.Add((genre.Genre, genre.Typeface,
                data.Count(c => c is 'M' or 'L' or 'C' or 'Z'),
                MsPerFill(path, (int)(NameHeight * genre.Aspect))));
        }

        output.WriteLine($"{"genre",-20} {"typeface",-24} {"cmds",6} {"ms/fill",9}");
        foreach (var (name, face, commands, ms) in costs.OrderByDescending(c => c.Ms))
            output.WriteLine($"{name,-20} {face,-24} {commands,6} {ms,9:F3}");

        var worst = costs.OrderByDescending(c => c.Ms).Take(OnScreen).Sum(c => c.Ms);
        var median = costs.OrderBy(c => c.Ms).ElementAt(costs.Count / 2).Ms;

        output.WriteLine("");
        output.WriteLine($"median name: {median:F3} ms");
        output.WriteLine($"worst {OnScreen} together: {worst:F3} ms against a 16.7 ms frame");

        Assert.NotEmpty(costs);
    }
}
