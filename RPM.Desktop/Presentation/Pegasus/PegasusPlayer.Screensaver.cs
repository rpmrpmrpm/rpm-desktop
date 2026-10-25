using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using RPM.Core.Viz;
using VirtualKey = Windows.System.VirtualKey;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// The screensaver, as on the TV. When nothing has been pressed or moved for the minutes the settings name
/// (IPegasusSettings.ScreensaverMinutes: Off, 1, 2, 5, 10, 15 or 30; 2 until changed), the screens over the
/// room fade out and only the room is left, moving in the animation the listener chose. The next key brings
/// the screens back as they were, and does nothing else, down to its release. The media keys and the
/// shortcuts (the next track, the volume) still do their own work as they bring the screens back: a pause
/// pressed over the room pauses. A pointer move brings them back too.
///
/// <para><b>Not over the player.</b> The record turning is what the screensaver would show anyway, and
/// someone watching the record or the lyric is not idle. So the time is only watched while the collection is
/// up (<c>_library</c>): home, the wall, radio and the other screens. Not over the greeting, which is the
/// only thing meant to be on the screen that night, and not while the black cover is up. Leaving the player
/// is a key, which starts the wait again.</para>
///
/// <para>What fades is the collection and everything drawn with it (ChromeHost: the screens, the mini player
/// and the side menu), the focus glow under them, and a message along the foot. The room is under all of
/// them, and the collection's shade over the room is in the chrome, so it goes too. A menu in a popup is
/// drawn above this view and would stay over the room, so the screens do not go while one is open: the wait
/// starts again. While the screens are away they take no pointer, so a click on the room presses nothing
/// that cannot be seen.</para>
///
/// <para>The rule itself (the time from the last key, which press is swallowed) is
/// <see cref="Screensaver"/>, in RPM.Core, where it is tested.</para>
/// </summary>
public sealed partial class PegasusPlayer
{
    /// <summary>How long the screens take to fade out, and to come back on a key or a move: out slowly, back
    /// quickly, as the key that wakes them asks. The TV's figures.</summary>
    private const int ScreensaverOutMs = 1500;
    private const int ScreensaverInMs = 400;

    private readonly Screensaver _saver = new(Environment.TickCount64);

    /// <summary>One-shot: wakes when the time could be up, and asks the rule again.</summary>
    private DispatcherQueueTimer? _saverTimer;

    /// <summary>The screens' fade. One number for the chrome and the focus glow, which go together.</summary>
    private ChromeFade? _saverFade;

    private ChromeFade SaverFade => _saverFade ??= new ChromeFade(v =>
    {
        if (ChromeHost.Opacity != v)
            ChromeHost.Opacity = v;
        if (FocusLayer.Opacity != v)
            FocusLayer.Opacity = v;
    }, 1);

    /// <summary>Whether the time is watched now: the view is up, finished, and showing the collection.</summary>
    private bool ScreensaverWatching => IsShowing && _library && !_greeting && !_loadingUp;

    /// <summary>
    /// A key went down while the view is up (a repeat too), handed over by the shell before it routes the
    /// key (see ShellPage.OnPreviewKeyDown). The time starts again. Returns true when the press brought the
    /// screens back, or is a repeat of the press that did: it goes no further, unless it is a key that does
    /// its own work through the screensaver, which the shell decides.
    /// </summary>
    public bool ScreensaverKeyDown(VirtualKey key)
    {
        var away = _saver.Shown;
        var swallowed = _saver.KeyDown((int)key, Environment.TickCount64);
        if (away)
        {
            Say("screensaver: a key brought the screens back");
            BringScreensBack();
        }
        return swallowed;
    }

    /// <summary>A key came up. Returns true for the release of the press that brought the screens back,
    /// which goes no further either: a held OK released on a line would press it.</summary>
    public bool ScreensaverKeyUp(VirtualKey key) => _saver.KeyUp((int)key);

    /// <summary>The pointer was used (a real move, a press or the wheel). The time starts again, and screens
    /// that were away come back.</summary>
    private void ScreensaverUsed()
    {
        if (_saver.Used(Environment.TickCount64))
        {
            Say("screensaver: the pointer brought the screens back");
            BringScreensBack();
        }
    }

    /// <summary>The wheel counts as use of the pointer. It is listened for on the root with the events the
    /// screens have handled too, because a scroller under the pointer takes every turn of it.</summary>
    private void HookScreensaver()
        => Root.AddHandler(PointerWheelChangedEvent, new PointerEventHandler((_, _) => ScreensaverUsed()), true);

    /// <summary>
    /// Sets the timer for when the time could be up, or stops it where the screensaver has nothing to
    /// watch. Called whenever what it watches changes: the view coming up or the cover lifting, the
    /// collection coming up or going, the greeting, and the choice in the settings. Screens that are away
    /// when the watch ends (the player came in front without a key) come back.
    /// </summary>
    private void ArmScreensaver()
    {
        if (!ScreensaverWatching)
        {
            _saverTimer?.Stop();
            if (_saver.Shown)
            {
                _saver.Reset(Environment.TickCount64);
                BringScreensBack();
            }
            return;
        }

        if (_saver.Shown)
            return;   // the next key or move brings them back
        if (_saver.DueIn(ScreensaverMinutes, Environment.TickCount64) is not { } due)
        {
            _saverTimer?.Stop();
            return;
        }

        if (_saverTimer is null)
        {
            if (DispatcherQueue is null)
                return;
            _saverTimer = DispatcherQueue.CreateTimer();
            _saverTimer.IsRepeating = false;
            _saverTimer.Tick += (_, _) => ScreensaverTimeUp();
        }

        _saverTimer.Stop();
        _saverTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(due, 250));
        _saverTimer.Start();
    }

    /// <summary>The timer woke. The rule is asked again, because a key or a move since it was set has moved
    /// the time on; the screens go only when it is really up.</summary>
    private void ScreensaverTimeUp()
    {
        if (!ScreensaverWatching || _saver.Shown)
            return;
        if (_saver.DueIn(ScreensaverMinutes, Environment.TickCount64) is not { } due)
            return;
        if (due > 0)
        {
            ArmScreensaver();
            return;
        }

        // A menu open in a popup is drawn above this view, and would stay alone over the room. Someone has it
        // open to read it, so the wait starts again.
        if (LineMenuOpen || FocusInPopup)
        {
            _saver.Used(Environment.TickCount64);
            ArmScreensaver();
            return;
        }

        PutScreensAway();
    }

    /// <summary>The screens fade out, and only the room is left.</summary>
    private void PutScreensAway()
    {
        _saver.PutAway();
        Say("screensaver: the screens are put away");

        // A room stopped under the pages' background starts again before the screens fade, so it is moving as it is
        // uncovered (see ApplyRoomCovered).
        _saverBack++;
        ApplyRoomCovered();

        ChromeHost.IsHitTestVisible = false;
        SaverFade.To(0, ScreensaverOutMs);
        HideFlash();
        PegasusChrome.CloseOpenToolTips(XamlRoot);

        // The pointer goes with them, as it does when the keys stand down.
        FullScreenWindow.SetCursorHidden(true);
    }

    /// <summary>The screens come back as they were. The focus never left the control it was on, so the remote
    /// goes on from there.</summary>
    private void BringScreensBack()
    {
        ChromeHost.IsHitTestVisible = true;
        SaverFade.To(1, ScreensaverInMs);
        ArmScreensaver();

        // Once the screens are fully back, a room under the pages' background stops again. Not before: it shows
        // through while they fade in. A later put-away cancels this.
        var back = ++_saverBack;
        DispatcherQueue?.TryEnqueue(async () =>
        {
            await Task.Delay(ScreensaverInMs + 40);
            if (back == _saverBack && !_saver.Shown)
                ApplyRoomCovered();
        });
    }

    /// <summary>Counts put-aways and returns, so a return's late room stop is dropped when the screens have gone
    /// again since.</summary>
    private int _saverBack;

    /// <summary>The view is going away: the screens are put back at once and the watch stops, so the next
    /// visit opens with them up and the full wait ahead.</summary>
    private void StopScreensaver()
    {
        _saverTimer?.Stop();
        _saver.Reset(Environment.TickCount64);
        SaverFade.To(1, 0);
        ChromeHost.IsHitTestVisible = true;
    }
}
