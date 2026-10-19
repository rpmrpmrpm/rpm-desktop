using RPM.Desktop.Presentation.Main.LeftPane;
using RPM.Desktop.Presentation.Main.RightPane;

namespace RPM.Desktop.Presentation.Main;

/// <summary>The library window's search fields, one for each screen that filters something.</summary>
internal enum SearchField
{
    /// <summary>The collection's, over the wall, the list and the table.</summary>
    Albums,

    /// <summary>The artists wall's.</summary>
    Artists,

    /// <summary>An artist's page, over the artist's songs.</summary>
    ArtistTracks,

    /// <summary>The import workbench's, in the side pane.</summary>
    Import,

    /// <summary>The internet radio's, in the side pane.</summary>
    Radio,
}

/// <summary>
/// Which search field Cmd or Ctrl+F puts the keyboard in on the library window.
///
/// <para>Read off what is on screen, which <see cref="PaneStage"/> already decides: the library card's
/// view and the side pane's. The side pane's field is taken first only while the keyboard is in the side
/// pane, so a person working in the import workbench searches the import; otherwise the card's, which is
/// the main thing on screen. Null when neither has one on screen (home, an album page, the genres, the
/// playlists, the track info): the caller then asks for the collection, as the Albums tab does, and its
/// field.</para>
///
/// <para>Pure, so it can be unit tested without a window.</para>
/// </summary>
internal static class SearchTarget
{
    public static SearchField? OnScreen(
        PaneStage stage, bool sideShown, bool albumPageUp, bool artistPageUp, bool keyboardInSide)
    {
        SearchField? card = stage.RightShowing switch
        {
            RightTab.Albums when !albumPageUp => SearchField.Albums,
            RightTab.Artists => artistPageUp ? SearchField.ArtistTracks : SearchField.Artists,
            _ => null,
        };

        SearchField? side = sideShown && stage.LeftViewUp
            ? stage.Tab switch
            {
                LeftTab.Import => SearchField.Import,
                LeftTab.Radio => SearchField.Radio,
                _ => null,
            }
            : null;

        return keyboardInSide ? side ?? card : card ?? side;
    }
}
