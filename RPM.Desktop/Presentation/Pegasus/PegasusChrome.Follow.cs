namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// Pegasus shows the collection that is open: the listener's own, or somebody else's while theirs is open
/// (<see cref="Services.Library.LibraryService.ViewingPeer"/>), as the TV app does.
///
/// <para>Every screen reads <c>library.Shelf</c> and <c>library.ShelfRoot</c>, as the main window's
/// screens do: the wall, the carousel, a record's page, the artists, genres and playlists screens, the
/// search screen and the drawer's shelf. Home is the main window's own <c>HomeView</c>, which follows by
/// itself, and radio follows through <c>RadioService.Saved</c>. So the "is this borrowed?" checks (see
/// <see cref="RecordBorrowed"/>) and what is on screen always agree: the listener's own records are never
/// treated as borrowed, and a friend's are never given the listener's hearts.</para>
///
/// <para>Writes stay where they belong. A record's heart goes to the collection on screen (see
/// <see cref="Services.Library.AlbumShelf.OnScreen"/>), and the stars, the playlists and the add sheet
/// are the listener's own and are offered only on their own collection.</para>
///
/// <para><b>When the collection changes.</b> The library raises <c>Changed</c> when another collection
/// is opened or put down, from the side menu (see <see cref="PickCollection"/>), from the main window, or
/// because a borrowed shelf stopped answering. <c>Changed</c> is also raised for every edit, so the
/// switch is told apart by the folder of the shelf on screen. Then what was read off the last collection
/// is let go: a record's page closes, the artists, genres, playlists and search screens cross to home
/// (their artist or genre may not be on this collection), and the wall reads again where it is up. A wall
/// that is not up reads again when it next comes up (see <see cref="WallOffAnotherCollection"/>).</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>The folder of the collection Pegasus last showed, or null before it has followed one. A
    /// borrowed shelf's mirror has a folder of its own, so another collection is another folder.</summary>
    private string? _followedRoot;

    /// <summary>Whether the wall's cards were read off another collection than the one on screen, or not
    /// read at all. Such a wall is read again before it is shown, and its cards are not used for a record
    /// opened from elsewhere (see ShowRecordAsync).</summary>
    private bool WallOffAnotherCollection =>
        !string.Equals(Library.ShelfRoot ?? "", _libraryRootFor, StringComparison.OrdinalIgnoreCase);

    /// <summary>Listens while Pegasus is up, and checks once on the way up, for a switch made while it was
    /// down. From <see cref="SetActive"/>.</summary>
    private void ListenForCollection(bool on)
    {
        Library.Changed -= OnLibraryChanged;
        if (!on)
            return;
        Library.Changed += OnLibraryChanged;
        FollowCollection();
    }

    /// <summary>Raised on any thread, for a switch and for every edit alike.</summary>
    private void OnLibraryChanged() => DispatcherQueue?.TryEnqueue(FollowCollection);

    /// <summary>Lets go of what was read off the last collection, where the collection on screen is
    /// another one. Nothing happens for an edit to the same collection: each screen reads again on its
    /// next visit, as it did before.</summary>
    private void FollowCollection()
    {
        var library = Library;
        var root = library.IsOpen ? library.ShelfRoot ?? "" : "";
        if (string.Equals(root, _followedRoot, StringComparison.OrdinalIgnoreCase))
            return;

        var first = _followedRoot is null;
        _followedRoot = root;
        if (first)
            return;   // nothing has been read off any collection yet

        RPM.Core.Diagnostics.Scope.Said(
            "pegasus", library.IsPeerView ? "following a shared collection" : "following the listener's own collection");

        // Reads still in flight are about the last collection, and land nowhere.
        _recordRead++;
        _shelfRead++;
        _shelf = null;
        _genreWall = [];

        HideRecord();
        if (LibraryUp && IsArtistsGenres(_collection))
            CrossToHome();
        else if (WallUp && WallOffAnotherCollection)
            FillLibraryAsync();
    }
}
