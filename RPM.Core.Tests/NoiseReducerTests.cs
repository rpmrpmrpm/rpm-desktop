using RPM.Core.Audio;

namespace RPM.Core.Tests;

public class NoiseReducerTests
{
    private const int Rate = 44100;

    private static float[] Noise(int frames, int channels, double rms, int seed = 7)
    {
        var rng = new Random(seed);
        var buf = new float[frames * channels];
        for (int i = 0; i < buf.Length; i++)
        {
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            double g = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
            buf[i] = (float)(g * rms);
        }
        return buf;
    }

    private static float[] Tone(int frames, int channels, double hz, double amplitude)
    {
        var buf = new float[frames * channels];
        for (int f = 0; f < frames; f++)
        {
            float s = (float)(amplitude * Math.Sin(2 * Math.PI * hz * f / Rate));
            for (int c = 0; c < channels; c++) buf[f * channels + c] = s;
        }
        return buf;
    }

    private static float[] Mix(float[] a, float[] b)
    {
        var m = new float[a.Length];
        for (int i = 0; i < a.Length; i++) m[i] = a[i] + b[i];
        return m;
    }

    /// <summary>RMS of the second half, so the reducer's latency and its gate settling are both over.</summary>
    private static double TailRms(float[] buf)
    {
        double sum = 0;
        int from = buf.Length / 2;
        for (int i = from; i < buf.Length; i++) sum += (double)buf[i] * buf[i];
        return Math.Sqrt(sum / (buf.Length - from));
    }

    [Fact]
    public void Noise_gated_well_above_its_own_floor_drops_by_the_set_reduction()
    {
        var noise = Noise(Rate * 2, 2, 0.01);
        var profile = NoiseProfile.Measure(noise, 2, Rate);

        // Sensitivity 12 puts the threshold 12 dB above the mean power of the floor. A bin of Gaussian
        // noise exceeds that about once in seven million, so with the smoothing off nearly every bin
        // stays closed and the full reduction is applied.
        var reducer = new NoiseReducer(
            profile,
            new NoiseReductionSettings(ReductionDb: 20, Sensitivity: 12, FrequencySmoothingBands: 0),
            Rate, 2);

        double before = TailRms(noise);
        var work = (float[])noise.Clone();
        reducer.Process(work, work.Length / 2);
        double after = TailRms(work);

        Assert.InRange(20 * Math.Log10(before / after), 16, 24);
    }

    [Fact]
    public void At_the_default_sensitivity_noise_drops_by_less_than_the_nominal_reduction()
    {
        // This looks like a bug but is not one. The gate reopens on the peaks of the noise itself:
        // at 6 dB above the mean, about one bin in fifty-five exceeds the threshold each window, and
        // the frequency smoothing opens six neighbours with each of them. Audacity's reduction figure
        // is a maximum for the same reason, which is why it also has a sensitivity control and not
        // only a reduction one.
        var noise = Noise(Rate * 2, 2, 0.01);
        var profile = NoiseProfile.Measure(noise, 2, Rate);
        var reducer = new NoiseReducer(profile, new NoiseReductionSettings(ReductionDb: 20), Rate, 2);

        double before = TailRms(noise);
        var work = (float[])noise.Clone();
        reducer.Process(work, work.Length / 2);
        double after = TailRms(work);

        double dropDb = 20 * Math.Log10(before / after);
        Assert.InRange(dropDb, 4, 12);
    }

    [Fact]
    public void A_tone_well_above_the_floor_survives()
    {
        var noise = Noise(Rate * 2, 2, 0.001);
        var profile = NoiseProfile.Measure(noise, 2, Rate);
        var signal = Mix(noise, Tone(Rate * 2, 2, 1000, 0.3));

        var reducer = new NoiseReducer(profile, new NoiseReductionSettings(ReductionDb: 20), Rate, 2);
        double before = TailRms(signal);
        reducer.Process(signal, signal.Length / 2);
        double after = TailRms(signal);

        Assert.InRange(20 * Math.Log10(before / after), -0.5, 0.7);
    }

    [Fact]
    public void Silence_in_is_silence_out()
    {
        var profile = NoiseProfile.Measure(Noise(Rate, 2, 0.01), 2, Rate);
        var reducer = new NoiseReducer(profile, new NoiseReductionSettings(), Rate, 2);

        var buf = new float[Rate * 2];
        reducer.Process(buf, buf.Length / 2);

        Assert.All(buf, s => Assert.InRange(s, -1e-6f, 1e-6f));
    }

    [Fact]
    public void The_result_does_not_depend_on_the_block_size()
    {
        var profile = NoiseProfile.Measure(Noise(Rate, 2, 0.01), 2, Rate);
        var source = Mix(Noise(Rate, 2, 0.01, seed: 11), Tone(Rate, 2, 440, 0.2));

        var wholeAtOnce = (float[])source.Clone();
        new NoiseReducer(profile, new NoiseReductionSettings(), Rate, 2)
            .Process(wholeAtOnce, wholeAtOnce.Length / 2);

        var inBlocks = (float[])source.Clone();
        var blocked = new NoiseReducer(profile, new NoiseReductionSettings(), Rate, 2);
        const int blockFrames = 2205;   // the recorder's own block, ~50 ms
        for (int f = 0; f < inBlocks.Length / 2; f += blockFrames)
        {
            int frames = Math.Min(blockFrames, inBlocks.Length / 2 - f);
            var block = new float[frames * 2];
            Array.Copy(inBlocks, f * 2, block, 0, block.Length);
            blocked.Process(block, frames);
            Array.Copy(block, 0, inBlocks, f * 2, block.Length);
        }

        for (int i = 0; i < wholeAtOnce.Length; i++)
            Assert.Equal(wholeAtOnce[i], inBlocks[i], 5);
    }

    [Fact]
    public void Reset_clears_the_tail()
    {
        var profile = NoiseProfile.Measure(Noise(Rate, 2, 0.001), 2, Rate);
        var reducer = new NoiseReducer(profile, new NoiseReductionSettings(), Rate, 2);

        var loud = Tone(Rate / 2, 2, 440, 0.8);
        reducer.Process(loud, loud.Length / 2);
        reducer.Reset();

        var quiet = new float[Rate];
        reducer.Process(quiet, quiet.Length / 2);
        Assert.All(quiet, s => Assert.InRange(s, -1e-6f, 1e-6f));
    }

    [Fact]
    public void A_profile_from_another_sample_rate_is_rejected()
    {
        var profile = NoiseProfile.Measure(Noise(Rate, 2, 0.01), 2, 48000);
        Assert.Throws<ArgumentException>(() =>
            new NoiseReducer(profile, new NoiseReductionSettings(), Rate, 2));
    }
}
