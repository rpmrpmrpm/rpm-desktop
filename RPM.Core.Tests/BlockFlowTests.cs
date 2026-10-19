using RPM.Core.Viz;

namespace RPM.Core.Tests;

/// <summary>
/// The block field. The look of it is not testable, but the flight is: the field is always in front of
/// the lens, the triangles it hands back are whole and opaque, the lattice scrolls with the camera
/// rather than being rebuilt, and the music pushes the speed.
/// </summary>
public class BlockFlowTests
{
    private static float[] Tone(int size = 2048)
    {
        var frame = new float[size];
        for (int i = 0; i < size; i++)
            frame[i] = 0.6f * MathF.Sin(i * 0.07f) + 0.3f * MathF.Sin(i * 0.31f) + 0.1f * MathF.Sin(i * 1.7f);
        return frame;
    }

    private static (float[] Xy, uint[] Argb) Buffers() =>
        (new float[BlockFlow.MaxVertices * 2], new uint[BlockFlow.MaxVertices]);

    [Fact]
    public void Fills_whole_triangles_and_no_more_than_it_promised()
    {
        var field = new BlockFlow();
        var (xy, argb) = Buffers();

        var count = field.Fill(1.5, 16f / 9f, xy, argb);

        Assert.True(count > 0, "the field drew nothing at all");
        Assert.True(count <= BlockFlow.MaxVertices);
        Assert.Equal(0, count % 3);
    }

    [Fact]
    public void Puts_every_corner_somewhere_real()
    {
        var field = new BlockFlow();
        var (xy, argb) = Buffers();

        var count = field.Fill(2.25, 16f / 9f, xy, argb);

        for (int i = 0; i < count * 2; i++)
            Assert.True(float.IsFinite(xy[i]), $"{xy[i]} is not a real coordinate");
    }

    [Fact]
    public void Draws_the_blocks_solid()
    {
        var field = new BlockFlow();
        var (xy, argb) = Buffers();

        var count = field.Fill(0.5, 2f, xy, argb);

        // Opaque, every one: the field is lit geometry, and a see-through block would show the draw
        // order through itself.
        for (int i = 0; i < count; i++)
            Assert.Equal(0xFFu, argb[i] >> 24);
    }

    [Fact]
    public void Colours_a_face_the_same_on_all_three_of_its_corners()
    {
        var field = new BlockFlow();
        var (xy, argb) = Buffers();

        var count = field.Fill(3.0, 2f, xy, argb);

        // Flat shading: a triangle is one face, and a gradient across it would mean the lighting had been
        // worked out per corner instead.
        for (int i = 0; i + 2 < count; i += 3)
        {
            Assert.Equal(argb[i], argb[i + 1]);
            Assert.Equal(argb[i], argb[i + 2]);
        }
    }

    [Fact]
    public void Flies_forward_and_faster_when_the_music_is_loud()
    {
        var quiet = new BlockFlow();
        var loud = new BlockFlow();
        var tone = Tone();

        for (int i = 0; i < 40; i++)
        {
            quiet.Listen(ReadOnlySpan<float>.Empty, i * 0.025);
            loud.Listen(tone, i * 0.025);
        }

        for (int i = 0; i < 60; i++)
        {
            quiet.Advance(1.0 / 60);
            loud.Advance(1.0 / 60);
        }

        Assert.True(quiet.Flown > 0);
        Assert.True(loud.Flown > quiet.Flown);
    }

    [Fact]
    public void Ignores_a_stalled_frame_rather_than_jumping_the_field()
    {
        var field = new BlockFlow();

        // A debugger break, a backgrounded app: the gap must not be integrated whole, or the flight
        // jumps a hundred rows as soon as the phone comes back.
        field.Advance(30.0);

        Assert.True(field.Flown < 1.0);
    }

    [Fact]
    public void Keeps_the_field_ahead_of_the_camera_as_it_travels()
    {
        var near = new BlockFlow();
        var far = new BlockFlow();
        var (xyNear, argbNear) = Buffers();
        var (xyFar, argbFar) = Buffers();

        for (int i = 0; i < 600; i++)
            far.Advance(1.0 / 60);

        var a = near.Fill(0.0, 2f, xyNear, argbNear);
        var b = far.Fill(0.0, 2f, xyFar, argbFar);

        // The lattice renumbers under the flight instead of being consumed, so ten seconds in there is
        // just as much field to look at as there was at the start.
        Assert.True(b > a * 0.5, $"the field thinned out: {a} to {b}");
        Assert.True(far.Flown > 10);
    }
}
