using RPM.Core.Viz;

namespace RPM.Core.Tests;

/// <summary>
/// The plate of sand. The figure is emergent, so what can be pinned down is the physics around it:
/// the sand stays on the plate, it does not move when nothing is playing, and it moves when
/// something is.
/// </summary>
public class ChladniPlateTests
{
    /// <summary>A frame of music-like signal: enough spread across the spectrum to sound the plate.</summary>
    private static float[] Tone(int size = 2048)
    {
        var frame = new float[size];
        for (int i = 0; i < size; i++)
            frame[i] = 0.6f * MathF.Sin(i * 0.07f) + 0.3f * MathF.Sin(i * 0.31f) + 0.1f * MathF.Sin(i * 1.7f);
        return frame;
    }

    private static ChladniPlate Sounded(int grains = 500, int frames = 40)
    {
        var plate = new ChladniPlate(grains);
        var tone = Tone();

        for (int i = 0; i < frames; i++)
        {
            plate.Listen(tone);
            plate.Settle(1f / 60f);
        }

        return plate;
    }

    [Fact]
    public void Throws_every_grain_onto_the_plate()
    {
        var plate = new ChladniPlate(1000);

        Assert.Equal(2000, plate.Positions.Length);
        Assert.All(plate.Positions, p => Assert.InRange(p, -1f, 1f));
    }

    [Fact]
    public void Silence_leaves_the_sand_exactly_where_it_lies()
    {
        // Not a decision about performance: it is what the experiment does. A plate nobody is
        // sounding does not shimmer, drift or settle further.
        var plate = new ChladniPlate(500);
        var before = plate.Positions.ToArray();

        for (int i = 0; i < 30; i++)
        {
            plate.Listen(ReadOnlySpan<float>.Empty);
            plate.Settle(1f / 60f);
        }

        Assert.Equal(0f, plate.Drive);
        Assert.Equal(before, plate.Positions);
    }

    [Fact]
    public void Music_drives_the_plate()
    {
        var plate = Sounded();

        Assert.True(plate.Drive > 0f, "a sounded plate is not being driven");
    }

    [Fact]
    public void Music_moves_the_sand()
    {
        var plate = new ChladniPlate(500);
        var before = plate.Positions.ToArray();
        var tone = Tone();

        for (int i = 0; i < 40; i++)
        {
            plate.Listen(tone);
            plate.Settle(1f / 60f);
        }

        Assert.NotEqual(before, plate.Positions);
    }

    [Fact]
    public void Keeps_the_sand_on_the_plate_however_hard_it_is_driven()
    {
        // Grains thrown off the edge are poured back on rather than bounced, so nothing may leave.
        var plate = Sounded(grains: 2000, frames: 300);

        Assert.All(plate.Positions, p => Assert.InRange(p, -1f, 1f));
    }

    [Fact]
    public void The_drive_falls_back_to_silence_when_the_music_stops()
    {
        var plate = Sounded();
        Assert.True(plate.Drive > 0f);

        for (int i = 0; i < 400; i++)
            plate.Listen(ReadOnlySpan<float>.Empty);

        Assert.Equal(0f, plate.Drive);
    }

    [Fact]
    public void The_same_seed_throws_the_same_plate()
    {
        var one = new ChladniPlate(400, seed: 7);
        var two = new ChladniPlate(400, seed: 7);

        Assert.Equal(one.Positions, two.Positions);
    }

    [Fact]
    public void A_different_seed_throws_a_different_plate()
    {
        var one = new ChladniPlate(400, seed: 7);
        var two = new ChladniPlate(400, seed: 8);

        Assert.NotEqual(one.Positions, two.Positions);
    }

    [Fact]
    public void Refuses_a_plate_with_no_sand_on_it()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChladniPlate(0));
    }
}
