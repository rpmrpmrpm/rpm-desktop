using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using RPM.Desktop.Services;

namespace RPM.Desktop.Presentation.Main.LeftPane;

/// <summary>
/// The heart in the pane's header: whether the listener has marked the record on the sheet as a favourite.
///
/// <para>It uses the same flag that the album views write at the end of a track line, the same one the
/// full-screen player's key writes, and the same one the generated favourites playlist is built from. This
/// pane stores nothing itself. The write goes through <see cref="LibraryService.SetTrackFavorite"/>, which
/// notifies the shelf, the wall and the screen over them. So a song marked here shows as a favourite
/// everywhere it is drawn by the time the listener looks.</para>
///
/// <para>The traffic runs both ways. A mark made on a track line or in the full-screen player reaches
/// this pane through <see cref="LibraryService.TrackFavoriteChanged"/>, if the pane is still showing that
/// song. The heart is not a memory of what was pressed in this pane. It shows what the collection says
/// right now.</para>
///
/// <para>A press also shows a short sentence along the foot of the window, naming the song. It stays up
/// for a couple of seconds (see <c>MainPage.ShowFlash</c>). The rest of the pane shows only facts, and
/// none of them changes when the heart is pressed. Without the sentence the key would look as if it did
/// nothing.</para>
/// </summary>
public sealed partial class TrackInfoView
{
    /// <summary>What the collection last said about the record on the sheet. It is kept so the press
    /// knows what it is toggling, without asking the database again for an answer it gave when the record
    /// arrived.</summary>
    private bool _favoriteOn;

    /// <summary>Whether any collection here holds this song. That is this library, or a shelf borrowed
    /// from another computer, since both have a row that a mark can be stored on (see <see
    /// cref="LibraryService.HoldsTrack"/>). A file dragged in from the desktop, or a radio stream, is not
    /// held, so the key is hidden rather than dimmed. The album key beside it is hidden on the same kind
    /// of answer. Two different ways of showing "not for this record" would be two marks instead of
    /// one.</summary>
    private bool _favoriteHolds;

    /// <summary>
    /// Reads the heart for the record the sheet now shows.
    ///
    /// <para>Called from <see cref="Show"/>, the one place where the record changes. Show has already
    /// ignored a repeat of the same path. So the database is asked once per song, not twice a second
    /// while playback runs.</para>
    /// </summary>
    private void ReadFavorite(string? path)
    {
        // Answered without asking anybody, on purpose. This runs from ShowEmpty, and ShowEmpty runs from the
        // constructor. At that point there is no window yet, let alone an open collection.
        if (string.IsNullOrEmpty(path))
        {
            _favoriteHolds = false;
            _favoriteOn = false;
        }
        else
        {
            var library = Library;
            _favoriteHolds = library.HoldsTrack(path);
            _favoriteOn = _favoriteHolds && library.IsTrackFavorite(path);
        }

        PaintFavorite();
    }

    /// <summary>Fills in the heart, or leaves it hollow.</summary>
    private void PaintFavorite()
    {
        FavoriteButton.Visibility = _favoriteHolds ? Visibility.Visible : Visibility.Collapsed;
        FavoriteButton.FontFamily = (FontFamily)Resources[_favoriteOn ? "Fa" : "FaRegular"];

        // Red once the mark is set. Otherwise the pane's own ink is used. The filled heart is the app's
        // one favourite colour, and it does not follow the pane (FavoriteMarkBrush reads well on a light
        // photograph and on a dark one alike). The hollow heart MUST follow the pane's ink. The ink
        // changes to black over a pale artist photograph, and a white outline there would be invisible.
        FavoriteButton.Foreground = _favoriteOn
            ? (Brush)Application.Current.Resources["FavoriteMarkBrush"]
            : Ink("InkStrong");

        IconName.Set(
            FavoriteButton, Strings.Loc.Get(_favoriteOn ? "Track_UnsetFavorite" : "Track_SetFavorite"));
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (_path.Length == 0)
            return;

        var next = !_favoriteOn;

        // False means the mark could not be stored. Nothing is announced and the heart does not fill in.
        // A mark that was not stored must not be reported as stored.
        if (!Library.SetTrackFavorite(_path, next))
            return;

        // Read back rather than assumed. The heart shows what the collection says, so it reads the answer
        // the same way it does when the record arrives. This costs one row read per press, and it is
        // correct whatever the write actually did.
        ReadFavorite(_path);

        // The sentence is built the same way, for the same reason: it reports what was stored, not what
        // was asked for.
        Announced?.Invoke(string.Format(
            Strings.Loc.Get(_favoriteOn ? "Viz_FavoriteAdded" : "Viz_FavoriteRemoved"), TitleSaid()));
    }

    /// <summary>The song's name as the sentence should say it: the name on the sheet, which is what the
    /// listener is looking at. The file name is used only where the header has not been written
    /// yet.</summary>
    private string TitleSaid()
        => TitleText.Text is { Length: > 0 } shown ? shown : TitleFor(null, null);

    /// <summary> The same mark, made somewhere else. The full-screen player sits over this window instead
    /// of replacing it, and the wall behind this pane lists the same song. So the heart here must be
    /// right whoever pressed what. The pane reads the collection's answer and does not remember its own
    /// press.
    /// </summary>
    private void OnTrackFavoriteChanged(string path, bool favorite)
    {
        if (DispatcherQueue is not { } queue)
            return;

        queue.TryEnqueue(() =>
        {
            // Only if this is still the same record. The write can complete a moment after the next song
            // has started. A mark for the record that just left must not fill in over the record that has
            // arrived. The path is the whole test, and it works whatever order the two arrive in.
            if (!string.Equals(path, _path, System.StringComparison.Ordinal))
                return;
            _favoriteOn = favorite;
            PaintFavorite();
        });
    }
}
