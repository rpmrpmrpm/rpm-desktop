using RPM.Core.Analysis;
using RPM.Core.Audio;
using RPM.Core.Mixing;

namespace RPM.Core.Tests;

/// <summary>
/// The DJ, its decks and the way it chooses records. Driven with a generated tone per deck instead
/// of files, so a whole set can be played in a test at a few thousand times real speed.
/// </summary>
public class DjTests
{
    private const int Rate = 44100, Channels = 2;

    /// <summary>A deck source that returns a steady full-scale signal for a fixed length. Its value
    /// is the record's marker, so a block of mix can be read as "how much of A, how much of B",
    /// which is what a crossfade test needs.</summary>
    private sealed class ToneSource : IPcmSource
    {
        private readonly float _value;
        private long _left;

        public ToneSource(string label, float value, double seconds, double tempoRatio)
        {
            Label = label;
            _value = value;
            // A stretched deck plays the same record in less (or more) real time, the same as the
            // decoder's atempo.
            _left = (long)(seconds * Rate / tempoRatio);
        }

        public string Label { get; }

        public int Read(float[] buffer, int frames)
        {
            if (_left <= 0) return 0;
            int give = (int)Math.Min(frames, _left);
            for (int i = 0; i < give * Channels; i++)
                buffer[i] = _value;
            _left -= give;
            return give;
        }

        public void Dispose() => _left = 0;
    }

    /// <summary>An energy curve that is quiet for the first half of the track and loud for the rest:
    /// a record with a long breakdown, so "how loud is this" has a different answer depending on the
    /// play position.</summary>
    private static float[] QuietThenLoud(double duration)
    {
        int steps = (int)(duration / EnergyProfile.StepSeconds);
        var levels = new float[steps];
        for (int i = 0; i < steps; i++)
            levels[i] = i < steps / 2 ? 0.02f : 0.30f;
        return levels;
    }

    /// <summary>A source whose samples say where in the record they came from: sample N has the value
    /// N. Playing any of it twice, or skipping any of it, shows as a jump in the numbers, which is
    /// what a stutter is.</summary>
    private sealed class RampSource : IPcmSource
    {
        private readonly double _ratio;
        private long _frame;

        public RampSource(string label, double startSeconds, double tempoRatio)
        {
            Label = label;
            _ratio = tempoRatio;
            _frame = (long)(startSeconds * Rate / tempoRatio);
        }

        public string Label { get; }

        /// <summary>The position on the record, in original seconds, of the next sample.</summary>
        public double NextSecond => _frame * _ratio / Rate;

        public int Read(float[] buffer, int frames)
        {
            for (int f = 0; f < frames; f++)
            {
                var value = (float)((_frame + f) * _ratio / Rate);
                for (int c = 0; c < Channels; c++)
                    buffer[f * Channels + c] = value;
            }
            _frame += frames;
            return frames;
        }

        public void Dispose() { }
    }

    /// <summary>
    /// A tone whose phase depends on which decoder produced it, not only on where in the record it
    /// is. That is what a pitch-preserving stretch really does: it places the music in overlapping
    /// grains, and a newly opened decoder starts its grains from where it started. So reopening at
    /// the same second gives the right music at the wrong point in the waveform. Joining the two
    /// directly makes a step, and a step is a click.
    /// </summary>
    private sealed class PhasedToneSource : IPcmSource
    {
        private static int _opened;
        private readonly double _phase;
        private long _frame;

        public PhasedToneSource(string label, double startSeconds, double tempoRatio)
        {
            Label = label;
            _frame = (long)(startSeconds * Rate / tempoRatio);
            _phase = Math.PI * Interlocked.Increment(ref _opened);   // every open gets a different phase
        }

        public string Label { get; }

        public int Read(float[] buffer, int frames)
        {
            for (int f = 0; f < frames; f++)
            {
                var value = (float)(0.5 * Math.Sin(2 * Math.PI * 220 * (_frame + f) / Rate + _phase));
                for (int c = 0; c < Channels; c++)
                    buffer[f * Channels + c] = value;
            }
            _frame += frames;
            return frames;
        }

        public void Dispose() { }
    }

    private static TrackAnalysis Analysis(string path, double bpm, MusicalKey key, double duration,
        double intro = 0, double? outro = null, double intensity = 0.5, bool modelled = false)
    {
        // A grid that actually covers the track, so the planner's bar snapping has bar lines to use.
        // From the start, because that is where a record's grid starts: a mix brings the new record in
        // BEFORE its drop, so the cue uses the bar lines under its intro.
        var beats = new List<double>();
        if (bpm > 0)
            for (double at = 0; at < duration; at += 60.0 / bpm)
                beats.Add(at);

        return new TrackAnalysis
        {
            FilePath = path,
            DurationSeconds = duration,
            Bpm = bpm,
            BpmConfidence = bpm > 0 ? 0.9 : 0,
            BeatTimes = beats,
            FirstDownbeat = 0,
            BeatsPerBar = 4,
            Key = key,
            KeyConfidence = key.IsKnown ? 0.8 : 0,
            EnergyCurve = [0.3f, 0.3f],
            Intensity = intensity,
            IntroEndSeconds = intro,
            OutroStartSeconds = outro ?? duration - 20,
            Version = modelled ? TrackAnalysis.ModelledVersion : TrackAnalysis.AnalyzerVersion,
        };
    }

    private static MixTrack Track(string name, double bpm, MusicalKey key, double duration = 120,
        double intro = 0, double? outro = null, double intensity = 0.5, bool analyzed = true,
        bool modelled = false)
    {
        var path = $"/library/{name}.flac";
        return new MixTrack
        {
            Source = path,
            Path = path,
            Title = name,
            TaggedDuration = duration,
            Analysis = analyzed ? Analysis(path, bpm, key, duration, intro, outro, intensity, modelled) : null,
        };
    }

    // ── the wheel ──

    [Fact]
    public void Camelot_neighbours_are_the_moves_a_dj_makes_and_a_tritone_is_not()
    {
        var aMinor = new MusicalKey(9, true);          // 8A
        Assert.Equal("8A", aMinor.Camelot);

        var cMajor = new MusicalKey(0, false);         // 8B, the relative major
        Assert.Equal("8B", cMajor.Camelot);
        Assert.Equal(1, aMinor.Distance(cMajor));
        Assert.True(aMinor.MixesWith(cMajor));

        var eMinor = new MusicalKey(4, true);          // 9A, one step round the wheel
        Assert.Equal(1, aMinor.Distance(eMinor));
        Assert.True(aMinor.MixesWith(eMinor));

        var ebMinor = new MusicalKey(3, true);         // 2A, half the wheel away
        Assert.Equal(6, aMinor.Distance(ebMinor));
        Assert.False(aMinor.MixesWith(ebMinor));

        // Unknown keys are "no evidence", not "far apart".
        Assert.Equal(int.MaxValue, aMinor.Distance(MusicalKey.Unknown));
    }

    [Fact]
    public void A_camelot_label_survives_a_trip_through_the_database()
    {
        for (int tonic = 0; tonic < 12; tonic++)
        {
            foreach (bool minor in (bool[])[false, true])
            {
                var key = new MusicalKey(tonic, minor);
                Assert.Equal(key, MusicalKey.FromCamelot(key.Camelot));
            }
        }
        Assert.False(MusicalKey.FromCamelot("nonsense").IsKnown);
        Assert.False(MusicalKey.FromCamelot(null).IsKnown);
    }

    // ── choosing records ──

    [Fact]
    public void The_next_record_is_the_one_that_matches_tempo_and_key()
    {
        var playing = Track("playing", 128, new MusicalKey(9, true));      // 128 BPM, 8A
        var candidates = new[]
        {
            Track("wrong-tempo", 168, new MusicalKey(9, true)),           // right key, unmixable tempo
            Track("wrong-key", 128, new MusicalKey(3, true)),             // right tempo, 2A: a clash
            Track("the-one", 129, new MusicalKey(4, true)),               // 9A, one step away: the mix
        };

        var next = DjSetPlanner.Next(playing, playing.Bpm, candidates);

        Assert.NotNull(next);
        Assert.Equal("the-one", next!.Title);
    }

    [Fact]
    public void A_record_at_half_the_tempo_still_counts_as_a_match()
    {
        // 70 and 140 are the same pulse counted differently, and a DJ mixes them easily.
        var playing = Track("fast", 140, new MusicalKey(9, true));
        var half = Track("half-time", 70, new MusicalKey(9, true));
        var unrelated = Track("unrelated", 96, new MusicalKey(9, true));

        Assert.True(
            DjSetPlanner.Score(half, 140, playing.Key, playing.Intensity) >
            DjSetPlanner.Score(unrelated, 140, playing.Key, playing.Intensity));
    }

    [Fact]
    public void An_unanalysed_record_scores_in_the_middle_rather_than_last()
    {
        var playing = Track("playing", 128, new MusicalKey(9, true));
        var unknown = Track("not-analysed-yet", 0, MusicalKey.Unknown, analyzed: false);
        var clash = Track("clash", 175, new MusicalKey(3, false));

        // Not knowing must not be treated as knowing it is bad: an unanalysed record wins over one
        // that is known to clash, and loses to one that is known to fit.
        double unknownScore = DjSetPlanner.Score(unknown, 128, playing.Key, playing.Intensity);
        double clashScore = DjSetPlanner.Score(clash, 128, playing.Key, playing.Intensity);
        double goodScore = DjSetPlanner.Score(Track("good", 128, new MusicalKey(9, true)), 128, playing.Key, playing.Intensity);

        Assert.True(unknownScore > clashScore, $"unknown {unknownScore:F2} should beat clash {clashScore:F2}");
        Assert.True(goodScore > unknownScore, $"a known match {goodScore:F2} should beat unknown {unknownScore:F2}");
    }

    [Fact]
    public void Between_two_equal_follow_ons_the_one_a_model_read_is_taken()
    {
        // Identical on everything the score can measure (same tempo, same key, same energy), so the
        // only difference is which kind of reading produced those numbers. This matters because the
        // score cannot see the difference that reading makes: TempoScore compares at half and double
        // on purpose, so a DSP octave error scores just as well here and only shows later, in the
        // pull and the fade.
        //
        // Not a perfect match to the playing record, on purpose: a perfect one gives a score of 1 for
        // both and the clamp would hide the tier.
        var playing = Track("playing", 128, new MusicalKey(9, true));
        var dsp = Track("dsp", 134, new MusicalKey(9, true));
        var modelled = Track("modelled", 134, new MusicalKey(9, true), modelled: true);

        double dspScore = DjSetPlanner.Score(dsp, 128, playing.Key, playing.Intensity);
        double modelledScore = DjSetPlanner.Score(modelled, 128, playing.Key, playing.Intensity);

        Assert.True(modelledScore > dspScore, $"modelled {modelledScore:F3} should beat dsp {dspScore:F3}");
        Assert.Same(modelled, DjSetPlanner.Next(playing, 128, [dsp, modelled]));
    }

    [Fact]
    public void The_model_tier_never_drags_a_worse_follow_on_in_front_of_a_better_one()
    {
        // The tier breaks ties between records that would both work. A good reading does not make a
        // record that would not work acceptable.
        var playing = Track("playing", 128, new MusicalKey(9, true));
        var goodDsp = Track("good-dsp", 128, new MusicalKey(9, true));
        var modelledClash = Track("modelled-clash", 175, new MusicalKey(3, false), modelled: true);

        Assert.Same(goodDsp, DjSetPlanner.Next(playing, 128, [modelledClash, goodDsp]));
    }

    [Fact]
    public void A_set_is_ordered_so_neighbours_are_mixable_and_nothing_is_dropped()
    {
        var tracks = new[]
        {
            Track("opener", 124, new MusicalKey(9, true)),      // 8A
            Track("far", 170, new MusicalKey(3, false)),        // far off on both tempo and key
            Track("close", 126, new MusicalKey(0, false)),      // 8B, relative major
            Track("middle", 130, new MusicalKey(4, true)),      // 9A
        };

        var ordered = DjSetPlanner.Order(tracks);

        Assert.Equal(tracks.Length, ordered.Count);
        Assert.Equal(tracks.Select(t => t.Title).OrderBy(t => t), ordered.Select(t => t.Title).OrderBy(t => t));
        Assert.Equal("opener", ordered[0].Title);           // the playlist's own first record opens
        Assert.Equal("far", ordered[^1].Title);             // the one that fits nothing is last
    }

    // ── planning a changeover ──

    [Fact]
    public void A_compatible_record_is_pulled_onto_the_playing_tempo_and_beat_matched()
    {
        var outgoing = Track("out", 128, new MusicalKey(9, true), duration: 240, outro: 200);
        var incoming = Track("in", 124, new MusicalKey(4, true), duration: 240, intro: 16);

        var plan = TransitionPlanner.Plan(outgoing, 128, incoming);

        Assert.Equal(TransitionStyle.BeatMatched, plan.Style);

        // They meet in the middle; one is not pulled all the way to the other. Both ratios move, in
        // opposite directions, and they reach the same tempo.
        Assert.True(plan.TempoRatio > 1, $"the slower record should speed up, ratio {plan.TempoRatio:F4}");
        Assert.True(plan.OutgoingTempoRatio < 1, $"the faster one should slow down, ratio {plan.OutgoingTempoRatio:F4}");
        Assert.Equal(124 * plan.TempoRatio, 128 * plan.OutgoingTempoRatio, 3);
        Assert.Equal(Math.Sqrt(128 * 124.0), plan.MixBpm, 2);

        // And each changes about half as much as one of them would alone.
        double eachWay = Math.Abs(plan.TempoRatio - 1);
        Assert.Equal(Math.Abs(plan.OutgoingTempoRatio - 1), eachWay, 3);
        Assert.True(eachWay < 128 / 124.0 - 1, "splitting the difference should bend each record less");

        // Bars counted at the tempo the MIX runs at, which is the midpoint, not either record's own
        // tempo.
        Assert.Equal(4 * 60.0 / plan.MixBpm, plan.BarSeconds, 4);
        Assert.Equal(TransitionPlanner.OutroFadeBars * plan.BarSeconds, plan.FadeSeconds, 2);

        // Both ends are on a bar line of their own grid.
        Assert.Equal(0, plan.StartAtSeconds % (60.0 / 128 * 4), 2);   // a bar line of the outgoing grid
        Assert.Equal(0, plan.CueInSeconds % (60.0 / 124 * 4), 2);     // and of the incoming one
    }

    [Fact]
    public void Records_too_far_apart_in_tempo_are_blended_rather_than_forced()
    {
        var outgoing = Track("out", 128, new MusicalKey(9, true), duration: 240, outro: 200);
        var incoming = Track("in", 175, new MusicalKey(9, true), duration: 240);

        var plan = TransitionPlanner.Plan(outgoing, 128, incoming);

        // 175 cannot be pulled to 128: a 27% stretch would not sound like the record any more.
        Assert.Equal(TransitionStyle.Blend, plan.Style);
        Assert.Equal(1, plan.TempoRatio, 6);
    }

    /// <summary>Four bars of the outgoing record in these tests: gridded from zero at 128 BPM, so a
    /// bar is 1.875 s and the fours the record is counted in are 7.5 s apart.</summary>
    private const double FourBars = 4 * 4 * 60.0 / 128;

    [Fact]
    public void A_changeover_starts_on_a_four_of_the_record_it_is_leaving()
    {
        // The outro is at 200 s, which is 26 and a half fours in. Any bar line works for the
        // arithmetic, but only a four sounds right: a mix that starts two bars into a phrase is
        // heard as out of phrase.
        var outgoing = Track("out", 128, new MusicalKey(9, true), duration: 300, outro: 200);
        var incoming = Track("in", 128, new MusicalKey(9, true), duration: 240, intro: 30);

        var plan = TransitionPlanner.Plan(outgoing, 128, incoming);

        Assert.Equal(TransitionStyle.BeatMatched, plan.Style);
        Assert.Equal(195, plan.StartAtSeconds, 3);
        Assert.Equal(0, plan.StartAtSeconds % FourBars, 3);
    }

    [Fact]
    public void A_blend_starts_on_a_four_as_well()
    {
        // Whether the two tempos can meet does not decide where the old record is left. A blend is
        // still a changeover out of a record with a grid, and it starts where the grid says.
        var outgoing = Track("out", 128, new MusicalKey(9, true), duration: 300, outro: 200);
        var incoming = Track("in", 175, new MusicalKey(9, true), duration: 240);

        var plan = TransitionPlanner.Plan(outgoing, 128, incoming);

        Assert.Equal(TransitionStyle.Blend, plan.Style);
        Assert.Equal(195, plan.StartAtSeconds, 3);
    }

    [Fact]
    public void A_changeover_asked_for_by_hand_lands_on_a_four_too()
    {
        // A user asks for a place, not for a sample. The result is the four at that place, the same
        // as the planner's own answer. A mark placed by eye must not make the only changeover in the
        // set that starts in the middle of a phrase.
        var outgoing = Track("out", 128, new MusicalKey(9, true), duration: 300, outro: 200);
        var incoming = Track("in", 128, new MusicalKey(9, true), duration: 240, intro: 30);

        var plan = TransitionPlanner.Plan(
            outgoing, 128, incoming, new MixOverrides { StartAtSeconds = 154.2 });

        Assert.Equal(150, plan.StartAtSeconds, 3);
    }

    [Fact]
    public void The_fade_begins_on_the_bar_line_the_plan_named_and_not_the_one_after_it()
    {
        // The plan names a bar line and the changeover waits for a bar line, and those are only the
        // same line if the set waits for the one it planned. If the set sees it one block late and
        // waits for the NEXT one, every changeover in the set starts a bar after where it was drawn:
        // the mix lab's green mark is on a four but the mix starts a bar later.
        var tracks = new[]
        {
            Track("first", 128, new MusicalKey(9, true), duration: 300, outro: 200),
            Track("second", 128, new MusicalKey(9, true), duration: 300, intro: 30, outro: 260),
        };

        using var dj = Booth(tracks, sourceSeconds: 300);
        var block = new float[512 * Channels];

        double planned = 0, began = -1;
        for (int i = 0; i < 200_000 && began < 0; i++)
        {
            if (dj.Read(block, 512) <= 0)
                break;
            if (dj.PendingPlan is { } pending)
                planned = pending.StartAtSeconds;

            // The hold counts the fade counter up from below zero, so the first block that reports any
            // progress is the block that contains the new record's first sample.
            if (dj.ChangeoverProgress > 0)
                began = dj.LivePositionSeconds;
        }

        Assert.Equal(195, planned, 3);
        Assert.True(began > 0, "the changeover never started");

        // Within a couple of blocks: the counter is read after the block that took it past zero, and
        // the position is read after that block was played. A bar is 1.875 s here, so a mix that
        // waits for the wrong line misses this by about 150 times the tolerance.
        Assert.InRange(began, planned, planned + 2 * 512 / (double)Rate);
    }

    [Fact]
    public void The_next_key_rolls_out_from_the_four_after_the_press_and_not_from_the_press()
    {
        // A press happens at a random moment. The record is left on a line, and the line is one of
        // its fours. If it is left on the bar after a press, the next record is a bar or two out of
        // phrase with the set for as long as it plays.
        var outgoing = Track("out", 128, new MusicalKey(9, true), duration: 300, outro: 260);
        var incoming = Track("in", 128, new MusicalKey(9, true), duration: 240, intro: 30);

        var skipped = TransitionPlanner.SkipTo(outgoing, 128, incoming, startAt: 92.3);

        Assert.Equal(TransitionStyle.RollOut, skipped.Style);
        Assert.Equal(97.5, skipped.StartAtSeconds, 3);          // the four after 92.3 s
        Assert.Equal(0, skipped.StartAtSeconds % FourBars, 3);

        // Pressed on a four, it starts there: a press that is already on a four is not moved to the
        // next one.
        Assert.Equal(90, TransitionPlanner.SkipTo(outgoing, 128, incoming, startAt: 90).StartAtSeconds, 3);
    }

    [Fact]
    public void A_next_key_press_holds_the_roll_until_the_record_reaches_that_four()
    {
        // And the set must wait for it. The plan naming a four is useless if the roll starts on the
        // first bar line after the press.
        var tracks = new[]
        {
            Track("first", 128, new MusicalKey(9, true), duration: 300, outro: 260),
            Track("second", 128, new MusicalKey(9, true), duration: 300, intro: 30, outro: 260),
        };

        using var dj = Booth(tracks, sourceSeconds: 300);
        var block = new float[512 * Channels];

        // A point that is neither a bar line nor a four: 40.1 s is just after the start of bar 21.
        for (int i = 0; i < 100_000 && dj.LivePositionSeconds < 40.1; i++)
            if (dj.Read(block, 512) <= 0)
                break;

        dj.SkipToNext();

        double planned = 0, began = -1;
        for (int i = 0; i < 200_000 && began < 0; i++)
        {
            if (dj.Read(block, 512) <= 0)
                break;
            if (dj.PendingPlan is { Style: TransitionStyle.RollOut } pending)
                planned = pending.StartAtSeconds;
            if (dj.ChangeoverProgress > 0)
                began = dj.LivePositionSeconds;
        }

        Assert.Equal(45, planned, 3);                           // the four after 40.1 s
        Assert.True(began > 0, "the next key never rolled the record out");
        Assert.InRange(began, planned, planned + 2 * 512 / (double)Rate);
    }


    [Fact]
    public void A_style_asked_for_is_honoured_whichever_planner_the_changeover_came_to()
    {
        // One request, two planners. A changeover is either one a record reaches on its own or one
        // the next key asks for. Plan makes the three styles that are mixes and SkipTo makes the
        // roll. The style box offers all four, so each planner passes a style that is not its own to
        // the other one. Otherwise some choices in the box would do nothing.
        var outgoing = Track("out", 128, new MusicalKey(9, true), duration: 300, outro: 260);
        var incoming = Track("in", 128, new MusicalKey(9, true), duration: 240, intro: 30);

        // The next key, asked for a mix: Plan's arithmetic, from the press and not from the outro,
        // moved forward to the four after it, because a mix planned behind the play position has
        // already missed its own start.
        var mixed = TransitionPlanner.SkipTo(
            outgoing, 128, incoming, startAt: 92.3,
            new MixOverrides { Style = TransitionStyle.BeatMatched });

        Assert.Equal(TransitionStyle.BeatMatched, mixed.Style);
        Assert.Equal(97.5, mixed.StartAtSeconds, 3);
        Assert.Equal(TransitionPlanner.OutroFadeBars, mixed.FadeBars);
        Assert.Contains(mixed.Trace, line => line.Contains("rather than rolled out", StringComparison.Ordinal));

        // And the changeover a record reaches on its own, asked to be a hand-over: the roll, at the
        // point the mix would have started.
        var rolled = TransitionPlanner.Plan(
            outgoing, 128, incoming, new MixOverrides { Style = TransitionStyle.RollOut });

        Assert.Equal(TransitionStyle.RollOut, rolled.Style);
        Assert.Equal(255, rolled.StartAtSeconds, 3);
        Assert.Equal(TransitionPlanner.RollOutBars, rolled.FadeBars);

        // Neither hand-off is passed back again: one call carries one style, and each side checks
        // before it passes it on.
        Assert.Equal(
            TransitionStyle.Blend,
            TransitionPlanner.SkipTo(
                outgoing, 128, incoming, startAt: 92.3,
                new MixOverrides { Style = TransitionStyle.Blend }).Style);
        Assert.Equal(
            TransitionStyle.Cut,
            TransitionPlanner.SkipTo(
                outgoing, 128, incoming, startAt: 92.3,
                new MixOverrides { Style = TransitionStyle.Cut }).Style);
    }

    [Fact]
    public void The_next_key_still_rolls_out_when_nobody_asked_for_anything_else()
    {
        // The default must survive the hand-off. An untouched style box is not a request (the mix lab
        // only sends one when the user picks a style). A next key must not silently become a
        // sixteen-bar mix just because the box shows the planner's own answer.
        var outgoing = Track("out", 128, new MusicalKey(9, true), duration: 300, outro: 260);
        var incoming = Track("in", 128, new MusicalKey(9, true), duration: 240, intro: 30);

        Assert.Equal(
            TransitionStyle.RollOut,
            TransitionPlanner.SkipTo(outgoing, 128, incoming, startAt: 90).Style);

        // Also together with every other request the user can make.
        var asked = new MixOverrides
        {
            FadeBars = 2,
            CueInSeconds = 40,
            Policy = new MixPolicy { MaxTempoPull = 0.005 },
        };
        var plan = TransitionPlanner.SkipTo(outgoing, 128, incoming, startAt: 90, asked);

        Assert.Equal(TransitionStyle.RollOut, plan.Style);
        Assert.Equal(2, plan.FadeBars);
        Assert.True(
            Math.Abs(plan.TempoRatio - 1) <= 0.005 + 1e-9,
            $"a pull of {plan.TempoRatio:F4} past the 0.5% the policy allowed");
    }

    [Fact]
    public void A_record_that_stops_playing_long_before_its_file_ends_is_left_where_it_stops()
    {
        // A file of seven minutes with two and a half minutes of music in it and silence after (for
        // example Gypsy Woman). The grid extends to both ends of the file on purpose, so every silent
        // minute has bar lines in it. If the mix-out point is taken from the file's length, the
        // changeover is four minutes into the silence and the set waits for a record that has
        // already ended.
        var track = Track("padded", 120, new MusicalKey(9, true), duration: 440, outro: 400);
        track = track with
        {
            Analysis = track.Analysis! with { AudioStartSeconds = 0.5, AudioEndSeconds = 155 },
        };

        double at = TransitionPlanner.MixOutPoint(track);

        Assert.True(at < 155, $"mixed out at {MixInputs.Sec(at)} of a record that stops at 2:35");
        Assert.True(at > 60, $"mixed out at {MixInputs.Sec(at)}, nowhere near the end of the music");

        // And the changeover itself fits inside the music and does not run past its end.
        var incoming = Track("in", 120, new MusicalKey(9, true), duration: 240, intro: 30);
        var plan = TransitionPlanner.Plan(track, 120, incoming);

        Assert.True(
            plan.StartAtSeconds + plan.FadeSeconds <= 155 + 0.01,
            $"the fade starts at {MixInputs.Sec(plan.StartAtSeconds)} and runs {plan.FadeSeconds:F1}s, " +
            $"past the {MixInputs.Sec(155)} the record stops at");
    }

    [Fact]
    public void A_changeover_is_only_as_long_as_the_two_grids_stay_together()
    {
        // Swept, not hand-picked: for any pair given to the planner, a changeover it calls
        // beat-matched must not drift audibly over its own length. Somewhere in this range the pull
        // reaches its limit and the fade must be shortened to keep that true, which is what is tested.
        var outgoing = Track("out", 128, new MusicalKey(9, true), duration: 240, outro: 200);
        bool sawShortened = false;

        for (double bpm = 100; bpm <= 160; bpm += 0.5)
        {
            var incoming = Track($"in-{bpm}", bpm, new MusicalKey(9, true), duration: 240);
            var plan = TransitionPlanner.Plan(outgoing, 128, incoming);
            if (plan.Style != TransitionStyle.BeatMatched)
                continue;

            double outBpm = 128 * plan.OutgoingTempoRatio;
            double inBpm = bpm * plan.TempoRatio;
            double drift = plan.FadeSeconds * Math.Abs(inBpm - outBpm) / outBpm;

            Assert.True(drift <= 0.016,
                $"{bpm:F1} BPM: {outBpm:F2} against {inBpm:F2} over {plan.FadeSeconds:F2}s drifts {drift * 1000:F0} ms");
            Assert.True(plan.FadeSeconds >= plan.BarSeconds, $"{bpm:F1} BPM: shorter than a bar");

            if (plan.FadeSeconds < TransitionPlanner.OutroFadeBars * plan.BarSeconds - 0.01)
                sawShortened = true;
        }

        Assert.True(sawShortened, "somewhere in that sweep a pair should have needed a shorter mix");
    }

    [Fact]
    public void A_pair_that_cannot_hold_a_bar_together_is_blended_not_beat_matched()
    {
        // This is decided not by the size of the pull but by whether the grids stay together: two
        // records the clamps cannot bring to the same tempo drift apart, and after a bar of drift,
        // calling it a beat-match would be worse than not calling it one.
        var outgoing = Track("out", 128, new MusicalKey(9, true), duration: 240, outro: 200);
        var far = Track("in", 100, new MusicalKey(9, true), duration: 240);

        Assert.Equal(TransitionStyle.Blend, TransitionPlanner.Plan(outgoing, 128, far).Style);

        // 112 against 128 is 12.5% apart: each deck changes about 6% and they meet exactly, which a
        // pull limit of 6% cannot quite do and 10% can.
        var reachable = Track("reachable", 112, new MusicalKey(9, true), duration: 240);
        var plan = TransitionPlanner.Plan(outgoing, 128, reachable);
        Assert.Equal(TransitionStyle.BeatMatched, plan.Style);
        Assert.Equal(128 * plan.OutgoingTempoRatio, 112 * plan.TempoRatio, 2);
    }

    [Fact]
    public void The_pull_reaches_the_pair_the_reference_mix_puts_together()
    {
        // 123 into 137 is 11.4% apart. A hand-made mix of these two records pulls one of them the
        // whole way; split between two decks it is about 5.5% each.
        var outgoing = Track("groove-jet", 123, new MusicalKey(9, true), duration: 222, outro: 219.8);
        var incoming = Track("rock-the-house", 137, new MusicalKey(3, false), duration: 209);

        var plan = TransitionPlanner.Plan(outgoing, 123, incoming);

        Assert.Equal(TransitionStyle.BeatMatched, plan.Style);
        Assert.Equal(123 * plan.OutgoingTempoRatio, 137 * plan.TempoRatio, 2);
        Assert.Equal(TransitionPlanner.OutroFadeBars * plan.BarSeconds, plan.FadeSeconds, 2);

        // And the range on each side of a record, which is what the limit really means: when both
        // move to meet in the middle, a pair within a factor of 1.1/0.9 can be matched and a wider one
        // cannot.
        var at128 = Track("out", 128, new MusicalKey(9, true), duration: 240, outro: 200);
        TransitionStyle[] reach =
        [
            TransitionPlanner.Plan(at128, 128, Track("high", 157, new MusicalKey(9, true), duration: 240)).Style,
            TransitionPlanner.Plan(at128, 128, Track("low", 105, new MusicalKey(9, true), duration: 240)).Style,
            TransitionPlanner.Plan(at128, 128, Track("too-high", 162, new MusicalKey(9, true), duration: 240)).Style,
            TransitionPlanner.Plan(at128, 128, Track("too-low", 102, new MusicalKey(9, true), duration: 240)).Style,
        ];
        Assert.Equal(
            [TransitionStyle.BeatMatched, TransitionStyle.BeatMatched, TransitionStyle.Blend, TransitionStyle.Blend],
            reach);
    }

    [Fact]
    public void The_tempo_pull_never_exceeds_what_a_record_takes()
    {
        var outgoing = Track("out", 128, new MusicalKey(9, true), duration: 240, outro: 200);
        var incoming = Track("in", 121, new MusicalKey(9, true), duration: 240);

        var plan = TransitionPlanner.Plan(outgoing, 128, incoming);

        Assert.True(Math.Abs(plan.TempoRatio - 1) <= TransitionPlanner.MaxTempoPull + 1e-9,
            $"pulled {(plan.TempoRatio - 1) * 100:F1}%");
    }

    [Fact]
    public void An_unanalysed_record_is_still_played_just_not_matched()
    {
        var outgoing = Track("out", 128, new MusicalKey(9, true), duration: 240, outro: 200);
        var incoming = Track("in", 0, MusicalKey.Unknown, duration: 200, analyzed: false);

        var plan = TransitionPlanner.Plan(outgoing, 128, incoming);

        Assert.Equal(TransitionStyle.Cut, plan.Style);
        Assert.True(plan.FadeSeconds > 0);
        Assert.Equal(incoming, plan.Incoming);
    }

    [Fact]
    public void The_opening_record_of_a_set_plays_from_the_top()
    {
        // There is no previous record to mix over: the first record of a set is played from the
        // start at its own tempo, with no fade.
        var opener = Track("opener", 128, new MusicalKey(9, true), duration: 240, intro: 30);

        var opening = TransitionPlanner.Opening(opener);
        Assert.Equal(0, opening.CueInSeconds, 6);
        Assert.Equal(1, opening.TempoRatio, 6);
        Assert.Equal(0, opening.FadeSeconds, 6);
    }

    [Fact]
    public void A_record_coming_in_drops_as_the_changeover_finishes()
    {
        // The intro is not skipped: the record is mixed in ON it. Cued a changeover's length before
        // its drop, the new record's intro plays under the old one for exactly as long as the two
        // play together, and the drop comes as the old record ends. A hand-made mix does the same:
        // both records come in at their own start.
        var outgoing = Track("out", 128, new MusicalKey(9, true), duration: 300, outro: 260);
        var incoming = Track("in", 128, new MusicalKey(9, true), duration: 240, intro: 48);

        var plan = TransitionPlanner.Plan(outgoing, 128, incoming);

        // Within a bar: the cue itself is on a bar line of the incoming record, so the drop can come
        // up to one bar early.
        double drop = incoming.Analysis!.IntroEndSeconds - plan.CueInSeconds;
        Assert.InRange(drop, plan.FadeSeconds - plan.BarSeconds, plan.FadeSeconds);

        // A record whose intro is shorter than the changeover comes in at its start, and its drop
        // comes part way through: there is no earlier point to cue it from.
        var early = Track("short-intro", 128, new MusicalKey(9, true), duration: 240, intro: 8);
        var shorter = TransitionPlanner.Plan(outgoing, 128, early);
        Assert.Equal(0, shorter.CueInSeconds, 6);
    }

    [Fact]
    public void The_first_record_the_dj_drops_starts_at_zero()
    {
        using var dj = Booth([Track("first", 128, new MusicalKey(9, true), duration: 240, intro: 30)]);
        var block = new float[512 * Channels];

        dj.Read(block, 512);

        Assert.True(dj.PositionSeconds < 1, $"the set opened {dj.PositionSeconds:F1}s into the record");
    }

    [Fact]
    public void A_record_with_no_analysis_at_all_still_gets_a_mix_out_point_before_its_end()
    {
        var track = Track("untouched", 0, MusicalKey.Unknown, duration: 180, analyzed: false);

        double at = TransitionPlanner.MixOutPoint(track);

        Assert.True(at > 0 && at < 180, $"mix-out at {at:F1}s of a 180 s record");
    }

    [Fact]
    public void A_record_left_to_reach_its_outro_is_mixed_and_one_that_is_skipped_is_rolled_out()
    {
        // The changeover a record reaches on its own is the only mixed one a set makes: sixteen bars
        // out of the old record over sixteen bars into the new one. Pressing "next" is not a mix at
        // all: the old record is looped out over eight bars and the new one starts after it.
        var outgoing = Track("out", 128, new MusicalKey(9, true), duration: 300, outro: 260);
        var incoming = Track("in", 128, new MusicalKey(9, true), duration: 240, intro: 30);

        var atTheOutro = TransitionPlanner.Plan(outgoing, 128, incoming);
        var skipped = TransitionPlanner.SkipTo(outgoing, 128, incoming, startAt: 90);

        Assert.Equal(TransitionPlanner.OutroFadeBars, atTheOutro.FadeBars);
        Assert.Equal(TransitionPlanner.OutroFadeBars * atTheOutro.BarSeconds, atTheOutro.FadeSeconds, 3);
        Assert.True(atTheOutro.StartAtSeconds > 150, "a changeover at the outro mixes out near the end");

        // Eight bars of the outgoing record's own bar, neither record moved off its own speed, and
        // the new one cued where it comes in, not before it.
        Assert.Equal(TransitionStyle.RollOut, skipped.Style);
        Assert.Equal(TransitionPlanner.RollOutBars, skipped.FadeBars);
        Assert.Equal(4 * 60.0 / 128, skipped.BarSeconds, 3);
        Assert.Equal(TransitionPlanner.RollOutBars * skipped.BarSeconds, skipped.FadeSeconds, 3);
        Assert.Equal(1, skipped.TempoRatio);
        Assert.Equal(1, skipped.OutgoingTempoRatio);
        Assert.Equal(0, skipped.ReleaseSeconds);
        Assert.Equal(30, skipped.CueInSeconds, 1);

        // Two records at one tempo: the loops are the same bar and they overlap.
        Assert.Equal(TransitionPlanner.RollInBars, skipped.OverlapBars);
        Assert.Equal(skipped.BarSeconds, skipped.RollInBarSeconds, 6);
    }

    [Fact]
    public void A_skip_pulls_the_two_records_onto_one_tempo_before_it_overlaps_them()
    {
        // The two loops play together for four bars, so they must have one tempo and one bar. Both
        // pitch faders move, as they do for a mix: 128 and 134 meet at 131.
        var outgoing = Track("out", 128, new MusicalKey(9, true), duration: 300, outro: 260);
        var incoming = Track("in", 134, new MusicalKey(9, true), duration: 240);

        var skipped = TransitionPlanner.SkipTo(outgoing, 128, incoming, startAt: 90);

        double met = Math.Sqrt(128 * 134.0);
        Assert.Equal(met, skipped.MixBpm, 1);
        Assert.Equal(met / 128, skipped.OutgoingTempoRatio, 4);
        Assert.Equal(met / 134, skipped.TempoRatio, 4);

        // One bar for both loops, four of them overlapped, and the incoming record that was pulled
        // returns to its own speed afterwards.
        Assert.Equal(TransitionPlanner.RollInBars, skipped.OverlapBars);
        Assert.Equal(skipped.BarSeconds, skipped.RollInBarSeconds, 6);
        Assert.True(skipped.ReleaseSeconds > 0, "a record that landed pulled has to be released");
    }

    [Fact]
    public void Two_records_that_cannot_meet_are_not_overlapped_at_all()
    {
        // 128 into 100 is beyond a pitch fader's range, so the grids would drift apart audibly. The
        // loop finishes on its own and the new record follows it, still looped in on its own bar, but
        // not played over a bar that does not match.
        var outgoing = Track("out", 128, new MusicalKey(9, true), duration: 300, outro: 260);
        var incoming = Track("in", 100, new MusicalKey(9, true), duration: 240);

        var skipped = TransitionPlanner.SkipTo(outgoing, 128, incoming, startAt: 90);

        Assert.Equal(TransitionStyle.RollOut, skipped.Style);
        Assert.Equal(0, skipped.OverlapBars);
        Assert.Equal(1, skipped.TempoRatio);
        Assert.Equal(1, skipped.OutgoingTempoRatio);
        Assert.Equal(0, skipped.ReleaseSeconds);

        // Its own bar, not the one being rolled out: looping a record to another record's bar is
        // worse than not looping it.
        Assert.True(skipped.RollInBarSeconds > skipped.BarSeconds,
            "the slower record's bar is longer than the one it is following");
    }

    [Fact]
    public void A_record_with_no_grid_is_cut_rather_than_rolled_out()
    {
        // There is no bar to loop, so there is nothing to roll out on. The old record leaves and the
        // new one starts, which is still a working next key.
        var outgoing = Track("out", 0, MusicalKey.Unknown, duration: 300, analyzed: false);
        var incoming = Track("in", 128, new MusicalKey(9, true), duration: 240);

        var skipped = TransitionPlanner.SkipTo(outgoing, 0, incoming, startAt: 90);

        Assert.Equal(TransitionStyle.Cut, skipped.Style);
        Assert.Equal(TransitionPlanner.SkipCutSeconds, skipped.FadeSeconds);
    }

    [Fact]
    public void A_tempo_counted_an_octave_low_is_still_beat_matched()
    {
        // The estimator often reports 148 BPM records as 74. Which count is used is a matter of
        // taste, not speed, and the window a tempo is reported in holds both. Read literally, 74
        // against 150 is a factor of two and no pitch fader reaches it, so a mix that MUST be
        // beat-matched would become a blend. The pair here is Revolution (reported 74, danced at 148)
        // into 6th Gear (150), which a hand-made mix beat-matches throughout.
        var outgoing = Track("revolution", 74.01, new MusicalKey(9, true), duration: 264, outro: 251);
        var incoming = Track("sixth-gear", 150.02, new MusicalKey(11, false), duration: 212, intro: 14.8);

        var plan = TransitionPlanner.Plan(outgoing, 74.01, incoming);

        Assert.Equal(TransitionStyle.BeatMatched, plan.Style);

        // The two meet an octave apart, which is the same tempo: 149.0 against 149.0.
        Assert.Equal(74.01 * plan.OutgoingTempoRatio * 2, 150.02 * plan.TempoRatio, 2);
        Assert.True(Math.Abs(plan.TempoRatio - 1) <= TransitionPlanner.MaxTempoPull);

        // And the bars are the ones the listener counts: 1.6 seconds, not 3.2. That is the main check
        // of this test: on the octave of the reading, a bar would be 3.2 s and the changeover twice as
        // long as expected.
        //
        // The length is checked against OutroFadeBars, not against the record, so the test checks
        // that the bar is 1.6 s whatever that constant is set to.
        Assert.Equal(4 * 60.0 / 149.0, plan.BarSeconds, 2);
        Assert.Equal(TransitionPlanner.OutroFadeBars, plan.FadeBars);
        Assert.Equal(TransitionPlanner.OutroFadeBars * plan.BarSeconds, plan.FadeSeconds, 2);
    }

    [Fact]
    public void A_record_that_ends_on_a_beat_still_gets_room_for_the_whole_changeover()
    {
        // Most dance records do not fade out, so the analyser puts their outro a second or two before
        // the last sample. Starting the changeover there leaves nothing to fade in, and the sixteen
        // bars become a cut.
        var outgoing = Track("hard-ending", 128, new MusicalKey(9, true), duration: 222, outro: 219.8);
        var incoming = Track("in", 128, new MusicalKey(9, true), duration: 240);

        var plan = TransitionPlanner.Plan(outgoing, 128, incoming);

        Assert.Equal(TransitionPlanner.OutroFadeBars * plan.BarSeconds, plan.FadeSeconds, 1);
        Assert.True(plan.StartAtSeconds + plan.FadeSeconds <= outgoing.DurationSeconds + 0.01,
            $"the fade starts at {plan.StartAtSeconds:F1}s and runs {plan.FadeSeconds:F1}s past a {outgoing.DurationSeconds:F0}s record");
    }

    [Fact]
    public void A_record_pulled_onto_another_tempo_is_given_the_time_to_come_back()
    {
        var outgoing = Track("out", 123, new MusicalKey(9, true), duration: 240, outro: 200);
        var pulled = TransitionPlanner.Plan(outgoing, 123, Track("in", 137, new MusicalKey(9, true), duration: 240));

        Assert.NotEqual(1, pulled.TempoRatio);
        Assert.Equal(TransitionPlanner.ReleaseBars * pulled.BarSeconds, pulled.ReleaseSeconds, 3);

        // Nothing to release when the record's tempo was not changed.
        var matched = TransitionPlanner.Plan(outgoing, 123, Track("same", 123, new MusicalKey(9, true), duration: 240));
        Assert.Equal(1, matched.TempoRatio, 6);
        Assert.Equal(0, matched.ReleaseSeconds, 6);
    }

    // ── the fader ──

    [Fact]
    public void Both_records_move_and_the_crossfade_holds_its_power_through_the_middle()
    {
        // Both faders move: the incoming record is faded in across the same bars the outgoing one is
        // faded out over. Equal power, because a linear pair sums to 0.7 in the middle and the mix
        // drops in level exactly where it should be strongest.
        for (double t = 0; t <= 1.0001; t += 0.1)
        {
            var levels = Crossfader.At(t);
            double power = levels.Outgoing * levels.Outgoing + levels.Incoming * levels.Incoming;
            Assert.Equal(1.0, power, 6);
        }

        Assert.Equal(1.0, Crossfader.At(0).Outgoing, 6);
        Assert.Equal(0, Crossfader.At(0).Incoming, 6);
        Assert.Equal(1.0, Crossfader.At(1).Incoming, 6);
        Assert.Equal(0, Crossfader.At(1).Outgoing, 6);

        // The two have the same level at the balance point, which is where the record being heard
        // changes.
        var middle = Crossfader.At(Crossfader.Balance);
        Assert.Equal(middle.Outgoing, middle.Incoming, 6);
    }

    [Fact]
    public void The_record_leaving_is_gone_before_the_changeover_ends()
    {
        // A record still playing a bar before the end of a changeover plays too long: the new one has
        // been playing for most of the handover and is the one the listener follows. The crossing
        // finishes early and the last part belongs only to the incoming record.
        Assert.True(Crossfader.OutBy < 1, "the crossing has to finish before the changeover does");

        var landed = Crossfader.At(Crossfader.OutBy);
        Assert.Equal(0, landed.Outgoing, 6);
        Assert.Equal(1.0, landed.Incoming, 6);

        // And it stays that way to the end and does not come back.
        Assert.Equal(0, Crossfader.At(0.9).Outgoing, 6);
        Assert.Equal(1.0, Crossfader.At(0.9).Incoming, 6);

        // The bass has moved to the incoming record by then too.
        Assert.Equal(DeckEq.KillDb, Crossfader.BassDb(Crossfader.OutBy).Outgoing, 3);
        Assert.Equal(0, Crossfader.BassDb(Crossfader.OutBy).Incoming, 3);
    }

    [Fact]
    public void The_bass_belongs_to_one_record_at_a_time()
    {
        // At either end, the record that is in the mix alone has the bass.
        Assert.Equal(0, Crossfader.BassDb(0).Outgoing, 3);
        Assert.Equal(DeckEq.KillDb, Crossfader.BassDb(0).Incoming, 3);
        Assert.Equal(DeckEq.KillDb, Crossfader.BassDb(1).Outgoing, 3);
        Assert.Equal(0, Crossfader.BassDb(1).Incoming, 3);

        // And through the swap they are never both up.
        for (double t = 0; t <= 1.0001; t += 0.05)
        {
            var bass = Crossfader.BassDb(t);
            Assert.True(bass.Outgoing + bass.Incoming <= DeckEq.KillDb + 0.001,
                $"at t={t:F2} both records still have bass: {bass.Outgoing:F1} / {bass.Incoming:F1}");
        }
    }

    [Fact]
    public void The_bass_shelf_goes_in_and_comes_back_out_without_a_step()
    {
        // The shelf is lowered and raised again on a record that is playing, so the join at either
        // end is heard. A filter that is skipped while flat and switched in at the first decibel
        // starts from an empty delay line against a signal at full level, and returns the raw input
        // at the other end while it still holds the filtered one: two steps, one at each end of
        // every changeover.
        var eq = new DeckEq(Rate, 1);
        var tone = new float[Rate / 4];
        var heard = new List<float>();

        void Push(double db, int blocks)
        {
            for (int b = 0; b < blocks; b++)
            {
                eq.SetLow(db);
                for (int i = 0; i < 256; i++)
                    tone[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 80 * (heard.Count + i) / Rate));
                eq.Process(tone, 256);
                heard.AddRange(tone.Take(256));
            }
        }

        // The user moving the knob from the centre to its limit at once, which is the fastest the
        // interface allows and must be clean on its own.
        Push(0, 8);
        Push(DeckEq.MaxCutDb, 8);
        Push(0, 8);

        // And then the kill, moved down and back the way a changeover moves it: a fraction of a dB
        // per block. Not in one jump: the swap reaches sixty dB below the record, and no filter can
        // apply that between two samples without a step. The mix moves it gradually (see
        // Crossfader.BassDb, read once per block across the middle third of the crossing), so that is
        // what is tested. The kill goes so deep that a single jump cannot be clean; that is the cost
        // of really removing the bass.
        for (int step = 0; step <= 32; step++)
            Push(DeckEq.KillDb * step / 32.0, 1);
        for (int step = 32; step >= 0; step--)
            Push(DeckEq.KillDb * step / 32.0, 1);

        // An 80 Hz sine moves about 0.006 per sample; anything several times that is the filter being
        // switched, not the tone being filtered.
        for (int i = 1; i < heard.Count; i++)
            Assert.True(Math.Abs(heard[i] - heard[i - 1]) < 0.05,
                $"step of {Math.Abs(heard[i] - heard[i - 1]):F3} at sample {i} ({i / (double)Rate * 1000:F1} ms)");
    }

    [Fact]
    public void A_deck_never_hands_back_a_short_block_while_the_record_is_still_running()
    {
        // The booth mixes what each deck returns and continues, so a short block becomes silence for
        // the rest of the block, possibly on the record the listener is hearing. A deck reads audio
        // ahead whenever it is cued or its speed is changed, and that read-ahead audio runs out in
        // the middle of a block almost every time: 8192 frames is not a whole multiple of what a
        // deck pulls ahead. A deck that returns a short block there loses up to a block of audio at
        // every changeover and at every step of a pitch change.
        var deck = new Deck(Deck.Side.A, Rate, Channels,
            options => new RampSource("ramp", options.StartSeconds, options.TempoRatio));

        deck.Load(Track("ramp", 128, new MusicalKey(9, true), duration: 600), cueSeconds: 0, tempoRatio: 1);
        deck.Prime();
        deck.Start();
        deck.Fader = 1;

        const int Block = 2205;                       // the engine's own block: 50 ms, not a factor of 8192
        var buffer = new float[Block * Channels];
        double last = 0;
        for (int i = 0; i < 60; i++)
        {
            int read = deck.Read(buffer, Block);
            Assert.Equal(Block, read);

            // And the audio itself is continuous: the ramp says where on the record each sample came
            // from, so a repeat or a skip at the join shows as a jump in the numbers.
            double first = buffer[0] / Math.Max(1e-9, deck.Fader);
            if (i > 0)
                Assert.Equal(last + 1.0 / Rate, first, 4);
            last = buffer[(Block - 1) * Channels];
        }
    }

    // ── the whole set ──

    /// <param name="sourceSeconds">How much audio every record has, from its first sample. Records
    /// longer than this run out early, which several tests below need and the tests that seek do
    /// not: a seek past the end of the audio finds nothing to play.</param>
    /// <param name="chance">The DJ's random choice. Never, by default: the app adds noise over a
    /// changeover a quarter of the time, and a test must not depend on chance. The test about the
    /// noise turns it on.</param>
    private static Dj Booth(
        IEnumerable<MixTrack> tracks, IDictionary<string, float>? markers = null, double sourceSeconds = 120,
        Func<double>? chance = null)
    {
        var crate = new DjCrate(tracks);
        return new Dj(
            crate, Rate, Channels,
            options =>
            {
                float marker = markers is not null && markers.TryGetValue(options.Source, out var v) ? v : 1f;
                double length = sourceSeconds - options.StartSeconds;
                return new ToneSource(options.Label ?? options.Source, marker, length, options.TempoRatio);
            },
            // Cue the next record inline. In the app that work opens a decoder and must run off the
            // audio thread; here it is a few field writes, and running it on the thread pool would make
            // every assertion below depend on how busy the machine is.
            cueRunner: work => { work(); return Task.CompletedTask; },
            chance: chance ?? (() => 1));
    }

    [Fact]
    public void A_set_plays_every_record_in_turn_and_then_ends()
    {
        var tracks = new[]
        {
            Track("one", 128, new MusicalKey(9, true), duration: 120, outro: 100),
            Track("two", 128, new MusicalKey(9, true), duration: 120, outro: 100),
            Track("three", 128, new MusicalKey(9, true), duration: 120, outro: 100),
        };

        using var dj = Booth(tracks);
        var heard = new List<string>();
        dj.TrackChanged += t => heard.Add(t.Title!);

        var block = new float[512 * Channels];
        int blocks = 0;
        while (dj.Read(block, 512) > 0 && blocks < 200_000)
            blocks++;

        Assert.True(dj.HasFinished);
        Assert.Equal(3, heard.Count);
        Assert.Equal(["one", "two", "three"], heard);
        Assert.Equal(3, dj.Played.Count);

        // Three ~120 s records overlapping by a few seconds each: the mix is shorter than playing
        // them end to end, and much longer than any one of them.
        double seconds = blocks * 512.0 / Rate;
        Assert.InRange(seconds, 200, 360);
    }

    [Fact]
    public void The_two_records_are_actually_heard_together_through_a_changeover()
    {
        // Deck A's record is +1 everywhere, deck B's is -1. Anything strictly between the two is the
        // sound of both decks at once, which is what a mix is.
        var tracks = new[]
        {
            Track("a-side", 128, new MusicalKey(9, true), duration: 40, outro: 24),
            Track("b-side", 128, new MusicalKey(9, true), duration: 40, outro: 24),
        };
        var markers = new Dictionary<string, float>
        {
            ["/library/a-side.flac"] = 1f,
            ["/library/b-side.flac"] = -1f,
        };

        using var dj = Booth(tracks, markers);
        var block = new float[512 * Channels];

        bool sawBoth = false, sawTransitioning = false;
        for (int i = 0; i < 20_000; i++)
        {
            if (dj.Read(block, 512) <= 0)
                break;
            if (dj.IsTransitioning)
            {
                sawTransitioning = true;
                // In the middle of the fade, the sum of a +1 and a -1 record is between them, never
                // at either.
                if (Math.Abs(block[0]) < 0.9f)
                    sawBoth = true;
            }
        }

        Assert.True(sawTransitioning, "the set should have run a changeover");
        Assert.True(sawBoth, "the two records should overlap, not cut");
    }

    [Fact]
    public void The_room_is_told_it_is_on_the_new_record_half_way_through_the_crossing()
    {
        // Both records are audible while they cross, so "what is playing" cannot change when the fade
        // starts: the old record is still the louder one for the first half of it. The readout, the
        // artwork and the progress bar all follow this.
        var tracks = new[]
        {
            Track("a-side", 128, new MusicalKey(9, true), duration: 100, outro: 60),
            Track("b-side", 128, new MusicalKey(9, true), duration: 100, outro: 60),
        };

        using var dj = Booth(tracks);
        var block = new float[512 * Channels];
        var landed = new List<string>();
        dj.TrackChanged += t => landed.Add(t.Title!);

        double fadeStartedAt = -1, changedAt = -1, secondsPlayed = 0;
        for (int i = 0; i < 40_000 && changedAt < 0; i++)
        {
            if (dj.Read(block, 512) <= 0)
                break;
            secondsPlayed += 512.0 / Rate;

            if (dj.IsTransitioning && fadeStartedAt < 0)
                fadeStartedAt = secondsPlayed;
            if (landed.Count == 2 && changedAt < 0)
                changedAt = secondsPlayed;
        }

        Assert.True(fadeStartedAt > 0, "the set should have run a changeover");
        Assert.True(changedAt > 0, "the set never said it had moved on");

        // Eight bars at 128 is 15 s, and the crossing is over by three quarters of that, so the two
        // have the same level at 5.6 s. Plus up to a bar of waiting for the outgoing record's next
        // downbeat, which is where the fade itself starts.
        //
        // Computed from the constant, not written out: the length of the changeover can change, and
        // a test with a fixed number would fail when it does, without saying anything about what it
        // checks: where in a fade the listener is told the record has changed.
        double bar = 4 * 60.0 / 128;
        double balance = Crossfader.Balance * TransitionPlanner.OutroFadeBars * bar;
        Assert.InRange(changedAt - fadeStartedAt, balance - 0.5, balance + bar + 0.2);
        Assert.Equal(["a-side", "b-side"], landed);
    }

    [Fact]
    public void A_record_that_ends_early_in_a_changeover_still_hands_over_properly()
    {
        // A record whose tags give the wrong length can run out before the fade reaches its half way
        // point, which is where the set reports the new record. The audio handles that by itself; the
        // risk is that everything showing what is playing stays on a record that has ended. It is a
        // silent failure, since it still sounds fine.
        var tracks = new[]
        {
            Track("short", 128, new MusicalKey(9, true), duration: 240, outro: 116),
            Track("next", 128, new MusicalKey(9, true), duration: 240, outro: 116),
        };

        using var dj = Booth(tracks);                 // the harness's decks run out at 120 s
        var block = new float[512 * Channels];
        var heard = new List<string>();
        dj.TrackChanged += t => heard.Add(t.Title!);

        int quiet = 0, longestQuiet = 0;
        for (int i = 0; i < 40_000; i++)
        {
            if (dj.Read(block, 512) <= 0)
                break;
            bool silent = true;
            for (int s = 0; s < 512 * Channels && silent; s++)
                silent = block[s] == 0;
            quiet = silent ? quiet + 1 : 0;
            longestQuiet = Math.Max(longestQuiet, quiet);
        }

        Assert.Equal(["short", "next"], heard);
        double gap = longestQuiet * 512.0 / Rate;
        Assert.True(gap < 0.3, $"the set went quiet for {gap:F2}s");
    }

    [Fact]
    public void A_skip_keeps_the_cued_record_when_the_planner_picks_it_again()
    {
        // The record cued for the outro was chosen to follow the END of this track, so a skip chooses
        // again from the current play position. The answer is usually the same record, and replacing
        // it with an identical one would mean stopping a decoder and starting another, at exactly the
        // moment the button is pressed.
        var tracks = new[]
        {
            Track("one", 128, new MusicalKey(9, true), duration: 120, outro: 100),
            Track("two", 128, new MusicalKey(9, true), duration: 120, outro: 100),
        };

        var opens = new Dictionary<string, int>();
        var crate = new DjCrate(tracks);
        using var dj = new Dj(
            crate, Rate, Channels,
            options =>
            {
                opens[options.Source] = opens.GetValueOrDefault(options.Source) + 1;
                return new ToneSource(options.Label ?? options.Source, 1f, 120 - options.StartSeconds, options.TempoRatio);
            },
            cueRunner: work => { work(); return Task.CompletedTask; });

        var block = new float[512 * Channels];
        const string second = "/library/two.flac";

        // Play until the DJ has cued the next record up ahead of the outro.
        for (int i = 0; i < 40_000 && opens.GetValueOrDefault(second) == 0; i++)
            if (dj.Read(block, 512) <= 0)
                break;
        Assert.Equal(1, opens.GetValueOrDefault(second));

        // Now press next. The planner chooses again, and with one record in the crate it can only
        // choose the same one, which is the case that must be cheap.
        dj.SkipToNext();
        for (int i = 0; i < 40_000; i++)
        {
            if (dj.Read(block, 512) <= 0)
                break;
            if (dj.NowPlaying?.Title == "two")
                break;
        }

        Assert.Equal("two", dj.NowPlaying?.Title);
        Assert.Equal(1, opens.GetValueOrDefault(second));      // still the deck it was cued on
    }

    [Fact]
    public void The_dj_says_when_a_changeover_is_in_flight_so_the_transport_can_show_it()
    {
        // Pressing "next" takes a moment: the record must be loaded on the deck, and the one playing
        // must finish. Until then the transport can do nothing (a second press is ignored), so it
        // must be able to report that it is busy.
        var tracks = new[]
        {
            Track("one", 128, new MusicalKey(9, true), duration: 120, outro: 100),
            Track("two", 128, new MusicalKey(9, true), duration: 120, outro: 100),
        };

        using var dj = Booth(tracks);
        var block = new float[512 * Channels];
        var said = new List<bool>();
        dj.ChangingOverChanged += changing => said.Add(changing);

        double at = 0, pressed = 0, landed = 0;
        for (int i = 0; i < 40_000; i++)
        {
            if (dj.Read(block, 512) <= 0)
                break;
            at += 512.0 / Rate;

            if (said.Count == 0 && at > 5)
            {
                dj.SkipToNext();
                pressed = at;
            }
            if (said.Count == 2)
            {
                landed = at;
                break;
            }
        }

        Assert.Equal([true, false], said);
        Assert.True(pressed > 0 && landed > pressed, "the changeover never finished");

        // Busy for the whole of it: the wait for the bar line, then eight bars of the loop, which at
        // 128 BPM is fifteen seconds.
        Assert.InRange(landed - pressed, 14.0, 30.0);
    }

    [Fact]
    public void A_record_that_landed_pulled_is_let_back_up_to_its_own_tempo()
    {
        // 123 into 137: the pair meet in the middle at about 130, so the incoming record is 5% slow
        // during the changeover. Leaving it there would play the whole record at the wrong speed.
        var tracks = new[]
        {
            Track("slow", 123, new MusicalKey(9, true), duration: 120, outro: 100),
            Track("fast", 137, new MusicalKey(9, true), duration: 120, outro: 100),
        };

        using var dj = Booth(tracks);
        var block = new float[512 * Channels];

        double duringTheMix = 0, afterTheMix = 0;
        for (int i = 0; i < 40_000; i++)
        {
            if (dj.Read(block, 512) <= 0)
                break;
            if (dj.NowPlaying?.Title != "fast")
                continue;
            if (dj.IsTransitioning)
            {
                if (duringTheMix == 0)
                    duringTheMix = dj.PlayingBpm;
            }
            else
            {
                afterTheMix = dj.PlayingBpm;             // keeps the last reading before the set ends
            }
        }

        Assert.InRange(duringTheMix, 126, 133);          // held near the outgoing record's tempo
        Assert.Equal(137, afterTheMix, 1);               // and back on its own once it is alone
    }

    [Fact]
    public void An_analysis_that_lands_late_still_reaches_the_record_on_the_deck()
    {
        // The set starts before the analyser has finished; the DJ uses the analysis when it arrives.
        var unanalysed = Track("late", 0, MusicalKey.Unknown, duration: 120, analyzed: false);
        using var dj = Booth([unanalysed, Track("second", 128, new MusicalKey(9, true))]);

        var block = new float[512 * Channels];
        dj.Read(block, 512);
        Assert.False(dj.NowPlaying!.IsAnalyzed);

        dj.NoteAnalysis(unanalysed with { Analysis = Analysis(unanalysed.Path, 126, new MusicalKey(9, true), 120) });

        Assert.True(dj.NowPlaying!.IsAnalyzed);
        Assert.Equal(126, dj.NowPlaying.Bpm, 3);
    }

    [Fact]
    public void A_roll_in_hands_the_record_on_at_a_whole_bar()
    {
        // A roll-in loops a slice and then plays ON, and the deck continues from where the CAPTURE
        // ended. The beat roll's slices get shorter, to a beat and then to half a beat. A deck that
        // captured only the longest slice would use a single beat and return the record three beats
        // before its own bar line, for the rest of the record, on every skip that uses the beat roll
        // (half of them).
        //
        // So the capture length is given separately from the slices: loop a beat, take a bar.
        const int Bar = 8192;

        var record = new RampSource("in", startSeconds: 0, tempoRatio: 1);
        var deck = new Deck(Deck.Side.B, Rate, Channels, _ => record);
        deck.Load(Track("in", 128, new MusicalKey(9, true)), cueSeconds: 0, tempoRatio: 1);
        deck.Fader = 1;
        deck.Start();

        var steps = Rolls.In(RollStyle.Beat, Bar, bars: 4);
        Assert.True(steps.Max(step => step.SliceFrames) < Bar, "the beat roll should loop less than a bar");

        deck.RollIn(steps, captureFrames: Bar);

        // Play the whole roll out and one block past it.
        var block = new float[1024 * Channels];
        long rolled = steps.Sum(step => step.LengthFrames);
        for (long played = 0; played < rolled + 1024;)
        {
            int got = deck.Read(block, 1024);
            if (got <= 0) break;
            played += got;
        }

        // A bar was used, so the record is on its second bar, not on its second beat.
        Assert.Equal(Bar / (double)Rate, record.NextSecond - 1024.0 / Rate, 3);
        deck.Dispose();
    }

    [Fact]
    public void A_cued_record_stands_still_until_the_dj_lets_it_go()
    {
        // The DJ cues the next record up to twelve seconds before the changeover. A deck that played
        // during that wait (silently, fader down) would reach the mix twelve seconds after the cue
        // point chosen for it, so the intro it should skip would be played.
        var deck = new Deck(Deck.Side.B, Rate, Channels, _ => new ToneSource("t", 1f, 120, 1));
        deck.Load(Track("cued", 128, new MusicalKey(9, true)), cueSeconds: 30, tempoRatio: 1);
        deck.Fader = 1;

        var block = new float[1024 * Channels];
        for (int i = 0; i < 500; i++)                       // ~11 s of blocks
            Assert.Equal(0, deck.Read(block, 1024));

        Assert.False(deck.IsRunning);
        Assert.Equal(30, deck.PositionSeconds, 3);          // exactly where it was cued

        deck.Start();
        Assert.True(deck.Read(block, 1024) > 0);
        deck.Dispose();
    }

    [Fact]
    public void A_held_record_waits_the_exact_number_of_frames_before_its_first_sample()
    {
        // How the drop is placed on a sample rather than on a block boundary.
        var deck = new Deck(Deck.Side.A, Rate, Channels, _ => new ToneSource("t", 1f, 60, 1));
        deck.Load(Track("held", 128, new MusicalKey(9, true)), 0, 1);
        deck.Fader = 1;
        deck.Start(delayFrames: 3000);

        var block = new float[2048 * Channels];

        // First block: all silence, and the record has not moved.
        Assert.Equal(2048, deck.Read(block, 2048));
        Assert.All(block, sample => Assert.Equal(0, sample, 6));
        Assert.Equal(0, deck.PositionSeconds, 6);

        // Second: the last 952 frames of the hold are silence, and the record starts on the exact
        // sample where the hold ends, in this block, not at the start of the next one. Returning a
        // short block here would put the drop on the next block edge after the bar line, up to 50 ms
        // late, which at club tempo is a tenth of a beat.
        Assert.Equal(2048, deck.Read(block, 2048));
        Assert.All(block[..(952 * Channels)], sample => Assert.Equal(0, sample, 6));
        Assert.Contains(block[(952 * Channels)..], sample => Math.Abs(sample) > 1e-3);

        // And the position has moved by what it played, not by the whole block.
        Assert.Equal((2048 - 952) / (double)Rate, deck.PositionSeconds, 6);

        Assert.True(deck.Read(block, 2048) > 0);
        Assert.True(block[^1] != 0, "the record should be playing once the hold has passed");
        deck.Dispose();
    }

    [Fact]
    public void A_deck_can_change_speed_mid_record_without_losing_its_place_or_its_sound()
    {
        // Moving the pitch fader on the record already playing: the half of a beat-match the
        // outgoing deck is responsible for.
        var deck = new Deck(Deck.Side.A, Rate, Channels,
            options => new ToneSource("t", 1f, 300 - options.StartSeconds, options.TempoRatio));
        deck.Load(Track("riding", 120, new MusicalKey(9, true), duration: 300), cueSeconds: 0, tempoRatio: 1);
        deck.Start();
        deck.Fader = 1;

        var block = new float[1024 * Channels];
        for (int i = 0; i < 400; i++)                 // ~9 s in
            deck.Read(block, 1024);

        double before = deck.PositionSeconds;
        Assert.Equal(120, deck.PlayingBpm, 1);

        deck.SetTempo(0.97);

        // The position did not jump, and the record is now running slower.
        Assert.Equal(before, deck.PositionSeconds, 1);
        Assert.Equal(120 * 0.97, deck.PlayingBpm, 1);

        // And there is no hole where the decoder was swapped: the next blocks are full and audible.
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(1024, deck.Read(block, 1024));
            Assert.True(block[0] != 0, $"block {i} after the speed change came back silent");
        }

        Assert.True(deck.PositionSeconds > before, "and it carries on from where it was");
        deck.Dispose();
    }

    [Fact]
    public async Task A_speed_change_neither_repeats_nor_skips_the_audio_around_it()
    {
        // The stutter this test catches. Opening a decoder takes real time, and the record keeps
        // playing during that time, so a new source cued from the OLD position replays everything
        // heard in between. Reproduced realistically: the second open blocks until the reader has
        // read a few hundred milliseconds, as a real ffmpeg start does.
        var opening = new ManualResetEventSlim(false);
        int opens = 0;

        var deck = new Deck(Deck.Side.A, Rate, Channels, options =>
        {
            if (Interlocked.Increment(ref opens) > 1)
                opening.Wait(TimeSpan.FromSeconds(5));      // the slow start
            return new RampSource("ramp", options.StartSeconds, options.TempoRatio);
        });

        deck.Load(Track("ramp", 120, new MusicalKey(9, true), duration: 600), cueSeconds: 0, tempoRatio: 1);
        deck.Start();
        deck.Fader = 1;

        // Every sample carries its own position on the record, so the audio the reader gets is a log
        // of exactly what was played, in order.
        var heard = new List<double>();
        var stop = new ManualResetEventSlim(false);
        var reader = new Thread(() =>
        {
            var block = new float[512 * Channels];
            while (!stop.IsSet)
            {
                int read = deck.Read(block, 512);
                if (read > 0)
                    heard.Add(block[0]);
                Thread.Sleep(1);
            }
        });
        reader.Start();

        Thread.Sleep(60);                                    // let it play a little
        var change = Task.Run(() => deck.SetTempo(0.97));
        Thread.Sleep(120);                                   // …while the new decoder is "opening"
        opening.Set();
        await Task.WhenAny(change, Task.Delay(TimeSpan.FromSeconds(5)));
        Thread.Sleep(60);                                    // and a little after the swap

        stop.Set();
        reader.Join(TimeSpan.FromSeconds(2));
        deck.Dispose();

        Assert.True(heard.Count > 10, $"the reader only got {heard.Count} blocks");

        // Nothing was played twice and nothing was dropped: the positions only ever move forward, and
        // never by more than the gap a single swapped block can leave.
        for (int i = 1; i < heard.Count; i++)
        {
            double step = heard[i] - heard[i - 1];
            Assert.True(step >= 0, $"audio was replayed at block {i}: went back {-step * 1000:F0} ms");
            Assert.True(step < 0.25, $"audio was skipped at block {i}: jumped {step * 1000:F0} ms");
        }
    }

    [Fact]
    public void A_speed_change_does_not_click()
    {
        // The click on "next". A speed change resumes at the right position but not at the right
        // point in the waveform, so joining the old decoder to the new one leaves a step, and a step
        // is a click. Here every decoder produces the same tone half a cycle out of phase with the
        // last, which makes that step as large as possible: full scale, in one sample.
        var deck = new Deck(Deck.Side.A, Rate, Channels,
            options => new PhasedToneSource("tone", options.StartSeconds, options.TempoRatio));

        deck.Load(Track("tone", 120, new MusicalKey(9, true), duration: 600), cueSeconds: 0, tempoRatio: 1);
        deck.Start();
        deck.Fader = 1;

        var heard = new List<float>();
        var block = new float[512 * Channels];
        void Play(int blocks)
        {
            for (int i = 0; i < blocks; i++)
            {
                int read = deck.Read(block, 512);
                for (int f = 0; f < read; f++)
                    heard.Add(block[f * Channels]);
            }
        }

        Play(8);
        deck.SetTempo(0.97);
        Play(8);
        deck.Dispose();

        // A 220 Hz tone at 44.1 kHz moves by at most 0.016 per sample. A directly joined half-cycle
        // jump would be up to 1.0, about a hundred times more than the signal itself can move.
        float worst = 0;
        int worstAt = 0;
        for (int i = 1; i < heard.Count; i++)
        {
            float step = Math.Abs(heard[i] - heard[i - 1]);
            if (step > worst) { worst = step; worstAt = i; }
        }

        Assert.True(worst < 0.1f,
            $"the waveform jumped {worst:F3} in one sample at {worstAt} — that is the click");
    }

    [Fact]
    public void A_deck_knows_how_long_until_its_next_bar_line()
    {
        // A beat-matched drop is scheduled with this number: the incoming record is held for exactly
        // this long, so its own bar line (it is cued on one) falls on the outgoing record's bar line.
        var deck = new Deck(Deck.Side.A, Rate, Channels, _ => new ToneSource("t", 1f, 120, 1));

        // 120 BPM, four beats to the bar, gridded from zero: bar lines every 2 s.
        deck.Load(Track("gridded", 120, new MusicalKey(9, true), duration: 60), cueSeconds: 20.7, tempoRatio: 1);
        deck.Start();

        Assert.Equal(1.3, deck.SecondsToNextDownbeat!.Value, 2);   // next bar at 22.0 s

        deck.Dispose();
    }

    [Fact]
    public void A_pulled_deck_reaches_its_bar_line_sooner_in_real_time()
    {
        // The wait is clock time, not record time: a deck running 10% fast reaches its next bar line
        // 10% sooner, and scheduling the drop in record seconds would make it late.
        var deck = new Deck(Deck.Side.A, Rate, Channels, _ => new ToneSource("t", 1f, 120, 1.1));
        deck.Load(Track("pulled", 120, new MusicalKey(9, true), duration: 60), cueSeconds: 20.0, tempoRatio: 1.1);
        deck.Start();

        // Next bar is 2 s away on the record; at 1.1× that is 1.818 s of real time.
        Assert.Equal(2.0 / 1.1, deck.SecondsToNextDownbeat!.Value, 3);

        deck.Dispose();
    }

    [Fact]
    public void A_record_with_no_grid_has_no_bar_line_to_wait_for()
    {
        var deck = new Deck(Deck.Side.A, Rate, Channels, _ => new ToneSource("t", 1f, 120, 1));
        deck.Load(Track("no-grid", 0, MusicalKey.Unknown, analyzed: false), 0, 1);
        deck.Start();

        Assert.Null(deck.SecondsToNextDownbeat);   // which is what makes the DJ blend instead
        deck.Dispose();
    }

    [Fact]
    public void The_mix_reports_the_record_it_is_on_so_the_header_can_follow_it()
    {
        // This keeps the player's readout, its progress bar and the artwork working during a set: the
        // DJ answers "what is playing" with a real library path and that record's own position, not
        // with one unnamed stream that is 40 minutes long.
        var tracks = new[]
        {
            Track("first", 128, new MusicalKey(9, true), duration: 40, outro: 24),
            Track("second", 128, new MusicalKey(9, true), duration: 40, outro: 24),
        };

        using var dj = Booth(tracks);
        var block = new float[512 * Channels];
        dj.Read(block, 512);

        var now = dj.NowPlayingTrack;
        Assert.NotNull(now);
        Assert.Equal("/library/first.flac", now!.Value.Path);      // a path the library can match
        Assert.Equal("first", now.Value.Title);
        Assert.Equal(40, now.Value.DurationSeconds, 3);            // the record's length, not the set's
        Assert.InRange(now.Value.PositionSeconds, 0, 1);

        // Play until the set has changed records, then it must name the other record.
        for (int i = 0; i < 20_000 && dj.NowPlayingTrack?.Path == "/library/first.flac"; i++)
            if (dj.Read(block, 512) <= 0) break;

        Assert.Equal("/library/second.flac", dj.NowPlayingTrack?.Path);
        Assert.True(dj.NowPlayingTrack!.Value.PositionSeconds < 40, "the position should be into the new record");
    }

    [Fact]
    public void The_position_a_mix_reports_walks_forward_within_the_record()
    {
        using var dj = Booth([Track("only", 128, new MusicalKey(9, true), duration: 40, outro: 30)]);
        var block = new float[512 * Channels];

        dj.Read(block, 512);
        double first = dj.NowPlayingTrack!.Value.PositionSeconds;

        for (int i = 0; i < 200; i++)
            dj.Read(block, 512);

        double later = dj.NowPlayingTrack!.Value.PositionSeconds;
        Assert.True(later > first, $"position went {first:F2} → {later:F2}");
        Assert.True(later <= 40, "and never past the record's own length");
    }

    [Fact]
    public void How_hard_a_record_is_hitting_is_read_where_the_needle_is()
    {
        var analysis = Analysis("/x.flac", 128, new MusicalKey(9, true), 120) with
        {
            EnergyCurve = QuietThenLoud(120),
        };

        double inTheBreakdown = analysis.IntensityAt(20);
        double atThePeak = analysis.IntensityAt(100);

        Assert.True(atThePeak > inTheBreakdown + 0.3,
            $"a breakdown ({inTheBreakdown:F2}) should read far quieter than the peak ({atThePeak:F2})");
    }

    [Fact]
    public void Skipping_picks_a_record_for_where_the_set_is_not_for_the_outro()
    {
        // The playing record is quiet for its first half and loud for its second. Cued at the outro
        // it would want something big to follow; skipped from the breakdown it wants something quiet.
        var playing = new MixTrack
        {
            Source = "/library/playing.flac",
            Path = "/library/playing.flac",
            Title = "playing",
            TaggedDuration = 240,
            Analysis = Analysis("/library/playing.flac", 128, new MusicalKey(9, true), 240, outro: 200) with
            {
                EnergyCurve = QuietThenLoud(240),
            },
        };

        var loud = Track("loud", 128, new MusicalKey(9, true), duration: 240, intensity: 0.95);
        var quiet = Track("quiet", 128, new MusicalKey(9, true), duration: 240, intensity: 0.10);

        using var dj = Booth([playing, loud, quiet]);
        var heard = new List<string>();
        dj.TrackChanged += t => heard.Add(t.Title!);

        var block = new float[512 * Channels];
        for (int i = 0; i < 1_000; i++)             // ~12 s in, inside the breakdown
            dj.Read(block, 512);

        dj.SkipToNext();
        for (int i = 0; i < 20_000 && dj.NowPlaying?.Title == "playing"; i++)
            if (dj.Read(block, 512) <= 0) break;

        Assert.Equal("quiet", dj.NowPlaying!.Title);
        Assert.Equal(["playing", "quiet"], heard);
    }

    [Fact]
    public void A_record_cued_for_the_outro_goes_back_in_the_crate_when_the_set_is_skipped()
    {
        // Cued for the end of the record, then asked to change now: the choice is made again, and the
        // record that was cued is still available to play later, not lost from the set.
        var playing = Track("playing", 128, new MusicalKey(9, true), duration: 60, outro: 20);
        var first = Track("cued-first", 128, new MusicalKey(9, true), duration: 60, outro: 40);
        var other = Track("other", 128, new MusicalKey(9, true), duration: 60, outro: 40);

        using var dj = Booth([playing, first, other]);
        var block = new float[512 * Channels];

        // Far enough in that the next record has been cued for the outro (12 s of cue-ahead).
        for (int i = 0; i < 900; i++)
            dj.Read(block, 512);

        dj.SkipToNext();
        for (int i = 0; i < 40_000 && dj.Read(block, 512) > 0; i++) { }

        // In whatever order, all three records were played: the skip putting one back did not lose
        // it.
        Assert.Equal(3, dj.Played.Count);
        Assert.Contains(dj.Played, t => t.Title == "cued-first");
        Assert.Contains(dj.Played, t => t.Title == "other");
    }

    [Fact]
    public void Next_rolls_the_record_playing_out_and_the_next_one_has_the_room_at_the_end_of_it()
    {
        var tracks = new[]
        {
            Track("first", 128, new MusicalKey(9, true), duration: 120, outro: 100),
            Track("second", 128, new MusicalKey(9, true), duration: 120, intro: 20, outro: 100),
        };

        using var dj = Booth(tracks);
        var block = new float[512 * Channels];

        // A few seconds in, far from the outro at 100 s.
        for (int i = 0; i < 200; i++)
            dj.Read(block, 512);
        Assert.Equal("first", dj.NowPlaying!.Title);
        Assert.False(dj.IsTransitioning);

        dj.SkipToNext();
        double after = 0;
        for (int i = 0; i < 5_000 && dj.NowPlaying?.Title == "first"; i++)
        {
            if (dj.Read(block, 512) <= 0) break;
            after += 512.0 / Rate;
        }

        Assert.Equal("second", dj.NowPlaying!.Title);

        // Eight bars at 128 BPM is fifteen seconds, plus up to a four of waiting for the line to loop
        // from: the press was two seconds in and the record is left on one of its own fours. The
        // record is heard the whole time.
        double bar = 4 * 60.0 / 128;
        Assert.InRange(
            after,
            TransitionPlanner.RollOutBars * bar,
            (TransitionPlanner.RollOutBars + TrackAnalysis.PhraseBars + 1) * bar);

        // And the new record starts where it comes in (its intro's end, moved back to its four-bar
        // line), not after it, because there is nothing left for an intro to play under, and a
        // record started in the middle of a four is out of phrase.
        Assert.Equal(TransitionStyle.RollOut, dj.CurrentPlan!.Style);
        Assert.Equal(1, dj.CurrentPlan.TempoRatio);
        Assert.InRange(dj.CurrentPlan.CueInSeconds, 20 - TrackAnalysis.PhraseBars * bar, 20);
    }

    [Fact]
    public void The_record_coming_in_rides_up_over_the_last_bars_of_the_roll_out()
    {
        // The record being rolled out is silent, so anything heard at all is the new one coming in.
        // The two ends overlap by the length of the roll-in (four bars), so the new record is at full
        // level and playing at the sample the old loop stops, with no gap.
        var tracks = new[]
        {
            Track("first", 128, new MusicalKey(9, true), duration: 120, outro: 100),
            Track("second", 128, new MusicalKey(9, true), duration: 120, outro: 100),
        };

        using var dj = Booth(tracks, new Dictionary<string, float>
        {
            ["/library/first.flac"] = 0f,
            ["/library/second.flac"] = 1f,
        });

        var block = new float[512 * Channels];
        for (int i = 0; i < 200; i++)
            dj.Read(block, 512);
        Assert.Equal("first", dj.NowPlaying!.Title);

        dj.SkipToNext();

        double at = 0, heard = -1, landed = -1;
        for (int i = 0; i < 20_000; i++)
        {
            if (dj.Read(block, 512) <= 0)
                break;
            at += 512.0 / Rate;

            // Half scale: the riser rises over these same bars and never gets near it, so this is
            // the incoming record and not the noise under it.
            if (heard < 0 && block.Any(sample => Math.Abs(sample) > 0.5f))
                heard = at;
            if (dj.NowPlaying?.Title == "second")
            {
                landed = at;
                break;
            }
        }

        Assert.True(heard > 0, "the record coming in was never heard");
        Assert.True(landed > 0, "the changeover never finished");

        // Four bars at 128 BPM is 7.5 s. It is heard through most of them, not all: the fade starts
        // from zero, so its first fraction of a second is under any useful threshold.
        double bar = 4 * 60.0 / 128;
        Assert.InRange(landed - heard, 4 * bar * 0.6, 5 * bar);
    }

    [Fact]
    public void A_record_that_could_not_be_matched_waits_for_the_loop_to_finish()
    {
        // Same test the other way round: 128 into 100 cannot be pulled together, so the two are never
        // heard at once. The new record starts when the loop has stopped, not over it.
        var tracks = new[]
        {
            Track("first", 128, new MusicalKey(9, true), duration: 120, outro: 100),
            Track("second", 100, new MusicalKey(9, true), duration: 120, outro: 100),
        };

        using var dj = Booth(tracks, new Dictionary<string, float>
        {
            ["/library/first.flac"] = 0f,
            ["/library/second.flac"] = 1f,
        });

        var block = new float[512 * Channels];
        for (int i = 0; i < 200; i++)
            dj.Read(block, 512);

        dj.SkipToNext();

        double at = 0, heard = -1, landed = -1;
        for (int i = 0; i < 20_000; i++)
        {
            if (dj.Read(block, 512) <= 0)
                break;
            at += 512.0 / Rate;

            // Half scale: the riser rises over these same bars and never gets near it, so this is
            // the incoming record and not the noise under it.
            if (heard < 0 && block.Any(sample => Math.Abs(sample) > 0.5f))
                heard = at;
            if (dj.NowPlaying?.Title == "second")
            {
                landed = at;
                break;
            }
        }

        Assert.True(heard > 0 && landed > 0, "the changeover never finished");
        Assert.InRange(landed - heard, 0, 0.2);
    }

    [Fact]
    public void A_skip_brings_the_record_playing_onto_the_new_tempo_before_it_starts_the_transition()
    {
        // The pull is applied gradually, not in one step. Nothing of the changeover happens until the
        // two are actually at one tempo: a loop cut half way through the pull is not the length the
        // plan was made for, and the new record would start on a grid that is still changing.
        var tracks = new[]
        {
            Track("first", 128, new MusicalKey(9, true), duration: 300, outro: 260),
            Track("second", 134, new MusicalKey(9, true), duration: 300, outro: 260),
        };

        using var dj = Booth(tracks, sourceSeconds: 300);
        var block = new float[512 * Channels];
        for (int i = 0; i < 200; i++)
            dj.Read(block, 512);

        Assert.Equal(128, dj.PlayingBpm, 1);

        dj.SkipToNext();

        double met = Math.Sqrt(128 * 134.0);
        double at = 0, arrived = -1, started = -1;
        for (int i = 0; i < 200_000; i++)
        {
            if (dj.Read(block, 512) <= 0) break;
            at += 512.0 / Rate;

            if (arrived < 0 && Math.Abs(dj.PlayingBpm - met) < 0.1)
                arrived = at;
            if (dj.IsTransitioning)
            {
                started = at;
                break;
            }
        }

        Assert.True(arrived > 0, "the record never reached the tempo the changeover runs at");
        Assert.True(started > 0, "the transition never started");
        Assert.True(arrived <= started, "the transition started before the two tempos had met");

        // And it took bars to get there, not a block: four bars at 128 BPM is 7.5 s.
        double bar = 4 * 60.0 / 128;
        Assert.InRange(arrived, bar, TransitionPlanner.MatchInBars * bar + bar);
    }

    [Fact]
    public void A_record_reaching_its_own_outro_is_pulled_across_in_steps_too()
    {
        // For a mix too, the pull is applied gradually, the same as for a skip. Twelve seconds of
        // cue-ahead does not hide a sudden change: the listener may not be paying attention yet, but
        // can still hear it.
        var tracks = new[]
        {
            Track("first", 128, new MusicalKey(9, true), duration: 300, outro: 120),
            Track("second", 134, new MusicalKey(9, true), duration: 300, outro: 260),
        };

        using var dj = Booth(tracks, sourceSeconds: 300);
        var block = new float[512 * Channels];

        double met = Math.Sqrt(128 * 134.0);
        double was = 128, biggest = 0, at = 0, moved = -1, arrived = -1;

        for (int i = 0; i < 400_000; i++)
        {
            if (dj.Read(block, 512) <= 0)
                break;
            at += 512.0 / Rate;

            double now = dj.PlayingBpm;
            if (dj.NowPlaying?.Title != "first")
                break;                                  // the changeover has happened; stop measuring

            if (Math.Abs(now - was) > 1e-6)
            {
                if (moved < 0) moved = at;
                biggest = Math.Max(biggest, Math.Abs(now - was));
                was = now;
            }
            if (arrived < 0 && Math.Abs(now - met) < 0.1)
                arrived = at;
        }

        Assert.True(moved > 0, "the record was never pulled at all");
        Assert.True(arrived > 0, "the record never reached the tempo of the changeover");

        // Several steps over several seconds, and no single one of them is a jump: the whole pull is
        // 2.3%, and a step is limited to well under one percent of it.
        Assert.True(arrived - moved > 1, $"the whole pull happened in {arrived - moved:F2}s");
        Assert.True(biggest < 128 * 0.012, $"one step moved the record by {biggest:F2} BPM");
    }

    [Fact]
    public void The_scrubber_moves_the_needle_on_the_record_playing()
    {
        var tracks = new[]
        {
            Track("first", 128, new MusicalKey(9, true), duration: 300, outro: 260),
            Track("second", 128, new MusicalKey(9, true), duration: 300, outro: 260),
        };

        using var dj = Booth(tracks, sourceSeconds: 300);
        var block = new float[512 * Channels];
        for (int i = 0; i < 200; i++)
            dj.Read(block, 512);

        Assert.InRange(dj.PositionSeconds, 0, 5);

        Assert.True(dj.SeekTo(120));
        dj.Read(block, 512);

        Assert.InRange(dj.PositionSeconds, 120, 122);
        Assert.Equal("first", dj.NowPlaying!.Title);      // the same record, at another position
    }

    [Fact]
    public void A_record_scrubbed_into_its_last_minute_is_played_out_rather_than_mixed_out_of()
    {
        // There is no room for a sixteen-bar handover in what is left, and the listener has just
        // chosen where they want to be. So the record finishes and the next one starts, the way a
        // queue plays.
        var tracks = new[]
        {
            Track("first", 128, new MusicalKey(9, true), duration: 300, outro: 260),
            Track("second", 128, new MusicalKey(9, true), duration: 300, intro: 20, outro: 260),
        };

        using var dj = Booth(tracks, sourceSeconds: 300);
        var block = new float[512 * Channels];
        for (int i = 0; i < 200; i++)
            dj.Read(block, 512);

        // Thirty seconds from the end, inside the last minute.
        Assert.True(dj.SeekTo(270));

        // Keep reading until the hand-over is finished, not only started, or CurrentPlan is still the
        // plan that brought the FIRST record in.
        for (int i = 0; i < 200_000; i++)
        {
            if (dj.Read(block, 512) <= 0) break;
            if (dj.NowPlaying?.Title == "second" && !dj.IsTransitioning) break;
        }

        Assert.Equal("second", dj.NowPlaying!.Title);

        // A cut, not a mix, and the new record plays from its own beginning, because nothing was
        // mixed over its intro.
        Assert.Equal(TransitionStyle.Cut, dj.CurrentPlan!.Style);
        Assert.Equal(0, dj.CurrentPlan.CueInSeconds);

        // And it did not change early: the first record played to its end.
        Assert.True(dj.CurrentPlan.StartAtSeconds > 290,
            $"handed over at {dj.CurrentPlan.StartAtSeconds:F1}s of a 300 s record");
    }

    [Fact]
    public void Scrubbing_back_out_of_the_last_minute_puts_the_changeover_back_on()
    {
        var tracks = new[]
        {
            Track("first", 128, new MusicalKey(9, true), duration: 300, outro: 260),
            Track("second", 128, new MusicalKey(9, true), duration: 300, outro: 260),
        };

        using var dj = Booth(tracks, sourceSeconds: 300);
        var block = new float[512 * Channels];
        for (int i = 0; i < 200; i++)
            dj.Read(block, 512);

        Assert.True(dj.SeekTo(270));       // no room to mix
        Assert.True(dj.SeekTo(60));        // and back out again: there is room once more

        for (int i = 0; i < 200_000; i++)
        {
            if (dj.Read(block, 512) <= 0) break;
            if (dj.NowPlaying?.Title == "second" && !dj.IsTransitioning) break;
        }

        Assert.Equal("second", dj.NowPlaying!.Title);
        Assert.Equal(TransitionStyle.BeatMatched, dj.CurrentPlan!.Style);
    }

    [Fact]
    public void The_scrubber_is_refused_while_a_changeover_is_running()
    {
        // Two records are playing. There is no single position to move, and moving one of them would
        // break its sync with the other.
        var tracks = new[]
        {
            Track("first", 128, new MusicalKey(9, true), duration: 300, outro: 260),
            Track("second", 128, new MusicalKey(9, true), duration: 300, outro: 260),
        };

        using var dj = Booth(tracks, sourceSeconds: 300);
        var block = new float[512 * Channels];
        for (int i = 0; i < 200; i++)
            dj.Read(block, 512);

        dj.SkipToNext();
        while (!dj.IsTransitioning && dj.Read(block, 512) > 0) { }
        Assert.True(dj.IsTransitioning);

        Assert.False(dj.SeekTo(30));
    }

    [Fact]
    public void A_skip_climbs_under_noise_and_comes_down_over_the_record_that_lands()
    {
        // Both records are silent, so anything heard at all is the noise. It should rise through the
        // roll-out, still be there over the first bars of the new record, and be gone by the end of
        // them.
        var tracks = new[]
        {
            Track("first", 128, new MusicalKey(9, true), duration: 300, outro: 260),
            Track("second", 128, new MusicalKey(9, true), duration: 300, outro: 260),
        };

        using var dj = Booth(tracks, new Dictionary<string, float>
        {
            ["/library/first.flac"] = 0f,
            ["/library/second.flac"] = 0f,
        }, sourceSeconds: 300, chance: () => 0);

        var block = new float[512 * Channels];
        for (int i = 0; i < 200; i++)
            dj.Read(block, 512);
        Assert.All(block, sample => Assert.Equal(0, sample, 6));   // nothing but the records so far

        dj.SkipToNext();

        double bar = 4 * 60.0 / 128;
        double opening = 0, climbing = 0, falling = 0, afterwards = 0;
        double at = 0, landed = -1;

        for (int i = 0; i < 200_000; i++)
        {
            if (dj.Read(block, 512) <= 0)
                break;
            at += 512.0 / Rate;

            double peak = 0;
            foreach (var sample in block)
                peak = Math.Max(peak, Math.Abs(sample));

            if (landed < 0)
            {
                if (at < 4)
                    opening = Math.Max(opening, peak);
                climbing = Math.Max(climbing, peak);
                if (dj.NowPlaying?.Title == "second" && !dj.IsTransitioning)
                    landed = at;
                continue;
            }

            if (at < landed + bar)
                falling = Math.Max(falling, peak);
            else if (at > landed + TransitionPlanner.DropBars * bar + 0.5)
            {
                afterwards = Math.Max(afterwards, peak);
                if (at > landed + TransitionPlanner.DropBars * bar + 2)
                    break;
            }
        }

        Assert.True(landed > 0, "the changeover never finished");

        // It rose, and it started quietly, not loud from the first bar.
        Assert.True(climbing > 0.1, $"there was no riser: the loudest thing heard was {climbing:F3}");
        Assert.True(opening < climbing / 2, $"it opened at {opening:F3} of an eventual {climbing:F3}");

        // The fall is there over the incoming record.
        Assert.True(falling > 0.05, $"nothing came down over the new record: {falling:F3}");

        // And two bars later there is nothing left of any of it.
        Assert.True(afterwards < 0.01, $"noise was still going {afterwards:F3} well after the changeover");
    }

    [Fact]
    public void Most_skips_are_the_records_alone_with_no_noise_over_them()
    {
        // An effect that happens every time becomes the normal sound of the app, so the noise is only
        // added sometimes. The roll-out still happens (it is how the records change), and the noise
        // over it does not. Both records are silent, so any noise would be all that is heard.
        var tracks = new[]
        {
            Track("first", 128, new MusicalKey(9, true), duration: 300, outro: 260),
            Track("second", 128, new MusicalKey(9, true), duration: 300, outro: 260),
        };

        using var dj = Booth(tracks, new Dictionary<string, float>
        {
            ["/library/first.flac"] = 0f,
            ["/library/second.flac"] = 0f,
        }, sourceSeconds: 300, chance: () => 1);

        var block = new float[512 * Channels];
        for (int i = 0; i < 200; i++)
            dj.Read(block, 512);

        dj.SkipToNext();

        double loudest = 0, at = 0, landed = -1;
        for (int i = 0; i < 200_000; i++)
        {
            if (dj.Read(block, 512) <= 0)
                break;
            at += 512.0 / Rate;

            foreach (var sample in block)
                loudest = Math.Max(loudest, Math.Abs(sample));

            if (landed < 0 && dj.NowPlaying?.Title == "second" && !dj.IsTransitioning)
                landed = at;
            if (landed > 0 && at > landed + 5)
                break;
        }

        Assert.True(landed > 0, "the changeover never finished");
        Assert.True(loudest < 1e-4, $"there was noise over a changeover that asked for none: {loudest:F4}");
    }

    [Fact]
    public void A_rolled_out_record_plays_its_last_bar_over_and_over_and_then_stops()
    {
        // A tenth of a second is used as a bar, so the test is small. The source's samples carry their
        // own position on the record, which makes a repeat visible: the second time round the numbers
        // start again from where they started the first time.
        var deck = new Deck(Deck.Side.A, Rate, Channels,
            options => new RampSource("t", options.StartSeconds, options.TempoRatio));
        deck.Load(Track("rolling", 128, new MusicalKey(9, true)), 0, 1);
        deck.Fader = 1;
        deck.Start();

        const int bar = 4410;
        deck.RollOut([new RollStep(bar, bar * 3)], afterFrames: 0);

        var block = new float[bar * Channels];

        // Round one IS the record: it plays while it is captured.
        Assert.Equal(bar, deck.Read(block, bar));
        Assert.Equal(0, block[0], 4);

        // Rounds two and three are that bar again, from its first sample.
        for (int round = 2; round <= 3; round++)
        {
            Assert.Equal(bar, deck.Read(block, bar));
            Assert.Equal(0, block[0], 4);
            Assert.Equal((bar - 1) / (double)Rate, block[^Channels], 4);
        }

        // Then nothing. The record is finished, and the next one starts on this exact sample.
        Assert.Equal(bar, deck.Read(block, bar));
        Assert.All(block, sample => Assert.Equal(0, sample, 6));
        deck.Dispose();
    }

    [Fact]
    public void The_needle_goes_round_the_looped_bar_while_the_file_position_stands_still()
    {
        // What a display of a roll must show. The file position is correct but not useful: a loop is
        // a copy of a part the deck has already passed, so PositionSeconds stays still for the whole
        // roll (eight bars with no movement, during the part the listener is hearing). NeedleSeconds
        // goes round the bar instead, which is what is being played.
        var deck = new Deck(Deck.Side.A, Rate, Channels,
            options => new RampSource("t", options.StartSeconds, options.TempoRatio));
        deck.Load(Track("rolling", 128, new MusicalKey(9, true)), 0, 1);
        deck.Fader = 1;
        deck.Start();

        // About a tenth of a second used as a bar, and a whole number of quarters, so that four reads
        // return exactly to where they started.
        const int bar = 4408;
        double barSeconds = bar / (double)Rate;
        deck.RollOut([new RollStep(bar, bar * 3)], afterFrames: 0);

        var block = new float[bar * Channels];

        // Round one is the capture, and the capture IS the record: the file plays a bar of itself.
        Assert.Equal(bar, deck.Read(block, bar));
        double parked = deck.PositionSeconds;
        Assert.Equal(barSeconds, parked, 4);

        // From here the file position does not move. Read the loop a quarter of a bar at a time.
        int quarter = bar / 4;
        var small = new float[quarter * Channels];
        var seen = new List<double>();

        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(quarter, deck.Read(small, quarter));
            Assert.Equal(parked, deck.PositionSeconds, 6);           // the file: not moving
            seen.Add(deck.NeedleSeconds);
        }

        string walk = string.Join(", ", seen.Select(s => s.ToString("F4")));

        // The needle stays inside the bar that is looping, and it moves.
        Assert.All(seen, at => Assert.InRange(at, parked - barSeconds - 1e-6, parked + 1e-6));
        Assert.True(seen.Distinct().Count() > 1, $"the needle never moved: {walk}");

        // And it loops with the bar: four quarters later it is at the same place again.
        for (int i = 0; i + 4 < seen.Count; i++)
            Assert.Equal(seen[i], seen[i + 4], 6);

        deck.Dispose();
    }

    /// <summary>A drum and nothing else: a short burst every <c>period</c> frames and near silence in
    /// between, so a test can put a hit at a known place and ask where the loop was cut from.</summary>
    private sealed class HitSource : IPcmSource
    {
        private readonly long _first, _period;
        private long _frame;

        public HitSource(long firstHit, long period)
        {
            _first = firstHit;
            _period = period;
        }

        /// <summary>How long a hit lasts. Short: a stick, not a note.</summary>
        public const int HitFrames = 256;

        public string Label => "hit";

        public int Read(float[] buffer, int frames)
        {
            for (int f = 0; f < frames; f++)
            {
                long here = _frame + f;
                long since = here >= _first ? (here - _first) % _period : long.MaxValue;

                float level = since < HitFrames ? 0.6f : 0.002f;
                float value = (here & 1) == 0 ? level : -level;   // alternating, so it is a signal
                for (int c = 0; c < Channels; c++)
                    buffer[f * Channels + c] = value;
            }
            _frame += frames;
            return frames;
        }

        public void Dispose() { }
    }

    [Fact]
    public void A_loop_is_cut_from_the_hit_and_not_from_the_ruler()
    {
        // The grid is a straight line through beats a drummer did not play evenly, so its bar line can
        // be a few milliseconds away from the real hit. Cut on the grid, the slice starts part way
        // through the hit's decay and its seam falls in the middle of the next one. Cut on the hit,
        // the seam falls where an attack hides it.
        const int wait = 4410, slice = 4410, early = 800;

        var deck = new Deck(Deck.Side.A, Rate, Channels, _ => new HitSource(wait - early, slice));
        deck.Load(Track("rolling", 128, new MusicalKey(9, true)), 0, 1);
        deck.Fader = 1;
        deck.Start();

        deck.RollOut([new RollStep(slice, slice * 3)], afterFrames: wait);

        // The wait and the capture.
        var block = new float[(wait + slice) * Channels];
        Assert.Equal(wait + slice, deck.Read(block, wait + slice));

        // The second time round is the captured slice, so the position of the hit in it shows where
        // the slice was cut from.
        var round = new float[slice * Channels];
        Assert.Equal(slice, deck.Read(round, slice));

        int firstHit = -1;
        for (int f = 0; f < slice && firstHit < 0; f++)
            if (Math.Abs(round[f * Channels]) > 0.1)
                firstHit = f;

        Assert.True(firstHit >= 0, "the loop has no hit in it at all");

        // Cut on the grid, the hit would be 800 frames from the END of the slice, about 3,600 frames
        // in. Cut on the hit, it is at the start, within the width of the window the rise is measured
        // over.
        Assert.True(firstHit < 512, $"the loop opens {firstHit} frames before its hit, not on it");

        deck.Dispose();
    }

    [Fact]
    public void A_loop_with_nothing_struck_in_it_is_cut_where_the_grid_says()
    {
        // The adjustment is also a check: a part with no hit in it has no better place to be cut than
        // the one the grid gives, and guessing one would be worse than not moving.
        var deck = new Deck(Deck.Side.B, Rate, Channels,
            options => new RampSource("t", options.StartSeconds, options.TempoRatio));
        deck.Load(Track("even", 128, new MusicalKey(9, true)), 0, 1);
        deck.Fader = 1;
        deck.Start();

        const int wait = 4410, slice = 4410;
        deck.RollOut([new RollStep(slice, slice * 3)], afterFrames: wait);

        var block = new float[(wait + slice) * Channels];
        Assert.Equal(wait + slice, deck.Read(block, wait + slice));

        // The ramp never rises sharply, so nothing reads as a hit and the slice starts on the line:
        // the second time round starts at exactly the position of the line.
        var round = new float[slice * Channels];
        Assert.Equal(slice, deck.Read(round, slice));
        Assert.Equal(wait / (double)Rate, round[0], 4);

        deck.Dispose();
    }

    [Fact]
    public void A_beat_roll_plays_a_shorter_slice_at_every_stage()
    {
        // The source's samples carry their own position on the record, so a slice starting again is
        // visible: the numbers go back to where that slice began. Read a beat at a time, because the
        // fader ramps across the first block it is given and every block after it is at full level.
        var deck = new Deck(Deck.Side.A, Rate, Channels,
            options => new RampSource("t", options.StartSeconds, options.TempoRatio));
        deck.Load(Track("rolling", 128, new MusicalKey(9, true)), 0, 1);
        deck.Fader = 1;
        deck.Start();

        const int beat = 1102;
        deck.RollOut([new RollStep(2 * beat, 8 * beat), new RollStep(beat, 2 * beat)], afterFrames: 0);

        var block = new float[beat * Channels];
        var opens = new List<double>();

        for (int i = 0; i < 11; i++)
        {
            Array.Clear(block);
            Assert.Equal(beat, deck.Read(block, beat));
            opens.Add(block[0]);
        }

        // Beats 0 and 1 are the two-beat slice being captured as it plays, so they are continuous.
        Assert.Equal(0, opens[0], 4);
        Assert.Equal(beat / (double)Rate, opens[1], 4);

        // Then it loops: every other beat is the slice starting again.
        for (int i = 2; i < 8; i += 2)
        {
            Assert.Equal(0, opens[i], 4);
            Assert.Equal(beat / (double)Rate, opens[i + 1], 4);
        }

        // And at eight beats the stage changes to one beat, so now EVERY beat starts it again.
        Assert.Equal(0, opens[8], 4);
        Assert.Equal(0, opens[9], 4);

        // Ten beats is the whole schedule. Nothing after it.
        Assert.All(block[..0], _ => { });
        Array.Clear(block);
        Assert.Equal(beat, deck.Read(block, beat));
        Assert.All(block, sample => Assert.Equal(0, sample, 6));
        deck.Dispose();
    }

    [Fact]
    public void A_rolled_in_record_plays_its_first_bar_over_and_over_and_then_plays_on()
    {
        var deck = new Deck(Deck.Side.B, Rate, Channels,
            options => new RampSource("t", options.StartSeconds, options.TempoRatio));
        deck.Load(Track("arriving", 128, new MusicalKey(9, true)), 0, 1);
        deck.Fader = 1;
        deck.Start();

        const int bar = 4410;
        deck.RollIn([new RollStep(bar, bar * 3)]);

        var block = new float[bar * Channels];

        for (int round = 1; round <= 3; round++)
        {
            Assert.Equal(bar, deck.Read(block, bar));
            Assert.Equal(0, block[0], 4);
        }

        // And then the record continues from where the capture ended: its second bar, not its fourth.
        // Looping a record in must not skip the part it was looping into.
        Assert.Equal(bar, deck.Read(block, bar));
        Assert.Equal(bar / (double)Rate, block[0], 4);
        deck.Dispose();
    }

    [Fact]
    public void Pressing_next_during_a_changeover_does_not_queue_a_second_skip()
    {
        var tracks = new[]
        {
            Track("one", 128, new MusicalKey(9, true), duration: 120, outro: 100),
            Track("two", 128, new MusicalKey(9, true), duration: 120, outro: 100),
            Track("three", 128, new MusicalKey(9, true), duration: 120, outro: 100),
        };

        using var dj = Booth(tracks);
        var heard = new List<string>();
        dj.TrackChanged += t => heard.Add(t.Title!);
        var block = new float[512 * Channels];

        for (int i = 0; i < 200; i++)
            dj.Read(block, 512);

        dj.SkipToNext();
        while (!dj.IsTransitioning && dj.Read(block, 512) > 0) { }
        Assert.True(dj.IsTransitioning);

        // A second press during the fade. The changeover it asks for is the one already running.
        dj.SkipToNext();
        for (int i = 0; i < 2_000 && dj.Read(block, 512) > 0; i++) { }

        Assert.Equal(["one", "two"], heard);
        Assert.Equal("two", dj.NowPlaying!.Title);
    }

    [Fact]
    public void A_skip_off_a_record_the_analyser_has_not_reached_still_goes_straight_through()
    {
        // Early in a set the analyser has not finished. Next does not mix, so it needs no bar line
        // and does not wait for a reading on a record with no grid.
        var unanalysed = Track("no-grid-yet", 0, MusicalKey.Unknown, duration: 240, analyzed: false);
        var next = Track("next", 128, new MusicalKey(9, true), duration: 240, outro: 200);

        using var dj = Booth([unanalysed, next]);
        var block = new float[512 * Channels];
        for (int i = 0; i < 200; i++)
            dj.Read(block, 512);
        Assert.Equal("no-grid-yet", dj.NowPlaying!.Title);

        dj.SkipToNext();

        double after = 0;
        for (int i = 0; i < 2_000 && dj.NowPlaying?.Title == "no-grid-yet"; i++)
        {
            if (dj.Read(block, 512) <= 0) break;
            after += 512.0 / Rate;
        }

        Assert.Equal("next", dj.NowPlaying!.Title);
        Assert.InRange(after, 0, 1.0);
    }

    [Fact]
    public void Previous_drops_the_needle_back_on_the_record_playing()
    {
        using var dj = Booth([Track("only", 128, new MusicalKey(9, true), duration: 120, outro: 100)]);
        var block = new float[512 * Channels];

        for (int i = 0; i < 400; i++)
            dj.Read(block, 512);
        double before = dj.PositionSeconds;
        Assert.True(before > 1, $"should be into the record, was {before:F2}s");

        dj.RestartRecord();
        dj.Read(block, 512);

        Assert.Equal("only", dj.NowPlaying!.Title);
        Assert.True(dj.PositionSeconds < before, $"went {before:F2}s → {dj.PositionSeconds:F2}s");
    }

    [Fact]
    public void A_record_that_runs_out_early_hands_over_instead_of_ending_the_set()
    {
        // The outro is past the end of the file (a bad cue point, or a track shorter than its tags
        // say). The first record just stops; the set must continue and not stop with it.
        var shortRecord = Track("stops-early", 128, new MusicalKey(9, true), duration: 200, outro: 190);
        var second = Track("carries-on", 128, new MusicalKey(9, true), duration: 200, outro: 190);

        // The tone source only has 120 s of audio however long the record claims to be.
        using var dj = Booth([shortRecord, second]);
        var heard = new List<string>();
        dj.TrackChanged += t => heard.Add(t.Title!);

        var block = new float[512 * Channels];
        for (int i = 0; i < 100_000 && dj.Read(block, 512) > 0; i++) { }

        Assert.Equal(["stops-early", "carries-on"], heard);
        Assert.True(dj.HasFinished);
    }

    [Fact]
    public void A_record_promoted_after_a_run_out_is_announced_like_any_other()
    {
        // The seam case: the outgoing record stops before its changeover can start, so the cued one
        // is promoted, not faded in. It must still be announced: if the player shows a different
        // record from the one being heard, that is a silent failure, because the audio is fine.
        var stopsEarly = Track("stops-early", 128, new MusicalKey(9, true), duration: 300, outro: 280);
        var cued = Track("cued", 128, new MusicalKey(9, true), duration: 300, outro: 280);

        // The tone sources only have 120 s whatever length the records claim, so the first one runs
        // out long before its outro.
        using var dj = Booth([stopsEarly, cued]);
        var heard = new List<string>();
        dj.TrackChanged += t => heard.Add(t.Title!);

        var block = new float[512 * Channels];
        for (int i = 0; i < 100_000 && dj.Read(block, 512) > 0; i++) { }

        Assert.Equal(["stops-early", "cued"], heard);
    }

    [Fact]
    public void An_empty_crate_ends_the_set_without_playing_anything()
    {
        using var dj = Booth([]);
        var block = new float[512 * Channels];

        Assert.Equal(0, dj.Read(block, 512));
        Assert.True(dj.HasFinished);
    }

    [Fact]
    public void A_records_position_is_reported_in_its_own_seconds_however_it_is_pulled()
    {
        var deck = new Deck(Deck.Side.A, Rate, Channels,
            options => new ToneSource("t", 1f, 60, options.TempoRatio));
        var track = Track("stretched", 120, new MusicalKey(9, true));

        // Cued 30 s in and 10% fast: after one real second the position has moved 1.1 s on the
        // record, so it reads 31.1. The cue points are measured against this.
        deck.Load(track, cueSeconds: 30, tempoRatio: 1.1);
        deck.Start();
        deck.Fader = 1;
        var block = new float[Rate * Channels];
        deck.Read(block, Rate);

        Assert.Equal(31.1, deck.PositionSeconds, 2);
        Assert.Equal(132, deck.PlayingBpm, 1);
        deck.Dispose();
    }

    [Fact]
    public void A_deck_with_its_fader_down_is_silent_and_one_up_is_not()
    {
        var deck = new Deck(Deck.Side.B, Rate, Channels, _ => new ToneSource("t", 1f, 10, 1));
        deck.Load(Track("quiet", 120, MusicalKey.Unknown), 0, 1);
        deck.Start();

        var block = new float[256 * Channels];
        deck.Fader = 0;
        deck.Read(block, 256);
        Assert.All(block, sample => Assert.Equal(0, sample, 6));

        deck.Fader = 1;
        deck.Read(block, 256);
        // The ramp starts from the fader's last value, so the block ends at full level.
        Assert.Equal(1.0, block[^1], 3);
        deck.Dispose();
    }
}
