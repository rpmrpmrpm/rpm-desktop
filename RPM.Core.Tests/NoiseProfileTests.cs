using RPM.Core.Audio;

namespace RPM.Core.Tests;

public class NoiseProfileTests
{
    /// <summary>Deterministic white noise at a known RMS, interleaved for <paramref name="channels"/>.</summary>
    private static float[] Noise(int frames, int channels, double rms, int seed = 1)
    {
        var rng = new Random(seed);
        var buf = new float[frames * channels];
        for (int i = 0; i < buf.Length; i++)
        {
            // Box-Muller: Gaussian noise has a flat spectrum, which a floor measurement needs.
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            double g = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
            buf[i] = (float)(g * rms);
        }
        return buf;
    }

    [Fact]
    public void Measure_flat_noise_gives_a_flat_profile()
    {
        var profile = NoiseProfile.Measure(Noise(44100, 2, 0.01), channels: 2, sampleRate: 44100);

        // Skip DC and the top bin: the window's edges make both unrepresentative.
        var body = profile.Magnitudes.Skip(4).Take(profile.Bins - 8).ToList();
        double mean = body.Average();
        Assert.All(body, m => Assert.InRange(m, mean * 0.45, mean * 2.2));
    }

    [Fact]
    public void Measure_scales_with_the_level_of_the_noise()
    {
        var quiet = NoiseProfile.Measure(Noise(44100, 2, 0.001), 2, 44100);
        var loud = NoiseProfile.Measure(Noise(44100, 2, 0.01), 2, 44100);

        double ratio = loud.Magnitudes.Skip(4).Take(200).Average()
                     / quiet.Magnitudes.Skip(4).Take(200).Average();
        Assert.InRange(ratio, 8.0, 12.5);   // ten times the amplitude, within measurement spread
    }

    [Fact]
    public void Measure_rejects_a_sample_shorter_than_one_window()
    {
        Assert.Throws<ArgumentException>(() =>
            NoiseProfile.Measure(new float[1000], channels: 2, sampleRate: 44100));
    }

    [Fact]
    public void Round_trips_through_json()
    {
        var profile = NoiseProfile.Measure(Noise(44100, 2, 0.01), 2, 44100);

        string json = System.Text.Json.JsonSerializer.Serialize(profile);
        var back = System.Text.Json.JsonSerializer.Deserialize<NoiseProfile>(json)!;

        Assert.Equal(profile.FftSize, back.FftSize);
        Assert.Equal(profile.SampleRate, back.SampleRate);
        Assert.Equal(profile.Magnitudes, back.Magnitudes);
    }
}
