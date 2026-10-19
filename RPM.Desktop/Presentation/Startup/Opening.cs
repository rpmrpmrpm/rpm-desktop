using Microsoft.UI.Dispatching;
using RPM.Desktop.Presentation.Shell;
using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Presentation.Startup;

/// <summary>
/// The startup, as a stated sequence.
///
/// <para><b>The sequence.</b> There are three steps, and each one reports how far the startup has
/// got:</para>
///
/// <list type="number">
/// <item><b>The wordmark</b> (<see cref="Phase.Opening"/>). A black rectangle and the rpm mark arriving.
/// Nothing else may use the drawing thread, because the mark is all the user can see.</item>
/// <item><b>The library</b> (<see cref="Phase.Loading"/>). The collection is opened and the first screen
/// is built. Nothing is added to the splash while this runs: the wordmark on black is the wait.</item>
/// <item><b>The interface</b> (<see cref="Phase.Ready"/>). The splash comes off, on
/// <see cref="RevealGate"/>'s terms: the first screen has something on it AND the drawing thread has
/// answered on time several turns running, or one of the two time limits has run out. Only from here do
/// the background passes begin, in the order <see cref="Wave"/> states.</item>
/// </list>
///
/// <para><b>One call, three parts.</b> The shell hands over the steps and this class calls them, so the
/// order is written once here and does not depend on where the shell subscribes. A caller cannot run the
/// steps in a different order, cannot leave one out, and cannot start the library before the mark is
/// up.</para>
/// </summary>
internal static class Opening
{
    /// <summary>
    /// How long the wordmark has the thread to itself before the library is opened.
    ///
    /// <para>This delay exists to get the first frame drawn. The mark's entrance is a storyboard. If it
    /// started in the same turn that opens a database and builds a page, its first frame would be drawn
    /// only after all of that. The delay is long enough for one frame and short enough that nobody
    /// notices it.</para>
    /// </summary>
    private static readonly TimeSpan MarkFirst = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// The parts of the opening the shell owns.
    /// </summary>
    /// <param name="Wordmark">Plays the mark's entrance. Step one.</param>
    /// <param name="Library">Opens the collection and asks for the first screen. Step two, and the
    /// expensive one: it runs on the drawing thread and everything below waits for it. When it returns, the
    /// screen is built but not yet FILLED: home reads the shelf on a worker afterwards, and reports it
    /// through <see cref="FirstScreen"/>, which is what step three waits for.</param>
    /// <param name="Reveal">Takes the splash off. It is told how the gate opened. Step three.</param>
    internal readonly record struct Parts(
        Action Wordmark,
        Action Library,
        Action<RevealWay> Reveal);

    /// <summary>
    /// Runs the opening. Called once, from the shell's first <c>Loaded</c>, on the drawing thread.
    /// </summary>
    /// <param name="floor">Nothing is revealed before this much time has passed since launch. This
    /// stops a fast start from only flashing the wordmark.</param>
    /// <param name="patience">How long the reveal waits for a calm drawing thread after the first screen
    /// has something on it. After this the app opens even if the thread never goes quiet.</param>
    /// <param name="backstop">The same limit for a first screen that never reports at all. It is
    /// measured from launch because there is nothing else to measure it from.</param>
    public static void Begin(
        DispatcherQueue queue,
        Governor governor,
        UiPulse pulse,
        FirstScreen screen,
        TimeSpan floor,
        TimeSpan patience,
        TimeSpan backstop,
        Parts parts)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(governor);
        ArgumentNullException.ThrowIfNull(pulse);
        ArgumentNullException.ThrowIfNull(screen);

        // ── one: the wordmark ────────────────────────────────────────────────────────────────────
        governor.Reach(Phase.Opening);
        parts.Wordmark();

        // ── two: the library ─────────────────────────────────────────────────────────────────────
        Ticker.Once(queue, MarkFirst, () =>
        {
            governor.Reach(Phase.Loading);

            RPM.Core.Diagnostics.Scope.Said("boot", "library opening");
            parts.Library();
            RPM.Core.Diagnostics.Scope.Said("boot", "library open, first screen built");

            // ── three: the interface ─────────────────────────────────────────────────────────────
            //
            // The gate is armed here, inside step two and after it. This placement is what keeps the
            // order. The gate's floor is measured from launch, so on a slow start the gate can already
            // be past its ceiling when it is armed. If it were armed earlier, it would open on its
            // first turn and the splash would come off an empty window, with the library still
            // loading behind an interface the user can already see. The floor limits when the reveal
            // may happen. It does not say what has happened by then.
            //
            // Arming late costs nothing. Both time limits are read from App.Uptime, not from the
            // moment of arming, so a slow startup still reveals at once. The quiet turns that the
            // gate wants are measured on the thread after the work, not during it.
            //
            // parts.Library() returning means the page is BUILT, not that anything is on it. Home's
            // rows are read from the shelf on a worker, and its content stays at zero opacity until
            // they arrive. So the gate also asks FirstScreen on every turn whether something is on
            // the screen.
            RevealGate.Open(pulse, screen, floor, patience, backstop, way =>
            {
                // The interface is on screen. Everything that was waiting for that may now begin,
                // each in its own wave.
                governor.Reach(Phase.Ready);

                parts.Reveal(way);
            });
        });
    }
}
