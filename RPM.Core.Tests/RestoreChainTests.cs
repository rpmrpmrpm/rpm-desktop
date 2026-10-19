using RPM.Core.Audio;
using RPM.Core.Recording;

namespace RPM.Core.Tests;

public class RestoreChainTests
{
    private const int Rate = 44100;

    private static float[] Tone(int frames, double hz, double amplitude)
    {
        var buf = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            float s = (float)(amplitude * Math.Sin(2 * Math.PI * hz * f / Rate));
            buf[f * 2] = s; buf[f * 2 + 1] = s;
        }
        return buf;
    }

    [Fact]
    public void Everything_off_is_bit_identical_passthrough()
    {
        var chain = new RestoreChain(RestoreSettings.Off, Rate, 2);
        var source = Tone(Rate / 4, 440, 0.5);
        var work = (float[])source.Clone();

        chain.Process(work, work.Length / 2);

        Assert.Equal(source, work);
        Assert.True(chain.IsBypass);
        Assert.Equal(0, chain.LatencyFrames);
    }

    [Fact]
    public void An_equaliser_boost_raises_the_band_it_names()
    {
        // Band 4 is 1000 Hz in Equalizer.Bands.
        var gains = new double[Equalizer.Bands.Length];
        gains[4] = 10;
        var chain = new RestoreChain(
            RestoreSettings.Off with { EqOn = true, Gains = gains }, Rate, 2);

        var work = Tone(Rate, 1000, 0.1);
        chain.Process(work, work.Length / 2);

        // Measure the settled half, so the filter's own start-up is past.
        double rms = 0;
        for (int i = work.Length / 2; i < work.Length; i++) rms += (double)work[i] * work[i];
        rms = Math.Sqrt(rms / (work.Length / 2));

        Assert.InRange(20 * Math.Log10(rms / (0.1 / Math.Sqrt(2))), 7, 11);
    }

    [Fact]
    public void The_equaliser_runs_before_the_gate()
    {
        // A profile measured on the raw noise, with an equaliser cut over the same band. If the gate
        // ran first it would see the untouched noise and gate exactly as the profile describes. Running
        // second, it sees noise that is already pulled down, so more of it falls under the threshold
        // and the result is quieter than the cut alone accounts for.
        var rng = new Random(3);
        var noise = new float[Rate * 2];
        for (int i = 0; i < noise.Length; i++) noise[i] = (float)((rng.NextDouble() - 0.5) * 0.02);

        var profile = NoiseProfile.Measure(noise, 2, Rate);
        var gains = new double[Equalizer.Bands.Length];
        for (int b = 0; b < gains.Length; b++) gains[b] = -6;

        var cutOnly = (float[])noise.Clone();
        new RestoreChain(RestoreSettings.Off with { EqOn = true, Gains = gains }, Rate, 2)
            .Process(cutOnly, cutOnly.Length / 2);

        var both = (float[])noise.Clone();
        new RestoreChain(
            RestoreSettings.Off with
            {
                EqOn = true,
                Gains = gains,
                NoiseOn = true,
                Profile = profile,
                Noise = new NoiseReductionSettings(ReductionDb: 20),
            }, Rate, 2).Process(both, both.Length / 2);

        static double Rms(float[] b)
        {
            double s = 0;
            for (int i = b.Length / 2; i < b.Length; i++) s += (double)b[i] * b[i];
            return Math.Sqrt(s / (b.Length / 2));
        }

        Assert.True(Rms(both) < Rms(cutOnly) * 0.6,
            $"gated {Rms(both):F6} should sit well below cut-only {Rms(cutOnly):F6}");
    }

    [Fact]
    public void Latency_is_the_gates_when_the_gate_is_on()
    {
        var profile = NoiseProfile.Measure(new float[8192], 2, Rate);
        var chain = new RestoreChain(
            RestoreSettings.Off with { NoiseOn = true, Profile = profile }, Rate, 2);

        Assert.Equal(NoiseReducer.FftSize + NoiseReducer.Hop, chain.LatencyFrames);
    }
}
