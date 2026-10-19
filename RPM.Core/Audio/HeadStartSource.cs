namespace RPM.Core.Audio;

/// <summary>
/// A track that starts from a few seconds already in memory, and continues from the real thing.
///
/// <para>
/// It removes the delay at the start of a track. Starting a track needs a signed URL, a file header and a
/// first decode before any sound comes out. That takes about half a second on a good connection, which is
/// noticeable when the user skips quickly through an album. Playback can prepare the records next to the
/// current one in advance, but not twenty of them, because each one costs a connection and seconds of
/// decoded audio.
/// </para>
///
/// <para>
/// A head clip is small enough to keep for a whole album at once (three seconds of 96 kbit/s AAC is
/// about 36 KB), so the first seconds of every track can already be in memory when the user skips.
/// This class plays the clip and then hands over to the real stream when it is ready.
/// </para>
///
/// <para>
/// The join is exact and has no crossfade, because it does not need to be hidden: it is the same
/// recording on both sides. The number of frames that the clip actually produced is discarded from
/// the start of the real source, so the join falls on a frame boundary whatever the encoder did with
/// priming samples. This is why the count comes from what was played and not from the requested
/// length of the clip.
/// </para>
/// </summary>
public sealed class HeadStartSource : IPcmSource
{
    private readonly IPcmSource _head;
    private readonly Func<IPcmSource?> _rest;
    private readonly int _channels;

    private IPcmSource? _real;
    private bool _headDone, _joined, _disposed;

    /// <summary>Frames of the real source still to be discarded before it is in step with the clip.
    /// It is kept between reads because the discard can run out of audio halfway: at the join the
    /// stream has only just started, and a wait there is only a wait, not the end of the track.</summary>
    private long _skipLeft;

    /// <summary>Frames the clip actually produced. This many frames are discarded from the real
    /// source, so the join falls on the frame that the listener has heard up to.</summary>
    private long _played;

    /// <summary>Buffer for reading the real source in the one block that spans the join.</summary>
    private float[] _scratch = [];

    /// <param name="head">The clip. This object takes ownership and disposes it when it is disposed
    /// itself.</param>
    /// <param name="rest">The real stream, requested when the clip runs out. It may block briefly. It may
    /// return null, and then the track ends where the clip ended.</param>
    public HeadStartSource(IPcmSource head, Func<IPcmSource?> rest, int channels, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(head);
        ArgumentNullException.ThrowIfNull(rest);

        _head = head;
        _rest = rest;
        _channels = Math.Max(1, channels);
        Label = label ?? head.Label;
    }

    public string Label { get; }

    /// <summary>Whether the real stream has taken over. A caller can use this to find out whether the
    /// head start was used, or whether it ran out first.</summary>
    public bool Joined => _joined;

    /// <summary>
    /// Whether the hand-over is complete, as opposed to still in progress.
    ///
    /// <para>This is not the same as <see cref="Joined"/>. <c>Joined</c> is set before the stream is
    /// requested, so that the stream is never requested twice. This property is set only after the
    /// stream has been received. A reader on another thread must use this property. It is volatile, so
    /// when a reader sees it true, it also sees everything written before it. A reader that used
    /// <c>Joined</c> would read the wait as zero in exactly the case where the wait matters.</para>
    /// </summary>
    public bool HandedOver => _handedOver;

    private volatile bool _handedOver;

    /// <summary>
    /// How long the seam waited for the stream behind the clip. Only meaningful once
    /// <see cref="HandedOver"/> says so.
    ///
    /// <para>The head start exists to keep this number at zero. Zero means the stream was open before
    /// the clip ran out, and the join cost nothing. Any larger value is silence that the listener
    /// heard: the clip had ended and nothing could play until this much time had passed.</para>
    /// </summary>
    public TimeSpan Waited { get; private set; }

    /// <summary>
    /// Whichever half is playing answers for the whole.
    ///
    /// <para>Before the join this is the state of the clip, which is a local file and ends when it
    /// ends. After the join it is the state of the real stream, including its waits. This matters:
    /// three seconds of clip may not be enough to cover a slow connection, and the result must then be
    /// a wait and not the end of the track.</para>
    ///
    /// <para>
    /// The state is <see cref="PcmState.Failed"/> when the clip ran out and there is nothing to hand
    /// over to. This answer must be correct, because from the outside it looks the same as a track
    /// that finished: no more audio. Only this class knows the difference. <c>AudioEngine</c> reads
    /// the state into <c>EndedAs</c> when it retires the source. Without it, the player would play
    /// three seconds of a record and move on to the next track as if nothing was wrong.
    /// </para>
    /// </summary>
    public PcmState State =>
        !_joined ? _head.State
        : _real is { } real ? real.State
        : PcmState.Failed;

    public string? Fault =>
        _joined && _real is null
            ? "the stream behind the head clip never arrived"
            : (_joined ? _real?.Fault : _head.Fault);

    public int Read(float[] buffer, int frames)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (_disposed || frames <= 0) return 0;

        var done = 0;

        // Play the clip while any of it remains.
        if (!_headDone)
        {
            done = _head.Read(buffer, frames);
            _played += done;

            if (done >= frames) return done;

            // A clip that returns fewer frames than requested has reached its end.
            _headDone = true;
        }

        var real = Join();
        if (real is null) return done;

        // Still catching up to the position where the clip ended. This must not block: a source with
        // nothing yet returns nothing, this method returns nothing, and the buffer above waits and
        // asks again. Blocking would hold the decode thread inside one read with no way to cancel.
        if (_skipLeft > 0 && !Skip(real)) return done;

        // Fill the part that the clip did not. Read straight into the caller's buffer when the clip
        // gave nothing to this block, which is true for every block after the first one past the join.
        if (done == 0) return real.Read(buffer, frames);

        // Otherwise read through the scratch buffer. A source always writes from the start of the
        // buffer it is given, so reading into the caller's buffer would overwrite the last frames of
        // the clip. This is the one block where the join happens, and the result would be audible.
        var want = frames - done;

        if (_scratch.Length < want * _channels) _scratch = new float[want * _channels];

        var got = real.Read(_scratch, want);
        if (got > 0)
        {
            Array.Copy(_scratch, 0, buffer, done * _channels, got * _channels);
            done += got;
        }

        return done;
    }

    /// <summary>
    /// Brings the real stream in. The frames that the clip already played are discarded by
    /// <see cref="Skip"/>.
    ///
    /// <para>The discard is the join. The frames come from what the source has already decoded ahead,
    /// so it is a copy and not a fetch. Audio that is not yet decoded arrives much faster than real
    /// time.</para>
    /// </summary>
    private IPcmSource? Join()
    {
        if (_joined) return _real;

        _joined = true;

        // The wait is measured. Everything the factory does (a network lookup, the signed URL, the
        // connection, the first decode) happens while the clip plays. If it takes longer than the clip, the
        // only sign is a short silence in the audio. See Waited.
        var began = System.Diagnostics.Stopwatch.GetTimestamp();

        _real = _rest();

        Waited = System.Diagnostics.Stopwatch.GetElapsedTime(began);
        _skipLeft = _real is null ? 0 : _played;

        // Set last. The field is volatile, so this write publishes the two assignments above to any
        // thread that reads them, such as the timer thread.
        _handedOver = true;

        return _real;
    }

    /// <summary>
    /// Discards the frames that the clip already played, so the clip and the real source are in step.
    ///
    /// <para>The frames come from what the source has decoded ahead, so this is a copy and not a fetch.
    /// It works only for audio that has arrived, which for a stream that has just started may be none
    /// of it.</para>
    /// </summary>
    /// <returns>Whether the two are now in step. False means more audio is needed first, or the real
    /// source is shorter than the clip. In that case the two are not the same recording, and the track
    /// should stop.</returns>
    private bool Skip(IPcmSource real)
    {
        // Read one block at a time. Do not allocate a buffer the size of the clip, because that is a large
        // allocation on the decode thread.
        if (_scratch.Length < 4096 * _channels) _scratch = new float[4096 * _channels];

        while (_skipLeft > 0)
        {
            var want = (int)Math.Min(4096, _skipLeft);
            var got = real.Read(_scratch, want);

            if (got > 0)
            {
                _skipLeft -= got;
                continue;
            }

            // Nothing right now: try again later. Any other state means the recording has no more
            // audio than the clip already played, and waiting will not produce any.
            if (real.State == PcmState.Waiting) return false;

            _skipLeft = 0;
            return false;
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _head.Dispose();

        // Only if it was requested. Calling the factory here would open a stream just to close it.
        if (_joined) _real?.Dispose();
    }
}
