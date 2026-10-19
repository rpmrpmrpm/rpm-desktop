using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Diagnostics;
using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Services.Diagnostics;

/// <summary>
/// The two tracks that a stall is read against: when the drawing thread was unavailable, and when the
/// user did something.
///
/// <para>Without these, the ring holds gate holds and throws with nothing to place them against. That is
/// a list of facts, not a picture. A hold of 200 ms is unremarkable while nobody is looking at the
/// screen. In the middle of a scroll it is the whole problem. Only the input track shows which of those
/// it was.</para>
///
/// <para><b>The measurement is not taken here.</b> It belongs to <see cref="UiPulse"/>, and this class
/// writes down what that measurement reports. A third consumer, the governor, also needs the reading, to
/// decide whether the interface can afford a background pass. Each watcher on the drawing thread adds
/// load to it. On this app a diagnostic has already made the app unusable, so there must be only one
/// measurement of that thread.</para>
///
/// <para><b>Measured from a thread that is not stalled.</b> A thread that is busy or parked cannot report
/// how long it was busy or parked, because reporting is more work for a thread that is not doing any work.
/// See <see cref="UiPulse"/> for how this is done.</para>
///
/// <para><b>The processor reading is taken on the drawing thread</b>, at the moment the item lands,
/// because it is the only thread that can be asked. This number splits a stall in two. A gap of 800 ms
/// with 780 ms of processor time is work. A gap of 800 ms with 4 ms of processor time is a thread that was
/// not running at all. These need opposite fixes, but they look the same from outside.</para>
/// </summary>
internal static class ScopeShell
{
    /// <summary>
    /// How late an item has to be before it is a stall worth a record.
    ///
    /// <para>100 ms is six frames. It is long enough that normal work does not trip it, and short enough
    /// that anything a user can feel does. It is the same bar that <c>Gate</c> uses, so the two tracks can
    /// be compared directly.</para>
    /// </summary>
    private static readonly int OverMs =
        int.TryParse(RPM.Core.Diagnostics.DevSwitch.Read("RPM_SCOPE_STALL_MS"), out var asked) && asked > 0
            ? asked
            : 100;

    private static bool _installed;
    private static long _lastCpu = ScopeClock.Unknown;

    /// <summary>
    /// Starts both tracks. Called once, after the window has content.
    /// </summary>
    /// <param name="pulse">The one measurement of the drawing thread. The caller has already started it.
    /// This method only listens, so that a run with the ring switched off still governs itself.</param>
    public static void Install(Window window, UiPulse pulse)
    {
        if (!Scope.On || _installed)
            return;

        _installed = true;
        WatchInput(window);

        pulse.Landed += Landed;

        // Reported while the outage is still happening, not only after it ends. During a long freeze no
        // outage has ended yet, so waiting would report nothing.
        pulse.StillOut += outMs => Scope.Say(ScopeKind.DispatchDepth, null, a: (long)(outMs * 1_000_000));
    }

    /// <summary>
    /// Runs on the drawing thread. Says how long the turn took to arrive, and what the thread burned
    /// while it was not answering.
    /// </summary>
    private static void Landed(Turn turn)
    {
        // The first landing has nothing to subtract from, so it has no processor reading. Reporting zero
        // there would be wrong. Zero means "the thread was not running", which is the reading that decides
        // which fix to try. The first landing is also the app's own startup, so the longest outage of the
        // run would wrongly be attributed to a parked thread. An unknown value is reported as unknown (a
        // negative number), and the reader shows it that way. This also applies on a platform with no
        // per-thread CPU counter. See ScopeClock.ThreadCpuNanos.
        var burned = _lastCpu < 0 || turn.CpuNanos < 0 ? ScopeClock.Unknown : turn.CpuNanos - _lastCpu;
        _lastCpu = turn.CpuNanos;

        // The animation census is added to the end of this record and nowhere else. A frame that used no
        // processor time still needs an explanation, and "which animations were asking for frames" is that
        // explanation. It is read only when the record is written, so a run under the threshold takes no
        // lock at all.
        if (turn.LateMs >= OverMs)
            Scope.Say(
                ScopeKind.FrameOverrun, "ui", a: (long)(turn.LateMs * 1_000_000), b: burned,
                detail: FrameWatch.Describe());
    }

    /// <summary>
    /// Every press, key and wheel, whether or not something else has already handled it.
    /// </summary>
    /// <remarks>
    /// <c>handledEventsToo</c> is required. By the time an event reaches this point it is usually already
    /// handled. A track that recorded only unhandled events would miss the presses that did nothing. The
    /// question is when the user acted, not how the UI tree handled it.
    /// </remarks>
    private static void WatchInput(Window window)
    {
        if (window.Content is not UIElement root)
            return;

        // Press, release and tap each name the element under the pointer. This tells apart a click that did
        // nothing: a press with no tap after it means a gesture was swallowed, and a tap with nothing after
        // it means a handler dropped it.
        root.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler((_, e) => Scope.Input("press", Where(e.OriginalSource, e.Handled))),
            handledEventsToo: true);

        root.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler((_, e) => Scope.Input("release", Where(e.OriginalSource, e.Handled))),
            handledEventsToo: true);

        root.AddHandler(
            UIElement.TappedEvent,
            new TappedEventHandler((_, e) => Scope.Input("tap", Where(e.OriginalSource, e.Handled))),
            handledEventsToo: true);

        root.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler((_, _) => Scope.Input("wheel")),
            handledEventsToo: true);

        root.AddHandler(
            UIElement.KeyDownEvent,
            new KeyEventHandler((_, e) => Scope.Input(e.Key.ToString())),
            handledEventsToo: true);

        // The key-up is recorded too. A key-down with no matching key-up means a modifier is stuck. For
        // example, Cmd-Tab sends the Cmd key-down to this app and the key-up to the next app. See HeldKeys.
        root.AddHandler(
            UIElement.KeyUpEvent,
            new KeyEventHandler((_, e) => Scope.Input(e.Key.ToString(), "up")),
            handledEventsToo: true);
    }

    /// <summary>The element an input landed on, as its type and the first few named elements above it, plus
    /// whether the event was already handled by the time it reached the root. Names only, no coordinates,
    /// so the name table grows with the number of distinct places and not with clicks.</summary>
    private static string Where(object? source, bool handled)
    {
        var parts = new List<string>(5);
        if (source is not null) parts.Add(source.GetType().Name);

        var node = source as DependencyObject;
        for (int depth = 0; node is not null && depth < 40 && parts.Count < 5; depth++)
        {
            if (node is FrameworkElement { Name: { Length: > 0 } name }) parts.Add(name);
            node = VisualTreeHelper.GetParent(node);
        }

        return string.Join(" < ", parts) + (handled ? " (handled)" : "");
    }
}
