using System.Globalization;
using RPM.Core.Analysis;

namespace RPM.Core.Tests;

/// <summary>
/// Holds the C# CLAP features against the ones the model's own feature extractor builds.
///
/// <para>The embedding, and every feeling list built on it, is the model's reading of these numbers. A
/// feature step that is slightly wrong does not fail. It gives a worse embedding for every song, and
/// nothing reports it. So the port is checked against the extractor itself, on the same signals.</para>
///
/// <para>Assets/clap-mel-reference-10s.txt and -3s.txt are written by scripts/make_clap_reference.py from
/// the signals built below. They are made from a formula, so both sides can build them exactly. Each file
/// holds every tenth frame.</para>
/// </summary>
public class ClapFeaturesTests
{
    private const int Every = 10;

    /// <summary>Three tones and two clicks, as scripts/make_clap_reference.py builds them.</summary>
    private static float[] Signal(int seconds)
    {
        int n = ClapFeatures.SampleRate * seconds;
        var x = new float[n];
        for (int i = 0; i < n; i++)
        {
            double t = 2 * Math.PI * i / ClapFeatures.SampleRate;
            x[i] = (float)(0.5 * Math.Sin(440 * t) + 0.3 * Math.Sin(97 * t) + 0.2 * Math.Sin(3000 * t));
        }
        x[n / 10] += 0.9f;
        x[n / 2] += 0.9f;
        return x;
    }

    private static float[][] Reference(int seconds)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", $"clap-mel-reference-{seconds}s.txt");
        return File.ReadAllLines(path)
            .Where(line => !line.StartsWith('#') && line.Length > 0)
            .Select(line => line.Split(' ').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray())
            .ToArray();
    }

    [Theory]
    [InlineData(10)]
    [InlineData(3)]
    public void Matches_the_models_own_feature_extractor(int seconds)
    {
        var reference = Reference(seconds);
        var features = ClapFeatures.Compute(Signal(seconds));

        Assert.Equal(ClapFeatures.ValuesPerClip, features.Length);
        Assert.Equal((ClapFeatures.Frames + Every - 1) / Every, reference.Length);

        double worst = 0;
        var worstAt = (frame: 0, band: 0);
        var drifts = new List<double>(reference.Length * ClapFeatures.MelBands);
        for (int r = 0; r < reference.Length; r++)
        {
            int frame = r * Every;
            Assert.Equal(ClapFeatures.MelBands, reference[r].Length);
            for (int b = 0; b < ClapFeatures.MelBands; b++)
            {
                double drift = Math.Abs(reference[r][b] - features[frame * ClapFeatures.MelBands + b]);
                drifts.Add(drift);
                if (drift > worst)
                {
                    worst = drift;
                    worstAt = (frame, b);
                }
            }
        }

        drifts.Sort();
        double median = drifts[drifts.Count / 2];

        // The reference is written with four decimals, so a drift up to 5e-5 dB is only the rounding of the
        // file. The worst case is bounded more loosely, because the two FFTs differ in the last bits and a
        // band near the -100 dB floor turns a tiny difference in power into a larger one in decibels.
        Assert.True(worst < 1e-2, $"largest drift {worst:E3} dB at frame {worstAt.frame}, band {worstAt.band}");
        Assert.True(median < 1e-4, $"typical drift {median:E3} dB is too large to be rounding");
    }

    [Fact]
    public void A_clip_is_ten_seconds_of_frames()
    {
        Assert.Equal(1001, ClapFeatures.Frames);
        Assert.Equal(1001 * 64, ClapFeatures.ValuesPerClip);
    }

    [Fact]
    public void A_longer_clip_is_cut_to_its_first_ten_seconds()
    {
        var ten = Signal(10);
        var longer = new float[ten.Length + ClapFeatures.SampleRate];
        ten.CopyTo(longer, 0);
        for (int i = ten.Length; i < longer.Length; i++)
            longer[i] = 0.7f;

        Assert.Equal(ClapFeatures.Compute(ten), ClapFeatures.Compute(longer));
    }

    [Fact]
    public void Silence_reads_as_the_floor()
    {
        var features = ClapFeatures.Compute(new float[ClapFeatures.ClipSamples]);
        Assert.All(features, v => Assert.Equal(-100f, v));
    }

    [Fact]
    public void An_empty_clip_is_refused()
    {
        Assert.Throws<ArgumentException>(() => ClapFeatures.Compute(ReadOnlySpan<float>.Empty));
    }
}
