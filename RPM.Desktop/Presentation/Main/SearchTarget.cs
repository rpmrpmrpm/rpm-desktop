using RPM.Desktop.Presentation.Main.LeftPane;

namespace RPM.Desktop.Presentation.Main;

/// <summary>The side pane's search fields, each of which searches only what its pane shows.</summary>
internal enum SearchField
{
    /// <summary>The import workbench's.</summary>
    Import,

    /// <summary>The internet radio's.</summary>
    Radio,
}

/// <summary>
/// Where Cmd or Ctrl+F puts the keyboard on the library window.
///
/// <para>The side pane's own field while the keyboard is in the side pane: a person working in the import
/// workbench or the radio searches what they are working in. Null everywhere else, and the caller opens the
/// library search over the whole window (<c>Search.SearchOverlay</c>). The pane's state is read off
/// <see cref="PaneStage"/>, which already decides what is on screen.</para>
///
/// <para>Pure, so it can be unit tested without a window.</para>
/// </summary>
internal static class SearchTarget
{
    public static SearchField? OnScreen(PaneStage stage, bool keyboardInSide)
    {
        if (!keyboardInSide || !stage.LeftViewUp)
            return null;

        return stage.Tab switch
        {
            LeftTab.Import => SearchField.Import,
            LeftTab.Radio => SearchField.Radio,
            _ => null,
        };
    }
}
