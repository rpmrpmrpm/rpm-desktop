using RPM.Core.Analysis;
using RPM.Core.Audio;
using RPM.Core.Mixing;

namespace RPM.Core.Tests;

/// <summary>
/// The six changeovers taken from how DJs mix in other genres: the catalogue that says which genre
/// each comes from, the plans they produce, and the two pieces of audio code that the four original
/// changeovers do not need (an echo taken after the fader, and a bar of record pulled backwards).
///
/// <para>Each test first checks that the move is refused, not given a different name, when the
/// records cannot support it. A changeover called a double drop that is really a blend is worse than
/// the blend, because the rest of the app trusts the name.</para>
/// </summary>
public class MixTechniqueTests
{
    private const int Rate = 44100, Channels = 2;

    // ── the catalogue ─────────────────────────────────────────────────────────

    [Fact]
    public void Every_style_the_planner_can_produce_is_in_the_catalogue_exactly_once()
    {
        // Catches a new move added without saying where it comes from. A style with no entry would
        // fall back to the first one in the list and silently claim to be a blend.
        foreach (var style in Enum.GetValues<TransitionStyle>())
        {
            var entries = MixTechniques.All.Where(t => t.Style == style).ToList();
            Assert.True(entries.Count == 1, $"{style} has {entries.Count} catalogue entries");
            Assert.False(string.IsNullOrWhiteSpace(entries[0].Source));
            Assert.NotEmpty(entries[0].Genres);
        }
    }

    [Fact]
    public void Tempo_says_which_room_a_record_could_have_come_from()
    {
        // 174 can only be drum and bass, so this is the one tempo where the guess is worth making.
        Assert.Equal([MixGenre.DrumAndBass], MixTechniques.RoomsAt(174));

        // In the middle of the range it is a list, because house, tech house and the genres based
        // on them overlap there.
        Assert.Contains(MixGenre.House, MixTechniques.RoomsAt(126));
        Assert.Contains(MixGenre.TechHouse, MixTechniques.RoomsAt(126));

        // No measured tempo means no genre.
        Assert.Empty(MixTechniques.RoomsAt(0));
    }

    [Fact]
    public void The_double_drop_is_a_drum_and_bass_move_and_the_long_layer_is_not()
    {
        Assert.Contains(MixGenre.DrumAndBass, MixTechniques.For(TransitionStyle.DoubleDrop).Genres);
        Assert.True(MixTechniques.For(TransitionStyle.DoubleDrop).SuitsTempo(174));
        Assert.False(MixTechniques.For(TransitionStyle.BassSwap).SuitsTempo(174));
        Assert.True(MixTechniques.For(TransitionStyle.BassSwap).SuitsTempo(126));
    }

    [Fact]
    public void A_move_is_refused_when_the_reading_it_needs_was_never_taken()
    {
        var gridded = Track("a", 128, duration: 240);
        var unread = Track("b", 0, duration: 240, analyzed: false);

        Assert.NotNull(MixTechniques.WhatIsMissing(
            MixTechniques.For(TransitionStyle.BassSwap), gridded, unread, canMeet: false));

        // The echo needs a bar line on the outgoing record and nothing from the incoming one. That is
        // why a DJ uses it when the next record has a different tempo.
        Assert.Null(MixTechniques.WhatIsMissing(
            MixTechniques.For(TransitionStyle.EchoOut), gridded, unread, canMeet: false));
    }

    [Fact]
    public void The_planner_reaches_for_a_gesture_only_when_the_luck_says_so()
    {
        var one = Track("one", 174, duration: 300);
        var two = Track("two", 0, duration: 300, analyzed: false);

        // Two records with no tempo in common: the planner suggests the cut on the one, unless the
        // luck value falls inside the share kept for the gestures.
        Assert.Equal(TransitionStyle.Slam,
            TransitionPlanner.Suggest(one, 174, two, luck: 0.9).Technique.Style);
        Assert.Equal(TransitionStyle.EchoOut,
            TransitionPlanner.Suggest(one, 174, two, luck: 0.0).Technique.Style);
    }

    // ── the plans ─────────────────────────────────────────────────────────────

    [Fact]
    public void The_cut_on_the_one_lands_on_a_bar_line_and_pulls_neither_record()
    {
        var from = Track("from", 128, duration: 240);
        var to = Track("to", 96, duration: 240);

        var plan = TransitionPlanner.Plan(from, 128, to, Ask(TransitionStyle.Slam));

        Assert.Equal(TransitionStyle.Slam, plan.Style);
        Assert.Equal(TransitionPlanner.SlamSeconds, plan.FadeSeconds, 6);

        // Neither record is stretched. In four hundredths of a second there is nothing to beat-match,
        // and the incoming record plays alone from its first sample.
        Assert.Equal(1, plan.TempoRatio, 6);
        Assert.Equal(1, plan.OutgoingTempoRatio, 6);

        // On one of the outgoing record's phrase starts. That is the only difference from a cut.
        Assert.Equal(from.Analysis!.PhraseAtOrBefore(plan.StartAtSeconds), plan.StartAtSeconds, 4);
    }

    /// <summary>The catalogue's own path to the bass swap. The details are tested in BassSwapTests;
    /// this test checks that asking for the style by name produces it.</summary>
    [Fact]
    public void The_bass_swap_layers_for_three_phrases_and_hands_the_bottom_over_across_the_middle_one()
    {
        var from = Track("from", 128, duration: 420, outro: 300);
        var to = Track("to", 128, duration: 420);

        var plan = TransitionPlanner.Plan(from, 128, to, Ask(TransitionStyle.BassSwap));

        Assert.Equal(TransitionStyle.BassSwap, plan.Style);
        Assert.Equal(TransitionPlanner.BassSwapPhrases * from.Analysis!.BarsPerPhrase, plan.FadeBars);

        var arc = plan.Arc;
        Assert.NotNull(arc);

        // The swap is the middle phrase, so it is a third of the changeover wide and starts a third in.
        Assert.Equal(1 / 3.0, arc.BassSwapStart, 3);
        Assert.Equal(2 / 3.0, arc.BassSwapEnd, 3);

        // The old record has all of the bass through the first phrase and none of it after the
        // second.
        Assert.Equal(0, arc.BassDbAt(0.3).Outgoing, 3);
        Assert.Equal(DeckEq.KillDb, arc.BassDbAt(0.7).Outgoing, 3);
        Assert.Equal(0, arc.BassDbAt(0.7).Incoming, 3);

        // Both records are at full level through the swap. That is the move.
        Assert.Equal(1, arc.LevelsAt(0.5).Incoming, 3);
        Assert.Equal(1, arc.LevelsAt(0.5).Outgoing, 3);

        // The changeover point is reported where the bass starts to move.
        Assert.InRange(arc.BalanceT, 0.3, 0.36);
    }

    [Fact]
    public void The_filter_fade_leaves_the_fader_alone_until_the_sweep_has_done_its_work()
    {
        var from = Track("from", 128, duration: 300, outro: 220);
        var to = Track("to", 128, duration: 300);

        var plan = TransitionPlanner.Plan(from, 128, to, Ask(TransitionStyle.FilterFade));

        Assert.Equal(TransitionStyle.FilterFade, plan.Style);
        Assert.NotNull(plan.Arc);

        // Two fifths of the way through, the fader has not moved the outgoing record yet: everything
        // that has happened to it so far is the filter.
        Assert.Equal(1, plan.Arc.LevelsAt(0.4).Outgoing, 3);
        Assert.InRange(plan.Arc.LevelsAt(0.9).Outgoing, 0, 0.35);
    }

    [Fact]
    public void The_double_drop_switches_on_both_records_drops_with_no_overlap()
    {
        // A record whose last drop is at 4:50, near where it hands over, and one whose own drop is
        // 1:04 in. A drop three minutes before the mix-out point is out of reach (see
        // DoubleDropReachSeconds), because getting to it would lose half the record.
        var from = Track("from", 174, duration: 360, outro: 300, drops: [290]);
        var to = Track("to", 174, duration: 360, drops: [64]);

        var plan = TransitionPlanner.Plan(from, 174, to, Ask(TransitionStyle.DoubleDrop));

        Assert.Equal(TransitionStyle.DoubleDrop, plan.Style);

        // This move has no overlap: the playing record stops on its own drop and the incoming record
        // starts on its own drop, one beat later.
        Assert.Equal(290, plan.StartAtSeconds, 2);
        Assert.Equal(64, plan.CueInSeconds, 2);
        Assert.Equal(TransitionPlanner.DoubleDropSwitchBeats * plan.BarSeconds / 4, plan.FadeSeconds, 4);
        Assert.Equal(0, plan.FadeBars);

        // Neither record is pulled: they never play together, so their tempos do not need to agree.
        Assert.Equal(1, plan.TempoRatio, 6);
        Assert.Equal(1, plan.OutgoingTempoRatio, 6);

        // No curve: a changeover one beat wide has no shape to edit.
        Assert.Null(plan.Arc);
    }

    [Fact]
    public void A_double_drop_with_nothing_to_land_on_is_an_ordinary_mix_rather_than_a_lie()
    {
        var from = Track("from", 174, duration: 360, outro: 300, drops: [120]);
        var to = Track("to", 174, duration: 360);              // nothing measured a drop on it

        var plan = TransitionPlanner.Plan(from, 174, to, Ask(TransitionStyle.DoubleDrop));

        Assert.Equal(TransitionStyle.BeatMatched, plan.Style);
        Assert.Contains(plan.Trace, line => line.Contains("no double drop", StringComparison.Ordinal));
    }

    [Fact]
    public void A_layer_asked_for_on_a_record_with_no_grid_is_not_relabelled_as_one()
    {
        var from = Track("from", 128, duration: 300, outro: 220);
        var to = Track("to", 0, duration: 300, analyzed: false);

        var plan = TransitionPlanner.Plan(from, 128, to, Ask(TransitionStyle.BassSwap));

        // The planner may choose whatever the pair can support (with no grid on the incoming record,
        // its blend is a cut). It must never layer two records with no tempo in common for thirty-two
        // bars, and never give another style that name.
        Assert.True(plan.Style is TransitionStyle.Blend or TransitionStyle.Cut, $"{plan.Style}");
        Assert.Contains(plan.Trace, line => line.Contains("one tempo", StringComparison.Ordinal));
    }

    [Fact]
    public void The_echo_out_bridges_two_tempos_that_will_not_meet()
    {
        // 96 against 174 is beyond any pitch fader. DJ guides recommend the echo out for this case.
        var from = Track("from", 174, duration: 300, outro: 220);
        var to = Track("to", 96, duration: 300, analyzed: false);

        var plan = TransitionPlanner.Plan(from, 174, to, Ask(TransitionStyle.EchoOut));

        Assert.Equal(TransitionStyle.EchoOut, plan.Style);
        Assert.Equal(TransitionPlanner.EchoBars, plan.FadeBars);

        // No tempo is changed: both records play at their own speed and the echo tail joins them.
        Assert.Equal(1, plan.TempoRatio, 6);
        Assert.Equal(1, plan.OutgoingTempoRatio, 6);
        Assert.True(plan.BarSeconds > 0);
    }

    [Fact]
    public void The_planner_makes_the_same_changeover_it_always_did_until_the_policy_says_otherwise()
    {
        // The outgoing record's drop is near where it hands over: a double drop switches ON that drop,
        // so a drop three minutes earlier is out of reach for this move.
        var from = Track("from", 174, duration: 360, outro: 300, drops: [290]);
        var to = Track("to", 174, duration: 360, drops: [64]);

        // The default: two drum and bass records with two measured drops, and the planner still makes
        // its standard beat-matched mix. The techniques are used only when the policy turns them on.
        var asIs = TransitionPlanner.Plan(from, 174, to);
        Assert.Equal(TransitionStyle.BeatMatched, asIs.Style);
        Assert.DoesNotContain(asIs.Trace, line => line.StartsWith("technique:", StringComparison.Ordinal));

        var chosen = TransitionPlanner.Plan(
            from, 174, to, new MixOverrides { Policy = MixPolicy.Default with { Techniques = true } });

        Assert.Equal(TransitionStyle.DoubleDrop, chosen.Style);
        Assert.Contains(chosen.Trace, line => line.StartsWith("technique:", StringComparison.Ordinal));

        // Chosen by the planner, not asked for by the user. Recording an override here would claim
        // that somebody asked for it.
        Assert.Null(chosen.Overrides?.Style);
    }

    [Fact]
    public void The_next_key_still_rolls_out_when_nobody_asks_for_anything_else()
    {
        var from = Track("from", 174, duration: 360, outro: 300, drops: [120]);
        var to = Track("to", 174, duration: 360, drops: [64]);

        // A press means leave the record now, and the result is a roll out, even with the techniques
        // turned on.
        var plan = TransitionPlanner.SkipTo(
            from, 174, to, startAt: 60,
            new MixOverrides { Policy = MixPolicy.Default with { Techniques = true } });

        Assert.Equal(TransitionStyle.RollOut, plan.Style);
    }

    [Fact]
    public void The_next_key_can_be_asked_for_a_spinback_and_gets_one()
    {
        var from = Track("from", 174, duration: 360, outro: 300);
        var to = Track("to", 174, duration: 360);

        var plan = TransitionPlanner.SkipTo(from, 174, to, startAt: 60, Ask(TransitionStyle.Spinback));

        // The next key must pass the requested move to the planner and get that move back, not fall
        // through to a beat-matched mix.
        Assert.Equal(TransitionStyle.Spinback, plan.Style);

        // It starts from the phrase after the press, never before it.
        Assert.True(plan.StartAtSeconds >= 60);
        Assert.Equal(from.Analysis!.PhraseAtOrAfter(60), plan.StartAtSeconds, 4);
    }

    // ── the echo ──────────────────────────────────────────────────────────────

    [Fact]
    public void An_echo_outlives_the_signal_that_threw_it()
    {
        var echo = new EchoTail(Rate, Channels);
        echo.Throw(delaySeconds: 0.25, feedback: 0.6, forSeconds: 4);

        // A tenth of a second of record, then silence. That is what the deck produces after the
        // fader has closed over it.
        var block = new float[Rate / 10 * Channels];
        Array.Fill(block, 0.5f);
        echo.Process(block, 0, Rate / 10);

        double loudest = 0;
        var silence = new float[Rate / 10 * Channels];
        for (int i = 0; i < 6; i++)                     // six tenths of a second of nothing
        {
            Array.Clear(silence);
            echo.Process(silence, 0, Rate / 10);
            loudest = Math.Max(loudest, silence.Max(Math.Abs));
        }

        // The record has stopped and the echo can still be heard.
        Assert.True(loudest > 0.1, $"the tail was {loudest:F3}");
        Assert.True(echo.IsRunning);
    }

    [Fact]
    public void An_echo_dies_of_its_own_feedback_and_stops_costing_anything()
    {
        var echo = new EchoTail(Rate, Channels);
        echo.Throw(delaySeconds: 0.05, feedback: 0.3, forSeconds: 30);

        var block = new float[Rate / 20 * Channels];
        Array.Fill(block, 0.5f);
        echo.Process(block, 0, Rate / 20);

        for (int i = 0; i < 60 && echo.IsRunning; i++)
        {
            Array.Clear(block);
            echo.Process(block, 0, Rate / 20);
        }

        // Thirty seconds were allowed. At 0.3 feedback the tail is below a thousandth well before
        // that, and the audio thread should not keep processing a tail nobody can hear.
        Assert.False(echo.IsRunning);
    }

    // ── the filter, the other way up ──────────────────────────────────────────

    [Fact]
    public void A_high_pass_takes_the_bottom_and_a_low_pass_takes_the_top()
    {
        // A slow sine, well below the corner frequency, like a kick and bassline to a filter.
        var low = Sine(40, seconds: 0.5);
        var high = new SweepFilter(Rate, Channels);
        high.SetHighPass(1000);
        high.Process(low, 0, low.Length / Channels);

        Assert.True(Loudest(low) < 0.15, $"the bottom end survived at {Loudest(low):F3}");

        // The low pass is unchanged: the same corner, keeping the other half.
        var again = Sine(40, seconds: 0.5);
        var lowPass = new SweepFilter(Rate, Channels);
        lowPass.SetCutoff(1000);
        lowPass.Process(again, 0, again.Length / Channels);

        Assert.True(Loudest(again) > 0.8, $"the bottom end was taken at {Loudest(again):F3}");
    }

    [Fact]
    public void A_filter_parked_out_of_the_way_is_not_run_at_all()
    {
        var sweep = new SweepFilter(Rate, Channels);

        sweep.SetHighPass(SweepFilter.FlatHz);
        Assert.False(sweep.IsClosing);

        sweep.SetHighPass(400);
        Assert.True(sweep.IsClosing);
        Assert.True(sweep.IsHighPass);

        // Reset returns it to the open low pass that every roll out uses.
        sweep.Reset();
        Assert.False(sweep.IsClosing);
        Assert.False(sweep.IsHighPass);
    }

    // ── the rewind ────────────────────────────────────────────────────────────

    [Fact]
    public void A_spinback_plays_the_bar_and_then_drags_it_back_through()
    {
        // A record whose samples say where in it they come from, so the direction of travel can be
        // read directly from the block.
        var deck = new Deck(Deck.Side.A, Rate, Channels,
            options => new Ramp(options.StartSeconds));

        deck.Load(Track("rip", 174, duration: 300), cueSeconds: 0, tempoRatio: 1);
        deck.Prime();
        deck.Start();
        deck.Fader = 1;

        const int Bar = Rate;                       // a second of each, for round numbers
        deck.SpinBack(afterFrames: 0, captureFrames: Bar, spinFrames: Bar);

        var block = new float[Rate / 100 * Channels];
        var heard = new List<float>();
        for (int i = 0; i < 300; i++)
        {
            if (deck.Read(block, Rate / 100) <= 0)
                break;
            heard.Add(block[0]);
        }

        // The bar plays forwards first: the gesture finishes the phrase and then pulls it back, it
        // does not skip the phrase.
        Assert.True(heard[10] > heard[5]);

        // Then it moves backwards, fast: two and a half times normal speed at the start of the pull.
        int rip = 100;                              // one second in, in hundredths
        Assert.True(heard[rip + 2] < heard[rip - 2], "the record did not reverse");
        Assert.True(heard[rip + 20] < heard[rip + 2], "the reverse did not keep going");

        // It slows to a stop and then there is silence: the deck has finished with the record.
        Assert.True(Math.Abs(heard[^1]) < 0.001);
    }

    [Fact]
    public void A_deck_pulled_backwards_draws_its_needle_backwards()
    {
        var deck = new Deck(Deck.Side.A, Rate, Channels,
            options => new Ramp(options.StartSeconds));

        deck.Load(Track("rip", 174, duration: 300), cueSeconds: 0, tempoRatio: 1);
        deck.Prime();
        deck.Start();
        deck.Fader = 1;
        deck.SpinBack(afterFrames: 0, captureFrames: Rate, spinFrames: Rate);

        var block = new float[Rate / 100 * Channels];
        for (int i = 0; i < 105; i++)               // through the capture and into the pull
            deck.Read(block, Rate / 100);

        Assert.True(deck.IsSpinning);
        double at = deck.NeedleSeconds;

        for (int i = 0; i < 40; i++)
            deck.Read(block, Rate / 100);

        // A needle that stays still for the length of the gesture looks frozen. This one moves back
        // through the bar it is pulling, which matches what is heard.
        Assert.True(deck.NeedleSeconds < at, $"needle stayed at {at:F2}");
    }

    // ── the whole set ─────────────────────────────────────────────────────────

    [Fact]
    public void A_set_asked_for_an_echo_out_plays_one_and_carries_on()
    {
        var tracks = new[]
        {
            Track("one", 128, duration: 60, outro: 40),
            Track("two", 128, duration: 60, outro: 40),
        };

        using var dj = Booth(tracks);
        dj.Overrides = Ask(TransitionStyle.EchoOut);

        var heard = new List<string>();
        dj.TrackChanged += t => heard.Add(t.Title!);

        var block = new float[512 * Channels];
        TransitionStyle? played = null;
        for (int i = 0; i < 40_000; i++)
        {
            if (dj.Read(block, 512) <= 0)
                break;
            if (dj.PendingPlan is { } pending)
                played = pending.Style;
        }

        Assert.Equal(TransitionStyle.EchoOut, played);
        Assert.Equal(["one", "two"], heard);
        Assert.True(dj.HasFinished);
    }

    [Fact]
    public void A_set_asked_for_a_spinback_rewinds_and_the_next_record_lands_after_it()
    {
        var tracks = new[]
        {
            Track("one", 174, duration: 60, outro: 40),
            Track("two", 174, duration: 60, outro: 40),
        };

        // The two records read +1 and -1, so one sample shows which of them is playing.
        var markers = new Dictionary<string, float>
        {
            ["/library/one.flac"] = 1f,
            ["/library/two.flac"] = -1f,
        };

        using var dj = Booth(tracks, markers);
        dj.Overrides = Ask(TransitionStyle.Spinback);

        var block = new float[512 * Channels];
        int partWayIn = 0;
        bool spun = false;
        for (int i = 0; i < 40_000; i++)
        {
            int read = dj.Read(block, 512);
            if (read <= 0)
                break;

            if (dj.PendingPlan is { Style: TransitionStyle.Spinback })
                spun = true;

            // A rewind is not a mix. The outgoing record stays at full level until the pull has
            // stopped, and the incoming one is held until then, so the new record is only heard at
            // full level. Its marker is negative and the old one's is positive: any value between 0
            // and -1 is the new record at less than full.
            for (int s = 0; s < read * Channels; s++)
            {
                if (block[s] < -0.1f && block[s] > -0.9f)
                    partWayIn++;
            }
        }

        Assert.True(spun, "the changeover was not a spinback");

        // One block of it is allowed: the deck's own ramp on the first block it plays. A fader that
        // jumps from zero to full makes a click, so every deck starts this way. There must be no
        // fade: at 174 BPM a bar is a hundred and twenty blocks, so a record faded in under another
        // would give tens of thousands of these.
        Assert.InRange(partWayIn, 0, 512 * Channels);

        Assert.True(dj.HasFinished);
        Assert.Equal(2, dj.Played.Count);
    }

    // ── the workbench's two loops ─────────────────────────────────────────────

    [Fact]
    public void A_record_turned_down_is_passed_over_and_another_one_cued()
    {
        var tracks = new[]
        {
            Track("one", 128, duration: 90, outro: 70),
            Track("two", 128, duration: 90, outro: 70),
            Track("three", 128, duration: 90, outro: 70),
        };

        using var dj = Booth(tracks);
        var block = new float[512 * Channels];

        // Play until the next record has been chosen and cued.
        for (int i = 0; i < 40_000 && dj.PendingPlan is null; i++)
        {
            if (dj.Read(block, 512) <= 0)
                break;
        }

        var first = dj.PendingPlan?.Incoming;
        Assert.NotNull(first);

        Assert.True(dj.RejectCued());
        Assert.Null(dj.PendingPlan);
        Assert.Contains(first.Path, dj.Rejected);

        // The set picks again, and does not pick the record it was just told not to.
        for (int i = 0; i < 40_000 && dj.PendingPlan is null; i++)
        {
            if (dj.Read(block, 512) <= 0)
                break;
        }

        Assert.NotNull(dj.PendingPlan);
        Assert.NotEqual(first.Path, dj.PendingPlan.Incoming.Path);

        // The record is still in the set, at the back: it is rejected for this changeover only, not
        // removed from the set.
        Assert.Contains(dj.Remaining, t => t.Path == first.Path);
    }

    [Fact]
    public void A_rejection_lasts_as_long_as_the_changeover_it_was_about()
    {
        var tracks = new[]
        {
            Track("one", 128, duration: 60, outro: 40),
            Track("two", 128, duration: 60, outro: 40),
            Track("three", 128, duration: 60, outro: 40),
        };

        using var dj = Booth(tracks);
        var block = new float[512 * Channels];

        for (int i = 0; i < 40_000 && dj.PendingPlan is null; i++)
        {
            if (dj.Read(block, 512) <= 0)
                break;
        }

        Assert.True(dj.RejectCued());
        Assert.NotEmpty(dj.Rejected);

        // After a changeover has really happened, the next outro asks about a different pair, so the
        // rejection no longer applies.
        for (int i = 0; i < 200_000 && dj.Rejected.Count > 0; i++)
        {
            if (dj.Read(block, 512) <= 0)
                break;
        }

        Assert.Empty(dj.Rejected);
    }

    [Fact]
    public void A_record_can_be_turned_down_before_it_has_reached_a_deck()
    {
        var tracks = new[]
        {
            Track("one", 128, duration: 300, outro: 280),
            Track("two", 128, duration: 300, outro: 280),
            Track("three", 128, duration: 300, outro: 280),
        };

        using var dj = Booth(tracks);
        var block = new float[512 * Channels];
        for (int i = 0; i < 200; i++)
            dj.Read(block, 512);

        // Five minutes before the outro, so nothing has been cued and there is no plan. But the queue
        // has already chosen, and the workbench shows that choice on the second deck.
        Assert.Null(dj.PendingPlan);
        var chosen = dj.RunningOrder.SkipWhile(t => t.Title != "one").Skip(1).First();

        Assert.True(dj.Reject(chosen));
        Assert.Contains(chosen.Path, dj.Rejected);

        // It is skipped when the choice is actually made.
        Assert.NotEqual(chosen.Path, dj.RunningOrder.SkipWhile(t => t.Title != "one").Skip(1).First().Path);
    }

    [Fact]
    public void A_hand_on_the_decks_stops_the_set_planning_anything()
    {
        var tracks = new[]
        {
            Track("one", 128, duration: 60, outro: 40),
            Track("two", 128, duration: 60, outro: 40),
        };

        using var dj = Booth(tracks);
        var block = new float[512 * Channels];

        Assert.True(dj.TakeTheDecks(true));
        Assert.True(dj.HandsOn);

        // Long past the outro and past the end of the record: while the user holds the decks the set
        // cues nothing, changes over nothing, and does not decide it has finished.
        for (int i = 0; i < 20_000; i++)
            Assert.True(dj.Read(block, 512) > 0);

        Assert.Null(dj.PendingPlan);
        Assert.False(dj.IsChangingOver);
        Assert.False(dj.HasFinished);
        Assert.Equal("one", dj.LiveTrack?.Title);

        // When the decks are handed back, the set continues exactly where it would have been.
        Assert.True(dj.TakeTheDecks(false));
        for (int i = 0; i < 200_000 && !dj.HasFinished; i++)
        {
            if (dj.Read(block, 512) <= 0)
                break;
        }

        Assert.True(dj.HasFinished);
        Assert.Equal(2, dj.Played.Count);
    }

    [Fact]
    public void A_hand_can_still_ask_for_the_next_record_to_be_put_on_the_free_deck()
    {
        var tracks = new[]
        {
            Track("one", 128, duration: 60, outro: 40),
            Track("two", 128, duration: 60, outro: 40),
        };

        using var dj = Booth(tracks);
        var block = new float[512 * Channels];

        // Started, then the decks are taken long before anything would be cued automatically.
        Assert.True(dj.Read(block, 512) > 0);
        Assert.True(dj.TakeTheDecks(true));
        Assert.Null(dj.CuedTrack);

        // The second deck's play key: put the next record on now, so it can be heard before the
        // changeover. Like every other request, it is answered on the audio thread.
        Assert.True(dj.CueNext());
        for (int i = 0; i < 200 && dj.CuedTrack is null; i++)
            Assert.True(dj.Read(block, 512) > 0);

        Assert.Equal("two", dj.CuedTrack?.Title);
        Assert.NotNull(dj.PendingPlan);

        // The user still holds the decks: cueing is not planning, and nothing has started.
        Assert.True(dj.HandsOn);
        Assert.False(dj.IsChangingOver);
        Assert.True(dj.AuditionCued(true));
        Assert.True(dj.IsAuditioning);
    }

    [Fact]
    public void Handing_the_decks_back_puts_the_record_coming_in_on_its_cue_point()
    {
        var tracks = new[]
        {
            Track("one", 128, duration: 60, outro: 40),
            Track("two", 128, duration: 60, outro: 40),
        };

        using var dj = Booth(tracks);
        var block = new float[512 * Channels];

        // Far enough in that the next record has been cued and is waiting at the plan's cue point.
        for (int i = 0; i < 40_000 && dj.PendingPlan is null; i++)
        {
            if (dj.Read(block, 512) <= 0)
                break;
        }

        var plan = dj.PendingPlan;
        Assert.NotNull(plan);
        Assert.True(dj.TakeTheDecks(true));

        // Moved to a place the plan did not choose, as when the user looks through the record.
        double away = plan!.CueInSeconds + 12;
        Assert.True(dj.SeekDeck(DjDeck.Cued, away));
        Assert.Equal(away, dj.CuedPositionSeconds, 1);

        // When the decks are handed back it is on the cue point again: the two decks show the
        // changeover that is about to happen, which is what pressing play on the set asks for.
        Assert.True(dj.TakeTheDecks(false));
        Assert.Equal(plan.CueInSeconds, dj.CuedPositionSeconds, 1);
    }

    [Fact]
    public void The_decks_cannot_be_taken_in_the_middle_of_a_changeover()
    {
        var tracks = new[]
        {
            Track("one", 128, duration: 60, outro: 40),
            Track("two", 128, duration: 60, outro: 40),
        };

        using var dj = Booth(tracks);
        var block = new float[512 * Channels];

        bool refused = false;
        for (int i = 0; i < 40_000; i++)
        {
            if (dj.Read(block, 512) <= 0)
                break;
            if (!dj.IsTransitioning)
                continue;

            // A fade controls both decks. Neither answer is available while one runs.
            refused = !dj.TakeTheDecks(true);
            break;
        }

        Assert.True(refused, "the decks were handed over mid-changeover");
        Assert.False(dj.HandsOn);
    }

    [Fact]
    public void A_rehearsed_changeover_puts_both_records_back_and_stands_at_the_run_up()
    {
        var tracks = new[]
        {
            Track("one", 128, duration: 90, outro: 70),
            Track("two", 128, duration: 90, outro: 70),
        };

        using var dj = Booth(tracks);
        dj.RehearseChangeover = true;

        int rehearsed = 0;
        dj.ChangeoverRehearsed += () => rehearsed++;

        var block = new float[512 * Channels];
        for (int i = 0; i < 200_000 && rehearsed == 0; i++)
        {
            if (dj.Read(block, 512) <= 0)
                break;
        }

        Assert.Equal(1, rehearsed);

        // Play on: the record loads on another thread, so the set outputs silence until it is back on
        // the deck. Then it is: the record that was mixed OUT of, at its run-up.
        for (int i = 0; i < 200 && dj.IsRehearsing; i++)
            dj.Read(block, 512);

        Assert.False(dj.IsRehearsing);
        Assert.Equal("one", dj.LiveTrack?.Title);

        // The record that arrived is back in the set, so there is something to mix into again.
        Assert.Contains(dj.Remaining, t => t.Title == "two");
        Assert.True(dj.IsNextReady);

        // The needle is just before the changeover, not at the start of the record: a mix needs the
        // music before it to be judged.
        Assert.InRange(dj.LivePositionSeconds, 1, 70);
    }

    [Fact]
    public void A_rehearsed_set_plays_the_same_changeover_over_and_over()
    {
        var tracks = new[]
        {
            Track("one", 128, duration: 90, outro: 70),
            Track("two", 128, duration: 90, outro: 70),
        };

        using var dj = Booth(tracks);
        dj.RehearseChangeover = true;

        int rehearsed = 0;
        dj.ChangeoverRehearsed += () => rehearsed++;

        // Nothing pauses the set here (in the app the engine pauses, and a test has no engine), so it
        // plays the same sixteen bars again and again. That is what the test checks.
        var block = new float[512 * Channels];
        for (int i = 0; i < 400_000 && rehearsed < 3; i++)
        {
            if (dj.Read(block, 512) <= 0)
                break;
        }

        Assert.True(rehearsed >= 3, $"the changeover ran {rehearsed} times");
        Assert.False(dj.HasFinished);
    }

    [Fact]
    public void A_set_that_is_not_rehearsing_plays_on_as_it_always_did()
    {
        var tracks = new[]
        {
            Track("one", 128, duration: 60, outro: 40),
            Track("two", 128, duration: 60, outro: 40),
        };

        using var dj = Booth(tracks);
        int rehearsed = 0;
        dj.ChangeoverRehearsed += () => rehearsed++;

        var block = new float[512 * Channels];
        for (int i = 0; i < 200_000; i++)
        {
            if (dj.Read(block, 512) <= 0)
                break;
        }

        Assert.Equal(0, rehearsed);
        Assert.True(dj.HasFinished);
        Assert.Equal(2, dj.Played.Count);
    }

    // ── the bench ─────────────────────────────────────────────────────────────

    private static MixOverrides Ask(TransitionStyle style) => new() { Style = style };

    /// <summary>A deck source whose samples say where in the record they come from (sample N holds
    /// N divided by the sample rate), so the direction the needle moves can be read directly from a
    /// block.</summary>
    private sealed class Ramp(double startSeconds) : IPcmSource
    {
        private long _at;

        public string Label => "ramp";

        public int Read(float[] buffer, int frames)
        {
            for (int f = 0; f < frames; f++)
            {
                var value = (float)(startSeconds + (double)(_at + f) / Rate);
                for (int c = 0; c < Channels; c++)
                    buffer[f * Channels + c] = value;
            }
            _at += frames;
            return frames;
        }

        public void Dispose() { }
    }

    /// <summary>A record with one constant value throughout, so a block of mix can be read as "how
    /// much of this record, how much of that one".</summary>
    private sealed class Tone(string label, float value, double seconds, double tempoRatio) : IPcmSource
    {
        private long _left = (long)(seconds * Rate / tempoRatio);

        public string Label => label;

        public int Read(float[] buffer, int frames)
        {
            if (_left <= 0)
                return 0;
            int give = (int)Math.Min(frames, _left);
            for (int i = 0; i < give * Channels; i++)
                buffer[i] = value;
            _left -= give;
            return give;
        }

        public void Dispose() => _left = 0;
    }

    private static Dj Booth(IEnumerable<MixTrack> tracks, IDictionary<string, float>? markers = null) =>
        new(new DjCrate(tracks), Rate, Channels,
            options =>
            {
                float marker = markers is not null && markers.TryGetValue(options.Source, out var v) ? v : 1f;
                return new Tone(options.Label ?? options.Source, marker, 120 - options.StartSeconds, options.TempoRatio);
            },
            cueRunner: work => { work(); return Task.CompletedTask; },
            chance: () => 1);

    private static float[] Sine(double hz, double seconds)
    {
        var block = new float[(int)(seconds * Rate) * Channels];
        for (int f = 0; f < block.Length / Channels; f++)
        {
            var value = (float)Math.Sin(2 * Math.PI * hz * f / Rate);
            for (int c = 0; c < Channels; c++)
                block[f * Channels + c] = value;
        }
        return block;
    }

    /// <summary>The loudest sample in the second half of a block. The first half of a filtered tone is
    /// the filter settling, and the test is about the value it settles to.</summary>
    private static float Loudest(float[] block)
    {
        float most = 0;
        for (int i = block.Length / 2; i < block.Length; i++)
            most = Math.Max(most, Math.Abs(block[i]));
        return most;
    }

    private static MixTrack Track(
        string name, double bpm, double duration = 120, double? outro = null,
        IReadOnlyList<double>? drops = null, bool analyzed = true)
    {
        string path = $"/library/{name}.flac";
        var beats = new List<double>();
        if (bpm > 0)
            for (double at = 0; at < duration; at += 60.0 / bpm)
                beats.Add(at);

        return new MixTrack
        {
            Source = path,
            Path = path,
            Title = name,
            TaggedDuration = duration,
            Analysis = analyzed
                ? new TrackAnalysis
                {
                    FilePath = path,
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
                    DropSeconds = drops ?? [],
                }
                : null,
        };
    }
}
