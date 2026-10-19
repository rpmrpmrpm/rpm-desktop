using RPM.Desktop.Presentation.Main.Library;
using RPM.Desktop.Presentation.Main.Models;

namespace RPM.Desktop.Presentation.Main;

/// <summary>The loaders on the main screens: Albums (wall and list), Artists, Genres and Playlists
/// each show the spinning record while their content has not been read from the collection open now.
///
/// <para>One question per screen, asked whenever an answer can have changed: when the library changes
/// (<see cref="MainModel.ShelfReadingChanged"/>, which can mean a different collection), after every
/// rebuild (the same event), and whenever the panes put a different screen up (PaneConductor.ApplyViews).
/// The answer is a fact read at the moment it is asked (<see cref="MainModel.AlbumsRead"/>,
/// <see cref="MainModel.ArtistsRead"/>), so these calls can arrive in any order and any number of
/// times.</para>
///
/// <para>The data table has no loader: it pages the database itself and is never empty for want of
/// a read. Home has its own (HomeView's ReadingState) and the first read happens behind the
/// splash.</para>
/// </summary>
public sealed partial class MainPage
{
    /// <summary>Puts each screen's loader up or down. UI thread only.</summary>
    internal void ReflectShelfReading()
    {
        var model = Model;

        AlbumsLoading.Reflect(
            model is { AlbumsRead: false }
            && LibraryBody.Visibility == Visibility.Visible
            && _view is LibraryViewMode.Wall or LibraryViewMode.List);

        ArtistsLoading.Reflect(
            model is { ArtistsRead: false } && ArtistsBody.Visibility == Visibility.Visible);

        // The genre wall is cut from the artists' half of the index (see FeedGenres).
        GenresLoading.Reflect(
            model is { ArtistsRead: false } && GenresBody.Visibility == Visibility.Visible);

        // The playlist pane asks its own model (PlaylistModel.GeneratedListsRead); the page only says
        // whether it is on screen.
        PlaylistPanel.ReflectReading(PlaylistPanel.Visibility == Visibility.Visible);
    }

    /// <summary>Listens for the model's reading changes for as long as the page is loaded.</summary>
    private void WatchShelfReading(MainModel model)
    {
        model.ShelfReadingChanged += OnShelfReadingChanged;
        Unloaded += (_, _) => model.ShelfReadingChanged -= OnShelfReadingChanged;
        ReflectShelfReading();

        void OnShelfReadingChanged() => DispatcherQueue.TryEnqueue(ReflectShelfReading);
    }
}
