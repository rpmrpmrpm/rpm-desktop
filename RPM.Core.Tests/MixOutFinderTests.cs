using RPM.Core.Analysis;
using RPM.Core.Mixing;

namespace RPM.Core.Tests;

/// <summary>
/// Leaving a long record before its outro.
///
/// <para>On a record longer than three minutes, the set looks for a stretch where the singing stops
/// and the drums continue, and starts the changeover there. A record with no such stretch is played
/// to its outro.</para>
/// </summary>
public class MixOutFinderTests
{
    private const double Bpm = 128;

    /// <summary>Sixteen bars at 128 BPM: the length a changeover out of one of these needs.</summary>
    private static readonly double Hold = 16 * 4 * 60.0 / Bpm;

    /// <param name="quietFrom">Where the singing stops, or null for a record that sings throughout.</param>
    /// <param name="drumsToo">True when the drums also stop in the break, so there is nothing left
    /// to mix over.</param>
    private static TrackAnalysis Record(
        double duration, double? quietFrom = null, double quietFor = 60, bool drumsToo = false,
        double outro = 0)
    {
        int steps = (int)(duration / EnergyProfile.StepSeconds);
        var voice = new float[steps];
        var bass = new float[steps];

        for (int i = 0; i < steps; i++)
        {
            double at = i * EnergyProfile.StepSeconds;
            bool quiet = quietFrom is { } from && at >= from && at < from + quietFor;

            voice[i] = quiet ? 0.12f : 0.50f;
            bass[i] = quiet && drumsToo ? 0.02f : 0.40f;
        }

        var beats = new List<double>();
        for (double at = 0; at < duration; at += 60.0 / Bpm)
            beats.Add(at);

        return new TrackAnalysis
        {
            FilePath = "/library/long.flac",
            DurationSeconds = duration,
            Bpm = Bpm,
            BpmConfidence = 0.9,
            BeatTimes = beats,
            FirstDownbeat = 0,
            BeatsPerBar = 4,
            EnergyCurve = [0.3f, 0.3f],
            Intensity = 0.5,
            BassCurve = bass,
            MidCurve = bass,
            TopCurve = bass,
            VocalCurve = voice,
            OutroStartSeconds = outro > 0 ? outro : duration - 20,
            AudioEndSeconds = duration,
            AudioStartSeconds = 0,
        };
    }

    private static MixTrack Track(TrackAnalysis analysis) => new()
    {
        Source = analysis.FilePath,
        Path = analysis.FilePath,
        Title = "long",
        TaggedDuration = analysis.DurationSeconds,
        Analysis = analysis,
    };

    [Fact]
    public void A_long_record_is_left_where_the_singing_stops()
    {
        // Six minutes, with the voice out from 3:20 to 4:20.
        var analysis = Record(duration: 360, quietFrom: 200);

        var found = MixOutFinder.Find(analysis, Hold);

        Assert.NotNull(found);
        Assert.InRange(found.Value, 200, 260 - Hold);

        // On a bar line, because that is where the changeover starts.
        Assert.Equal(found.Value, analysis.BarAtOrBefore(found.Value), 3);
    }

    [Fact]
    public void The_whole_changeover_has_to_fall_inside_the_break()
    {
        // The break is shorter than the changeover: the singing would come back half way through the
        // fade, which is what this code exists to prevent.
        var analysis = Record(duration: 360, quietFrom: 200, quietFor: 10);

        Assert.Null(MixOutFinder.Find(analysis, Hold));
    }

    [Fact]
    public void A_break_that_takes_the_drums_with_it_is_not_a_way_out()
    {
        // Nothing left to bring the next record in over: the mix would be two intros.
        var analysis = Record(duration: 360, quietFrom: 200, drumsToo: true);

        Assert.Null(MixOutFinder.Find(analysis, Hold));
    }

    [Fact]
    public void A_record_that_sings_all_the_way_through_is_played_to_its_outro()
    {
        var analysis = Record(duration: 360);

        Assert.Null(MixOutFinder.Find(analysis, Hold));
    }

    [Fact]
    public void A_short_record_is_not_cut_short()
    {
        // Under three minutes: it is not a long record, however much of it is instrumental.
        var analysis = Record(duration: 170, quietFrom: 40);

        Assert.Null(MixOutFinder.Find(analysis, Hold));
    }

    [Fact]
    public void A_record_read_before_the_bands_were_measured_is_played_as_it_always_was()
    {
        var analysis = Record(duration: 360, quietFrom: 200) with { VocalCurve = [], BassCurve = [] };

        Assert.Null(MixOutFinder.Find(analysis, Hold));
    }

    [Fact]
    public void The_planner_leaves_a_long_record_at_the_break_rather_than_at_its_outro()
    {
        var analysis = Record(duration: 360, quietFrom: 200);

        double at = TransitionPlanner.MixOutPoint(Track(analysis));

        // Well before the outro at 5:40, and also before the half-way minimum that protects against a
        // bad outro reading. An early way out is deliberately not held to that minimum.
        Assert.InRange(at, 200, 260 - Hold);
    }

    [Fact]
    public void A_long_record_with_nowhere_to_go_still_mixes_out_at_its_outro()
    {
        var analysis = Record(duration: 360, outro: 330);

        Assert.Equal(330, TransitionPlanner.MixOutPoint(Track(analysis)), 3);
    }
}
