namespace RPM.Desktop.Services.Shell;

/// <summary>
/// Who is asking the window to redraw.
///
/// <para>A frame that scrolled nothing, prepared no rows, allocated nothing and collected nothing still
/// took 45 milliseconds. It also painted two or three times. The grid had no part in this. Something
/// else was subscribed to <c>CompositionTarget.Rendering</c>. A subscriber does not just receive frames.
/// It demands them. The compositor keeps producing frames for as long as anyone is listening. On a
/// renderer without dirty rectangles, each frame repaints the whole window.</para>
///
/// <para>So each animation says when it starts and when it stops, and the names are added to the end of
/// the frame-overrun records that <c>ScopeShell</c> writes. A long frame with nothing in it becomes a
/// list of names. What is NOT measured here is what each handler spends, on purpose. The cost under
/// suspicion is the repaint that a subscription forces, not the arithmetic inside the tick. An animation
/// that spends almost nothing can still be the expensive kind.</para>
/// </summary>
internal static class FrameWatch
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, int> Live = [];

    /// <summary>Says an animation has started asking for frames.</summary>
    /// <param name="name">What to call it on a stall line. Keep it short, because it shares the
    /// line.</param>
    public static void Began(string name)
    {
        lock (Gate)
        {
            Live[name] = Live.TryGetValue(name, out var count) ? count + 1 : 1;
        }
    }

    /// <summary>Says it has stopped.</summary>
    /// <param name="name">The name it started under.</param>
    public static void Ended(string name)
    {
        lock (Gate)
        {
            if (Live.TryGetValue(name, out var count) && count > 0)
            {
                Live[name] = count - 1;
            }
        }
    }

    /// <summary>What is demanding frames right now.</summary>
    public static string Describe()
    {
        lock (Gate)
        {
            var running = Live.Where(e => e.Value > 0)
                              .Select(e => e.Value > 1 ? $"{e.Key}x{e.Value}" : e.Key)
                              .ToList();

            return running.Count == 0 ? "animating=none" : "animating=" + string.Join(",", running);
        }
    }
}
