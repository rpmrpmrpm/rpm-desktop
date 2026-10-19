using RPM.Core.Splitting;

namespace RPM.Core.Tests;

public class DemoSideTests
{
    // The six-track side the gallery's split demo uses.
    private static readonly double[] Tracks = [212, 176, 258, 149, 231, 194];

    [Fact]
    public void Envelope_length_matches_the_side()
    {
        var scan = DemoSide.Envelope(Tracks);
        var (env, window, total) = (scan.EnvelopeDb, scan.WindowSeconds, scan.TotalSeconds);
        Assert.NotEmpty(env);
        Assert.Equal(env.Length * window, total, precision: 6);
        // Lead-in + tracks + 5 gaps + run-out, give or take a window of rounding.
        var expected = 4 + Tracks.Sum() + 5 * 2.3 + 5;
        Assert.True(Math.Abs(total - expected) < 1.0, $"total {total} vs expected {expected}");
    }

    [Fact]
    public void The_real_detector_finds_the_grooves_as_silences()
    {
        var scan = DemoSide.Envelope(Tracks);
        var (env, window) = (scan.EnvelopeDb, scan.WindowSeconds);
        var options = new SilenceDetectorOptions();

        var silences = SilenceDetector.Detect(env, window, options);

        // Five inter-track grooves (the lead-in/run-out sit at the very ends). The detector may or
        // may not also flag those, so require at least the five that separate tracks.
        Assert.True(silences.Count >= 5, $"found {silences.Count} silences, expected >= 5");
    }

    [Fact]
    public void The_side_resolves_to_six_editable_tracks()
    {
        var scan = DemoSide.Envelope(Tracks);
        var (env, window, total) = (scan.EnvelopeDb, scan.WindowSeconds, scan.TotalSeconds);
        var options = new SilenceDetectorOptions();

        var silences = SilenceDetector.Detect(env, window, options);
        var provisional = SilenceDetector.ToProvisionalTracks(silences, total, options);

        Assert.Equal(6, provisional.Count);
        // Every proposed track is longer than the minimum length: none is so short that the dialog
        // would merge it.
        Assert.All(provisional, t =>
            Assert.True(t.EndSeconds - t.StartSeconds >= options.MinTrackSeconds,
                $"track {t.TrackNumber} is {t.EndSeconds - t.StartSeconds:0}s"));
        // And together they cover the whole side, end to end.
        Assert.Equal(0, provisional[0].StartSeconds, precision: 3);
        Assert.Equal(total, provisional[^1].EndSeconds, precision: 3);
    }

    [Fact]
    public void The_side_carries_a_waveform_for_the_editor_to_draw()
    {
        var scan = DemoSide.Envelope(Tracks);

        Assert.NotNull(scan.Peaks);
        Assert.Equal(scan.TotalSeconds, scan.Peaks!.TotalSeconds, precision: 1);
        // Music is louder than groove noise, which is why the scan looks at it.
        Assert.True(scan.Peaks.Range(10, 12, out _, out var music));
        Assert.True(scan.Peaks.Range(0.5, 2, out _, out var groove));
        Assert.True(music > groove * 4, $"music {music} vs groove {groove}");
    }
}
