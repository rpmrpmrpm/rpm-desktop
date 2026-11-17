using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Library;
using RPM.Desktop.Presentation.Main.Artists;
using RPM.Desktop.Services.Library;

namespace RPM.Desktop.Presentation.Main;

/// <summary>
/// The keys in the headings of the Artists, Genres and Playlists views, beside the album header's own: the
/// box size on the two walls, the artists wall's heart, and the slide that keeps each heading's keys clear of
/// the spinning record, as the album header's are (<see cref="UpdateChromeAvoidance"/>).
/// </summary>
public sealed partial class MainPage
{
    // ── the slide clear of the record ──

    /// <summary>The headings' key groups that dodge the record, besides the album header's own
    /// (<see cref="_chromeDodge"/>). Each is made once its heading exists.</summary>
    private readonly List<Common.DodgeShift> _headingDodges = [];

    /// <summary>Has the group in <paramref name="heading"/> dodge the record. The group is measured again
    /// when its heading is resized and when its view is shown, the two moves that put it somewhere else in
    /// the window.</summary>
    private void DodgeRecord(FrameworkElement group, TranslateTransform shift, FrameworkElement heading, UIElement view)
    {
        _headingDodges.Add(new Common.DodgeShift(group, shift));
        heading.SizeChanged += (_, _) => UpdateChromeAvoidance(animate: false);
        group.SizeChanged += (_, _) => UpdateChromeAvoidance(animate: false);
        view.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => UpdateChromeAvoidanceWhenLaidOut());
        UpdateChromeAvoidanceWhenLaidOut();
    }

    // ── the artists wall ──

    /// <summary>Whether the artists wall is narrowed to the artists something of whom is hearted.</summary>
    private bool _artistFavoritesOnly;

    /// <summary>Counts the reads of who is hearted, so a slow read that a newer one overtook is
    /// dropped.</summary>
    private int _artistFavoritesRead;

    /// <summary>The artists heading's keys: the box size, put back as it was left, and the heart.</summary>
    private void AttachArtistHeading()
    {
        var settings = App.Services.GetRequiredService<SettingsService>();

        // Seeded before the wall is first laid out, quietly: a seed is not the reader's choice, and the
        // handler is hooked after it.
        var size = Math.Clamp(settings.ArtistTileSize, ArtistWallView.SmallestTarget, ArtistWallView.LargestTarget);
        ArtistsWall.SetTileTarget(size);
        ArtistSizeSlider.Value = size;
        ArtistSizeSlider.ValueChanged += (_, e) =>
        {
            ArtistsWall.SetTileTarget(e.NewValue);
            settings.ArtistTileSize = e.NewValue;
        };

        _artistFavoritesOnly = Session.ArtistFavoritesOnly;
        PaintArtistFavorites();

        // A heart given or taken anywhere (a song's line, the player, a record's menu) can put an artist on
        // the narrowed wall or take them off it.
        var library = App.Services.GetRequiredService<LibraryService>();
        library.TrackFavoriteChanged += OnHeartForArtists;
        library.AlbumFavoriteChanged += OnHeartForArtists;
        Unloaded += (_, _) =>
        {
            library.TrackFavoriteChanged -= OnHeartForArtists;
            library.AlbumFavoriteChanged -= OnHeartForArtists;
        };
        void OnHeartForArtists(string _, bool __) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_artistFavoritesOnly)
                NarrowArtistsToFavorites();
        });

        DodgeRecord(ArtistChrome, ArtistChromeShift, ArtistsHeading, ArtistsBody);
    }

    private void ArtistFavoritesOnly_Click(object sender, RoutedEventArgs e)
    {
        _artistFavoritesOnly = !_artistFavoritesOnly;
        Session.ArtistFavoritesOnly = _artistFavoritesOnly;
        PaintArtistFavorites();
        NarrowArtistsToFavorites();
    }

    /// <summary>The heart is filled and lit while the wall is narrowed, and an outline while it is not: the
    /// album header's pair (<see cref="SetFavoritesToggle"/>).</summary>
    private void PaintArtistFavorites()
    {
        ArtistFavoritesIcon.FontFamily = (FontFamily)Resources[_artistFavoritesOnly ? "Fa" : "FaRegular"];
        ArtistFavoritesIcon.Foreground = (Brush)Application.Current.Resources[
            _artistFavoritesOnly ? "FavoriteMarkBrush" : "FavoriteMarkIdleBrush"];
        SelectedSegment.Paint(ArtistFavoritesButton, _artistFavoritesOnly);
    }

    /// <summary>
    /// Puts the wall's narrowing in step with the heart: everybody while it is off, and while it is on, the
    /// artists with a hearted record or song (<see cref="ArtistWall.Favorites"/>), read on a worker.
    /// </summary>
    /// <remarks>Run after every feed of the wall too (<see cref="FeedArtists"/>): a library change brings
    /// a new list of artists, and the narrowing is worked out against the list on the wall.</remarks>
    private void NarrowArtistsToFavorites()
    {
        var read = ++_artistFavoritesRead;
        if (!_artistFavoritesOnly || Model is not { } model)
        {
            ArtistsWall.SetOnly(null);
            CountArtists();
            return;
        }

        var library = App.Services.GetRequiredService<LibraryService>();
        if (!library.IsOpen)
            return;

        var shelf = model.ArtistShelf.Acts;
        var db = library.Shelf;
        _ = Task.Run(() =>
        {
            IReadOnlySet<string>? kept = null;
            try
            {
                kept = ArtistWall.Favorites(shelf, db.FavoriteAlbumFolders(), db.FavoriteSongCredits());
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException
                                           or Microsoft.Data.Sqlite.SqliteException)
            {
                // The library was closed or swapped under the read. The next feed asks again.
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                if (kept is null || read != _artistFavoritesRead || !_artistFavoritesOnly)
                    return;
                ArtistsWall.SetOnly(kept);
                CountArtists();
            });
        });
    }

    // ── the genres wall ──

    /// <summary>The genres heading's keys: the card size, put back as it was left.</summary>
    private void AttachGenreHeading()
    {
        var settings = App.Services.GetRequiredService<SettingsService>();
        var size = Math.Clamp(settings.GenreTileSize, ArtistWallView.SmallestTarget, ArtistWallView.LargestTarget);
        GenresWall.SetCardTarget(size);
        GenreSizeSlider.Value = size;
        GenreSizeSlider.ValueChanged += (_, e) =>
        {
            GenresWall.SetCardTarget(e.NewValue);
            settings.GenreTileSize = e.NewValue;
        };

        DodgeRecord(GenreChrome, GenreChromeShift, GenresHeading, GenresBody);
    }

    // ── the playlists view ──

    /// <summary>The playlists heading's keys dodge the record too. Called once the view is made
    /// (EnsurePlaylistPanel).</summary>
    private void AttachPlaylistHeading()
        => DodgeRecord(PlaylistPanel.Chrome, PlaylistPanel.ChromeShift,
                       (FrameworkElement)PlaylistPanel.Chrome.Parent, PlaylistPanel);
}
