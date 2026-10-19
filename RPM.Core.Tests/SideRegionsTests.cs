using RPM.Core.Recording;
using RPM.Core.Splitting;

namespace RPM.Core.Tests;

/// <summary>
/// What a recorded side actually contains: a needle drop, lead-in noise, the music, the run-out, and
/// a needle lift. These pin down that the cut lands on the music and not on the handling noise,
/// especially that a needle drop, which is <i>louder</i> than the music, is not mistaken for it.
/// </summary>
public class SideRegionsTests
{
    private const int Rate = 8000;               // the analyzer's envelope rate
    private const double Window = 0.05;
    private static readonly SilenceDetectorOptions Opt = new() { MinTrackSeconds = 10, MinSilenceSeconds = 0.8 };

    private static float[] Tone(double seconds, double amplitude, double hz = 440)
    {
        var samples = new float[(int)(seconds * Rate)];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate));
        return samples;
    }

    private static float[] Envelope(params float[][] segments)
    {
        var acc = new SilenceDetector.EnvelopeAccumulator(Rate, Window);
        foreach (var seg in segments)
            acc.Add(seg);
        return acc.ToDbEnvelope();
    }

    private static SplitPlan Plan(double total, params (double start, double end)[] tracks) => new(
        "/x/album.flac", ".flac", total, "Album", "Artist", 2020, null,
        tracks.Select((t, i) => new SplitTrack(t.start, t.end, $"T{i + 1}", "Artist",
            (uint)(i + 1), 0.8, BoundarySource.Silence)).ToArray());

    [Fact]
    public void A_needle_drop_is_not_mistaken_for_the_music_starting()
    {
        // Lead-in: 5 s of quiet, a 0.2 s THUMP louder than the music, 5 s of quiet, then the record.
        var env = Envelope(
            Tone(5, 0.0005),        // lead-in groove noise
            Tone(0.2, 0.9),         // needle drop, loud, but a transient
            Tone(5, 0.0005),        // still lead-in
            Tone(60, 0.4),          // the music
            Tone(6, 0.0005));       // run-out
        double total = env.Length * Window;

        var region = Assert.Single(SideRegions.Detect(env, Window, total, [], Opt));

        // Music starts ~10.2 s in. The thump at 5 s must not have claimed the start.
        Assert.InRange(region.StartSeconds, 9.5, 10.5);
        Assert.InRange(region.EndSeconds, 69.5, 71.0);
    }

    [Fact]
    public void The_run_out_is_trimmed_off_the_end()
    {
        var env = Envelope(Tone(30, 0.4), Tone(20, 0.0005));
        double total = env.Length * Window;

        var region = Assert.Single(SideRegions.Detect(env, Window, total, [], Opt));

        Assert.InRange(region.StartSeconds, 0, 0.5);
        Assert.InRange(region.EndSeconds, 29.5, 31.0);   // not the full 50 s
    }

    [Fact]
    public void Each_side_gets_its_own_region()
    {
        // Side 1: quiet, music, quiet. Side 2 (joined at 40 s): quiet, music, quiet.
        var env = Envelope(
            Tone(5, 0.0005), Tone(30, 0.4), Tone(5, 0.0005),
            Tone(5, 0.0005), Tone(30, 0.4), Tone(5, 0.0005));
        double total = env.Length * Window;

        var regions = SideRegions.Detect(env, Window, total, new[] { 40.0 }, Opt);

        Assert.Equal(2, regions.Count);
        Assert.InRange(regions[0].StartSeconds, 4.5, 5.5);
        Assert.InRange(regions[0].EndSeconds, 34.5, 36.0);
        Assert.InRange(regions[1].StartSeconds, 44.5, 45.5);
        Assert.InRange(regions[1].EndSeconds, 74.5, 76.0);
    }

    [Fact]
    public void A_silent_side_is_left_untrimmed_rather_than_thrown_away()
    {
        var env = Envelope(Tone(30, 0.0));
        double total = env.Length * Window;

        var region = Assert.Single(SideRegions.Detect(env, Window, total, [], Opt));

        Assert.Equal(0, region.StartSeconds, 3);
        Assert.Equal(total, region.EndSeconds, 3);
    }

    // ── Apply ──

    [Fact]
    public void Apply_clips_tracks_to_the_music()
    {
        // Detection said the album runs 0 to 100; the music is only 10 to 90.
        var plan = Plan(100, (0, 50), (50, 100));
        var regions = new[] { new SideRegion(10, 90) };

        var result = SideRegions.Apply(plan, regions, minTrackSeconds: 5);

        Assert.Equal(2, result.Tracks.Count);
        Assert.Equal(10, result.Tracks[0].StartSeconds, 3);   // lead-in trimmed off track 1
        Assert.Equal(90, result.Tracks[1].EndSeconds, 3);     // run-out trimmed off the last
    }

    [Fact]
    public void Apply_drops_a_track_that_is_only_dead_air_between_sides()
    {
        // A "track" the detector invented inside the flip gap (45 to 55) has no music in it.
        var plan = Plan(100, (0, 45), (45, 55), (55, 100));
        var regions = new[] { new SideRegion(0, 45), new SideRegion(55, 100) };

        var result = SideRegions.Apply(plan, regions, minTrackSeconds: 5);

        Assert.Equal(2, result.Tracks.Count);
        Assert.Equal(new uint[] { 1, 2 }, result.Tracks.Select(t => t.TrackNumber).ToArray());
    }

    [Fact]
    public void Apply_splits_a_track_that_would_bridge_a_side_flip()
    {
        var plan = Plan(100, (0, 100));
        var regions = new[] { new SideRegion(0, 45), new SideRegion(55, 100) };

        var result = SideRegions.Apply(plan, regions, minTrackSeconds: 5);

        Assert.Equal(2, result.Tracks.Count);
        Assert.Equal(45, result.Tracks[0].EndSeconds, 3);
        Assert.Equal(55, result.Tracks[1].StartSeconds, 3);
        Assert.Equal("T1", result.Tracks[0].Title);
        Assert.Null(result.Tracks[1].Title);       // the far half is a different song; don't claim it
    }

    /// <summary>Clipping can leave a sliver where a forced side boundary cuts a track (for example a
    /// ten-second track). A sliver is an artefact, not a song, so it is merged back into its
    /// neighbour.</summary>
    [Fact]
    public void Apply_merges_a_sliver_into_its_neighbour_rather_than_calling_it_a_track()
    {
        // The second "track" is 10 s, below the bar the detector holds its own boundaries to.
        var plan = Plan(200, (0, 90), (90, 100), (100, 200));
        var regions = new[] { new SideRegion(0, 200) };

        var result = SideRegions.Apply(plan, regions, minTrackSeconds: 60);

        Assert.Equal(2, result.Tracks.Count);
        Assert.Equal(0, result.Tracks[0].StartSeconds, 3);
        Assert.Equal(100, result.Tracks[0].EndSeconds, 3);   // sliver folded into the track before it
        Assert.Equal(100, result.Tracks[1].StartSeconds, 3);
        Assert.Equal(200, result.Tracks[1].EndSeconds, 3);   // no audio lost
    }

    [Fact]
    public void Apply_keeps_a_short_side_that_is_all_there_is()
    {
        // One piece, under the bar: merging it away would leave nothing to cut.
        var plan = Plan(30, (0, 30));
        var result = SideRegions.Apply(plan, new[] { new SideRegion(0, 30) }, minTrackSeconds: 60);
        Assert.Single(result.Tracks);
    }

    // ── TrimTracksToMusic ──

    /// <summary>
    /// A boundary lands on the QUIETEST point of the gap, which is its middle. So track 2 would begin
    /// with half a gap of silence and "start too soon". Each track must end up holding its own music
    /// and none of the silence around it.
    /// </summary>
    [Fact]
    public void Each_track_is_trimmed_to_its_own_music_not_to_the_middle_of_the_gap()
    {
        // 20 s music | 6 s perfect silence | 20 s music. The detector cuts at ~23 s, mid-gap.
        var env = Envelope(Tone(20, 0.4), Tone(6, 0.0), Tone(20, 0.4));
        double total = env.Length * Window;
        var plan = Plan(total, (0, 23), (23, total));

        var result = SideRegions.TrimTracksToMusic(plan, env, Window, Opt);

        // Track 1 ends where its music ends (~20 s), not at the mid-gap cut.
        Assert.InRange(result.Tracks[0].StartSeconds, 0, 0.5);
        Assert.InRange(result.Tracks[0].EndSeconds, 19.5, 21.0);
        // Track 2 starts where its music starts (~26 s), not at 23 s.
        Assert.InRange(result.Tracks[1].StartSeconds, 25.5, 26.5);
        Assert.InRange(result.Tracks[1].EndSeconds, 45.5, 46.5);
    }

    [Fact]
    public void A_segue_is_left_alone()
    {
        // No gap at all: the sound never stops, so there is nothing to trim off either track.
        var env = Envelope(Tone(40, 0.4));
        double total = env.Length * Window;
        var plan = Plan(total, (0, 20), (20, total));

        var result = SideRegions.TrimTracksToMusic(plan, env, Window, Opt);

        Assert.InRange(result.Tracks[0].EndSeconds, 19.5, 20.5);
        Assert.InRange(result.Tracks[1].StartSeconds, 19.5, 20.5);
    }

    [Fact]
    public void A_track_of_pure_silence_is_passed_through_untouched()
    {
        var env = Envelope(Tone(20, 0.4), Tone(20, 0.0));
        double total = env.Length * Window;
        var plan = Plan(total, (0, 20), (20, total));

        var result = SideRegions.TrimTracksToMusic(plan, env, Window, Opt);

        Assert.Equal(20, result.Tracks[1].StartSeconds, 3);
        Assert.Equal(total, result.Tracks[1].EndSeconds, 3);
    }

    [Fact]
    public void Apply_without_regions_changes_nothing()
    {
        var plan = Plan(100, (0, 50), (50, 100));
        Assert.Same(plan, SideRegions.Apply(plan, [], minTrackSeconds: 5));
    }

    [Fact]
    public void Apply_refuses_to_trim_everything_away()
    {
        // A region that matches no track would empty the plan. That means detection was wrong, and
        // returning nothing to cut is worse than returning the untrimmed plan.
        var plan = Plan(100, (0, 50), (50, 100));
        var result = SideRegions.Apply(plan, new[] { new SideRegion(200, 201) }, minTrackSeconds: 5);
        Assert.Equal(2, result.Tracks.Count);
    }
}
