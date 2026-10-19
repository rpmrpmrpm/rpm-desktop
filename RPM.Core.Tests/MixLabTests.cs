using RPM.Core.Analysis;
using RPM.Core.Audio;
using RPM.Core.Cloud;
using RPM.Core.Mixing;

namespace RPM.Core.Tests;

/// <summary>
/// The workbench: the grid as something a person can move, the catalogue of what the mixer reads, and
/// the overrides a person can put on top of a plan.
/// </summary>
public class MixLabTests
{
    // ── the grid, moved by hand ───────────────────────────────────────────────

    [Fact]
    public void A_regridded_record_counts_at_the_tempo_it_was_given()
    {
        var before = Analysis(bpm: 120, duration: 60);
        var after = GridEdit.Regrid(before, bpm: 128, downbeatSeconds: 0, beatsPerBar: 4);

        Assert.Equal(128, after.Bpm, 3);

        // A straight line, which is the only kind of grid the readers downstream can walk.
        double period = 60.0 / 128;
        for (int i = 1; i < after.BeatTimes.Count; i++)
            Assert.Equal(period, after.BeatTimes[i] - after.BeatTimes[i - 1], 6);
    }

    [Fact]
    public void The_time_named_as_the_downbeat_is_a_downbeat()
    {
        var before = Analysis(bpm: 120, duration: 60);

        // Deliberately not on the old grid, and not at zero: the point of naming one is to move the
        // phase to somewhere the tracker did not put it.
        const double named = 3.37;
        var after = GridEdit.Regrid(before, bpm: 120, downbeatSeconds: named, beatsPerBar: 4);

        // The bar line at or before the named time IS the named time.
        Assert.Equal(named, after.BarAtOrBefore(named), 6);
        Assert.Equal(named, after.BarAtOrAfter(named), 6);

        // And a bar later is exactly a bar later.
        Assert.Equal(named + 4 * 60.0 / 120, after.NextDownbeatAfter(named) ?? -1, 6);
    }

    [Fact]
    public void A_record_counted_in_three_puts_its_bar_lines_three_beats_apart()
    {
        var after = GridEdit.Regrid(Analysis(bpm: 120, duration: 60), 120, 0, beatsPerBar: 3);

        Assert.Equal(3, after.BeatsPerBar);
        Assert.Equal(3 * 60.0 / 120, after.NextDownbeatAfter(0) ?? -1, 6);
    }

    [Fact]
    public void The_octave_can_be_halved_and_doubled_without_moving_the_bar_line()
    {
        var before = GridEdit.Regrid(Analysis(bpm: 140, duration: 90), 140, downbeatSeconds: 1.5, 4);
        double bar = GridEdit.FirstDownbeatSeconds(before);

        var halved = GridEdit.Octave(before, 0.5);
        Assert.Equal(70, halved.Bpm, 3);
        Assert.Equal(bar, GridEdit.FirstDownbeatSeconds(halved), 6);

        var back = GridEdit.Octave(halved, 2);
        Assert.Equal(140, back.Bpm, 3);
        Assert.Equal(bar, GridEdit.FirstDownbeatSeconds(back), 6);
    }

    [Fact]
    public void A_nudge_moves_the_whole_line_by_what_it_was_given()
    {
        var before = GridEdit.Regrid(Analysis(bpm: 120, duration: 60), 120, 0, 4);
        var after = GridEdit.NudgePhase(before, 0.020);

        Assert.Equal(
            GridEdit.FirstDownbeatSeconds(before) + 0.020,
            GridEdit.FirstDownbeatSeconds(after),
            6);
    }

    [Fact]
    public void Steadiness_is_given_up_when_the_ruler_is_replaced()
    {
        var before = Analysis(bpm: 120, duration: 60) with { TempoStability = 0.8 };
        var after = GridEdit.Regrid(before, 124, 0, 4);

        // The value was the distance between the heard beats and the old ruler. There is a new ruler
        // and the heard beats are not kept, so the only honest answer is that nobody has measured this.
        Assert.Equal(TrackShape.Unmeasured, after.TempoStability);
        Assert.True(after.IsSteadyEnoughToHold);
    }

    [Fact]
    public void Moving_a_cue_leaves_the_other_one_alone()
    {
        var before = Analysis(bpm: 120, duration: 200) with { IntroEndSeconds = 8, OutroStartSeconds = 180 };

        var moved = GridEdit.WithCues(before, outroStartSeconds: 150);
        Assert.Equal(8, moved.IntroEndSeconds, 3);
        Assert.Equal(150, moved.OutroStartSeconds, 3);
    }

    [Fact]
    public void What_was_moved_is_reported_against_what_was_measured()
    {
        var before = Analysis(bpm: 120, duration: 60);
        var after = GridEdit.Regrid(before, 124, 0.25, 3);

        var diff = GridEdit.Diff(before, after);
        Assert.Contains(diff, d => d.Contains("bpm", StringComparison.Ordinal));
        Assert.Contains(diff, d => d.Contains("beatsPerBar", StringComparison.Ordinal));
        Assert.Contains(diff, d => d.Contains("downbeat", StringComparison.Ordinal));

        // And a reading nobody touched reports nothing, so an untouched record never reaches a journal.
        Assert.Empty(GridEdit.Diff(before, before));
    }

    // ── a hand edit is one person's opinion, and stays here ───────────────────

    [Fact]
    public void An_edited_reading_is_marked_as_one()
    {
        var before = Analysis(bpm: 120, duration: 60);
        Assert.False(before.IsEdited);

        Assert.True(GridEdit.Regrid(before, 124, 0, 4).IsEdited);
        Assert.True(GridEdit.Octave(before, 2).IsEdited);
        Assert.True(GridEdit.WithCues(before, introEndSeconds: 4).IsEdited);
    }

    [Fact]
    public void An_edited_reading_is_never_published_to_the_pool()
    {
        var measured = Analysis(bpm: 120, duration: 60);
        Assert.NotEmpty(AnalysisFacts.From(measured));

        // The same record after somebody moved its bar line. It would reach a stranger as a
        // measurement, and nothing in a fact could ever tell them it was not one.
        Assert.Empty(AnalysisFacts.From(GridEdit.DownbeatAt(measured, 1.25)));
    }

    // ── the catalogue ─────────────────────────────────────────────────────────

    [Fact]
    public void Steadiness_is_reported_as_measured_and_half_wired_up()
    {
        var track = Track(Analysis(bpm: 120, duration: 200) with { TempoStability = 0.31 });

        // MixTechniques reads it: a record measured loose is refused the bass swap and the double
        // drop. TransitionPlanner does not read it, which is what Partly means: the fade length of
        // every ordinary changeover is decided as though both records hold their tempo for the whole
        // sixteen bars.
        var signal = MixInputs.For(track).Single(s => s.Name == nameof(TrackAnalysis.TempoStability));
        Assert.Equal(MixUse.Partly, signal.Use);
        Assert.True(signal.Measured);

        // So it is not a measurement being thrown away, and it is not counted as one.
        Assert.DoesNotContain(MixInputs.Wasted(track), s => s.Name == nameof(TrackAnalysis.TempoStability));
        Assert.Contains(MixInputs.Unused(track), s => s.Name == nameof(TrackAnalysis.TempoStability));
    }

    [Fact]
    public void A_signal_nobody_measured_is_not_counted_as_wasted()
    {
        // Unmeasured steadiness costs nothing and proves nothing: it is not a measurement being
        // thrown away: it is a measurement nobody took.
        var track = Track(Analysis(bpm: 120, duration: 200));

        Assert.DoesNotContain(MixInputs.Wasted(track), s => s.Name == nameof(TrackAnalysis.TempoStability));
        Assert.Contains(MixInputs.Unused(track), s => s.Name == nameof(TrackAnalysis.TempoStability));
    }

    [Fact]
    public void The_tempo_and_the_grid_are_reported_as_read()
    {
        var signals = MixInputs.For(Track(Analysis(bpm: 120, duration: 200)));

        Assert.Equal(MixUse.Used, signals.Single(s => s.Name == nameof(TrackAnalysis.Bpm)).Use);
        Assert.Equal(MixUse.Used, signals.Single(s => s.Name == nameof(TrackAnalysis.BeatTimes)).Use);
        Assert.Equal(MixUse.Used, signals.Single(s => s.Name == nameof(TrackAnalysis.OutroStartSeconds)).Use);
    }

    [Fact]
    public void A_record_nobody_read_still_produces_the_whole_catalogue()
    {
        var signals = MixInputs.For(new MixTrack { Source = "s", Path = "p", TaggedDuration = 100 });

        Assert.NotEmpty(signals);
        Assert.All(signals, s => Assert.False(s.Measured));
        Assert.Empty(MixInputs.Wasted(null));
    }

    // ── the trace ─────────────────────────────────────────────────────────────

    [Fact]
    public void Every_plan_says_how_it_got_there()
    {
        var plan = TransitionPlanner.Plan(
            Track(Analysis(bpm: 128, duration: 300)), 128, Track(Analysis(bpm: 126, duration: 300)));

        Assert.NotEmpty(plan.Trace);
        Assert.Contains(plan.Trace, t => t.Contains("meet at", StringComparison.Ordinal));
    }

    [Fact]
    public void A_pair_that_cannot_be_matched_says_why_not()
    {
        var plan = TransitionPlanner.Plan(
            Track(Analysis(bpm: 128, duration: 300)),
            128,
            Track(Analysis(bpm: 0, duration: 300)));

        Assert.Contains(plan.Trace, t => t.Contains("cannot beat-match", StringComparison.Ordinal));
    }

    // ── overrides ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_shorter_changeover_asked_for_is_the_changeover_that_is_played()
    {
        var outgoing = Track(Analysis(bpm: 128, duration: 400));
        var incoming = Track(Analysis(bpm: 128, duration: 400));

        var planned = TransitionPlanner.Plan(outgoing, 128, incoming);
        Assert.Equal(TransitionPlanner.OutroFadeBars, planned.FadeBars);

        var asked = TransitionPlanner.Plan(outgoing, 128, incoming, new MixOverrides { FadeBars = 4 });
        Assert.Equal(4, asked.FadeBars);
        Assert.Equal(4 * asked.BarSeconds, asked.FadeSeconds, 3);
        Assert.Contains(asked.Trace, t => t.Contains("asked for 4 bars", StringComparison.Ordinal));
    }

    [Fact]
    public void A_changeover_the_record_has_no_room_for_is_cut_down_and_says_so()
    {
        // A short record: thirty-two bars at this tempo runs well past the end of it.
        var outgoing = Track(Analysis(bpm: 128, duration: 40, outro: 20));
        var incoming = Track(Analysis(bpm: 128, duration: 400));

        var asked = TransitionPlanner.Plan(outgoing, 128, incoming, new MixOverrides { FadeBars = 32 });

        Assert.True(asked.FadeBars < 32);
        Assert.True(asked.FadeSeconds <= outgoing.DurationSeconds);
        Assert.Contains(asked.Trace, t => t.Contains("32 bars asked for", StringComparison.Ordinal));
    }

    [Fact]
    public void A_style_the_records_cannot_support_is_refused_rather_than_relabelled()
    {
        var outgoing = Track(Analysis(bpm: 128, duration: 300));
        var incoming = Track(Analysis(bpm: 0, duration: 300));       // nothing to match against

        var asked = TransitionPlanner.Plan(
            outgoing, 128, incoming, new MixOverrides { Style = TransitionStyle.BeatMatched });

        Assert.NotEqual(TransitionStyle.BeatMatched, asked.Style);
        Assert.Contains(asked.Trace, t => t.Contains("refused", StringComparison.Ordinal));
    }

    [Fact]
    public void A_blend_asked_for_is_a_blend_even_where_a_mix_was_possible()
    {
        var outgoing = Track(Analysis(bpm: 128, duration: 400));
        var incoming = Track(Analysis(bpm: 128, duration: 400));

        Assert.Equal(TransitionStyle.BeatMatched, TransitionPlanner.Plan(outgoing, 128, incoming).Style);

        var asked = TransitionPlanner.Plan(
            outgoing, 128, incoming, new MixOverrides { Style = TransitionStyle.Blend });
        Assert.Equal(TransitionStyle.Blend, asked.Style);
    }

    [Fact]
    public void What_was_asked_for_is_kept_on_the_plan_even_where_it_was_cut_down()
    {
        var outgoing = Track(Analysis(bpm: 128, duration: 40, outro: 20));
        var incoming = Track(Analysis(bpm: 128, duration: 400));

        var asked = TransitionPlanner.Plan(outgoing, 128, incoming, new MixOverrides { FadeBars = 32 });

        // "The DJ wanted 32 and the arithmetic allowed fewer" has to survive as a fact about the
        // arithmetic, so the request is kept beside the result rather than replaced by it.
        Assert.Equal(32, asked.Overrides?.FadeBars);
        Assert.True(asked.FadeBars < 32);
    }

    [Fact]
    public void A_plan_nobody_touched_carries_no_overrides()
    {
        var plan = TransitionPlanner.Plan(
            Track(Analysis(bpm: 128, duration: 400)), 128, Track(Analysis(bpm: 128, duration: 400)));

        Assert.Null(plan.Overrides);
    }

    // ── the record coming in reaches full at its drop ─────────────────────────

    /// <summary>
    /// An intro longer than the changeover is cued a fade's worth before its drop, so the drop lands
    /// at the end of the fade and the ordinary equal-power curve is right. Nothing to do.
    /// </summary>
    [Fact]
    public void A_long_intro_keeps_the_ordinary_curve()
    {
        // 15s of fade at 128, against a 40s intro: there is room to cue it back.
        var outgoing = Track(Analysis(bpm: 128, duration: 400));
        var incoming = Track(Analysis(bpm: 128, duration: 400) with { IntroEndSeconds = 40 });

        var plan = TransitionPlanner.Plan(outgoing, 128, incoming);

        Assert.Null(plan.Arc);
        Assert.InRange(
            incoming.Analysis!.IntroEndSeconds - plan.CueInSeconds,
            plan.FadeSeconds - plan.BarSeconds, plan.FadeSeconds);
    }

    /// <summary>
    /// An intro shorter than the changeover cannot be cued far enough back (there is no record
    /// before its first sample), so the record comes up over its intro instead of over the fade, and
    /// its drop still lands at full.
    /// </summary>
    [Fact]
    public void A_short_intro_brings_the_record_up_over_its_intro_rather_than_over_the_fade()
    {
        var outgoing = Track(Analysis(bpm: 128, duration: 400));
        var incoming = Track(Analysis(bpm: 128, duration: 400) with { IntroEndSeconds = 6 });

        var plan = TransitionPlanner.Plan(outgoing, 128, incoming);

        // The changeover keeps its length: what gives way is the shape, not the mix.
        Assert.Equal(TransitionPlanner.OutroFadeBars, plan.FadeBars);
        Assert.NotNull(plan.Arc);

        // At the drop the record coming in is at full…
        double drop = (incoming.Analysis!.IntroEndSeconds - plan.CueInSeconds) / plan.FadeSeconds;
        Assert.True(drop < 1, "this record's intro is shorter than the changeover");
        Assert.Equal(1, plan.Arc!.LevelsAt(drop).Incoming, 2);

        // …and it was not already at full a good deal earlier, which would be a plain cut.
        Assert.True(plan.Arc.LevelsAt(drop / 2).Incoming < 0.95);

        // The record going out is untouched: it still fades across the whole changeover, which is
        // what keeps this a changeover and not a slam.
        var ordinary = FadeArc.EqualPower();
        Assert.Equal(ordinary.LevelsAt(0.5).Outgoing, plan.Arc.LevelsAt(0.5).Outgoing, 6);
        Assert.Equal(0, plan.Arc.LevelsAt(1).Outgoing, 6);
    }

    /// <summary>
    /// A record with no intro starts on its drop. There is nothing to fade up under, so it arrives at
    /// full with no ramp at all (the same rule at zero).
    /// </summary>
    [Fact]
    public void A_record_with_no_intro_arrives_at_full()
    {
        var arc = FadeArc.EqualPower().IncomingUpBy(0);

        Assert.Equal(1, arc.LevelsAt(0).Incoming, 6);
        Assert.Equal(1, arc.LevelsAt(0.5).Incoming, 6);

        // And the record leaving still fades out over the whole of it.
        Assert.Equal(0, arc.LevelsAt(1).Outgoing, 6);
    }

    [Fact]
    public void An_arc_drawn_by_hand_outranks_the_intro()
    {
        var outgoing = Track(Analysis(bpm: 128, duration: 400));
        var incoming = Track(Analysis(bpm: 128, duration: 400) with { IntroEndSeconds = 6 });

        var mine = FadeArc.EqualPower().MovePoint(ArcSide.Incoming, 3, 0.4, 0.8);
        var plan = TransitionPlanner.Plan(
            outgoing, 128, incoming, new MixOverrides { Arc = mine });

        Assert.Equal(mine, plan.Arc);
    }

    // ── the cut, and the one in four that is not ──────────────────────────────

    /// <summary>
    /// A record nobody can read is a cut three times in four.
    /// </summary>
    [Fact]
    public void An_unreadable_record_is_still_a_cut_on_ordinary_luck()
    {
        var outgoing = Track(Analysis(bpm: 128, duration: 400));
        var unread = Track(Analysis(bpm: 0, duration: 400));

        foreach (double luck in new[] { 0.25, 0.5, 0.9, 1.0 })
        {
            var plan = TransitionPlanner.Plan(outgoing, 128, unread, luck: luck);
            Assert.Equal(TransitionStyle.Cut, plan.Style);
        }
    }

    /// <summary>
    /// And one time in four it is an echo out instead: thrown on the OUTGOING record's bar line,
    /// which is the one grid still there when the record coming in cannot be read.
    /// </summary>
    [Fact]
    public void One_unreadable_record_in_four_gets_an_echo_out()
    {
        var outgoing = Track(Analysis(bpm: 128, duration: 400));
        var unread = Track(Analysis(bpm: 0, duration: 400));

        var plan = TransitionPlanner.Plan(outgoing, 128, unread, luck: 0.1);

        Assert.Equal(TransitionStyle.EchoOut, plan.Style);

        // The new record starts at its own beginning. Nothing is mixed over it: the tail rings out
        // alone and it lands dry on the next bar. There is no old record for an intro to play
        // under, and skipping past one would throw part of the track away.
        Assert.Equal(0, plan.CueInSeconds, 6);
    }

    /// <summary>
    /// The echo is thrown on the record playing, so a record playing that has no grid of its own has
    /// nowhere to throw it. That one stays a cut whatever the random draw says.
    /// </summary>
    [Fact]
    public void A_record_playing_with_no_grid_has_nowhere_to_throw_an_echo()
    {
        var noGrid = Track(Analysis(bpm: 0, duration: 400));
        var unread = Track(Analysis(bpm: 0, duration: 400));

        Assert.Equal(
            TransitionStyle.Cut,
            TransitionPlanner.Plan(noGrid, 0, unread, luck: 0.1).Style);
    }

    /// <summary>A hand that named a changeover, or a cue point, is not overridden by the random draw.</summary>
    [Fact]
    public void A_named_changeover_is_not_turned_into_an_echo_out()
    {
        var outgoing = Track(Analysis(bpm: 128, duration: 400));
        var unread = Track(Analysis(bpm: 0, duration: 400));

        var asked = TransitionPlanner.Plan(
            outgoing, 128, unread, new MixOverrides { Style = TransitionStyle.Cut }, luck: 0.1);
        Assert.Equal(TransitionStyle.Cut, asked.Style);

        var cued = TransitionPlanner.Plan(
            outgoing, 128, unread, new MixOverrides { CueInSeconds = 30 }, luck: 0.1);
        Assert.NotEqual(TransitionStyle.EchoOut, cued.Style);
    }

    /// <summary>
    /// A pair that CAN be beat-matched is never an echo out from here. This branch is the cut's, and
    /// the cut is only reached where the record coming in cannot be read at all.
    /// </summary>
    [Fact]
    public void Two_readable_records_are_never_given_the_cuts_echo_out()
    {
        var outgoing = Track(Analysis(bpm: 128, duration: 400));
        var incoming = Track(Analysis(bpm: 128, duration: 400));

        var plan = TransitionPlanner.Plan(outgoing, 128, incoming, luck: 0.1);
        Assert.Equal(TransitionStyle.BeatMatched, plan.Style);
    }

    // ── the proposals ─────────────────────────────────────────────────────────

    [Fact]
    public void A_record_measured_loose_is_proposed_a_shorter_changeover()
    {
        var outgoing = Track(Analysis(bpm: 128, duration: 400) with { TempoStability = 0.2 });
        var incoming = Track(Analysis(bpm: 128, duration: 400));
        var plan = TransitionPlanner.Plan(outgoing, 128, incoming);

        var found = MixProposals.For(outgoing, 128, incoming, plan);
        Assert.Contains(found, p => p.Signal == nameof(TrackAnalysis.TempoStability));
    }

    [Fact]
    public void A_steady_record_is_proposed_nothing_about_its_steadiness()
    {
        var outgoing = Track(Analysis(bpm: 128, duration: 400) with { TempoStability = 0.9 });
        var incoming = Track(Analysis(bpm: 128, duration: 400));
        var plan = TransitionPlanner.Plan(outgoing, 128, incoming);

        Assert.DoesNotContain(
            MixProposals.For(outgoing, 128, incoming, plan),
            p => p.Signal == nameof(TrackAnalysis.TempoStability));
    }

    [Fact]
    public void A_bar_line_nobody_could_find_is_raised()
    {
        var outgoing = Track(Analysis(bpm: 128, duration: 400) with { DownbeatConfidence = 0.05 });
        var incoming = Track(Analysis(bpm: 128, duration: 400));
        var plan = TransitionPlanner.Plan(outgoing, 128, incoming);

        Assert.Contains(
            MixProposals.For(outgoing, 128, incoming, plan),
            p => p.Signal == nameof(TrackAnalysis.DownbeatConfidence));
    }

    [Fact]
    public void A_record_counted_in_three_is_told_its_fade_is_measured_in_four()
    {
        var threes = GridEdit.Regrid(Analysis(bpm: 120, duration: 400), 120, 0, beatsPerBar: 3);
        var outgoing = Track(threes with { OutroStartSeconds = 300 });
        var incoming = Track(Analysis(bpm: 120, duration: 400));
        var plan = TransitionPlanner.Plan(outgoing, 120, incoming);

        Assert.Contains(
            MixProposals.For(outgoing, 120, incoming, plan),
            p => p.Signal == nameof(TrackAnalysis.BeatsPerBar));
    }

    [Fact]
    public void Nothing_is_proposed_about_a_record_nobody_read()
    {
        Assert.Empty(MixProposals.For(null, 0, null, null));
    }

    // ── the roll-in lands on a bar of the record it is a bar of ───────────────

    [Fact]
    public void A_record_read_an_octave_from_the_count_loops_its_own_bar_and_not_the_mix_s()
    {
        // 87 is danced at 174 (see TempoEstimator.DanceLevel), so the mix counts bars twice as fast
        // as this record's own grid does. The roll-out may loop the mix's bar freely, because it
        // stops. The record coming in plays ON after its loop, so a loop of half its bar leaves it
        // half a bar out for the rest of the record, and the changeover would land on the three.
        var outgoing = Track(Analysis(bpm: 174, duration: 400));
        var incoming = Track(Analysis(bpm: 87, duration: 400));

        var plan = TransitionPlanner.SkipTo(outgoing, 174, incoming, startAt: 100);

        Assert.Equal(TransitionStyle.RollOut, plan.Style);
        Assert.True(plan.OverlapBars > 0, "the two should be matched, so there is an overlap to get wrong");

        // Its own bar: four beats of 87, not four beats of the 174 the mix is counting.
        double ownBar = 4 * 60.0 / (incoming.Bpm * plan.TempoRatio);
        Assert.Equal(ownBar, plan.RollInBarSeconds, 6);

        // And that really is the case worth a test: it is not the mix's bar.
        Assert.NotEqual(plan.BarSeconds, plan.RollInBarSeconds, 3);
    }

    [Fact]
    public void The_overlap_lasts_the_bars_the_drift_was_budgeted_for()
    {
        // The two records run together for the overlap, and how far their grids may walk apart over
        // it is decided from how LONG it is. So the overlap has to come out at the same duration
        // whatever octave the record coming in was read at: counted in its own bars it may be two or
        // eight, and in seconds it is always the four bars of the mix that were budgeted for.
        foreach (double bpm in new[] { 87.0, 128.0, 174.0 })
        {
            var outgoing = Track(Analysis(bpm: 128, duration: 400));
            var incoming = Track(Analysis(bpm: bpm, duration: 400));
            var plan = TransitionPlanner.SkipTo(outgoing, 128, incoming, startAt: 100);

            if (plan.OverlapBars == 0)
                continue;                       // given up rather than fudged, which is the other case

            Assert.Equal(
                TransitionPlanner.RollInBars * plan.BarSeconds,
                plan.OverlapBars * plan.RollInBarSeconds,
                6);

            // And it still fits inside the roll it is riding up over.
            Assert.True(plan.OverlapBars * plan.RollInBarSeconds <= plan.FadeSeconds + 1e-6);
        }
    }

    [Fact]
    public void The_two_loops_still_start_together()
    {
        // Whatever the octave, the roll-in bar has to be a whole number of mix bars or a whole
        // divisor of one, or the loops drift apart and the overlap stops being an overlap.
        foreach (double bpm in new[] { 87.0, 128.0, 174.0, 65.0 })
        {
            var outgoing = Track(Analysis(bpm: 128, duration: 400));
            var incoming = Track(Analysis(bpm: bpm, duration: 400));
            var plan = TransitionPlanner.SkipTo(outgoing, 128, incoming, startAt: 100);

            // Only where the two actually run together. Where they cannot be made to agree the
            // overlap is given up rather than fudged, which is the other half of the same fix.
            if (plan.OverlapBars == 0 || plan.RollInBarSeconds <= 0 || plan.BarSeconds <= 0)
                continue;

            double ratio = plan.RollInBarSeconds / plan.BarSeconds;
            double folded = ratio >= 1 ? ratio : 1 / ratio;
            Assert.True(
                Math.Abs(folded - Math.Round(folded)) < 0.01,
                $"{bpm}: roll-in bar {plan.RollInBarSeconds:F4}s against mix bar {plan.BarSeconds:F4}s");
        }
    }

    [Fact]
    public void A_record_counted_in_three_loops_three_beats_and_not_four()
    {
        var outgoing = Track(Analysis(bpm: 120, duration: 400));
        var threes = GridEdit.Regrid(Analysis(bpm: 120, duration: 400), 120, 0, beatsPerBar: 3);

        var plan = TransitionPlanner.SkipTo(outgoing, 120, Track(threes), startAt: 100);

        Assert.Equal(3 * 60.0 / (120 * plan.TempoRatio), plan.RollInBarSeconds, 6);

        // And it is not laid over the four-beat roll-out, because three against four share a line
        // once in three bars and the other two would start the record in the middle of its own.
        Assert.Equal(0, plan.OverlapBars);
        Assert.Contains(plan.Trace, t => t.Contains("do not share a bar line", StringComparison.Ordinal));
    }

    // ── the fours ─────────────────────────────────────────────────────────────

    [Fact]
    public void The_fours_are_counted_from_where_the_drums_come_in()
    {
        // A record that opens on something soft and brings the drums in at 6s. The audio starts long
        // before that and is not what anybody counts from: the one is where the kick is.
        var analysis = Analysis(bpm: 120, duration: 200) with
        {
            AudioStartSeconds = 0.4,
            AudioEndSeconds = 190,
            BassCurve = PadThenDrums(steps: 400, drumsFrom: 12),   // 12 steps of 0.5s = 6s
        };

        Assert.Equal(6.0, analysis.DrumsStartSeconds, 6);

        // Bars are 2s here, so 6s is the grid's bar 3, and the fours run 3, 7, 11 (6s, 14s, 22s).
        Assert.Equal(3, analysis.FirstWholeBar);
        Assert.Equal(6.0, analysis.FirstWholeBarSeconds, 6);
        Assert.Equal(6.0, analysis.PhraseAtOrAfter(5.0), 6);
        Assert.Equal(14.0, analysis.PhraseAtOrAfter(6.1), 6);
        Assert.Equal(6.0, analysis.PhraseAtOrBefore(13.9), 6);
    }

    [Fact]
    public void A_single_hit_in_front_of_the_beat_is_not_the_drums_starting()
    {
        // The crash that opens a record is not the groove. One loud reading is a hit; the drums are
        // the ones that keep going.
        var bass = PadThenDrums(steps: 400, drumsFrom: 12);
        bass[2] = 1f;                                          // a stab at 1s, alone

        var analysis = Analysis(bpm: 120, duration: 200) with { BassCurve = bass };

        Assert.Equal(6.0, analysis.DrumsStartSeconds, 6);
    }

    [Fact]
    public void A_record_whose_bands_were_never_measured_falls_back_to_its_edges()
    {
        var analysis = Analysis(bpm: 120, duration: 200);      // no bands, no edges

        Assert.Equal(0, analysis.DrumsStartSeconds);
        Assert.False(analysis.HasEdges);
        Assert.Equal(0, analysis.FirstWholeBar);
        Assert.Equal(8.0, analysis.PhraseAtOrAfter(6.1), 6);   // 0, 4, 8 in grid bars of 2s
    }

    /// <summary>A bass curve that idles under a pad and then has the drums in it.</summary>
    private static float[] PadThenDrums(int steps, int drumsFrom)
    {
        var bass = new float[steps];
        for (int i = 0; i < steps; i++)
            bass[i] = i < drumsFrom ? 0.05f : 1f;
        return bass;
    }

    [Fact]
    public void The_next_key_cues_the_record_coming_in_on_a_four()
    {
        var outgoing = Track(Analysis(bpm: 120, duration: 400));
        var incoming = Track(Analysis(bpm: 120, duration: 400) with
        {
            AudioStartSeconds = 4.4,
            AudioEndSeconds = 380,
            IntroEndSeconds = 25,
        });

        var plan = TransitionPlanner.SkipTo(outgoing, 120, incoming, startAt: 100);

        // Whatever the intro reading said, the playback starts on one of the record's own four-bar
        // lines: the same lines the lab draws in yellow, counted from the same first whole bar.
        Assert.Equal(
            incoming.Analysis!.PhraseAtOrBefore(plan.CueInSeconds),
            plan.CueInSeconds,
            6);
    }

    // ── how long a record is allowed to be ────────────────────────────────────

    [Fact]
    public void An_extended_record_hands_over_around_the_three_minute_mark()
    {
        // Eight minutes, with an outro reading right at the end of it. Left alone the set would sit
        // on this one record for the length of two others.
        var track = Track(Analysis(bpm: 128, duration: 480, outro: 440));

        double at = TransitionPlanner.MixOutPoint(track);

        Assert.Equal(TransitionPlanner.ExtendedMixOutSeconds, at, 3);
        Assert.True(at < 440, "the outro reading should not win on an extended record");
    }

    [Fact]
    public void A_record_of_ordinary_length_is_left_where_its_outro_says()
    {
        // Five minutes: under the bar, so nothing here applies and the record plays normally.
        var track = Track(Analysis(bpm: 128, duration: 300, outro: 250));

        Assert.Equal(250, TransitionPlanner.MixOutPoint(track), 3);
    }

    [Fact]
    public void A_way_out_the_record_offers_early_still_wins_on_an_extended_one()
    {
        // The ceiling is a ceiling, not a mark. A record that offers somewhere better before three
        // minutes is still left there, see MixOutFinder.
        var track = Track(Analysis(bpm: 128, duration: 480, outro: 100));

        Assert.True(TransitionPlanner.MixOutPoint(track) <= TransitionPlanner.ExtendedMixOutSeconds);
    }

    [Fact]
    public void A_record_too_long_to_mix_is_left_out_of_the_crate()
    {
        var ordinary = Track(Analysis(bpm: 128, duration: 300) with { FilePath = "short.flac" });
        var recorded = Track(Analysis(bpm: 128, duration: 3600) with { FilePath = "an-hour.flac" });

        var crate = new DjCrate([ordinary, recorded]);

        Assert.Equal(1, crate.RemainingCount);
        Assert.Equal("short.flac", crate.Remaining[0].Path);
    }

    [Fact]
    public void A_crate_of_nothing_but_long_records_still_plays()
    {
        // The rule is about what to leave out of a choice. Where there is no choice there is nothing
        // to leave out, and a set that silently refuses to start is worse than a long record.
        var recorded = Track(Analysis(bpm: 128, duration: 3600) with { FilePath = "an-hour.flac" });

        Assert.Equal(1, new DjCrate([recorded]).RemainingCount);
    }

    [Fact]
    public void The_next_key_on_a_record_too_long_to_mix_is_a_hard_skip()
    {
        var recorded = Track(Analysis(bpm: 128, duration: 3600));
        var incoming = Track(Analysis(bpm: 128, duration: 300));

        var plan = TransitionPlanner.SkipTo(recorded, 128, incoming, startAt: 900);

        // No roll, no loop, no overlap: there is nothing in an hour of somebody else's set for any of
        // those to be cut against.
        Assert.Equal(TransitionStyle.Cut, plan.Style);
        Assert.Equal(0, plan.OverlapBars);
        Assert.Contains(plan.Trace, t => t.Contains("hard skip", StringComparison.Ordinal));
    }

    // ── the pull has to actually be applied ───────────────────────────────────

    [Fact]
    public void A_pull_the_planner_counted_on_is_not_dropped_as_too_small()
    {
        // The drift budget is what decides whether two records may be held together, and it is spent
        // on the tempo the clamps could NOT reach. A decoder that silently declines to apply a small
        // pull spends that budget twice: once in the arithmetic and again when it is played.
        //
        // So whatever the decoder is willing to ignore has to be smaller than the smallest mismatch
        // the planner would call a beat-match, over the longest changeover it will build.
        const double LongestFadeSeconds = 40;      // sixteen bars at a slow club tempo

        double worstDrift = FfmpegPcmSource.UnstretchedRatio * LongestFadeSeconds;

        Assert.True(
            worstDrift < TransitionPlanner.MaxDriftSeconds,
            $"a dropped pull drifts {worstDrift * 1000:F1} ms over a long fade, and the budget is " +
            $"{TransitionPlanner.MaxDriftSeconds * 1000:F0} ms");
    }

    // ── the journal ───────────────────────────────────────────────────────────

    [Fact]
    public void An_edit_is_written_down_with_both_sides_of_it()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rpm-mixlab-{Guid.NewGuid():N}");
        try
        {
            var journal = new MixLabJournal(dir);
            Assert.NotNull(journal.Path);

            var outgoing = Track(Analysis(bpm: 128, duration: 400) with { TempoStability = 0.2 });
            var incoming = Track(Analysis(bpm: 128, duration: 400));

            var planned = TransitionPlanner.Plan(outgoing, 128, incoming);
            var result = TransitionPlanner.Plan(outgoing, 128, incoming, new MixOverrides { FadeBars = 4 });

            journal.Edited(
                outgoing, 128, 300, planned, result, new MixOverrides { FadeBars = 4 },
                ["outroStart 380.00s -> 300.00s"]);

            string line = Assert.Single(File.ReadAllLines(journal.Path!));

            // Both sides of the disagreement, the reasoning behind the machine's side, and the
            // measurements that were unused at the time. Any one of those alone is an anecdote.
            Assert.Contains("\"kind\":\"edit\"", line, StringComparison.Ordinal);
            Assert.Contains("\"planned\"", line, StringComparison.Ordinal);
            Assert.Contains("\"result\"", line, StringComparison.Ordinal);
            // Not the whole line: the arrow is written as an escape, which is the serialiser's
            // business and not this test's.
            Assert.Contains("outroStart 380.00s", line, StringComparison.Ordinal);
            Assert.Contains("TempoStability", line, StringComparison.Ordinal);
            Assert.Equal(1, journal.Count);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void A_journal_that_cannot_be_written_is_a_lost_line_rather_than_a_throw()
    {
        // A path that is a file, so the folder underneath it cannot be created.
        var file = Path.GetTempFileName();
        try
        {
            var journal = new MixLabJournal(Path.Combine(file, "mixlab"));
            journal.Edited(null, 0, 0, null, null, null, null);    // must not throw
            Assert.Equal(0, journal.Count);
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
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

    private static MixTrack Track(TrackAnalysis analysis) => new()
    {
        Source = analysis.FilePath,
        Path = analysis.FilePath,
        Title = "t",
        TaggedDuration = analysis.DurationSeconds,
        Analysis = analysis,
    };
}
