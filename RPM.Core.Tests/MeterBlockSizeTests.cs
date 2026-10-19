using RPM.Core.Analysis;

namespace RPM.Core.Tests;

/// <summary>
/// The loudness and mood meters give the same reading whatever size of block they are fed.
///
/// <para>LoudnessScanner reads in blocks of <see cref="LoudnessScanner.BlockFrames"/>, chosen small enough
/// that its buffers stay off the large-object heap. That choice is only free because the meters keep their
/// own sub-block and frame counters and accumulate sample by sample; a meter that measured per call would
/// read differently at a different block size. These tests are what would fail.</para>
/// </summary>
public class MeterBlockSizeTests
{
    private const int Rate = LoudnessScanner.Rate;

    /// <summary>A block of one second: the large block size that the scanner's
    /// small blocks are compared with.</summary>
    private const int OneSecond = Rate;

    [Fact]
    public void Loudness_reads_the_same_in_small_blocks_as_in_one_second_blocks()
    {
        var signal = Programme(seconds: 20);

        var whole = new LoudnessMeter(Rate, 2);
        Feed(whole.Add, signal, OneSecond);

        var small = new LoudnessMeter(Rate, 2);
        Feed(small.Add, signal, LoudnessScanner.BlockFrames);

        Assert.Equal(whole.IntegratedLufs, small.IntegratedLufs);
        Assert.Equal(whole.TruePeak, small.TruePeak);
    }

    [Fact]
    public void Mood_reads_the_same_in_small_blocks_as_in_one_second_blocks()
    {
        var signal = Programme(seconds: 20);

        var whole = new MoodMeter(Rate, 2);
        Feed(whole.Add, signal, OneSecond);

        var small = new MoodMeter(Rate, 2);
        Feed(small.Add, signal, LoudnessScanner.BlockFrames);

        Assert.Equal(whole.Reading, small.Reading);
    }

    [Fact]
    public void The_scanners_blocks_stay_off_the_large_object_heap()
    {
        // 85,000 bytes is where the runtime puts an array on the large-object heap. The scanner's float
        // block and the PCM source's byte scratch are both BlockFrames x 2 channels x 4 bytes.
        Assert.True(LoudnessScanner.BlockFrames * 2 * sizeof(float) < 85_000);
    }

    private static void Feed(Action<ReadOnlySpan<float>, int> add, float[] interleaved, int blockFrames)
    {
        var frames = interleaved.Length / 2;
        for (var at = 0; at < frames; at += blockFrames)
        {
            var n = Math.Min(blockFrames, frames - at);
            add(interleaved.AsSpan(at * 2, n * 2), n);
        }
    }

    /// <summary>Deterministic stereo noise whose level rises and falls, with a quiet passage, so the
    /// gating, the peak and the mood frames all have something to do.</summary>
    private static float[] Programme(int seconds)
    {
        var random = new Random(42);
        var frames = seconds * Rate;
        var samples = new float[frames * 2];
        for (var f = 0; f < frames; f++)
        {
            var t = (double)f / Rate;
            var level = t is > 8 and < 10 ? 0.01 : 0.2 + 0.15 * Math.Sin(t * 0.7);
            var tone = Math.Sin(2 * Math.PI * 440 * t);
            samples[f * 2] = (float)(level * (0.6 * tone + 0.4 * (random.NextDouble() * 2 - 1)));
            samples[f * 2 + 1] = (float)(level * (0.6 * tone + 0.4 * (random.NextDouble() * 2 - 1)));
        }

        return samples;
    }
}
