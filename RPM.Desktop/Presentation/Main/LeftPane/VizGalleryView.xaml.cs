namespace RPM.Desktop.Presentation.Main.LeftPane;

/// <summary>
/// The left pane's Visualizations view: a wall of cards, one for each visualizer in the catalogue.
/// The cards are stills, so filling the list does not start a GL context. The list is filled the
/// first time the view is shown.
/// Launching is not this view's job: it reports the id and the shell runs the visualizer over the
/// whole window.
/// </summary>
internal sealed partial class VizGalleryView : UserControl
{
    public VizGalleryView() => InitializeComponent();

    /// <summary>A card's launch button was pressed, carrying that visualizer's id.</summary>
    public event Action<string>? LaunchRequested;

    /// <summary>Fills the gallery with the catalogue, once.</summary>
    public void EnsureFilled()
    {
        if (VizList.ItemsSource is null)
            VizList.ItemsSource = VizCatalog.All;
    }

    private void VizLaunch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id })
            LaunchRequested?.Invoke(id);
    }
}
