namespace RPM.Desktop.Services.Art;

/// <summary>
/// How much of each frame a background job may take on the thread that draws.
///
/// <para>The album wall's loader already gives the thread back between jobs, at the lowest priority.
/// That is not the same as waiting for a frame. The dispatcher drains its low priority queue many times
/// inside one frame. So three workers can put nine cover decodes into one 16 millisecond frame. One row
/// being realised was measured at 47 milliseconds of UI thread time. That dropped three frames for one
/// row. A scroll realises a new row every 160 points.</para>
///
/// <para>So the loader is paced against the clock, not against the queue. It spends what it is allowed in
/// each frame and then waits for the next one. The covers arrive a little later, and the wall keeps
/// scrolling smoothly while they arrive. That is the trade this class makes.</para>
/// </summary>
public sealed class FrameBudget
{
    /// <summary>The length of the window the budget is counted in. It is one frame at 60 frames per
    /// second.</summary>
    public const double FrameMs = 16;

    /// <summary>What the loader may use of each frame. That is about a third of it. This is enough for a
    /// screenful of covers to fill in quickly. It is little enough that the frame is still delivered on
    /// time.</summary>
    public const double ShareMs = 5;

    private double _windowOpenedAt;
    private double _spent;

    /// <summary>
    /// Records <paramref name="costMs"/> of work done at <paramref name="nowMs"/>. It returns how long the
    /// caller should wait before doing more work. The result is zero while the window has budget left.
    /// </summary>
    /// <param name="shareMs">What the loader may use of this frame. The default is <see cref="ShareMs"/>.
    /// A wall that is the whole screen, with empty frames that somebody is waiting for, is worth more of a
    /// frame than a wall that fills in quietly behind a reader.</param>
    public double Spend(double nowMs, double costMs, double shareMs = ShareMs)
    {
        // A window that has run out is replaced. What was spent in it belonged to a frame that has
        // already been drawn.
        if (nowMs - _windowOpenedAt >= FrameMs)
        {
            _windowOpenedAt = nowMs;
            _spent = 0;
        }

        _spent += costMs;
        if (_spent < shareMs)
            return 0;

        // Spent up. Wait out the rest of this window, and open the next one after it.
        var wait = FrameMs - (nowMs - _windowOpenedAt);
        _windowOpenedAt = nowMs + wait;
        _spent = 0;
        return wait > 0 ? wait : 0;
    }
}
