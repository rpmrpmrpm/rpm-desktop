using RPM.Core.Splitting;

namespace RPM.Core.Tests;

/// <summary>The waveform's source data: streamed PCM folded into per-bucket extremes, and the range
/// query one drawn column asks.</summary>
public class WavePeaksTests
{
    [Fact]
    public void Buckets_keep_the_extremes_of_the_samples_they_cover()
    {
        var acc = new WavePeaks.Accumulator(sampleRate: 10, bucketSeconds: 0.5);   // 5 samples a bucket
        acc.Add([0.1f, -0.4f, 0.9f, 0.2f, -0.1f]);
        acc.Add([0.0f, 0.05f, -0.02f, 0.01f, 0.0f]);
        var peaks = acc.ToPeaks();

        Assert.Equal(2, peaks.Count);
        Assert.Equal(0.5, peaks.BucketSeconds, 6);
        Assert.True(peaks.Range(0, 0.5, out var min, out var max));
        Assert.Equal(-0.4f, min, 5);
        Assert.Equal(0.9f, max, 5);

        Assert.True(peaks.Range(0.5, 1.0, out min, out max));
        Assert.Equal(-0.02f, min, 5);
        Assert.Equal(0.05f, max, 5);
    }

    [Fact]
    public void A_partial_last_bucket_is_still_flushed()
    {
        var acc = new WavePeaks.Accumulator(sampleRate: 10, bucketSeconds: 0.5);
        acc.Add([0.2f, 0.3f]);   // two of five

        var peaks = acc.ToPeaks();
        Assert.Equal(1, peaks.Count);
        Assert.True(peaks.Range(0, 0.5, out _, out var max));
        Assert.Equal(0.3f, max, 5);
    }

    [Fact]
    public void Chunk_boundaries_do_not_break_bucketing()
    {
        var whole = new WavePeaks.Accumulator(10, 0.5);
        whole.Add([0.1f, -0.4f, 0.9f, 0.2f, -0.1f, 0.0f, 0.05f, -0.02f, 0.01f, 0.0f]);

        var split = new WavePeaks.Accumulator(10, 0.5);
        split.Add([0.1f, -0.4f, 0.9f]);
        split.Add([0.2f, -0.1f, 0.0f, 0.05f]);
        split.Add([-0.02f, 0.01f, 0.0f]);

        var a = whole.ToPeaks();
        var b = split.ToPeaks();
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            double at = i * a.BucketSeconds;
            Assert.True(a.Range(at, at + a.BucketSeconds, out var aMin, out var aMax));
            Assert.True(b.Range(at, at + b.BucketSeconds, out var bMin, out var bMax));
            Assert.Equal(aMin, bMin, 5);
            Assert.Equal(aMax, bMax, 5);
        }
    }

    [Fact]
    public void A_column_spanning_many_buckets_takes_the_extremes_of_all_of_them()
    {
        var acc = new WavePeaks.Accumulator(100, 0.01);   // one sample a bucket
        var samples = new float[100];
        samples[42] = 0.8f;
        samples[77] = -0.6f;
        acc.Add(samples);
        var peaks = acc.ToPeaks();

        Assert.True(peaks.Range(0, 1, out var min, out var max));
        Assert.Equal(-0.6f, min, 5);
        Assert.Equal(0.8f, max, 5);
    }

    [Fact]
    public void Ranges_outside_the_recording_report_nothing_rather_than_silence()
    {
        var acc = new WavePeaks.Accumulator(10, 0.5);
        acc.Add([0.5f, 0.5f, 0.5f, 0.5f, 0.5f]);
        var peaks = acc.ToPeaks();

        Assert.False(peaks.Range(5, 6, out _, out _));
        Assert.False(peaks.Range(-2, -1, out _, out _));
        Assert.False(peaks.Range(0.2, 0.2, out _, out _));
    }
}
