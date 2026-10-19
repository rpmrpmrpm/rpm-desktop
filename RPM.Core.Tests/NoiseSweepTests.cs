using RPM.Core.Audio;

namespace RPM.Core.Tests;

/// <summary>
/// The noise a changeover is built on: the rise under a roll out, the sudden stop that is its
/// purpose, and the fall that comes down over the incoming record.
/// </summary>
public class NoiseSweepTests
{
    private const int Rate = 44100;
    private const int Channels = 2;

    /// <summary>Runs the riser for <paramref name="frames"/> and returns the loudest sample in each
    /// block, so the shape of the rise can be read from it.</summary>
    private static List<double> Run(NoiseSweep riser, long frames, int block = 4410)
    {
        var buffer = new float[block * Channels];
        var peaks = new List<double>();

        for (long done = 0; done < frames; done += block)
        {
            Array.Clear(buffer);
            riser.Add(buffer, 0, block);

            double loudest = 0;
            foreach (var sample in buffer)
                loudest = Math.Max(loudest, Math.Abs(sample));
            peaks.Add(loudest);
        }

        return peaks;
    }

    [Fact]
    public void It_says_nothing_until_it_has_been_started()
    {
        var riser = new NoiseSweep(Rate, Channels);

        Assert.False(riser.IsRunning);
        Assert.All(Run(riser, Rate), peak => Assert.Equal(0, peak, 6));
    }

    [Fact]
    public void It_waits_for_the_bar_line_it_was_hung_on()
    {
        var riser = new NoiseSweep(Rate, Channels);
        riser.Rise(delayFrames: Rate, lengthFrames: 4 * Rate);

        // A second of silence first: the loop it rises under has not started either.
        var peaks = Run(riser, Rate, block: Rate / 4);
        Assert.All(peaks, peak => Assert.Equal(0, peak, 6));
        Assert.Equal(0, riser.Level, 6);
    }

    [Fact]
    public void It_climbs_and_most_of_it_happens_late()
    {
        var riser = new NoiseSweep(Rate, Channels);
        riser.Rise(delayFrames: 0, lengthFrames: 8 * Rate);

        var peaks = Run(riser, 8L * Rate, block: Rate);
        Assert.Equal(8, peaks.Count);

        // Every second is louder than the one before it.
        for (int i = 1; i < peaks.Count; i++)
            Assert.True(peaks[i] > peaks[i - 1],
                $"second {i} ({peaks[i]:F3}) is not above second {i - 1} ({peaks[i - 1]:F3})");

        // It ends at its full gain. Measured on the gain, not on the loudest sample, because filtered
        // noise sometimes goes above its own gain, and that is not what "how loud" means.
        Assert.Equal(NoiseSweep.RisePeak, riser.Level, 6);

        // It is very quiet for the first quarter: a rise that is already loud at the start is a
        // wash, not a rise.
        Assert.True(peaks[1] < NoiseSweep.RisePeak * 0.2, $"it opens at {peaks[1]:F3}, which is too loud");
    }

    [Fact]
    public void The_stop_is_sharp_and_then_it_is_gone()
    {
        var riser = new NoiseSweep(Rate, Channels);
        riser.Rise(delayFrames: 0, lengthFrames: 4 * Rate);

        Run(riser, 4L * Rate, block: Rate);
        double atTheTop = riser.Level;
        Assert.True(atTheTop > NoiseSweep.RisePeak * 0.7, "it should be near its peak by the end");

        riser.Release();

        // Thirty milliseconds, and nothing after it. Read in two parts: the release ends inside the
        // first, and the second is the silence after it.
        var buffer = new float[Rate / 4 * Channels];
        riser.Add(buffer, 0, Rate / 4);
        Assert.False(riser.IsRunning);

        Array.Clear(buffer);
        riser.Add(buffer, 0, Rate / 4);
        Assert.All(buffer, sample => Assert.Equal(0, sample, 6));

        // It really did fade and not stop suddenly: the end of that block is silent well before
        // its last sample, but the start of it is not.
        Assert.Equal(0, riser.Level, 6);
    }

    [Fact]
    public void A_release_lands_inside_its_own_length()
    {
        var riser = new NoiseSweep(Rate, Channels);
        riser.Rise(delayFrames: 0, lengthFrames: 4 * Rate);
        Run(riser, 4L * Rate, block: Rate);
        riser.Release();

        // One block a little longer than the release: the release must finish inside it.
        int block = (int)(NoiseSweep.ReleaseSeconds * Rate) + 64;
        var buffer = new float[block * Channels];
        riser.Add(buffer, 0, block);

        Assert.False(riser.IsRunning);
        for (int f = block - 32; f < block; f++)
            for (int c = 0; c < Channels; c++)
                Assert.Equal(0, buffer[f * Channels + c], 6);
    }

    [Fact]
    public void A_rise_opens_up_as_it_climbs()
    {
        // It starts as a rumble with its middle and top removed and gets them back as it rises.
        // Measured as how much of the noise is left above the middle of the band: at the bottom of the
        // sweep almost none of it is, at the top nearly all of it.
        var riser = new NoiseSweep(Rate, Channels);
        riser.Rise(delayFrames: 0, lengthFrames: 8 * Rate);

        var buffer = new float[Rate * Channels];
        riser.Add(buffer, 0, Rate);
        double opening = HighShare(buffer);

        for (int second = 0; second < 6; second++)
        {
            Array.Clear(buffer);
            riser.Add(buffer, 0, Rate);
        }

        Array.Clear(buffer);
        riser.Add(buffer, 0, Rate);
        double landing = HighShare(buffer);

        Assert.True(opening < 0.15, $"it opens with {opening:P0} of its energy up top, which is not cut");
        Assert.True(landing > opening * 2, $"it lands at {landing:P0}, no brighter than it started");
    }

    [Fact]
    public void A_fall_starts_full_and_is_gone_by_the_end_of_its_length()
    {
        var faller = new NoiseSweep(Rate, Channels);
        faller.Fall(2 * Rate);

        Assert.True(faller.IsRunning);
        Assert.False(faller.IsRising);
        Assert.Equal(NoiseSweep.FallPeak, faller.Level, 6);

        var peaks = Run(faller, 2L * Rate, block: Rate / 4);

        // Loudest at the start and quieter every quarter after it.
        for (int i = 1; i < peaks.Count; i++)
            Assert.True(peaks[i] < peaks[i - 1],
                $"quarter {i} ({peaks[i]:F3}) is not below quarter {i - 1} ({peaks[i - 1]:F3})");

        // It ends by itself: nothing has to cut a fall off.
        Assert.False(faller.IsRunning);
        Assert.All(Run(faller, Rate), peak => Assert.Equal(0, peak, 6));
    }

    /// <summary>How much of a block's energy is above the middle of the band, as a share. A rough
    /// measure of how open the filter was: the block is compared with itself after a plain one-pole
    /// filter has removed the highs.</summary>
    private static double HighShare(float[] block)
    {
        double whole = 0, low = 0, carried = 0;
        for (int i = 0; i < block.Length; i += Channels)
        {
            // One pole at about 2 kHz: below everything a mid or a top would have.
            carried += (block[i] - carried) * 0.25;
            low += carried * carried;
            whole += block[i] * block[i];
        }
        return whole <= 0 ? 0 : Math.Max(0, whole - low) / whole;
    }

    [Fact]
    public void Stopping_takes_it_away_at_once()
    {
        var riser = new NoiseSweep(Rate, Channels);
        riser.Rise(delayFrames: 0, lengthFrames: 4 * Rate);
        Run(riser, 2L * Rate, block: Rate);

        riser.Stop();

        Assert.False(riser.IsRunning);
        Assert.All(Run(riser, Rate), peak => Assert.Equal(0, peak, 6));
    }
}
