using RPM.Core.Analysis;

namespace RPM.Core.Tests;

/// <summary>
/// Fitting a beat grid from another user's rip onto this machine's rip.
///
/// <para>An analysis found by fingerprint describes the music, not the file. Two people who ripped
/// the same CD have copies that start at different samples, so the grid arrives offset by the
/// difference between their drives. These tests check that the grid is moved onto this copy when
/// possible, and refused when not. A grid that is half a beat out for a whole track is worse than no
/// grid: with no grid the DJ blends instead, which sounds intended.</para>
/// </summary>
public class GridRealignTests
{
    private const int Rate = 22050;
    private const double Bpm = 120;

    /// <summary>Onsets at every beat of a 120 BPM track, as an envelope at the analyser's own frame
    /// rate: quiet everywhere, loud on the beat.</summary>
    private static float[] Onsets(double seconds, double firstBeatAt)
    {
        double rate = OnsetEnvelope.FrameRate(Rate);
        var frames = new float[(int)(seconds * rate)];
        for (int i = 0; i < frames.Length; i++)
            frames[i] = 0.05f;

        for (double at = firstBeatAt; at < seconds; at += 60.0 / Bpm)
        {
            int frame = (int)Math.Round(at * rate);
            if (frame >= 0 && frame < frames.Length)
                frames[frame] = 1f;
        }

        return frames;
    }

    private static TrackAnalysis Borrowed(double firstBeatAt, double duration = 120, double audioStart = 0)
    {
        var beats = new List<double>();
        for (double at = firstBeatAt; at < duration; at += 60.0 / Bpm)
            beats.Add(at);

        return new TrackAnalysis
        {
            FilePath = "/somebody/elses/rip.flac",
            DurationSeconds = duration,
            Bpm = Bpm,
            BpmConfidence = 0.9,
            BeatTimes = beats,
            FirstDownbeat = 0,
            BeatsPerBar = 4,
            Key = new MusicalKey(9, true),
            KeyConfidence = 0.8,
            AudioStartSeconds = audioStart,
            AudioEndSeconds = duration,
            IntroEndSeconds = 8,
            OutroStartSeconds = 100,
            Version = TrackAnalysis.ModelledVersion,
        };
    }

    /// <summary>The normal case: the other machine's rip has half a second more silence at the start
    /// than ours, so every beat in its grid is half a second late for our file.</summary>
    [Fact]
    public void A_grid_from_another_rip_is_moved_onto_this_one()
    {
        // Ours starts at 0; theirs at 0.5, and their grid is written against their own file.
        var theirs = Borrowed(firstBeatAt: 0.5, audioStart: 0.5);
        var ourOnsets = Onsets(120, firstBeatAt: 0);

        var fitted = GridRealign.Fit(
            theirs, ourDuration: 120, new AudioSpan(0, 120), ourOnsets, OnsetEnvelope.FrameRate(Rate));

        Assert.True(fitted.Kept);
        Assert.Equal(-0.5, fitted.ShiftSeconds, 2);
        Assert.True(fitted.Agreement > 0.5, $"agreement {fitted.Agreement:F2}");

        // The returned beats match this file.
        Assert.Equal(0, fitted.Analysis.BeatTimes[0], 2);
        Assert.Equal(0.5, fitted.Analysis.BeatTimes[1], 2);
    }

    /// <summary>The audio edges are found with a threshold and can differ by tens of milliseconds
    /// between two rips. The onsets decide the final offset, so a wrong edge estimate is
    /// corrected.</summary>
    [Fact]
    public void The_onsets_correct_what_the_edges_only_guessed()
    {
        var theirs = Borrowed(firstBeatAt: 0.5, audioStart: 0.5);
        var ourOnsets = Onsets(120, firstBeatAt: 0);

        // The edges say the two rips are 0.35 s apart. They are 0.5 s apart.
        var fitted = GridRealign.Fit(
            theirs, 120, new AudioSpan(0.15, 120), ourOnsets, OnsetEnvelope.FrameRate(Rate));

        Assert.True(fitted.Kept);
        Assert.Equal(-0.5, fitted.ShiftSeconds, 2);
    }

    /// <summary>A remaster or a vinyl transfer can run at a slightly different speed. One offset
    /// cannot fix that (the grid is right at the start and a beat out by the end), and the onset
    /// check alone does not catch it, because the early beats match well.</summary>
    [Fact]
    public void A_different_master_is_refused_rather_than_stretched()
    {
        var theirs = Borrowed(firstBeatAt: 0);
        var ourOnsets = Onsets(120, firstBeatAt: 0);

        // Two percent longer: the same music, a different transfer.
        var fitted = GridRealign.Fit(
            theirs, ourDuration: 122.4, new AudioSpan(0, 122.4), ourOnsets, OnsetEnvelope.FrameRate(Rate));

        Assert.False(fitted.Kept);
        Assert.Empty(fitted.Analysis.BeatTimes);
        Assert.Equal(0, fitted.Analysis.Bpm);

        // Values that do not depend on where the file starts are kept. The key belongs to the music,
        // so the analysis is reduced rather than dropped.
        Assert.Equal(new MusicalKey(9, true), fitted.Analysis.Key);
    }

    /// <summary>Two different recordings that matched by mistake. The beats match no onsets, so the
    /// grid is not used.</summary>
    [Fact]
    public void A_grid_that_lands_on_nothing_is_thrown_away()
    {
        var theirs = Borrowed(firstBeatAt: 0);

        // A track with no beat at all: even noise.
        var flat = new float[(int)(120 * OnsetEnvelope.FrameRate(Rate))];
        Array.Fill(flat, 0.2f);

        var fitted = GridRealign.Fit(
            theirs, 120, new AudioSpan(0, 120), flat, OnsetEnvelope.FrameRate(Rate));

        Assert.False(fitted.Kept);
        Assert.Empty(fitted.Analysis.BeatTimes);
    }

    /// <summary>The cues and the drops are times in the same file, so they move with the grid.</summary>
    [Fact]
    public void Everything_that_is_a_time_moves_with_the_grid()
    {
        var theirs = Borrowed(firstBeatAt: 0.5, audioStart: 0.5) with { DropSeconds = [30.5, 60.5] };
        var ourOnsets = Onsets(120, firstBeatAt: 0);

        var fitted = GridRealign.Fit(
            theirs, 120, new AudioSpan(0, 120), ourOnsets, OnsetEnvelope.FrameRate(Rate));

        Assert.True(fitted.Kept);
        Assert.Equal(7.5, fitted.Analysis.IntroEndSeconds, 2);
        Assert.Equal(99.5, fitted.Analysis.OutroStartSeconds, 2);
        Assert.Equal([30, 60], fitted.Analysis.DropSeconds.Select(d => Math.Round(d)));
    }

    /// <summary>An analysis with no grid has nothing to fit and is passed through.</summary>
    [Fact]
    public void A_reading_with_no_grid_passes_straight_through()
    {
        var theirs = Borrowed(0) with { BeatTimes = [], Bpm = 0 };

        var fitted = GridRealign.Fit(theirs, 120, new AudioSpan(0, 120), [], OnsetEnvelope.FrameRate(Rate));

        Assert.False(fitted.Kept);
        Assert.Equal(new MusicalKey(9, true), fitted.Analysis.Key);
    }
}
