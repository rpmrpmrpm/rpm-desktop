using RPM.Desktop.Presentation.Pegasus;
using Microsoft.UI.Dispatching;

namespace RPM.Desktop.Services.Shell;

/// <summary>The three ways RPM shows itself. Exactly one is on screen at a time.</summary>
public enum AppView
{
    /// <summary>The library: the collection, the left pane and the deck along the foot. This is where
    /// everything that is not listening happens, and where closing either of the other two views returns
    /// to.</summary>
    Library,

    /// <summary>RPaMp: the skin, on its own, over the whole window. In this view the player is the whole
    /// app. This is the only view in which the skin's own size makes sense.</summary>
    RPaMp,

    /// <summary>Pegasus: the record turning full screen, on a background of its own colour.</summary>
    FullScreen,
}

/// <summary>
/// Which of the three views is up, and the one place that changes it.
///
/// <para>This is held in a service, not in any of the three views. A view cannot put itself away and bring
/// another one up without knowing about the others. If each surface holds its own opinion about the other
/// two, the library can end up hidden behind a full screen, which is behind a player window. Every surface
/// only says "I have been closed" (<see cref="Close"/>) or "show me" (<see cref="Switch"/>). This class
/// decides what those messages mean.</para>
///
/// <para>Closing anything returns to <see cref="AppView.Library"/>. This is the rule, not a fallback. The
/// other two views are ways of listening, and the library is the app. So leaving one of them means going
/// back, not going nowhere.</para>
/// </summary>
public sealed class ViewModeService
{
    private readonly DispatcherQueue _dispatcher;
    private readonly Lazy<RPaMpService> _amp;

    /// <summary>Where the full-screen player is found. This is a callback, not a reference. The shell owns
    /// the control and is built long after this service is. A service that reached into the visual tree
    /// when it was constructed would find nothing there.</summary>
    private Func<PegasusPlayer?>? _pegasus;
    private bool _hooked;

    public ViewModeService(DispatcherQueue dispatcher, IServiceProvider services)
    {
        _dispatcher = dispatcher;
        // Lazy: resolving the player service here would build the model, and its two idle timers, for an
        // app that may never play anything.
        _amp = new Lazy<RPaMpService>(services.GetRequiredService<RPaMpService>);
    }

    /// <summary>Which view is on screen.</summary>
    public AppView Current { get; private set; } = AppView.Library;

    /// <summary>Raised after a change, on the UI thread, so the deck can tick the right line of its
    /// menu.</summary>
    public event Action<AppView>? Changed;

    /// <summary>The shell hands over its full-screen player once the tree is up.</summary>
    public void AttachPegasus(Func<PegasusPlayer?> player) => _pegasus = player;

    /// <summary>Whether there is a full-screen player to show yet. It is false between the app starting and
    /// the shell building its tree. This matters for anything that opens this view without the user
    /// pressing a key. A user cannot press anything in that moment, but a clock can still fire (see <see
    /// cref="NewYearHerald"/>).</summary>
    public bool CanShowFullScreen => _pegasus?.Invoke() is not null;

    /// <summary>Moves to a view. Being on the view already does nothing. Picking the item that is already
    /// selected must not tear the screen down and build it again.</summary>
    public void Switch(AppView view) => _dispatcher.TryEnqueue(() =>
    {
        if (Current == view)
            return;

        var from = Current;
        Current = view;

        // Down first, then up. Two views over each other for one frame reads as a flash. The view that is
        // going away is the one that knows how to leave tidily. Pegasus gives the window its chrome back.
        if (from == AppView.FullScreen)
            _pegasus?.Invoke()?.Hide();
        if (from == AppView.RPaMp)
            ShellPage.Current?.ShowAmp(false);

        switch (view)
        {
            case AppView.FullScreen:
                Show(null);
                break;

            case AppView.RPaMp:
                ShellPage.Current?.ShowAmp(true);
                break;
        }

        Changed?.Invoke(view);
    });

    /// <summary>
    /// Opens Pegasus on a named composition. This is the gallery shoot's way in, and the only caller that
    /// chooses which composition it opens on.
    /// </summary>
    public void ShowFullScreen(PegasusView? openOn) => _dispatcher.TryEnqueue(() =>
    {
        if (Current == AppView.RPaMp)
            ShellPage.Current?.ShowAmp(false);
        Current = AppView.FullScreen;
        Show(openOn);
        Changed?.Invoke(Current);
    });

    /// <summary>
    /// Opens Pegasus on the New Year greeting. This is the herald's way in, and the only caller that opens
    /// this view without the user asking for it (see <see cref="NewYearHerald"/>).
    ///
    /// <para>It has its own method rather than a flag on <see cref="ShowFullScreen"/>. What makes it
    /// different is not the composition it opens on. It opens on something that is not a composition at
    /// all, and it puts the listener's own view back afterwards. <paramref name="record"/> is the file that
    /// the greeting belongs to, so the greeting can take itself down when that record is over.</para>
    /// </summary>
    public void ShowNewYear(string record) => _dispatcher.TryEnqueue(() =>
    {
        if (Current == AppView.RPaMp)
            ShellPage.Current?.ShowAmp(false);
        Current = AppView.FullScreen;
        _pegasus?.Invoke()?.ShowNewYear(record);
        Changed?.Invoke(Current);
    });

    /// <summary>A view reported that the user closed it. Go back to the library. See the class remarks.
    /// This is ignored when that view is not the one on screen. A close that arrives from a view that was
    /// already put away is a stale message. Acting on it would pull the app out of the view it has moved
    /// to.</summary>
    public void Close(AppView view)
    {
        if (Current == view)
            Switch(AppView.Library);
    }

    /// <summary>Subscribes the two closable views to <see cref="Close"/>. This happens once. The shell
    /// calls it as it builds them. A second call is ignored, so that a re-navigation cannot fire the
    /// handlers twice.</summary>
    public void HookCloseHandlers(PegasusPlayer pegasus)
    {
        if (_hooked)
            return;
        _hooked = true;
        pegasus.Closed += () => Close(AppView.FullScreen);
        _amp.Value.PutAway += () => Close(AppView.RPaMp);
    }

    private void Show(PegasusView? openOn)
    {
        if (_pegasus?.Invoke() is { } player)
            player.Show(openOn);
    }
}
