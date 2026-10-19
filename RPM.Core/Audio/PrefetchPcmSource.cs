namespace RPM.Core.Audio;

/// <summary>A decode-ahead buffer in front of another source. Its own thread reads blocks from
/// <paramref name="inner"/> as fast as the ring accepts them. The audio pump only ever copies out of
/// memory.
///
/// <para>Without this, decoding and playing run on the same thread. The pump reads a block from ffmpeg's
/// pipe, hands it to the device, and then reads the next one. Any delay on the way to the bytes (a cold
/// file, a spinning disk, a presigned URL over the network, ffmpeg being descheduled) comes straight out of
/// the device's few hundred milliseconds of queued audio. When that runs out, the sound stops. Continuity
/// comes first, so the decoder is taken off the deadline. It may stall for as long as the ring holds out
/// (seconds, not milliseconds), and the pump never notices.</para>
///
/// <para>The ring is bounded, so the decoder cannot run to the end of the track. It fills up, the producer
/// waits in <see cref="PcmRing.Write"/>, and decoding continues at exactly the speed the pump consumes. The
/// ring is kept small on purpose. Everything in flight here is audio the equaliser has not processed yet,
/// so its size also sets how long an EQ or volume change takes to be heard.</para>
/// </summary>
public sealed class PrefetchPcmSource : IPcmSource, INowPlayingSource
{
    private readonly IPcmSource _inner;
    private readonly PcmRing _ring;
    private readonly Thread _thread;
    private readonly int _channels;
    private readonly int _blockFrames;

    private volatile bool _stopping;

    /// <summary>Why the decode loop stopped. The code above this reads it once the ring runs dry, which is
    /// the end of the track for it. This is the only thing that says what kind of end it was.</summary>
    private volatile PcmState _ended = PcmState.Ended;
    private volatile string? _fault;

    /// <summary>How long to keep waiting for a source that says more audio is coming.
    ///
    /// <para>The wait must be honoured. That is the whole point of <see cref="PcmState.Waiting"/>. But it
    /// cannot be honoured forever. A stream that quietly died would leave the player silent, with nothing
    /// on screen and no way out. Half a minute is much longer than any stall worth waiting through, and
    /// much shorter than a listener's patience with a record that has stopped.</para>
    /// </summary>
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    /// <summary>How often to look again while waiting. It is short enough for the audio to resume as soon
    /// as the bytes arrive, and long enough not to keep a core busy over a slow connection.</summary>
    private const int WaitPollMs = 20;

    /// <param name="inner">The real source. It is taken over, so disposing this disposes it.</param>
    /// <param name="channels">Interleaved channel count of <paramref name="inner"/>.</param>
    /// <param name="capacityFrames">How much audio to decode ahead. This is also the upper limit on how
    /// long a decode stall can be absorbed, and on how late an EQ change is heard.</param>
    /// <param name="blockFrames">How much to read from <paramref name="inner"/> at a time.</param>
    public PrefetchPcmSource(IPcmSource inner, int channels, int capacityFrames, int blockFrames)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (channels is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(channels));
        if (blockFrames < 1) throw new ArgumentOutOfRangeException(nameof(blockFrames));
        if (capacityFrames < blockFrames) throw new ArgumentOutOfRangeException(nameof(capacityFrames));

        _inner = inner;
        _channels = channels;
        _blockFrames = blockFrames;
        _capacityFrames = capacityFrames;
        _keepAhead = capacityFrames;
        _ring = new PcmRing(capacityFrames, channels);

        _thread = new Thread(DecodeLoop)
        {
            IsBackground = true,
            Name = $"RPaMp decode ({inner.Label})",
        };
        _thread.Start();
    }

    public string Label => _inner.Label;

    /// <summary>Passed straight through from the source underneath. A mix reports which record it is on
    /// and where the needle is. Everything that displays what is playing reads this from whatever the
    /// engine is pumping. So a buffer in front of the mix must pass it on, or the header goes blank as soon
    /// as the buffer is added. The reading can be up to a ring's worth of audio ahead of the speakers. That
    /// is small enough for a progress bar.
    /// </summary>
    public NowPlayingTrack? NowPlayingTrack => (_inner as INowPlayingSource)?.NowPlayingTrack;

    /// <summary>What the source underneath meant, once the ring has run dry.
    ///
    /// <para>While the ring still holds audio, this reports <see cref="PcmState.Playing"/>. From above, a
    /// buffer that has not run out has not stopped, whatever the decoder is doing behind it. That is the
    /// purpose of the ring.</para>
    /// </summary>
    public PcmState State => _ring.QueuedFrames > 0 ? PcmState.Playing : _ended;

    public string? Fault => _fault ?? _inner.Fault;

    /// <summary>How much decoded audio is waiting. This is the margin the pump has. If it stays near zero
    /// while playing, the decoder cannot keep up and the sound is at risk.</summary>
    public int BufferedFrames => _ring.QueuedFrames;

    /// <summary>How far ahead of the pump to decode, in frames. The ring's size is the upper limit. This
    /// is where the decoder actually stops, and it can be changed while a record plays.
    ///
    /// <para>Anything a source does downstream of audio that is already decoded (a fader moved, a platter
    /// stopped, a deck swapped) is heard this far late. It is applied to audio that is already in the ring.
    /// A player wants a deep ring. It absorbs a cold file, a cloud read or an ffmpeg hiccup, and the extra
    /// delay cannot be noticed by someone listening to a record. A workbench wants a shallow ring, because
    /// there each of those changes is checked for clicks.</para>
    /// </summary>
    public int KeepAheadFrames
    {
        get => _keepAhead;
        set => _keepAhead = Math.Clamp(value, _blockFrames, _capacityFrames);
    }

    private readonly int _capacityFrames;
    private volatile int _keepAhead;

    /// <summary>How long the decoder waits before looking again once it is far enough ahead. It is short
    /// enough to top the ring up as soon as the pump has taken a block out of it.</summary>
    private const int FullPollMs = 3;

    /// <summary>Copies out of the ring, and waits only if the decoder has not got there yet. Returns short
    /// (and then zero) at the end of the stream, like the source it wraps.</summary>
    public int Read(float[] buffer, int frames)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (frames <= 0) return 0;

        int want = Math.Min(frames * _channels, buffer.Length);
        int got = 0;
        while (got < want)
        {
            int n = _ring.Read(buffer.AsSpan(got, want - got));
            if (n <= 0) break;      // drained and the decoder is finished, or torn down
            got += n;
        }
        return got / _channels;
    }

    private void DecodeLoop()
    {
        AudioThread.EnterDecode();

        var block = new float[_blockFrames * _channels];
        var waitingSince = default(DateTime?);

        try
        {
            while (!_stopping)
            {
                // Far enough ahead. The ring itself waits in Write when it is physically full. This is the
                // softer limit, which can be changed while a record plays. See KeepAheadFrames.
                if (_ring.QueuedFrames >= _keepAhead)
                {
                    Thread.Sleep(FullPollMs);
                    continue;
                }

                int frames = _inner.Read(block, _blockFrames);

                if (frames > 0)
                {
                    waitingSince = null;
                    if (!_ring.Write(block, frames)) return;   // torn down while we were waiting
                    continue;
                }

                // Nothing came back. That can mean three different things, see PcmState. Only one of them
                // is the end of the track. Reading the other two as the end is what made a stream that had
                // not delivered its first bytes look like a record that had finished. The ring still holds
                // what was decoded ahead, so the queued audio keeps playing while this waits.
                if (_inner.State != PcmState.Waiting)
                {
                    _ended = _inner.State;
                    _fault = _inner.Fault;
                    break;
                }

                waitingSince ??= DateTime.UtcNow;

                if (DateTime.UtcNow - waitingSince > WaitLimit)
                {
                    _ended = PcmState.Failed;
                    _fault = _inner.Fault
                        ?? $"nothing arrived for {WaitLimit.TotalSeconds:0} seconds";
                    break;
                }

                Thread.Sleep(WaitPollMs);
            }
        }
        catch (Exception ex)
        {
            // The decoder failed. The ring's completion tells the pump that something happened. This
            // records what happened, which is the difference between a track that ended and one that broke.
            _ended = PcmState.Failed;
            _fault = ex.Message;
        }
        finally
        {
            _ring.CompleteWriting();
        }
    }

    public void Dispose()
    {
        _stopping = true;

        // Order matters. Close() releases the decoder if it is waiting to write into a full ring. Disposing
        // the inner source ends a blocking read on ffmpeg's pipe, because it kills the process behind it.
        // Joining the thread before either of those would only wait out the timeout.
        _ring.Close();
        try { _inner.Dispose(); } catch { }
        if (_thread != Thread.CurrentThread) _thread.Join(1000);
    }
}
