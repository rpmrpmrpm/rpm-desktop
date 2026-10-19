using RPM.Core.Analysis;
using RPM.Core.Cloud;
using RPM.Fingerprint;

namespace RPM.Core.Tests;

/// <summary>
/// An analysis taken apart into the facts the pool keeps and put back together again. What has to
/// hold: that a record survives the round trip, that two machines measuring one record produce the
/// same text so the store can see them agree, and that a partial answer is still worth having.
/// </summary>
public class AnalysisFactsTests
{
    private static TrackAnalysis Reading(
        double bpm = 128.4,
        bool modelled = false,
        double keyConfidence = 0.8) => new()
    {
        FilePath = "/music/a.flac",
        DurationSeconds = 214.5,
        Bpm = bpm,
        BpmConfidence = 0.91,
        BeatTimes = [.. Enumerable.Range(0, 64).Select(i => i * 0.4675)],
        FirstDownbeat = 2,
        DownbeatConfidence = 0.62,
        BeatsPerBar = 4,
        Key = new MusicalKey(9, IsMinor: true),
        KeyConfidence = keyConfidence,
        EnergyCurve = [.. Enumerable.Range(0, 40).Select(i => (float)(i / 40.0))],
        Intensity = 0.71,
        IntroEndSeconds = 12.5,
        OutroStartSeconds = 190.25,
        AudioStartSeconds = 0.75,
        AudioEndSeconds = 213.0,
        BeatFirstSeconds = 1.2,
        BeatLastSeconds = 205.5,
        BassCurve = [.. Enumerable.Range(0, 40).Select(i => (float)(i / 80.0))],
        MidCurve = [.. Enumerable.Range(0, 40).Select(i => (float)(i / 100.0))],
        TopCurve = [.. Enumerable.Range(0, 40).Select(i => (float)(i / 200.0))],
        VocalCurve = [.. Enumerable.Range(0, 40).Select(i => (float)(i / 40.0))],
        Phrases = new PhraseGrid(16, 2, 0.62),
        TempoStability = 0.94,
        DropSeconds = [64.5, 128.25],
        IntroLevel = 0.31,
        OutroLevel = 0.58,
        Version = modelled ? TrackAnalysis.ModelledVersion : TrackAnalysis.AnalyzerVersion,
    };

    private static KnownRecording Pooled(TrackAnalysis analysis) =>
        new("rec-1", AnalysisFacts.From(analysis));

    [Fact]
    public void A_reading_survives_the_journey_to_another_machine()
    {
        var here = Reading();

        var there = AnalysisFacts.To(Pooled(here), "/elsewhere/b.flac");

        Assert.NotNull(there);
        Assert.Equal("/elsewhere/b.flac", there!.FilePath);
        Assert.Equal(here.DurationSeconds, there.DurationSeconds, 3);
        Assert.Equal(here.Bpm, there.Bpm, 3);
        Assert.Equal(here.BpmConfidence, there.BpmConfidence, 3);
        Assert.Equal(here.BeatTimes.Count, there.BeatTimes.Count);
        Assert.Equal(here.BeatTimes[^1], there.BeatTimes[^1], 3);
        Assert.Equal(here.FirstDownbeat, there.FirstDownbeat);
        Assert.Equal(here.DownbeatConfidence, there.DownbeatConfidence, 3);
        Assert.Equal(here.BeatsPerBar, there.BeatsPerBar);
        Assert.Equal(here.Key.Camelot, there.Key.Camelot);
        Assert.Equal(here.KeyConfidence, there.KeyConfidence, 3);
        Assert.Equal(here.Intensity, there.Intensity, 3);
        Assert.Equal(here.EnergyCurve.Count, there.EnergyCurve.Count);
        Assert.Equal(here.IntroEndSeconds, there.IntroEndSeconds, 3);
        Assert.Equal(here.OutroStartSeconds, there.OutroStartSeconds, 3);
    }

    [Fact]
    public void What_arrived_can_still_be_mixed_on()
    {
        var there = AnalysisFacts.To(Pooled(Reading()), "/elsewhere/b.flac");

        Assert.True(there!.CanBeatMatch);
        Assert.True(there.NextDownbeatAfter(0) > 0);
    }

    [Fact]
    public void Two_machines_measuring_one_record_say_the_same_words()
    {
        // The store sees agreement only when the text matches. Two readings that differ below the
        // rounding must not look like two people contradicting each other for ever.
        var here = Reading();
        var thereabouts = here with { Bpm = here.Bpm + 1e-9, Intensity = here.Intensity + 1e-9 };

        var a = AnalysisFacts.From(here).Single(f => f.Field == RecordingFact.Grid);
        var b = AnalysisFacts.From(thereabouts).Single(f => f.Field == RecordingFact.Grid);

        Assert.Equal(a.Value, b.Value);
    }

    [Fact]
    public void A_models_reading_says_so_and_a_dsp_one_does_not()
    {
        Assert.True(AnalysisFacts.From(Reading(modelled: true)).Single(f => f.Field == RecordingFact.Grid).Modelled);
        Assert.False(AnalysisFacts.From(Reading()).Single(f => f.Field == RecordingFact.Grid).Modelled);

        // And what arrives modelled is stamped as modelled, so the upgrade pass leaves it alone,
        // while what arrives from the DSP can still be found by it.
        Assert.True(AnalysisFacts.To(Pooled(Reading(modelled: true)), "/x.flac")!.Modelled);
        Assert.False(AnalysisFacts.To(Pooled(Reading()), "/x.flac")!.Modelled);
    }

    [Fact]
    public void A_record_with_no_tempo_is_published_confidently()
    {
        // "This record has no beat to match" is a finding, and one every other machine would otherwise
        // spend a whole decode to reach. Sent at the tracker's own zero confidence the store would
        // refuse it as too hesitant to serve.
        var quiet = Reading(bpm: 0) with { BpmConfidence = 0, BeatTimes = [] };

        var grid = AnalysisFacts.From(quiet).Single(f => f.Field == RecordingFact.Grid);

        Assert.True(grid.Confidence >= RecordingFacts.MinConfidence);
    }

    [Fact]
    public void A_reading_with_no_key_publishes_no_key()
    {
        var keyless = Reading() with { Key = MusicalKey.Unknown, KeyConfidence = 0 };

        Assert.DoesNotContain(AnalysisFacts.From(keyless), f => f.Field == RecordingFact.Key);
    }

    [Fact]
    public void A_grid_on_its_own_is_enough_to_be_worth_taking()
    {
        // Energy and cues are an overlay. A record whose curve nobody has published still arrives
        // beat-matchable, which is the thing the DJ actually needs.
        var gridOnly = new KnownRecording(
            "rec-1", [.. AnalysisFacts.From(Reading()).Where(f => f.Field == RecordingFact.Grid)]);

        var there = AnalysisFacts.To(gridOnly, "/x.flac");

        Assert.NotNull(there);
        Assert.True(there!.CanBeatMatch);
        Assert.Empty(there.EnergyCurve);
        // Nothing said where to mix out, so the whole record plays rather than an invented outro.
        Assert.Equal(there.DurationSeconds, there.OutroStartSeconds, 3);
    }

    [Fact]
    public void Without_a_grid_there_is_nothing_worth_having()
    {
        var keyOnly = new KnownRecording(
            "rec-1", [.. AnalysisFacts.From(Reading()).Where(f => f.Field == RecordingFact.Key)]);

        Assert.Null(AnalysisFacts.To(keyOnly, "/x.flac"));
        Assert.Null(AnalysisFacts.To(null, "/x.flac"));
    }

    [Fact]
    public void A_fact_from_a_generation_this_build_does_not_read_is_left_alone()
    {
        var future = new KnownRecording("rec-1", [.. AnalysisFacts.From(Reading())
            .Select(f => f with { Generation = f.Generation + 100 })]);

        Assert.Null(AnalysisFacts.To(future, "/x.flac"));
    }

    [Fact]
    public void One_field_from_the_future_does_not_spoil_the_rest()
    {
        // The purpose of stamping per field: a cue-point measurement this build cannot read leaves
        // the grid, the key and the energy exactly as usable as they were.
        var mixed = new KnownRecording("rec-1", [.. AnalysisFacts.From(Reading())
            .Select(f => f.Field == RecordingFact.Cues ? f with { Generation = f.Generation + 100 } : f)]);

        var there = AnalysisFacts.To(mixed, "/x.flac");

        Assert.NotNull(there);
        Assert.True(there!.CanBeatMatch);
        Assert.Equal("8A", there.Key.Camelot);   // A minor
        Assert.NotEmpty(there.EnergyCurve);
        Assert.Equal(there.DurationSeconds, there.OutroStartSeconds, 3);
    }

    [Fact]
    public void The_edges_travel_with_everything_else()
    {
        var there = AnalysisFacts.To(Pooled(Reading()), "/x.flac");

        Assert.True(there!.HasEdges);
        Assert.Equal(0.75, there.AudioStartSeconds, 3);
        Assert.Equal(213.0, there.AudioEndSeconds, 3);
        Assert.Equal(1.2, there.BeatFirstSeconds, 3);
        Assert.Equal(205.5, there.BeatLastSeconds, 3);

        // And they do what they are for on the far side, not just survive the journey.
        Assert.Null(there.NextDownbeatAfter(213.5));
        Assert.True(there.IsExtrapolatedAt(210));
    }

    [Fact]
    public void A_machine_that_never_measured_the_edges_publishes_none()
    {
        // Zeroes would read as a record that is silent from its first sample, which is worse than
        // saying nothing, and every row analysed before the seventh generation has zeroes.
        var older = Reading() with { AudioStartSeconds = 0, AudioEndSeconds = 0 };

        Assert.DoesNotContain(AnalysisFacts.From(older), f => f.Field == RecordingFact.Edges);
        Assert.False(AnalysisFacts.To(new KnownRecording("rec-1", AnalysisFacts.From(older)), "/x.flac")!.HasEdges);
    }

    [Fact]
    public void The_grid_generation_is_not_the_analysers_own()
    {
        // The seventh analyser measures the edges and holds the cues inside them. It moves no beats,
        // so tying the grid's generation to it would throw away every beat grid in the pool (for
        // every user, at once) for a change to something else.
        Assert.NotEqual(TrackAnalysis.AnalyzerVersion, AnalysisFacts.GridGeneration);
    }

    [Fact]
    public void A_reading_borrowed_from_an_older_analyser_is_not_complete()
    {
        // The case a freshly ripped CD lands in: somebody published this record before the edges were
        // measured, so what comes back is a perfectly good grid with nothing to say about where the
        // music stops. Taking it whole would stamp the row as current and edgeless, and nothing ever
        // looks at a current row again, so the DJ would mix into that record's trailing silence for
        // the life of the library. The caller has to be able to see the gap.
        var older = Reading() with { AudioStartSeconds = 0, AudioEndSeconds = 0 };

        var borrowed = AnalysisFacts.To(new KnownRecording("rec-1", AnalysisFacts.From(older)), "/x.flac");

        Assert.NotNull(borrowed);
        Assert.True(borrowed!.CanBeatMatch);
        Assert.False(borrowed.HasEdges);
    }

    [Fact]
    public void Nonsense_in_the_pool_is_not_taken_as_an_analysis()
    {
        var rubbish = new KnownRecording("rec-1",
        [
            new RecordingFactBody(RecordingFact.Grid, AnalysisFacts.GridGeneration, "not json at all"),
        ]);

        Assert.Null(AnalysisFacts.To(rubbish, "/x.flac"));
    }

    /// <summary>The bands and the shape survive the pool. Both are new kinds of fact rather than
    /// additions to the grid or the energy: raising either of those generations would throw away
    /// every grid and every energy curve in the pool (for every user, at once) for a change to
    /// something else.</summary>
    [Fact]
    public void The_bands_and_the_shape_survive_the_round_trip()
    {
        var here = Reading();
        var there = AnalysisFacts.To(Pooled(here), "/elsewhere/b.flac");

        Assert.NotNull(there);
        Assert.Equal(here.BassCurve.Count, there!.BassCurve.Count);
        Assert.Equal(here.BassCurve[10], there.BassCurve[10], 3);
        Assert.Equal(here.VocalCurve[20], there.VocalCurve[20], 3);

        Assert.Equal(here.Phrases.Bars, there.Phrases.Bars);
        Assert.Equal(here.Phrases.FirstBar, there.Phrases.FirstBar);
        Assert.Equal(here.TempoStability, there.TempoStability, 3);
        Assert.Equal(here.DropSeconds, there.DropSeconds);
        Assert.Equal(here.IntroLevel, there.IntroLevel, 3);
        Assert.Equal(here.OutroLevel, there.OutroLevel, 3);
    }

    /// <summary>A machine running the older analyser publishes neither, and a reading without them is
    /// still worth having: all the older facts still arrive.</summary>
    [Fact]
    public void A_reading_with_no_bands_is_still_a_reading()
    {
        var old = Reading() with
        {
            BassCurve = [], MidCurve = [], TopCurve = [], VocalCurve = [],
            Phrases = default, TempoStability = 0, DropSeconds = [],
        };

        var fields = AnalysisFacts.From(old).Select(f => f.Field).ToList();
        Assert.DoesNotContain(RecordingFact.Bands, fields);
        Assert.DoesNotContain(RecordingFact.Shape, fields);

        var there = AnalysisFacts.To(Pooled(old), "/elsewhere/b.flac");
        Assert.NotNull(there);
        Assert.Equal(old.Bpm, there!.Bpm, 3);
        Assert.False(there.HasBands);
    }

    /// <summary>
    /// Two machines that measured the same record have to produce the same text, or the store cannot
    /// see that they agree, and agreement is what turns a claim into a fact.
    ///
    /// <para>Agreement is only ever claimed to the precision that is written down. Below the third
    /// place the two are the same reading by construction; at the third place they are the same text
    /// or they are two claims, and there is no third possibility to test for.</para>
    /// </summary>
    [Fact]
    public void Two_machines_write_the_bands_the_same_way()
    {
        string Bands(TrackAnalysis a) =>
            AnalysisFacts.From(a).Single(f => f.Field == RecordingFact.Bands).Value;
        string Shape(TrackAnalysis a) =>
            AnalysisFacts.From(a).Single(f => f.Field == RecordingFact.Shape).Value;

        var mine = Reading();
        Assert.Equal(Bands(mine), Bands(Reading()));
        Assert.Equal(Shape(mine), Shape(Reading()));

        // And a difference too small to be written down is not written down.
        var theirs = Reading() with
        {
            TempoStability = mine.TempoStability + 0.00001,
            IntroLevel = mine.IntroLevel + 0.00001,
        };
        Assert.Equal(Shape(mine), Shape(theirs));
    }

    /// <summary>
    /// A reading from a machine running an older analyser arrives stamped with THIS analyser's
    /// version, because the facts carry their own generations and the stamp is about the reader
    /// rather than the writer.
    ///
    /// <para>Which is exactly why nothing may adopt one without checking what is in it: stamped
    /// current and short of a field is a row nothing will ever look at again.</para>
    /// </summary>
    [Fact]
    public void A_reading_from_an_older_analyser_still_arrives_looking_current()
    {
        var old = Reading() with
        {
            BassCurve = [], MidCurve = [], TopCurve = [], VocalCurve = [],
            Phrases = default, TempoStability = 0, DropSeconds = [],
        };

        var there = AnalysisFacts.To(Pooled(old), "/elsewhere/b.flac");

        Assert.NotNull(there);
        Assert.True(TrackAnalysis.IsCurrent(there!.Version));
        Assert.False(there.HasBands);
    }

    /// <summary>
    /// A reading whose shape fact this build no longer accepts still arrives with its phrases, because
    /// they are arithmetic over the bands and the grid rather than something only the publisher knew.
    ///
    /// <para>Without this it arrives short and is stored as current, and nothing looks at a current
    /// row again.</para>
    /// </summary>
    [Fact]
    public void A_reading_with_no_usable_shape_works_its_own_out()
    {
        // Bars of bass that change every eight, and no shape fact at all.
        var bars = Enumerable.Range(0, 64).Select(b => b / 8 % 2 == 0 ? 0.8f : 0.1f);
        var withBands = Reading() with
        {
            BeatTimes = [.. Enumerable.Range(0, 257).Select(i => i * 0.5)],
            FirstDownbeat = 0,
            BeatsPerBar = 4,
            BassCurve = [.. bars.SelectMany(v => Enumerable.Repeat(v, 4))],
            Phrases = default,
            TempoStability = 0,
            DropSeconds = [],
        };

        var withoutShape = AnalysisFacts.From(withBands).Where(f => f.Field != RecordingFact.Shape).ToList();
        var there = AnalysisFacts.To(new KnownRecording("rec-1", withoutShape), "/elsewhere/b.flac");

        Assert.NotNull(there);
        Assert.True(there!.Phrases.IsKnown);
        Assert.Equal(8, there.Phrases.Bars);

        // Steadiness is the one thing that cannot be worked out from a ruler, and is not guessed at.
        Assert.Equal(0, there.TempoStability);
    }
}
