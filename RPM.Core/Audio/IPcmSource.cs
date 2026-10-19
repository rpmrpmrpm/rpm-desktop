namespace RPM.Core.Audio;

/// <summary>What a source meant by handing back fewer frames than it was asked for.
///
/// <para>A short read can mean three different things. The track is over, the next bytes have not arrived
/// yet, or the reader broke. A caller that cannot tell these apart takes the first meaning. On a cold start
/// every stream is still opening, so a stream that has not started yet looks like a finished track, and the
/// queue moves on to the next one.
/// </para>
///
/// <para>Working it out afterwards from arithmetic (how much was heard, compared with the length the
/// catalogue gives the track) is a guess. It is wrong for every file whose tag disagrees with its contents.
/// The source knows what happened, so it is asked.
/// </para>
/// </summary>
public enum PcmState
{
    /// <summary>Audio was handed over, and there is more where it came from.</summary>
    Playing,

    /// <summary>Nothing is available at this instant, but more is coming. For example, a stream whose next
    /// bytes are still on the way.
    ///
    /// <para>This is not a failure and not an end. A consumer that sees this waits. This value is the
    /// reason the enum exists.</para>
    /// </summary>
    Waiting,

    /// <summary>The track is over. The only value that should advance a queue.</summary>
    Ended,

    /// <summary>It broke. <see cref="IPcmSource.Fault"/> says how.</summary>
    Failed,
}

/// <summary>Somewhere interleaved float PCM comes from, at the engine's rate and channel count. The audio
/// engine's pump reads blocks from one of these and knows nothing else about them. A
/// <see cref="FfmpegPcmSource"/> is one decoded file. The DJ's mix of two spinning decks is another. That
/// is the reason this interface exists. The mix reaches the equaliser, the visualiser, the volume and the
/// Cast sink by exactly the same path as a plain track.
/// </summary>
public interface IPcmSource : IDisposable
{
    /// <summary>Fills up to <paramref name="frames"/> frames of <paramref name="buffer"/> (interleaved,
    /// <c>frames × channels</c> samples) and returns how many frames were actually written. Called from the
    /// audio pump thread and nowhere else.
    /// </summary>
    /// <returns>Frames written. Fewer than asked for, including none, does not by itself mean the end of
    /// anything. <see cref="State"/> says what it was.</returns>
    int Read(float[] buffer, int frames);

    /// <summary>A name for the thing being played, for logs and the player's readout.</summary>
    string Label { get; }

    /// <summary>What the last short <see cref="Read"/> meant.
    ///
    /// <para>Only worth asking after a short read. A read that filled the buffer says nothing about what
    /// comes next. Sources that read a local file need not override this. A local file that returns nothing
    /// has ended, and that is what this default says.</para>
    /// </summary>
    PcmState State => PcmState.Ended;

    /// <summary>Why it broke, when <see cref="State"/> is <see cref="PcmState.Failed"/>. Used in the log
    /// and in what the player shows on screen.</summary>
    string? Fault => null;
}
