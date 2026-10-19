using RPM.Core.Analysis;
using RPM.Core.Cloud;
using RPM.Fingerprint;

namespace RPM.Core.Tests;

/// <summary>
/// The stamps that decide what is read again. Getting one wrong is silent in both directions: too low
/// and every machine keeps a reading the analyser no longer produces, too high and a library spends
/// hours computing the answer it already had.
/// </summary>
public class AnalysisGenerationTests
{
    [Fact]
    public void The_fourteenth_generation_goes_back_for_everything_the_thirteenth_wrote()
    {
        // Both halves of a stamp. A modelled row from an old generation is every bit as out of date as
        // a DSP one, and it is the row that has to be found: it is the better of the two readings, so
        // nothing else would ever go near it again.
        Assert.False(TrackAnalysis.IsCurrent(13));
        Assert.False(TrackAnalysis.IsCurrent(TrackAnalysis.ModelledOffset + 13));

        Assert.True(TrackAnalysis.IsCurrent(TrackAnalysis.AnalyzerVersion));
        Assert.True(TrackAnalysis.IsCurrent(TrackAnalysis.ModelledVersion));
    }

    [Fact]
    public void A_grid_the_pool_holds_from_the_last_generation_is_not_read()
    {
        // The grid holds the tempo, the beats and the bar line. In this generation the bar line is
        // searched for together with the beats (not picked and then snapped onto one), and a tempo
        // below 70 is not doubled on output.
        Assert.Equal(10, AnalysisFacts.GridGeneration);

        const string body = """
            {"duration":"120","bpm":"128","bpmConfidence":"1","beats":"0,0.5,1,1.5",
             "firstDownbeat":0,"downbeatConfidence":"1","beatsPerBar":4}
            """;

        var stale = new KnownRecording(
            "r", [new RecordingFactBody(RecordingFact.Grid, AnalysisFacts.GridGeneration - 1, body)]);
        var current = new KnownRecording(
            "r", [new RecordingFactBody(RecordingFact.Grid, AnalysisFacts.GridGeneration, body)]);

        Assert.Null(AnalysisFacts.To(stale, "/library/x.wav"));
        Assert.NotNull(AnalysisFacts.To(current, "/library/x.wav"));
    }

    [Fact]
    public void Steadiness_from_the_last_generation_is_not_served_beside_a_grid_that_bends()
    {
        // This fact depends on the grid, so its generation changes with the grid's. Measured against a
        // straight line it is the same number either way; measured against a grid that follows the
        // record it is not, and a reading from an older grid would call a record that its grid follows
        // exactly the least steady in the library.
        Assert.Equal(3, AnalysisFacts.ShapeGeneration);
    }

    [Fact]
    public void The_facts_the_change_did_not_touch_keep_their_generations()
    {
        // This is why each fact has its own stamp. A better beat tracker replaces beat grids and
        // nothing else. The per-fact stamps prevent a change to the grid from throwing away every key
        // and every energy curve for every user.
        Assert.Equal(1, AnalysisFacts.KeyGeneration);
        Assert.Equal(1, AnalysisFacts.EnergyGeneration);
        Assert.Equal(3, AnalysisFacts.CuesGeneration);
        Assert.Equal(1, AnalysisFacts.EdgesGeneration);
        Assert.Equal(1, AnalysisFacts.BandsGeneration);
    }

    [Fact]
    public void A_reading_written_today_is_published_at_the_generations_it_was_made_at()
    {
        var analysis = new TrackAnalysis
        {
            FilePath = "/library/x.wav",
            DurationSeconds = 120,
            Bpm = 128,
            BpmConfidence = 1,
            BeatTimes = [0, 0.46875, 0.9375, 1.40625],
            Key = MusicalKey.FromCamelot("8A"),
            KeyConfidence = 0.9,
            EnergyCurve = [0.5f, 0.5f],
            Intensity = 0.5,
            TempoStability = 0.8,
        };

        var facts = AnalysisFacts.From(analysis);

        Assert.Equal(
            AnalysisFacts.GridGeneration,
            facts.First(f => f.Field == RecordingFact.Grid).Generation);
        Assert.Equal(
            AnalysisFacts.ShapeGeneration,
            facts.First(f => f.Field == RecordingFact.Shape).Generation);
    }
}
