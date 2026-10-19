namespace RPM.Core.Analysis;

/// <summary>The state of the machine at the moment the analyser is about to use some of it.</summary>
/// <param name="ProcessorCount">Cores, as the runtime counts them.</param>
/// <param name="AvailableMemoryBytes">Memory the machine could still hand out, or zero when the
/// runtime cannot say (for example before the garbage collector has run).</param>
/// <param name="IsPlaying">Whether the app is playing music. Analysis must stay behind playback:
/// a starved audio thread causes dropouts, which the listener hears at once.</param>
/// <param name="IsMixing">Whether a DJ set is running. That means playback plus a second decode, a
/// resampler and a changeover that must land on a beat.</param>
public readonly record struct MachineState(
    int ProcessorCount,
    long AvailableMemoryBytes,
    bool IsPlaying,
    bool IsMixing);

/// <summary>
/// How hard the analyser may work right now: how many tracks at once, and how long to
/// leave the machine alone between them.
/// </summary>
/// <param name="Slots">Tracks decoded and analysed at the same time. Never zero.</param>
/// <param name="Breath">The pause after each track. Zero when nothing needs protecting.</param>
/// <param name="Deferred">True when the machine does not have the memory for this at all and the
/// background sweep should wait and ask again. Direct asks ignore it, because something is waiting
/// for the result.</param>
public readonly record struct AnalysisPace(int Slots, TimeSpan Breath, bool Deferred);

/// <summary>
/// Decides <see cref="AnalysisPace"/> from <see cref="MachineState"/>.
///
/// <para>Analysis would use every core if allowed: a track is a full decode plus nine windows
/// through a transformer, and there is always another track. The visible result would be a slow
/// interface and skipping playback.</para>
///
/// <para>Two resources are limited: cores, and memory. One window through the beat model peaks near
/// a gigabyte (see <c>Assets/OnnxModels/README.md</c>), so on a machine with 8 GB and a browser
/// open, two analyses at once can mean the machine starts swapping.</para>
///
/// <para>This class is pure and separate from the service that acts on it, so the rules can be
/// read and tested without the audio decoding around them.</para>
/// </summary>
public static class AnalysisThrottle
{
    /// <summary>The most tracks analysed at once, whatever the machine has. Above four, the decodes
    /// compete for memory bandwidth more than they add throughput.</summary>
    public const int MaxSlots = 4;

    /// <summary>Cores left free for the rest of the app: the UI thread, the audio thread, and the
    /// threads the compositor needs to draw the moving record.</summary>
    private const int ReservedCores = 2;

    /// <summary>The peak memory of one track through the beat model. Below this much free memory the
    /// background sweep waits, so the machine does not start swapping.</summary>
    public const long WindowMemoryBytes = 1_100L * 1024 * 1024;

    /// <summary>Extra free memory required for everything else that is running. Analysis is a
    /// background task and must not be the reason a machine starts paging.</summary>
    public const long HeadroomBytes = 700L * 1024 * 1024;

    /// <summary>The pause between tracks while music is playing.</summary>
    private static readonly TimeSpan PlayingBreath = TimeSpan.FromSeconds(2);

    /// <summary>The pause between tracks during a set, where a changeover has to land on a beat.</summary>
    private static readonly TimeSpan MixingBreath = TimeSpan.FromSeconds(4);

    /// <summary>
    /// The pace for a background sweep: the pass that works through a playlist nobody is waiting
    /// on.
    /// </summary>
    public static AnalysisPace ForBackground(MachineState state)
    {
        int slots = Math.Clamp(state.ProcessorCount - ReservedCores, 1, MaxSlots);

        // Playing limits the sweep to two tracks; mixing limits it to one. A set already needs two
        // decodes, a resampler and a fade, and it is the one thing in the app with a deadline.
        if (state.IsPlaying)
            slots = Math.Min(slots, 2);
        if (state.IsMixing)
            slots = 1;

        // The number of tracks the free memory can carry. On a small machine this is the binding
        // limit rather than the cores. Zero means the runtime did not say; an unknown value is not
        // a reason to stop, so the limits above still apply.
        if (state.AvailableMemoryBytes > 0)
        {
            long spare = state.AvailableMemoryBytes - HeadroomBytes;
            if (spare < WindowMemoryBytes)
                return new AnalysisPace(1, MixingBreath, Deferred: true);

            slots = Math.Min(slots, (int)(spare / WindowMemoryBytes));
        }

        var breath = state.IsMixing ? MixingBreath
                   : state.IsPlaying ? PlayingBreath
                   : TimeSpan.Zero;

        return new AnalysisPace(Math.Max(slots, 1), breath, Deferred: false);
    }

    /// <summary>
    /// The pace for a track that something is waiting on, for example the record about to start on
    /// a deck.
    ///
    /// <para>It uses the same limit on parallel tracks and nothing else. A pause between tracks
    /// would delay the caller, and a machine short of memory must still answer, because the
    /// alternative is a record played without a beat grid.</para>
    /// </summary>
    public static AnalysisPace ForDirectAsk(MachineState state) =>
        new(Math.Clamp(state.ProcessorCount - ReservedCores, 1, MaxSlots), TimeSpan.Zero, Deferred: false);
}
