using RPM.Core.Net;
using RPM.Core.Files;
using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Core.Metadata.Providers;
using RPM.Desktop.Services;
using RPM.Desktop.Strings;
using RPM.Fingerprint.Catalog;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Main;

/// <summary>
/// The heavier album-menu actions: opening the full album page, the review editor, editing the whole
/// record's tags and cover, and deleting an album from the library (to the OS trash, recoverably).
/// </summary>
public sealed partial class MainPage
{
    // ── album page ──

    /// <summary>
    /// Opens one record in the library card, over whichever view was showing.
    ///
    /// <para>Not a page in the frame. The frame is the whole window below the artwork, and navigating
    /// it would take the deck (the transport, the cast pill and the cloud chip) away with it. An album
    /// is something to look at while the music plays, so it replaces the grid and leaves the rest of
    /// the app standing.</para>
    /// </summary>
    /// <param name="from">Which page the record was reached out of. Closing the record has to put that
    /// page back. The caller states it, and it is never worked out here. One caller is itself a way BACK
    /// from an artist, and it runs while that page is still fading. A look at the screen would then answer
    /// "from the artist" about a page that is being left. The two would send the reader back and forth
    /// forever.</param>
    private void NavigateToAlbumPage(string folder, AlbumFrom from)
    {
        // A record belongs to the Albums view. So asking for one asks for that view. This happens from the
        // track-info pane's "open the whole record", from a menu, or when the session puts back the record
        // the app was closed on. Opening it under a rail set to Playlists would load a page nobody could see.
        // Remember where the record was opened from. A record reached from an artist's or a genre's page
        // belongs to that page. Closing it there must put that page back, not drop the reader on the album
        // wall they never came through.
        _albumOpenedFrom = from;

        // The page is BUILT before the move is asked for. That order is why the transition is clean.
        //
        // The other order fails. OpenRightTab starts a 110 ms fade and then returns. If this line ran
        // inside that fade, on the same thread the fade's landing is called back on, the swap would be
        // late, and by different amounts each time. Sometimes it waited until the deadline. Meanwhile
        // both panes sat faded out, with the screen the reader had just left showing through.
        //
        // This costs a short pause between the press and the move. The work is the same either way. A
        // short pause before a clean move looks better than a stall in the middle of the move.
        AlbumDetail.Load(folder, Library.AlbumOpen.Fresh);

        Panes.OpenRightTab(RightTab.Albums);
        ShowAlbumDetail(true);
    }

    /// <summary>The page the reader is looking at right now. A record opened from somewhere else (a menu,
    /// the info pane, a track row) is opened out of this page. It is asked at the call site, where it is
    /// still true. See NavigateToAlbumPage.</summary>
    private AlbumFrom WhereTheReaderIs =>
        Panes.Reading == RightTab.Artists && ArtistDetail.Visibility == Visibility.Visible
            ? AlbumFrom.ArtistPage
            : Panes.Reading == RightTab.Genres && GenreDetail.Visibility == Visibility.Visible
                ? AlbumFrom.GenrePage
                : AlbumFrom.Collection;

    /// <summary>
    /// Which page a record was opened out of, and so which one closing it goes back to.
    ///
    /// <para>This is a type, not a flag, because there are four answers. Three of them put something else
    /// on screen on the way out, and the fourth does not. A boolean could only say one of those moves.
    /// Every call site must state one. There is no default, so a new way of opening a record cannot
    /// quietly land the reader on the album wall.</para>
    /// </summary>
    private enum AlbumFrom
    {
        /// <summary>Off the collection itself, the wall, the list, the session. The record closes back
        /// onto whichever of those was showing.</summary>
        Collection,

        /// <summary>Out of an artist's page, which is in the Artists view: closing the record moves the
        /// rail back as well as hiding the page.</summary>
        ArtistPage,

        /// <summary>And out of a genre's page, which is in the Genres view, and the same again.</summary>
        GenrePage,

        /// <summary>Off one of the home screen's rows. Home is not on either rail. It is a view of the app's
        /// own that covers both panes. So closing the record puts home back up, rather than moving a rail.
        /// The reader ends where they started, not on an album wall they never came through.</summary>
        HomeScreen,
    }

    /// <summary>The view the album was opened over, so leaving it goes back to where the user was
    /// rather than to whichever view the setting happens to name.</summary>
    private UIElement? _albumDetailReturnsTo;

    /// <summary>Which page the record was opened out of, when that page is in one of the OTHER right-pane
    /// views. The album page takes the Albums view to draw itself in. So putting the reader back means
    /// moving the rail as well as hiding the page.</summary>
    private AlbumFrom _albumOpenedFrom;

    private void ShowAlbumDetail(bool show)
    {
        if (show)
        {
            _albumDetailReturnsTo = ViewFor(_view ?? LibraryViewMode.List);

            // The window takes the record's own colours while its page is up (see PageBackdrop). This is
            // set HERE, on the press. It is not left to the pane conductor, which runs at the end of the
            // pane's own move, a quarter of a second later. In that gap the window would be told the reader
            // is somewhere they have already left.
            PageBackdrop.Reading(AlbumDetail);

            // The record's page comes up OVER the collection. The collection stays where it is until the
            // page has landed on top of it.
            //
            // Do not crossfade here. Two half-transparent layers over one another are still two
            // half-transparent layers. In the middle of each fade the reader would see through BOTH of
            // them to the window's own background, which looks like the screen flashing. The page is the
            // last of the four in the markup and spans both rows, so it covers them completely. Nothing
            // under it has to move. What is left is one layer coming up over a still picture.
            AlbumDetail.Visibility = Visibility.Visible;
            FadeView(AlbumDetail, 1, then: () =>
            {
                // Put down only once the page is actually over them, and only if it still is: a fast
                // back would otherwise collapse the view the reader has just returned to.
                if (AlbumDetail.Visibility != Visibility.Visible)
                    return;

                // The heading goes with the grid it belongs to. Searching tracks and switching between
                // the list and the wall are things done to a collection, and there is no collection on
                // screen while one record is open.
                LibraryHeading.Visibility = Visibility.Collapsed;

                if (_albumDetailReturnsTo is { } view)
                    view.Visibility = Visibility.Collapsed;
            });

            return;
        }

        CloseAlbumDetail(AlbumCloseInto.View);
    }

    /// <summary>
    /// What is about to cover the card once the record's page is closed. It decides one thing: whether
    /// the page is faded off what is underneath, or left standing until something else covers it.
    /// </summary>
    private enum AlbumCloseInto
    {
        /// <summary>The collection, or whichever page the record was opened out of, the view that is
        /// already in this pane. It is put back at full strength behind the page, and the page is then
        /// faded off it.</summary>
        View,

        /// <summary>The home screen, which is not in this pane at all: it covers both of them, and it
        /// arrives on the pane conductor's own clock rather than this one.</summary>
        Home,
    }

    /// <summary>How long the record's page stands behind home before it is put down. The pane's own
    /// fade-out is 110 ms (PaneConductor.FadeMs), and the swap is at the end of it. This is longer than
    /// that, so the page is only put down once home is over it.</summary>
    private const int HomeCoversMs = 160;

    private void CloseAlbumDetail(AlbumCloseInto into)
    {
        if (AlbumDetail.Visibility != Visibility.Visible) return;

        // Where the reader is going is said BEFORE the record is closed. A page that is left for another
        // page must not take the window through the app's own backdrop on the way. The artist underneath
        // is still dressed. Naming them first turns two fades into one crossfade.
        //
        // Nothing is said where this page is no longer the one being read. Leaving for an artist opens
        // their page first (see the ArtistHooks in MainPage.AlbumWall). By the time this runs, the window
        // has already been handed over. Handing it back would make the wallpaper flash between two pages
        // that both have a background.
        if (ReferenceEquals(PageBackdrop.ReadingPage, AlbumDetail))
            PageBackdrop.Reading(_albumOpenedFrom switch
            {
                AlbumFrom.ArtistPage => ArtistDetail,
                AlbumFrom.GenrePage => (object)GenreDetail,
                _ => null,
            });

        AlbumDetail.Close();

        // Back to the page the record was opened out of, if it was opened out of one. The Albums view
        // underneath is still put back in order below. It is just not the one on screen.
        //
        // Home is not in this list, and that is the point. Closing a record is not always a way BACK. The
        // view switcher, a rail tab and a deleted album all come through here. Each of them is the reader
        // asking for something other than home. Only the back button returns to home, so only the back
        // button says so. See AlbumDetail_BackRequested. The claim is dropped here either way, so no later
        // close can act on a page the reader has long left.
        if (_albumOpenedFrom is AlbumFrom.ArtistPage or AlbumFrom.GenrePage)
        {
            var back = _albumOpenedFrom == AlbumFrom.ArtistPage ? RightTab.Artists : RightTab.Genres;
            _albumOpenedFrom = AlbumFrom.Collection;
            Panes.OpenRightTab(back);
        }
        else
        {
            _albumOpenedFrom = AlbumFrom.Collection;
        }

        var returning = _albumDetailReturnsTo ?? ViewFor(_view ?? LibraryViewMode.List);

        if (into is AlbumCloseInto.Home)
        {
            // NOT faded. Home covers both panes and comes up on the conductor's clock. So there is nothing
            // for this page to fade ONTO, except the collection underneath it. The reader would see that
            // collection for the whole length of the fade.
            //
            // So the page stands where it is, at full strength, covering the collection until home is over
            // it. The reader sees the record's page crossfading into home, which is the one move they asked
            // for.
            //
            // The collection is NOT put back yet. This page's ground is a wash (PageGround, PageWash), not
            // an opaque plate. A collection revealed underneath would show THROUGH it, fade or no fade. So
            // the collection is put back with the page's own put-down, behind home, where nobody can see
            // either change.
            _homeReturnsTo = returning;

            _homeCovers ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HomeCoversMs) };
            _homeCovers.Stop();
            _homeCovers.Tick -= HomeCovers_Tick;
            _homeCovers.Tick += HomeCovers_Tick;
            _homeCovers.Start();
            return;
        }

        // Going back to the view that is already in this pane. It is put back at full strength FIRST,
        // behind the page that still covers it. Then the page is faded off it. Nothing is ever
        // half-transparent over a half-transparent thing, so the window's own background never shows
        // between the two.
        ShowReturningView(returning);

        FadeView(AlbumDetail, 0, then: () =>
        {
            if (AlbumDetail.Opacity == 0)
                AlbumDetail.Visibility = Visibility.Collapsed;
        });
    }

    /// <summary>The collection, back on screen at full strength. Either behind the record's page as it
    /// fades off, or behind home, depending on where the reader is going.</summary>
    private void ShowReturningView(UIElement returning)
    {
        returning.Visibility = Visibility.Visible;
        Dissolving(returning).Set(1);

        // The wall was collapsed while the album page was up. Its first pass back is the one that can leave
        // its repeater estimating rows into empty space. See AlbumWallView.EnsureAnchored.
        if (ReferenceEquals(returning, AlbumWall))
            AlbumWall.EnsureAnchored();

        LibraryHeading.Visibility = Visibility.Visible;
        Dissolving(LibraryHeading).Set(1);
    }

    private UIElement? _homeReturnsTo;

    private DispatcherTimer? _homeCovers;

    /// <summary>Puts the record's page down behind home, and resets its opacity. The next record that is
    /// opened then has a fade to run, rather than appearing at full strength.</summary>
    private void HomeCovers_Tick(object? sender, object e)
    {
        _homeCovers?.Stop();

        // Only where home really is what is up. A reader who left home again within those few frames is
        // looking at something else. Putting this page down would take it away from under them.
        if (!Panes.AtHome)
            return;

        // The collection comes back first, then the page is put down. Both happen behind home, in one
        // turn, so no frame is visible to anybody.
        if (_homeReturnsTo is { } returning)
            ShowReturningView(returning);
        _homeReturnsTo = null;

        Dissolving(AlbumDetail).Set(0);
        AlbumDetail.Visibility = Visibility.Collapsed;
    }

    /// <summary>Back from a record: to the page it was opened out of, or to HOME if it was opened from one
    /// of home's rows.
    ///
    /// <para>The origin is read before the page is closed, because closing it drops the claim. Home is not
    /// a tab. It covers both panes, so the view underneath must be set straight first. Otherwise the wrong
    /// view waits there for whenever home is left. The artist page's back button reads its own origin the
    /// same way.</para></summary>
    private void AlbumDetail_BackRequested(object? sender, EventArgs e)
    {
        var home = _albumOpenedFrom == AlbumFrom.HomeScreen;

        // The origin is read before the close, because the close drops the claim. The close is also told
        // where the reader is going. That decides whether this page is faded off the collection, or left
        // standing until home covers it. See AlbumCloseInto.
        CloseAlbumDetail(home ? AlbumCloseInto.Home : AlbumCloseInto.View);

        if (home)
            ShowHome(true);
    }

    // ── write a review ──

    private async Task ShowAlbumReviewDialogAsync(string folder)
    {
        var library = App.Services.GetRequiredService<LibraryService>();
        if (!library.IsOpen || XamlRoot is null)
            return;
        var db = library.Db;

        var result = await Dialogs.AlbumReviewDialog.ShowAsync(
            XamlRoot, AlbumDisplayName(db, folder), db.GetAlbumMeta(folder));
        if (result is null)
            return;

        db.SetAlbumReview(folder, result.Rating, result.Markdown, DateTime.UtcNow.Ticks);
    }

    // ── edit the album (the mass-edit window over every track on it) ──

    /// <summary>
    /// "Edit album": the record's own editor, over every track on it.
    ///
    /// <para>The mass edit window is not used here, because it is the wrong tool for an album. It says
    /// "Editing 12 tracks", asks for a tick per field, and offers "multiple values" combos for things a
    /// record has only one of. An album has one name, one year, one label and one cover. The album editor
    /// shows them in boxes with those values in them, and writes what was changed to every track. It marks
    /// in amber the fields the tracks do not agree on. That is the only place where a record that has split
    /// in two is visible, without reading every row.</para>
    /// </summary>
    private async Task EditAlbumAsync(string folder)
    {
        var log = App.Services.GetRequiredService<ILogger<MainPage>>();
        if (XamlRoot is null)
            return;

        var tracks = await LoadAlbumTracksAsync(folder, CancellationToken.None);
        if (tracks.Count == 0)
        {
            log.LogWarning("[edit-album] {Folder} has no tracks in the mirror", folder);
            return;
        }

        var rows = tracks.Select(t => new TrackRow(t)).ToList();
        // There are two ends to the edit: the folder the album was in (this one), and any folder it lands
        // in. A changed album artist re-files the record under the naming template. The wall keeps one card
        // per folder and reuses it across every rebuild, cover included. So a card left where it was keeps
        // showing what it read first, unless it is told otherwise.
        // Both library-side answers are keyed on the library-relative folder, which is how the mirror knows
        // this record. It is not the absolute path that the editor prints in its footer.
        var db = App.Services.GetRequiredService<LibraryService>().Db;
        var meta = db.GetAlbumMeta(folder);

        var touched = await AlbumEdit.EditAsync(
            XamlRoot, LibraryFolderPath(folder) ?? folder, rows,
            ask => LookUpReleaseAsync(folder, ask, LibraryTrackStep(rows, ticked: false)),
            normalize: meta.Normalize,
            classical: meta.Classical,
            onLibraryFacts: facts =>
            {
                if (facts.NormalizeChanged)
                    db.SetAlbumNormalize(folder, facts.Normalize);

                // The listener's own answer, and the only thing that makes a record classical.
                if (facts.ClassicalChanged)
                    db.SetAlbumClassical(folder, facts.Classical);

                // The resolver caches per album, and the record being edited may be the one playing.
                // Both answers feed it: the classical one is what excuses a record from levelling.
                App.Services.GetRequiredService<NormalizationService>().Forget();
            });
        if (touched.Count == 0)
            log.LogInformation("[edit-album] {Folder}: nothing written", folder);
        var refreshed = touched.Append(folder).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var f in refreshed)
        {
            AlbumWall.RefreshAlbum(f);
            AlbumList.RefreshAlbum(f);
        }

        // The record on the deck is the one that was just edited. Its sleeve and the label on the turning
        // disc were read when it started playing, so they are now one version behind. Only that record is
        // refreshed. This re-runs the whole show, backdrops included.
        if (PlayingFolder() is { } playing
            && refreshed.Contains(playing, StringComparer.OrdinalIgnoreCase))
            _art?.Refresh();
    }

    /// <summary>The library folder of the record on the deck. It is null when nothing is playing, or when the
    /// file is not part of this library (a radio station, or a file dragged in from elsewhere).
    /// </summary>
    private static string? PlayingFolder()
    {
        var library = App.Services.GetRequiredService<LibraryService>();
        if (library.Root is not { } root) return null;
        if (App.Services.GetRequiredService<PlaybackService>().NowPlaying?.Path is not { Length: > 0 } path)
            return null;
        if (Path.GetDirectoryName(path) is not { Length: > 0 } dir) return null;

        var relative = Path.GetRelativePath(root, dir).Replace(Path.DirectorySeparatorChar, '/');
        if (relative.StartsWith("..", StringComparison.Ordinal)) return null;

        // A file sitting in the library root is the mirror's empty folder, not ".".
        return relative == "." ? "" : relative;
    }

    /// <summary>The other way to answer "what record is this": point at one the library already holds.
    ///
    /// <para>It gives the same shape of answer as the catalogue lookup, so the windows do not care which
    /// was used: the record's fields and its cover. It is the better answer for the most common case, a
    /// stray file that belongs to an album already on the shelf. There is no network, no rate limit, and no
    /// chance of landing on the wrong pressing, because the user points at the record they own. The file
    /// moves itself into that folder on save, since the naming template puts it where its tags say it
    /// belongs.</para>
    /// </summary>
    /// <param name="seed">What the track says it is, so the picker opens on the likely answer.</param>
    private async Task<Dialogs.MassEditLookup?> PickLibraryAlbumAsync(string? seed, Dialogs.ArtAsk ask)
    {
        var library = App.Services.GetRequiredService<LibraryService>();
        if (!library.IsOpen || library.Root is not { } root || XamlRoot is null)
            return null;
        if (await Dialogs.AlbumPickerDialog.ShowAsync(XamlRoot, seed) is not { } folder)
            return null;

        var tracks = library.Db.FolderTracks(folder).Select(t => t.ToTrackInfo(root)).ToList();
        if (tracks.Count == 0)
            return null;

        // What the record itself says, taken the way the album editor takes it: the value most of its
        // tracks carry, with blanks losing to anything named. A half-tagged album still gives an answer.
        var rows = tracks.Select(t => new TrackRow(t)).ToList();
        var fields = new Dictionary<Dialogs.TagFieldKind, string>();
        foreach (var tag in Dialogs.TagFields.AlbumAndDisc().Where(f => f.Kind is not Dialogs.TagFieldKind.DiscSubtitle))
            if (Dominant(rows.Select(tag.Get)) is { Length: > 0 } value)
                fields[tag.Kind] = value;

        var log = App.Services.GetRequiredService<ILogger<MainPage>>();
        log.LogInformation("[edit-track] library album {Folder} offers [{Fields}]", folder, string.Join(",", fields.Keys));

        // Nobody is asked here. There is no release window in this path. Pointing at a record on the
        // shelf and being handed its cover is the whole purpose. Ask.None still means the words alone,
        // which is what a window that is not showing a picture wants.
        if (ask == Dialogs.ArtAsk.None)
            return new Dialogs.MassEditLookup(fields);

        // The folder's own cover, at the size it is kept on disk. Nothing is fetched. The record's
        // picture is already on disk, and that is half the point of picking it.
        var path = Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));
        byte[]? cover = null;
        try
        {
            if (RPM.Core.Import.FolderArtWriter.FindFolderImage(path) is { } file)
                cover = await File.ReadAllBytesAsync(file);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "[edit-track] could not read the cover of {Folder}", folder);
        }

        return new Dialogs.MassEditLookup(
            fields,
            cover is { Length: > 0 }
                ? new Dialogs.MassEditArt(cover, cover is [0x89, (byte)'P', (byte)'N', (byte)'G', ..] ? "image/png" : "image/jpeg", cover)
                : null);
    }

    /// <summary>What to open the album picker's search on: the record this track thinks it is from, and
    /// failing that the artist. This is better than nothing typed at all. The answer is usually the first
    /// row.
    /// </summary>
    private static string? SeedFor(TrackRow row)
    {
        var album = Blank(row.Album) ?? Blank(row.CurrentAlbum);
        if (album is not null)
            return album;
        return Blank(row.AlbumArtist) ?? Blank(row.Artist) ?? Blank(row.CurrentArtist);
    }

    /// <summary>The value most of the tracks carry, with blanks losing to anything named. This is the same
    /// rule the album editor uses to show a record's fields.</summary>
    private static string Dominant(IEnumerable<string> values)
    {
        var named = values.Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
        return named.Count == 0
            ? ""
            : named.GroupBy(v => v, StringComparer.Ordinal)
                   .OrderByDescending(g => g.Count())
                   .ThenBy(g => g.Key, StringComparer.Ordinal)
                   .First().Key;
    }

    /// <summary>Where an album folder actually is on this disk. The mirror keeps folders relative to the
    /// library root; the editor writes a cover and a disc.png into a real directory.</summary>
    private static string? LibraryFolderPath(string folder)
    {
        var library = App.Services.GetRequiredService<LibraryService>();
        return library.Root is { } root ? Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar)) : null;
    }

    /// <summary>
    /// The release lookup, for an album that is already in the library. It searches on what the record
    /// says it is. It gives the pressing the user picked back to the mass edit, as values it can pour
    /// into its ticked boxes: album, artist, year, label and disc count. It also gives the sleeve and the
    /// disc label when a picture was asked for. The Discogs id is stored on the way past, so "Show in
    /// Discogs" works for this album from then on, whether or not any of the rest is kept.
    /// </summary>
    /// <param name="ask">None when neither picture row is ticked. A sleeve and a disc label are two more
    /// round trips, and there is nowhere for them to go. Otherwise it says whether the record already has
    /// pictures, which is where the window's overwrite offer starts.</param>
    private Task<Dialogs.MassEditLookup?> LookUpReleaseAsync(
        string folder, Dialogs.ArtAsk ask, Dialogs.Models.TrackMetadataStep step)
    {
        var library = App.Services.GetRequiredService<LibraryService>();
        if (!library.IsOpen)
            return Task.FromResult<Dialogs.MassEditLookup?>(null);

        var first = library.Db.FolderTracks(folder).FirstOrDefault();
        var parts = folder.Split('/', StringSplitOptions.RemoveEmptyEntries);

        return LookUpReleaseAsync(
            folder,
            !string.IsNullOrWhiteSpace(first?.Album) ? first!.Album : parts.Length > 0 ? parts[^1] : null,
            first?.AlbumArtist ?? first?.Artist ?? (parts.Length >= 2 ? parts[^2] : null),
            first?.Year ?? 0,
            ask,
            step: step);
    }

    /// <summary>
    /// The same lookup, told what to search on, rather than working it out from an album folder. Editing
    /// a few tracks from the tree works this way. It has rows, and no album card around them.
    /// </summary>
    /// <param name="folder">The album folder the tracks live in, when they all live in one. Null only
    /// loses the Discogs stamp and the folder's disc.png. The tags and the sleeve still land.</param>
    /// <param name="wantDiscArt">False where a disc label has nowhere to go. A queue row has no album
    /// folder until it is imported, so fetching one would be a round trip for nothing.</param>
    /// <param name="only">The single row being edited, when there is one. This is what lets the per-track
    /// credits (the work, the movement, the ISRC and the conductor) be filled. A selection of several
    /// tracks has no single answer for any of them, and an album has none either.</param>
    /// <param name="step">Whether this lookup may go on to name the caller's own files, and where those
    /// files land. It is required. <see cref="Dialogs.Models.TrackMetadataStep.None"/> is the answer for
    /// every window that only identifies a record.</param>
    private async Task<Dialogs.MassEditLookup?> LookUpReleaseAsync(
        string? folder, string? album, string? artist, uint year, Dialogs.ArtAsk ask,
        Dialogs.Models.TrackMetadataStep step, bool wantDiscArt = true, TrackRow? only = null)
    {
        var library = App.Services.GetRequiredService<LibraryService>();
        if (!library.IsOpen || XamlRoot is null || Model is not { } model)
            return null;

        // The catalogues are reached on the user's own session. So a lookup made while signed out would
        // search and find nothing. Ask for the sign-in instead, and carry on with the lookup if it succeeds.
        if (!await Dialogs.SignInGate.AllowAsync(XamlRoot, Loc.Get("Login_ForLookup")))
            return null;

        var db = library.Db;

        var log = App.Services.GetRequiredService<ILogger<MainPage>>();
        // Tracks already in the library say nothing about what they were ripped from. So the medium
        // starts unnarrowed, and the user picks one if the wrong pressings come back.
        var picked = await Dialogs.ReleaseLookupDialog.ShowAsync(
            XamlRoot, model.Catalog, step, Array.Empty<double>(), album, artist, ReleaseMedia.Any,
            country: null, year: year, ask: ask);
        if (picked is null)
        {
            log.LogInformation("[edit-album] release lookup cancelled for {Folder}", folder);
            return null;
        }

        // The window went on to the tracks and the user applied the listing, so the rows already carry it.
        // Carrying it onto the files is the step's job. Only the step knows whether these are files on the
        // shelf or copies from the import store.
        if (picked.Fill is { Named: > 0 } fill)
        {
            log.LogInformation("[edit-album] track step filled {Named} of {Total} rows from {Release}",
                fill.Named, fill.Total, picked.Release.Ref.Id);
            await step.CommitAsync();
        }

        // The pressing is stored whichever catalogue answered. The shared album entry is matched on the
        // pressing, so a MusicBrainz pick must be saved too. Keeping only Discogs picks would drop the link.
        if (folder is { Length: > 0 })
            db.SetAlbumPressing(folder, picked.Release.Ref.Provider, picked.Release.Ref.Id);

        var matched = only is null ? null : MatchTrack(picked, only);
        var fields = ReleaseFields(picked, await DiscCountAsync(model, picked), matched);
        log.LogInformation("[edit-album] release {Release} offers [{Fields}]{Track}",
            picked.Release.Ref.Id, string.Join(",", fields.Keys),
            only is null ? "" : matched is null ? " (no track matched)" : $" (track {matched.Position})");
        // The words alone were asked for. Or the user was offered the pictures in the window and said no,
        // which is the same answer arriving later. Either way the record keeps what it has.
        if (ask == Dialogs.ArtAsk.None || !picked.OverwriteArt)
            return new Dialogs.MassEditLookup(fields, Release: picked.Release.Ref);

        // The sleeve, not the picture in the search list. It ends up on disk and is looked at from then on.
        // So it goes through the same full-size ladder that the rip and split paths use. The list thumbnail
        // is 150 px, and it is only the fallback when no archive has a scan.
        var artwork = App.Services.GetRequiredService<ArtworkService>();
        var pickedArtist = picked.Release.Artist ?? artist;
        var pickedAlbum = picked.Release.Title ?? album;

        var sleeve = await model.Catalog.SleeveAsync(picked.Release);

        // The archive is keyed by release id, and it has no scan for many pressings. The artwork service
        // answers on artist and album instead. This is how the disc label is found here too. So the two
        // agree about whether this record has a picture.
        if (sleeve is null && await artwork.CoverBytesAsync(pickedArtist, pickedAlbum) is { } scan)
            sleeve = SleeveScaler.ToSleeve(scan);

        if (sleeve is not { Embedded.Bytes.Length: > 0, Folder.Bytes.Length: > 0 })
            log.LogWarning("[edit-album] no sleeve for {Release} — nothing to put on {Folder}",
                picked.Release.Ref.Id, folder);
        else
            log.LogInformation("[edit-album] sleeve for {Release}: {Embedded} KB embedded, {Folder} KB on disk",
                picked.Release.Ref.Id, sleeve.Embedded.Bytes.Length / 1024, sleeve.Folder.Bytes.Length / 1024);

        // The label printed on the disc, for the release the user just settled. This is the same fetch the
        // CD rip makes, asked with the picked pressing's own names rather than the folder's. Most records
        // have no disc art, and that is not an error.
        var discArt = wantDiscArt ? await artwork.DiscArtBytesAsync(pickedArtist, pickedAlbum) : null;
        if (wantDiscArt)
            discArt = await StampedDiscArtAsync(model, picked, discArt, log);

        return new Dialogs.MassEditLookup(
            fields,
            sleeve is null ? null : new Dialogs.MassEditArt(sleeve.Embedded.Bytes, sleeve.Embedded.Mime, sleeve.Folder.Bytes),
            discArt,
            picked.Release.Ref);
    }

    /// <summary>
    /// The disc art the record actually gets, once both places have been asked.
    ///
    /// <para>The artwork ladder answers on artist and album. It cuts a disc out of whatever it finds
    /// before handing it back (see <see cref="ArtworkService.DiscArtBytesAsync"/>). So what arrives here
    /// without transparency is what it could not cut a disc out of. It can also be nothing at all, which
    /// is the ordinary answer for many pressings.</para>
    ///
    /// <para>The catalogue the user has just picked from is the other place to look. It is the better
    /// place for a record, because it has collectors' photographs of that exact pressing. A CD found there
    /// is stamped whole and replaces the drawn record. A record's label is stamped at the label's own
    /// size, and RPM's own pressing shows around it.</para>
    /// </summary>
    private static async Task<byte[]?> StampedDiscArtAsync(
        MainModel model, Dialogs.PickedRelease picked, byte[]? fetched, ILogger log)
    {
        if (fetched is { Length: > 0 } && DiscStamp.HasAlpha(fetched))
            return fetched;

        var fromCatalog = await model.Catalog.DiscArtAsync(picked.Release);
        if (fromCatalog is { Length: > 0 })
            log.LogInformation("[edit-album] disc art stamped out of {Provider}'s photographs of {Release}",
                picked.Release.Ref.Provider, picked.Release.Ref.Id);

        return fromCatalog ?? fetched;
    }

    /// <summary>
    /// The release lookup for a selection of tracks rather than a whole album. Editing from the tree
    /// works this way. It searches on what the rows say, and treats them as one record only when they
    /// agree. Rows from two albums have no single pressing to look up. So the search opens on the artist
    /// alone, not on whichever album happened to be first.
    /// </summary>
    private Task<Dialogs.MassEditLookup?> LookUpReleaseForRowsAsync(
        IReadOnlyList<TrackRow> rows, Dialogs.ArtAsk ask, Dialogs.Models.TrackMetadataStep step,
        bool wantDiscArt = true)
    {
        var album = Agreed(rows.Select(r => Blank(r.Album) ?? Blank(r.CurrentAlbum)));
        var artist = Agreed(rows.Select(r => Blank(r.AlbumArtist) ?? Blank(r.Artist) ?? Blank(r.CurrentArtist)));
        uint.TryParse(Agreed(rows.Select(r => Blank(r.Year))), out var year);

        // One row means the per-file editor. Only then can a track's own credits be looked up.
        return LookUpReleaseAsync(
            AlbumFolderOf(rows), album, artist, year, ask, step, wantDiscArt,
            only: rows.Count == 1 ? rows[0] : null);
    }

    /// <summary>
    /// Names a set of rows from a release. It fills the title, and everything else a listing knows about a
    /// track rather than about the record: its own artist on a compilation, its ISRC, the work it performs
    /// and which movement, who conducted and who played.
    ///
    /// <para>Nothing is written without first being shown. The listing is laid against the files in a grid
    /// that the user can read and correct. Apply puts it on the files (see
    /// <see cref="Dialogs.Models.ReleaseTrackFill"/>). The record's own fields are also applied, but only
    /// when the user ticks them. A lookup meant to fix twelve titles must not quietly rename the album.
    /// A box with the values printed beside it is not quiet, so the user can see what will change.</para>
    ///
    /// <para><b>A record already linked to a pressing skips the search.</b> Whichever way the link was made
    /// (the album editor, the album page's picker, or an identified rip), it is stored in album_meta.
    /// Asking the user to find it again would make the app forget what it was told. But applying a listing
    /// to seven files, with nobody shown which pressing it is, is not the same as asking. So the user is
    /// shown the page, and is asked on it.</para>
    ///
    /// <para>Matching is done by <see cref="ReleaseTrackMatcher"/>. It pairs by disc and number, and it
    /// refuses a near miss. Files the release has no track for keep their row, marked. A release with more
    /// tracks than the record has files stops when the files run out.</para>
    /// </summary>
    /// <param name="settled">The pressing this record is already linked to, when it is.</param>
    /// <param name="known">What is printed on its sleeve. It seeds the search when there is no link.</param>
    /// <param name="step">The rows to fill, and where they are written afterwards. Its rows and
    /// <paramref name="rows"/> are the same objects.</param>
    private async Task<Dialogs.Models.TrackFill?> NameTracksFromReleaseAsync(
        IReadOnlyList<TrackRow> rows, ReleaseRef? settled, AlbumMeta? known,
        Dialogs.Models.TrackMetadataStep step)
    {
        if (rows.Count == 0 || XamlRoot is null || Model is not { } model)
            return null;

        if (!await Dialogs.SignInGate.AllowAsync(XamlRoot, Loc.Get("Login_ForLookup")))
            return null;

        var album = Agreed(rows.Select(r => Blank(r.Album) ?? Blank(r.CurrentAlbum)));
        var artist = Agreed(rows.Select(r => Blank(r.AlbumArtist) ?? Blank(r.Artist) ?? Blank(r.CurrentArtist)));
        uint.TryParse(Agreed(rows.Select(r => Blank(r.Year))), out var year);

        // Already settled: go straight to the tracks, with no search in front of them. The window fetches
        // the listing itself, on the page the user is waiting on. Fetching it here first meant seconds of
        // a menu that had done nothing yet. The album and artist are passed too, for the one case the page
        // cannot answer: a pressing whose catalogue has no listing for it. The window then falls back to
        // its search, rather than closing with an error.
        if (settled is not null)
        {
            var straight = await Dialogs.ReleaseLookupDialog.ShowTracksAsync(
                XamlRoot, model.Catalog, settled, step, album, artist);
            if (straight is { Named: > 0 })
                await step.CommitAsync();
            return straight;
        }

        // What the record already knows about itself goes INTO the window. The catalogue number and the
        // barcode from its sleeve fill the form. So the search opens on a narrow question: "SHVL 804" is
        // one pressing, but "Dark Side of the Moon" matches two hundred.
        var picked = await Dialogs.ReleaseLookupDialog.ShowAsync(
            XamlRoot, model.Catalog, step, Array.Empty<double>(), album, artist, ReleaseMedia.Any,
            country: null, year: year,
            catno: Blank(known?.Catno), barcode: Blank(known?.Barcode));
        if (picked is null)
            return null;

        // The pressing the user just settled is written down. Without this, the album stays unlinked, and
        // the same search has to be run again. It is the same fact that the album editor and the album
        // page both store from their own pickers.
        if (AlbumFolderOf(rows) is { Length: > 0 } folder
            && App.Services.GetRequiredService<LibraryService>() is { IsOpen: true } library)
            library.Db.SetAlbumPressing(folder, picked.Release.Ref.Provider, picked.Release.Ref.Id);

        if (picked.Fill is { Named: > 0 })
            await step.CommitAsync();
        return picked.Fill;
    }

    /// <summary>The one library folder every row is in, or null when they are spread across more than one.
    /// A disc.png and a Discogs id belong to an album. With no single album, there is nothing to give them
    /// to.</summary>
    private static string? AlbumFolderOf(IReadOnlyList<TrackRow> rows)
    {
        var library = App.Services.GetRequiredService<LibraryService>();
        if (library.Root is not { } root)
            return null;

        string? folder = null;
        foreach (var row in rows)
        {
            if (Path.GetDirectoryName(row.FilePath) is not { Length: > 0 } dir)
                return null;

            var relative = Path.GetRelativePath(root, dir).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith("..", StringComparison.Ordinal))
                return null;   // outside the library: not a folder the mirror knows

            if (folder is null)
                folder = relative;
            else if (!string.Equals(folder, relative, StringComparison.OrdinalIgnoreCase))
                return null;
        }
        return folder;
    }

    /// <summary>The value every row gives, or null when they disagree or none has one.</summary>
    private static string? Agreed(IEnumerable<string?> values)
    {
        string? agreed = null;
        foreach (var value in values)
        {
            if (value is null)
                continue;
            if (agreed is null)
                agreed = value;
            else if (!string.Equals(agreed, value, StringComparison.OrdinalIgnoreCase))
                return null;
        }
        return agreed;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>
    /// What a pressing can say about the fields the edit windows offer. A field the catalogue cannot
    /// answer is simply absent rather than filled with a blank, that is what leaves a comment or a
    /// BPM alone.
    ///
    /// <para>Most of this comes off the full listing rather than the search row, because the search
    /// row is deliberately thin (see <c>ReleaseSearch</c>, no provider returns detail from a search).
    /// The listing has been fetched by the time a release is picked, so nothing here costs a request.</para>
    /// </summary>
    /// <param name="track">The one track being edited, when it is one track. Its own credits, the
    /// work it performs, its movement, its ISRC, who conducted, cannot be filled from an album, and a
    /// selection of several has no single answer for any of them.</param>
    private static Dictionary<Dialogs.TagFieldKind, string> ReleaseFields(
        Dialogs.PickedRelease picked, uint discCount, ReleaseTrackInfo? track = null)
    {
        var release = picked.Release;
        var detail = picked.Detail;
        var fields = new Dictionary<Dialogs.TagFieldKind, string>();

        void Set(Dialogs.TagFieldKind kind, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                fields[kind] = value.Trim();
        }
        void SetNumber(Dialogs.TagFieldKind kind, uint value)
        {
            if (value != 0)
                fields[kind] = value.ToString();
        }

        Set(Dialogs.TagFieldKind.Album, release.Title);
        if (!string.IsNullOrWhiteSpace(release.Artist))
        {
            // Both are set, because a release names one artist and the window keeps the two apart. On a
            // record that is not a compilation they are the same name. This is the mass edit's own case.
            Set(Dialogs.TagFieldKind.AlbumArtist, release.Artist);
            Set(Dialogs.TagFieldKind.Artist, release.Artist);
        }
        SetNumber(Dialogs.TagFieldKind.Year, release.Year);
        // The search row's label, then the listing's, the same fact from whichever half has it.
        Set(Dialogs.TagFieldKind.Publisher, release.Label ?? detail?.Label);
        SetNumber(Dialogs.TagFieldKind.DiscCount, discCount);

        // The MusicBrainz id fields hold MusicBrainz ids. A Discogs release number would resolve to a
        // different record, or to nothing, for us and for every other tagger that reads it.
        bool musicBrainz = string.Equals(
            release.Ref.Provider, RpmMetadataProvider.ProviderId, StringComparison.OrdinalIgnoreCase);
        void SetMbid(Dialogs.TagFieldKind kind, string? value)
        {
            if (musicBrainz)
                Set(kind, value);
        }

        if (detail is not null)
        {
            Set(Dialogs.TagFieldKind.Genre, detail.Genre);
            // Discogs sends a record's styles beside its genres. The catalogue's single genre string is
            // also read for a style. Either way, this is the answer arriving without anybody typing it,
            // which is the whole point of asking.
            Set(Dialogs.TagFieldKind.Subgenre, detail.Subgenre);
            Set(Dialogs.TagFieldKind.Barcode, detail.Barcode);
            Set(Dialogs.TagFieldKind.CatalogNumber, detail.CatalogNumber ?? release.CatalogNumber);
            Set(Dialogs.TagFieldKind.Media, detail.Format ?? release.Format);
            SetNumber(Dialogs.TagFieldKind.OriginalYear, detail.OriginalYear);
            // What kind of release it is, as the catalogue files it. Stored by its own name, which is what
            // the release type's list holds (TagFields.ReleaseTypeValues).
            if (detail.ReleaseType is { } type)
                Set(Dialogs.TagFieldKind.ReleaseType, RPM.Core.Library.ReleaseTypes.ToStored(type));

            // One name for the shelf, on both fields for the same reason the artist is set twice.
            Set(Dialogs.TagFieldKind.AlbumArtistSort, detail.ArtistSort);
            Set(Dialogs.TagFieldKind.ArtistSort, detail.ArtistSort);

            SetMbid(Dialogs.TagFieldKind.ReleaseId, detail.Ref.Id);
            SetMbid(Dialogs.TagFieldKind.ReleaseGroupId, detail.ReleaseGroupId);
            SetMbid(Dialogs.TagFieldKind.ReleaseArtistId, detail.ArtistId);
        }

        if (track is not null)
        {
            // A compilation's row has its own artist, and it is not the release's. Overwriting what was set
            // above is the point. This is the more specific answer.
            Set(Dialogs.TagFieldKind.Artist, track.Artist);
            Set(Dialogs.TagFieldKind.ArtistSort, track.ArtistSort);
            SetMbid(Dialogs.TagFieldKind.ArtistId, track.ArtistId);
            Set(Dialogs.TagFieldKind.Isrc, track.Isrc);
            SetMbid(Dialogs.TagFieldKind.RecordingId, track.RecordingId);

            Set(Dialogs.TagFieldKind.Work, track.Work);
            Set(Dialogs.TagFieldKind.MovementName, track.MovementName);
            SetNumber(Dialogs.TagFieldKind.Movement, track.MovementNumber);

            Set(Dialogs.TagFieldKind.Composers, track.Composers);
            Set(Dialogs.TagFieldKind.ComposerSort, track.ComposerSort);
            Set(Dialogs.TagFieldKind.Conductor, track.Conductor);
            Set(Dialogs.TagFieldKind.Ensemble, track.Ensemble);
            Set(Dialogs.TagFieldKind.Soloists, track.Soloists);
            Set(Dialogs.TagFieldKind.Lyricist, track.Lyricist);
            Set(Dialogs.TagFieldKind.Arranger, track.Arranger);
        }

        return fields;
    }

    /// <summary>
    /// The listing's row for the track being edited, or null when it cannot be identified with confidence.
    ///
    /// <para>It matches by number first, because that is what the user is looking at. Then it is confirmed
    /// by title, where there is one to confirm against. A release whose track order differs from the
    /// file's would otherwise put one track's composer onto another. A near miss returns nothing. The
    /// album-level fields still land, and the per-track fields keep what the file had.</para>
    /// </summary>
    private static ReleaseTrackInfo? MatchTrack(Dialogs.PickedRelease picked, TrackRow row)
    {
        if (picked.Detail is not { Tracks.Count: > 0 } detail)
            return null;

        uint number = uint.TryParse(Blank(row.Track), out var n) ? n : 0;
        uint disc = uint.TryParse(Blank(row.Disc), out var d) ? d : 0;
        var title = Blank(row.Title) ?? Blank(row.Current.Title);

        // On a multi-disc listing the per-disc number is the one printed on the sleeve; on a
        // single-disc one the flat position is the same number anyway.
        var candidates = detail.Tracks
            .Where(t => number == 0 ||
                        (t.DiscPosition == 0 ? t.Position : t.DiscPosition) == number)
            .Where(t => disc == 0 || t.Disc == 0 || t.Disc == disc)
            .ToList();

        if (candidates.Count == 1)
            return Confirm(candidates[0]);

        return title is null
            ? null
            : Confirm(candidates.FirstOrDefault(t =>
                string.Equals(t.Title?.Trim(), title, StringComparison.OrdinalIgnoreCase)));

        // A number that lands on a track with a different name is what this guards against. The listing
        // and the file disagree about the running order, so nothing per-track can be trusted.
        ReleaseTrackInfo? Confirm(ReleaseTrackInfo? candidate)
        {
            if (candidate is null || title is null || string.IsNullOrWhiteSpace(candidate.Title))
                return candidate;
            return string.Equals(candidate.Title.Trim(), title, StringComparison.OrdinalIgnoreCase)
                ? candidate
                : null;
        }
    }

    /// <summary>
    /// The picked release's full listing. It is the one the release came with, or one fetched from the
    /// catalogue that owns it.
    ///
    /// <para>The picker only ranks results when it has track lengths to rank against. Only a ranked row
    /// carries a listing. So every path that opens the picker on a record, rather than on a recording,
    /// gets a release with no tracklist. Naming tracks needs exactly that listing.</para>
    /// </summary>
    /// <returns>The listing, null where the catalogue has none, or unanswered where it did not
    /// answer.</returns>
    private static async Task<Asked<ReleaseDetail?>> ListingAsync(
        MainModel model, ReleaseRef release, ReleaseDetail? carried)
    {
        if (carried is { Tracks.Count: > 0 })
            return Asked.Answer<ReleaseDetail?>(carried);

        var provider = model.Catalog.Search.Providers.FirstOrDefault(p =>
            string.Equals(p.Id, release.Provider, StringComparison.OrdinalIgnoreCase));
        return provider is null
            ? Asked.Answer(carried)
            : (await provider.GetReleaseAsync(release.Id)).Then(fetched => fetched ?? carried);
    }

    /// <summary>How many discs the pressing spans, from whichever listing can be had. A catalogue that
    /// will not answer leaves the disc count unfilled.</summary>
    private static async Task<uint> DiscCountAsync(MainModel model, Dialogs.PickedRelease picked)
    {
        if (picked.Detail is { DiscCount: > 0 } detail)
            return detail.DiscCount;
        // 0 means "not known", which leaves the disc-count tag unwritten. That is right both for a listing
        // that says nothing and for one that did not arrive. Neither writes a count that is not true.
        try
        {
            return (await ListingAsync(model, picked.Release.Ref, picked.Detail)).Answers(out var fetched, out _)
                   && fetched is { DiscCount: > 0 }
                ? fetched.DiscCount
                : 0;
        }
        catch
        {
            return 0;
        }
    }
}
