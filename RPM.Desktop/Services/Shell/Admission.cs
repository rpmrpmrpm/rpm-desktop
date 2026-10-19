namespace RPM.Desktop.Services.Shell;

/// <summary>Why a pass was not let through. Said rather than inferred, because "wait 400ms" is the
/// same answer for four different reasons and only one of them is worth acting on.</summary>
public enum Held
{
    /// <summary>It was let through.</summary>
    None = 0,

    /// <summary>The run has not reached the phase this pass asked to start in.</summary>
    TooEarly,

    /// <summary>The run is past that phase, but this pass's wave has not opened yet: something ahead
    /// of it in the stated order is still working.</summary>
    WaveShut,

    /// <summary>The drawing thread is unavailable right now. This is an outage in progress, not one
    /// that ended earlier. It is the reading that matters during a hang.</summary>
    UiHeld,

    /// <summary>The drawing thread is answering, but not yet steadily. A few turns of calm show that the
    /// busy period is over, and not just a gap between two busy turns.</summary>
    UiBusy,

    /// <summary>The interface is free and this pass may run, but the lane it wants is full.</summary>
    LaneFull,
}

/// <summary>What the governor answered.</summary>
/// <param name="Go">Whether the pass may do its next unit of work.</param>
/// <param name="WaitMs">How long to wait before asking again. Zero when <paramref name="Go"/> is
/// true.</param>
/// <param name="Why">What stopped it, for the ring and for a test.</param>
public readonly record struct Verdict(bool Go, double WaitMs, Held Why)
{
    public static Verdict Yes { get; } = new(true, 0, Held.None);
}

/// <summary>
/// Whether a background pass may do its next unit of work, and for how long to go away if not.
///
/// <para><b>A function, with no state and no clock of its own.</b> Everything it needs is an argument,
/// which is what makes the app's admission policy a thing that can be read in one screen and tested
/// without a window, a dispatcher, a library or a thread. <see cref="Governor"/> is the object that
/// holds the state and does the waiting; this is the rule it applies.</para>
///
/// <para><b>The rule, in order.</b> A pass gets through when the run has reached its phase, its wave
/// has opened, the drawing thread is not held and has been calm for a few turns, and its lane has
/// room. If any one of these fails, the pass waits. The length of the wait comes from the interface's
/// own reading, not from a constant. The busier the drawing thread, the longer the pass stays away.
/// For example, a pass is asked to wait 32 ms during a smooth frame, and two seconds during a hang.
/// Neither number is a fixed choice.
/// </para>
/// </summary>
public static class Admission
{
    /// <summary>
    /// The shortest wait there is: one turn of the pulse. Asking again sooner than the reading can
    /// change is asking the same question of the same answer.
    /// </summary>
    public const double FloorMs = 32;

    /// <summary>
    /// The longest. A longer wait would make the pass too late. The interface can become free in the
    /// middle of a long wait, and the pass would not notice for the rest of it.
    /// </summary>
    public const double CeilingMs = 2000;

    /// <summary>
    /// How much longer than the outage itself to stay away.
    ///
    /// <para>Twice, so that a pass backs off faster than the interface recovers. A pass must not arrive
    /// again exactly as the busy period ends. A factor below one would shrink the wait into a spin
    /// against a busy thread.</para>
    /// </summary>
    private const double BackOff = 2;

    /// <summary>How many passes may be inside a lane at once.</summary>
    /// <remarks>
    /// <b>One for the library.</b> The database gate serialises these passes anyway. So a second pass
    /// in this lane does not run twice as fast. It waits in the queue that the interface also uses, and
    /// makes the interface wait behind two holds instead of one. Letting one through at a time does not
    /// change how long the passes take together. It does change how long the window waits.
    /// </remarks>
    public static int Width(Lane lane) => lane switch
    {
        Lane.Library => 1,
        Lane.Disk => 2,

        // Slow somewhere else. A request in flight holds nothing here, so the limit is about not
        // opening forty connections at once rather than about the interface.
        Lane.Network => 4,

        // A quarter of the machine's cores, and never more than two. A listener is playing music on
        // the same machine.
        Lane.Processor => Math.Clamp(Environment.ProcessorCount / 4, 1, 2),

        _ => 1,
    };

    /// <summary>
    /// The rule.
    /// </summary>
    /// <param name="lateMs">How late the last turn of the drawing thread was.</param>
    /// <param name="heldForMs">How long the drawing thread has been unavailable right now, or 0.</param>
    /// <param name="calm">How many turns in a row have arrived on time.</param>
    /// <param name="reached">The phase the run has reached.</param>
    /// <param name="opened">The furthest wave that has been opened.</param>
    /// <param name="notBefore">The phase this pass asked not to start before.</param>
    /// <param name="wave">The wave this pass is in.</param>
    /// <param name="lane">What it competes for.</param>
    /// <param name="runningInLane">How many passes are already inside that lane.</param>
    public static Verdict Ask(
        double lateMs,
        double heldForMs,
        int calm,
        Phase reached,
        Wave opened,
        Phase notBefore,
        Wave wave,
        Lane lane,
        int runningInLane)
    {
        // These two order questions come first. They are answered without looking at the interface. A
        // pass that is not due yet is not due on a quiet machine either. A wait worked out from a calm
        // reading would make it ask again every 32 ms until it is due.
        if (reached < notBefore)
            return new Verdict(false, CeilingMs, Held.TooEarly);

        if (wave > opened)
            return new Verdict(false, CeilingMs, Held.WaveShut);

        // An outage in progress. It is measured from the post that has not landed yet. So this is true
        // during a hang, not only after one. Only the during case is useful.
        if (heldForMs > UiPulse.HeldMs)
            return new Verdict(false, Wait(heldForMs), Held.UiHeld);

        // Answering, but not steadily. A gap between two layout passes does not mean the busy period
        // has ended.
        if (calm < UiPulse.CalmTurns)
            return new Verdict(false, Wait(lateMs), Held.UiBusy);

        // The interface can afford a pass. The last question is whether the lane has room for this pass.
        if (runningInLane >= Width(lane))
            return new Verdict(false, FloorMs, Held.LaneFull);

        return Verdict.Yes;
    }

    /// <summary>How long to stay away, given how long the interface was unavailable for.</summary>
    private static double Wait(double outageMs) =>
        Math.Clamp(outageMs * BackOff, FloorMs, CeilingMs);
}
