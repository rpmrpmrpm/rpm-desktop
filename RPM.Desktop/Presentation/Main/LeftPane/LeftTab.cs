namespace RPM.Desktop.Presentation.Main.LeftPane;

/// <summary>The views the left pane can show, in rail order. The rail has one button for each view,
/// and the pane shows one view at a time.</summary>
internal enum LeftTab
{
    /// <summary>Everything known about the track that is playing: its file tags, the library's own
    /// record of it, and what the free encyclopedias add to both.</summary>
    TrackInfo,
    Import,
    Visualization,
    Radio,
}
