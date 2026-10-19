using RPM.Core.Audio;

namespace RPM.Core.Tests;

/// <summary>
/// The thread that stays further along than the decoder.
///
/// <para>These tests are not about bytes coming out in order, which is the easy half. They are about
/// two things the audio thread depends on: a read is a plain memory copy whenever the bytes have
/// already arrived, and a connection that died is told apart from a recording that ended.</para>
/// </summary>
public class ReadAheadTests
{
    /// <summary>A stream that hands over its bytes slowly, and can be made to break.</summary>
    private sealed class Wire(byte[] bytes, int perRead = 64, int delayMs = 0, int breakAt = -1) : Stream
    {
        private int _at;

        public bool Closed { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (delayMs > 0) Thread.Sleep(delayMs);
            if (Closed) throw new ObjectDisposedException(nameof(Wire));
            if (breakAt >= 0 && _at >= breakAt) throw new IOException("the connection dropped");

            var take = Math.Min(Math.Min(count, perRead), bytes.Length - _at);
            if (take <= 0) return 0;

            Array.Copy(bytes, _at, buffer, offset, take);
            _at += take;
            return take;
        }

        protected override void Dispose(bool disposing)
        {
            Closed = true;
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A wire that can be told to stop sending for a while, which is how a connection
    /// falling behind looks from this side.</summary>
    private sealed class SlowsDown(byte[] bytes) : Stream
    {
        private readonly ManualResetEventSlim _flowing = new(initialState: true);
        private int _at;

        public void Stall() => _flowing.Reset();
        public void Resume() => _flowing.Set();

        public override int Read(byte[] buffer, int offset, int count)
        {
            _flowing.Wait();

            var take = Math.Min(Math.Min(count, 8 * 1024), bytes.Length - _at);
            if (take <= 0) return 0;

            Array.Copy(bytes, _at, buffer, offset, take);
            _at += take;
            return take;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _flowing.Set();
            base.Dispose(disposing);
        }
    }

    private static byte[] Ramp(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++) bytes[i] = (byte)(i % 251);
        return bytes;
    }

    private static byte[] DrainAll(Stream stream, int block)
    {
        var got = new MemoryStream();
        var buffer = new byte[block];

        while (true)
        {
            var read = stream.Read(buffer, 0, block);
            if (read <= 0) break;
            got.Write(buffer, 0, read);
        }

        return got.ToArray();
    }

    [Fact]
    public void Hands_back_every_byte_in_order_however_it_is_asked_for()
    {
        var bytes = Ramp(300_000);

        using var ahead = new ReadAhead(new Wire(bytes, perRead: 1_337), capacity: 128 * 1024);

        // A block size that divides neither the ring nor the wire's reads, so the wrap is exercised and
        // not assumed.
        Assert.Equal(bytes, DrainAll(ahead, block: 999));
    }

    [Fact]
    public void Is_ahead_of_the_reader_by_the_time_the_reader_asks()
    {
        var bytes = Ramp(500_000);

        using var ahead = new ReadAhead(new Wire(bytes, perRead: 32 * 1024), capacity: 256 * 1024);

        // The whole point of reading ahead: by the time anybody reads, the bytes are already here and
        // the read is a copy, not a round trip.
        Assert.True(SpinWaitFor(() => ahead.Buffered > 128 * 1024), $"only {ahead.Buffered} bytes in hand");
    }

    [Fact]
    public void Stops_reading_ahead_once_the_ring_is_full()
    {
        var bytes = Ramp(4 * 1024 * 1024);
        var wire = new Wire(bytes, perRead: 32 * 1024);

        using var ahead = new ReadAhead(wire, capacity: 256 * 1024);

        Assert.True(SpinWaitFor(() => ahead.Buffered >= 256 * 1024 - 32 * 1024));

        // Bounded, and it stays bounded: a record that nobody is listening to any more must not keep
        // fetching into memory on somebody's data plan.
        Thread.Sleep(150);
        Assert.True(ahead.Buffered <= 256 * 1024, $"{ahead.Buffered} bytes in hand");
    }

    [Fact]
    public void A_recording_that_ended_reads_as_an_end()
    {
        var bytes = Ramp(10_000);

        using var ahead = new ReadAhead(new Wire(bytes), capacity: 128 * 1024);

        Assert.Equal(bytes, DrainAll(ahead, block: 4096));
        Assert.Equal(0, ahead.Read(new byte[16], 0, 16));   // and it stays ended
    }

    [Fact]
    public void A_connection_that_dropped_reads_as_a_failure_after_what_arrived()
    {
        var bytes = Ramp(10_000);

        using var ahead = new ReadAhead(new Wire(bytes, perRead: 1_000, breakAt: 4_000), capacity: 128 * 1024);

        var got = new MemoryStream();
        var buffer = new byte[512];

        // Everything that did arrive is played first. Discarding it to report the fault sooner would
        // turn a connection that dropped four seconds early into one that dropped now.
        var broke = Assert.Throws<IOException>(() =>
        {
            while (true)
            {
                var read = ahead.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                got.Write(buffer, 0, read);
            }
        });

        Assert.Equal(4_000, got.Length);
        Assert.Contains("dropped", broke.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Closing_it_closes_the_connection_under_the_pump()
    {
        var wire = new Wire(Ramp(8 * 1024 * 1024), perRead: 4096, delayMs: 5);
        var ahead = new ReadAhead(wire, capacity: 128 * 1024);

        ahead.Read(new byte[64], 0, 64);
        ahead.Dispose();

        // The line the engine's teardown depends on: a read parked on a station that has stopped
        // sending returns because the stream under it was closed, not because it timed out.
        Assert.True(wire.Closed);
    }

    [Fact]
    public void A_reader_that_arrives_after_it_was_closed_is_told_the_stream_ended()
    {
        var ahead = new ReadAhead(new Wire(Ramp(1_000_000), perRead: 4096, delayMs: 5), capacity: 128 * 1024);

        ahead.Dispose();

        // A teardown is an end, not a failure. Reporting it as a failure would start the retry ladder
        // on a track the user stopped.
        Assert.Equal(0, ahead.Read(new byte[64], 0, 64));
    }

    [Fact]
    public void A_stream_that_has_only_just_opened_is_not_a_stream_that_is_starving()
    {
        // Slow to give the first bytes, so the reader is waiting, but the ring has never held anything,
        // which is what "just opened" looks like from inside. Counting it would turn every cold start
        // into evidence that the connection is too slow for the rate.
        var told = 0;
        using var ahead = new ReadAhead(
            new Wire(Ramp(200_000), perRead: 4096, delayMs: 30), capacity: 128 * 1024,
            starved: () => Interlocked.Increment(ref told));

        ahead.Read(new byte[64], 0, 64);

        Assert.Equal(TimeSpan.Zero, ahead.Starved);
        Assert.Equal(0, Volatile.Read(ref told));
    }

    [Fact]
    public void A_ring_that_filled_and_then_ran_dry_says_so_once()
    {
        var told = 0;
        var wire = new SlowsDown(Ramp(2_000_000));

        using var ahead = new ReadAhead(wire, capacity: 128 * 1024,
            starved: () => Interlocked.Increment(ref told));

        Assert.True(SpinWaitFor(() => ahead.Buffered >= 64 * 1024));

        wire.Stall();

        // Drain what is in hand and then keep asking: the ring is empty, the far end has not ended,
        // and the bytes arrive slower than they are read. That is the connection failing to carry
        // what was asked of it.
        var buffer = new byte[16 * 1024];
        var reader = new Thread(() => { for (var i = 0; i < 32; i++) ahead.Read(buffer, 0, buffer.Length); });
        reader.IsBackground = true;
        reader.Start();

        Assert.True(SpinWaitFor(() => Volatile.Read(ref told) > 0), "never said it was starving");

        // Said while it is happening, not once it is over: the notice is for a gap that is being
        // heard now.
        Assert.True(ahead.Starved > TimeSpan.Zero, "counted nothing while it was stalled");

        wire.Resume();
        reader.Join(5_000);

        // Once per stream. A stall four seconds long is one stall, not four hundred reads of one.
        Assert.Equal(1, Volatile.Read(ref told));
        Assert.True(ahead.Starved > TimeSpan.Zero, $"starved {ahead.Starved}");
    }

    [Fact]
    public void A_recording_running_out_is_not_starving()
    {
        var told = 0;
        using var ahead = new ReadAhead(new Wire(Ramp(400_000)), capacity: 128 * 1024,
            starved: () => Interlocked.Increment(ref told));

        DrainAll(ahead, block: 4096);

        // The ring did fill, and it did end empty. That is the record finishing, not the connection
        // giving up, and holding the rate down for it would punish every album that ends.
        Assert.Equal(TimeSpan.Zero, ahead.Starved);
        Assert.Equal(0, Volatile.Read(ref told));
    }

    [Fact]
    public void Sizes_the_ring_from_the_rate_the_store_said_it_was_serving()
    {
        // Ten seconds at 128 kbps is 160,000 bytes. Kilobits are the store's decimal ones, not binary,
        // because that is what the rate on the screen means.
        Assert.Equal(160_000, ReadAhead.RoomFor(128));

        // The file as uploaded has no rate to work from, so it gets the ceiling and not a guess.
        Assert.True(ReadAhead.RoomFor(0) > ReadAhead.RoomFor(320));

        // And the ceiling is a ceiling.
        Assert.Equal(ReadAhead.RoomFor(0), ReadAhead.RoomFor(100_000));
    }

    private static bool SpinWaitFor(Func<bool> until, int msMost = 2_000)
    {
        for (var waited = 0; waited < msMost; waited += 10)
        {
            if (until()) return true;
            Thread.Sleep(10);
        }

        return until();
    }
}
