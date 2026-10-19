using RPM.Core.Viz;

namespace RPM.Core.Tests;

/// <summary>
/// The ribbon sea. A test cannot hold what the picture looks like, so these tests pin down the
/// camera and the dynamics around it: every point lands somewhere real, the far lines sit higher on
/// the screen than the near ones, a wider frame is a wider window and not a stretched one, and the
/// swell rises to music and not to silence.
/// </summary>
public class RibbonSeaTests
{
    /// <summary>A frame with enough spread across the spectrum to read as music.</summary>
    private static float[] Tone(int size = 2048)
    {
        var frame = new float[size];
        for (int i = 0; i < size; i++)
            frame[i] = 0.6f * MathF.Sin(i * 0.07f) + 0.3f * MathF.Sin(i * 0.31f) + 0.1f * MathF.Sin(i * 1.7f);
        return frame;
    }

    private static float MeanY(float[] xy, int line)
    {
        var sum = 0f;
        var start = line * RibbonSea.Cols * 2;
        for (int c = 0; c < RibbonSea.Cols; c++)
            sum += xy[start + c * 2 + 1];
        return sum / RibbonSea.Cols;
    }

    [Fact]
    public void Traces_every_point_of_every_line()
    {
        var sea = new RibbonSea();
        var xy = new float[RibbonSea.TraceLength];

        sea.Trace(3.5, 0.5f, xy);

        Assert.Equal(RibbonSea.Lines * RibbonSea.Cols * 2, xy.Length);
        Assert.All(xy, v => Assert.True(float.IsFinite(v), $"{v} is not a real coordinate"));
    }

    [Fact]
    public void Stacks_the_far_lines_above_the_near_ones()
    {
        var sea = new RibbonSea();
        var xy = new float[RibbonSea.TraceLength];

        sea.Trace(2.0, 0.5f, xy);

        // The camera looks down on the sea, so depth reads as height: the back line has to sit above
        // the front one, or the sea is turned inside out.
        Assert.True(MeanY(xy, RibbonSea.Lines - 1) > MeanY(xy, 0));
    }

    [Fact]
    public void Scales_both_axes_by_the_frame_s_height()
    {
        var sea = new RibbonSea();
        var tall = new float[RibbonSea.TraceLength];
        var less = new float[RibbonSea.TraceLength];

        // Two shapes narrow enough that neither gets extra ocean, so this is the projection alone.
        sea.Trace(1.0, 1.0f, tall);
        sea.Trace(1.0, 0.8f, less);

        // One isotropic scale, keyed to the height: x follows the aspect and y does not move. A
        // picture scaled by width in one axis and by height in the other is a stretched picture.
        for (int i = 0; i < RibbonSea.TraceLength; i += 2)
        {
            Assert.Equal(tall[i] * 0.8f, less[i], 4);
            Assert.Equal(tall[i + 1], less[i + 1], 4);
        }
    }

    [Fact]
    public void Runs_the_lines_off_the_sides_of_a_wide_frame()
    {
        var sea = new RibbonSea();
        var wide = new float[RibbonSea.TraceLength];
        var square = new float[RibbonSea.TraceLength];

        sea.Trace(1.0, 390f / 844f, wide);     // a phone on its side
        sea.Trace(1.0, 1.0f, square);

        // A line whose taper is visible in mid-air looks as if the sea has run out. On the desktop's
        // square surface the lines leave the frame on their own. Scaling the same world by a landscape
        // phone's height would pull their ends a few per cent inside the frame, which is what the
        // extra ocean is for. Both shapes have to overrun, and neither by so much that the sea is all
        // off-screen.
        Assert.True(Widest(wide) > 1.05f, $"the sea stopped short of the edges at {Widest(wide):F2}");
        Assert.True(Widest(square) > 1.05f, $"the sea stopped short of the edges at {Widest(square):F2}");
        Assert.InRange(Widest(wide), 1.05f, 2.5f);
    }

    private static float Widest(float[] xy)
    {
        var widest = 0f;
        for (int i = 0; i < RibbonSea.TraceLength; i += 2)
            widest = MathF.Max(widest, MathF.Abs(xy[i]));
        return widest;
    }

    [Fact]
    public void Keeps_the_lines_inside_a_frame_that_could_be_looked_at()
    {
        var sea = new RibbonSea();
        var xy = new float[RibbonSea.TraceLength];

        // Loud, so the swell is at its highest and the sea is moving strongly.
        var tone = Tone();
        for (int i = 0; i < 60; i++)
            sea.Listen(tone, i * 0.025);

        sea.Trace(4.0, 0.5f, xy);

        // Lines run off the sides on purpose: the frame is a window onto a wider ocean. But nothing
        // may run off the top or the bottom, which would look as if the camera had lost the sea.
        for (int i = 1; i < RibbonSea.TraceLength; i += 2)
            Assert.InRange(xy[i], -1.5f, 1.5f);
    }

    [Fact]
    public void Leads_with_the_front_line()
    {
        var sea = new RibbonSea();

        Assert.True(sea.Brightness(0) > sea.Brightness(RibbonSea.Lines - 1));
    }

    [Fact]
    public void Rests_flat_in_silence_and_heaves_to_music()
    {
        var quiet = new RibbonSea();
        var loud = new RibbonSea();
        var tone = Tone();

        for (int i = 0; i < 40; i++)
        {
            quiet.Listen(ReadOnlySpan<float>.Empty, i * 0.025);
            loud.Listen(tone, i * 0.025);
        }

        Assert.True(loud.Swell > quiet.Swell);
        Assert.True(quiet.Swell < 0.05f);
    }

    [Fact]
    public void Settles_slowly_once_the_music_stops()
    {
        var sea = new RibbonSea();
        var tone = Tone();

        for (int i = 0; i < 40; i++)
            sea.Listen(tone, i * 0.025);

        var peak = sea.Swell;

        // A quarter of a second of silence: past the hold, but the release is deliberately slow, so
        // the sea must still be most of the way up. An ocean cannot stop dead.
        for (int i = 40; i < 60; i++)
            sea.Listen(ReadOnlySpan<float>.Empty, i * 0.025);

        Assert.True(sea.Swell < peak);
        Assert.True(sea.Swell > peak * 0.5f);
    }
}
