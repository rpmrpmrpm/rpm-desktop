using Microsoft.UI.Xaml.Media.Animation;

namespace RPM.Desktop.Presentation.Main.LeftPane;

/// <summary>
/// The left pane's function rail: the four views it can show, and the button that folds it away.
/// It holds no state of its own. It reports which button was pressed and paints the view that
/// <see cref="SetActive"/> marks as current, so the pane itself decides which view is up.
/// </summary>
internal sealed partial class LeftRail : UserControl
{
    public LeftRail() => InitializeComponent();

    /// <summary>A rail button was pressed: show that view.</summary>
    public event Action<LeftTab>? TabRequested;

    /// <summary>The foot button was pressed: fold the view away, or bring it back.</summary>
    public event Action? CloseRequested;

    /// <summary>The Import button, for the first-run hint to point at. The hint is about where music
    /// comes in. It is exposed by that purpose, not as a control, so the rail alone decides which
    /// button that is.</summary>
    public FrameworkElement ImportTabTarget => RailImport;

    /// <summary>
    /// Turns the fold button to show what it does now: left while there is a view to fold, right
    /// once the view is folded and pressing the button brings it back.
    ///
    /// <para>The button turns instead of changing to a different chevron. A change would look like a
    /// new button appearing in the same place.</para>
    /// </summary>
    public void SetFolded(bool folded)
    {
        if (_folded == folded)
            return;
        _folded = folded;

        IconName.Set(FoldButton, Loc.Get(folded ? "Playlists_Reopen" : "Playlists_Close"));

        _spin?.Stop();

        var turn = new DoubleAnimation
        {
            From = FoldSpin.Angle,
            To = folded ? 180 : 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(turn, FoldSpin);
        Storyboard.SetTargetProperty(turn, "Angle");

        _spin = new Storyboard();
        _spin.Children.Add(turn);
        _spin.Begin();
    }

    private bool _folded;
    private Storyboard? _spin;

    /// <summary>Paints the rail for the view that is shown now. <paramref name="importActive"/> is
    /// passed separately because Import is also lit while a queue is in progress, not only when it is
    /// the shown view.
    ///
    /// <para>One of the buttons is always lit, folded or not. When the pane is folded, the lit button
    /// is the view that the fold button brings back.</para></summary>
    public void SetActive(LeftTab tab, bool importActive)
    {
        SelectedSegment.Paint(RailTrackInfo, tab == LeftTab.TrackInfo);
        SelectedSegment.Paint(RailImport, importActive);
        SelectedSegment.Paint(RailViz, tab == LeftTab.Visualization);
        SelectedSegment.Paint(RailRadio, tab == LeftTab.Radio);
    }

    private void RailTrackInfo_Click(object sender, RoutedEventArgs e) => TabRequested?.Invoke(LeftTab.TrackInfo);
    private void RailViz_Click(object sender, RoutedEventArgs e) => TabRequested?.Invoke(LeftTab.Visualization);
    private void RailRadio_Click(object sender, RoutedEventArgs e) => TabRequested?.Invoke(LeftTab.Radio);
    private void RailImport_Click(object sender, RoutedEventArgs e) => TabRequested?.Invoke(LeftTab.Import);
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
