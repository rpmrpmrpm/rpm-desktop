using RPM.Core.Viz;

namespace RPM.Core.Tests;

/// <summary>
/// The figures of the room's other animations (<see cref="RoomFigures"/>): Yang's balls and their tails,
/// Lava's blobs and Alchemy's ribbons. The shader only draws what these numbers say, so what can be seen of
/// the motion is tested here. The Android TV app has the same tests for the same numbers.
/// </summary>
public class RoomFiguresTests
{
    private static float At(float[] a, int i, int k) => a[i * 4 + k];

    [Fact]
    public void Yangs_pairs_stand_opposite_each_other()
    {
        var f = new RoomFigures();
        f.Advance(3.7, 0.4f, 0.2f);
        f.Yang(3.7, 0.4f, 0.2f, 0.3f);

        foreach (var (family, other) in new[] { (f.Big, RoomFigures.BigEchoes + 1), (f.Small, RoomFigures.SmallEchoes + 1) })
        {
            Assert.Equal(-At(family, 0, 0), At(family, other, 0), 1e-5f);
            Assert.Equal(-At(family, 0, 1), At(family, other, 1), 1e-5f);
        }
    }

    [Fact]
    public void The_big_balls_follow_the_bass_and_the_small_ones_the_air()
    {
        var quiet = new RoomFigures();
        quiet.Yang(1.0, 0f, 0f, 0f);
        var bass = new RoomFigures();
        bass.Yang(1.0, 1f, 0f, 0f);
        var air = new RoomFigures();
        air.Yang(1.0, 0f, 0f, 1f);

        Assert.True(At(bass.Big, 0, 2) > At(quiet.Big, 0, 2) * 1.5f);
        Assert.Equal(At(quiet.Small, 0, 2), At(bass.Small, 0, 2), 1e-6f);
        Assert.True(At(air.Small, 0, 2) > At(quiet.Small, 0, 2) * 1.5f);
        Assert.Equal(At(quiet.Big, 0, 2), At(air.Big, 0, 2), 1e-6f);
    }

    [Fact]
    public void Each_echo_is_smaller_lighter_and_further_back()
    {
        var f = new RoomFigures();
        f.Advance(2, 0f, 0f);
        f.Yang(2.0, 0f, 0f, 0f);

        double angle = Math.Atan2(At(f.Big, 0, 1), At(f.Big, 0, 0) / RoomFigures.Wide);
        for (int k = 1; k <= RoomFigures.BigEchoes; k++)
        {
            Assert.True(At(f.Big, k, 2) < At(f.Big, k - 1, 2));
            Assert.True(At(f.Big, k, 3) < At(f.Big, k - 1, 3));

            // Behind: the room turns anticlockwise, so each echo is at a smaller angle (modulo a turn).
            double a = Math.Atan2(At(f.Big, k, 1), At(f.Big, k, 0) / RoomFigures.Wide);
            double step = Math.IEEERemainder(angle - a, 2 * Math.PI);
            Assert.True(step > 0.0, $"echo {k} is not behind");
            angle = a;
        }
        Assert.Equal(1f, At(f.Big, 0, 3));
    }

    [Fact]
    public void The_bass_turns_Yang_faster_while_music_plays()
    {
        // Music playing, with the middle up either way: only the bass differs.
        var calm = new RoomFigures();
        calm.Advance(1, 0f, 1f);
        var loud = new RoomFigures();
        loud.Advance(1, 1f, 1f);

        Assert.True(loud.Spin > calm.Spin * 2);
    }

    [Fact]
    public void Yang_turns_at_the_music_pace_while_music_plays()
    {
        // Full bass: the speed the levels give, at 40%.
        var loud = new RoomFigures();
        loud.Advance(1, 1f, 1f);
        Assert.Equal((RoomFigures.YangSpin + RoomFigures.YangSpinBass) * RoomFigures.YangMusicPace, loud.Spin, 9);

        // Silence keeps the plain drift.
        var quiet = new RoomFigures();
        quiet.Advance(1, 0f, 0f);
        Assert.Equal(RoomFigures.YangSpin, quiet.Spin, 9);
    }

    [Fact]
    public void The_small_balls_tail_is_one_streak_whatever_the_spin()
    {
        // The echoes stand closer than the smaller of two neighbours' radii, so they overlap into one tail. That
        // holds at any spin, and at the loudest air, which spaces them furthest.
        foreach (var spin in new[] { 0f, 3f, 40f, 400f })
        {
            var f = new RoomFigures();
            f.Advance(spin, 1f, 0f);
            f.Yang(spin, 1f, 0f, 1f);

            double length = 0;
            for (int k = 1; k <= RoomFigures.SmallEchoes; k++)
            {
                double gap = Math.Sqrt(
                    Math.Pow(At(f.Small, k, 0) - At(f.Small, k - 1, 0), 2)
                    + Math.Pow(At(f.Small, k, 1) - At(f.Small, k - 1, 1), 2));
                Assert.True(gap < At(f.Small, k, 2), $"echo {k} is {gap} away");
                length += gap;
            }

            // Spaced by length along the orbit: the same tail at every spin. The gaps are chords, so the sum
            // is a little short of the arc.
            Assert.InRange(length, 0.045 * 1.4 * RoomFigures.SmallEchoes - 0.012, 0.045 * 1.4 * RoomFigures.SmallEchoes + 0.012);
        }
    }

    [Fact]
    public void A_still_room_keeps_its_pose()
    {
        var f = new RoomFigures();
        f.Advance(5, 0.5f, 0.5f);
        f.Alchemy(0f, 0f, 0f);
        var before = f.RibbonB.ToArray();

        f.Advance(0, 1f, 1f);
        f.Alchemy(0f, 0f, 0f);

        Assert.Equal(before, f.RibbonB);
    }

    [Fact]
    public void A_new_room_is_the_skins_picture()
    {
        // At a drift of 0 every swing is at its middle: the script's own numbers (make_rpamp_ground.py).
        var f = new RoomFigures();
        f.Alchemy(0f, 0f, 0f);

        Assert.Equal(0.44f, At(f.RibbonA, 1, 0), 1e-6f);
        Assert.Equal(0.13f, At(f.RibbonA, 1, 2), 1e-6f);
        Assert.Equal(2.1f, At(f.RibbonB, 1, 0), 1e-6f);
        Assert.Equal(0.16f, At(f.RibbonB, 1, 2), 1e-6f);
        Assert.Equal(0.90f, At(f.RibbonC, 1, 1), 1e-6f);
        Assert.Equal(0.22f, At(f.RibbonC, 1, 2), 1e-6f);
    }

    [Fact]
    public void The_ribbons_swing_but_never_wander_off()
    {
        var start = new RoomFigures();
        start.Alchemy(0f, 0f, 0f);

        var f = new RoomFigures();
        for (int n = 0; n < 4000; n++)
        {
            f.Advance(0.25, 0f, 1f);
            f.Alchemy(0f, 0f, 0f);
            for (int i = 0; i < RoomFigures.Ribbons; i++)
            {
                Assert.True(Math.Abs(At(f.RibbonB, i, 0) - At(start.RibbonB, i, 0)) <= 1.1f + 1e-5f);
                Assert.True(Math.Abs(At(f.RibbonA, i, 0) - At(start.RibbonA, i, 0)) <= 0.025f + 1e-5f);
            }
        }
    }

    [Fact]
    public void Lava_stays_on_the_screen()
    {
        var f = new RoomFigures();
        for (int n = 0; n < 2000; n++)
        {
            f.Advance(0.25, 0.6f, 0.3f);
            f.Lava(0.6f, 0.3f);
            for (int i = 0; i < RoomFigures.LavaBlobs; i++)
            {
                Assert.True(Math.Abs(At(f.Blobs, i, 0)) < 0.89f);
                Assert.True(Math.Abs(At(f.Blobs, i, 1)) <= 0.45f);
            }
        }
    }

    [Fact]
    public void Lavas_background_swells_with_the_middle()
    {
        var quiet = new RoomFigures();
        quiet.Lava(0f, 0f);
        var loud = new RoomFigures();
        loud.Lava(0f, 1f);

        Assert.True(loud.Back[2] > quiet.Back[2] * 1.4f);
    }

    [Fact]
    public void The_bass_swells_the_main_ribbon_and_the_air_flares_its_filament()
    {
        var quiet = new RoomFigures();
        quiet.Alchemy(0f, 0f, 0f);
        var bass = new RoomFigures();
        bass.Alchemy(1f, 0f, 0f);
        var air = new RoomFigures();
        air.Alchemy(0f, 0f, 1f);

        Assert.True(At(bass.RibbonC, 1, 1) > At(quiet.RibbonC, 1, 1));
        Assert.True(At(bass.RibbonB, 1, 2) > At(quiet.RibbonB, 1, 2));
        Assert.Equal(At(quiet.RibbonC, 2, 1), At(bass.RibbonC, 2, 1));
        Assert.True(At(air.RibbonC, 1, 2) > At(quiet.RibbonC, 1, 2) * 2);
        Assert.Equal(At(quiet.RibbonC, 0, 2), At(air.RibbonC, 0, 2));
    }

    [Fact]
    public void A_stored_name_reads_back_as_its_style()
    {
        foreach (var style in Enum.GetValues<RoomStyle>())
            Assert.Equal(style, RoomStyles.Named(style.ToString()));

        Assert.Equal(RoomStyle.Original, RoomStyles.Named(null));
        Assert.Equal(RoomStyle.Original, RoomStyles.Named(""));
        Assert.Equal(RoomStyle.Original, RoomStyles.Named("Plasma"));
        Assert.Equal(RoomStyle.Original, RoomStyles.Named("1"));
    }

    [Fact]
    public void The_row_steps_through_every_style_and_wraps()
    {
        var seen = new List<RoomStyle>();
        var style = RoomStyle.Original;
        for (int i = 0; i < Enum.GetValues<RoomStyle>().Length; i++)
        {
            seen.Add(style);
            style = RoomStyles.Next(style);
        }

        Assert.Equal(RoomStyle.Original, style);
        Assert.Equal(Enum.GetValues<RoomStyle>().OrderBy(s => s), seen.OrderBy(s => s));
    }

    /// <summary>
    /// Each animation's text declares the function the program calls and the arrays it fills, and nothing
    /// that would collide with the program around it: no entry point, and none of the uniforms the program
    /// declares itself.
    /// </summary>
    [Fact]
    public void Each_style_declares_its_room_and_its_arrays()
    {
        foreach (var style in new[] { RoomStyle.Yang, RoomStyle.Lava, RoomStyle.Alchemy })
        {
            var glsl = RoomStyleSource.For(style);

            Assert.Contains("vec3 styleRoom(vec2 p)", glsl, StringComparison.Ordinal);
            Assert.DoesNotContain("void main(", glsl, StringComparison.Ordinal);
            Assert.DoesNotContain("float rpmLobe(", glsl, StringComparison.Ordinal);
            foreach (var hosts in new[] { "uResolution", "uBass", "uMid", "uAir", "uGround", "uTint", "uGlow", "uShade" })
                Assert.DoesNotContain($"uniform float {hosts};", glsl, StringComparison.Ordinal);

            var arrays = RoomStyleSource.Arrays(style);
            Assert.NotNull(arrays);
            Assert.Equal(3, arrays.Length);
            foreach (var (name, _) in arrays)
                Assert.Contains($"uniform vec4 {name}", glsl, StringComparison.Ordinal);
        }

        Assert.Equal("", RoomStyleSource.For(RoomStyle.Original));
        Assert.Null(RoomStyleSource.Arrays(RoomStyle.Original));
    }

    /// <summary>The arrays the figures fill are as long as the shader's arrays, so an upload never reads past
    /// the end of a figure array or leaves part of a shader array unset.</summary>
    [Fact]
    public void The_figures_fill_the_arrays_exactly()
    {
        var f = new RoomFigures();
        var lengths = new Dictionary<RoomStyle, int[]>
        {
            [RoomStyle.Yang] = [f.Big.Length, f.Small.Length, f.Centre.Length],
            [RoomStyle.Lava] = [f.Blobs.Length, f.Pools.Length, f.Back.Length],
            [RoomStyle.Alchemy] = [f.RibbonA.Length, f.RibbonB.Length, f.RibbonC.Length],
        };

        foreach (var (style, floats) in lengths)
        {
            var arrays = RoomStyleSource.Arrays(style)!;
            for (int i = 0; i < 3; i++)
                Assert.Equal(arrays[i].Count * 4, floats[i]);
        }
    }

    /// <summary>ASCII only, comments included: GLSL ES accepts nothing above ASCII, even in a comment.</summary>
    [Fact]
    public void The_styles_are_ascii_throughout()
    {
        foreach (var style in Enum.GetValues<RoomStyle>())
            foreach (var c in RoomStyleSource.For(style))
                Assert.True(c <= 0x7F, $"non-ASCII '{c}' (U+{(int)c:X4}) in {style}'s source");
    }
}
