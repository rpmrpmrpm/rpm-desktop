using RPM.Core.Analysis;
using RPM.Core.Viz;

namespace RPM.Core.Tests;

/// <summary>
/// The tunnel's edit. What is checked is that the picture belongs to the RECORD: the count is the
/// track's own beats, a cut lands on one, and the same instant of the same record is always the
/// same shot however it was reached: played through, seeked to, or asked for twice.
/// </summary>
public class VizDirectorTests
{
    /// <summary>A grid of <paramref name="beats"/> beats at <paramref name="bpm"/>, with the first bar
    /// line at <paramref name="firstDownbeat"/>.</summary>
    private static TrackAnalysis Grid(double bpm = 120, int beats = 512, int firstDownbeat = 0,
        double start = 0, IReadOnlyList<double>? drops = null) => new()
    {
        FilePath = "/x.flac",
        Bpm = bpm,
        BeatTimes = Enumerable.Range(0, beats).Select(i => start + i * 60.0 / bpm).ToArray(),
        FirstDownbeat = firstDownbeat,
        BeatsPerBar = 4,
        DropSeconds = drops ?? [],
    };

    [Fact]
    public void The_count_is_the_records_own_beats()
    {
        var clock = BeatClock.For(Grid(bpm: 90), 4 * (60.0 / 90));

        Assert.True(clock.Measured);
        Assert.Equal(4, clock.Beats, 3);
    }

    /// <summary>Zero is the first bar line, not the first beat, so a bar always starts on a multiple
    /// of four and the run-up to it counts backwards.</summary>
    [Fact]
    public void The_count_is_zero_at_the_first_bar_line()
    {
        var analysis = Grid(bpm: 120, firstDownbeat: 3);

        Assert.Equal(-3, BeatClock.For(analysis, 0).Beats, 3);
        Assert.Equal(0, BeatClock.For(analysis, 3 * 0.5).Beats, 3);
    }

    /// <summary>A record whose beats drift is one that a straight multiplication of the tempo walks
    /// away from, so the tracked beats win over the tempo.</summary>
    [Fact]
    public void A_drifting_grid_is_followed_beat_by_beat()
    {
        // Beats that slow down: 0.5 s apart, then 0.6, then 0.7.
        var analysis = new TrackAnalysis
        {
            FilePath = "/x.flac",
            Bpm = 120,
            BeatTimes = [0, 0.5, 1.1, 1.8],
            BeatsPerBar = 4,
        };

        Assert.Equal(2.5, BeatClock.For(analysis, 1.1 + 0.35).Beats, 3);
    }

    /// <summary>With nothing analysed the picture still moves: the clock stands in, and says so.</summary>
    [Fact]
    public void An_unanalysed_record_falls_back_to_a_steady_clock()
    {
        var clock = BeatClock.For(null, 2);

        Assert.False(clock.Measured);
        Assert.Equal(2 * BeatClock.FreeBpm / 60, clock.Beats, 3);
    }

    [Fact]
    public void A_tempo_with_no_tracked_beats_is_still_a_count()
    {
        var clock = BeatClock.For(new TrackAnalysis { FilePath = "/x.flac", Bpm = 180 }, 2);

        Assert.True(clock.Measured);
        Assert.Equal(6, clock.Beats, 3);
    }

    /// <summary>The point of deriving the shot from the position: two ways of reaching the same
    /// instant give the same picture. Nothing here accumulates.</summary>
    [Fact]
    public void The_same_instant_is_always_the_same_shot()
    {
        var analysis = Grid();
        var at = BeatClock.For(analysis, 37.25);

        Assert.Equal(VizDirector.Shot(at, 120, -1), VizDirector.Shot(at, 120, -1));
    }

    [Fact]
    public void The_camera_holds_a_shot_for_eight_beats_and_then_cuts()
    {
        var early = VizDirector.Shot(new BeatClock(0.5, 4, true), 120, -1);
        var late = VizDirector.Shot(new BeatClock(7.9, 4, true), 120, -1);
        var next = VizDirector.Shot(new BeatClock(8.1, 4, true), 120, -1);

        Assert.Equal(early.Orbit, late.Orbit);
        Assert.NotEqual(early.Orbit, next.Orbit);
    }

    /// <summary>The run-up to the first bar line is a shot of its own, not a doubled first shot. A
    /// truncated count would hold cut zero from -8 to +8 and mirror the edit around it.</summary>
    [Fact]
    public void The_cut_before_the_first_bar_line_is_its_own()
    {
        var before = VizDirector.Shot(new BeatClock(-1, 4, true), 120, -1);
        var after = VizDirector.Shot(new BeatClock(1, 4, true), 120, -1);

        Assert.NotEqual(before.Orbit, after.Orbit);
    }

    [Fact]
    public void The_room_changes_every_eight_bars_and_slides_rather_than_cuts()
    {
        var last = VizDirector.Shot(new BeatClock(31.9, 4, true), 120, -1);   // end of section 0
        var first = VizDirector.Shot(new BeatClock(32.0, 4, true), 120, -1);  // start of section 1
        var settled = VizDirector.Shot(new BeatClock(34.0, 4, true), 120, -1);

        Assert.Equal(VizDirector.Room(0), last.Now);
        Assert.Equal(VizDirector.Room(1), first.Now);

        // The new room arrives over the first two beats: at the boundary the walls are still the old
        // room's, two beats later they are wholly the new one's.
        Assert.Equal(VizDirector.Room(0), first.Was);
        Assert.Equal(0f, first.Blend);
        Assert.Equal(1f, settled.Blend);
    }

    [Fact]
    public void A_room_is_the_same_room_every_time_it_comes_round()
    {
        Assert.Equal(VizDirector.Room(7), VizDirector.Room(7));
        Assert.NotEqual(VizDirector.Room(7), VizDirector.Room(8));
    }

    /// <summary>A fractional fold count leaves a seam down the length of the tunnel where the last wedge
    /// meets the first.</summary>
    [Fact]
    public void A_room_is_folded_a_whole_number_of_times()
    {
        for (int section = 0; section < 200; section++)
        {
            var room = VizDirector.Room(section);

            Assert.Equal(MathF.Round(room.Folds), room.Folds);
            Assert.InRange(room.Folds, 3, 9);
            Assert.InRange(room.Twist, 0.05f, 0.35f);
            Assert.InRange(room.Ring, 1.6f, 4.4f);
        }
    }

    /// <summary>A cut picks a new speed. If the distance flown were that speed times the whole count,
    /// the camera would jump to a different place at every cut, so the picture would lurch every two
    /// bars.</summary>
    [Fact]
    public void A_cut_changes_the_speed_without_moving_the_camera()
    {
        float before = VizDirector.Travelled(8 - 1e-4);
        float after = VizDirector.Travelled(8 + 1e-4);

        Assert.True(Math.Abs(after - before) < 0.01f,
            $"the camera jumped {after - before:F3} units across a cut");
    }

    [Fact]
    public void The_distance_flown_only_ever_goes_forward()
    {
        float last = VizDirector.Travelled(-20);
        for (double beats = -20; beats < 400; beats += 0.37)
        {
            float now = VizDirector.Travelled(beats);
            Assert.True(now >= last - 1e-4f, $"went backwards at beat {beats}");
            last = now;
        }
    }

    [Fact]
    public void Nothing_has_been_flown_at_the_first_bar_line()
    {
        Assert.Equal(0f, VizDirector.Travelled(0), 4);
    }

    /// <summary>Inside a shot the distance is simply that shot's speed, so a bar of a steady record is a
    /// bar's worth of tunnel.</summary>
    [Fact]
    public void Inside_a_shot_the_camera_flies_at_that_shots_speed()
    {
        var shot = VizDirector.Shot(new BeatClock(4, 4, true), 120, -1);

        Assert.Equal(shot.Advance * 4f, VizDirector.Travelled(4), 3);
    }

    [Fact]
    public void The_pulse_falls_from_the_beat_to_the_next_one()
    {
        Assert.Equal(1f, VizDirector.Shot(new BeatClock(4.0, 4, true), 120, -1).Pulse, 3);
        Assert.Equal(0f, VizDirector.Shot(new BeatClock(4.999, 4, true), 120, -1).Pulse, 2);
    }

    /// <summary>The beat count runs at a constant rate. The lattice's slice and its travel are both
    /// this count, so any easing here would show on screen as the picture lurching on the beat.</summary>
    [Fact]
    public void The_beat_count_advances_evenly_within_a_beat()
    {
        float on = VizDirector.Shot(new BeatClock(4.0, 4, true), 120, -1).Beats;
        float half = VizDirector.Shot(new BeatClock(4.5, 4, true), 120, -1).Beats;
        float next = VizDirector.Shot(new BeatClock(5.0, 4, true), 120, -1).Beats;

        Assert.Equal(4f, on, 3);
        Assert.Equal(5f, next, 3);
        Assert.Equal(half - on, next - half, 3);
    }

    [Fact]
    public void A_drop_opens_the_shot_out_and_fades_over_the_bars_after_it()
    {
        var at = new BeatClock(64, 4, true);

        var onDrop = VizDirector.Shot(at, 120, 0);
        var after = VizDirector.Shot(at, 120, 4);
        var past = VizDirector.Shot(at, 120, 9);

        Assert.Equal(1f, onDrop.Drop, 3);
        Assert.Equal(0.5f, after.Drop, 3);
        Assert.Equal(0f, past.Drop);

        // A drop steadies the aim, and that is all it does to the camera. It must not change the SPEED:
        // the distance flown is added up from the speeds of the shots already flown, so a speed that
        // fades in over eight beats would drag the camera backwards as it faded.
        Assert.True(onDrop.Sway < past.Sway, "a drop should steady the aim");
        Assert.Equal(past.Advance, onDrop.Advance);
    }

    [Fact]
    public void The_last_drop_before_now_is_the_one_that_counts()
    {
        double[] drops = [10, 40, 90];

        Assert.Equal(-1, VizDirector.BeatsSinceDrop(drops, 5, 120));      // none reached yet
        Assert.Equal(4, VizDirector.BeatsSinceDrop(drops, 12, 120), 3);   // 2 s after the first
        Assert.Equal(2, VizDirector.BeatsSinceDrop(drops, 41, 120), 3);   // 1 s after the second
        Assert.Equal(-1, VizDirector.BeatsSinceDrop([], 41, 120));
    }
}
