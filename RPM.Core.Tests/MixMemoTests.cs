using RPM.Core.Analysis;
using RPM.Core.Mixing;

namespace RPM.Core.Tests;

/// <summary>
/// The DJ's measurements are cached (memoised) rather than computed again every block. The cache must
/// never return a stale answer after the reading is edited. These tests check that.
/// </summary>
public class MixMemoTests
{
    [Fact]
    public void The_mix_out_point_follows_an_edited_reading()
    {
        // Under six minutes, so the extended-record ceiling stays out of the way of the question.
        var track = Track(Analysis(bpm: 128, duration: 300, outro: 280));

        Assert.Equal(280, TransitionPlanner.MixOutPoint(track), 1);

        // Asked twice, so the second answer comes from the memo. Then the reading is edited, which
        // makes a new instance, and the memo belongs to the old instance only.
        Assert.Equal(280, TransitionPlanner.MixOutPoint(track), 1);

        var edited = track with { Analysis = track.Analysis! with { OutroStartSeconds = 200 } };
        Assert.Equal(200, TransitionPlanner.MixOutPoint(edited), 1);
    }

    [Fact]
    public void The_mix_out_point_is_retaken_when_the_record_length_changes()
    {
        // The same reading under a different tagged length: rare, but the memo must not let the
        // reading answer for a record it was never measured against.
        var analysis = Analysis(bpm: 128, duration: 300, outro: 280) with { DurationSeconds = 0 };

        var longRecord = new MixTrack
        {
            Source = "x.flac", Path = "x.flac", Title = "t",
            TaggedDuration = 300, Analysis = analysis,
        };
        var shortRecord = longRecord with { TaggedDuration = 100 };

        Assert.Equal(280, TransitionPlanner.MixOutPoint(longRecord), 1);

        // 280 is past the end of a 100 s record; the clamps must be re-run, not remembered.
        Assert.InRange(TransitionPlanner.MixOutPoint(shortRecord), 0, 100);
    }

    [Fact]
    public void Where_the_drums_come_in_follows_a_new_curve()
    {
        var quietOpen = new float[40];
        for (int i = 6; i < quietOpen.Length; i++)
            quietOpen[i] = 0.8f;

        var analysis = Analysis(bpm: 128, duration: 20) with { BassCurve = quietOpen };
        Assert.Equal(6 * EnergyProfile.StepSeconds, analysis.DrumsStartSeconds, 3);

        // Asked again (remembered), then cloned with a different curve: the clone carries the memo
        // field, and it must notice the curve is not the one the memo was taken from.
        Assert.Equal(6 * EnergyProfile.StepSeconds, analysis.DrumsStartSeconds, 3);

        var drumsFromTheTop = new float[40];
        for (int i = 0; i < drumsFromTheTop.Length; i++)
            drumsFromTheTop[i] = 0.8f;

        var recut = analysis with { BassCurve = drumsFromTheTop };
        Assert.Equal(0, recut.DrumsStartSeconds, 3);
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
