using RPM.Desktop.Presentation.Main;
using RPM.Desktop.Presentation.Main.LeftPane;
using RPM.Desktop.Presentation.Main.RightPane;

namespace RPM.Desktop.Tests;

/// <summary>
/// <see cref="PaneStage"/>: what the window shows, as against what the panes were asked for.
///
/// <para>The home screen is the one thing that makes the two differ. The fault this guards against is
/// silent. Home is a view in its own right and stands over the library card. A surface that reads the
/// tab instead of this leaves its view up behind home, and it then looks like a view that is merely not
/// on top. Leaving home is the other half. Home changes neither pane's tab on the way in, so leaving it
/// brings back the view that was up before, with nothing stored in between.</para>
///
/// <para>What home covers is the library card. The left pane stands beside it either way, which these
/// tests pin from both directions: it is not taken down on the way in, and it is not brought back out
/// on the way out.</para>
/// </summary>
public class PaneStageTests
{
    private static PaneStage Reading(RightTab tab, bool atHome) =>
        new(AtHome: atHome, Chevron: true, Tab: LeftTab.TrackInfo, RightOpen: true, Reading: tab);

    [Fact]
    public void HomeLightsTheLibraryRailForNothing()
    {
        var stage = Reading(RightTab.Artists, atHome: true);

        Assert.Null(stage.RightLit);
        Assert.True(stage.HomeLit);
    }

    /// <summary>Home covers the library card and nothing else. The left pane shows what its chevron says
    /// either way. Its rail names the view it is showing, or the view it is folded on. Opening the left
    /// flyout must keep home on screen, not show the library grid.</summary>
    [Fact]
    public void HomeLeavesTheLeftPaneAlone()
    {
        Assert.True(Reading(RightTab.Albums, atHome: false).LeftViewUp);
        Assert.True(Reading(RightTab.Albums, atHome: true).LeftViewUp);

        Assert.Equal(LeftTab.TrackInfo, Reading(RightTab.Albums, atHome: true).LeftLit);
    }

    [Fact]
    public void NoneOfTheCardsFourViewsStandsBehindHome()
    {
        foreach (var tab in Enum.GetValues<RightTab>())
            Assert.Null(Reading(tab, atHome: true).RightShowing);
    }

    /// <summary>Leaving home changes nothing else. Nothing about either pane was changed to show home, so
    /// clearing the flag alone brings the reader back to the view they left.</summary>
    [Fact]
    public void LeavingHomePutsBackTheViewThatWasUp()
    {
        var before = new PaneStage(
            AtHome: false, Chevron: true, Tab: LeftTab.Radio,
            RightOpen: true, Reading: RightTab.Playlists);

        var home = before with { AtHome = true };
        var after = home with { AtHome = false };

        Assert.Equal(before, after);
        Assert.Equal(RightTab.Playlists, after.RightShowing);
        Assert.Equal(LeftTab.Radio, after.LeftLit);
        Assert.True(after.LeftViewUp);
        Assert.False(after.HomeLit);
    }

    /// <summary>The chevron's own fold survives a visit to home. A reader who folded the left view away
    /// does not find it unfolded again after going home and back.</summary>
    [Fact]
    public void AFoldedLeftPaneComesBackFolded()
    {
        var folded = new PaneStage(
            AtHome: false, Chevron: false, Tab: LeftTab.Import,
            RightOpen: true, Reading: RightTab.Albums);

        Assert.False((folded with { AtHome = true }).LeftViewUp);
        Assert.False(folded.LeftViewUp);

        // And the other way round: opened at home, it is open coming out of home as well.
        var open = folded with { Chevron = true, AtHome = true };
        Assert.True(open.LeftViewUp);
        Assert.True((open with { AtHome = false }).LeftViewUp);
    }

    /// <summary>When the library is folded away for the import workbench, it shows nothing. Its rail still
    /// names the view it is folded on, so the reader knows what pressing it brings back. Home is different:
    /// it leaves that rail lit for nothing.</summary>
    [Fact]
    public void AFoldedLibraryRailStaysLitAndHomeDoesNot()
    {
        var folded = new PaneStage(
            AtHome: false, Chevron: true, Tab: LeftTab.Import,
            RightOpen: false, Reading: RightTab.Genres);

        Assert.Null(folded.RightShowing);
        Assert.Equal(RightTab.Genres, folded.RightLit);

        Assert.Null((folded with { AtHome = true }).RightLit);
    }
}
