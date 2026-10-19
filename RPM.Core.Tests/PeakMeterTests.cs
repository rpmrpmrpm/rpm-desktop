using RPM.Core.Recording;

namespace RPM.Core.Tests;

public class PeakMeterTests
{
    private const int Rate = 44100;

    /// <summary>Interleaved stereo sine at a given per-channel amplitude.</summary>
    private static float[] Stereo(double amplitude, double seconds = 0.05, double hz = 440)
    {
        int frames = (int)(seconds * Rate);
        var buf = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            float s = (float)(amplitude * Math.Sin(2 * Math.PI * hz * f / Rate));
            buf[f * 2] = s;
            buf[f * 2 + 1] = s;
        }
        return buf;
    }

    [Fact]
    public void Silence_reads_floor()
    {
        var frame = PeakMeter.Measure(new float[2048], channels: 2);
        Assert.Equal(PeakMeter.MinDb, frame.Channels[0].PeakDb);
        Assert.Equal(PeakMeter.MinDb, frame.Channels[0].RmsDb);
        Assert.False(frame.Channels[0].Clipped);
    }

    [Fact]
    public void Full_scale_sine_reads_near_zero_dbfs_and_clips()
    {
        var frame = PeakMeter.Measure(Stereo(1.0), channels: 2);
        // Peak of a full-scale sine reaches 0 dBFS; RMS is ~-3 dBFS (1/sqrt2).
        Assert.InRange(frame.Channels[0].PeakDb, -0.6f, 0.1f);
        Assert.InRange(frame.Channels[0].RmsDb, -4f, -2f);
        Assert.True(frame.Channels[0].Clipped);
    }

    [Fact]
    public void Half_amplitude_reads_minus_six_and_does_not_clip()
    {
        var frame = PeakMeter.Measure(Stereo(0.5), channels: 2);
        Assert.InRange(frame.Channels[0].PeakDb, -6.5f, -5.5f);
        Assert.InRange(frame.Channels[0].RmsDb, -10f, -8f);
        Assert.False(frame.Channels[0].Clipped);
    }

    [Fact]
    public void Channels_are_measured_independently()
    {
        // Left loud (1.0), right quiet (0.1).
        int frames = 2205;
        var buf = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            buf[f * 2] = (float)Math.Sin(2 * Math.PI * 440 * f / Rate);
            buf[f * 2 + 1] = (float)(0.1 * Math.Sin(2 * Math.PI * 440 * f / Rate));
        }
        var frame = PeakMeter.Measure(buf, channels: 2);
        Assert.InRange(frame.Channels[0].PeakDb, -0.6f, 0.1f);
        Assert.InRange(frame.Channels[1].PeakDb, -21f, -19f);
        Assert.True(frame.Channels[0].Clipped);
        Assert.False(frame.Channels[1].Clipped);
    }

    [Fact]
    public void PeakHold_jumps_up_instantly_and_decays()
    {
        var hold = new PeakHold(decayDbPerSecond: 12);
        Assert.Equal(-6f, hold.Update(-6f, 0), 3);        // first reading
        Assert.Equal(-6f, hold.Update(-40f, 0), 3);       // quieter peak, no time passed → unchanged
        Assert.Equal(-7.2f, hold.Update(-40f, 0.1), 3);   // 0.1 s × 12 dB/s = 1.2 dB decay
        Assert.Equal(-3f, hold.Update(-3f, 1.0), 3);      // louder peak → instant jump up
    }

    [Fact]
    public void PeakHold_never_falls_below_floor()
    {
        var hold = new PeakHold(decayDbPerSecond: 12);
        hold.Update(-6f, 0);
        float v = hold.Update(PeakMeter.MinDb, 100);       // huge elapsed
        Assert.Equal(PeakMeter.MinDb, v);
    }
}
