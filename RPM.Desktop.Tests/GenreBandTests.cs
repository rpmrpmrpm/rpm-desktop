// The genre placer is in Presentation/Dev, which only a Debug build compiles (RPM.Desktop.csproj,
// the last PropertyGroup), so its arithmetic is tested in Debug only.
#if DEBUG
using System;
using RPM.Desktop.Presentation.Dev;
using Xunit;

namespace RPM.Desktop.Tests;

/// <summary>
/// The arithmetic behind placing a genre's photograph in its head, and the numbers
/// <c>scripts/gen_genre_photos.py</c> is held to.
///
/// <para><b>Why this file matters more than an ordinary unit test.</b> The tool places the picture and the
/// generator cuts it, in two languages, and neither can call the other. If they disagree by a rounding, the
/// head shows something other than what was placed. The fault shows only after a generator run, so it is
/// hard to find. So the windows below are written out as whole pixels, and the same cases are in the Python
/// docstring, to be checked against.</para>
///
/// <para>The source used throughout is 940 by 650. This is the size of the pinned Pexels image for every
/// one of the 36 genres.</para>
/// </summary>
public class GenreBandTests
{
    private const int W = 940;
    private const int H = 650;

    /// <summary>The widest window is the one whose width is the whole picture. Nothing is wider, because a
    /// wider window would need pixels that the file does not have.</summary>
    [Fact]
    public void WidestIsTheWholeWidth()
    {
        var widest = GenreBand.WidestFor(W, H);
        var window = GenreBand.Window(new GenreFrame(0.5, 0.5, widest), W, H);

        Assert.Equal(W, window.Width, 3);
        Assert.Equal(W / GenreBand.Shape, window.Height, 3);
    }

    /// <summary>Every window has the band's shape, whatever is asked for. This is what lets the generator
    /// write the cut without letterboxing it.</summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(0.48)]
    [InlineData(0.24)]
    [InlineData(0.15)]
    [InlineData(0.09)]
    [InlineData(0.0)]
    public void EveryWindowIsTheBandsShape(double height)
    {
        var window = GenreBand.Window(new GenreFrame(0.5, 0.5, height), W, H);
        Assert.Equal(GenreBand.Shape, window.Width / window.Height, 6);
    }

    /// <summary>
    /// Every window stays on the picture, except that it may hang off the left edge.
    ///
    /// <para>Down and to the right there is nothing to cover a gap, so those edges are hard. The left of
    /// the band is under the genre's colour and name. That is why a gap is allowed there and only there.
    /// See <see cref="GenreBand.MaxGap"/>.</para>
    /// </summary>
    [Theory]
    [InlineData(-3.0, 0.5)]
    [InlineData(4.0, 0.5)]
    [InlineData(0.5, -2.0)]
    [InlineData(0.5, 9.0)]
    public void EveryWindowStandsOnThePictureExceptForTheGap(double across, double down)
    {
        var window = GenreBand.Window(new GenreFrame(across, down, 0.15), W, H);

        Assert.True(window.X >= -(GenreBand.MaxGap * window.Width) - 1e-9);
        Assert.True(window.Y >= 0);
        Assert.True(window.X + window.Width <= W + 1e-9);
        Assert.True(window.Y + window.Height <= H + 1e-9);
    }

    /// <summary>Dragged hard to the right, the window leaves the picture's left edge behind and stops at
    /// the gap's limit. This is the case that MaxGap exists for: a subject left of centre is moved right,
    /// and the colour covers the gap it leaves.</summary>
    [Fact]
    public void DraggedRightItLeavesAGapAndStopsThere()
    {
        var window = GenreBand.Window(new GenreFrame(-5.0, 0.5, 0.15), W, H);

        Assert.Equal(GenreBand.MaxGap * window.Width, window.Gap, 6);
        Assert.True(window.X + window.Width <= W);
    }

    /// <summary>
    /// The zoom goes out past the point where the picture fills the band. The space it leaves goes to the
    /// left.
    ///
    /// <para>Past <see cref="GenreBand.WidestFor"/> the window is wider than the photograph, so the
    /// photograph cannot fill it. The window is held against the picture's RIGHT edge, and the slack is all
    /// on the left, where the colour and the name are. This is the reason zoom can go past one hundred per
    /// cent at all.</para>
    /// </summary>
    [Fact]
    public void ZoomedOutTheSlackIsAllOnTheLeft()
    {
        var window = GenreBand.Window(new GenreFrame(0.5, 0.5, GenreBand.FurthestFor(W, H)), W, H);

        Assert.True(window.Width > W, $"the window is {window.Width} on a picture {W} wide");
        Assert.Equal(W, window.X + window.Width, 6);
        Assert.Equal(GenreBand.MaxGap * window.Width, window.Gap, 6);
    }

    /// <summary>The furthest zoom out is where the slack reaches the largest gap the colour can cover. This
    /// is the same limit a drag reaches from the other direction. Asking for more gets this same
    /// window.</summary>
    [Fact]
    public void ThereIsNothingPastTheFurthest()
    {
        var furthest = GenreBand.Window(new GenreFrame(0.5, 0.5, GenreBand.FurthestFor(W, H)), W, H);
        var asked = GenreBand.Window(new GenreFrame(0.5, 0.5, 9.0), W, H);

        Assert.Equal(GenreBand.WidestFor(W, H) / (1 - GenreBand.MaxGap), GenreBand.FurthestFor(W, H), 6);
        Assert.Equal(furthest.Width, asked.Width, 6);
    }

    /// <summary>The window never leaves a gap on the right, however far it is dragged or zoomed. Nothing
    /// covers that end of the band, so a gap there would show as a flat block beside the
    /// photograph.</summary>
    [Theory]
    [InlineData(0.15)]
    [InlineData(0.24)]
    [InlineData(0.35)]
    [InlineData(0.48)]
    public void ItNeverLeavesAGapOnTheRight(double height)
    {
        var window = GenreBand.Window(new GenreFrame(5.0, 0.5, height), W, H);
        Assert.True(window.X + window.Width <= W + 1e-9);
    }

    /// <summary>A frame with no usable numbers (NaN), for example from a division that went wrong, gives
    /// the picture filling the band. This is the same as the app with no frame at all. It must not give the
    /// far end of the zoom. Once the zoom could go past filling the band, that far end would have been the
    /// wrong answer.</summary>
    [Fact]
    public void NonsenseFillsTheBand()
    {
        var window = GenreBand.Window(new GenreFrame(double.NaN, double.NaN, double.NaN), W, H);
        Assert.Equal(W, window.Width, 3);
    }

    /// <summary>
    /// The focus a shelf ships with, read back as a frame, cuts the same band as the generator's focus
    /// path, to within a pixel.
    ///
    /// <para>This lets the tool open on exactly the shipped view. The picture does not jump when the window
    /// comes up, so each later adjustment is made against what is on screen. The right-hand side is the
    /// focus arithmetic written out: full width, and the slice taken at <c>focus</c> of the way down what
    /// is left.</para>
    ///
    /// <para><b>Within a pixel, not exact to the pixel.</b> The two paths round in different places. The
    /// focus cut rounds the slice's height and then takes a share of what is left. A frame keeps the share
    /// and rounds once at the end. Half a pixel of band cannot be seen. Closing the gap would mean teaching
    /// the frame a rounding that belongs to the other path. The SIZE must not drift, so it is checked
    /// exactly.</para>
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.42)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void FocusReadsBackAsTheSameBand(double focus)
    {
        var window = GenreBand.Window(GenreBand.FromFocus(focus, W, H), W, H);
        var (x, y, width, height) = window.Round(W, H);

        var slice = (int)Math.Round(W / GenreBand.Shape);
        var top = (int)Math.Round((H - slice) * focus);

        Assert.Equal(0, x);
        Assert.Equal(W, width);
        Assert.Equal(slice, height);
        Assert.True(Math.Abs(top - y) <= 1, $"the band moved from {top} to {y}");
    }

    /// <summary>
    /// The windows the Python is held to, as whole pixels.
    ///
    /// <para>Written out rather than derived, because a test that recomputes the thing it is testing pins
    /// nothing. These numbers are what <c>gen_genre_photos.py</c>'s <c>window()</c> must return for the
    /// same frame on the same source.</para>
    /// </summary>
    [Theory]
    // The app's answer: full width, with the slice a little above the middle.
    [InlineData(0.5, 0.4393, 0.2410, 0, 207, 940, 157)]
    // Taken in to two thirds, and moved left and up far enough that the left edge catches it
    [InlineData(0.35, 0.35, 0.16, 17, 175, 624, 104)]
    // Dragged hard into the top-left corner. The top edge holds, and the left edge gives by half a window.
    [InlineData(0.0, 0.0, 0.12, -234, 0, 468, 78)]
    // Dragged hard into the bottom-right corner.
    [InlineData(1.0, 1.0, 0.12, 472, 572, 468, 78)]
    // Moved right until it leaves the picture. The gap is 265 pixels, and the genre's colour covers it.
    [InlineData(0.05, 0.5, 0.16, -265, 273, 624, 104)]
    // Zoomed all the way out. The window is wider than the picture, and is held against its right edge.
    [InlineData(0.5, 0.5, 0.48, -932, 169, 1872, 312)]
    public void TheWindowsThePythonMustAnswer(
        double across, double down, double height, int x, int y, int width, int tall)
    {
        var window = GenreBand.Window(new GenreFrame(across, down, height), W, H);
        Assert.Equal((x, y, width, tall), window.Round(W, H));
    }

    /// <summary>Zooming about a point keeps that pixel of the photograph in place. This is what makes zoom
    /// follow the pointer, not the middle.</summary>
    [Fact]
    public void ZoomHoldsThePixelUnderTheAnchor()
    {
        var frame = new GenreFrame(0.5, 0.5, 0.2);
        var was = GenreBand.Window(frame, W, H);
        var heldX = was.X + (was.Width * 0.25);
        var heldY = was.Y + (was.Height * 0.75);

        var now = GenreBand.Window(GenreBand.Zoomed(frame, 0.6, 0.25, 0.75, W, H), W, H);

        Assert.Equal(heldX, now.X + (now.Width * 0.25), 0);
        Assert.Equal(heldY, now.Y + (now.Height * 0.75), 0);
    }

    /// <summary>A settled frame is one the generator can cut as it stands. Putting it through again changes
    /// nothing. Everything written to the JSON goes through this, so a number in the file always means the
    /// same thing when it is used.</summary>
    [Theory]
    [InlineData(-3.0, 7.0, 4.0)]
    [InlineData(0.5, 0.5, 0.0)]
    [InlineData(0.31, 0.77, 0.13)]
    public void SettlingIsIdempotent(double across, double down, double height)
    {
        var once = GenreBand.Settled(new GenreFrame(across, down, height), W, H);
        var twice = GenreBand.Settled(once, W, H);

        Assert.True(GenreBand.Same(once, twice));
    }

    /// <summary>A photograph with no size yet, because the frame is read before the file, is treated as the
    /// whole picture. This avoids a division by zero.</summary>
    [Fact]
    public void NoSourceIsNoWindow()
    {
        var window = GenreBand.Window(new GenreFrame(0.5, 0.5, 0.2), 0, 0);
        Assert.True(window.Width >= 1 && window.Height >= 1);
    }
}
#endif
