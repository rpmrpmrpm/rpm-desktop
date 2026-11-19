using RPM.Desktop.Presentation.Main;
using RPM.Desktop.Presentation.Main.LeftPane;
using RPM.Desktop.Presentation.Main.RightPane;

namespace RPM.Desktop.Tests;

/// <summary>
/// Where Cmd or Ctrl+F puts the keyboard on the library window, read off the pane state with no window. Null
/// is the library search over the whole window.
/// </summary>
public class SearchTargetTests
{
    private static PaneStage Stage(
        RightTab reading, bool atHome = false, bool sideOpen = false, LeftTab tab = LeftTab.TrackInfo)
        => new(atHome, sideOpen, tab, reading);

    /// <summary>Every view of the library, and home, opens the search over the window.</summary>
    [Theory]
    [InlineData(RightTab.Albums, false)]
    [InlineData(RightTab.Artists, false)]
    [InlineData(RightTab.Genres, false)]
    [InlineData(RightTab.Playlists, false)]
    [InlineData(RightTab.Albums, true)]
    public void The_library_opens_the_search_over_the_window(object reading, bool atHome)
        => Assert.Null(SearchTarget.OnScreen(Stage((RightTab)reading, atHome), keyboardInSide: false));

    /// <summary>The import workbench and the radio keep their own field while the keyboard is in them.</summary>
    [Theory]
    [InlineData(LeftTab.Import, SearchField.Import)]
    [InlineData(LeftTab.Radio, SearchField.Radio)]
    public void The_side_panes_field_while_the_keyboard_is_in_the_side_pane(object tab, object field)
        => Assert.Equal((SearchField)field, SearchTarget.OnScreen(
            Stage(RightTab.Albums, sideOpen: true, tab: (LeftTab)tab), keyboardInSide: true));

    /// <summary>The same pane on screen with the keyboard elsewhere: the search over the window.</summary>
    [Theory]
    [InlineData(LeftTab.Import)]
    [InlineData(LeftTab.Radio)]
    public void The_side_pane_on_screen_without_the_keyboard_opens_the_search_over_the_window(object tab)
        => Assert.Null(SearchTarget.OnScreen(
            Stage(RightTab.Albums, sideOpen: true, tab: (LeftTab)tab), keyboardInSide: false));

    /// <summary>A side pane folded by its chevron has no field on screen whatever its tab says.</summary>
    [Fact]
    public void A_folded_side_pane_has_none()
        => Assert.Null(SearchTarget.OnScreen(Stage(RightTab.Genres, sideOpen: false, tab: LeftTab.Radio), keyboardInSide: true));

    [Theory]
    [InlineData(LeftTab.TrackInfo)]
    [InlineData(LeftTab.Visualization)]
    public void Side_panes_without_a_field(object tab)
        => Assert.Null(SearchTarget.OnScreen(Stage(RightTab.Genres, sideOpen: true, tab: (LeftTab)tab), keyboardInSide: true));
}
