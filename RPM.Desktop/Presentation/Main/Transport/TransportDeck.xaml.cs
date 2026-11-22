using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Main.Transport;

/// <summary>
/// The deck: the bar along the foot of the main screen. Home and the search are on the left edge. The transport
/// floats in the exact centre. The state chrome sits on the right: the rip, the cloud, the cast pill and the menu.
///
/// <para>The deck drives everything through the one shared player model (<see cref="AttachTransport"/>). The
/// buttons are Commands on that model, so the bar plays whatever the player is playing. The deck decides no view
/// and opens no search itself. It reports the press, and the page does the rest.</para>
/// </summary>
internal sealed partial class TransportDeck : UserControl
{
    public TransportDeck()
    {
        InitializeComponent();

        // The picker only reports the choice, and the deck passes it on. As with the home and search keys and the
        // hamburger, nothing here decides anything about the app.
        ViewPickerContent.Chosen += view =>
        {
            ViewFlyout.Hide();
            ViewRequested?.Invoke(view);
        };
    }

    /// <summary>The search key was pressed: the library search over the window, as Cmd or Ctrl+F asks for
    /// it.</summary>
    public event Action? SearchRequested;

    /// <summary>The home key was pressed. Carries nothing: there is one home and the page that shows
    /// it knows where it goes.</summary>
    public event Action? HomeRequested;

    /// <summary>The hamburger was clicked, carrying itself so the menu can open at the button.</summary>
    public event Action<FrameworkElement>? MenuRequested;

    /// <summary>The menu button. The first-run hint about signing in points at it. The shell's own edge hamburger is
    /// collapsed on the library page, and this is the menu people use there. A hint aimed at the hamburger would
    /// point at nothing.</summary>
    public FrameworkElement MenuTarget => MenuPill;

    // ── the view ─────────────────────────────────────────────────────────────

    /// <summary>A line of the view menu was pressed. The deck reports the choice, then paints whatever
    /// <see cref="SetView"/> reports. Which of the three views is up is not the deck's decision. It is the same
    /// as for the home key and home.</summary>
    public event Action<AppView>? ViewRequested;

    private AppView _view = AppView.Library;

    /// <summary>The view now on screen, so the picker can light the card the app is already on.</summary>
    public void SetView(AppView view) => _view = view;

    /// <summary>Called when the picker opens, rather than kept in step with every change. The picker is closed
    /// most of the time, and this is the only place the answer is shown.</summary>
    private void ViewFlyout_Opening(object? sender, object e)
    {
        ViewPickerContent.SetCurrent(_view);

        // Opening it is the answer to the hint that points at the eye button.
        Coachmarks.CoachmarkState.Seen(RPM.Core.Library.CoachMark.AppViews);
    }

    /// <summary>Laid out now, so the hint about what each view is can be placed beside it.</summary>
    private void ViewFlyout_Opened(object? sender, object e) => ShellPage.Current?.RefreshCoachmarks();

    /// <summary>Closing the picker is the answer to the hint beside it, once the hint before it is.</summary>
    private void ViewFlyout_Closed(object? sender, object e)
    {
        HoldViewPicker(false);
        if (!Coachmarks.CoachmarkState.Remaining().Contains(RPM.Core.Library.CoachMark.AppViews))
            Coachmarks.CoachmarkState.Seen(RPM.Core.Library.CoachMark.AppViewsOpen);
        ShellPage.Current?.RefreshCoachmarks();
    }

    /// <summary>Closes the views picker, for the hint beside it.</summary>
    public void CloseViewPicker() => ViewFlyout.Hide();

    /// <summary>Holds the picker's cards still while the hint beside it explains them: the picker is a
    /// popup over the tour's sheet, so the sheet cannot stop a press on it. Let go when it closes.</summary>
    public void HoldViewPicker(bool hold) => ViewPickerContent.IsHitTestVisible = !hold;

    /// <summary>The eye button. The first-run hint about rpm's views points at it.</summary>
    public FrameworkElement ViewTarget => ViewButton;

    /// <summary>The picker the eye button opens, while it is open, and null otherwise.</summary>
    public FrameworkElement? ViewPickerTarget => ViewFlyout.IsOpen ? ViewPickerContent : null;

    // ── the player ────────────────────────────────────────────────────────────

    private RPaMpModel? _transport;

    /// <summary>Gives the deck the one shared player model. The transport bar uses it as its DataContext, which binds
    /// every key's Command. The cast pill is given the model separately, because its siblings inherit the page's
    /// own model.</summary>
    public void AttachTransport(RPaMpModel transport)
    {
        if (_transport is { } old)
            old.PropertyChanged -= Transport_PropertyChanged;

        _transport = transport;
        TransportBar.DataContext = transport;
        DeckCastButton.DataContext = transport;
        transport.PropertyChanged += Transport_PropertyChanged;
        SyncTransportPulse();
    }

    /// <summary>While the DJ hands over, the next key pulses and does nothing. The player's own keys do the same,
    /// since both drive the one model.</summary>
    private void Transport_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RPaMpModel.IsTransportBusy))
            SyncTransportPulse();
    }

    private void SyncTransportPulse() =>
        Pulse.Set(TransportNext, _transport?.IsTransportBusy == true);

    /// <summary>The speaker mark is the mute: down to nothing, and back to the level it was left at.</summary>
    private void VolumeMark_Click(object sender, RoutedEventArgs e)
        => _transport?.ToggleMute();

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
            MenuRequested?.Invoke(fe);
    }

    // Re-scan for speakers each time the deck's cast picker opens (same as the popout's).
    private void DeckCast_Opening(object? sender, object e) => DeckCastContent.Refresh();

    // The queue card reads the count and finds the playing row each time it opens.
    private void QueueFlyout_Opening(object? sender, object e) => QueueCardContent.Refresh();

    private void QueueFlyout_Closed(object? sender, object e) => QueueCardContent.Closed();

    // ── home and the search ───────────────────────────────────────────────────

    /// <summary>The reader asked for home. This is a request, not an action. The deck does not know what is on the
    /// right-hand pane, and it cannot put that pane away. See MainPage, which owns the pane and every surface in
    /// it. It works like the search key beside it and the view menu.</summary>
    private void HomePill_Click(object sender, RoutedEventArgs e) => HomeRequested?.Invoke();

    /// <summary>The reader asked for the search. A request too: the bar it opens belongs to the shell.</summary>
    private void SearchPill_Click(object sender, RoutedEventArgs e) => SearchRequested?.Invoke();

    private static readonly SolidColorBrush HomeGlyphIdle =
        new(Windows.UI.Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));

    /// <summary>Lights the home key while the home screen is showing. The key uses the same mark style as the tabs on
    /// the rails. Home is a view like the four on either rail, and this key is its only tab. It is painted here,
    /// not through <see cref="Presentation.Common.SelectedSegment.Paint"/>, because the glyph has its own colour
    /// and would not take the button's.</summary>
    public void SetHomeActive(bool active)
    {
        HomePill.Background = Presentation.Common.SelectedSegment.Wash(active);
        HomeGlyph.Foreground = active
            ? Presentation.Common.SelectedSegment.Ink(true)
            : HomeGlyphIdle;   // the deck's own resting ink, not the rails': nothing else here dims
    }
}
