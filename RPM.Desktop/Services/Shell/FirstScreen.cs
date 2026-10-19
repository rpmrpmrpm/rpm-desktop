namespace RPM.Desktop.Services.Shell;

/// <summary>Whether the first screen has anything on it yet.
/// <para><b>Why this is not the same question as a free drawing thread.</b>
/// <see cref="Presentation.Shell.RevealGate"/> opens on two facts: enough time since launch, and a run
/// of turns that the drawing thread answered on time. Both can be true of a window with nothing in it.
/// Home reads the shelf on a worker (<c>Cancellable.RunAsync</c>, about 1.2 seconds on a 13,000-track
/// collection). The drawing thread is idle for that whole read. The gate counts that idle time as a
/// free drawing thread, so it releases the splash too early. The home screen's content is held at zero
/// opacity until its rows are filled, so the user sees an empty wall for the rest of the read.</para>
/// <para><b>Asked, not told.</b> It is a latch that only goes from false to true. The gate reads it on
/// each turn of the pulse rather than having it pushed. That makes the two arrivals order-independent.
/// A screen that comes up before the gate is armed is seen by the gate's first turn. One that comes up
/// after that is seen by the turn that follows. There is no event to miss and no gap to fall
/// into.</para>
/// <para><b>Every landing sets it, including the ones that land on nothing.</b> A first run has no
/// library and lands on setup. A read that stops early leaves the rows empty. In both cases the screen
/// is as ready as it will get, and the gate should not wait for its ceiling. That would trade a few
/// seconds of empty wall for six seconds of splash. See <c>HomeView.ShowScreen</c>, which is called on
/// every way out of the reading, and <c>ShellPage.OpenLibraryAndLand</c>.</para>
/// </summary>
public sealed class FirstScreen
{
    /// <summary>Uptime when the screen came up, in ticks, or 0 while it has not. One field rather than a
    /// flag and a time, because a reader that caught the instant between two writes could otherwise see
    /// a screen that is up at no particular moment.</summary>
    private long _upAtTicks;

    /// <summary>
    /// When the first screen got something on it, measured as <see cref="App.Uptime"/> at that moment,
    /// or null while it still has nothing.
    /// </summary>
    /// <remarks>
    /// <b>The moment, not just the fact.</b> The reveal's patience is counted from this moment, not from
    /// launch. On a cold start the shell can load about 9.5 seconds after launch. A six-second bound
    /// counted from launch would already have run out before there was a window to fill. The splash would
    /// then come off an empty window. A time limit on waiting must start when the waiting starts.
    ///
    /// <para>Readable from any thread at any moment, which is what lets the gate ask rather than
    /// subscribe.</para>
    /// </remarks>
    public TimeSpan? UpAt =>
        Volatile.Read(ref _upAtTicks) is var ticks && ticks != 0 ? TimeSpan.FromTicks(ticks) : null;

    /// <summary>True once the first screen has its content, and for the rest of the run.</summary>
    public bool IsUp => Volatile.Read(ref _upAtTicks) != 0;

    private long _dressedAtTicks;

    /// <summary>
    /// When the first screen's pictures had all landed, or null while any is still coming.
    /// </summary>
    /// <remarks>
    /// <b>A screen with a shape is not a finished screen.</b> <see cref="UpAt"/> means that the rows,
    /// headings and banner have a shape. The pictures on them arrive afterwards, one per card, and each
    /// one fades in as it lands. Revealing between the two gives an app that appears and then fills in.
    /// That is the problem that <see cref="UpAt"/> also addresses, one step further along.
    ///
    /// <para>This is a preference, not a requirement. The reveal waits for it only up to its time limit.
    /// A cover from a slow disk or from the cloud must not hold up the whole app. The picture fills follow
    /// the same rule: one picture may be held up, but not the screen.</para>
    /// </remarks>
    public TimeSpan? DressedAt =>
        Volatile.Read(ref _dressedAtTicks) is var ticks && ticks != 0 ? TimeSpan.FromTicks(ticks) : null;

    /// <summary>True once every picture the first reading asked for has landed or given up.</summary>
    public bool IsDressed => Volatile.Read(ref _dressedAtTicks) != 0;

    /// <summary>
    /// Said by the first screen when the last of its pictures has landed.
    /// </summary>
    /// <remarks>
    /// Like <see cref="Up"/>, only the first one counts: the screen re-reads on a shelf change and asks
    /// for its pictures again, and that is not a fact about the startup.
    /// </remarks>
    public void Dressed()
    {
        var now = App.Uptime.Ticks;
        if (Interlocked.CompareExchange(ref _dressedAtTicks, now == 0 ? 1 : now, 0) != 0)
            return;

        var up = UpAt;
        RPM.Core.Diagnostics.Scope.Said(
            "boot",
            up is { } shape
                ? FormattableString.Invariant(
                    $"first screen dressed, {(App.Uptime - shape).TotalMilliseconds:F0}ms after its shape")
                : "first screen dressed");
    }

    /// <summary>
    /// Said by whatever landed, once it has something to show.
    /// </summary>
    /// <remarks>
    /// Idempotent and callable from any thread. Home says it on every way out of its reading, so this is
    /// reached several times in a run where a second reading follows the first; only the first one is a
    /// fact about the startup, and only the first one is kept or logged.
    /// </remarks>
    public void Up()
    {
        // Ticks first, and a compare-exchange rather than a write: the moment kept is the moment of the
        // FIRST landing. A later reading overwriting it would move the patience window forward under a
        // gate that is already counting against it.
        var now = App.Uptime.Ticks;
        if (Interlocked.CompareExchange(ref _upAtTicks, now == 0 ? 1 : now, 0) == 0)
            RPM.Core.Diagnostics.Scope.Said("boot", "first screen up");
    }
}
