using RPM.Core.Audio;
using RPM.Core.Analysis;
using RPM.Core.Mixing;

namespace RPM.Core.Tests;

/// <summary>
/// The volume arc: the level curve each record follows through a changeover. It can be drawn by hand
/// in the lab, and until it is edited it is exactly the app's equal-power crossfade.
/// </summary>
public class FadeArcTests
{
    // ── the default shape is the crossfader's shape ───────────────────────────

    [Fact]
    public void The_equal_power_arc_is_the_crossfader_redrawn()
    {
        var arc = FadeArc.EqualPower();

        for (int i = 0; i <= 100; i++)
        {
            double t = i / 100.0;
            var drawn = arc.LevelsAt(t);
            var classic = Crossfader.At(t);

            // Very close: the arc is the same curve sampled onto handles, and a straight line between
            // handles this close is smaller than an audible fader step.
            Assert.Equal(classic.Outgoing, drawn.Outgoing, tolerance: 0.01);
            Assert.Equal(classic.Incoming, drawn.Incoming, tolerance: 0.01);
        }
    }

    [Fact]
    public void The_default_bass_swap_is_where_it_has_always_been()
    {
        var arc = FadeArc.EqualPower();

        for (int i = 0; i <= 100; i++)
        {
            double t = i / 100.0;
            var drawn = arc.BassDbAt(t);
            var classic = Crossfader.BassDb(t);
            Assert.Equal(classic.Outgoing, drawn.Outgoing, 1);
            Assert.Equal(classic.Incoming, drawn.Incoming, 1);
        }
    }

    [Fact]
    public void The_balance_point_of_the_equal_power_arc_is_the_crossfaders()
    {
        Assert.Equal(Crossfader.Balance, FadeArc.EqualPower().BalanceT, 2);
    }

    // ── a drawn arc is followed ───────────────────────────────────────────────

    [Fact]
    public void A_drawn_arc_is_followed_between_its_points()
    {
        var arc = new FadeArc
        {
            Outgoing = [new ArcPoint(0, 1), new ArcPoint(1, 0)],
            Incoming = [new ArcPoint(0, 0), new ArcPoint(1, 1)],
        };

        Assert.Equal(0.5, arc.LevelsAt(0.5).Outgoing, 6);
        Assert.Equal(0.5, arc.LevelsAt(0.5).Incoming, 6);
        Assert.Equal(0.75, arc.LevelsAt(0.25).Outgoing, 6);
    }

    [Fact]
    public void The_ends_hold_outside_the_drawn_points()
    {
        var arc = new FadeArc
        {
            Outgoing = [new ArcPoint(0.2, 0.9), new ArcPoint(0.8, 0.1)],
            Incoming = [new ArcPoint(0.2, 0.1), new ArcPoint(0.8, 0.9)],
        };

        Assert.Equal(0.9, arc.LevelsAt(0).Outgoing, 6);
        Assert.Equal(0.9, arc.LevelsAt(0.1).Outgoing, 6);
        Assert.Equal(0.1, arc.LevelsAt(1).Outgoing, 6);
        Assert.Equal(0.9, arc.LevelsAt(0.95).Incoming, 6);
    }

    [Fact]
    public void The_bass_swap_happens_where_it_was_drawn()
    {
        var arc = FadeArc.EqualPower() with { BassSwapStart = 0.6, BassSwapEnd = 0.8 };

        // Before the window: the outgoing record still has the bass, the incoming one is killed.
        Assert.Equal(0, arc.BassDbAt(0.5).Outgoing, 3);
        Assert.Equal(DeckEq.KillDb, arc.BassDbAt(0.5).Incoming, 3);

        // The middle of the window: half swapped.
        Assert.Equal(DeckEq.KillDb / 2, arc.BassDbAt(0.7).Outgoing, 3);

        // After it: fully swapped.
        Assert.Equal(DeckEq.KillDb, arc.BassDbAt(0.9).Outgoing, 3);
        Assert.Equal(0, arc.BassDbAt(0.9).Incoming, 3);
    }

    // ── editing ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_moved_point_cannot_cross_its_neighbours()
    {
        var arc = FadeArc.EqualPower();
        double before = arc.Outgoing[3].T;
        double left = arc.Outgoing[2].T;
        double right = arc.Outgoing[4].T;

        var moved = arc.MovePoint(ArcSide.Outgoing, 3, t: 0.0, level: 0.5);
        Assert.True(moved.Outgoing[3].T > left, "a point dragged left stops at its neighbour");

        moved = arc.MovePoint(ArcSide.Outgoing, 3, t: 1.0, level: 0.5);
        Assert.True(moved.Outgoing[3].T < right, "a point dragged right stops at its neighbour");

        // And the ends do not move in time at all: the arc always covers the whole fade.
        moved = arc.MovePoint(ArcSide.Outgoing, 0, t: 0.4, level: 0.8);
        Assert.Equal(arc.Outgoing[0].T, moved.Outgoing[0].T, 6);
        Assert.Equal(0.8, moved.Outgoing[0].Level, 6);

        // Levels are levels: nothing above full, nothing below silence.
        moved = arc.MovePoint(ArcSide.Outgoing, 3, before, level: 1.7);
        Assert.Equal(1.0, moved.Outgoing[3].Level, 6);
    }

    // ── through the planner ───────────────────────────────────────────────────

    [Fact]
    public void An_arc_asked_for_rides_the_plan()
    {
        var outgoing = Track(Analysis(bpm: 128, duration: 300, outro: 280));
        var incoming = Track(Analysis(bpm: 126, duration: 300));
        var arc = FadeArc.EqualPower() with { BassSwapStart = 0.5, BassSwapEnd = 0.7 };

        var plan = TransitionPlanner.Plan(outgoing, 128, incoming, new MixOverrides { Arc = arc });

        Assert.Same(arc, plan.Arc);
        Assert.Contains(plan.Trace, line => line.Contains("arc", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_roll_out_keeps_its_own_shape()
    {
        var outgoing = Track(Analysis(bpm: 128, duration: 300, outro: 280));
        var incoming = Track(Analysis(bpm: 128, duration: 300));

        var plan = TransitionPlanner.SkipTo(
            outgoing, 128, incoming, startAt: 100, new MixOverrides { Arc = FadeArc.EqualPower() });

        Assert.Null(plan.Arc);
        Assert.Contains(plan.Trace, line => line.Contains("arc", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_arc_alone_still_counts_as_an_override()
    {
        Assert.False(new MixOverrides { Arc = FadeArc.EqualPower() }.IsEmpty);
        Assert.True(new MixOverrides().IsEmpty);
    }

    // ── on the decks ──────────────────────────────────────────────────────────

    [Fact]
    public void A_drawn_arc_is_what_the_faders_do()
    {
        // An arc that removes the outgoing record early: gone by a fifth of the fade. The classic
        // curve would still be at 0.95 there, so the difference is clear in the output.
        var early = new FadeArc
        {
            Outgoing = [new ArcPoint(0, 1), new ArcPoint(0.2, 0), new ArcPoint(1, 0)],
            Incoming = [new ArcPoint(0, 0), new ArcPoint(0.2, 1), new ArcPoint(1, 1)],
        };

        var tracks = new[]
        {
            Track(Analysis(bpm: 128, duration: 120, outro: 100), "one"),
            Track(Analysis(bpm: 128, duration: 120, outro: 100), "two"),
        };

        using var dj = new Dj(
            new DjCrate(tracks), Rate, Channels,
            options => new ToneSource(options.Source == "one" ? 1f : 0f, 120 - options.StartSeconds, options.TempoRatio),
            cueRunner: work => { work(); return Task.CompletedTask; },
            chance: () => 1);

        dj.Overrides = new MixOverrides { Arc = early };

        var buffer = new float[Block * Channels];
        int guard = 0;

        // Play until the changeover is well inside the crossing, past the first fifth where the arc
        // fades out. Half way is safely past it.
        while ((!dj.IsChangingOver || dj.ChangeoverProgress < 0.5) && guard++ < 100_000)
            dj.Read(buffer, Block);

        Assert.True(dj.IsChangingOver, "the set never reached its changeover");

        // The outgoing record is the only signal (the incoming one's marker is silence), so what is
        // in the block is its fader. The classic curve is near 0.5 here; the drawn arc is at zero.
        dj.Read(buffer, Block);
        Assert.True(Math.Abs(buffer[0]) < 0.05,
            $"outgoing record still at {buffer[0]:F3} half way through a fade drawn to remove it at 0.2");
    }

    private const int Rate = 44100, Channels = 2, Block = 2205;

    /// <summary>A steady value for a fixed length, so a mixed block reads as the fader itself.</summary>
    private sealed class ToneSource(float value, double seconds, double tempoRatio) : IPcmSource
    {
        private long _left = (long)(seconds * Rate / tempoRatio);

        public string Label => "tone";

        public int Read(float[] buffer, int frames)
        {
            if (_left <= 0) return 0;
            int give = (int)Math.Min(frames, _left);
            for (int i = 0; i < give * Channels; i++)
                buffer[i] = value;
            _left -= give;
            return give;
        }

        public void Dispose() => _left = 0;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static TrackAnalysis Analysis(double bpm, double duration, double? outro = null)
    {
        var beats = new List<double>();
        if (bpm > 0)
            for (double at = 0; at < duration; at += 60.0 / bpm)
                beats.Add(at);

        return new TrackAnalysis
        {
            FilePath = "x.flac",
            DurationSeconds = duration,
            Bpm = bpm,
            BpmConfidence = bpm > 0 ? 0.9 : 0,
            BeatTimes = beats,
            FirstDownbeat = 0,
            BeatsPerBar = 4,
            EnergyCurve = [0.3f, 0.3f],
            Intensity = 0.5,
            IntroEndSeconds = 8,
            OutroStartSeconds = outro ?? Math.Max(1, duration - 20),
        };
    }

    private static MixTrack Track(TrackAnalysis analysis, string? path = null) => new()
    {
        Source = path ?? analysis.FilePath,
        Path = path ?? analysis.FilePath,
        Title = path ?? "t",
        TaggedDuration = analysis.DurationSeconds,
        Analysis = path is null ? analysis : analysis with { FilePath = path },
    };
}
