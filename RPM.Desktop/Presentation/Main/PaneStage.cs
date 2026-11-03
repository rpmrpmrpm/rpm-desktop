using RPM.Desktop.Presentation.Main.LeftPane;
using RPM.Desktop.Presentation.Main.RightPane;

namespace RPM.Desktop.Presentation.Main;

/// <summary>What the window is showing. It is worked out from what the two panes have been asked to show.
///
/// <para>These two are not always the same. Home is a screen that stands over the library card. While home
/// is up, the card's rail is lit for nothing. The tabs underneath do not change to show home, so leaving
/// home brings back the view that was up before. Nothing has to be saved and restored.</para>
///
/// <para><b>The left pane does not depend on home.</b> The left pane shows the view that its chevron and
/// its tab say, whether home is up or not. Home only goes down when something asks for the library card
/// that it covers.</para>
///
/// <para>Every surface that needs this state reads it from this record, not from the tab. That covers the
/// four views in either pane, the marks on both rails, the deck's home key and the window's backdrop. The
/// raw fields are private to the conductor. A view shown from <see cref="Reading"/> alone would stay
/// standing behind the home screen. That looks the same as a view that is simply not on top, so the error
/// is hard to see.</para>
/// </summary>
/// <param name="AtHome">Whether the home screen is the view up. It covers the library card and nothing
/// else, so only the right-hand members below read it.</param>
/// <param name="Chevron">What the left rail's fold button last said: whether the pane's view stands beside
/// the rail. Nothing overrides it. See <see cref="LeftViewUp"/>.</param>
internal readonly record struct PaneStage(
    bool AtHome,
    bool Chevron,
    LeftTab Tab,
    RightTab Reading)
{
    /// <summary>Whether the selected function's view stands beside the left rail. This is the chevron's
    /// answer and nothing else. Home stands over the library card and does not change this
    /// column.</summary>
    public bool LeftViewUp => Chevron;

    /// <summary>Which tab the left rail is lit for: the view standing beside it, and nothing once the view
    /// is folded away. A glow on a tab whose view is not there reads as a view still open. Home does not
    /// change it, because home does not take this column's view away.</summary>
    public LeftTab? LeftLit => Chevron ? Tab : null;

    /// <summary>Whether the import workbench is up. It does not stand in the left column: it is laid over
    /// the whole row, both cards and the seam, with only the left rail beside it. The columns underneath
    /// stay as they were, so nothing in the library is laid out again on the way in or out.</summary>
    public bool ImportOver => Chevron && Tab == LeftTab.Import;

    /// <summary>Which of the library card's four views is in front of the reader. None while home stands
    /// over the card, and none while the import workbench lies over the whole row.</summary>
    public RightTab? RightShowing => AtHome || ImportOver ? null : Reading;

    /// <summary>Which of the card's four views is standing in it, seen or not. The import workbench covers
    /// the card without taking its view down: a view put back up is laid out again, and laying the library
    /// out is the cost the workbench is laid over it to avoid. Home is different, because it takes the
    /// card's place.</summary>
    public RightTab? RightStanding => AtHome ? null : Reading;

    /// <summary>Which tab the library rail is lit for: the view standing in the card, which under the
    /// import workbench is covered along with its rail.</summary>
    public RightTab? RightLit => RightStanding;

    /// <summary>Whether the deck's home key wears the rails' mark. Home is the only tab that is on neither
    /// rail.</summary>
    public bool HomeLit => AtHome;
}
