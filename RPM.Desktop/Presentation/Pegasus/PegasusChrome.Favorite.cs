using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// The heart under the words: whether the record playing is one the listener has loved.
///
/// <para>This is the same mark that the album views write at the end of a track line, and the one the
/// generated favourites playlist is made from. The write goes through
/// <see cref="LibraryService.SetTrackFavorite"/>, which updates the library view behind this screen. So a
/// song loved here shows as loved on its line by the time the listener goes back to it.</para>
///
/// <para>Pegasus adds the confirmation. Full screen there is no line to watch and no menu that just closed,
/// so the press has to confirm itself: one sentence at the foot of the screen, naming the song. It goes
/// away after a couple of seconds (see <see cref="Announced"/>).</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>
    /// Raised with a line to show at the foot of the screen. The host shows it, not this control, because a
    /// message about the record is not part of the picture this control draws.
    /// </summary>
    public event Action<string>? Announced;

    /// <summary>The song the heart was last read for. Reading the mark is a database call, and the deck is
    /// checked on every frame. So the mark is read only when the song changes.</summary>
    private string? _favoriteFor;

    /// <summary>The mark as it was read for that song. A press toggles this value, so no database call is
    /// needed.</summary>
    private bool _favoriteOn;

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (_amp?.Deck is not { } deck || string.IsNullOrEmpty(deck.FilePath))
            return;

        var next = !_favoriteOn;

        // False means there was nowhere to write it: no library open, or a file played from outside the
        // collection. Nothing is shown and the heart does not fill in. A mark that was not stored must not
        // be reported as stored.
        if (!Library.SetTrackFavorite(deck.FilePath, next))
            return;

        _favoriteOn = next;
        PaintFavorite();

        Announced?.Invoke(string.Format(
            Strings.Loc.Get(next ? "Viz_FavoriteAdded" : "Viz_FavoriteRemoved"), deck.Title));
    }

    /// <summary>
    /// Reads the heart for whatever is on the deck, and only when the deck has actually moved.
    ///
    /// <para>Called from the tick, like the volume beside it. The mark can change from somewhere other than
    /// this key. The record ending and the next one starting is the usual case. A value read on the frame
    /// is correct whoever wrote it. The guard at the top is the only cost: the database is never asked
    /// twice about one song.</para>
    /// </summary>
    private void ReadFavorite()
    {
        var path = _amp?.Deck?.FilePath;
        if (path == _favoriteFor)
            return;
        _favoriteFor = path;

        var library = Library;
        _favoriteOn = path is not null && library.IsTrackFavorite(path);
        _favoriteHolds = path is not null && library.HoldsTrack(path);
        PaintFavorite();
    }

    /// <summary>Whether the collection holds this song at all. A file dragged in from the desktop has no
    /// row for a mark to live on, and a key that cannot do anything should not look as though it
    /// can.</summary>
    private bool _favoriteHolds;

    /// <summary>Fills the heart in, or hollows it out.</summary>
    private void PaintFavorite()
    {
        FavoriteButton.FontFamily = (FontFamily)Resources[_favoriteOn ? "PegasusFa" : "PegasusFaRegular"];
        FavoriteButton.IsEnabled = _favoriteHolds;
        FavoriteButton.Opacity = _favoriteHolds ? 1 : 0.35;
        IconName.Set(
            FavoriteButton, Strings.Loc.Get(_favoriteOn ? "Track_UnsetFavorite" : "Track_SetFavorite"));
    }
}
