using RPM.Core.Analysis;
using RPM.Core.Audio;
using RPM.Core.Mixing;

namespace RPM.Core.Tests;

/// <summary>
/// The rule the decks have to keep: while both records are audible, they run at ONE tempo. A plan
/// that says two decks are locked has no value if the decks are not locked.
///
/// <para>Driven through the real transport, not asserted from a plan. The fault this guards against
/// sits between the two: the planner works out a pull and decides that the pair will hold, but the
/// pitch ride can decline to apply it.</para>
/// </summary>
public class TempoLockTests
{
    private const int Rate = 44100, Channels = 2;

    private static TrackAnalysis Analysis(string path, double bpm, double duration, double outro)
    {
        var beats = new List<double>();
        for (double at = 0; at < duration; at += 60.0 / bpm)
            beats.Add(at);

        return new TrackAnalysis
        {
            FilePath = path,
            DurationSeconds = duration,
            Bpm = bpm,
            BpmConfidence = 0.9,
            BeatTimes = beats,
            FirstDownbeat = 0,
            BeatsPerBar = 4,
            Key = new MusicalKey(9, true),
            KeyConfidence = 0.8,
            EnergyCurve = [0.3f, 0.3f],
            IntroEndSeconds = 0,
            OutroStartSeconds = outro,
        };
    }

    /// <summary>
    /// The same, on a record that does not hold one tempo: the beats are laid down against a tempo
    /// that ramps by <paramref name="bend"/> across the whole record.
    ///
    /// <para>This is a normal case. <see cref="ModelBeatGrid"/> bends a grid to follow the record
    /// wherever the bend is worth keeping, and <see cref="TrackAnalysis.BpmAt"/> exists because
    /// <see cref="TrackAnalysis.Bpm"/> is not the whole story on those records. A tenth of a percent
    /// across four minutes is well inside what the analyser produces.</para>
    /// </summary>
    private static TrackAnalysis Bent(string path, double bpm, double duration, double outro, double bend)
    {
        var beats = new List<double>();
        for (double at = 0; at < duration;)
        {
            beats.Add(at);
            at += 60.0 / (bpm * (1 + bend * at / duration));
        }

        return Analysis(path, bpm, duration, outro) with { BeatTimes = beats };
    }

    /// <summary>
    /// A grid like a real one: the tempo wanders a little across the record and every beat is a few
    /// milliseconds from where a perfect ruler would put it.
    ///
    /// <para>Neither is a defect. <see cref="ModelBeatGrid"/> bends its line to follow a record that
    /// does not hold one tempo, and a tracker's beats are where the transients are, which is not a
    /// straight line even on a machine-made record. Every grid in the library looks like this. The
    /// perfect grids in the other tests do not.</para>
    /// </summary>
    private static TrackAnalysis Real(string path, double bpm, double duration, double outro, int seed)
    {
        var random = new Random(seed);
        var beats = new List<double>();

        // A slow wander of a few tenths of a percent, like a record played by hand rather than
        // programmed, and like what the model keeps when the bend is worth keeping.
        for (double at = 0; at < duration;)
        {
            beats.Add(at + (random.NextDouble() - 0.5) * 0.004);
            double here = bpm * (1 + 0.003 * Math.Sin(2 * Math.PI * at / 180) + 0.002 * at / duration);
            at += 60.0 / here;
        }

        beats.Sort();
        return Analysis(path, bpm, duration, outro) with { BeatTimes = beats };
    }

    /// <summary>
    /// A grid that wanders the way the ones that break a changeover do: the tempo moves by
    /// <paramref name="wander"/> either side of the record's own, over and over, with a period short
    /// enough that a single changeover sits inside one swing of it.
    ///
    /// <para>Half a percent at a period of fifty seconds gives a local tempo about one percent
    /// different at the end of a blend from the start. A real record in the library behaves like
    /// this, and no single pull can follow it. The pull has to move with the grid: see
    /// <see cref="TempoStep"/>.</para>
    /// </summary>
    private static TrackAnalysis Wanders(
        string path, double bpm, double duration, double outro, double wander, double period)
    {
        var beats = new List<double>();
        for (double at = 0; at < duration;)
        {
            beats.Add(at);
            at += 60.0 / (bpm * (1 + wander * Math.Sin(2 * Math.PI * at / period)));
        }

        return Analysis(path, bpm, duration, outro) with { BeatTimes = beats };
    }

    private static MixTrack Track(string name, double bpm, double duration, double outro)
    {
        string path = $"/library/{name}.flac";
        return new MixTrack
        {
            Source = path,
            Path = path,
            Title = name,
            TaggedDuration = duration,
            Analysis = Analysis(path, bpm, duration, outro),
        };
    }

    /// <summary>A steady value for a fixed length. The length is divided by the pull, so a deck that
    /// was stretched runs for a different number of output frames than one that was not. From outside,
    /// this is the only way a test can tell the two apart.</summary>
    private sealed class ToneSource(double seconds, double tempoRatio) : IPcmSource
    {
        private long _left = (long)(seconds * Rate / Math.Max(0.001, tempoRatio));

        public string Label => "tone";

        public int Read(float[] buffer, int frames)
        {
            if (_left <= 0)
                return 0;

            int give = (int)Math.Min(frames, _left);
            for (int i = 0; i < give * Channels; i++)
                buffer[i] = 0.5f;
            _left -= give;
            return give;
        }

        public void Dispose() => _left = 0;
    }

    private static Dj Booth(IEnumerable<MixTrack> tracks, MixOverrides? asked = null) =>
        new(new DjCrate(tracks), Rate, Channels,
            options => new ToneSource(300 - options.StartSeconds, options.TempoRatio),
            cueRunner: work => { work(); return Task.CompletedTask; },
            chance: () => 1)
        { Overrides = asked };

    /// <summary>
    /// Two records a percent apart. They meet at half a percent each, which is under
    /// <c>Dj.RideStep</c>. The pitch ride must still make this small move, so that the playing record
    /// and the record coming in share a tempo.
    /// </summary>
    [Theory]
    [InlineData(128, 129.3)]        // about a percent: both pulls land under the ride's step
    [InlineData(128, 128.2)]        // a sixth of a percent: smaller still, but not zero
    [InlineData(124, 131)]          // several percent: big enough that the ride always runs
    [InlineData(128, 128)]          // a pair that needs no pull at all
    public void Both_decks_run_at_one_tempo_for_every_block_where_both_are_audible(
        double first, double second)
    {
        var tracks = new[]
        {
            Track("one", first, duration: 120, outro: 70),
            Track("two", second, duration: 120, outro: 70),
        };

        using var dj = Booth(tracks);
        var block = new float[512 * Channels];

        double worst = 0;
        int heardTogether = 0;

        for (int i = 0; i < 40_000; i++)
        {
            if (dj.Read(block, 512) <= 0)
                break;

            // Only where the changeover is running. Outside one there is nothing on the other deck to agree
            // with, and the record that has landed is allowed back up to its own speed on purpose.
            if (dj.ChangeoverProgress <= 0 || dj.CuedTrack is null)
                continue;

            heardTogether++;

            double live = dj.PlayingBpm, cued = dj.CuedBpm;
            Assert.True(live > 0 && cued > 0, $"a deck reported no tempo at all: {live} and {cued}");
            worst = Math.Max(worst, Math.Abs(live - cued) / live);
        }

        Assert.True(heardTogether > 0, "the set never ran a changeover, so nothing was tested");
        Assert.True(
            worst <= TransitionPlanner.TempoLockTolerance,
            $"the two decks sat {worst:P4} apart at their worst while both were audible");
    }

    // ── the phase ────────────────────────────────────────────────────────────

    /// <summary>
    /// How far apart the two records' grids are, in seconds of the mix, at this instant.
    ///
    /// <para>Wrapped into half a beat either side, because two grids a whole beat apart are two grids
    /// in time. The flam the listener hears is the distance to the NEAREST beat of the other record,
    /// not to the beat with the same number.</para>
    /// </summary>
    private static double Apart(Dj dj)
    {
        double live = Toward(dj.LiveTrack, dj.LiveNeedleSeconds, dj.LiveTempoRatio);
        double cued = Toward(dj.CuedTrack, dj.CuedNeedleSeconds, dj.CuedTempoRatio);
        double beat = 60.0 / dj.PlayingBpm;

        double gap = cued - live;
        gap -= Math.Round(gap / beat) * beat;
        return gap;

        // Where this deck's next beat falls, in seconds of the mix rather than of the record. The deck
        // is played pulled, so a second of the record is not a second of the mix.
        static double Toward(MixTrack? track, double at, double ratio)
        {
            var beats = track!.Analysis!.BeatTimes;
            foreach (double b in beats)
                if (b >= at)
                    return (b - at) / ratio;
            return 0;
        }
    }

    /// <summary>
    /// The rule itself: while both records are audible their beats land together. Not their tempos,
    /// their beats.
    ///
    /// <para>The tests above assert that the two decks run at one RATE, which is what the pitch faders
    /// control and what the planner checks. A rate is not an alignment. Two grids at identical tempo,
    /// started a fortieth of a second apart, stay a fortieth of a second apart for the whole of a
    /// sixteen-bar blend. Every tempo test in this file passes while the listener hears two kicks.</para>
    ///
    /// <para>Held to <see cref="TransitionPlanner.MaxDriftSeconds"/>, which is where two kicks stop
    /// being one.</para>
    /// </summary>
    [Theory]
    [InlineData(128, 128, 0, 0)]              // a pair that needs no pull at all
    [InlineData(128, 129.3, 0, 0)]            // about a percent, both pulls under the ride's step
    [InlineData(124, 131, 0, 0)]              // several percent
    [InlineData(128, 128, 0.004, 0)]          // a record that does not hold one tempo
    [InlineData(128, 129.3, 0.004, -0.003)]   // both records bending, in opposite directions
    public void The_two_grids_land_on_the_same_beat_for_every_block_where_both_are_audible(
        double first, double second, double bendOne, double bendTwo) =>
        AssertLocked(first, second, bendOne, bendTwo, block: 512, asked: null);

    /// <summary>
    /// The same rule, at the block size the app runs at and through the changeovers a person picks.
    ///
    /// <para>The earlier tests use 512-frame blocks and the planner's default. The app differs in two
    /// ways: <c>AudioEngine.FramesPerBuffer</c> is 2205 (fifty milliseconds, four times as coarse), and
    /// the changeovers the workbench offers are the phrase ones. Anything that rounds to a block, or
    /// that only the phrase path reaches, would be missed.</para>
    /// </summary>
    [Theory]
    [InlineData(2205, null)]
    [InlineData(2205, TransitionStyle.BeatMatched)]
    [InlineData(2205, TransitionStyle.BassSwap)]
    [InlineData(512, TransitionStyle.BassSwap)]
    [InlineData(4096, TransitionStyle.BeatMatched)]
    public void The_two_grids_land_on_the_same_beat_at_the_block_size_the_app_runs_at(
        int block, TransitionStyle? style)
    {
        var asked = style is { } wanted
            ? new MixOverrides { Style = wanted, Policy = MixPolicy.Default with { PhraseBlend = true } }
            : null;

        AssertLocked(124, 131, 0, 0, block, asked);
        AssertLocked(128, 128.2, 0, 0, block, asked);
    }

    /// <summary>
    /// The rule on grids like the ones in the library, over a changeover as long as the ones the
    /// workbench plans.
    ///
    /// <para>The earlier tests use a perfect-ruler grid and whatever changeover length the planner
    /// picked for a two-minute record. The app differs: a real grid wanders, and a phrase blend runs
    /// for twenty-four bars, which at 121 BPM is nearly a minute. A tempo lock worked out from one
    /// reading at each end has a long time to be slightly wrong in.</para>
    /// </summary>
    [Theory]
    [InlineData(121, 121, 24)]
    [InlineData(121, 124, 24)]
    [InlineData(128, 128, 32)]
    [InlineData(128, 126, 16)]
    public void The_two_grids_stay_on_the_same_beat_over_a_long_changeover_on_real_grids(
        double first, double second, int bars)
    {
        var one = Track("one", first, duration: 420, outro: 380);
        var two = Track("two", second, duration: 420, outro: 380);

        one = one with { Analysis = Real(one.Path, first, 420, 380, seed: 1) };
        two = two with { Analysis = Real(two.Path, second, 420, 380, seed: 2) };

        var asked = new MixOverrides
        {
            Style = TransitionStyle.BeatMatched,
            FadeBars = bars,
        };

        Measure(one, two, block: 2205, asked, out double worst, out double worstAt, out int seen);

        Assert.True(seen > 0, "the set never ran a changeover, so nothing was tested");
        Assert.True(
            worst <= TransitionPlanner.MaxDriftSeconds,
            $"{first} into {second} over {bars} bars: the two grids sat {worst * 1000:F1} ms apart at " +
            $"their worst, {worstAt:P0} through the changeover, against the " +
            $"{TransitionPlanner.MaxDriftSeconds * 1000:F0} ms two kicks stop being one at");
    }

    /// <summary>
    /// The rule on a record that cannot be held by one pull: a grid that wanders a percent inside the
    /// length of the changeover. Driven through the transport, so the assertion is that the DECKS
    /// stayed together, not that a plan said they would.
    ///
    /// <para>One pull meets the grid at both ends but is a tenth of a second out in the middle. What
    /// holds it is the pull moving with the grid through the changeover, like a sync button whose
    /// moves are worked out in advance: see <see cref="TempoStep"/>.</para>
    /// </summary>
    [Theory]
    [InlineData(121, 121, 24)]
    [InlineData(121, 124, 24)]
    [InlineData(128, 126, 16)]
    public void The_two_grids_stay_together_over_a_record_whose_tempo_will_not_hold_still(
        double first, double second, int bars)
    {
        var one = Track("one", first, duration: 420, outro: 380);
        var two = Track("two", second, duration: 420, outro: 380);

        // The record coming in is the one that wanders. This is the case that matters: the record
        // playing is already turning and is never pulled to suit anything.
        two = two with { Analysis = Wanders(two.Path, second, 420, 380, wander: 0.005, period: 50) };

        var asked = new MixOverrides { Style = TransitionStyle.BeatMatched, FadeBars = bars };

        // Held together by following the grid, not by giving the changeover up. Shortening it would also
        // keep the decks in line, but that would answer a different question.
        var plan = TransitionPlanner.Plan(one, first, two, asked);
        Assert.Equal(bars, plan.FadeBars);
        Assert.True(plan.TempoSteps.Count > 1, "one pull was kept on a record that will not hold one");

        Measure(one, two, block: 2205, asked, out double worst, out double worstAt, out int seen);

        Assert.True(seen > 0, "the set never ran a changeover, so nothing was tested");
        Assert.True(
            worst <= TransitionPlanner.MaxDriftSeconds,
            $"{first} into a wandering {second} over {bars} bars: the decks sat {worst * 1000:F1} ms " +
            $"apart at their worst, {worstAt:P0} through the changeover");
    }

    /// <summary>
    /// The changeover corrects itself when the decks do not carry out the plan.
    ///
    /// <para>The failure staged here is silent: a <see cref="Deck.SetTempo"/> that cannot open a
    /// decoder at the new speed keeps the old speed rather than break the record, and reports
    /// nothing. Everything downstream then believes a pull that is not being played.</para>
    ///
    /// <para>The phase lock answers it: it reads the two grids while both records are turning and
    /// bends the record coming in until they land together again. So the assertion is not that the
    /// mix never goes out of time (it does, that is what is staged) but that it comes back and is
    /// together by the end.</para>
    /// </summary>
    [Fact]
    public void A_speed_change_the_deck_could_not_make_is_pulled_back_into_line()
    {
        var one = Track("one", 121, duration: 420, outro: 380);
        var two = Track("two", 121, duration: 420, outro: 380);
        two = two with { Analysis = Wanders(two.Path, 121, 420, 380, wander: 0.005, period: 50) };

        // Where the changeover intends to change the record's speed. This is the moment to refuse: a
        // refusal that lands nowhere near a speed change stages nothing.
        var plan = TransitionPlanner.Plan(
            one, 121, two, new MixOverrides { Style = TransitionStyle.BeatMatched, FadeBars = 24 });
        Assert.True(plan.TempoSteps.Count > 1, "this pair was expected to need the pull to move");
        double changesAt = plan.TempoSteps[1].AtSeconds / plan.FadeSeconds;

        // Refused for a stretch either side of it: any decoder asked for during the window comes back
        // empty, which is what a deck that cannot reopen looks like from inside.
        bool refusing = false;
        using var dj = new Dj(
            new DjCrate([one, two]), Rate, Channels,
            options => new ToneSource(refusing ? 0 : 420 - options.StartSeconds, options.TempoRatio),
            cueRunner: work => { work(); return Task.CompletedTask; },
            chance: () => 1)
        { Overrides = new MixOverrides { Style = TransitionStyle.BeatMatched, FadeBars = 24 } };

        var buffer = new float[2205 * Channels];
        double was = -1, worstLate = 0;
        int seenLate = 0;

        for (int i = 0; i < 100_000; i++)
        {
            if (dj.Read(buffer, 2205) <= 0)
                break;

            if (!dj.IsChangingOver || dj.LiveTrack?.Analysis is null || dj.CuedTrack?.Analysis is null ||
                dj.PlayingBpm <= 0)
            {
                was = -1;
                continue;
            }

            double now = dj.CuedNeedleSeconds;
            bool turning = was >= 0 && now > was + 1e-9;
            was = now;
            if (!turning)
                continue;

            // The window in which the deck refuses everything: over the changeover's own speed change, and
            // ending well before the end so there is time to recover.
            double through = dj.ChangeoverProgress;
            refusing = through > changesAt - 0.02 && through < changesAt + 0.05;

            // Measured after the window has closed and the lock has had a few seconds. Before that, the
            // fault is still being staged and the lock is still noticing it. What is asserted is that the
            // changeover comes back together.
            if (through > changesAt + 0.25)
            {
                seenLate++;
                worstLate = Math.Max(worstLate, Math.Abs(Apart(dj)));
            }
        }

        Assert.True(seenLate > 0, "the set never reached the back half of a changeover");
        Assert.True(
            worstLate <= TransitionPlanner.MaxDriftSeconds,
            $"after a speed change the deck refused, the two records were still {worstLate * 1000:F1} ms " +
            $"apart in the back half of the changeover");
    }

    private static void AssertLocked(
        double first, double second, double bendOne, double bendTwo, int block, MixOverrides? asked)
    {
        var one = Track("one", first, duration: 120, outro: 70);
        var two = Track("two", second, duration: 120, outro: 70);

        if (bendOne != 0)
            one = one with { Analysis = Bent(one.Path, first, 120, 70, bendOne) };
        if (bendTwo != 0)
            two = two with { Analysis = Bent(two.Path, second, 120, 70, bendTwo) };

        Measure(one, two, block, asked, out double worst, out double worstAt, out int heardTogether);

        Assert.True(heardTogether > 0, "the set never ran a changeover, so nothing was tested");
        Assert.True(
            worst <= TransitionPlanner.MaxDriftSeconds,
            $"the two grids sat {worst * 1000:F1} ms apart at their worst, {worstAt:P0} through the " +
            $"changeover, against the {TransitionPlanner.MaxDriftSeconds * 1000:F0} ms two kicks stop " +
            $"being one at ({first} and {second} BPM, {block}-frame blocks, {asked?.Style?.ToString() ?? "planner's pick"})");
    }

    /// <summary>Runs one changeover and reports the worst distance between the two grids while both
    /// records were making sound.</summary>
    private static void Measure(
        MixTrack one, MixTrack two, int block, MixOverrides? asked,
        out double worst, out double worstAt, out int heardTogether)
    {
        using var dj = Booth([one, two], asked);
        var buffer = new float[block * Channels];

        worst = 0;
        worstAt = 0;
        heardTogether = 0;
        double was = -1;

        for (int i = 0; i < 100_000; i++)
        {
            if (dj.Read(buffer, block) <= 0)
                break;

            if (!dj.IsChangingOver || !dj.IsDeckPlaying(DjDeck.Cued) ||
                dj.LiveTrack?.Analysis is null || dj.CuedTrack?.Analysis is null || dj.PlayingBpm <= 0)
            {
                was = -1;
                continue;
            }

            // Both actually making sound, which is not the same as both started. Deck.Start takes the number
            // of frames to wait before the first sample, and IsRunning is true for the whole wait. So at the
            // start of every changeover the record coming in is "playing" while it sits on its cue point
            // emitting nothing. A deck that has not moved since the last block has no beat to be out of line
            // with.
            double now = dj.CuedNeedleSeconds;
            bool turning = was >= 0 && now > was + 1e-9;
            was = now;
            if (!turning)
                continue;

            heardTogether++;

            double apart = Math.Abs(Apart(dj));
            if (apart > worst)
            {
                worst = apart;
                worstAt = dj.ChangeoverProgress;
            }
        }
    }

    /// <summary>
    /// The pitch ride makes a move even when it is too small to be worth riding. Its step is how
    /// finely to travel, never whether to arrive.
    /// </summary>
    [Fact]
    public void A_pull_smaller_than_the_riders_step_is_still_made()
    {
        var tracks = new[]
        {
            Track("one", 128, duration: 120, outro: 70),
            Track("two", 129.3, duration: 120, outro: 70),
        };

        using var dj = Booth(tracks);
        var block = new float[512 * Channels];

        TransitionPlan? planned = null;
        for (int i = 0; i < 40_000; i++)
        {
            if (dj.Read(block, 512) <= 0)
                break;

            planned ??= dj.PendingPlan;

            if (dj.ChangeoverProgress <= 0)
                continue;

            Assert.NotNull(planned);

            // The pull is real and smaller than the ride's step: exactly the band the ride must not skip.
            double pull = Math.Abs(planned.OutgoingTempoRatio - 1);
            Assert.InRange(pull, FfmpegPcmSource.UnstretchedRatio, 0.008);

            // And the deck runs at it, not at its own speed.
            Assert.Equal(planned.OutgoingTempoRatio, dj.LiveTempoRatio, 6);
            return;
        }

        Assert.Fail("the set never reached a changeover");
    }

    // ── the scrubber ─────────────────────────────────────────────────────────

    /// <summary>
    /// Moving the needle on the record playing, anywhere before the changeover starts, leaves the
    /// changeover alone. A scrub must not send the cued record back to the crate, however small the
    /// move and however far from the mix, and must not drop the tempo pull with it.
    /// </summary>
    [Theory]
    [InlineData(0)]                 // right back to the top
    [InlineData(20)]                // a nudge
    [InlineData(60)]                // most of the way to the mix
    public void A_scrub_before_the_changeover_leaves_it_alone(double to)
    {
        using var dj = Booth([
            Track("one", 128, duration: 120, outro: 70),
            Track("two", 128, duration: 120, outro: 70),
        ]);

        var block = new float[512 * Channels];

        // Run until a changeover has been planned and its record cued.
        TransitionPlan? held = null;
        for (int i = 0; i < 40_000 && held is null; i++)
        {
            if (dj.Read(block, 512) <= 0)
                break;
            held = dj.PendingPlan;
        }

        Assert.NotNull(held);
        Assert.True(to < held.StartAtSeconds, "the test is meaningless past the changeover's start");

        var cued = dj.CuedTrack;
        Assert.NotNull(cued);

        Assert.True(dj.SeekTo(to));

        Assert.Same(held, dj.PendingPlan);
        Assert.Same(cued, dj.CuedTrack);
    }

    /// <summary>
    /// Past the point the plan was to start from, the plan is void (the second it names is behind
    /// the needle), so the record goes back to the crate and the choice is made again.
    /// </summary>
    [Fact]
    public void A_scrub_past_the_start_of_the_changeover_gives_it_up()
    {
        using var dj = Booth([
            Track("one", 128, duration: 120, outro: 70),
            Track("two", 128, duration: 120, outro: 70),
        ]);

        var block = new float[512 * Channels];

        TransitionPlan? held = null;
        for (int i = 0; i < 40_000 && held is null; i++)
        {
            if (dj.Read(block, 512) <= 0)
                break;
            held = dj.PendingPlan;
        }

        Assert.NotNull(held);
        Assert.True(dj.SeekTo(held.StartAtSeconds + 1));
        Assert.Null(dj.PendingPlan);
    }

    /// <summary>
    /// The tempo pull is kept where the plan is kept. A record left part way onto another record's
    /// tempo, with no ride to finish it, gives two decks that will not agree when they meet.
    /// </summary>
    [Fact]
    public void A_scrub_that_keeps_the_changeover_keeps_the_pull_that_goes_with_it()
    {
        using var dj = Booth([
            Track("one", 124, duration: 120, outro: 70),
            Track("two", 131, duration: 120, outro: 70),
        ]);

        var block = new float[512 * Channels];

        TransitionPlan? held = null;
        for (int i = 0; i < 40_000 && held is null; i++)
        {
            if (dj.Read(block, 512) <= 0)
                break;
            held = dj.PendingPlan;
        }

        Assert.NotNull(held);
        Assert.NotEqual(1, held.OutgoingTempoRatio);

        // Let some of the ride happen, but stop well short of the changeover itself: a seek is refused
        // for the whole of one, so a test that waits too long tests nothing.
        for (int i = 0; i < 200 && dj.ChangeoverProgress <= 0; i++)
            if (dj.Read(block, 512) <= 0)
                break;

        double pulled = dj.LiveTempoRatio;
        Assert.True(dj.SeekTo(10), "the seek was refused");
        Assert.Same(held, dj.PendingPlan);

        // Whatever the deck was pulled to, it is still pulled to it: the seek did not put the pitch
        // fader back to the record's own speed and leave the plan believing otherwise.
        Assert.Equal(pulled, dj.LiveTempoRatio, 9);
    }

    /// <summary>
    /// The changeover does not begin until the record playing has arrived at the tempo it hands over
    /// at. Starting half way through the pull would put the new record on a grid that is still moving
    /// under it.
    /// </summary>
    [Fact]
    public void A_changeover_never_starts_while_the_tempo_is_still_moving()
    {
        var tracks = new[]
        {
            Track("one", 124, duration: 120, outro: 70),
            Track("two", 131, duration: 120, outro: 70),
        };

        using var dj = Booth(tracks);
        var block = new float[512 * Channels];

        while (dj.Read(block, 512) > 0)
            Assert.False(
                dj.IsMatchingIn && dj.ChangeoverProgress > 0,
                "the changeover began while the record playing was still being pulled");
    }
}
