using RPM.Core.Analysis;

namespace RPM.Core.Tests;

/// <summary>
/// The three arousal numbers, measured from a stream.
///
/// <para>Tested against made-up signals, not music, because the properties of a made-up signal are
/// known: a low sine has no high frequencies, a click train has four events a second, and a constant
/// tone has no spread. The recipe that uses these numbers is tested on music elsewhere; these tests
/// check the measurement itself.</para>
/// </summary>
public class MoodMeterTests
{
    private const int Rate = 48000;

    /// <summary>A tone, in mono, at a level and a frequency.</summary>
    private static float[] Tone(double hz, double seconds, double amplitude = 0.5)
    {
        var samples = new float[(int)(Rate * seconds)];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate));
        return samples;
    }

    private static MoodReading Measure(float[] mono, int blockFrames = 4096)
    {
        var meter = new MoodMeter(Rate, 1);
        for (int at = 0; at < mono.Length; at += blockFrames)
        {
            int frames = Math.Min(blockFrames, mono.Length - at);
            meter.Add(mono.AsSpan(at, frames), frames);
        }
        return meter.Reading;
    }

    // ── brightness ──

    /// <summary>A bass tone has nothing above the top cutoff in it, and reads as dark.</summary>
    [Fact]
    public void A_low_tone_is_dark()
    {
        var reading = Measure(Tone(100, 10));

        Assert.True(reading.Known);
        Assert.True(reading.Brightness < 0.05, $"a 100 Hz tone read as {reading.Brightness:F3} bright");
    }

    /// <summary>A tone above it reads as bright. Together these two cover the whole measurement:
    /// where the energy is, as a share. That makes it a fact about the arrangement, not about the
    /// level the record was mastered at.</summary>
    [Fact]
    public void A_high_tone_is_bright()
    {
        var reading = Measure(Tone(12000, 10));

        Assert.True(reading.Known);
        Assert.True(reading.Brightness > 0.8, $"a 12 kHz tone read as {reading.Brightness:F3} bright");
    }

    /// <summary>Brightness is a share, so making the same record louder does not make it brighter.
    /// Loudness does not have this property, which is why this number is worth measuring as
    /// well.</summary>
    [Fact]
    public void Brightness_does_not_move_with_the_level()
    {
        var quiet = Measure(Tone(8000, 10, amplitude: 0.05));
        var loud = Measure(Tone(8000, 10, amplitude: 0.9));

        Assert.Equal(quiet.Brightness, loud.Brightness, 2);
    }

    // ── onsets ──

    /// <summary>A tone that never changes has nothing happening in it.</summary>
    [Fact]
    public void A_steady_tone_has_no_events_in_it()
        => Assert.True(Measure(Tone(440, 10)).Onsets < 0.2);

    /// <summary>Four clicks a second read as about four events a second, whatever the bar length.
    /// Tempo cannot give this: nothing in it depends on choosing between 70 and 140.</summary>
    [Fact]
    public void A_click_train_reads_as_its_own_rate()
    {
        var samples = Tone(440, 10, amplitude: 0.02);   // a quiet floor, so the clicks are rises
        int every = Rate / 4;
        for (int at = 0; at < samples.Length; at += every)
            for (int i = at; i < Math.Min(samples.Length, at + Rate / 100); i++)
                samples[i] *= 20;

        var reading = Measure(samples);

        Assert.InRange(reading.Onsets, 3, 5);
    }

    // ── dynamics ──

    /// <summary>A tone at one level has no spread; one that alternates between loud and quiet has the
    /// distance between them. Percentiles are used, not the extremes, so a single clipped sample does
    /// not decide the number for a whole record.</summary>
    [Fact]
    public void The_spread_is_the_distance_between_loud_and_quiet()
    {
        var even = Measure(Tone(440, 12));

        var uneven = Tone(440, 12);
        for (int i = 0; i < uneven.Length; i++)
            if ((i / (Rate / 2)) % 2 == 0)
                uneven[i] *= 0.01f;   // 40 dB down, every other half second

        var swung = Measure(uneven);

        Assert.True(even.Dynamics < 3, $"an even tone spread {even.Dynamics:F1} dB");
        Assert.True(swung.Dynamics > 30, $"a swinging tone spread only {swung.Dynamics:F1} dB");
    }

    // ── the stream ──

    /// <summary>
    /// The same answer however the samples are split into blocks.
    ///
    /// <para>The design depends on this: the meter runs on a decode that passes it blocks of any size,
    /// and the filters keep their state across those blocks. If that is wrong the numbers still look
    /// reasonable, but they are a different measurement on every machine.</para>
    /// </summary>
    [Fact]
    public void The_block_size_does_not_change_the_answer()
    {
        var samples = Tone(2000, 10);

        var whole = Measure(samples, blockFrames: samples.Length);
        var chopped = Measure(samples, blockFrames: 313);

        Assert.Equal(whole.Brightness, chopped.Brightness, 3);
        Assert.Equal(whole.Onsets, chopped.Onsets, 3);
        Assert.Equal(whole.Dynamics, chopped.Dynamics, 3);
    }

    // ── what cannot be measured ──

    /// <summary>Too short to describe. Not zero: zero is a real answer to all three, and a sine wave
    /// really is that dark, that still and that even.</summary>
    [Fact]
    public void A_track_too_short_to_describe_says_so()
        => Assert.False(Measure(Tone(440, 2)).Known);

    [Fact]
    public void Silence_says_so_too()
        => Assert.False(Measure(new float[Rate * 10]).Known);

    /// <summary>Empty input gives an empty result, not an exception, because a pass runs over a
    /// whole library.</summary>
    [Fact]
    public void Nothing_at_all_is_not_an_error()
        => Assert.False(new MoodMeter(Rate, 2).Reading.Known);

    /// <summary>Stereo is mixed to mono before anything is filtered: every number here is about the
    /// record, not about its stereo image.</summary>
    [Fact]
    public void Stereo_is_measured_as_the_record_rather_than_as_two_channels()
    {
        var mono = Tone(200, 10);
        var stereo = new float[mono.Length * 2];
        for (int i = 0; i < mono.Length; i++)
            stereo[i * 2] = stereo[i * 2 + 1] = mono[i];

        var one = new MoodMeter(Rate, 1);
        one.Add(mono, mono.Length);

        var two = new MoodMeter(Rate, 2);
        two.Add(stereo, mono.Length);

        Assert.Equal(one.Reading.Brightness, two.Reading.Brightness, 4);
        Assert.Equal(one.Reading.Dynamics, two.Reading.Dynamics, 4);
    }
}
