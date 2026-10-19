namespace RPM.Desktop.Services.Art;

/// <summary>
/// One picture decoded at a time, in the order it was asked for, each inside a share of the frame.
///
/// <para><b>Why it exists.</b> A decode can only run on the thread that draws. A page that realises all of
/// its contents at once asks for all of them together. <c>async</c> does not hold them back. A dozen
/// continuations at normal priority run one after another inside a single frame, and the layout that was
/// due in that frame is delayed. On a genre page with six shelves, every card was realised before the
/// first one was on screen. The drawing thread was then blocked for 200 milliseconds during the fade that
/// brings the page in, and the transition stalled halfway.</para>
///
/// <para><b>What it does instead</b> is what the walls do, in the smallest form. The decodes go through one
/// gate, in the order they were asked for. Each one books what it spent against <see cref="FrameBudget"/>.
/// So the pictures take their share of a frame and give the rest back. The order matters as much as the
/// limit. Cards realise from the top down, so the shelf the user is looking at is decoded before the shelves
/// below the fold.</para>
///
/// <para>The FETCH is not in here, and it must not be. Reading a cover runs on another thread, and it can
/// happen for every card at once. Only the decode at the end costs the drawing thread. If the fetch were
/// gated too, a page would fill in one card at a time over the network.</para>
///
/// <para>This type is not thread-safe, on purpose. Every caller is a realise callback, or the continuation
/// of one. Both run on the UI thread. A second thread using one of these would be two threads deciding how
/// much time the drawing thread may spend.</para>
/// </summary>
internal sealed class PacedPictures
{
    private readonly FrameBudget _budget = new();
    private readonly double _shareMs;

    /// <param name="shareMs">What these pictures may use of each frame. Give the value at the call site and
    /// do not use a default. The right value differs. A wall that fills in quietly behind a reader is worth
    /// less of a frame than a page the user is waiting for. This type cannot pick that for them.
    /// <see cref="FrameBudget.ShareMs"/> is the usual value.
    ///
    /// <para><b>A bigger share does not make the screen faster.</b> Giving the home page nearly the whole
    /// frame, while the splash screen covers the window, can look sensible. A share that protects a screen
    /// nobody can see seems to be spent on nothing. But the layout that builds the decoded picture needs
    /// the frame too. In one measurement, a share of about 14 ms of each 16 ms frame made every picture
    /// arrive after about 11 seconds. With the usual 10 ms share, it took about 4 seconds. The drawing
    /// thread was blocked for over a second in 230 of 396 checks. The decodes take the frame, and then they
    /// wait for a screen that cannot be laid out.</para></param>
    public PacedPictures(double shareMs) => _shareMs = shareMs;

    /// <summary>One clock for the pacing, owned by this gate. A budget is a share of a frame. Two pages
    /// sharing one clock would be two budgets measuring the same window.</summary>
    private readonly System.Diagnostics.Stopwatch _pace = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>What is in the gate, or a finished task where nothing is.</summary>
    private Task _queue = Task.CompletedTask;

    /// <summary>
    /// Runs one decode in its turn and holds the next caller off until the frame it spent has room again.
    /// </summary>
    /// <param name="decode">The work that costs the drawing thread. It is called when the gate reaches it,
    /// and never before. Anything read inside it is read at that moment, not at the time of the request.</param>
    /// <returns>What the decode returned. A decode that throws passes the exception to its own caller only.
    /// The gate decides when work runs, not what happens to it. One picture that cannot be read must not
    /// leave the rest of the page blank.</returns>
    public Task<T> Next<T>(Func<Task<T>> decode)
    {
        var mine = Queued(_queue);
        _queue = mine;
        return mine;

        async Task<T> Queued(Task after)
        {
            try
            {
                await after;
            }
            catch
            {
                // Whatever the one in front did with itself, this one still goes.
            }

            var at = _pace.Elapsed.TotalMilliseconds;
            try
            {
                return await decode();
            }
            finally
            {
                // Booked whether the decode returned or threw. A decode that failed halfway still used the
                // thread, so the frame is not given back.
                var wait = _budget.Spend(at, _pace.Elapsed.TotalMilliseconds - at, _shareMs);
                if (wait > 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(wait));
            }
        }
    }
}
