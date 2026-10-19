using RPM.Core.Audio;

namespace RPM.Core.Tests;

// The ring is required to block, and a bounded wait that times out is how a test proves a thread
// is parked. Awaiting instead would check nothing.
#pragma warning disable xUnit1031  // Test methods should not use blocking task operations

public class PcmRingTests
{
    private const int Channels = 2;

    private static float[] Ramp(int frames, int from = 0)
    {
        var buf = new float[frames * Channels];
        for (int i = 0; i < frames; i++)
        {
            buf[i * Channels] = from + i;
            buf[i * Channels + 1] = -(from + i);
        }
        return buf;
    }

    [Fact]
    public void RoundTrips_Samples_In_Order()
    {
        var ring = new PcmRing(capacityFrames: 64, Channels);
        Assert.True(ring.Write(Ramp(10), 10));

        var dst = new float[10 * Channels];
        Assert.Equal(dst.Length, ring.Read(dst));

        Assert.Equal(Ramp(10), dst);
        Assert.Equal(10, ring.WrittenFrames);
        Assert.Equal(10, ring.ReadFrames);
        Assert.Equal(0, ring.QueuedFrames);
    }

    [Fact]
    public void Wraps_Around_The_End_Of_The_Buffer()
    {
        var ring = new PcmRing(capacityFrames: 8, Channels);

        // Move the cursors near the end, then cross the wrap point with a write and a read.
        ring.Write(Ramp(6), 6);
        var drain = new float[6 * Channels];
        ring.Read(drain);

        Assert.True(ring.Write(Ramp(6, from: 100), 6));   // wraps: 2 frames at the end, 4 at the front

        var dst = new float[6 * Channels];
        Assert.Equal(dst.Length, ring.Read(dst));
        Assert.Equal(Ramp(6, from: 100), dst);
    }

    [Fact]
    public void Read_Returns_What_Is_There_When_Asked_For_More()
    {
        var ring = new PcmRing(capacityFrames: 64, Channels);
        ring.Write(Ramp(3), 3);

        var dst = new float[10 * Channels];
        Assert.Equal(3 * Channels, ring.Read(dst));
    }

    /// <summary>The main purpose of the ring: a full ring parks the producer instead of growing.
    /// This is what matches the decoder's speed to the Cast receiver.</summary>
    [Fact]
    public void Write_Blocks_Until_The_Consumer_Makes_Room()
    {
        var ring = new PcmRing(capacityFrames: 4, Channels);
        ring.Write(Ramp(4), 4);   // full

        var wrote = new TaskCompletionSource<bool>();
        var writer = new Thread(() => wrote.SetResult(ring.Write(Ramp(4, from: 50), 4)));
        writer.Start();

        Assert.False(wrote.Task.Wait(TimeSpan.FromMilliseconds(150)), "write should still be parked on a full ring");

        var dst = new float[4 * Channels];
        ring.Read(dst);                                    // make room

        Assert.True(wrote.Task.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(wrote.Task.Result);
        Assert.Equal(Ramp(4), dst);

        var second = new float[4 * Channels];
        ring.Read(second);
        Assert.Equal(Ramp(4, from: 50), second);
    }

    [Fact]
    public void Read_Blocks_Until_The_Producer_Delivers()
    {
        var ring = new PcmRing(capacityFrames: 16, Channels);

        var read = new TaskCompletionSource<int>();
        var dst = new float[2 * Channels];
        new Thread(() => read.SetResult(ring.Read(dst))).Start();

        Assert.False(read.Task.Wait(TimeSpan.FromMilliseconds(150)), "read should still be parked on an empty ring");

        ring.Write(Ramp(2, from: 7), 2);

        Assert.True(read.Task.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(2 * Channels, read.Task.Result);
        Assert.Equal(Ramp(2, from: 7), dst);
    }

    [Fact]
    public void CompleteWriting_Drains_Then_Reports_End_Of_Stream()
    {
        var ring = new PcmRing(capacityFrames: 16, Channels);
        ring.Write(Ramp(2), 2);
        ring.CompleteWriting();

        var dst = new float[8 * Channels];
        Assert.Equal(2 * Channels, ring.Read(dst));   // the tail still comes out
        Assert.Equal(0, ring.Read(dst));              // then end of stream, not a block
    }

    [Fact]
    public void CompleteWriting_Releases_A_Parked_Writer()
    {
        var ring = new PcmRing(capacityFrames: 4, Channels);
        ring.Write(Ramp(4), 4);

        var wrote = new TaskCompletionSource<bool>();
        new Thread(() => wrote.SetResult(ring.Write(Ramp(4), 4))).Start();
        Assert.False(wrote.Task.Wait(TimeSpan.FromMilliseconds(150)));

        ring.CompleteWriting();

        Assert.True(wrote.Task.Wait(TimeSpan.FromSeconds(2)));
        Assert.False(wrote.Task.Result);   // refused, not silently dropped
    }

    /// <summary>Stop must not deadlock the pump: closing the ring must release any thread that is
    /// parked in it, on both sides.</summary>
    [Fact]
    public void Close_Unblocks_Both_Ends()
    {
        var ring = new PcmRing(capacityFrames: 4, Channels);
        ring.Write(Ramp(4), 4);

        var wrote = new TaskCompletionSource<bool>();
        new Thread(() => wrote.SetResult(ring.Write(Ramp(4), 4))).Start();
        Assert.False(wrote.Task.Wait(TimeSpan.FromMilliseconds(150)));

        ring.Close();

        Assert.True(wrote.Task.Wait(TimeSpan.FromSeconds(2)));
        Assert.False(wrote.Task.Result);
        Assert.Equal(0, ring.Read(new float[Channels]));   // and the reader sees end of stream
        Assert.True(ring.IsClosed);
    }

    [Fact]
    public void Close_Unblocks_A_Parked_Reader()
    {
        var ring = new PcmRing(capacityFrames: 4, Channels);

        var read = new TaskCompletionSource<int>();
        new Thread(() => read.SetResult(ring.Read(new float[Channels]))).Start();
        Assert.False(read.Task.Wait(TimeSpan.FromMilliseconds(150)));

        ring.Close();

        Assert.True(read.Task.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, read.Task.Result);
    }

    [Fact]
    public void WaitDrained_Returns_When_The_Consumer_Has_Taken_Everything()
    {
        var ring = new PcmRing(capacityFrames: 16, Channels);
        ring.Write(Ramp(4), 4);

        var drained = new TaskCompletionSource<bool>();
        new Thread(() => drained.SetResult(ring.WaitDrained())).Start();
        Assert.False(drained.Task.Wait(TimeSpan.FromMilliseconds(150)));

        ring.Read(new float[4 * Channels]);

        Assert.True(drained.Task.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(drained.Task.Result);
    }

    [Fact]
    public void WaitDrained_Gives_Up_When_Cancelled()
    {
        var ring = new PcmRing(capacityFrames: 16, Channels);
        ring.Write(Ramp(4), 4);   // never read

        using var cts = new CancellationTokenSource();
        var drained = new TaskCompletionSource<bool>();
        new Thread(() => drained.SetResult(ring.WaitDrained(cts.Token))).Start();
        Assert.False(drained.Task.Wait(TimeSpan.FromMilliseconds(150)));

        cts.Cancel();

        Assert.True(drained.Task.Wait(TimeSpan.FromSeconds(2)));
        Assert.False(drained.Task.Result);
    }

    [Fact]
    public void Reset_Reopens_A_Closed_Ring_For_The_Next_Track()
    {
        var ring = new PcmRing(capacityFrames: 8, Channels);
        ring.Write(Ramp(4), 4);
        ring.Close();

        ring.Reset();

        Assert.False(ring.IsClosed);
        Assert.Equal(0, ring.WrittenFrames);
        Assert.Equal(0, ring.ReadFrames);
        Assert.Equal(0, ring.QueuedFrames);

        Assert.True(ring.Write(Ramp(2, from: 9), 2));
        var dst = new float[2 * Channels];
        Assert.Equal(dst.Length, ring.Read(dst));
        Assert.Equal(Ramp(2, from: 9), dst);
    }

    [Fact]
    public void Rejects_A_Block_Bigger_Than_Itself()
    {
        var ring = new PcmRing(capacityFrames: 4, Channels);
        Assert.Throws<ArgumentOutOfRangeException>(() => ring.Write(Ramp(5), 5));
    }

    /// <summary>A slow consumer and a fast producer must not lose or reorder a sample between them.</summary>
    [Fact]
    public void Survives_A_Fast_Producer_And_A_Slow_Consumer()
    {
        const int TotalFrames = 5000;
        var ring = new PcmRing(capacityFrames: 128, Channels);

        var consumer = Task.Run(() =>
        {
            var got = new List<float>();
            var dst = new float[37 * Channels];   // deliberately not a divisor of anything
            while (true)
            {
                int n = ring.Read(dst);
                if (n == 0) break;
                for (int i = 0; i < n; i++) got.Add(dst[i]);
            }
            return got;
        });

        var block = 64;
        for (int at = 0; at < TotalFrames; at += block)
        {
            int frames = Math.Min(block, TotalFrames - at);
            Assert.True(ring.Write(Ramp(frames, from: at), frames));
        }
        ring.CompleteWriting();

        Assert.True(consumer.Wait(TimeSpan.FromSeconds(10)));
        var all = consumer.Result;

        Assert.Equal(TotalFrames * Channels, all.Count);
        for (int i = 0; i < TotalFrames; i++)
        {
            Assert.Equal(i, all[i * Channels]);
            Assert.Equal(-i, all[i * Channels + 1]);
        }
    }
}
