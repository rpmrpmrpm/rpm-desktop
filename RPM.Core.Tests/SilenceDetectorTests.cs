using RPM.Core.Splitting;

namespace RPM.Core.Tests;

public class SilenceDetectorTests
{
    private const int Rate = 8000;
    private static readonly SilenceDetectorOptions Opt = new() { MinTrackSeconds = 10, MinSilenceSeconds = 0.8 };

    /// <summary>Synthesises seconds of sine at the given peak amplitude.</summary>
    private static float[] Tone(double seconds, double amplitude, double hz = 440)
    {
        var samples = new float[(int)(seconds * Rate)];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate));
        return samples;
    }

    private static float[] Envelope(params float[][] segments)
    {
        var acc = new SilenceDetector.EnvelopeAccumulator(Rate, 0.05);
        foreach (var seg in segments)
            acc.Add(seg);
        return acc.ToDbEnvelope();
    }

    [Fact]
    public void Detects_gap_between_tones()
    {
        var env = Envelope(Tone(30, 0.5), Tone(2, 0.0), Tone(30, 0.5));

        var silences = SilenceDetector.Detect(env, 0.05, Opt);

        var s = Assert.Single(silences);
        Assert.InRange(s.MidSec, 29.5, 32.5);
        Assert.True(s.EndSec - s.StartSec >= 1.5);
    }

    [Fact]
    public void Gapless_audio_detects_nothing()
    {
        var env = Envelope(Tone(120, 0.4));
        Assert.Empty(SilenceDetector.Detect(env, 0.05, Opt));
    }

    [Fact]
    public void Short_musical_rest_is_not_a_boundary()
    {
        var env = Envelope(Tone(30, 0.5), Tone(0.4, 0.0), Tone(30, 0.5));
        Assert.Empty(SilenceDetector.Detect(env, 0.05, Opt));
    }

    [Fact]
    public void Vinyl_crackle_floor_raises_threshold()
    {
        // "Silence" here is -50 dB surface noise (amplitude ~0.003), music at -6 dB.
        var env = Envelope(Tone(30, 0.5), Tone(2, 0.003, hz: 3000), Tone(30, 0.5));

        var silences = SilenceDetector.Detect(env, 0.05, Opt);

        var s = Assert.Single(silences);
        Assert.InRange(s.MidSec, 29.5, 32.5);
    }

    [Fact]
    public void Quiet_fade_does_not_split_track()
    {
        // A fade-out into the next loud section: level drops but never near the floor.
        var fade = new float[(int)(3.0 * Rate)];
        for (int i = 0; i < fade.Length; i++)
        {
            double amp = 0.5 * (1 - (double)i / fade.Length) + 0.05; // 0.55 → 0.05, still audible
            fade[i] = (float)(amp * Math.Sin(2 * Math.PI * 440 * i / Rate));
        }
        var env = Envelope(Tone(30, 0.5), fade, Tone(30, 0.5));

        Assert.Empty(SilenceDetector.Detect(env, 0.05, Opt));
    }

    [Fact]
    public void Provisional_tracks_merge_short_segments()
    {
        var silences = new[]
        {
            new SilenceCandidate(29, 31, 30, -80),
            new SilenceCandidate(33, 35, 34, -80),   // only 4s after previous boundary → merged
            new SilenceCandidate(59, 61, 60, -80),
        };

        var tracks = SilenceDetector.ToProvisionalTracks(silences, totalSeconds: 90, Opt);

        Assert.Equal(3, tracks.Count);
        Assert.Equal(new[] { 0.0, 30.0, 60.0 }, tracks.Select(t => t.StartSeconds).ToArray());
        Assert.Equal(90, tracks[^1].EndSeconds, 3);
        Assert.All(tracks, t => Assert.Equal(BoundarySource.Silence, t.Source));
        Assert.Equal(new uint[] { 1, 2, 3 }, tracks.Select(t => t.TrackNumber).ToArray());
    }

    [Fact]
    public void Leading_and_trailing_silence_are_not_boundaries()
    {
        var silences = new[]
        {
            new SilenceCandidate(0, 3, 1.5, -80),
            new SilenceCandidate(44, 46, 45, -80),
            new SilenceCandidate(88, 90, 89, -80),
        };

        var tracks = SilenceDetector.ToProvisionalTracks(silences, totalSeconds: 90, Opt);

        Assert.Equal(2, tracks.Count);
        Assert.Equal(45, tracks[1].StartSeconds, 3);
    }

    [Fact]
    public void Snap_moves_to_quietest_window()
    {
        var env = Envelope(Tone(10, 0.5), Tone(1, 0.0), Tone(10, 0.5));

        double snapped = SilenceDetector.SnapToLocalMinimum(env, 0.05, targetSec: 9.0, searchRadiusSec: 3.0);

        Assert.InRange(snapped, 9.9, 11.1);
    }

    [Fact]
    public void Accumulator_windows_partial_tail()
    {
        var acc = new SilenceDetector.EnvelopeAccumulator(Rate, 0.05);
        acc.Add(Tone(0.07, 0.5)); // 1.4 windows
        var env = acc.ToDbEnvelope();

        Assert.Equal(2, env.Length);
    }
}
