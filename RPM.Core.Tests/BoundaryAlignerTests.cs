using RPM.Core.Splitting;

namespace RPM.Core.Tests;

public class BoundaryAlignerTests
{
    private static (string, string?, double)[] Expected(params double[] lengths) =>
        lengths.Select((len, i) => ($"Song {i + 1}", (string?)"Artist", len)).ToArray();

    private static SilenceCandidate Cand(double mid) => new(mid - 1, mid + 1, mid, -80);

    /// <summary>Flat envelope: snapping has nothing to prefer, virtual boundaries stay at target.</summary>
    private static readonly float[] FlatEnv = Enumerable.Repeat(-20f, 12000).ToArray(); // 600s at 50ms

    [Fact]
    public void Exact_durations_match_candidates()
    {
        var result = BoundaryAligner.Align(
            Expected(100, 150, 110),
            new[] { Cand(100), Cand(250) },
            totalSeconds: 360, FlatEnv, 0.05);

        Assert.Equal(3, result.Tracks.Count);
        Assert.Equal(100, result.Tracks[0].EndSeconds, 1);
        Assert.Equal(250, result.Tracks[1].EndSeconds, 1);
        Assert.Equal(360, result.Tracks[2].EndSeconds, 1);
        Assert.All(result.Tracks, t => Assert.Equal(BoundarySource.Silence, t.Source));
        Assert.Equal("Song 2", result.Tracks[1].Title);
        Assert.True(result.MeanErrorSeconds < 0.5);
    }

    [Fact]
    public void Drift_is_rescaled_globally()
    {
        // Rip runs 1.5% long (vinyl speed): candidates sit at scaled positions.
        var result = BoundaryAligner.Align(
            Expected(100, 150, 110),
            new[] { Cand(101.5), Cand(253.75) },
            totalSeconds: 365.4, FlatEnv, 0.05);

        Assert.Equal(101.5, result.Tracks[0].EndSeconds, 1);
        Assert.Equal(253.75, result.Tracks[1].EndSeconds, 1);
        Assert.True(result.MeanErrorSeconds < 0.5);
        Assert.All(result.Tracks, t => Assert.Equal(BoundarySource.Silence, t.Source));
    }

    [Fact]
    public void Missing_candidate_becomes_virtual_boundary()
    {
        // Gapless transition between tracks 2 and 3: no silence at 250.
        var result = BoundaryAligner.Align(
            Expected(100, 150, 110),
            new[] { Cand(100) },
            totalSeconds: 360, FlatEnv, 0.05);

        Assert.Equal(3, result.Tracks.Count);
        Assert.Equal(100, result.Tracks[0].EndSeconds, 1);
        Assert.InRange(result.Tracks[1].EndSeconds, 246, 254); // at expected offset (flat env → stays near 250)
        Assert.Equal(BoundarySource.DurationAligned, result.Tracks[1].Source);
        Assert.Equal(BoundarySource.DurationAligned, result.Tracks[2].Source);
        Assert.Equal(BoundarySource.Silence, result.Tracks[0].Source);
        Assert.True(result.Tracks[1].Confidence < result.Tracks[0].Confidence);
    }

    [Fact]
    public void Extra_mid_track_silences_are_skipped()
    {
        // A quiet passage at 60s and a vinyl side-gap at 180s are not track boundaries.
        var result = BoundaryAligner.Align(
            Expected(100, 150, 110),
            new[] { Cand(60), Cand(100), Cand(180), Cand(250) },
            totalSeconds: 360, FlatEnv, 0.05);

        Assert.Equal(100, result.Tracks[0].EndSeconds, 1);
        Assert.Equal(250, result.Tracks[1].EndSeconds, 1);
        Assert.All(result.Tracks, t => Assert.Equal(BoundarySource.Silence, t.Source));
    }

    [Fact]
    public void Virtual_boundary_snaps_to_quiet_moment()
    {
        // Envelope with a dip at 248s; expected boundary 250 (no candidate) should snap into it.
        var env = (float[])FlatEnv.Clone();
        for (int w = (int)(247.5 / 0.05); w < (int)(248.5 / 0.05); w++)
            env[w] = -90f;

        var result = BoundaryAligner.Align(
            Expected(100, 150, 110),
            new[] { Cand(100) },
            totalSeconds: 360, env, 0.05);

        Assert.InRange(result.Tracks[1].EndSeconds, 247.4, 248.6);
    }

    [Fact]
    public void Single_track_returns_whole_file()
    {
        var result = BoundaryAligner.Align(Expected(360), Array.Empty<SilenceCandidate>(), 360, FlatEnv, 0.05);

        var t = Assert.Single(result.Tracks);
        Assert.Equal(0, t.StartSeconds, 3);
        Assert.Equal(360, t.EndSeconds, 3);
    }

    [Fact]
    public void Empty_expected_returns_empty()
    {
        var result = BoundaryAligner.Align(
            Array.Empty<(string, string?, double)>(), new[] { Cand(10) }, 360, FlatEnv, 0.05);
        Assert.Empty(result.Tracks);
    }
}
