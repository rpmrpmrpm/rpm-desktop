namespace RPM.Core.Audio;

/// <summary>Decoded audio held between the decoder that produced it and the engine that will ask for it.
///
/// <para>A decoder returns whatever a frame of its format happens to contain. That is a few thousand
/// samples, never the number the pump asked for. Something has to hold the difference. A single buffer with
/// two indices is enough when the only request is for the next block.
/// </para>
///
/// <para>Reading ahead needs more. It means accumulating several decoded buffers instead of replacing one,
/// so that a skip is answered from memory rather than from the network. Accumulation is where the
/// arithmetic is easy to get wrong. If the offset is not compacted, the buffer grows for the length of the
/// track. If it is compacted wrongly, a fraction of a second of the previous buffer plays again, which is
/// audible as a click.
/// </para>
///
/// <para>It is here, and not in the phone head, so that the arithmetic can be tested without a device, like
/// <see cref="RPM.Core.Playback.QueueEdit"/> and <see cref="RPM.Core.Playback.StartGate"/>. It holds no
/// unmanaged memory and knows nothing about where the audio came from.</para>
/// </summary>
public sealed class Spill
{
    private readonly int _channels;

    private float[] _samples = [];

    /// <summary>Frames held and not yet taken.</summary>
    private int _frames;

    /// <summary>Where the unread audio starts, in frames. Everything before it has been handed over, and is
    /// dead space until the next compaction.</summary>
    private int _offset;

    public Spill(int channels) => _channels = Math.Max(1, channels);

    /// <summary>How much audio is waiting, in frames.</summary>
    public int Frames => _frames;

    /// <summary>How much has been allocated, in samples. Used by a test that checks the buffer does not
    /// grow without limit over a long read.</summary>
    public int Capacity => _samples.Length;

    /// <summary>Makes room for <paramref name="samples"/> more, and says where to put them.
    ///
    /// <para>Returning the array instead of taking one keeps this free of a copy. The decoder writes
    /// straight from its own buffer into this one. For an audio path fed forty times a second, that
    /// matters. Call <see cref="Commit"/> afterwards. Until then, nothing has been added.</para>
    /// </summary>
    public (float[] Buffer, int At) Reserve(int samples)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(samples);

        var held = _frames * _channels;

        // Everything already handed out is dead space at the front. Moving what is left down stops the
        // buffer growing for the length of the track.
        if (_offset > 0)
        {
            if (held > 0) Array.Copy(_samples, _offset * _channels, _samples, 0, held);
            _offset = 0;
        }

        if (_samples.Length < held + samples) Array.Resize(ref _samples, held + samples);

        return (_samples, held);
    }

    /// <summary>Accepts what was just written into the space that <see cref="Reserve"/> gave.</summary>
    public void Commit(int samples)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(samples);

        _frames += samples / _channels;
    }

    /// <summary>Copies up to <paramref name="frames"/> frames into <paramref name="destination"/>,
    /// starting at
    /// <paramref name="atFrame"/> frames into it, and returns how many were actually there.
    /// </summary>
    public int Take(float[] destination, int atFrame, int frames)
    {
        ArgumentNullException.ThrowIfNull(destination);

        var take = Math.Min(frames, _frames);
        if (take <= 0) return 0;

        Array.Copy(_samples, _offset * _channels, destination, atFrame * _channels, take * _channels);

        _offset += take;
        _frames -= take;

        return take;
    }

    public void Clear()
    {
        _frames = 0;
        _offset = 0;
    }
}
