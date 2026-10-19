namespace RPM.Core.Audio;

/// <summary>A bounded, blocking ring of interleaved float PCM. It has one producer and one consumer.
///
/// <para>This is the Cast sink's backpressure valve. The producer is the audio engine's decode pump. The
/// consumer is the thread that feeds the encoder, which feeds the receiver. When the receiver stops
/// pulling, the consumer stalls, the ring fills, and <see cref="Write"/> waits. So the decoder runs at
/// exactly the speed the far end consumes. There is a fixed, known amount of audio in flight, and no timers
/// anywhere. Pausing a Cast device is this same wait, lasting until play is pressed again.</para>
///
/// <para>The bound is the point. An unbounded queue would let the decoder run to the end of the track into
/// memory, and the reported position would then be wrong.</para>
/// </summary>
public sealed class PcmRing
{
    private readonly object _gate = new();
    private readonly float[] _buffer;

    private int _head;          // read cursor
    private int _count;         // samples currently held
    private bool _completed;    // producer is done; drain what's left, then stop
    private bool _closed;       // torn down; everybody leaves immediately

    private long _written;
    private long _read;

    /// <param name="capacityFrames">How much audio may be in flight. This sets the worst-case delay between
    /// a live EQ change and hearing it, so keep it to a couple of seconds.</param>
    public PcmRing(int capacityFrames, int channels)
    {
        if (capacityFrames < 1) throw new ArgumentOutOfRangeException(nameof(capacityFrames));
        if (channels is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(channels));

        Channels = channels;
        CapacityFrames = capacityFrames;
        _buffer = new float[capacityFrames * channels];
    }

    public int Channels { get; }

    public int CapacityFrames { get; }

    /// <summary>Frames handed to the ring since the last <see cref="Reset"/>.</summary>
    public long WrittenFrames { get { lock (_gate) return _written / Channels; } }

    /// <summary>Frames taken out of the ring since the last <see cref="Reset"/>.</summary>
    public long ReadFrames { get { lock (_gate) return _read / Channels; } }

    /// <summary>Frames sitting in the ring right now, which the far end has not yet consumed.</summary>
    public int QueuedFrames { get { lock (_gate) return _count / Channels; } }

    public bool IsClosed { get { lock (_gate) return _closed; } }

    /// <summary>Copies <paramref name="frames"/> interleaved frames in, <b>blocking</b> until they all
    /// fit. Returns false if the ring was closed (or completed) while waiting. The caller should stop
    /// producing.
    /// </summary>
    public bool Write(ReadOnlySpan<float> interleaved, int frames)
    {
        int samples = frames * Channels;
        if (samples <= 0) return !IsClosed;
        if (samples > interleaved.Length) throw new ArgumentOutOfRangeException(nameof(frames));
        if (samples > _buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(frames), "block is larger than the ring");

        lock (_gate)
        {
            while (!_closed && !_completed && _buffer.Length - _count < samples)
                Monitor.Wait(_gate);

            if (_closed || _completed) return false;

            int tail = (_head + _count) % _buffer.Length;
            int first = Math.Min(samples, _buffer.Length - tail);
            interleaved[..first].CopyTo(_buffer.AsSpan(tail));
            if (first < samples)
                interleaved.Slice(first, samples - first).CopyTo(_buffer.AsSpan(0));

            _count += samples;
            _written += samples;
            Monitor.PulseAll(_gate);
            return true;
        }
    }

    /// <summary>Takes up to the length of <paramref name="destination"/> in samples out, <b>blocking</b>
    /// until at least one is available. Returns 0 when the ring is drained and the producer has finished,
    /// when the ring was closed, or when <paramref name="cancellation"/> fired. Every one of those means
    /// the same thing to a consumer ("stop reading"), so they are not told apart.
    /// </summary>
    public int Read(Span<float> destination, CancellationToken cancellation = default)
    {
        lock (_gate)
        {
            while (!_closed && _count == 0 && !_completed)
            {
                if (cancellation.IsCancellationRequested) return 0;
                // Poll rather than wait forever. A consumer that has been replaced must be able to leave,
                // and nothing will pulse the gate on its behalf.
                Monitor.Wait(_gate, 25);
            }

            if (_closed || cancellation.IsCancellationRequested) return 0;
            if (_count == 0) return 0;   // completed and drained

            int take = Math.Min(destination.Length, _count);
            int first = Math.Min(take, _buffer.Length - _head);
            _buffer.AsSpan(_head, first).CopyTo(destination);
            if (first < take)
                _buffer.AsSpan(0, take - first).CopyTo(destination[first..]);

            _head = (_head + take) % _buffer.Length;
            _count -= take;
            _read += take;
            Monitor.PulseAll(_gate);
            return take;
        }
    }

    /// <summary>The producer has no more audio. Readers drain what is left and then see the end of the
    /// stream. Any writer still waiting gives up.</summary>
    public void CompleteWriting()
    {
        lock (_gate)
        {
            _completed = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Blocks until the consumer has taken everything. The audio is then on its way out, not still
    /// sitting in the ring. Returns false if the ring closed first.</summary>
    public bool WaitDrained(CancellationToken cancellation = default)
    {
        lock (_gate)
        {
            while (!_closed && _count > 0)
            {
                if (cancellation.IsCancellationRequested) return false;
                // Time out so that cancellation is noticed even when nothing is pulsing the gate.
                Monitor.Wait(_gate, 50);
            }
            return !_closed;
        }
    }

    /// <summary>Tears the ring down. Everything queued is discarded, and every waiting thread leaves.
    /// Calling it again has no effect.</summary>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            _count = 0;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Empties the ring and reopens it for the next track. The counters go back to zero, because
    /// the consumer's position is measured from the start of the stream.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _head = 0;
            _count = 0;
            _written = 0;
            _read = 0;
            _completed = false;
            _closed = false;
            Monitor.PulseAll(_gate);
        }
    }
}
