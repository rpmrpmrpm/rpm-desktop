using RPM.Core.Analysis;

namespace RPM.Core.Tests;

/// <summary>
/// The analysis passes, driven with signals built here rather than with audio files: a click track
/// has a tempo you can assert against, and a synthesised chord has a key.
/// </summary>
public class TrackAnalyzerTests
{
    private const int Rate = TrackAnalyzer.AnalysisRate;

    /// <summary>A click track at a known tempo: a short burst of noise on each beat, over a quiet
    /// tone so the signal is not pure silence between hits.</summary>
    private static float[] ClickTrack(double bpm, double seconds, int accentEvery = 4)
    {
        var samples = new float[(int)(Rate * seconds)];
        var rng = new Random(7);
        double period = 60.0 / bpm;

        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)(0.02 * Math.Sin(2 * Math.PI * 220 * i / Rate));

        int beat = 0;
        for (double at = 0; at < seconds; at += period, beat++)
        {
            int start = (int)(at * Rate);
            int length = Rate / 40;                             // 25 ms of click
            double gain = beat % accentEvery == 0 ? 1.0 : 0.55;  // the downbeat is the loud one
            for (int i = 0; i < length && start + i < samples.Length; i++)
            {
                double decay = 1 - (double)i / length;
                samples[start + i] += (float)(gain * decay * (rng.NextDouble() * 2 - 1));
            }
        }
        return samples;
    }

    /// <summary>A sustained triad, as three sine partials per note: enough for the chroma fold to
    /// find the three pitch classes and nothing else.</summary>
    private static float[] Chord(double seconds, params double[] rootHz)
    {
        var samples = new float[(int)(Rate * seconds)];
        foreach (var hz in rootHz)
        {
            for (int harmonic = 1; harmonic <= 3; harmonic++)
            {
                double f = hz * harmonic;
                if (f > Rate / 2.0) break;
                double amplitude = 0.25 / harmonic;
                for (int i = 0; i < samples.Length; i++)
                    samples[i] += (float)(amplitude * Math.Sin(2 * Math.PI * f * i / Rate));
            }
        }
        return samples;
    }

    [Theory]
    [InlineData(100)]
    [InlineData(120)]
    [InlineData(128)]
    [InlineData(140)]
    public void The_tempo_of_a_click_track_is_found_within_a_beat_per_minute(double bpm)
    {
        var onsets = OnsetEnvelope.Compute(ClickTrack(bpm, 30));
        var tempo = TempoEstimator.Estimate(onsets, OnsetEnvelope.FrameRate(Rate));

        Assert.True(tempo.IsKnown, "the click track should have a tempo");
        Assert.True(Math.Abs(tempo.Bpm - bpm) < 1.0, $"read {tempo.Bpm:F2}, wanted {bpm}");
    }

    [Theory]
    [InlineData(123)]
    [InlineData(137)]
    [InlineData(128.5)]
    public void The_tempo_is_close_enough_to_mix_on_not_merely_to_tap_along_to(double bpm)
    {
        // Within a beat per minute is fine for a readout but not for a mix: at 137, one BPM out is 0.7%,
        // which walks the two grids a fifth of a beat apart over an eight-bar changeover, so the two
        // kicks are heard separately. A tenth of a beat per minute is the standard here.
        var onsets = OnsetEnvelope.Compute(ClickTrack(bpm, 90));
        var tempo = TempoEstimator.Estimate(onsets, OnsetEnvelope.FrameRate(Rate));

        Assert.True(Math.Abs(tempo.Bpm - bpm) < 0.1, $"read {tempo.Bpm:F3}, wanted {bpm}");
    }

    [Fact]
    public void A_tempo_read_slightly_wrong_is_pinned_back_onto_the_grid()
    {
        // The autocorrelation gives a good guess a fraction of a percent out. Laying one rigid grid over
        // the whole track turns that into a number worth mixing on, because a grid that has to hit every
        // beat of a three-minute record has no room to be wrong.
        var onsets = OnsetEnvelope.Compute(ClickTrack(137, 120));
        double frameRate = OnsetEnvelope.FrameRate(Rate);

        foreach (double guess in (double[])[136.2, 137.8])
        {
            double pinned = TempoEstimator.PinDown(onsets, frameRate, guess);
            Assert.True(Math.Abs(pinned - 137) < 0.05,
                $"a guess of {guess} was pinned to {pinned:F3}, not to 137");
        }
    }

    [Fact]
    public void A_record_whose_offbeat_hits_as_hard_as_its_downbeat_is_still_pinned()
    {
        // Which is most of them, and a phase-blind measurement of the beat rate cannot survive it: the
        // offbeat arrives half a turn from the downbeat, so the two almost cancel and whichever tempo is
        // left is noise. The pin has to be read off how the track folds onto one beat, where the two add.
        var onsets = OnsetEnvelope.Compute(ClickTrack(150, 120, accentEvery: 1));
        double frameRate = OnsetEnvelope.FrameRate(Rate);

        foreach (double guess in (double[])[148.5, 151.5])
        {
            double pinned = TempoEstimator.PinDown(onsets, frameRate, guess);
            Assert.True(Math.Abs(pinned - 150) < 0.05,
                $"a guess of {guess} was pinned to {pinned:F3}, not to 150");
        }
    }

    [Fact]
    public void The_beat_grid_is_evenly_spaced_and_says_the_tempo_it_was_measured_at()
    {
        // Everything that lines a mix up counts beats by index (a bar line is every fourth entry), so a
        // grid with a beat dropped out of it rotates the bar phase for the rest of the record. The mix
        // also works out its tempo pull from the BPM while landing the drop on a beat from the grid. If
        // those two disagree, the records meet on the bar line and walk apart afterwards.
        var analysis = TrackAnalyzer.Analyze(
            "/library/clicks.wav", ClickTrack(150, 120), Rate, new StandInBeatModel(150));

        Assert.True(analysis.BeatTimes.Count > 200, $"only {analysis.BeatTimes.Count} beats tracked");

        double beat = 60.0 / analysis.Bpm;
        for (int i = 1; i < analysis.BeatTimes.Count; i++)
            Assert.Equal(beat, analysis.BeatTimes[i] - analysis.BeatTimes[i - 1], 6);

        // So the tempo and the grid are the same statement, to the last decimal.
        double spacing = (analysis.BeatTimes[^1] - analysis.BeatTimes[0]) / (analysis.BeatTimes.Count - 1);
        Assert.Equal(analysis.Bpm, 60.0 / spacing, 6);
        Assert.Equal(150, analysis.Bpm, 1);

        // And the grid reaches both ends of the track, because a changeover needs bar lines under the
        // intro of the record coming in and under the outro of the one going out.
        Assert.True(analysis.BeatTimes[0] < beat, $"the grid starts {analysis.BeatTimes[0]:F2}s in");
        Assert.True(analysis.DurationSeconds - analysis.BeatTimes[^1] < 2 * beat,
            $"the grid stops {analysis.DurationSeconds - analysis.BeatTimes[^1]:F2}s before the end");
    }

    [Fact]
    public void A_tempo_outside_the_reporting_window_is_folded_into_it()
    {
        // 60 BPM is the same record as 120 counted twice; the DJ works in the folded range.
        Assert.Equal(120, TempoEstimator.Fold(60), 3);
        Assert.Equal(100, TempoEstimator.Fold(200), 3);
        Assert.InRange(TempoEstimator.Fold(65), TempoEstimator.MinBpm, TempoEstimator.MaxBpm);
    }

    [Fact]
    public void What_a_grid_folds_only_comes_down()
    {
        // What the stored tempo goes through. A fast record is counted at the library's octave. A slow
        // one is left where it was read, because doubling it never helped over 1697 annotated tracks.
        Assert.Equal(100, TempoEstimator.FoldDown(200), 3);
        Assert.Equal(112.5, TempoEstimator.FoldDown(450), 3);
        Assert.Equal(60, TempoEstimator.FoldDown(60), 3);
        Assert.Equal(42, TempoEstimator.FoldDown(42), 3);
        Assert.Equal(0, TempoEstimator.FoldDown(-1), 3);

        // The two agree wherever the tempo is already in the window, which is most records.
        foreach (double bpm in new[] { 70.0, 96.0, 124.0, 174.0, 180.0 })
            Assert.Equal(TempoEstimator.Fold(bpm), TempoEstimator.FoldDown(bpm), 9);
    }

    [Fact]
    public void Two_records_are_compared_at_the_same_octave_however_each_was_counted()
    {
        // The reporting window spans a factor of two and a half, so a 148 BPM record can come back as
        // 74 and still be inside it. Against a record at 150 that reads as a factor of two (a pull no
        // fader reaches) when the two are in fact the same tempo.
        Assert.Equal(148.02, TempoEstimator.MatchLevel(74.01, 150.02), 2);
        Assert.Equal(74.01, TempoEstimator.MatchLevel(148.02, 75.01), 2);

        // A record that is genuinely a different tempo is left where it is: 100 against 128 is a real
        // gap, not a counting one, and moving it an octave would only make it wider.
        Assert.Equal(100, TempoEstimator.MatchLevel(100, 128), 3);
        Assert.Equal(128, TempoEstimator.MatchLevel(128, 128), 3);
        Assert.Equal(0, TempoEstimator.MatchLevel(0, 128), 6);
    }

    [Fact]
    public void A_bar_is_measured_at_the_tempo_the_room_counts()
    {
        // Bars are what a changeover is built out of, and a reading an octave low makes every one of
        // them twice as long: an eight-bar mix would run sixteen. Anything already danceable is left
        // exactly as it is.
        Assert.Equal(148.02, TempoEstimator.DanceLevel(74.01), 2);
        Assert.Equal(150.02, TempoEstimator.DanceLevel(150.02), 2);
        Assert.Equal(128, TempoEstimator.DanceLevel(128), 3);
        Assert.Equal(90, TempoEstimator.DanceLevel(90), 3);
        Assert.Equal(100, TempoEstimator.DanceLevel(200), 3);
    }

    [Fact]
    public void Silence_has_no_tempo_and_no_key()
    {
        var silence = new float[Rate * 5];

        var onsets = OnsetEnvelope.Compute(silence);
        Assert.False(TempoEstimator.Estimate(onsets, OnsetEnvelope.FrameRate(Rate)).IsKnown);

        var chroma = ChromaAnalyzer.Compute(silence, Rate);
        Assert.False(KeyDetector.Detect(chroma).Key.IsKnown);
    }

    [Fact]
    public void The_beat_grid_lands_on_the_clicks_and_holds_the_spacing()
    {
        const double bpm = 128;
        var onsets = OnsetEnvelope.Compute(ClickTrack(bpm, 20));
        double frameRate = OnsetEnvelope.FrameRate(Rate);
        var beats = BeatTracker.Track(onsets, bpm, frameRate);

        Assert.True(beats.Length > 30, $"a 20 s track at {bpm} BPM should give ~42 beats, got {beats.Length}");

        // Every beat should sit within 15 ms of a real click. A beat-matched mix depends on this being
        // tight, so the tolerance is well inside one analysis frame (23 ms).
        // Frame 0 is skipped: a transient inside the very first analysis window has nothing before it
        // to be a rise against, so where in that window it sat cannot be recovered. Every beat the
        // analysis can see is held to a third of a frame.
        double period = 60.0 / bpm;
        foreach (var frame in BeatTracker.Refine(onsets, beats).Where(f => f > 0))
        {
            double at = OnsetEnvelope.FrameTime(frame, Rate);
            double offBy = Math.Abs(at - Math.Round(at / period) * period);
            Assert.True(offBy < 0.015, $"beat at {at:F3}s is {offBy * 1000:F0} ms off the click");
        }
    }

    [Fact]
    public void The_grid_rides_through_a_bar_of_silence_and_stays_in_step()
    {
        // The point of tracking with dynamic programming rather than peak-picking: a gap in the music
        // must not shift the grid.
        const double bpm = 120;
        var samples = ClickTrack(bpm, 24);
        int gapStart = (int)(Rate * 10), gapEnd = (int)(Rate * 12);
        Array.Clear(samples, gapStart, gapEnd - gapStart);

        double frameRate = OnsetEnvelope.FrameRate(Rate);
        var beats = BeatTracker.Track(OnsetEnvelope.Compute(samples), bpm, frameRate);

        var afterTheGap = BeatTracker.Refine(OnsetEnvelope.Compute(samples), beats)
            .Where(f => OnsetEnvelope.FrameTime(f, Rate) > 13).ToList();
        Assert.NotEmpty(afterTheGap);
        double period = 60.0 / bpm;
        foreach (var frame in afterTheGap)
        {
            double at = OnsetEnvelope.FrameTime(frame, Rate);
            double offBy = Math.Abs(at - Math.Round(at / period) * period);
            Assert.True(offBy < 0.04, $"after the silence the grid is {offBy * 1000:F0} ms out at {at:F2}s");
        }
    }

    /// <summary>How far into a bar a downbeat reading falls, in seconds. 0 when it is right.</summary>
    private static double OffTheBar(ReadOnlySpan<float> onsets, int[] beats, int firstDownbeat, double barSeconds)
    {
        double at = OnsetEnvelope.FrameTime(BeatTracker.Refine(onsets, beats)[firstDownbeat], Rate);
        double into = at % barSeconds;
        return Math.Min(into, barSeconds - into);
    }

    private static DownbeatDetector.Result Downbeat(float[] samples, double bpm, out float[] onsets, out int[] beats)
    {
        onsets = OnsetEnvelope.Compute(samples);
        double frameRate = OnsetEnvelope.FrameRate(Rate);
        beats = BeatTracker.Track(onsets, bpm, frameRate);
        return DownbeatDetector.Find(
            onsets, beats, frameRate,
            ChromaAnalyzer.ComputeFrames(samples, Rate), ChromaAnalyzer.FrameRate(Rate));
    }

    [Fact]
    public void The_downbeat_falls_on_the_accented_click()
    {
        var found = Downbeat(ClickTrack(120, 24, accentEvery: 4), 120, out var onsets, out var beats);

        Assert.InRange(found.FirstDownbeat, 0, 3);

        // The accents are on whole bars from t=0, so every downbeat should land on a multiple of four
        // beats, which is two seconds at 120 BPM.
        Assert.True(OffTheBar(onsets, beats, found.FirstDownbeat, 2.0) < 0.1, "the downbeat is not on a bar line");
    }

    /// <summary>
    /// Four-on-the-floor: the same kick on every single beat, with the chord changing once a bar. No
    /// beat hits harder than any other, so the only thing in the signal that says where the bar line
    /// is, is the harmony. This is the case across most of a dance record.
    /// </summary>
    private static float[] FourOnTheFloor(double bpm, double seconds)
    {
        var samples = new float[(int)(Rate * seconds)];
        double period = 60.0 / bpm;
        double[][] chords = [[261.63, 329.63, 392.00], [220.00, 261.63, 329.63]];   // C major, A minor

        // The chord, changing every four beats on the bar.
        for (int i = 0; i < samples.Length; i++)
        {
            int bar = (int)(i / (double)Rate / (period * 4));
            foreach (var hz in chords[bar % chords.Length])
                samples[i] += (float)(0.10 * Math.Sin(2 * Math.PI * hz * i / Rate));
        }

        // An identical kick on every beat: 55 Hz, below the chromagram's range, decaying fast.
        for (double at = 0; at < seconds; at += period)
        {
            int start = (int)(at * Rate);
            int length = Rate / 12;
            for (int i = 0; i < length && start + i < samples.Length; i++)
            {
                double decay = Math.Exp(-8.0 * i / length);
                samples[start + i] += (float)(0.9 * decay * Math.Sin(2 * Math.PI * 55 * i / Rate));
            }
        }
        return samples;
    }

    [Fact]
    public void The_downbeat_is_found_from_where_the_chord_changes_when_every_beat_hits_the_same()
    {
        var found = Downbeat(FourOnTheFloor(120, 32), 120, out var onsets, out var beats);

        Assert.True(found.Confidence >= TrackAnalysis.MinDownbeatConfidence,
            $"the bar line should be readable off the chord changes, confidence was {found.Confidence:F2}");
        Assert.True(OffTheBar(onsets, beats, found.FirstDownbeat, 2.0) < 0.1,
            "the downbeat did not land where the chord changes");
    }

    /// <summary>
    /// The chord changes on <em>every</em> beat, cycling over two bars: C Am C Am | F G F G. Looking
    /// one beat back finds a change everywhere, so it cannot tell one beat of the bar from another.
    /// This is the record that reads as a half-bar and leaves the one and the three tied. Only
    /// comparing a whole bar against the bar before it separates them.
    /// </summary>
    private static float[] TwoBarCycle(double bpm, double seconds)
    {
        var samples = new float[(int)(Rate * seconds)];
        double period = 60.0 / bpm;
        double[][] perBeat =
        [
            [261.63, 329.63, 392.00],   // C
            [220.00, 261.63, 329.63],   // Am
            [261.63, 329.63, 392.00],   // C
            [220.00, 261.63, 329.63],   // Am
            [174.61, 220.00, 261.63],   // F
            [196.00, 246.94, 293.66],   // G
            [174.61, 220.00, 261.63],   // F
            [196.00, 246.94, 293.66],   // G
        ];

        for (int i = 0; i < samples.Length; i++)
        {
            int beat = (int)(i / (double)Rate / period);
            foreach (var hz in perBeat[beat % perBeat.Length])
                samples[i] += (float)(0.10 * Math.Sin(2 * Math.PI * hz * i / Rate));
        }

        for (double at = 0; at < seconds; at += period)
        {
            int start = (int)(at * Rate);
            int length = Rate / 12;
            for (int i = 0; i < length && start + i < samples.Length; i++)
            {
                double decay = Math.Exp(-8.0 * i / length);
                samples[start + i] += (float)(0.9 * decay * Math.Sin(2 * Math.PI * 55 * i / Rate));
            }
        }
        return samples;
    }

    [Fact]
    public void The_one_is_told_from_the_three_by_comparing_whole_bars()
    {
        var found = Downbeat(TwoBarCycle(120, 48), 120, out var onsets, out var beats);

        Assert.True(found.Confidence >= TrackAnalysis.MinDownbeatConfidence,
            $"the bar should be readable by comparing bars, confidence was {found.Confidence:F2}");
        Assert.True(OffTheBar(onsets, beats, found.FirstDownbeat, 2.0) < 0.1,
            "the downbeat landed on the three rather than the one");
    }

    [Fact]
    public void A_record_with_nothing_marking_the_bar_says_so_rather_than_pretending()
    {
        // Every beat identical and the harmony never moves: all four phases are the same, so there is no
        // honest answer. The reading says so, but the mix still aligns to the bar it picked. The
        // alternative, aligning to whichever beat comes next, lands on a random phase of the bar. Both
        // are wrong three times in four.
        var flat = ClickTrack(120, 24, accentEvery: 1);
        // The model hears the pulse (there is one) and finds no bar in it. A reading with no bar lines
        // leaves the question to the harmony, and the harmony never moves.
        var analysis = TrackAnalyzer.Analyze(
            "/library/flat.wav", flat, Rate, new StandInBeatModel(120, findsBars: false));

        Assert.True(analysis.DownbeatConfidence < TrackAnalysis.MinDownbeatConfidence,
            $"a featureless loop should not claim a bar line, confidence was {analysis.DownbeatConfidence:F2}");

        double bar = 4 * 60.0 / analysis.Bpm;
        double next = analysis.NextDownbeatAfter(10.0) ?? 0;
        Assert.True(next - 10.0 <= bar + 0.05, $"the next line to align to is {next - 10:F2}s away, a bar is {bar:F2}s");
        double after = analysis.NextDownbeatAfter(next + 0.01) ?? 0;
        Assert.Equal(bar, after - next, 1);   // a bar apart, not a beat
    }

    [Fact]
    public void A_major_chord_is_read_as_its_own_key()
    {
        // C major triad: C4 261.6, E4 329.6, G4 392.0.
        var chroma = ChromaAnalyzer.Compute(Chord(8, 261.63, 329.63, 392.00), Rate);

        // The three notes of the chord should dominate the fold.
        Assert.True(chroma[0] > 0.5, $"C should be strong, was {chroma[0]:F2}");
        Assert.True(chroma[4] > 0.3, $"E should be present, was {chroma[4]:F2}");
        Assert.True(chroma[7] > 0.3, $"G should be present, was {chroma[7]:F2}");

        var key = KeyDetector.Detect(chroma);
        Assert.True(key.Key.IsKnown);
        Assert.Equal(0, key.Key.Tonic);          // C
        Assert.False(key.Key.IsMinor);
    }

    [Fact]
    public void A_minor_chord_is_read_as_minor()
    {
        // A minor triad: A3 220, C4 261.6, E4 329.6.
        var key = KeyDetector.Detect(ChromaAnalyzer.Compute(Chord(8, 220.00, 261.63, 329.63), Rate)).Key;

        Assert.True(key.IsKnown);
        Assert.True(key.IsMinor, $"read {key}");
        Assert.Equal(9, key.Tonic);              // A
        Assert.Equal("8A", key.Camelot);
    }

    [Fact]
    public void The_cue_points_skip_a_quiet_intro_and_stop_before_a_fade_out()
    {
        // Quiet for 8 s, loud for 40 s, quiet again for the last 12.
        var levels = new float[120];             // 60 s at half a second a step
        for (int i = 0; i < levels.Length; i++)
        {
            double at = i * EnergyProfile.StepSeconds;
            levels[i] = at < 8 ? 0.02f : at < 48 ? 0.30f : 0.02f;
        }

        var cues = CuePointFinder.Find(levels, 60, [], 0);

        Assert.InRange(cues.IntroEndSeconds, 7, 9);
        Assert.InRange(cues.OutroStartSeconds, 47, 49);
    }

    [Fact]
    public void An_intro_of_speech_over_clapping_is_not_mistaken_for_the_body_of_the_track()
    {
        // The case this is for: a record that opens with talking and hand claps. It is as loud as the
        // song from the first second, so a full-band meter calls it the body and the next record gets
        // mixed straight into the talking. What is missing is the kick, and that is what the cue finder
        // looks at.
        const double introSeconds = 12, total = 60;
        var samples = new float[(int)(Rate * total)];
        var rng = new Random(11);

        for (int i = 0; i < samples.Length; i++)
        {
            double at = (double)i / Rate;
            if (at < introSeconds)
            {
                // Speech-band tone plus claps: loud, busy, nothing under 200 Hz.
                samples[i] = (float)(0.22 * Math.Sin(2 * Math.PI * 700 * i / Rate));
                if ((int)(at * 2) % 2 == 0 && i % (Rate / 2) < Rate / 60)
                    samples[i] += (float)(0.5 * (rng.NextDouble() * 2 - 1));
            }
            else
            {
                // The song: a four-on-the-floor kick under the same mid content.
                samples[i] = (float)(0.18 * Math.Sin(2 * Math.PI * 700 * i / Rate));
                double intoBeat = at % 0.5;
                if (intoBeat < 0.08)
                    samples[i] += (float)(0.8 * Math.Exp(-intoBeat * 40) * Math.Sin(2 * Math.PI * 55 * i / Rate));
            }
        }

        var levels = EnergyProfile.Compute(samples, Rate);
        var bass = EnergyProfile.ComputeLowBand(samples, Rate);

        // Full band alone cannot tell the two sections apart. That is the trap.
        var naive = CuePointFinder.Find(levels, total, [], 0);
        Assert.True(naive.IntroEndSeconds < 4, $"the full-band reading should be fooled, gave {naive.IntroEndSeconds:F1}s");

        // With the bottom end included, the intro ends where the kick starts.
        var cues = CuePointFinder.Find(levels, total, [], 0, beatsPerBar: 4, bassLevels: bass);
        Assert.InRange(cues.IntroEndSeconds, introSeconds - 2, introSeconds + 2);
    }

    [Fact]
    public void A_record_with_no_bottom_end_still_gets_its_cues_from_the_full_band()
    {
        // Solo piano, a cappella: there is no kick to find, so falling back is right, rather than
        // reading cues off whatever noise is down there.
        const double total = 40;
        var samples = new float[(int)(Rate * total)];
        for (int i = 0; i < samples.Length; i++)
        {
            double at = (double)i / Rate;
            double gain = at < 8 ? 0.02 : 0.25;          // quiet opening, then the piece proper
            samples[i] = (float)(gain * Math.Sin(2 * Math.PI * 880 * i / Rate));
        }

        var levels = EnergyProfile.Compute(samples, Rate);
        var bass = EnergyProfile.ComputeLowBand(samples, Rate);
        var cues = CuePointFinder.Find(levels, total, [], 0, beatsPerBar: 4, bassLevels: bass);

        Assert.InRange(cues.IntroEndSeconds, 6, 10);
    }

    [Fact]
    public void A_track_that_never_settles_is_played_whole_rather_than_half_skipped()
    {
        // Rising all the way through: there is no body to find, so no cue may eat into it.
        var levels = new float[60];
        for (int i = 0; i < levels.Length; i++)
            levels[i] = 0.01f + i * 0.005f;

        var cues = CuePointFinder.Find(levels, 30, [], 0);

        Assert.True(cues.IntroEndSeconds <= 30 * 0.35, "the intro cue must not eat a third of the track");
        Assert.True(cues.OutroStartSeconds > cues.IntroEndSeconds);
    }

    [Fact]
    public void The_analyzer_reads_a_click_track_end_to_end()
    {
        var analysis = TrackAnalyzer.Analyze(
            "/tmp/click.wav", ClickTrack(126, 40), Rate, new StandInBeatModel(126));

        Assert.True(Math.Abs(analysis.Bpm - 126) < 1.5, $"bpm was {analysis.Bpm:F2}");
        Assert.True(analysis.CanBeatMatch,
            $"bpm {analysis.Bpm:F2} confidence {analysis.BpmConfidence:F3} beats {analysis.BeatTimes.Count}");
        Assert.True(analysis.BpmConfidence > 0.5, $"a click track should read as confident, was {analysis.BpmConfidence:F2}");
        Assert.True(analysis.BeatTimes.Count > 60);
        Assert.Equal(4, analysis.BeatsPerBar);
        Assert.InRange(analysis.DurationSeconds, 39.5, 40.5);
        Assert.True(analysis.BarSeconds > 0);
    }

    [Fact]
    public void A_file_too_short_to_analyse_comes_back_playable_but_unmatched()
    {
        var analysis = TrackAnalyzer.Analyze("/tmp/blip.wav", new float[Rate / 2], Rate);

        Assert.False(analysis.CanBeatMatch);
        Assert.Equal(0, analysis.Bpm);
        // Still playable end to end: no cue points were invented for it.
        Assert.Equal(analysis.DurationSeconds, analysis.OutroStartSeconds, 3);
    }

    // ── the model path, driven by a stand-in ──

    /// <summary>
    /// A beat model that reports what the test tells it to.
    ///
    /// <para>The real one is eighty megabytes of weights behind an ONNX runtime and lives in
    /// RPM.Desktop, so tests here cannot reach it. <see cref="IBeatModel"/> is an interface in Core so
    /// that the analyser can be driven without one. A stand-in that says "I hear this track at sixty
    /// four" is enough to ask the questions that matter: what does the analyser do with a reading
    /// that disagrees with the DSP, and what reaches the mix afterwards.</para>
    ///
    /// <para>Beats are rounded to the model's own twenty millisecond frame, because the real one
    /// cannot report anything else, and exact times would test a precision the analyser never
    /// receives.</para>
    /// </summary>
    /// <param name="findsBars">False for a model that hears the pulse and no bar in it, which leaves
    /// the bar to the harmony reading.</param>
    private sealed class StandInBeatModel(
        double bpm, int beatsPerBar = 4, int firstDownbeat = 0, double offsetSeconds = 0,
        bool findsBars = true) : IBeatModel
    {
        public BeatReading? Read(ReadOnlySpan<float> mono, int sampleRate, CancellationToken ct = default)
        {
            double duration = (double)mono.Length / sampleRate;
            double period = 60.0 / bpm;
            var beats = new List<double>();
            var downbeats = new List<double>();

            for (int i = 0; ; i++)
            {
                double at = offsetSeconds + i * period;
                if (at >= duration)
                    break;

                double framed = Math.Round(at * MelSpectrogram.FrameRate) / MelSpectrogram.FrameRate;
                beats.Add(framed);
                if (findsBars && i >= firstDownbeat && (i - firstDownbeat) % beatsPerBar == 0)
                    downbeats.Add(framed);
            }

            return new BeatReading { BeatTimes = beats, DownbeatTimes = downbeats };
        }
    }

    [Fact]
    public void With_no_model_there_is_no_tempo_and_no_grid()
    {
        // The rule the whole path is built on: the tempo is the model's or there is none. A click track
        // is the easiest thing the DSP will ever be handed, and its tempo is still not used. The DSP
        // cannot tell the readings it gets right from the ones it gets wrong: on real records it reads
        // the dotted quarter as the beat and reports a confidence of 1.00 while doing it.
        var analysis = TrackAnalyzer.Analyze("/library/clicks.wav", ClickTrack(126, 40), Rate);

        Assert.Equal(0, analysis.Bpm);
        Assert.Equal(0, analysis.BpmConfidence);
        Assert.Empty(analysis.BeatTimes);
        Assert.False(analysis.CanBeatMatch);
        Assert.False(analysis.Modelled);
    }

    [Fact]
    public void A_record_with_no_tempo_is_still_worth_analysing()
    {
        // And the other half: everything the DSP is good at still runs. Losing the tempo must not cost
        // the record its cues, its key, its loudness or its shape. It is played and faded like any
        // other, just not matched.
        var analysis = TrackAnalyzer.Analyze("/library/clicks.wav", ClickTrack(126, 40), Rate);

        Assert.True(analysis.EnergyCurve.Count > 0, "no energy arc");
        Assert.True(analysis.BassCurve.Count > 0, "no bottom end");
        Assert.True(analysis.OutroStartSeconds > analysis.IntroEndSeconds,
            $"cues came back as {analysis.IntroEndSeconds:F1}..{analysis.OutroStartSeconds:F1}");
        Assert.True(analysis.AudioEndSeconds > analysis.AudioStartSeconds, "no audio edges");
    }

    [Fact]
    public void A_key_the_model_named_does_not_stamp_the_row_as_modelled()
    {
        // Modelled is what sends a row back to be read again once a beat model can do better. A key
        // model answering while the beat model declined would freeze a record with no tempo into the
        // library, marked as though the good pass had already been made.
        var analysis = TrackAnalyzer.Analyze(
            "/library/keyed.wav", ClickTrack(126, 40), Rate,
            beatModel: null, keyModel: new StandInKeyModel());

        Assert.False(analysis.Modelled, "only the beat model settles this");
        Assert.True(TrackAnalysis.WantsModelUpgrade(analysis.Version, modelAvailable: true));
    }

    private sealed class StandInKeyModel : IKeyModel
    {
        public KeyReading? Read(ReadOnlySpan<float> mono, int sampleRate, CancellationToken ct = default) =>
            new(new MusicalKey(0, true), 0.9);
    }

    [Fact]
    public void A_model_counting_the_record_at_half_speed_is_stored_at_half_speed()
    {
        // The app stores what was read. 64 is a legal way to count a 128 record, and doubling it into
        // the window on sight corrected nothing over 1697 annotated tracks (it fired 49 times and turned
        // 37 ballads into records twice as fast as they are). Every place that compares two tempi
        // already works in octaves, so a 64 next to a 128 is not a problem: TempoEstimator.MatchLevel
        // pairs them and TempoEstimator.DanceLevel counts the bars.
        var analysis = TrackAnalyzer.Analyze(
            "/library/half-counted.wav", ClickTrack(128, 40), Rate, new StandInBeatModel(64));

        Assert.True(analysis.Modelled, "the model read this track and should be recorded as having done so");
        Assert.True(Math.Abs(analysis.Bpm - 64) < 2, $"bpm was {analysis.Bpm:F2}, expected about 64");
        Assert.Equal(128, TempoEstimator.MatchLevel(analysis.Bpm, 128), 1);
    }

    [Fact]
    public void A_model_counting_the_record_at_double_speed_is_brought_back_the_same_way()
    {
        var analysis = TrackAnalyzer.Analyze(
            "/library/double-counted.wav", ClickTrack(128, 40), Rate, new StandInBeatModel(256));

        Assert.InRange(analysis.Bpm, TempoEstimator.MinBpm, TempoEstimator.MaxBpm);
        Assert.True(Math.Abs(analysis.Bpm - 128) < 2, $"bpm was {analysis.Bpm:F2}, expected about 128");
    }

    [Fact]
    public void The_bar_length_comes_from_the_model_because_the_dsp_assumes_four()
    {
        // DownbeatDetector is handed beatsPerBar and defaults it to four; it never reads the bar length
        // off the track. A record counted in three makes every fade in the mix the wrong length, and the
        // model's bar lines are the only evidence of it.
        var analysis = TrackAnalyzer.Analyze(
            "/library/waltz.wav", ClickTrack(129, 40, accentEvery: 3), Rate,
            new StandInBeatModel(129, beatsPerBar: 3));

        Assert.Equal(3, analysis.BeatsPerBar);
    }

    [Fact]
    public void The_grid_keeps_the_placement_the_dsp_found_rather_than_the_models_framed_one()
    {
        // The model's beats land on a 20 ms grid; the DSP's are refined against the onsets themselves.
        // A changeover is allowed 15 ms of drift in total, so which of the two the grid is built from
        // matters.
        var analysis = TrackAnalyzer.Analyze(
            "/library/placement.wav", ClickTrack(128, 40), Rate, new StandInBeatModel(128));

        double period = 60.0 / 128;
        foreach (double beat in analysis.BeatTimes.Take(40))
        {
            double off = Math.Abs(beat - Math.Round(beat / period) * period);
            Assert.True(off < 0.015, $"beat at {beat:F4}s sits {off * 1000:F1} ms off the true grid");
        }
    }

    // ── which stored rows a model is worth re-reading ──

    [Fact]
    public void A_current_dsp_row_is_worth_redoing_once_there_is_a_model()
    {
        // The row nothing else would touch: current, so it is served from the cache for ever, and made
        // by the DSP, so it is the worse of the two readings the app can produce.
        Assert.True(TrackAnalysis.WantsModelUpgrade(TrackAnalysis.AnalyzerVersion, modelAvailable: true));
    }

    [Fact]
    public void A_row_a_model_already_made_is_left_alone()
    {
        Assert.False(TrackAnalysis.WantsModelUpgrade(TrackAnalysis.ModelledVersion, modelAvailable: true));
    }

    [Fact]
    public void Nothing_is_redone_on_a_machine_with_no_weights_to_read()
    {
        // Redoing it there spends a whole decode arriving at the same answer.
        Assert.False(TrackAnalysis.WantsModelUpgrade(TrackAnalysis.AnalyzerVersion, modelAvailable: false));
    }

    [Fact]
    public void An_out_of_date_row_is_left_to_the_ordinary_path()
    {
        // It is re-analysed anyway because the generation changed; claiming it here would queue the same
        // decode twice.
        Assert.False(TrackAnalysis.WantsModelUpgrade(TrackAnalysis.AnalyzerVersion - 1, modelAvailable: true));
        Assert.False(TrackAnalysis.WantsModelUpgrade(
            TrackAnalysis.ModelledOffset + TrackAnalysis.AnalyzerVersion - 1, modelAvailable: true));
    }

    [Fact]
    public void A_row_from_a_later_generation_is_not_dragged_backwards()
    {
        // A newer build wrote it. It is modelled or it is not, and this build leaves it alone.
        Assert.False(TrackAnalysis.WantsModelUpgrade(
            TrackAnalysis.ModelledOffset + TrackAnalysis.AnalyzerVersion + 1, modelAvailable: true));
    }
}
