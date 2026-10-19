namespace RPM.Desktop.Presentation.Main.RightPane;

/// <summary>The four ways the library pane shows the collection, in rail order. The rail names them.
/// The conductor decides which one is showing. Only one of the pane's views is shown at a time, the
/// same way the left pane's rail works.</summary>
internal enum RightTab
{
    /// <summary>The collection as records: the wall, the album list and the data table, chosen between
    /// by the library header's own switch.</summary>
    Albums,
    Artists,
    Genres,
    Playlists,
}
