using RPM.Core.Analysis;

namespace RPM.Core.Tests;

/// <summary>
/// The loudness meter, against the cases the standard itself is checked with.
///
/// <para>Worth testing to this depth because nothing downstream can tell when it is wrong. A meter
/// reading two dB low produces a perfectly plausible gain, applies it without complaint, and leaves
/// a library levelled to the wrong place. There is no error, no exception and no symptom except
/// that the tracks do not match each other. EBU Tech 3341 exists for exactly this, and its first
/// cases are signals that can be generated rather than downloaded.</para>
/// </summary>
public class LoudnessMeterTests
{
    private const int Rate = 48000;

    /// <summary>How far two compliant meters are allowed to disagree, in LU. EBU Tech 3341's own
    /// figure, and the tolerance every reading here is checked to.</summary>
    private const double Tolerance = 0.1;

    /// <summary>
    /// EBU Tech 3341 case 1: a 1 kHz sine on both channels at −23 dBFS reads −23.0 LUFS, ±0.1.
    ///
    /// <para>The tolerance is the standard's own. It is not slack for the implementation: it is how
    /// much two compliant meters are allowed to disagree.</para>
    /// </summary>
    [Fact]
    public void A_stereo_sine_at_minus_23_dBFS_reads_minus_23_LUFS()
    {
        var meter = new LoudnessMeter(Rate, 2);
        Feed(meter, Sine(seconds: 20, amplitude: Math.Pow(10, -23.0 / 20)));

        Within(-23.0, meter.IntegratedLufs);
    }

    /// <summary>Tech 3341 case 2, the same signal ten dB down. Two points rather than one, because a
    /// meter can be right at one level and wrong about the slope between levels.</summary>
    [Fact]
    public void A_stereo_sine_at_minus_33_dBFS_reads_minus_33_LUFS()
    {
        var meter = new LoudnessMeter(Rate, 2);
        Feed(meter, Sine(seconds: 20, amplitude: Math.Pow(10, -33.0 / 20)));

        Within(-33.0, meter.IntegratedLufs);
    }

    /// <summary>Ten dB in is ten dB out. The measurement has to be linear in level or every gain
    /// derived from it is wrong by an amount that depends on the record.</summary>
    [Fact]
    public void Turning_the_signal_down_turns_the_reading_down_by_the_same_amount()
    {
        var loud = new LoudnessMeter(Rate, 2);
        Feed(loud, Sine(seconds: 10, amplitude: 0.5));

        var quiet = new LoudnessMeter(Rate, 2);
        Feed(quiet, Sine(seconds: 10, amplitude: 0.05));

        Within(20.0, loud.IntegratedLufs - quiet.IntegratedLufs);
    }

    /// <summary>
    /// The relative gate is the whole reason this is not an RMS meter.
    ///
    /// <para>A record that is music for ten seconds and silence for fifty is as loud as the music: a
    /// listener turns it up to hear the notes, not the average of the notes and the gaps. An ungated
    /// average would call this eight dB quieter than it is and turn it up into the ceiling.</para>
    /// </summary>
    [Fact]
    public void Silence_between_the_notes_does_not_count_against_the_loudness()
    {
        var solid = new LoudnessMeter(Rate, 2);
        Feed(solid, Sine(seconds: 10, amplitude: 0.25));

        var sparse = new LoudnessMeter(Rate, 2);
        Feed(sparse, Sine(seconds: 10, amplitude: 0.25));
        Feed(sparse, new float[Rate * 2 * 50]);            // fifty seconds of nothing after it

        Within(solid.IntegratedLufs, sparse.IntegratedLufs);
    }

    /// <summary>Silence has no loudness, which is not the same as a loudness of zero, and a caller
    /// that treated it as one would compute an enormous gain for an empty file.</summary>
    [Fact]
    public void Silence_has_no_loudness_at_all()
    {
        var meter = new LoudnessMeter(Rate, 2);
        Feed(meter, new float[Rate * 2 * 5]);

        Assert.True(double.IsNegativeInfinity(meter.IntegratedLufs));
    }

    /// <summary>A file too short to fill one 400 ms window cannot be measured. It has to say so
    /// rather than answer from a partial block.</summary>
    [Fact]
    public void A_clip_shorter_than_one_block_has_no_reading()
    {
        var meter = new LoudnessMeter(Rate, 2);
        Feed(meter, Sine(seconds: 0.1, amplitude: 0.5));

        Assert.False(meter.HasSignal);
        Assert.True(double.IsNegativeInfinity(meter.IntegratedLufs));
    }

    /// <summary>
    /// The same music read at three sample rates is the same music.
    ///
    /// <para>This is the test that earns the filter coefficients being derived rather than copied:
    /// the standard tabulates them for 48 kHz only, and a library holds 44.1 kHz CDs beside 48 kHz
    /// rips. If the derivation were wrong, every rate but one would be levelled to its own private
    /// reference and nobody would ever see an error.</para>
    /// </summary>
    [Theory]
    [InlineData(44100)]
    [InlineData(22050)]
    [InlineData(96000)]
    public void The_reading_does_not_depend_on_the_sample_rate(int rate)
    {
        var reference = new LoudnessMeter(Rate, 2);
        Feed(reference, Sine(seconds: 10, amplitude: 0.25, rate: Rate));

        var other = new LoudnessMeter(rate, 2);
        Feed(other, Sine(seconds: 10, amplitude: 0.25, rate: rate));

        Within(reference.IntegratedLufs, other.IntegratedLufs);
    }

    /// <summary>Blocks are 400 ms of audio however the decoder happens to cut it up. A meter whose
    /// answer moved with the caller's buffer size would give one reading on a file and another on the
    /// same file streamed.</summary>
    [Fact]
    public void The_reading_does_not_depend_on_how_the_audio_is_handed_over()
    {
        var whole = new LoudnessMeter(Rate, 2);
        var samples = Sine(seconds: 10, amplitude: 0.25);
        whole.Add(samples, samples.Length / 2);

        var piecemeal = new LoudnessMeter(Rate, 2);
        int at = 0;
        int size = 1;
        while (at < samples.Length / 2)
        {
            int frames = Math.Min(size, samples.Length / 2 - at);
            piecemeal.Add(samples.AsSpan(at * 2), frames);
            at += frames;
            size = size % 977 + 1;                          // deliberately ragged block sizes
        }

        Assert.Equal(whole.IntegratedLufs, piecemeal.IntegratedLufs, 6);
    }

    /// <summary>
    /// A sine that never lands on its own peak still has that peak.
    ///
    /// <para>The case is real and it is the reason true peak exists: a tone at a quarter of the
    /// sample rate, phase-shifted so the samples straddle the crest, is a waveform reaching 1.0 whose
    /// every sample reads 0.707. A player that clamped its gain against the sample peak would leave
    /// three dB of clipping it believed it had prevented.</para>
    /// </summary>
    [Fact]
    public void The_peak_between_two_samples_is_found()
    {
        var meter = new LoudnessMeter(Rate, 1);
        var samples = new float[Rate];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)Math.Sin(2 * Math.PI * (Rate / 4.0) * i / Rate + Math.PI / 4);

        meter.Add(samples, samples.Length);

        Assert.Equal(0.7071, meter.SamplePeak, 3);
        Assert.True(meter.TruePeak > 0.99, $"true peak {meter.TruePeak:F4} missed the crest between samples");
    }

    /// <summary>True peak is never below sample peak: it is the same curve looked at more
    /// closely.</summary>
    [Fact]
    public void True_peak_is_never_under_the_sample_peak()
    {
        var meter = new LoudnessMeter(Rate, 2);
        Feed(meter, Sine(seconds: 2, amplitude: 0.9));

        Assert.True(meter.TruePeak >= meter.SamplePeak - 1e-6);
    }

    /// <summary>Two readings that agree as far as the standard requires them to.</summary>
    private static void Within(double expected, double actual) =>
        Assert.True(Math.Abs(expected - actual) <= Tolerance,
            $"expected {expected:F3} LUFS, read {actual:F3} — {Math.Abs(expected - actual):F3} LU apart, over the {Tolerance} LU allowed");

    private static void Feed(LoudnessMeter meter, float[] interleaved) =>
        meter.Add(interleaved, interleaved.Length / 2);

    /// <summary>A 1 kHz sine on both channels of a stereo pair, as the compliance signals are
    /// defined: <paramref name="amplitude"/> is the peak, so a "−23 dBFS" tone is one whose crest
    /// sits 23 dB below full scale.</summary>
    private static float[] Sine(double seconds, double amplitude, int rate = Rate)
    {
        int frames = (int)(rate * seconds);
        var samples = new float[frames * 2];

        for (int f = 0; f < frames; f++)
        {
            var value = (float)(amplitude * Math.Sin(2 * Math.PI * 1000 * f / rate));
            samples[f * 2] = value;
            samples[f * 2 + 1] = value;
        }

        return samples;
    }
}
