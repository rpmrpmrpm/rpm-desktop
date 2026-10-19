using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RPM.Desktop.Presentation.Main.RightPane;

namespace RPM.Desktop.Presentation.Main;

public sealed partial class MainPage
{
    /// <summary>Cmd or Ctrl+F on the library window. The keyboard goes to the search field on screen,
    /// chosen by <see cref="SearchTarget"/>. If no search field is on screen (home, an album page, the
    /// genres, the playlists), the Albums tab is opened and its field takes the keyboard once it is
    /// shown.
    ///
    /// <para>A request for a field that is still on its way is held by <see cref="FieldFocus"/>. It
    /// stays valid only while the Albums view is still the one asked for. A press elsewhere during the
    /// move cancels it.</para>
    /// </summary>
    internal void FocusSearch()
    {
        if (OnScreen() is { } shown)
        {
            FieldFocus.Give(FieldFor(shown), () => OnScreen() == shown);
            return;
        }

        AskForTab(RightTab.Albums);
        FieldFocus.Give(SearchBox, () => Panes.OnScreen.RightShowing == RightTab.Albums);
    }

    private SearchField? OnScreen() => SearchTarget.OnScreen(
        Panes.OnScreen,
        Panes.Shown,
        albumPageUp: AlbumDetail.Visibility == Visibility.Visible,
        artistPageUp: ArtistDetail.Visibility == Visibility.Visible,
        keyboardInSide: KeyboardIn(LeftViews));

    private TextBox FieldFor(SearchField field) => field switch
    {
        SearchField.Albums => SearchBox,
        SearchField.Artists => ArtistSearchBox,
        SearchField.ArtistTracks => ArtistDetail.TrackSearch,
        SearchField.Import => ImportWorkbench.SearchField,
        SearchField.Radio => RadioBody.SearchField,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };

    /// <summary>Whether the focused element is <paramref name="scope"/> or inside it.</summary>
    private bool KeyboardIn(DependencyObject scope)
    {
        if (XamlRoot is null || FocusManager.GetFocusedElement(XamlRoot) is not DependencyObject focused)
            return false;

        for (var node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, scope))
                return true;

        return false;
    }
}
