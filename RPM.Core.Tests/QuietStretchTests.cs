using RPM.Core.Recording;
using RPM.Core.Splitting;

namespace RPM.Core.Tests;

/// <summary>
/// Which stretch "Select a gap for me" selects for the noise profile: the quietest gap between two
/// tracks on one side, trimmed at its edges and capped in length.
/// </summary>
public class QuietStretchTests
{
    private const double Window = 0.05;

    /// <summary>An envelope of <paramref name="seconds"/> at -20 dB, with the given stretches set to
    /// their own level.</summary>
    private static float[] Envelope(double seconds, params (double from, double to, float db)[] quiet)
    {
        var env = new float[(int)Math.Round(seconds / Window)];
        Array.Fill(env, -20f);
        foreach (var (from, to, db) in quiet)
            for (int i = (int)(from / Window); i < (int)(to / Window); i++)
                env[i] = db;
        return env;
    }

    [Fact]
    public void The_quieter_of_two_gaps_is_picked()
    {
        TrackSpan[] tracks = [new(0, 60), new(63, 120), new(123, 180)];
        var env = Envelope(180, (60, 63, -50), (120, 123, -62));

        var picked = QuietStretch.Pick(tracks, [], env, Window);

        Assert.NotNull(picked);
        Assert.Equal(120 + QuietStretch.EdgeSeconds, picked.Value.Start, 3);
        Assert.Equal(123 - QuietStretch.EdgeSeconds, picked.Value.End, 3);
    }

    [Fact]
    public void A_gap_that_holds_a_side_join_is_never_picked()
    {
        // The flip is the quietest thing on the recording, and still not the record's noise.
        TrackSpan[] tracks = [new(0, 60), new(63, 120), new(140, 200)];
        var env = Envelope(200, (60, 63, -50), (120, 140, -90));

        var picked = QuietStretch.Pick(tracks, [130], env, Window);

        Assert.NotNull(picked);
        Assert.True(picked.Value.End <= 63);
    }

    [Fact]
    public void A_join_on_a_track_edge_still_rules_the_gap_out()
    {
        TrackSpan[] tracks = [new(0, 60), new(66, 120)];
        var env = Envelope(120, (60, 66, -60));

        Assert.Null(QuietStretch.Pick(tracks, [60], env, Window));
    }

    [Fact]
    public void A_gap_too_short_once_its_edges_are_trimmed_is_skipped()
    {
        // 0.8 s between the tracks leaves 0.4 s after 0.2 s off each end, under the half second.
        TrackSpan[] tracks = [new(0, 60), new(60.8, 120)];
        var env = Envelope(120, (60, 60.8, -60));

        Assert.Null(QuietStretch.Pick(tracks, [], env, Window));
    }

    [Fact]
    public void A_long_gap_is_cut_down_to_its_middle()
    {
        TrackSpan[] tracks = [new(0, 60), new(80, 120)];
        var env = Envelope(120, (60, 80, -60));

        var picked = QuietStretch.Pick(tracks, [], env, Window);

        Assert.NotNull(picked);
        Assert.Equal(QuietStretch.MaxSeconds, picked.Value.Length, 3);
        Assert.Equal(70, (picked.Value.Start + picked.Value.End) / 2, 3);
    }

    [Fact]
    public void Nothing_is_picked_without_two_tracks()
    {
        var env = Envelope(60);

        Assert.Null(QuietStretch.Pick([], [], env, Window));
        Assert.Null(QuietStretch.Pick([new(2, 58)], [], env, Window));
    }
}
