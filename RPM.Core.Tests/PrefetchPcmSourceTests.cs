using RPM.Core.Audio;

namespace RPM.Core.Tests;

// These tests prove a timing property: the consumer does NOT wait for a slow producer. So they
// deliberately block and measure. Awaiting would assert nothing.
#pragma warning disable xUnit1031  // Test methods should not use blocking task operations

public class PrefetchPcmSourceTests
{
    private const int Channels = 2;

    /// <summary>A source that gives a fixed number of frames, counting up, so the consumer can check
    /// that it got every sample in order. It can optionally stall, to act as a cold file.</summary>
    private sealed class RampSource : IPcmSource
    {
        private readonly int _totalFrames;
        private readonly TimeSpan _delayPerBlock;
        private int _position;

        public RampSource(int totalFrames, TimeSpan delayPerBlock = default)
        {
            _totalFrames = totalFrames;
            _delayPerBlock = delayPerBlock;
        }

        public string Label => "ramp";

        public int Reads { get; private set; }

        public bool Disposed { get; private set; }

        public int Read(float[] buffer, int frames)
        {
            if (_delayPerBlock > TimeSpan.Zero) Thread.Sleep(_delayPerBlock);
            Reads++;

            int take = Math.Min(frames, _totalFrames - _position);
            if (take <= 0) return 0;

            for (int i = 0; i < take; i++)
            {
                buffer[i * Channels] = _position + i;
                buffer[i * Channels + 1] = -(_position + i);
            }
            _position += take;
            return take;
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>Waits briefly for something a background thread will do. If it does not happen, it
    /// returns anyway, so that the assertion after it reports the failure.</summary>
    private static void Until(Func<bool> done)
    {
        for (int i = 0; i < 500 && !done(); i++)
            Thread.Sleep(10);
    }

    /// <summary>Reads everything the source will give, in blocks, and returns it as one array.</summary>
    private static float[] DrainAll(IPcmSource source, int blockFrames)
    {
        var block = new float[blockFrames * Channels];
        var all = new List<float>();
        while (true)
        {
            int frames = source.Read(block, blockFrames);
            if (frames <= 0) break;
            all.AddRange(block[..(frames * Channels)]);
        }
        return [.. all];
    }

    [Fact]
    public void Delivers_Every_Sample_In_Order()
    {
        using var prefetch = new PrefetchPcmSource(new RampSource(5000), Channels, capacityFrames: 1000, blockFrames: 100);

        var got = DrainAll(prefetch, blockFrames: 100);

        Assert.Equal(5000 * Channels, got.Length);
        for (int i = 0; i < 5000; i++)
        {
            Assert.Equal(i, got[i * Channels]);
            Assert.Equal(-i, got[i * Channels + 1]);
        }
    }

    [Fact]
    public void Fills_A_Whole_Block_Even_When_The_Inner_Source_Hands_Out_Less()
    {
        // Inner blocks of 100 frames, consumer asking for 512: the prefetcher has to stitch them.
        using var prefetch = new PrefetchPcmSource(new RampSource(2048), Channels, capacityFrames: 4096, blockFrames: 100);

        var block = new float[512 * Channels];
        Assert.Equal(512, prefetch.Read(block, 512));
        Assert.Equal(0, block[0]);
        Assert.Equal(511, block[511 * Channels]);
    }

    [Fact]
    public void Returns_A_Short_Block_Then_Zero_At_The_End_Of_The_Stream()
    {
        using var prefetch = new PrefetchPcmSource(new RampSource(150), Channels, capacityFrames: 1000, blockFrames: 100);

        var block = new float[100 * Channels];
        Assert.Equal(100, prefetch.Read(block, 100));
        Assert.Equal(50, prefetch.Read(block, 100));      // the tail
        Assert.Equal(0, prefetch.Read(block, 100));       // and then nothing
    }

    [Fact]
    public void Works_Ahead_So_A_Stalling_Decoder_Costs_The_Consumer_Nothing()
    {
        // 10 ms per inner block is a decoder that would stall a pump reading it directly. By the time
        // the first block is out, the thread behind it has already been filling the ring.
        var inner = new RampSource(1000, delayPerBlock: TimeSpan.FromMilliseconds(10));
        using var prefetch = new PrefetchPcmSource(inner, Channels, capacityFrames: 1000, blockFrames: 100);

        var block = new float[100 * Channels];
        prefetch.Read(block, 100);                        // pays for the first block only

        // Give the decode thread time to run ahead, then take the rest: it must already be waiting.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (prefetch.BufferedFrames < 500 && DateTime.UtcNow < deadline) Thread.Sleep(5);
        Assert.True(prefetch.BufferedFrames >= 500, $"decoder did not work ahead ({prefetch.BufferedFrames} frames)");

        var start = DateTime.UtcNow;
        for (int i = 0; i < 5; i++) Assert.Equal(100, prefetch.Read(block, 100));
        var elapsed = DateTime.UtcNow - start;

        // Read straight from the inner source, those five blocks would have cost about 50 ms.
        Assert.True(elapsed < TimeSpan.FromMilliseconds(30), $"reads waited on the decoder ({elapsed.TotalMilliseconds:F0} ms)");
    }

    [Fact]
    public void Does_Not_Run_Away_With_The_Track_Beyond_Its_Capacity()
    {
        var inner = new RampSource(100_000);
        using var prefetch = new PrefetchPcmSource(inner, Channels, capacityFrames: 1000, blockFrames: 100);

        Thread.Sleep(100);   // ample time to decode 100_000 frames if it were unbounded

        Assert.True(prefetch.BufferedFrames <= 1000, $"ring overfilled ({prefetch.BufferedFrames} frames)");
        Assert.True(inner.Reads < 200, $"decoder ran away ({inner.Reads} reads)");
    }

    [Fact]
    public void Dispose_Takes_The_Inner_Source_With_It_And_Does_Not_Hang_On_A_Full_Ring()
    {
        var inner = new RampSource(100_000);
        var prefetch = new PrefetchPcmSource(inner, Channels, capacityFrames: 1000, blockFrames: 100);

        Thread.Sleep(50);    // let the ring fill, so the decode thread is parked in Write

        var start = DateTime.UtcNow;
        prefetch.Dispose();
        var elapsed = DateTime.UtcNow - start;

        Assert.True(inner.Disposed);
        Assert.True(elapsed < TimeSpan.FromMilliseconds(500), $"dispose waited out a join ({elapsed.TotalMilliseconds:F0} ms)");
    }

    [Fact]
    public void Reads_After_Dispose_Report_End_Of_Stream()
    {
        var prefetch = new PrefetchPcmSource(new RampSource(10_000), Channels, capacityFrames: 1000, blockFrames: 100);
        prefetch.Dispose();

        Assert.Equal(0, prefetch.Read(new float[100 * Channels], 100));
    }

    [Fact]
    public void Carries_The_Inner_Label_So_The_Player_Still_Knows_What_Is_Playing()
    {
        using var prefetch = new PrefetchPcmSource(new RampSource(100), Channels, capacityFrames: 1000, blockFrames: 100);
        Assert.Equal("ramp", prefetch.Label);
    }

    [Fact]
    public void Works_No_Further_Ahead_Than_It_Is_Told_To()
    {
        // A ring with room for a hundred blocks, told to keep four. This proves what a workbench
        // needs: how far ahead the decoder runs decides how late everything done to the source is
        // heard, and the depth can be shortened without rebuilding anything.
        using var prefetch = new PrefetchPcmSource(
            new RampSource(1_000_000), Channels, capacityFrames: 10_000, blockFrames: 100)
        {
            KeepAheadFrames = 400,
        };

        // Drained back under the limit first. The decode thread starts with the object and the depth
        // is set a little later, so whatever it read in between is not what is measured. What is
        // measured is where it settles once it has been told the depth.
        var block = new float[100 * Channels];
        for (int i = 0; i < 200 && prefetch.BufferedFrames > 400; i++)
            prefetch.Read(block, 100);

        // Waited for, not slept through: this machine may be running all the other tests at the same
        // time, and a decode thread that has not had a CPU core yet has not decided anything.
        Until(() => prefetch.BufferedFrames >= 400);
        Thread.Sleep(50);       // and long enough after that for an unbounded one to overshoot

        Assert.InRange(prefetch.BufferedFrames, 1, 600);   // the target, plus at most the block in hand

        // And letting it out again fills the ring the rest of the way.
        prefetch.KeepAheadFrames = 10_000;
        Until(() => prefetch.BufferedFrames > 5_000);

        Assert.True(prefetch.BufferedFrames > 5_000, $"only {prefetch.BufferedFrames} frames ahead");
    }

    [Fact]
    public void Is_Never_Told_To_Work_Less_Than_One_Block_Ahead()
    {
        // A depth below one block is no margin at all: the decoder would hand over the same block
        // the pump is asking for, which is no ring.
        using var prefetch = new PrefetchPcmSource(
            new RampSource(10_000), Channels, capacityFrames: 1000, blockFrames: 100)
        {
            KeepAheadFrames = 1,
        };

        Assert.Equal(100, prefetch.KeepAheadFrames);

        prefetch.KeepAheadFrames = 999_999;
        Assert.Equal(1000, prefetch.KeepAheadFrames);
    }

    /// <summary>A source that says which record it is on, as a DJ set does.</summary>
    private sealed class MixingSource : IPcmSource, INowPlayingSource
    {
        public string Label => "dj-set";
        public NowPlayingTrack? NowPlayingTrack =>
            new("/library/b-side.flac", IsRecord: true, "B Side", "Someone", null, 42, 300);
        public int Read(float[] buffer, int frames) { Array.Clear(buffer, 0, frames * Channels); return frames; }
        public void Dispose() { }
    }

    [Fact]
    public void Carries_What_A_Mix_Is_Playing_So_The_Header_Survives_The_Buffer()
    {
        // The engine asks whatever it is pumping which record is on and where the needle is. If a
        // buffer is put in front of a DJ set without passing this through, the readout, the artwork
        // and the progress bar go blank, while the audio plays normally. Nothing then shows why.
        using var prefetch = new PrefetchPcmSource(new MixingSource(), Channels, capacityFrames: 1000, blockFrames: 100);

        var now = ((INowPlayingSource)prefetch).NowPlayingTrack;

        Assert.NotNull(now);
        Assert.Equal("/library/b-side.flac", now!.Value.Path);
        Assert.Equal(42, now.Value.PositionSeconds);
    }
}
