using RPM.Desktop.Presentation.Main;
using RPM.Desktop.Presentation.Main.LeftPane;
using RPM.Desktop.Presentation.Main.RightPane;

namespace RPM.Desktop.Tests;

/// <summary>
/// Which search field Cmd or Ctrl+F puts the keyboard in on the library window, read off the pane state
/// with no window.
/// </summary>
public class SearchTargetTests
{
    private static PaneStage Stage(
        RightTab reading, bool atHome = false, bool rightOpen = true, bool sideOpen = false, LeftTab tab = LeftTab.TrackInfo)
        => new(atHome, sideOpen, tab, rightOpen, reading);

    [Fact]
    public void The_collection_searches_the_collection()
        => Assert.Equal(SearchField.Albums, SearchTarget.OnScreen(Stage(RightTab.Albums), true, false, false, false));

    [Fact]
    public void An_album_page_has_no_field_of_its_own()
        => Assert.Null(SearchTarget.OnScreen(Stage(RightTab.Albums), true, albumPageUp: true, false, false));

    [Fact]
    public void The_artists_wall_and_an_artists_page_have_their_own()
    {
        Assert.Equal(SearchField.Artists, SearchTarget.OnScreen(Stage(RightTab.Artists), true, false, false, false));
        Assert.Equal(SearchField.ArtistTracks, SearchTarget.OnScreen(Stage(RightTab.Artists), true, false, artistPageUp: true, false));
    }

    [Theory]
    [InlineData(RightTab.Genres)]
    [InlineData(RightTab.Playlists)]
    public void Genres_and_playlists_have_none(object reading)
        => Assert.Null(SearchTarget.OnScreen(Stage((RightTab)reading), true, false, false, false));

    /// <summary>Home covers the library card, so the collection's field under it is not on
    /// screen.</summary>
    [Fact]
    public void Home_covers_the_cards_field()
        => Assert.Null(SearchTarget.OnScreen(Stage(RightTab.Albums, atHome: true), true, false, false, false));

    /// <summary>With the radio beside home, the radio's field is the one on screen.</summary>
    [Fact]
    public void The_side_panes_field_when_the_card_has_none()
        => Assert.Equal(SearchField.Radio, SearchTarget.OnScreen(
            Stage(RightTab.Albums, atHome: true, sideOpen: true, tab: LeftTab.Radio), true, false, false, false));

    /// <summary>Both on screen: the card's, unless the keyboard is in the side pane.</summary>
    [Fact]
    public void The_card_first_unless_the_keyboard_is_in_the_side_pane()
    {
        var both = Stage(RightTab.Albums, sideOpen: true, tab: LeftTab.Import);

        Assert.Equal(SearchField.Albums, SearchTarget.OnScreen(both, true, false, false, keyboardInSide: false));
        Assert.Equal(SearchField.Import, SearchTarget.OnScreen(both, true, false, false, keyboardInSide: true));
    }

    /// <summary>The library folded for an import leaves the workbench's field.</summary>
    [Fact]
    public void A_folded_library_leaves_the_import()
        => Assert.Equal(SearchField.Import, SearchTarget.OnScreen(
            Stage(RightTab.Albums, rightOpen: false, sideOpen: true, tab: LeftTab.Import), true, false, false, false));

    /// <summary>A side pane taken off the screen with the deck's pill, or folded by its chevron, has no
    /// field on screen whatever its tab says.</summary>
    [Fact]
    public void A_hidden_or_folded_side_pane_has_none()
    {
        Assert.Null(SearchTarget.OnScreen(Stage(RightTab.Genres, sideOpen: true, tab: LeftTab.Radio), sideShown: false, false, false, false));
        Assert.Null(SearchTarget.OnScreen(Stage(RightTab.Genres, sideOpen: false, tab: LeftTab.Radio), sideShown: true, false, false, false));
    }

    [Theory]
    [InlineData(LeftTab.TrackInfo)]
    [InlineData(LeftTab.Visualization)]
    public void Side_panes_without_a_field(object tab)
        => Assert.Null(SearchTarget.OnScreen(Stage(RightTab.Genres, sideOpen: true, tab: (LeftTab)tab), true, false, false, true));
}
