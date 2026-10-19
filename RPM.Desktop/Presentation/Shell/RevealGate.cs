using Microsoft.UI.Dispatching;

namespace RPM.Desktop.Presentation.Shell;

/// <summary>
/// How the gate came to open. This is the same question as whether the interface may be faded in.
///
/// <para>The caller is told this, because the two answers need different reveals. The fade is a
/// <c>DispatcherDoubleAnimator</c> on <c>CompositionTarget.Rendering</c>. The drawing thread drives it,
/// so a busy thread does not make it run late. It makes it stop and jump. A 730 ms fade took 6.7
/// seconds on a busy thread. Do not start a fade on a thread that is known to be busy.</para>
/// </summary>
internal enum RevealWay
{
    /// <summary>The drawing thread answered six turns on time. It is free, and the fade will run.</summary>
    Quiet,

    /// <summary>A bound ran out while the thread was still busy. This is either the patience after the
    /// screen came up, or the backstop from launch. What is shown is shown at once, because an animation
    /// would play as a slideshow on a busy thread.</summary>
    Ceiling,
}

/// <summary>
/// When the interface may be shown.
///
/// <para>The reveal does not use a fixed clock. A fixed time after the first layout is a guess, not a
/// fact about the app. On this app the busiest part of startup is when the library feeds the wall,
/// each card measures itself at a width that is about to change, and a collection changes every 80 ms.
/// A fixed delay can start the fade inside that busy part, and the fade then stops and jumps.</para>
///
/// <para>That matters more on Skia. There, EVERY <c>DoubleAnimation</c> is a
/// <c>DispatcherDoubleAnimator</c> that ticks on <c>CompositionTarget.Rendering</c>. The fade is driven
/// by the same thread it competes with, so a busy thread means a fade that stops and jumps.</para>
///
/// <para>So the reveal waits for two facts. The first is that <b>the drawing thread has answered on time,
/// several turns in a row, just now</b>. A timer asks for a turn every 16 ms. A turn that arrives more
/// than two frames late means the thread is held by other work. Only a run of turns that are not late
/// shows that the startup work has settled. <see cref="Diagnostics.SwitchDiag"/> makes the same
/// measurement for a view switch, for the same reason. It costs little: a dispatcher timer watches the
/// thread, whereas <c>CompositionTarget.Rendering</c> would need a frame on every turn.</para>
///
/// <para>The second is that <b>there is something on the screen to reveal</b>, see
/// <see cref="Services.Shell.FirstScreen"/>. A free thread is not a finished screen. The home screen's
/// read runs on a worker, so the thread is idle for all of a 1.2 second read. Without this second fact
/// the gate would open at its floor over a wall with nothing in it. Both facts are required. The run of
/// calm is counted only while the screen is up. See <see cref="Turn"/>.</para>
///
/// <para><b>Three bounds, and none has a default.</b> The floor is a product decision: a fast start must
/// not just flash the wordmark. The patience applies when the thread never goes calm, and it is counted
/// FROM THE SCREEN. The backstop applies when the screen never reports at all, and it is counted from
/// launch, because there is nothing else to count it from. A caller that forgot any of them would be
/// wrong in a way that only shows on a slow machine, so the compiler requires all three.</para>
///
/// <para><b>Why the patience is not counted from launch.</b> A single bound covering both jobs had a flaw.
/// On a cold start the shell loaded 9.5 seconds after launch. A six-second bound counted from launch had
/// already expired when the gate was armed. So the gate opened on its first turn, and the interface was
/// shown while the wall was still 5.4 seconds from having anything in it. A bound on how long someone
/// waits must start when they start waiting.</para>
/// </summary>

internal sealed class RevealGate
{
    /// <summary>A turn later than this means the thread is held, not just a little late. It is two frames
    /// at 60 frames per second. The value is defined once for the whole app, on the pulse.</summary>
    internal const double HeldMs = Services.Shell.UiPulse.HeldMs;

    /// <summary>The number of on-time turns needed before the thread counts as free. Six is about 50 ms of
    /// calm. That is long enough that the gap between two layout passes is not taken as the end of the
    /// work, and short enough that it adds no real delay.</summary>
    internal const int QuietTicks = Services.Shell.UiPulse.CalmTurns;

    private readonly TimeSpan _floor;
    private readonly TimeSpan _patience;
    private readonly TimeSpan _backstop;
    private int _quiet;

    internal RevealGate(TimeSpan floor, TimeSpan patience, TimeSpan backstop)
    {
        _floor = floor;
        _patience = patience;
        _backstop = backstop;
    }

    /// <summary>
    /// Whether the reveal is owed whatever the drawing thread is doing.
    /// </summary>
    /// <remarks>
    /// <para>There are two reasons a reveal can be owed, and they are different failures. <b>The
    /// patience</b> applies when the screen has been up for a while and the thread never goes calm.
    /// There is something to look at, and the fade has had its chance. <b>The backstop</b> applies when
    /// the screen never reports at all, within a length of time no working startup takes. That is a
    /// fault, not a slow machine.</para>
    ///
    /// <para>The floor applies to the patience but not to the backstop. A start that gets its screen up
    /// before the floor still waits for the floor. A broken startup that reaches the backstop does not
    /// wait for it.</para>
    /// </remarks>
    internal bool Overdue(TimeSpan sinceLaunch, TimeSpan? screenUpAt) =>
        sinceLaunch >= _backstop
        || (screenUpAt is { } up && sinceLaunch - up >= _patience && sinceLaunch >= _floor);

    /// <summary>
    /// One turn of the thread, and the whole rule. <paramref name="gapMs"/> is how long that turn took to
    /// arrive. <paramref name="sinceLaunch"/> is the time since launch. <paramref name="screenUpAt"/> is
    /// when the first screen got something on it, or null if it has not yet. <paramref name="dressed"/>
    /// says whether its pictures have landed. Returns how to reveal, or null when the splash stays up.
    /// </summary>
    /// <remarks>
    /// <para>The counter is reset by a late turn, not decremented by it. A thread that is held for 200 ms
    /// and then free for two turns has not gone quiet. It has only paused briefly.</para>
    ///
    /// <para><b>The counter is also reset while the first screen is not up yet.</b> This follows the same
    /// rule. Turns counted while the first screen is still being read say nothing about the thread. That
    /// read runs on a worker, so the thread is idle for all of it. A run of calm gathered then would be
    /// used up the moment the rows arrive, which is when the thread lays out five rows, a banner and forty
    /// covers. Counting only turns with the screen up means the run measures the thread AFTER the work the
    /// reveal has to animate against.</para>
    ///
    /// <para><b>The pictures are waited for as well as the shape.</b> This is the difference between an
    /// app that appears, and one that appears and then fills in. <paramref name="dressed"/> is the
    /// second fact the screen reports. It is a preference, not a requirement: the patience sets the
    /// longest wait, so a cover coming out of the cloud delays the reveal by at most that long and
    /// never holds it.</para>
    ///
    /// <para><b>A calm thread does not open the gate early.</b> The fade runs on the compositor, so it
    /// does not need a free thread. If calm opened the gate, the app could fade up onto blank squares
    /// before the covers had landed. Calm decides only which way is reported, which is the Skia head's
    /// question. See <see cref="RevealWay"/>.</para>
    /// </remarks>
    internal RevealWay? Turn(double gapMs, TimeSpan sinceLaunch, TimeSpan? screenUpAt, bool dressed)
    {
        _quiet = screenUpAt is null || gapMs > HeldMs ? 0 : _quiet + 1;

        // Shape AND pictures, past the floor. Calm does not decide WHETHER there is a reveal. The fade runs
        // on the compositor and does not need a free thread. Calm only decides which way is reported, which
        // is the Skia head's question now.
        if (screenUpAt is not null && dressed && sinceLaunch >= _floor)
            return _quiet >= QuietTicks ? RevealWay.Quiet : RevealWay.Ceiling;

        return Overdue(sinceLaunch, screenUpAt) ? RevealWay.Ceiling : null;
    }

    /// <summary>Calls <paramref name="reveal"/> once, on the drawing thread. This happens at the first
    /// turn that is past <paramref name="floor"/> from launch, has the first screen up, and is quiet.
    /// It also happens when <paramref name="patience"/> has passed since the screen came up, or
    /// <paramref name="backstop"/> since launch, whichever comes first.
    /// </summary>
    /// <param name="screen">When the first screen got something on it, and whether its pictures have
    /// landed since. Asked on every turn rather than subscribed to, so the arrivals cannot be ordered
    /// wrongly.</param>
    /// <param name="floor">Nothing is revealed before this much has passed since launch.</param>
    /// <param name="patience">How long the gate waits for the screen's pictures AFTER it has its shape.
    /// Measured from the screen and not from launch, because that is when the user starts waiting for a
    /// window they could already be using.</param>
    /// <param name="backstop">The screen never reported at all: reveal anyway once this much has passed
    /// since launch. This is longer than any working startup takes. See the class remarks.</param>
    /// <param name="reveal">Told HOW the gate opened, because the two ways need different reveals. See
    /// <see cref="RevealWay"/>.</param>
    public static void Open(
        Services.Shell.UiPulse pulse,
        Services.Shell.FirstScreen screen,
        TimeSpan floor,
        TimeSpan patience,
        TimeSpan backstop,
        Action<RevealWay> reveal)
    {
        ArgumentNullException.ThrowIfNull(pulse);
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(reveal);

        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null)
        {
            // No dispatcher means there is no thread to wait for. Show the app now, without a fade,
            // because a fade needs a dispatcher to drive it.
            reveal(RevealWay.Ceiling);
            return;
        }

        var gate = new RevealGate(floor, patience, backstop);
        var once = 0;

        // Both halves subscribe, and whichever fires first opens the gate. Landed runs on the drawing
        // thread and opens the gate on quiet. StillOut runs on the watch thread and opens it when a
        // bound runs out while the thread is still busy. The watch thread is needed because the drawing
        // thread cannot run the deadline.
        //
        // The deadline must not depend on the drawing thread. If it did, a startup where that thread never
        // goes calm would never open the gate. In one run the bound was six seconds and the splash came off
        // at 12.1 seconds, because the gate could not get a turn to notice its own deadline. A promise
        // measured by the thing it is a promise against is not a promise.
        void Landed(Services.Shell.Turn turn)
        {
            if (gate.Turn(turn.LateMs, App.Uptime, screen.UpAt, screen.IsDressed) is { } way)
                Fire(way);
        }

        void StillOut(double outMs)
        {
            // The thread is held and the deadline has passed. Whatever is on screen is shown at once,
            // because an animation started on a thread that is not answering would play as a slideshow.
            // The reveal is queued rather than called, because this is not the drawing thread and the
            // reveal touches the UI tree.
            //
            // This uses the same rule the turns are judged by, asked from the other side. This half
            // cannot reach the quiet answer, because a thread that is not answering is not calm. So
            // Overdue is all it can decide.
            if (!gate.Overdue(App.Uptime, screen.UpAt))
                return;

            queue.TryEnqueue(() => Fire(RevealWay.Ceiling));
        }

        void Fire(RevealWay way)
        {
            // Only once. Both halves can reach here, because the thread can free up in the same moment
            // a bound passes. A second reveal would fade an overlay that is already gone.
            if (Interlocked.Exchange(ref once, 1) != 0)
                return;

            pulse.Landed -= Landed;
            pulse.StillOut -= StillOut;

            // This records WHICH bound it was, not just that one was hit. A reveal because the screen
            // never reported is a different fault from a reveal where the screen was up and the thread
            // would not go calm. The boot log is where this is read back.
            var why = screen.UpAt is not { } up
                ? "BACKSTOP, no screen at all"
                : screen.IsDressed
                    ? FormattableString.Invariant(
                        $"dressed, {(App.Uptime - up).TotalMilliseconds:F0}ms after its shape, thread {(way == RevealWay.Quiet ? "calm" : "busy")}")
                    : FormattableString.Invariant(
                        $"PATIENCE, still undressed {(App.Uptime - up).TotalMilliseconds:F0}ms after its shape");

            RPM.Core.Diagnostics.Scope.Said("boot", "gate opened: " + why);
            reveal(way);
        }

        pulse.Landed += Landed;
        pulse.StillOut += StillOut;
    }
}
