using RPM.Core.Analysis;
using RPM.Core.Audio;
using RPM.Core.Mixing;

namespace RPM.Core.Tests;

/// <summary>
/// The bass swap, in three phrases: the record coming in arrives over the first with its bottom end
/// shut off, the bass changes hands across the second, and the old record is faded away above it over
/// the third.
///
/// <para>The order of the steps is the move, so it is the order that is pinned: what each deck's
/// level and low shelf are at each phrase line, rather than only the length. A bass swap whose bass
/// swaps in the wrong phrase is beat-matched, bar-aligned, correctly timed and not a bass swap.</para>
/// </summary>
public class BassSwapTests
{
    private const int Bpm = 120;                    // a bar every two seconds, a phrase every sixteen

    private static MixTrack Track(
        string name, double duration, double outro, double intro = 0, double bpm = Bpm)
    {
        var beats = new List<double>();
        for (double at = 0; at < duration; at += 60.0 / bpm)
            beats.Add(at);

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
                BeatsPerBar = 4,
                Key = new MusicalKey(9, true),
                KeyConfidence = 0.8,
                EnergyCurve = [0.3f, 0.3f],
                IntroEndSeconds = intro,
                OutroStartSeconds = outro,
                Phrases = new PhraseGrid(32, 0, 1),
            },
        };
    }

    private static TransitionPlan Swap(double duration = 400, double outro = 300) =>
        TransitionPlanner.Plan(
            Track("out", duration, outro), Bpm, Track("in", 400, 380),
            new MixOverrides { Style = TransitionStyle.BassSwap });

    [Fact]
    public void It_runs_for_three_phrases()
    {
        var plan = Swap();

        Assert.Equal(TransitionStyle.BassSwap, plan.Style);
        Assert.Equal(TransitionPlanner.BassSwapPhrases * 8, plan.FadeBars);
        Assert.Equal(24, plan.FadeBars);
    }

    /// <summary>The first phrase: the new record comes up to full, and the old one does not move.</summary>
    [Fact]
    public void Over_the_first_phrase_the_new_record_arrives_and_the_old_one_is_untouched()
    {
        var arc = Swap().Arc!;

        Assert.Equal(0, arc.LevelsAt(0).Incoming, 3);
        Assert.Equal(1, arc.LevelsAt(0).Outgoing, 3);

        // Half way up the first phrase, and at full by the end of it.
        Assert.InRange(arc.LevelsAt(1 / 6.0).Incoming, 0.4, 0.6);
        Assert.Equal(1, arc.LevelsAt(1 / 3.0).Incoming, 3);

        // The record playing is at full for the whole of it.
        Assert.Equal(1, arc.LevelsAt(1 / 6.0).Outgoing, 3);
        Assert.Equal(1, arc.LevelsAt(1 / 3.0).Outgoing, 3);
    }

    /// <summary>And it arrives with no bottom end at all. That is what makes it a bass swap rather than
    /// two records playing at once.</summary>
    [Fact]
    public void The_new_record_arrives_with_its_bass_shut_off()
    {
        var arc = Swap().Arc!;

        Assert.Equal(DeckEq.KillDb, arc.BassDbAt(0).Incoming, 3);
        Assert.Equal(DeckEq.KillDb, arc.BassDbAt(1 / 6.0).Incoming, 3);

        // And the old record keeps the whole of its own.
        Assert.Equal(0, arc.BassDbAt(0).Outgoing, 3);
        Assert.Equal(0, arc.BassDbAt(1 / 3.0).Outgoing, 3);
    }

    /// <summary>
    /// The second phrase is the swap: the bottom end rises on one record as it falls on the other, and
    /// on the phrase line at the end of it the old record has none left.
    /// </summary>
    [Fact]
    public void Across_the_second_phrase_the_bass_changes_hands()
    {
        var arc = Swap().Arc!;

        // Half way across, both are part way: this ramps rather than steps.
        var middle = arc.BassDbAt(0.5);
        Assert.InRange(middle.Outgoing, DeckEq.KillDb * 0.6, DeckEq.KillDb * 0.4);
        Assert.InRange(middle.Incoming, DeckEq.KillDb * 0.6, DeckEq.KillDb * 0.4);

        // Monotone: one goes only up and the other only down.
        double previousOut = 0, previousIn = DeckEq.KillDb;
        for (int i = 0; i <= 20; i++)
        {
            double t = 1 / 3.0 + (1 / 3.0) * i / 20.0;
            var here = arc.BassDbAt(t);
            Assert.True(here.Outgoing <= previousOut + 1e-9, $"the old record's bass came back at t={t:F3}");
            Assert.True(here.Incoming >= previousIn - 1e-9, $"the new record's bass dipped at t={t:F3}");
            previousOut = here.Outgoing;
            previousIn = here.Incoming;
        }

        // On the line at the end of the second phrase the swap is complete.
        Assert.Equal(DeckEq.KillDb, arc.BassDbAt(2 / 3.0).Outgoing, 3);
        Assert.Equal(0, arc.BassDbAt(2 / 3.0).Incoming, 3);
    }

    /// <summary>Both records play whole through the swap. The old one keeps its level right across it:
    /// the bottom end leaving is what thins it, and pulling the fader at the same time would do the
    /// same job twice.</summary>
    [Fact]
    public void Neither_fader_moves_during_the_swap()
    {
        var arc = Swap().Arc!;

        Assert.Equal(1, arc.LevelsAt(1 / 3.0).Outgoing, 3);
        Assert.Equal(1, arc.LevelsAt(0.5).Outgoing, 3);
        Assert.Equal(1, arc.LevelsAt(2 / 3.0).Outgoing, 3);

        Assert.Equal(1, arc.LevelsAt(1 / 3.0).Incoming, 3);
        Assert.Equal(1, arc.LevelsAt(2 / 3.0).Incoming, 3);
    }

    /// <summary>The third phrase: the old record is taken away, and the new one is alone and whole at
    /// the end of it.</summary>
    [Fact]
    public void Over_the_third_phrase_the_old_record_is_faded_out()
    {
        var arc = Swap().Arc!;

        Assert.InRange(arc.LevelsAt(5 / 6.0).Outgoing, 0.4, 0.6);
        Assert.Equal(0, arc.LevelsAt(1).Outgoing, 3);
        Assert.Equal(1, arc.LevelsAt(1).Incoming, 3);
        Assert.Equal(0, arc.BassDbAt(1).Incoming, 3);
    }

    /// <summary>
    /// A changeover the clamps cut down is not a shorter bass swap: its three sections are no longer
    /// phrases and the bottom end would change hands somewhere inside a bar. The move needs its whole
    /// length or it does not happen, and the blend the planner already made is used instead.
    /// </summary>
    [Fact]
    public void A_changeover_without_the_room_for_three_phrases_is_not_a_bass_swap()
    {
        // 40 s of record all told, and 24 bars at 120 wants 48 s. It cannot fit however it is placed.
        var plan = TransitionPlanner.Plan(
            Track("out", duration: 40, outro: 30), Bpm, Track("in", 400, 380),
            new MixOverrides { Style = TransitionStyle.BassSwap });

        Assert.NotEqual(TransitionStyle.BassSwap, plan.Style);
        Assert.Contains(plan.Trace, t => t.Contains("no bass swap"));
    }

    /// <summary>
    /// Three phrases of both records at full needs them on ONE tempo. A pair the pitch faders cannot
    /// close does not get the move: no part of this changeover is quiet enough to hide a drift.
    /// </summary>
    [Fact]
    public void A_pair_the_faders_cannot_close_does_not_get_a_bass_swap()
    {
        var from = Track("out", 400, 300);
        // 146 against 120: the outgoing pull clamps at 10% and the incoming one does not, so the two
        // land a quarter of a percent apart. Small enough to pass the ordinary drift budget, which is
        // exactly the case this rule exists for. A pair at 150 would already be a blend before this rule.
        var to = Track("in", 400, 380, bpm: 146);

        var plan = TransitionPlanner.Plan(
            from, Bpm, to, new MixOverrides { Style = TransitionStyle.BassSwap });

        Assert.NotEqual(TransitionStyle.BassSwap, plan.Style);
        Assert.Contains(plan.Trace, t => t.Contains("no bass swap") && t.Contains("locked"));
    }

    /// <summary>And when it does happen, both decks are running the same number of beats a minute for
    /// the whole of it.</summary>
    [Fact]
    public void Both_decks_run_at_one_tempo_through_the_swap()
    {
        var plan = Swap();

        Assert.True(plan.TempoMismatch <= TransitionPlanner.TempoLockTolerance);
        Assert.Equal(Bpm * plan.OutgoingTempoRatio, Bpm * plan.TempoRatio, 9);
    }

    /// <summary>A curve set by hand is never overwritten by the move's.</summary>
    [Fact]
    public void A_drawn_arc_still_wins()
    {
        var drawn = FadeArc.EqualPower();
        var plan = TransitionPlanner.Plan(
            Track("out", 400, 300), Bpm, Track("in", 400, 380),
            new MixOverrides { Style = TransitionStyle.BassSwap, Arc = drawn });

        Assert.Same(drawn, plan.Arc);
    }

    /// <summary>The mix is reported as changing records when the bottom end changes, not when the
    /// fader does. That is the moment the swap is audible.</summary>
    [Fact]
    public void The_room_changes_records_when_the_bass_does()
    {
        var arc = Swap().Arc!;

        // The two levels are equal from the end of the first phrase on, so the balance point is there.
        Assert.InRange(arc.BalanceT, 1 / 3.0 - 0.02, 1 / 3.0 + 0.02);
    }
}
