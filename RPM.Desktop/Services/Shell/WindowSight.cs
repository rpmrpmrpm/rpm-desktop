using Microsoft.UI.Windowing;

namespace RPM.Desktop.Services.Shell;

/// <summary>
/// Whether the main window is minimised. This is for the drawing that exists only to be looked at: the GL
/// stages (Pegasus and the visualizers, see WinGlCanvas) and the card rooms on home (CardRoomBeat). Audio
/// and background work do not ask.
///
/// <para>A minimised window must not keep drawing. Before this existed, the app drew 60 frames a second in
/// the background while Pegasus was up, and used about two cores.</para>
///
/// <para>The state is read again on every window change, rather than taken from the flags of one event.
/// <c>AppWindow.Changed</c> and <c>Window.VisibilityChanged</c> can each arrive without the other, and
/// either may come first. The window counts as minimised when its overlapped presenter says so, or when it
/// reports itself as not visible. Pegasus runs under the full-screen presenter, which has no state to
/// ask.</para>
///
/// <para>UI thread only. <see cref="Changed"/> is raised on the UI thread when the answer changes.</para>
/// </summary>
public static class WindowSight
{
    private static Window? _window;

    /// <summary>Whether the main window is minimised. False before <see cref="Watch"/> is called.</summary>
    public static bool Minimised { get; private set; }

    /// <summary>Raised when <see cref="Minimised"/> changes. A listener reads the property.</summary>
    public static event Action? Changed;

    /// <summary>Starts following the main window. Called once, when the window exists.</summary>
    public static void Watch(Window window)
    {
        if (_window is not null)
            return;

        _window = window;
        window.AppWindow.Changed += (_, _) => Reread();
        window.VisibilityChanged += (_, _) => Reread();
        Reread();
    }

    private static void Reread()
    {
        if (_window is not { } window)
            return;

        bool minimised;
        try
        {
            minimised = !window.Visible
                || Platform.WindowPresenter.Overlapped(window.AppWindow)?.State == OverlappedPresenterState.Minimized;
        }
        catch
        {
            return;   // The window is being torn down. The last answer stands.
        }

        if (minimised == Minimised)
            return;

        Minimised = minimised;
        RPM.Core.Diagnostics.Scope.Said("window", minimised ? "minimised: drawing paused" : "restored: drawing resumed");
        Changed?.Invoke();
    }
}
