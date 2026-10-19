namespace RPM.Core.Splitting;

/// <summary>What part of the recording the waveform is showing: a start time and how many seconds
/// fit across the control.</summary>
public readonly record struct WaveView(double Start, double Span)
{
    public double End => Start + Span;

    public bool Contains(double seconds) => seconds >= Start && seconds <= End;
}

/// <summary>
/// The waveform editor's viewport arithmetic: zoom, pan, and where a second sits on screen.
///
/// <para>It is kept out of the control. This is the part that can be wrong in ways a screenshot will
/// not show. Each rule here is a promise that the editor makes. A zoom keeps the point under the
/// pointer in place. Panning cannot go past either end. The view never gets so tight that a drag of
/// one pixel moves a cut past the note it was aimed at.</para>
/// </summary>
public static class WaveTimeline
{
    /// <summary>Tightest zoom: half a second across the window. At a typical width that is about one
    /// millisecond per pixel. This is finer than any cut needs, and finer than the 5 ms peak buckets
    /// can show.</summary>
    public const double MinSpanSeconds = 0.5;

    /// <summary>One click of the zoom buttons (and one notch of the wheel).</summary>
    public const double ZoomStep = 1.6;

    public static WaveView Fit(double totalSeconds) =>
        new(0, Math.Max(MinSpanSeconds, totalSeconds));

    /// <summary>Holds a view inside the recording: never wider than the whole recording, never tighter
    /// than <see cref="MinSpanSeconds"/>, and never starting before 0 or ending after the end.</summary>
    public static WaveView Clamp(WaveView view, double totalSeconds)
    {
        double total = Math.Max(MinSpanSeconds, totalSeconds);
        double span = Math.Clamp(view.Span, Math.Min(MinSpanSeconds, total), total);
        double start = Math.Clamp(view.Start, 0, Math.Max(0, total - span));
        return new WaveView(start, span);
    }

    /// <summary>
    /// Zooms about a fixed point. <paramref name="anchorSeconds"/> stays at the same fraction across
    /// the control. So the wheel zooms into what the pointer is over, and the buttons zoom into the
    /// middle of what is on screen. <paramref name="factor"/> above 1 zooms in.
    /// </summary>
    public static WaveView ZoomAt(WaveView view, double anchorSeconds, double factor, double totalSeconds)
    {
        if (factor <= 0)
            return Clamp(view, totalSeconds);

        double fraction = view.Span > 0
            ? Math.Clamp((anchorSeconds - view.Start) / view.Span, 0, 1)
            : 0.5;
        double span = Math.Clamp(view.Span / factor, MinSpanSeconds, Math.Max(MinSpanSeconds, totalSeconds));
        return Clamp(new WaveView(anchorSeconds - fraction * span, span), totalSeconds);
    }

    public static WaveView Pan(WaveView view, double deltaSeconds, double totalSeconds) =>
        Clamp(view with { Start = view.Start + deltaSeconds }, totalSeconds);

    public static WaveView CenterOn(WaveView view, double seconds, double totalSeconds) =>
        Clamp(view with { Start = seconds - view.Span / 2 }, totalSeconds);

    /// <summary>The view that frames <c>[from, to]</c> with a little space on either side. "Zoom to
    /// selection" and "zoom to this track" both use this.</summary>
    public static WaveView Around(double from, double to, double totalSeconds, double padFraction = 0.06)
    {
        if (to < from)
            (from, to) = (to, from);
        double span = Math.Max(MinSpanSeconds, (to - from) * (1 + 2 * padFraction));
        double start = from - (to - from) * padFraction;
        return Clamp(new WaveView(start, span), totalSeconds);
    }

    /// <summary>
    /// Keeps a moving playhead on screen without the picture moving while the user reads it. While the
    /// playhead is inside the middle of the view, nothing moves. When it reaches the edge, the view
    /// jumps on by a page. Returns the view unchanged when there is nothing to do.
    /// </summary>
    public static WaveView Follow(WaveView view, double seconds, double totalSeconds, double edgeFraction = 0.12)
    {
        if (view.Span <= 0)
            return view;

        double margin = view.Span * Math.Clamp(edgeFraction, 0, 0.4);
        if (seconds >= view.Start + margin && seconds <= view.End - margin)
            return view;
        if (seconds < view.Start || seconds > view.End)
            return CenterOn(view, seconds, totalSeconds);   // A seek outside the view: centre on it.
        return Clamp(view with { Start = seconds - margin }, totalSeconds);
    }

    public static double TimeToX(WaveView view, double seconds, double width) =>
        view.Span <= 0 ? 0 : (seconds - view.Start) / view.Span * width;

    public static double XToTime(WaveView view, double x, double width) =>
        width <= 0 ? view.Start : view.Start + x / width * view.Span;

    /// <summary>Seconds per pixel at this zoom. This is the resolution that a drag can place a cut to.
    /// The pixel tolerances (grab radius, snap radius) are converted with this value.</summary>
    public static double SecondsPerPixel(WaveView view, double width) =>
        width <= 0 ? view.Span : view.Span / width;

    private static readonly double[] TickCandidates =
        [0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800];

    /// <summary>A ruler step that reads as a round number and leaves at least
    /// <paramref name="minPixels"/> between labels.</summary>
    public static double TickSeconds(WaveView view, double width, double minPixels = 74)
    {
        if (width <= 0 || view.Span <= 0)
            return 1;
        double perPixel = view.Span / width;
        foreach (var candidate in TickCandidates)
            if (candidate / perPixel >= minPixels)
                return candidate;
        return TickCandidates[^1];
    }
}
