using Microsoft.UI.Xaml.Media.Animation;

namespace RPM.Desktop.Presentation.Main.RightPane;

/// <summary>
/// The library pane's function rail: the four ways of reading the collection. Like
/// <see cref="LeftPane.LeftRail"/>, it holds no state of its own. It reports what was clicked and paints
/// whatever <see cref="SetActive"/> says is current. The pane's conductor decides which view is up.
/// </summary>
internal sealed partial class RightRail : UserControl
{
    public RightRail() => InitializeComponent();

    /// <summary>
    /// How tall one tab on this rail stands.
    ///
    /// <para>The pane header's own height: 12 of margin, the 36-high title box and the 23 for the reserved
    /// subtitle strip. There is one tab per header, so the first tab's foot lands on the line where every
    /// pane starts its content.</para>
    ///
    /// <para>The style in RightRail.xaml uses the same value, because a XAML style setter cannot read a
    /// constant. A test checks that the two match. The genre page's header is exactly two tabs tall (see
    /// <c>GenreDetailView.BandHeight</c>). A header that is nearly two tabs tall would have a line that
    /// nearly, but not exactly, lines up with the tabs.</para>
    /// </summary>
    public const double OneTab = 71;

    /// <summary>A rail button was pressed. Show that view.</summary>
    public event Action<RightTab>? TabRequested;

    /// <summary>The height of the tab stack as drawn. The page reads it from here. Before the first layout
    /// it is the height of four tabs.</summary>
    public double TabHeight => TabStack.ActualHeight > 0 ? TabStack.ActualHeight : TabHeightFallback;

    private const double TabHeightFallback = 4 * OneTab;

    /// <summary>Paints the rail for the view that is currently up. A null tab lights nothing, which is the
    /// state the home screen shows. See <see cref="LeftPane.LeftRail.SetActive"/>.</summary>
    public void SetActive(RightTab? tab)
    {
        SelectedSegment.Paint(RailAlbums, tab == RightTab.Albums);
        SelectedSegment.Paint(RailArtists, tab == RightTab.Artists);
        SelectedSegment.Paint(RailGenres, tab == RightTab.Genres);
        SelectedSegment.Paint(RailPlaylists, tab == RightTab.Playlists);
    }

    /// <summary>
    /// Slides the tabs down clear of the spinning record, and back up when it has gone.
    ///
    /// <para>The corner moves with them. The top tab keeps the rail's rounded corner (radius 7) while it is
    /// in the corner, and loses it once it has moved out. A background cut to a corner the tab has left
    /// would show as a notch in the middle of the rail.</para>
    /// </summary>
    /// <param name="drop">How far down, in px. Zero puts the tabs back in the corner.</param>
    /// <param name="animate">False while the window is being resized. The drop then follows the pointer,
    /// and an animation would keep chasing it.</param>
    public void Duck(double drop, bool animate)
    {
        RailAlbums.CornerRadius = new CornerRadius(0, drop < 0.5 ? 7 : 0, 0, 0);

        if (!animate)
        {
            _duck?.Stop();
            _duck = null;
            TabDuck.Y = drop;
            return;
        }

        if (Math.Abs(TabDuck.Y - drop) < 0.5)
            return;

        _duck?.Stop();
        var slide = new DoubleAnimation
        {
            To = drop,
            Duration = new Duration(TimeSpan.FromSeconds(ArtDisplay.TransitionSeconds)),
            EnableDependentAnimation = true,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(slide, TabDuck);
        Storyboard.SetTargetProperty(slide, "Y");
        _duck = new Storyboard();
        _duck.Children.Add(slide);
        _duck.Begin();
    }

    private Storyboard? _duck;

    private void RailAlbums_Click(object sender, RoutedEventArgs e) => TabRequested?.Invoke(RightTab.Albums);
    private void RailArtists_Click(object sender, RoutedEventArgs e) => TabRequested?.Invoke(RightTab.Artists);
    private void RailGenres_Click(object sender, RoutedEventArgs e) => TabRequested?.Invoke(RightTab.Genres);
    private void RailPlaylists_Click(object sender, RoutedEventArgs e) => TabRequested?.Invoke(RightTab.Playlists);
}
