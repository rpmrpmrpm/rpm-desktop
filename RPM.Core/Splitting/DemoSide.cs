namespace RPM.Core.Splitting;

/// <summary>
/// Builds a fake RMS-in-dB envelope for an album side. Each track is a loud stretch. A quiet groove
/// lies between tracks, and quiet lead-in and run-out sit at the ends. The split pipeline can then
/// run without decoding any audio. The design harness uses this to fill the split dialog with a
/// recording that does not exist.
///
/// <para>The output has the same form as the envelope that the reader in
/// <see cref="AlbumSplitAnalyzer"/> produces. It is therefore also a fixed test input for
/// <see cref="SilenceDetector"/>. The default levels below are chosen so that the detector's default
/// options find the grooves as silence and the tracks as music.</para>
/// </summary>
public static class DemoSide
{
    /// <param name="trackSeconds">Length of each track, in order. Each should exceed the detector's
    /// <see cref="SilenceDetectorOptions.MinTrackSeconds"/> or it will be merged into its
    /// neighbour.</param>
    public static EnvelopeScan Envelope(
        IReadOnlyList<double> trackSeconds,
        double windowSeconds = 0.05,
        double musicDb = -15,
        double grooveDb = -50,
        double gapSeconds = 2.3,
        double leadInSeconds = 4,
        double runOutSeconds = 5)
    {
        var samples = new List<float>();

        void Fill(double seconds, double db, double wobble)
        {
            var n = (int)Math.Round(seconds / windowSeconds);
            for (var i = 0; i < n; i++)
                samples.Add((float)(db + Math.Sin(i * 0.35) * wobble));
        }

        Fill(leadInSeconds, grooveDb, 1.5);
        for (var i = 0; i < trackSeconds.Count; i++)
        {
            Fill(trackSeconds[i], musicDb, 4.0);
            if (i < trackSeconds.Count - 1)
                Fill(gapSeconds, grooveDb, 1.0);
        }
        Fill(runOutSeconds, grooveDb, 1.5);

        var envelope = samples.ToArray();
        return new EnvelopeScan(envelope, windowSeconds, envelope.Length * windowSeconds,
            PeaksFrom(envelope, windowSeconds));
    }

    /// <summary>
    /// Builds a waveform that matches the envelope. The level of each window (50 ms by default)
    /// becomes peaks at the editor's own resolution. Two detuned sine waves shape the peaks, so the
    /// result looks like music and not a flat block. Without this the design harness shows an
    /// empty waveform strip.
    /// </summary>
    private static WavePeaks PeaksFrom(float[] envelopeDb, double windowSeconds)
    {
        int perWindow = Math.Max(1, (int)Math.Round(windowSeconds / WavePeaks.DefaultBucketSeconds));
        var min = new float[envelopeDb.Length * perWindow];
        var max = new float[min.Length];

        for (int w = 0; w < envelopeDb.Length; w++)
        {
            // Convert the RMS level in dB back to a peak amplitude. The factor 3.2 is a typical
            // crest factor (the ratio of peak to RMS) for music.
            float level = (float)Math.Min(1.0, Math.Pow(10, envelopeDb[w] / 20.0) * 3.2);
            for (int i = 0; i < perWindow; i++)
            {
                int at = w * perWindow + i;
                double t = at * WavePeaks.DefaultBucketSeconds;
                float shape = (float)(0.55 + 0.45 * Math.Abs(Math.Sin(t * 2.7) * Math.Cos(t * 0.31)));
                max[at] = level * shape;
                min[at] = -level * shape * (float)(0.8 + 0.2 * Math.Abs(Math.Sin(t * 1.13)));
            }
        }
        return new WavePeaks(min, max, WavePeaks.DefaultBucketSeconds);
    }
}
