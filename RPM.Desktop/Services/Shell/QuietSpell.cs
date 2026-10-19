namespace RPM.Desktop.Services.Shell;

/// <summary>What ended a wait on a <see cref="QuietSpell"/>.</summary>
public enum Woke
{
    /// <summary>Something moved, and then nothing did for the quiet span.</summary>
    Quiet,

    /// <summary>Somebody called <see cref="QuietSpell.AtOnce"/>, so the quiet span was not waited out.
    /// </summary>
    AtOnce
}

/// <summary>
/// Waits for something to happen, and then for it to stop happening.
///
/// <para>For work that should run after a run of changes, not during one. A scan, an upload run or a sweep
/// raises its change hundreds of times. A job that answers each change separately would race the thing it
/// is describing. A waiter asks for the next quiet moment instead, and gets one wake for the whole
/// run.</para>
///
/// <para><b>A token, not a queue.</b> The waiter asks "has anything moved since I last looked?" Ten changes
/// between two looks still mean one look. So <see cref="Nudge"/> raises at most one waiting token, however
/// often it is called, and it never blocks the thread that raised the change.</para>
///
/// <para>One waiter. Two tasks waiting on the same spell would each get some of the nudges, and neither
/// could reason about what it had seen.</para>
/// </summary>
/// <param name="quiet">How long nothing may move before the wait is over.</param>
public sealed partial class QuietSpell(TimeSpan quiet) : IDisposable
{
    private readonly SemaphoreSlim _nudged = new(0, 1);

    /// <summary>Whether the change that is pending must not wait. Set by <see cref="AtOnce"/> and taken by
    /// whichever wait wakes next (see <see cref="Asked"/>).</summary>
    private volatile bool _atOnce;

    /// <summary>Says that something moved. Called from whichever thread raised the change. Returns at
    /// once.</summary>
    public void Nudge()
    {
        if (_nudged.CurrentCount > 0)
            return;

        // Two threads can both read zero and both release. That lets the one token arrive twice. A lock is
        // not worth it here: the waiter wakes once either way, then waits out the quiet span before it does
        // anything.
        try { _nudged.Release(); }
        catch (SemaphoreFullException) { }
    }

    /// <summary>
    /// Says that something moved, and that the waiter must not wait out the quiet span for it.
    ///
    /// <para>For a change that somebody is watching, such as a press whose result is already on the screen.
    /// Waiting out a lull would leave the two screens disagreeing about a list for that long. Everything
    /// that a worker raises on its own is a <see cref="Nudge"/>.</para>
    ///
    /// <para>A waiter woken by the token must see the reason, so the reason is set before the token is
    /// released. Returns at once, like <see cref="Nudge"/>.</para>
    /// </summary>
    public void AtOnce()
    {
        _atOnce = true;
        Nudge();
    }

    /// <summary>Takes the pending ask, if there is one. It is read once per wake. The flag is the signal.
    /// Leaving it set would end the next wait early too, for a press that nobody made.</summary>
    private bool Asked()
    {
        if (!_atOnce)
            return false;

        _atOnce = false;
        return true;
    }

    /// <summary>
    /// Waits out a fixed span, or returns as soon as somebody calls <see cref="AtOnce"/>.
    ///
    /// <para>For the delay that a job takes before its first piece of work. An ordinary change is not a
    /// reason to start early. An explicit ask is. A change raised inside the span is consumed here, not
    /// left set, because the work after the span covers it.</para>
    /// </summary>
    public async Task<Woke> SettleAsync(TimeSpan span, CancellationToken ct)
    {
        var until = Environment.TickCount64 + (long)span.TotalMilliseconds;

        for (var left = span;
             left > TimeSpan.Zero;
             left = TimeSpan.FromMilliseconds(until - Environment.TickCount64))
        {
            if (!await _nudged.WaitAsync(left, ct))
                break;

            if (Asked())
                return Woke.AtOnce;
        }

        return Woke.Quiet;
    }

    /// <summary>Returns once something has moved and then nothing has for <c>quiet</c>. It waits forever on
    /// a spell that nobody nudges, which is the right answer for a library that nobody is
    /// touching.</summary>
    /// <returns>Why the wait ended: the span went quiet, or somebody asked for the work at once.
    /// </returns>
    public async Task<Woke> WaitAsync(CancellationToken ct)
    {
        await _nudged.WaitAsync(ct);

        if (Asked())
            return Woke.AtOnce;

        // While things keep moving, keep waiting.
        while (await _nudged.WaitAsync(quiet, ct))
        {
            if (Asked())
                return Woke.AtOnce;
        }

        return Woke.Quiet;
    }

    public void Dispose() => _nudged.Dispose();
}
