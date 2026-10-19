using RPM.Core.Analysis;
using RPM.Core.Mixing;

namespace RPM.Core.Tests;

/// <summary>
/// The phrase blend: the standard beat-matched changeover, started on the start of a
/// phrase rather than on any old four, and run for two phrases.
///
/// <para>A phrase is TrackAnalysis.PhraseBeats: thirty-two beats, eight bars of a record counted in
/// four, so two of them is sixteen bars. Deliberately NOT PhraseGrid.Bars, which is counted in bars
/// and is the span the record repeats over; only its phase is used. The two are easy to confuse
/// because both are written 8, 16 and 32, and confusing them makes a sixty-four bar changeover.</para>
///
/// <para>Its own mode, not a change to either auto mode (see MixPolicy.PhraseBlend).</para>
/// </summary>
public class PhraseBlendTests
{
    private const int Bpm = 120;                    // a beat every half second, a bar every two
    private const int BeatsPerBar = 4;

    private static readonly MixPolicy Blend = new() { PhraseBlend = true };

    /// <summary>
    /// A record with a grid the whole way and a phrase of <paramref name="phraseBars"/> bars whose
    /// first boundary is <paramref name="firstBar"/> bars in.
    /// </summary>
    private static MixTrack Track(
        string name, double duration, double outro, int phraseBars = 32, int firstBar = 0,
        double intro = 0, bool phrased = true, Func<double, bool>? singingAt = null, double bpm = Bpm,
        IReadOnlyList<double>? drops = null, float? bassLevel = null, float? energyLevel = null)
    {
        var beats = new List<double>();
        for (double at = 0; at < duration; at += 60.0 / bpm)
            beats.Add(at);

        // Bands, when a test wants the vocal search to have something to read. The beat runs the whole
        // way and the voice is on wherever the caller says it is.
        var voice = new List<float>();
        var bass = new List<float>();
        if (singingAt is not null)
            for (double at = 0; at < duration; at += EnergyProfile.StepSeconds)
            {
                voice.Add(singingAt(at) ? 0.8f : 0.05f);
                bass.Add(0.8f);
            }
        else if (bassLevel is { } level)
            for (double at = 0; at < duration; at += EnergyProfile.StepSeconds)
                bass.Add(level);

        var energy = new List<float> { 0.3f, 0.3f };
        if (energyLevel is { } loud)
        {
            energy.Clear();
            for (double at = 0; at < duration; at += EnergyProfile.StepSeconds)
                energy.Add(loud);
        }

        string path = $"/library/{name}.flac";
        return new MixTrack
        {
            Source = path,
            Path = path,
            Title = name,
            TaggedDuration = duration,
            Analysis = new TrackAnalysis
            {
                FilePath = path,
                DurationSeconds = duration,
                Bpm = bpm,
                BpmConfidence = 0.9,
                BeatTimes = beats,
                FirstDownbeat = 0,
                BeatsPerBar = BeatsPerBar,
                Key = new MusicalKey(9, true),
                KeyConfidence = 0.8,
                EnergyCurve = energy,
                IntroEndSeconds = intro,
                OutroStartSeconds = outro,
                VocalCurve = voice,
                BassCurve = bass,
                DropSeconds = drops ?? [],
                Phrases = phrased ? new PhraseGrid(phraseBars, firstBar, 1) : default,
            },
        };
    }

    /// <summary>Which bar of the outgoing record a time falls on, counted from its first downbeat.
    /// One bar is two seconds at 120.</summary>
    private static double BarOf(double seconds) => seconds / 2.0;

    [Fact]
    public void The_changeover_starts_on_a_phrase_line_and_not_merely_on_a_four()
    {
        // A measured phase of 3 bars: phrases begin at bars 3, 11, 19, 27... The outro reading lands
        // at bar 100, which is a four and is NOT the start of a phrase.
        var outgoing = Track("out", duration: 300, outro: 200, phraseBars: 32, firstBar: 3);
        var incoming = Track("in", duration: 300, outro: 280);

        var four = TransitionPlanner.Plan(outgoing, Bpm, incoming);
        var phrase = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Blend });

        // The ordinary plan lands on a four, and on this record that is mid-phrase.
        Assert.Equal(0, BarOf(four.StartAtSeconds) % 4, 6);
        Assert.NotEqual(0, (BarOf(four.StartAtSeconds) - 3) % 8, 6);

        // The phrase blend lands on the start of a phrase.
        Assert.Equal(0, (BarOf(phrase.StartAtSeconds) - 3) % 8, 6);
        Assert.NotEqual(four.StartAtSeconds, phrase.StartAtSeconds);
    }

    /// <summary>Two phrases is sixteen bars, whatever the record was measured to repeat over. The
    /// repeat supplies the phase and never the length.</summary>
    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void It_runs_for_two_phrases_which_is_sixteen_bars(int repeatBars)
    {
        var outgoing = Track("out", duration: 300, outro: 130, phraseBars: repeatBars);
        var incoming = Track("in", duration: 300, outro: 280);

        var plan = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Blend });

        Assert.Equal(TransitionStyle.PhraseBlend, plan.Style);
        Assert.Equal(16, plan.FadeBars);
        Assert.Equal(16 * plan.BarSeconds, plan.FadeSeconds, 3);
    }

    /// <summary>A phrase is thirty-two beats, so eight bars of a record counted in four.</summary>
    [Fact]
    public void How_many_phrases_is_a_knob()
    {
        var outgoing = Track("out", duration: 400, outro: 150);
        var incoming = Track("in", duration: 300, outro: 280);

        Assert.Equal(2, MixPolicy.Default.BlendPhrases);

        var plan = TransitionPlanner.Plan(
            outgoing, Bpm, incoming,
            new MixOverrides { Policy = new MixPolicy { PhraseBlend = true, BlendPhrases = 4 } });

        Assert.Equal(32, plan.FadeBars);
    }

    /// <summary>
    /// A record nobody measured a phrase on is planned exactly as the ordinary Auto plans it.
    /// There is nothing to line up on, and a phrase guessed at is worse than a four that is known.
    /// </summary>
    [Fact]
    public void A_record_with_no_measured_phrase_falls_back_to_the_fours()
    {
        var outgoing = Track("out", duration: 300, outro: 200, phrased: false);
        var incoming = Track("in", duration: 300, outro: 280);

        var ordinary = TransitionPlanner.Plan(outgoing, Bpm, incoming);
        var asked = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Blend });

        Assert.Equal(ordinary.StartAtSeconds, asked.StartAtSeconds, 6);
        Assert.Equal(ordinary.FadeBars, asked.FadeBars);
        Assert.Contains(asked.Trace, t => t.Contains("no measured phrase"));
    }

    /// <summary>
    /// The mode does not make a beat-match out of a pair that has none. Alignment is the whole of what
    /// it changes, and there is nothing to align two records that will not meet.
    /// </summary>
    [Fact]
    public void A_pair_that_cannot_be_matched_is_still_a_blend()
    {
        var outgoing = Track("out", duration: 300, outro: 200);
        var incoming = Track("in", duration: 300, outro: 280) with { };
        incoming = incoming with
        {
            Analysis = incoming.Analysis! with { Bpm = 0, BpmConfidence = 0, BeatTimes = [] },
        };

        var plan = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Blend });

        Assert.NotEqual(TransitionStyle.BeatMatched, plan.Style);
        Assert.Contains(plan.Trace, t => t.Contains("cannot be beat-matched"));
    }

    /// <summary>
    /// A record without a blend's worth of body in front of its outro does not get a short blend: it
    /// does not get this mix at all. A shortened phrase blend is not a small version of the move; it is
    /// the ordinary changeover with a different name.
    /// </summary>
    [Fact]
    public void A_record_without_the_body_for_it_does_not_get_this_mix()
    {
        // An outro only 20 s in: nowhere near the 32 s that sixteen bars wants.
        var outgoing = Track("out", duration: 220, outro: 20, phraseBars: 32, firstBar: 3);
        var incoming = Track("in", duration: 300, outro: 280, intro: 33);

        var ordinary = TransitionPlanner.Plan(outgoing, Bpm, incoming);
        var plan = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Blend });

        Assert.Contains(plan.Trace, t => t.Contains("no phrase blend"));

        // What comes back is the changeover the planner would have made anyway, in every respect.
        Assert.Equal(ordinary.StartAtSeconds, plan.StartAtSeconds, 6);
        Assert.Equal(ordinary.FadeBars, plan.FadeBars);
        Assert.Equal(ordinary.CueInSeconds, plan.CueInSeconds, 6);
    }

    /// <summary>And it is the whole blend that has to fit, not whatever was left after the other
    /// clamps: the question is whether this record can hold the move at all.</summary>
    [Fact]
    public void A_record_with_exactly_the_body_for_it_still_gets_it()
    {
        // Sixteen bars is 32 s, and the outro is at 32 s exactly.
        var outgoing = Track("out", duration: 220, outro: 32, phraseBars: 32, firstBar: 0);
        var incoming = Track("in", duration: 300, outro: 280);

        var plan = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Blend });

        Assert.DoesNotContain(plan.Trace, t => t.Contains("no phrase blend"));
        Assert.Equal(16, plan.FadeBars);
        Assert.Equal(0, plan.StartAtSeconds, 6);
    }

    /// <summary>A length requested by the user still beats the mode: the phrase decides where it starts,
    /// and the box decides how long it runs.</summary>
    [Fact]
    public void A_length_asked_for_by_hand_still_wins()
    {
        var outgoing = Track("out", duration: 300, outro: 130, phraseBars: 32, firstBar: 3);
        var incoming = Track("in", duration: 300, outro: 280);

        var plan = TransitionPlanner.Plan(
            outgoing, Bpm, incoming,
            new MixOverrides { Policy = Blend, FadeBars = 4 });

        Assert.Equal(4, plan.FadeBars);
        Assert.Equal(0, (BarOf(plan.StartAtSeconds) - 3) % 8, 6);
    }

    [Fact]
    public void The_two_records_are_brought_onto_one_tempo_exactly_as_before()
    {
        var outgoing = Track("out", duration: 300, outro: 130);
        var incoming = Track("in", duration: 300, outro: 280);

        var plan = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Blend });

        // Same tempo on both decks, so neither is pulled and nothing drifts.
        Assert.Equal(1, plan.TempoRatio, 6);
        Assert.Equal(1, plan.OutgoingTempoRatio, 6);
        Assert.Equal(0, plan.TempoMismatch, 9);
    }

    // ── the rule: neither end is mixed over ──────────────────────────────────

    /// <summary>
    /// The blend finishes where the outro begins rather than starting there. Both records are in their
    /// bodies for the whole of it.
    /// </summary>
    [Fact]
    public void The_blend_ends_before_the_outro_rather_than_running_into_it()
    {
        var outgoing = Track("out", duration: 300, outro: 200, phraseBars: 32, firstBar: 3);
        var incoming = Track("in", duration: 300, outro: 280);

        var ordinary = TransitionPlanner.Plan(outgoing, Bpm, incoming);
        var plan = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Blend });

        Assert.True(
            plan.StartAtSeconds + plan.FadeSeconds <= 200 + 1e-6,
            $"the blend runs to {plan.StartAtSeconds + plan.FadeSeconds:F1}s, past the outro at 200s");

        // And the ordinary changeover does the opposite, which is what it is for.
        Assert.True(ordinary.StartAtSeconds >= 200 - 1e-6 || ordinary.StartAtSeconds > plan.StartAtSeconds);
    }

    /// <summary>The record coming in starts where its own music does, never underneath it.</summary>
    [Fact]
    public void The_record_coming_in_starts_after_its_intro_and_not_under_it()
    {
        var outgoing = Track("out", duration: 300, outro: 200);
        var incoming = Track("in", duration: 300, outro: 280, phraseBars: 32, firstBar: 2, intro: 33);

        var ordinary = TransitionPlanner.Plan(outgoing, Bpm, incoming);
        var plan = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Blend });

        Assert.True(
            plan.CueInSeconds >= 33 - 1e-6,
            $"cued at {plan.CueInSeconds:F1}s, inside an intro that runs to 33s");

        // On the start of one of its own phrases, not merely on a bar.
        Assert.Equal(0, (BarOf(plan.CueInSeconds) - 2) % 8, 6);

        // The ordinary changeover cues BEFORE the intro end on purpose: the intro is what it fades over.
        Assert.True(ordinary.CueInSeconds < 33);
    }

    /// <summary>A user request still beats the rule. It is what the mode does, not a law.</summary>
    [Fact]
    public void A_cue_asked_for_by_hand_still_wins()
    {
        var outgoing = Track("out", duration: 300, outro: 200);
        var incoming = Track("in", duration: 300, outro: 280, intro: 33);

        var plan = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Blend, CueInSeconds = 10 });

        Assert.True(plan.CueInSeconds < 33);
    }

    // ── the bias: away from the singing ──────────────────────────────────────

    /// <summary>
    /// Two records can share drums and cannot share voices. Given a stretch with the singing stopped a
    /// few phrases behind the back of the record, the blend moves back onto it.
    /// </summary>
    [Fact]
    public void The_blend_moves_back_onto_a_stretch_with_no_singing_over_it()
    {
        // Singing everywhere except 96..164 s, a break with the drums still going. Wide enough to hold
        // a phrase-aligned window: a 32 s blend on 16 s phrase lines needs a break of 32 + 16 to be
        // sure one fits inside it, which is a real constraint and not a quirk of this fixture.
        var outgoing = Track(
            "out", duration: 300, outro: 200, phraseBars: 32, firstBar: 0,
            singingAt: at => at < 96 || at > 164);
        var incoming = Track("in", duration: 300, outro: 280);

        var plan = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Blend });

        // Without the bias it would sit at the phrase line under 200 - 32 = 168 s, which is 160 and is
        // under the last chorus.
        Assert.True(
            plan.StartAtSeconds >= 96 && plan.StartAtSeconds + plan.FadeSeconds <= 164 + 1e-6,
            $"blended at {plan.StartAtSeconds:F1}s for {plan.FadeSeconds:F1}s, outside the 96-164 s break");
        Assert.True(plan.StartAtSeconds < 160, "it should have moved back off the chorus");

        Assert.Contains(plan.Trace, t => t.Contains("voices: out "));
    }

    /// <summary>A record singing all the way through has nowhere better to go, and blends where it was
    /// already going to rather than being dragged somewhere worse.</summary>
    [Fact]
    public void A_record_with_a_voice_on_every_bar_blends_at_the_back_as_before()
    {
        var outgoing = Track(
            "out", duration: 300, outro: 200, phraseBars: 32, singingAt: _ => true);
        var incoming = Track("in", duration: 300, outro: 280);

        var plan = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Blend });

        // The latest phrase line that still leaves 32 s in front of the outro at 200: 160, not 168,
        // the blend is always held to a phrase line, whether or not the voice moved it.
        Assert.Equal(160, plan.StartAtSeconds, 6);
        Assert.Contains(plan.Trace, t => t.Contains("nowhere better within reach"));
    }

    /// <summary>The search is bounded. A break an entire side of the record away is not worth the
    /// music it would cost to reach.</summary>
    [Fact]
    public void It_will_not_walk_the_whole_record_to_find_a_break()
    {
        // Quiet only right at the top, far more than PhrasesBack behind the back of the record.
        var outgoing = Track(
            "out", duration: 300, outro: 200, phraseBars: 32, singingAt: at => at > 40);
        var incoming = Track("in", duration: 300, outro: 280);

        var plan = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Blend });

        double back = (200 - plan.FadeSeconds) - plan.StartAtSeconds;
        Assert.True(
            back <= MixOutFinder.PhrasesBack * outgoing.Analysis!.PhraseSeconds + 1e-6,
            $"walked {back:F1}s back, past the {MixOutFinder.PhrasesBack} phrases it is allowed");
    }

    /// <summary>And never back into the outgoing record's own intro: the rule holds at both ends of
    /// both records.</summary>
    [Fact]
    public void The_search_never_reaches_back_into_the_outgoing_records_intro()
    {
        var outgoing = Track(
            "out", duration: 200, outro: 120, phraseBars: 32, intro: 60,
            singingAt: at => at > 20);
        var incoming = Track("in", duration: 300, outro: 280);

        var plan = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Blend });

        Assert.True(
            plan.StartAtSeconds >= 60 - 1e-6,
            $"blended at {plan.StartAtSeconds:F1}s, inside an intro that runs to 60 s");
    }

    // ── the lock: one tempo while both are audible ───────────────────────────

    /// <summary>
    /// Two records within reach of each other meet exactly, and "exactly" is the word: nothing is left
    /// over to walk the grids apart with.
    /// </summary>
    [Fact]
    public void Two_records_that_meet_are_locked_to_one_tempo_for_the_whole_blend()
    {
        var outgoing = Track("out", duration: 300, outro: 200);
        var incoming = Track("in", duration: 300, outro: 280);

        var plan = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Blend });

        Assert.Equal(TransitionStyle.PhraseBlend, plan.Style);
        Assert.True(
            plan.TempoMismatch <= TransitionPlanner.TempoLockTolerance,
            $"{plan.TempoMismatch:P6} apart, which the two decks would walk over the blend");

        // Both decks land on the same number of beats a minute, which is the whole of what "locked"
        // means: outgoing at its own tempo times its pull, incoming at its own times its own.
        double outAt = Bpm * plan.OutgoingTempoRatio;
        double inAt = incoming.Analysis!.Bpm * plan.TempoRatio;
        Assert.Equal(outAt, inAt, 9);
    }

    /// <summary>
    /// And a pair the pitch faders cannot close does not get this changeover at all. The ordinary
    /// changeover budgets for the drift and shortens itself; this one has no barely-audible ends to
    /// hide a drift in, so it gives way instead.
    /// </summary>
    [Fact]
    public void A_pair_the_faders_cannot_close_does_not_get_a_phrase_blend()
    {
        // 120 against 150 is a quarter apart. Meeting in the middle is 134.2, which is 11.8% off each
        // of them, and the pull is capped at 10%.
        var outgoing = Track("out", duration: 300, outro: 200);
        var incoming = Track("in", duration: 300, outro: 280, bpm: 150);

        var plan = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Blend });

        Assert.Contains(plan.Trace, t => t.Contains("no phrase blend") && t.Contains("locked"));

        // What comes back is whatever the planner would have made for that pair anyway.
        var ordinary = TransitionPlanner.Plan(outgoing, Bpm, incoming);
        Assert.Equal(ordinary.Style, plan.Style);
        Assert.Equal(ordinary.FadeBars, plan.FadeBars);
    }

    // ── the second changeover: a bass swap when both are heavy ───────────────

    /// <summary>Two records both carrying a lot down low have one bassline too many for a blend, so the
    /// mode makes a bass swap instead.</summary>
    [Fact]
    public void Two_records_both_heavy_down_low_get_a_bass_swap()
    {
        // 0.35 raw is about 0.90 rescaled, comfortably over the bar.
        var outgoing = Track("out", duration: 400, outro: 300, bassLevel: 0.35f);
        var incoming = Track("in", duration: 400, outro: 380, bassLevel: 0.35f);

        var plan = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Blend });

        Assert.Equal(TransitionStyle.BassSwap, plan.Style);
        Assert.Contains(plan.Trace, t => t.Contains("both heavy down low"));

        // Three phrases, and the swap across the middle one.
        Assert.Equal(TransitionPlanner.BassSwapPhrases * 8, plan.FadeBars);
        Assert.NotNull(plan.Arc);
        Assert.Equal(1 / 3.0, plan.Arc.BassSwapStart, 3);
    }

    /// <summary>One thin record is room enough to lay the other over, so the blend stands.</summary>
    [Fact]
    public void One_record_light_down_low_keeps_the_blend()
    {
        var outgoing = Track("out", duration: 400, outro: 300, bassLevel: 0.35f);
        var incoming = Track("in", duration: 400, outro: 380, bassLevel: 0.05f);

        var plan = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Blend });

        Assert.Equal(TransitionStyle.PhraseBlend, plan.Style);
        Assert.Contains(plan.Trace, t => t.Contains("not both heavy down low"));
    }

    /// <summary>A record read before the bands were measured is not evidence of anything, so nothing is
    /// concluded from it.</summary>
    [Fact]
    public void A_record_with_no_bands_measured_keeps_the_blend()
    {
        var outgoing = Track("out", duration: 400, outro: 300);
        var incoming = Track("in", duration: 400, outro: 380);

        var plan = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Blend });

        Assert.Equal(TransitionStyle.PhraseBlend, plan.Style);
    }

    /// <summary>A changeover requested by the user still beats the judgement.</summary>
    [Fact]
    public void A_style_asked_for_by_hand_outranks_the_bass_reading()
    {
        var outgoing = Track("out", duration: 400, outro: 300, bassLevel: 0.35f);
        var incoming = Track("in", duration: 400, outro: 380, bassLevel: 0.35f);

        var plan = TransitionPlanner.Plan(
            outgoing, Bpm, incoming,
            new MixOverrides { Policy = Blend, Style = TransitionStyle.FilterFade });

        Assert.Equal(TransitionStyle.FilterFade, plan.Style);
    }

    // ── the drop swap ────────────────────────────────────────────────────────

    /// <summary>
    /// The record playing runs to its drop and stops there. The record coming in is cued at its own
    /// drop and starts there. No overlap: one beat, and the two drops are the same moment.
    /// </summary>
    [Fact]
    public void The_records_change_on_their_two_drops_with_no_overlap()
    {
        // A phrase is 8 bars = 16 s at 120. A drop at 160 s is on a phrase line.
        var outgoing = Track(
            "out", duration: 400, outro: 300, drops: [160], energyLevel: 0.20f);
        var incoming = Track(
            "in", duration: 400, outro: 380, drops: [90], energyLevel: 0.60f);

        var plan = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Blend });

        Assert.Equal(TransitionStyle.DoubleDrop, plan.Style);
        Assert.Contains(plan.Trace, t => t.Contains("drop swap"));

        // The record playing stops on its own drop.
        Assert.Equal(160, plan.StartAtSeconds, 3);

        // The record coming in starts on its own, rather than being cued a changeover in front of it.
        Assert.Equal(90, plan.CueInSeconds, 3);

        // One beat across, and nothing else.
        Assert.Equal(plan.BarSeconds / 4, plan.FadeSeconds, 4);
        Assert.Equal(0, plan.FadeBars);
        Assert.Null(plan.Arc);
    }

    /// <summary>Only when the room is going up. Swapping a drop for a smaller one throws away the
    /// better of the two.</summary>
    [Fact]
    public void A_record_coming_in_softer_than_the_one_playing_gets_no_drop_swap()
    {
        var outgoing = Track("out", duration: 400, outro: 300, drops: [160], energyLevel: 0.60f);
        var incoming = Track("in", duration: 400, outro: 380, drops: [90], energyLevel: 0.20f);

        var plan = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Blend });

        Assert.NotEqual(TransitionStyle.DoubleDrop, plan.Style);
        Assert.DoesNotContain(plan.Trace, t => t.Contains("drop swap"));
    }

    /// <summary>A drop that does not fall on a phrase line is a drop that arrives while the room is
    /// still counting, so it is not swapped onto.</summary>
    [Fact]
    public void A_drop_off_the_phrase_line_is_not_swapped_onto()
    {
        // 166 s is three bars into a phrase, not on one.
        var outgoing = Track("out", duration: 400, outro: 300, drops: [166], energyLevel: 0.20f);
        var incoming = Track("in", duration: 400, outro: 380, drops: [90], energyLevel: 0.60f);

        var plan = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Blend });

        Assert.NotEqual(TransitionStyle.DoubleDrop, plan.Style);
    }

    /// <summary>And a record playing with no drop of its own has nothing to swap.</summary>
    [Fact]
    public void A_record_with_no_drop_of_its_own_gets_no_swap()
    {
        var outgoing = Track("out", duration: 400, outro: 300, energyLevel: 0.20f);
        var incoming = Track("in", duration: 400, outro: 380, drops: [90], energyLevel: 0.60f);

        var plan = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Blend });

        Assert.NotEqual(TransitionStyle.DoubleDrop, plan.Style);
    }

    [Fact]
    public void The_default_policy_is_unchanged_by_any_of_this()
    {
        Assert.False(MixPolicy.Default.PhraseBlend);
        Assert.Equal(2, MixPolicy.Default.BlendPhrases);
        Assert.True(MixPolicy.Default.IsDefault);
    }

    // ── what the mode may reach ───────────────────────────────────────────────
    //
    // This mode is Auto X in the workbench: everything the ordinary Auto makes, laid out on the
    // phrasing. What it must NOT do is reach for any of the four moves taken from other rooms that are
    // not finished: the cut on the one, the filter fade, the echo out and the spinback. Those belong
    // to MixPolicy.Techniques, which is a separate flag and a separate mode.
    //
    // "Reach for" rather than "make", and the distinction is the whole of it: Auto gets to the echo out
    // by itself on a record it cannot read, so a mode that promises everything Auto makes has to be
    // allowed the same one. What is being caught is a move this mode found that Auto did not.
    //
    // The two tests below are a pair: the first says this mode reaches none of them on its own, the
    // second says that the records used here would get them from the other mode. Without the second,
    // the first could pass because the pairs were dull.

    /// <summary>The four moves a set is not to be given until somebody finishes them.</summary>
    private static readonly TransitionStyle[] Unfinished =
    [
        TransitionStyle.Slam,
        TransitionStyle.FilterFade,
        TransitionStyle.EchoOut,
        TransitionStyle.Spinback,
    ];

    /// <summary>
    /// Three pairs, each chosen because the technique catalogue has a strong opinion about it: two
    /// records still going hard where they meet, a steady pair at a tempo the rewind belongs to, and
    /// a pair with no tempo in common at all.
    /// </summary>
    private static IEnumerable<(string What, MixTrack Out, MixTrack In)> TemptingPairs()
    {
        yield return (
            "two busy records",
            Track("busy-out", duration: 400, outro: 300, energyLevel: 0.80f, bassLevel: 0.80f),
            Track("busy-in", duration: 400, outro: 380, intro: 20, energyLevel: 0.80f, bassLevel: 0.80f));

        yield return (
            "a steady pair",
            Track("steady-out", duration: 400, outro: 300),
            Track("steady-in", duration: 400, outro: 380, intro: 20));

        var loose = Track("loose-in", duration: 400, outro: 380);
        yield return (
            "no tempo in common",
            Track("loose-out", duration: 400, outro: 300),
            loose with { Analysis = loose.Analysis! with { Bpm = 0, BpmConfidence = 0, BeatTimes = [] } });
    }

    /// <summary>Luck at its lowest, which is what the catalogue reads before it reaches for a gesture:
    /// under MixTechniques.TheatricalChance the echo out and the spinback are both on the table.</summary>
    private const double Unlucky = 0;

    /// <summary>
    /// Auto X reaches no unfinished move that the ordinary Auto would not have reached anyway.
    ///
    /// <para>
    /// <b>The result is compared with Auto, not with a fixed list.</b> A flat rule of "never any of
    /// the four" is not what the mode promises. Auto X is everything Auto makes, laid out on the
    /// phrasing, and Auto itself plays an echo out for one unreadable record in four: the record
    /// PLAYING carries the grid the echo is thrown on, so the move is available with no tempo in
    /// common at all. See <c>MixLabTests.One_unreadable_record_in_four_gets_an_echo_out</c>, which
    /// tests that behaviour.
    /// </para>
    ///
    /// <para>
    /// So the question is which of the four came from the MODE. Every one of them that Auto did not
    /// make on the same pair is one this mode reached for on its own, which is the fault to catch.
    /// The pairs still prove it, because two of the three get a technique from the mode that
    /// owns them and neither gets one from Auto.
    /// </para>
    /// </summary>
    [Fact]
    public void It_never_reaches_the_moves_that_are_not_finished()
    {
        foreach (var (what, outgoing, incoming) in TemptingPairs())
        {
            var mode = TransitionPlanner.Plan(
                outgoing, Bpm, incoming, new MixOverrides { Policy = Blend }, luck: Unlucky).Style;

            if (!Unfinished.Contains(mode)) continue;

            var auto = TransitionPlanner.Plan(outgoing, Bpm, incoming, luck: Unlucky).Style;

            Assert.True(
                mode == auto,
                $"{what}: this mode made a {mode} where the ordinary Auto made a {auto}, so it reached " +
                $"for an unfinished changeover of its own");
        }
    }

    /// <summary>The other half of the pair above: these records really are the ones the catalogue
    /// reaches for its unfinished moves on, so the test that says this mode does not is saying
    /// something.</summary>
    [Fact]
    public void And_the_same_records_do_get_them_from_the_mode_that_owns_them()
    {
        var suited = new MixPolicy { Techniques = true };

        var reached = TemptingPairs()
            .Select(pair => TransitionPlanner.Plan(
                pair.Out, Bpm, pair.In, new MixOverrides { Policy = suited }, luck: Unlucky).Style)
            .Where(Unfinished.Contains)
            .ToList();

        Assert.NotEmpty(reached);
    }

    // ── the alignment on its own ──────────────────────────────────────────────
    //
    // MixPolicy.PhraseMoves off: the phrase layout, and none of the changeovers it makes possible.
    // This is the workbench's Auto X, which is sold as the ordinary Auto laid out on the phrasing,
    // so it has to make what Auto makes and nothing else. The two moves below are the phrase mode's
    // own, and each is tested here on the very records that DO get it with the moves left on, a few
    // tests up this file.

    /// <summary>What Auto X hands the planner.</summary>
    private static readonly MixPolicy Aligned = new() { PhraseBlend = true, PhraseMoves = false };

    [Fact]
    public void With_the_moves_off_a_pair_of_drops_is_still_only_a_blend()
    {
        // The pair the drop swap fires on: both drops on a phrase line, the record coming in the
        // harder of the two.
        var outgoing = Track("out", duration: 400, outro: 300, drops: [160], energyLevel: 0.20f);
        var incoming = Track("in", duration: 400, outro: 380, drops: [96], energyLevel: 0.60f);

        var swapped = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Blend });
        var aligned = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Aligned });

        Assert.Equal(TransitionStyle.DoubleDrop, swapped.Style);
        Assert.Equal(TransitionStyle.PhraseBlend, aligned.Style);
        Assert.DoesNotContain(aligned.Trace, t => t.Contains("drop swap"));
    }

    [Fact]
    public void With_the_moves_off_two_heavy_records_are_still_only_a_blend()
    {
        var outgoing = Track("out", duration: 400, outro: 300, bassLevel: 0.80f);
        var incoming = Track("in", duration: 400, outro: 380, intro: 20, bassLevel: 0.80f);

        var swapped = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Blend });
        var aligned = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Aligned });

        Assert.Equal(TransitionStyle.BassSwap, swapped.Style);
        Assert.Equal(TransitionStyle.PhraseBlend, aligned.Style);
    }

    /// <summary>And the alignment itself is untouched by it: this is the same changeover the mode
    /// always made, in the same place, for the same length.</summary>
    [Fact]
    public void The_moves_being_off_changes_nothing_about_the_blend_itself()
    {
        var outgoing = Track("out", duration: 300, outro: 200, phraseBars: 32, firstBar: 3);
        var incoming = Track("in", duration: 300, outro: 280);

        var blend = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Blend });
        var aligned = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Aligned });

        Assert.Equal(TransitionStyle.PhraseBlend, aligned.Style);
        Assert.Equal(blend.StartAtSeconds, aligned.StartAtSeconds, 6);
        Assert.Equal(blend.FadeBars, aligned.FadeBars);
        Assert.Equal(blend.CueInSeconds, aligned.CueInSeconds, 6);
    }

    /// <summary>A hand picking one of the two out of the style box still gets it. The switch is about
    /// what the mode chooses on its own, not about what the app can make.</summary>
    [Fact]
    public void A_move_asked_for_by_hand_is_not_touched_by_the_switch()
    {
        var outgoing = Track("out", duration: 400, outro: 300, bassLevel: 0.80f);
        var incoming = Track("in", duration: 400, outro: 380, intro: 20, bassLevel: 0.80f);

        var plan = TransitionPlanner.Plan(
            outgoing, Bpm, incoming,
            new MixOverrides { Policy = Aligned, Style = TransitionStyle.BassSwap });

        Assert.Equal(TransitionStyle.BassSwap, plan.Style);
    }

    [Fact]
    public void The_moves_are_on_unless_a_mode_says_otherwise()
    {
        Assert.True(MixPolicy.Default.PhraseMoves);
    }
}

