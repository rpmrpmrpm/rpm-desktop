using Microsoft.UI.Xaml;

namespace RPM.Desktop.Presentation.Main;

/// <summary>
/// What the first-run hints point at on this page.
///
/// <para>Two named properties, so the shell does not reach into this page's markup. Which control is
/// "where music comes in" is decided on this page, and it can move. A hint that asks for the control
/// by name keeps working when it moves, and the compiler reports an error if the property is removed.</para>
///
/// <para>The hints themselves are drawn by the shell, over the whole window: see
/// <c>ShellPage.Coachmarks</c> for why they cannot belong to the pages that own their targets.</para>
/// </summary>
public sealed partial class MainPage
{
    /// <summary>The rail's Import button, bottom left.</summary>
    internal FrameworkElement? CoachAddMusicTarget => Rail?.ImportTabTarget;

    /// <summary>
    /// The three-key view switch in the library header, or null while nobody can see it.
    ///
    /// <para><b>Visible is not the same as on screen here.</b> The home screen is drawn over the library
    /// card, not in place of it. The header underneath still reports itself as visible while home is
    /// showing. If only the switch were checked, the hint would point at empty space on the home screen.
    /// All three conditions must hold: the switch is drawn, the collection is the view that is up, and
    /// home is not drawn over it.</para>
    /// </summary>
    internal FrameworkElement? CoachViewsTarget =>
        ViewToggle is { Visibility: Visibility.Visible } toggle
        && LibraryBody.Visibility == Visibility.Visible
        && Home.Visibility != Visibility.Visible
            ? toggle
            : null;

    /// <summary>The deck's menu button, which is where signing in is on this page. The shell's edge
    /// hamburger is collapsed here, so the deck carries the same flyout.</summary>
    internal FrameworkElement? CoachMenuTarget => Deck?.MenuTarget;

    /// <summary>Set once the collection has been opened for the view-switch hint. The app does this only
    /// once per run. A user who goes back to the home screen has said where they want to be, so the
    /// app must not keep moving them.</summary>
    private bool _openedForViewsCoach;

    /// <summary>
    /// Opens the collection, so the hint about the view switch has a switch to point at.
    ///
    /// <para><b>Moves the user, on purpose and only once.</b> The switch is not drawn on the home screen.
    /// A new user lands on the collection, so this path is reached only when the app was restarted
    /// before the tour finished. The other choice was to wait for the user to find the collection alone.
    /// Showing them the thing the hint is about is more useful, and it is the view they would open next
    /// anyway.</para>
    /// </summary>
    /// <returns>True when it moved. False when it has already done so once, or the collection is
    /// what is showing.</returns>
    internal bool OpenCollectionForCoach()
    {
        if (_openedForViewsCoach || CoachViewsTarget is not null)
            return false;

        _openedForViewsCoach = true;
        ShowAlbumDetail(false);
        Panes.OpenRightTab(RightTab.Albums);
        return true;
    }
}
