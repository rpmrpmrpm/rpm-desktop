namespace RPM.Core.Audio;

/// <summary>A stream kept ahead of whoever reads it.
///
/// <para>The decoders on both heads read their socket from inside the engine's own read. <c>Supply</c> in
/// <c>StreamPcmSource</c> takes another eight kilobytes off the wire when it has run out of packets, and
/// blocking there is its job. That is fine while the bytes are already in the kernel's buffer, because the
/// read is then a memory copy. It is not fine the rest of the time. The thread that fills the speakers is
/// then waiting on a network round trip, and what comes out is a gap.
/// </para>
///
/// <para>The join of a head start shows this most clearly. Nothing reads the socket between the response
/// headers arriving and the clip running dry. So the kernel takes what fits in its receive window, and then
/// the sender stops. Three seconds later the join asks for three seconds of audio at once (half a megabyte
/// of FLAC), and pays for whatever the window would not hold, inline, in silence.
/// </para>
///
/// <para>So one thread has the job of staying further along than the decoder, and the decoder reads from a
/// ring that this thread fills. This is a common design, and it does more than fix the join.
/// <see cref="Seconds"/> of audio in hand are <see cref="Seconds"/> of network trouble that the listener
/// never hears.
/// </para>
///
/// <para>Ownership is unchanged, and it matters. This takes over the stream handed to it. Closing it is
/// still what unblocks a read that is waiting on a connection that has stopped delivering.</para>
/// </summary>
public sealed class ReadAhead : Stream
{
    /// <summary>How much audio is kept in hand, in seconds.
    ///
    /// <para>This number decides what a hiccup costs. Ten seconds covers the join of a head start three
    /// times over, and a ride in a lift between two wifi access points. It is short enough that a record
    /// skipped after ten seconds has not already fetched a minute of itself over somebody's data
    /// plan.</para>
    /// </summary>
    public const double Seconds = 10;

    /// <summary>The upper limit. An unknown rate also gets this value. A phone can have two of these open
    /// at once: the record playing, and the one opening behind it. The limit is chosen so that two of them
    /// use little memory.</summary>
    private const int Most = 2 * 1024 * 1024;

    /// <summary>The lower limit. Below this the ring is smaller than a few reads, and the thread spends its
    /// time being woken instead of reading.</summary>
    private const int Least = 128 * 1024;

    /// <summary>One read from the network. It is large enough that a fast connection is not read in small
    /// pieces, and small enough that the first bytes reach the parser without waiting for a full
    /// read.</summary>
    private const int Bite = 32 * 1024;

    /// <summary>How long an empty ring must stay empty before it counts as a gap.
    ///
    /// <para>A quarter of a second is about where a wait stops being normal scheduling and starts being
    /// something a listener can hear. Below that, the decoder is simply ahead of the pump for a moment.
    /// This is normal on a connection that keeps up.</para>
    /// </summary>
    private static readonly TimeSpan Stall = TimeSpan.FromMilliseconds(250);

    /// <summary>Room for <see cref="Seconds"/> of audio at the rate the store says it is serving.
    /// </summary>
    /// <param name="kilobits">What is being served, or <c>0</c> for the file as uploaded. Its rate cannot
    /// be known from here, so it gets the upper limit.</param>
    public static int RoomFor(int kilobits) =>
        kilobits <= 0 ? Most : (int)Math.Clamp(kilobits * 1000L / 8 * Seconds, Least, Most);

    private readonly Stream _inner;
    private readonly byte[] _ring;
    private readonly object _gate = new();
    private readonly Thread _pump;

    /// <summary>Called once, the first time this stream really runs dry. See <see cref="Starved"/> for what
    /// "really" rules out.</summary>
    private readonly Action? _starved;

    /// <summary>Where the reader is, and how much is in hand. Only the pump adds, and only the reader
    /// takes. So the write position (<c>_at + _held</c>) does not change when the reader consumes:
    /// <c>_at</c> goes up by exactly what <c>_held</c> goes down by. That lets the pump work out where to
    /// write, release the lock for the read, and still be correct afterwards.</summary>
    private int _at, _held;

    private bool _ended, _closed;
    private Exception? _fault;

    /// <summary>Whether the ring has ever held a useful amount. Until it has, an empty ring means the
    /// stream has only just opened. That is not the same as a connection that cannot keep up. Telling the
    /// two apart is the purpose of the count.</summary>
    private bool _filled;

    /// <param name="inner">The bytes. Taken over, and disposed when this is.</param>
    /// <param name="capacity">How much to keep in hand. See <see cref="RoomFor"/>.</param>
    /// <param name="starved">Called the first time the decoder asks and the ring, having filled once, is
    /// empty. That is the connection failing to keep up with what was asked of it. It is the only place
    /// this fact can be known.</param>
    public ReadAhead(Stream inner, int capacity, Action? starved = null)
    {
        ArgumentNullException.ThrowIfNull(inner);

        _inner = inner;
        _starved = starved;
        _ring = new byte[Math.Clamp(capacity, Bite * 2, Most)];

        // Its own thread rather than a pool thread. This one spends the length of a record parked inside a
        // blocking read. A pool thread that does that cannot be used by anyone else. With two streams open
        // and a pool sized to the phone's cores, that is a real share of the pool. See Dispose for what
        // unparks it.
        _pump = new Thread(Pump) { IsBackground = true, Name = "read-ahead" };
        _pump.Start();
    }

    /// <summary>How much is in hand right now. A diagnostic reads this to answer "was the gap the network,
    /// or was it us". An empty ring at a stutter means the connection. A full ring does not.</summary>
    public int Buffered { get { lock (_gate) return _held; } }

    /// <summary>Whether the far end has finished sending. This is not the same as having been read to the
    /// end. What is in hand is still to come.</summary>
    public bool Drained { get { lock (_gate) return _ended; } }

    /// <summary>How long the decoder has spent waiting on an empty ring. This is the gap as the listener
    /// heard it.
    ///
    /// <para>It is counted in <see cref="Stall"/> steps. Three cases are deliberately not counted, because
    /// none of them is the connection failing. A wait shorter than one step is the decoder overtaking the
    /// pump for a moment, which happens on every fast connection. A ring that has never filled belongs to a
    /// stream that has only just opened. And an empty ring after the far end has stopped sending is the end
    /// of the recording.</para>
    ///
    /// <para>What is left is the honest measure of a connection that cannot carry what was asked of it. The
    /// <see cref="RateWatch"/> turns it into a lower rate for the next record.</para>
    /// </summary>
    public TimeSpan Starved { get { lock (_gate) return _starves * Stall; } }

    private int _starves;

    private void Pump()
    {
        var bite = new byte[Bite];

        while (true)
        {
            int room, at;

            lock (_gate)
            {
                while (!_closed && _held >= _ring.Length) Monitor.Wait(_gate);
                if (_closed) return;

                // Up to the end of the ring, so that one read lands as one contiguous copy. The pump never
                // has to handle a wrap-around.
                at = (_at + _held) % _ring.Length;
                room = Math.Min(Math.Min(bite.Length, _ring.Length - _held), _ring.Length - at);
            }

            int got;
            try
            {
                got = _inner.Read(bite, 0, room);
            }
            catch (Exception ex)
            {
                // The fault is kept, not swallowed. It is passed to the reader once the ring has drained. A
                // connection that died is not a recording that ended. The difference decides whether the
                // album moves on, or whether the track is reopened where it stopped.
                lock (_gate)
                {
                    _fault = ex;
                    _ended = true;
                    Monitor.PulseAll(_gate);
                }
                return;
            }

            lock (_gate)
            {
                if (_closed) return;

                if (got <= 0)
                {
                    _ended = true;
                    Monitor.PulseAll(_gate);
                    return;
                }

                Buffer.BlockCopy(bite, 0, _ring, at, got);
                _held += got;

                // Half is enough to call the ring filled. Requiring a full ring would be a stronger claim
                // than this needs. And a ring that never quite fills, because the reader keeps pace with a
                // fast connection, would never let a real stall be counted.
                if (_held >= _ring.Length / 2) _filled = true;

                Monitor.PulseAll(_gate);
            }
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if (count == 0) return 0;

        while (true)
        {
            lock (_gate)
            {
                if (_held > 0)
                {
                    var got = Math.Min(count, Math.Min(_held, _ring.Length - _at));

                    Buffer.BlockCopy(_ring, _at, buffer, offset, got);

                    _at = (_at + got) % _ring.Length;
                    _held -= got;

                    Monitor.PulseAll(_gate);
                    return got;
                }

                // Closed under us is a teardown, and a teardown is an end, not a failure. A fault is raised
                // only once everything that did arrive has been played out.
                if (_closed) return 0;

                if (_ended)
                    return _fault is { } fault ? throw new IOException(fault.Message, fault) : 0;

                // Nothing in hand, and more is coming. Waiting here is correct. But waiting is not
                // starving, and the difference is how long. The decoder overtakes the pump for a moment on
                // every fast connection. What a listener hears is a wait that lasts.
                if (Monitor.Wait(_gate, Stall)) continue;

                _starves++;

                // Before the ring has filled once, an empty ring means a stream that has only just opened.
                // It is not a connection that cannot keep up.
                if (!_filled || _starves > 1) continue;
            }

            // Outside the lock, and once per stream. This reaches a rate watcher. Holding the ring's lock
            // while calling code outside this class lets the pump end up waiting on something it does not
            // control.
            _starved?.Invoke();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;
                Monitor.PulseAll(_gate);
            }

            // Outside the lock. This is the line that matters. The pump may be parked inside a read on a
            // connection that never sends again. Closing the stream under it is the only thing that makes
            // that read return. Nothing joins the thread. It is a background thread, and its last action is
            // to notice that it was closed.
            try { _inner.Dispose(); } catch { /* already gone; there is nothing to do about it */ }
        }

        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
