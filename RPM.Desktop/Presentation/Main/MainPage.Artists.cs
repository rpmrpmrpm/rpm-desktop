using RPM.Core.Library;
using RPM.Core.Metadata.Providers;
using RPM.Core.Threading;
using RPM.Desktop.Presentation.Main.Artists.Models;
using RPM.Desktop.Services;
using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace RPM.Desktop.Presentation.Main;

/// <summary>
/// The Artists view: the wall of boxes, the artist page it opens, and the loaders that feed both.
///
/// <para>The wall and the album views are two readings of ONE collection, told by one event, so a
/// record imported while the artists view is up appears on it at the same moment it appears on the wall
/// of covers.</para>
///
/// <para>Their SEARCHES are separate. This view is fed the whole collection
/// (<see cref="LibraryAlbumIndex.ArtistShelf"/>) and narrows it with its own field; the field over the
/// album wall narrows the album views and nothing here.</para>
/// </summary>
public sealed partial class MainPage
{
    private bool _artistsHooked;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _artistSearchDebounce;

    private void AttachArtists()
    {
        if (_artistsHooked || Model is not { } model)
            return;
        _artistsHooked = true;

        // The photograph a box is drawn from. It comes from the shared cache that the track pane also uses.
        // So a wall of forty artists is not forty downloads, even on a machine that has been playing music
        // all evening. The picture, its grey copy and its colour are worked out once per artist per session,
        // the first time they are asked for.
        ArtistsWall.PortraitLoader = LoadArtistPortraitAsync;
        // Opened from the wall, so there is no record under this page. Back from it is the wall itself.
        ArtistsWall.ArtistOpened += artist => NavigateToArtistPage(artist, ArtistFrom.Collection);

        // The artist page reads the same records from the same loaders as the album views. It is a way into
        // one collection, not a second copy of it. It uses a different portrait loader from the wall, and
        // the difference is one name. The compilations shelf gets a mosaic of its own sleeves here, and the
        // wall keeps the made figure. See LoadArtistPageFaceAsync.
        ArtistDetail.PortraitLoader = LoadArtistPageFaceAsync;
        ArtistDetail.CoverLoader = LoadAlbumCoverAsync;
        ArtistDetail.TracksLoader = LoadAlbumTracksAsync;
        ArtistDetail.PlayTrack = PlayAlbumTrack;
        ArtistDetail.FactsLoader = AlbumFactsFor;
        // The release types off the same reading as the facts, so the page shelves the collection it is showing.
        ArtistDetail.ReleaseTypesLoader = () =>
            Model?.ArtistShelf.AlbumReleaseTypes
            ?? new Dictionary<string, RPM.Core.Library.ReleaseType>();
        ArtistDetail.PapersLoader = LoadArtistPapersAsync;
        ArtistDetail.PapersCached = artist =>
            NobodyInParticular(artist)
                ? null
                : App.Services.GetRequiredService<TrackInfoService>().Cached(artist, album: null);
        ArtistDetail.PopularLoader = LoadPopularReleasesAsync;
        ArtistDetail.GalleryLoader = LoadArtistPicturesAsync;
        ArtistDetail.LogoLoader = LoadArtistLogoAsync;
        ArtistDetail.PlaylistsLoader = LoadArtistPlaylistsAsync;

        // The card's picture comes from the one place a playlist's cover is ever drawn. So the shelf here and
        // the head of that playlist's own tracklist show the same square. Only the square is used. The wash
        // drawn beside it is for a page to stand on, and a card is not a page.
        //
        // This is a node lookup, not a field on PlaylistHolding. That record is the shape that
        // FlattenPlaylistTracks and the shelf both build from. A second field on it, for one caller's
        // convenience, would be a field that every other reader of PlaylistHolding must account for.
        ArtistDetail.PlaylistCoverLoader = async (playlist, ct) =>
        {
            var library = App.Services.GetRequiredService<Services.Library.LibraryService>();
            var borrowed = library.IsPeerView;
            var db = borrowed ? library.Shelf : library.Db;
            var cover = db.GetPlaylistNodes().FirstOrDefault(n => n.Id == playlist.NodeId)?.Cover
                ?? new PlaylistCover.Automatic();

            return (await PlaylistCovers.MadeAsync(
                playlist.NodeId, playlist.Name, cover,
                borrowed ? Common.PlaylistShelf.Borrowed : Common.PlaylistShelf.Own,
                ct)).Cover;
        };

        // Pressing a playlist here leaves for where playlists live. The page is not closed on the way out.
        // The rail's Artists button then comes back to this artist, not to the wall.
        ArtistDetail.OpenPlaylist = nodeId =>
        {
            EnsurePlaylistPanel();
            Panes.OpenRightTab(RightTab.Playlists);
            PlaylistPanel.OpenPlaylist(nodeId, fromHome: false);   // Opened from an artist's page, so back is the tree
        };

        // The search is coalesced. A keystroke re-folds two thousand names and re-lays the wall. A fast
        // typist would otherwise do that six times on the way to a three-letter word. The delay is the same
        // 200 ms that the queue's own search uses.
        _artistSearchDebounce = DispatcherQueue.CreateTimer();
        _artistSearchDebounce.Interval = TimeSpan.FromMilliseconds(200);
        _artistSearchDebounce.IsRepeating = false;
        _artistSearchDebounce.Tick += (_, _) => ApplyArtistSearch();

        model.LibraryAlbumsChanged += OnArtistsChanged;
        Unloaded += (_, _) => model.LibraryAlbumsChanged -= OnArtistsChanged;

        FeedArtists();

        void OnArtistsChanged() => DispatcherQueue.TryEnqueue(FeedArtists);
    }

    /// <summary>
    /// What the free catalogues say about one artist: their biography, the doors out to their own pages,
    /// and who is in the band. A failure is thrown, not returned as an empty dossier. The page would draw
    /// an empty dossier as an artist nobody has written about. The page's catch draws nothing, which is
    /// the honest result for a question that got no answer.
    ///
    /// <para>It is the same service that the track pane reads. So the two surfaces cannot disagree about
    /// who an artist is. An artist whose record has been playing all evening opens with their card already
    /// filled in.</para>
    ///
    /// <para>No album is passed, and that is on purpose. The wall's names are acts rather than credits, and
    /// this page is about the person. A record passed here would narrow the lookup to whichever one happened
    /// to be first on their shelf.</para>
    /// </summary>
    private static async Task<TrackDossier> LoadArtistPapersAsync(string artist, CancellationToken ct)
    {
        if (NobodyInParticular(artist))
            return TrackDossier.Empty;

        var asked = await App.Services.GetRequiredService<TrackInfoService>().GetAsync(artist, album: null, ct: ct);
        return asked.Answers(out var dossier, out var why)
            ? dossier
            : throw new HttpRequestException("The artist's papers did not arrive: " + why);
    }

    /// <summary>Whether this box stands for a shelf rather than for a person. This is the compilations:
    /// every record filed under "Various Artists" lands on this shelf (see ArtistWall.Compilations).
    ///
    /// <para>It has a page like any other act, because the records on it are real, and somebody looking for
    /// them has to find them somewhere. It does NOT have a biography, a photograph or a Discogs number.
    /// Asking the catalogues for them returns somebody else's band. MusicBrainz has entries called "Various
    /// Artists", and none of them is this shelf. So the wall draws the made figure from the colours of its
    /// records. The page draws a mosaic of the sleeves on the shelf (see <see
    /// cref="LoadArtistPageFaceAsync"/>). Neither says anything it cannot know.</para>
    /// </summary>
    private static bool NobodyInParticular(string? name)
        => RPM.Core.Metadata.Providers.ArtistCredit.IsVariousArtists(name);

    /// <summary>The artist's best-known records, off Discogs.
    ///
    /// <para>It is read on the user's OWN connected Discogs account. That is the only way this app talks to
    /// Discogs: the API proxies it, and the proxy signs with the caller's tokens. A user who has not
    /// connected an account gets no card. Asking anyway is a refusal, not an answer. So the service is
    /// asked first whether it may answer at all. This stops a page from making a round trip on every artist
    /// opened, when no account is connected.</para>
    ///
    /// <para><b>Every page the catalogue names them under, not the first.</b> An artist can have more than
    /// one Discogs page: a rename, a second spelling, or a side project that the catalogue files
    /// separately. MusicBrainz lists all of them, in no particular order. If only the first were asked, a
    /// name variant with a few records could be ranked as the artist's most popular. When all of them are
    /// asked about and ranked once, the order is a fact about the artist, not about which link came back
    /// first.</para>
    ///
    /// <para><b>And more than one page of each.</b> The endpoint will not sort by popularity. So the counts
    /// are read from the rows, and a page that is not read is a record that cannot win. See <see
    /// cref="DiscogsWalk"/> for what bounds the walk.</para>
    /// </summary>
    /// <param name="discogsArtistIds">Every Discogs artist number the catalogues gave for this act, in the
    /// order they gave them. An empty list is not asked about at all. The caller has already decided there
    /// is no card.</param>
    private static async Task<PopularAnswer> LoadPopularReleasesAsync(
        IReadOnlyList<string> discogsArtistIds, CancellationToken ct)
    {
        var discogs = App.Services.GetRequiredService<DiscogsService>();

        // Already known to have no account connected. This is answered without a round trip. So a listener
        // who has not connected Discogs is not asking the network about every artist they open.
        if (discogs.Status is { Known: true, Configured: true, Connected: false })
            return PopularAnswer.NoAccount;

        if (!discogs.MayAnswer)
            return PopularAnswer.None;

        var gathered = new List<PopularRelease>();
        var asked = false;

        try
        {
            var reachable = true;

            foreach (var id in discogsArtistIds.Distinct(StringComparer.Ordinal).Take(DiscogsPages))
            {
                if (!reachable)
                    break;

                var pages = DiscogsWalk;

                for (var page = 1; page <= pages; page++)
                {
                    // Nothing back at all means Discogs declined to be asked, which is not the same as an
                    // empty page. The session ended during the walk, or the account was disconnected in
                    // another window. What was gathered before that still stands, and nothing more will come.
                    if (await discogs.ArtistReleasesAsync(id, page, ct) is not { } json)
                    {
                        reachable = false;
                        break;
                    }

                    asked = true;

                    var rows = DiscogsPopular.Read(json);
                    gathered.AddRange(rows);

                    // The endpoint states how many pages there are. This is read from the first page, and
                    // then trusted. Asking again on every page would read the same number back. It is kept
                    // under the ceiling either way.
                    if (page == 1)
                        pages = Math.Min(DiscogsWalk, DiscogsPopular.PageCount(json));

                    // A page with nothing on it ends the walk, whatever the pagination claims. This is also
                    // what an API that ignores the page number looks like from here. That is the case the
                    // ranking removes duplicates for.
                    if (rows.Count == 0)
                        break;
                }
            }
        }
        catch (DiscogsUnavailableException)
        {
            // The call itself settles the question the first time. A 428 response means "no account
            // connected", and the service records that as it passes. So the status is read AGAIN after the
            // failure, rather than guessed from the message.
            if (discogs.Status is { Known: true, Configured: true, Connected: false })
                return PopularAnswer.NoAccount;
            if (gathered.Count == 0)
                return PopularAnswer.None;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (gathered.Count == 0)
                return PopularAnswer.None;   // No network, or Discogs is unavailable
        }

        // What was gathered before the trouble started is still an answer. A walk that read three pages and
        // then hit a rate limit knows more about this artist than a card that is not there.
        var ranked = asked ? DiscogsPopular.Rank(gathered) : [];

        // This is logged because the card is the end of a chain that nobody watching the screen can see.
        // The log shows which numbers the catalogue gave for this act, how many of their credits were their
        // own work, and what ranked top. An artist whose card is empty, or whose top record is one that two
        // people own, is explained by this line and by no amount of looking at the screen. See the same
        // argument at RPM.Faces.
        App.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RPM.Popular").LogInformation(
            "[popular] discogs ids [{Ids}] -> {Rows} of their own records, top {Top}",
            string.Join(", ", discogsArtistIds), gathered.Count,
            ranked.Count > 0 ? $"\"{ranked[0].Title}\" ({ranked[0].Have} have)" : "none");

        return asked ? PopularAnswer.Of(ranked) : PopularAnswer.None;
    }

    /// <summary>How many pages of one Discogs artist's releases the card reads. Four pages is enough that
    /// most of an artist's catalogue is in hand, and the ranking is the whole truth about them. It is few
    /// enough that opening an artist is not a dozen round trips against a rate limit that other pages share.
    /// A prolific artist loses the tail of the sort. That is where the pressings and reissues are, not where
    /// a most-owned list is decided.</summary>
    private const int DiscogsWalk = 4;

    /// <summary>How many of an artist's Discogs artist numbers are asked about. There are three. An act with
    /// more numbers than that has been filed under every spelling anybody used. The later numbers are not
    /// where their best-known record is.</summary>
    private const int DiscogsPages = 3;

    /// <summary>What the mirror knows about one record, for the line under its sleeve on an artist's page.
    /// It is read from the index the wall was built from, not from the disk. The page lists a shelf, and
    /// reading twenty records' files to caption them would be twenty reads for two words each.
    ///
    /// <para>It uses the artists' side of the index, which covers the whole collection. The album views'
    /// own dictionaries are narrowed to their search field. Captioning an artist's records from those would
    /// leave a page opened during a search with a blank line under every sleeve that the search did not
    /// match.</para></summary>
    private (int Year, int Tracks, bool Explicit) AlbumFactsFor(string folder)
    {
        if (Model is not { } model)
            return (0, 0, false);

        var shelf = model.ArtistShelf;
        var year = shelf.AlbumFacts.TryGetValue(folder, out var facts) ? facts.Year : 0;
        var tracks = shelf.TrackCounts.TryGetValue(folder, out var count) ? count : 0;

        // One reading of the question, not a second one. A record marked explicit on one shelf and clean on
        // another would mean the collection disagrees with itself about that record.
        return (year, tracks, shelf.ExplicitAlbums.Contains(folder));
    }

    /// <summary>The wall's artists, and the line that counts them. This runs on every shelf rebuild. The list
    /// itself only changes on a library change, because a search over the albums does not reach it.</summary>
    private void FeedArtists()
    {
        if (Model is not { } model)
            return;

        var artists = model.ArtistShelf.Acts;
        ArtistsWall.SetArtists(artists, model.ArtistsRead);
        CountArtists();

        // An artist left open who is no longer in the collection (their last record was deleted) has nothing
        // left to be a page about. A search cannot cause this, because the list is the whole collection.
        if (ArtistDetail.Visibility == Visibility.Visible
            && ArtistDetail.Artist is { } showing
            && !artists.Any(a => string.Equals(a.Name, showing.Name, StringComparison.OrdinalIgnoreCase)))
            ShowArtistDetail(false);
    }

    /// <summary>The line under the heading counts what is on the wall, not what the collection holds. The
    /// wall's own search narrows the one and not the other. A subtitle that kept counting the whole
    /// collection over twelve boxes would describe something nobody can see.</summary>
    private void CountArtists()
    {
        var shown = ArtistsWall.Shown;
        ArtistsSubheading.Text = Model is { ArtistsRead: false } ? ""
            : shown == 0
            ? Loc.Get(ArtistsWall.Searching ? "Artists_NoMatch" : "Stats_Empty")
            : Loc.Format(shown == 1 ? "Artists_CountOne" : "Artists_Count", shown);
    }

    // ── the wall's own search ──

    /// <summary>A keystroke: the clear button appears as soon as there is something to clear, and the
    /// wall itself is narrowed once the typing settles.</summary>
    private void ArtistSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        ArtistSearchClear.Visibility = ArtistSearchBox.Text.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        _artistSearchDebounce?.Stop();
        _artistSearchDebounce?.Start();
    }

    /// <summary>Escape clears a search, which is the one key everybody tries. Enter does nothing on purpose.
    /// The wall narrows as the user types, so there is nothing left for Enter to submit.</summary>
    private void ArtistSearch_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape)
            return;

        ClearArtistSearch();
        e.Handled = true;
    }

    private void ArtistSearchClear_Click(object sender, RoutedEventArgs e) => ClearArtistSearch();

    private void ClearArtistSearch()
    {
        if (ArtistSearchBox.Text.Length == 0)
            return;

        ArtistSearchBox.Text = "";
        // Straight through, without the debounce. Clearing is one gesture, not a word being typed. A fifth
        // of a second of the old results after it would look like a control that stuck.
        _artistSearchDebounce?.Stop();
        ApplyArtistSearch();
    }

    private void ApplyArtistSearch()
    {
        ArtistsWall.SetSearch(ArtistSearchBox.Text);
        CountArtists();

        // Searching for somebody while looking at somebody else's page means looking for them on the wall. So
        // the page steps aside. The field is over the wall, and it is the wall that it filters.
        if (ArtistsWall.Searching)
            ShowArtistDetail(false);
    }

    /// <summary>What one artist's box is drawn from, on the artists wall and on a genre page: their
    /// photograph, drained to grey, with the colour to wash it in. Where no catalogue has ever photographed
    /// them, the box is painted in the colour of their first record instead.
    ///
    /// <para>Nobody has photographed every credit as it is WRITTEN. The wall's names are acts rather than
    /// credits. So this asks under the artist's own name and nothing else. The splitting that a track pane
    /// has to do as a second guess has already happened upstream, in <see cref="ArtistWall"/>.</para>
    ///
    /// <para>The sleeve is read only where the photograph did not come. That order is the point. On a
    /// collection of any size most names ARE photographed. Reading a cover for every one of them would be a
    /// decode per box, for a colour that nothing draws.</para>
    ///
    /// <para><b>A backdrop, never the square.</b> The boxes on the artists wall and on a genre page are
    /// 16:9. That is the shape backdrops arrive in, so a backdrop fills a box with nothing cropped. The
    /// squares are for the surfaces about one artist: the track pane and the artist page. The phone's wall
    /// uses the same rule, and it keeps the squares out of its 16:9 boxes.</para>
    /// </summary>
    private Task<ArtistFace> LoadArtistPortraitAsync(ArtistWallItem artist, CancellationToken ct)
        => LoadArtistFaceAsync(artist, ArtistPortraits.ChooseAsync, ct);

    /// <summary>One artist's face, with the picture chosen by <paramref name="choose"/>.
    /// </summary>
    /// <param name="choose">Which of the artist's pictures this surface wears:
    /// <see cref="ArtistPortraits.ChooseAsync"/> (a backdrop) for a box, <see
    /// cref="ArtistPortraits.SquareAsync"/> (the square first) for the artist page. No default: the two
    /// surfaces differ on purpose, and a default would be one of them wearing the other's picture without
    /// anybody deciding it.</param>
    private async Task<ArtistFace> LoadArtistFaceAsync(
        ArtistWallItem artist, Func<string, string?, CancellationToken, Task<Asked<string?>>> choose, CancellationToken ct)
    {
        // The compilations shelf is not a person and has no photograph, see NobodyInParticular. Its
        // box is the made figure, drawn from the colours of the records on it.
        if (NobodyInParticular(artist.Name))
            return await FigureAsync(artist, artist.Name, ct);

        // The lookup that names the photograph did not answer: the same stand-in as a photograph that
        // could not be fetched, for the same reason.
        if (!(await choose(artist.Name, AlbumAskedAbout(artist), ct)).Answers(out var chosen, out _))
            return (await FigureAsync(artist, artist.Name, ct)).InsteadOfUnfetchedPhoto();

        if (chosen is { } url)
        {
            // The photograph could not be fetched, so the made figure stands in. It is marked, so a box that
            // remembers it has asked asks again the next time it is shown. The artist does have a photograph.
            // It just did not arrive this time.
            if (!(await ArtistPortraits.PictureAsync(url, ct)).Answers(out var picture, out _))
                return (await FigureAsync(artist, artist.Name, ct)).InsteadOfUnfetchedPhoto();

            // The grey copy is what a box draws. The colour copy stands in where the drain failed, because a
            // colour photograph under the wash is a better box than an empty one.
            if (picture is not null && (picture.Grey ?? picture.Thumb) is { Length: > 0 } grey)
                return ArtistFace.Photograph(grey, picture.Thumb, picture.Wash, picture.Tint);
        }

        return await FigureAsync(artist, artist.Name, ct);
    }

    /// <summary>
    /// The artist's own lettering for the top of their page, or null where no catalogue holds any.
    ///
    /// <para>Not asked for the compilations shelf, for the same reason nothing else is: it is not a
    /// person, and the catalogues answer about somebody else entirely (see
    /// <see cref="NobodyInParticular"/>). Its title is the words "Various Artists", which is what the
    /// shelf is called.</para>
    /// </summary>
    private Task<byte[]?> LoadArtistLogoAsync(ArtistWallItem artist, CancellationToken ct)
        => NobodyInParticular(artist.Name)
            ? Task.FromResult<byte[]?>(null)
            : ArtistLogos.ForAsync(artist.Name, AlbumAskedAbout(artist), ct);

    /// <summary>The record an artist is asked about. The artwork service answers better for an artist with
    /// one of their albums beside them than for a bare name. The first record is taken from their OWN
    /// records. That includes the records they made with somebody. An artist whose only records are
    /// collaborations still has a record to be asked about.</summary>
    private string? AlbumAskedAbout(ArtistWallItem artist)
        => artist.Own.FirstOrDefault() is { } folder ? AlbumNameFor(folder) : null;

    /// <summary>Every photograph the app can show of one artist, for the band at the top of their page to
    /// page through. The one it already wears comes first, so page one is what is on screen.
    ///
    /// <para>The saved order is used where there is one. It is the order the now-playing carousel was left
    /// in, and the user may have arranged it by hand. Where there is none, the art sources' own list is
    /// used. Both are read exactly as <see cref="ArtistPortraits.ChooseAsync"/> reads them, with the same
    /// list and the same de-duplication. So the picture the band opened on is one of these, not a fifth
    /// thing.</para>
    ///
    /// <para>The pictures themselves are not fetched here. This is a list of addresses. Each one is
    /// downloaded only if the reader pages to it. Opening and reading an artist does not ask for their other
    /// photographs.</para>
    ///
    /// <para><b>Squares only, where the band opened on one.</b> The band opens on the picture that <see
    /// cref="LoadArtistPageFaceAsync"/> chose. That is <see cref="ArtistPortraits.SquareAsync"/>'s answer:
    /// the square of the act from fanart.tv, where there is one. For such an artist the pager shows their
    /// squares and nothing else. The wide backdrops are left out. An artist with no square opens on a
    /// backdrop, and pages through the backdrops. Each one is drawn at the band's width, with its
    /// reflection under it (see <see cref="RunOff"/>). See <see cref="ArtistPortraits.PagesSquares"/> for
    /// why the choice follows the opening picture.</para>
    ///
    /// <para>Either way, the opening picture is put at the front, because the page's pager treats page one
    /// as the picture on screen. Nothing here is saved, so a square cannot reach the backdrop order that
    /// the now-playing carousel keeps.</para>
    /// </summary>
    /// <returns>Unanswered where a lookup did not answer: the page keeps its one picture and does not start
    /// the slideshow, and the next time the artist is opened it asks again.</returns>
    private async Task<Asked<IReadOnlyList<string>>> LoadArtistPicturesAsync(
        ArtistWallItem artist, CancellationToken ct)
    {
        if (NobodyInParticular(artist.Name))
            return Asked.Answer<IReadOnlyList<string>>([]);

        var album = AlbumAskedAbout(artist);
        if (!(await ArtistPortraits.SquareAsync(artist.Name, album, ct)).Answers(out var opened, out var why))
            return Asked.Failed<IReadOnlyList<string>>(why);

        if (!(await ArtistPortraits.SquaresAsync(artist.Name, album, ct)).Answers(out var squares, out why))
            return Asked.Failed<IReadOnlyList<string>>(why);

        if (ArtistPortraits.PagesSquares(opened, squares))
            return Asked.Answer(ArtistPortraits.WithFirst(squares, opened));

        return (await BackdropsForPageAsync(artist, ct)).Then(backdrops => ArtistPortraits.WithFirst(backdrops, opened));
    }

    /// <summary>The artist's backdrops for the band's pager. The saved order is used where there is one,
    /// and the art sources' order where there is not. The backdrop that the rest of the app wears goes at
    /// the front.</summary>
    private async Task<Asked<IReadOnlyList<string>>> BackdropsForPageAsync(ArtistWallItem artist, CancellationToken ct)
    {
        var saved = ArtistPortraits.SavedBackdrops(artist.Name);
        if (saved.Count > 0)
            return Asked.Answer(ArtistPortraits.ShownFirst(artist.Name, saved));

        var album = artist.Albums.Count > 0 ? AlbumNameFor(artist.Albums[0]) : null;
        return (await App.Services.GetRequiredService<ArtworkService>().GetAsync(artist.Name, album, ct: ct))
            .Then(art => ArtistPortraits.ShownFirst(artist.Name, ArtistPortraits.OnePerPicture(art.Backgrounds)));
    }

    /// <summary>A picture MADE for an artist nobody has photographed, out of the colours of the first
    /// record the collection files under them.
    ///
    /// <para>Their own records come first. An appearance is used only where they have none of their own. A
    /// guest on one compilation would otherwise wear that compilation's colours, which they had no part in.
    /// Even so, it is still a record with their name on it, and its colours beat a flat dark
    /// fill.</para>
    ///
    /// <para>Three colours come from the one cover, not one. The dominant colour is the biggest thing in
    /// the picture. The highlight is its most vivid colour. The mean is what the whole sleeve averages to.
    /// These are three different questions, and they disagree on exactly the sleeves that are worth drawing
    /// from. One colour would give a figure of one flat colour.</para>
    ///
    /// <para>The cover is asked for at the row edge, not at full size. It is read for COLOURS, and a
    /// dominant tone does not change with the size of the picture it is read from. A full-size sleeve is a
    /// megabyte or so of decode per box, on a wall that may have hundreds of them.</para>
    /// </summary>
    /// <param name="seed">What the arrangement is drawn from. The same subject then gets the same picture
    /// every time, on every machine. For an artist's box it is the artist's own name. For a genre card it
    /// is the GENRE's name, which keeps two cards drawn from one artist's records from being the same
    /// picture twice. Both callers state it, rather than it defaulting to the artist. Which of the two it
    /// is decides whether a wall repeats itself, so the compiler should make the caller ask.</param>
    private static async Task<ArtistFace> FigureAsync(
        ArtistWallItem artist, string seed, CancellationToken ct)
    {
        // Their own records come first, and an appearance is used only where they have none. Folders orders
        // itself by that rule, so the choice is stated once, there.
        var folder = artist.Folders.FirstOrDefault();

        if (folder is null)
            return ArtistFace.Unknown;

        var cover = await AlbumCovers.LoadAsync(folder, ct, ArtScaler.RowEdge);
        if (cover is not { Length: > 0 })
            return ArtistFace.Unknown;

        // Off the UI thread. This is a decode, three passes over the pixels, two draws and two blurs. It runs
        // while the wall is being scrolled.
        var run = await Cancellable.RunAsync(() =>
        {
            var colours = new List<Color>(3);
            if (CoverColor.Dominant(cover) is { } dominant) colours.Add(dominant);
            if (CoverColor.Highlight(cover) is { } highlight) colours.Add(highlight);
            if (CoverColor.Average(cover) is { } mean) colours.Add(mean);

            if (colours.Count == 0)
                return ArtistFace.Unknown;

            // Two draws, because a box and a band are different shapes. Where Skia will not paint them, the
            // surfaces fall back to a flat fill in the record's own colour.
            return SoftFigure.Box(colours, seed) is { Length: > 0 } box
                   && SoftFigure.Banner(colours, seed) is { Length: > 0 } banner
                ? ArtistFace.Figure(box, banner, RpmPalette.Nearest(colours[0]))
                : ArtistFace.Record(colours[0]);
        }, ct);

        // A run that happened and a face that came back are two separate facts. The match asks for the
        // face by name, so the compiler checks it.
        return run is { Ran: true, Value: { } face } ? face : ArtistFace.Unknown;
    }

    /// <summary>The band at the top of ONE artist's page. It is the wall's own picture for everybody
    /// except the compilations shelf.
    ///
    /// <para><b>Why the page has a loader of its own.</b> The shelf is not a person, and there is no
    /// photograph of it anywhere. So the wall shows the made figure, a soft coloured form. That is a fine
    /// box an inch wide. At four hundred points tall it looks like a page whose picture failed to arrive.
    /// The shelf does have its records, and a grid of their sleeves is a true picture of the shelf, made
    /// from the user's own collection. See <see cref="ShelfMosaic"/>.</para>
    ///
    /// <para><b>And why only here.</b> Reading a dozen covers is a dozen decodes. The wall realizes its
    /// boxes while it is being scrolled. The box also has a name written across it, under which twelve
    /// sleeves are a grey texture. So the wall keeps <see cref="LoadArtistPortraitAsync"/>, and this loader
    /// belongs to the page, which is the only surface that asks for it.</para>
    /// </summary>
    private async Task<ArtistFace> LoadArtistPageFaceAsync(ArtistWallItem artist, CancellationToken ct)
    {
        // The square comes first. The page is about one artist, and fanart.tv's square is framed on them.
        // The box on the wall wears a backdrop (see LoadArtistPortraitAsync). So for an act with a square,
        // the box and the page show two different pictures. That is the intended result.
        if (!NobodyInParticular(artist.Name))
            return await LoadArtistFaceAsync(artist, ArtistPortraits.SquareAsync, ct);

        // The mosaic comes first. The made figure is used only where the mosaic cannot be drawn. That happens
        // when a shelf's records are nearly all without artwork, or when Skia will not paint on this machine.
        // Not both. The figure needs a cover read, three passes over its pixels and two blurred draws. Doing
        // that beside a mosaic that is about to cover it would be work done only to be thrown away.
        return await ShelfFaceAsync(artist, ct) ?? await FigureAsync(artist, artist.Name, ct);
    }

    /// <summary>The one shelf's mosaic, remembered. Leaving the page and coming back does not read a dozen
    /// covers again. It is keyed by the records on the shelf, so an import that adds one redraws the band
    /// rather than showing the old one.
    ///
    /// <para>It is written on the dispatcher and nowhere else. The only caller is the page's band, which
    /// runs there. That is what makes a bare static safe here. It is also the thing the next edit could
    /// break.</para>
    /// </summary>
    private static (string Shelf, ArtistFace Face)? _shelfFace;

    /// <summary>
    /// A picture of the compilations shelf: the sleeves of the records on it, in the shelf's own order.
    ///
    /// <para>It is null where the shelf has too few covers to draw with, or where Skia would not paint. The
    /// caller then falls back to the made figure.</para>
    ///
    /// <para>The covers are asked for at the row edge, like every other read done for colour or for a
    /// thumbnail. The mosaic is drawn at 160 pixels a cell and then blurred again for the page's background.
    /// A full-size sleeve would be a megabyte of decode per cell, thrown away.</para>
    /// </summary>
    private static async Task<ArtistFace?> ShelfFaceAsync(ArtistWallItem artist, CancellationToken ct)
    {
        // A few more folders than cells, because a record with no artwork anywhere cannot fill a cell. A
        // shelf that is half without artwork should still fill the grid.
        var folders = artist.Folders.Take(ShelfMosaic.Cells + 6).ToList();
        if (folders.Count < ShelfMosaic.Least)
            return null;

        var key = string.Join('|', folders);
        if (_shelfFace is { } remembered && remembered.Shelf == key)
            return remembered.Face;

        // Read together, not one after another. Each is a file read on a worker thread. A dozen in a row
        // would be a dozen round trips to the disk while the band is still empty.
        var covers = await Task.WhenAll(
            folders.Select(folder => AlbumCovers.LoadAsync(folder, ct, ArtScaler.RowEdge)));
        if (ct.IsCancellationRequested)
            return null;

        var sleeves = covers.Where(cover => cover is { Length: > 0 }).Select(cover => cover!)
                            .Take(ShelfMosaic.Cells).ToList();
        if (sleeves.Count < ShelfMosaic.Least)
            return null;

        // Off the UI thread: a dozen decodes, a draw and a blur. The wall this page opened over is still
        // live behind it.
        var (made, face) = await Cancellable.RunAsync(() =>
        {
            if (ShelfMosaic.Banner(sleeves) is not { Length: > 0 } banner)
                return null;

            // The first sleeve on the shelf is snapped by the surfaces, exactly as a photograph's tone is.
            // The made figure on the wall's box takes its colours from the same end of the same list. So
            // pressing that box still opens a page in the colour it was.
            return ArtistFace.Shelf(banner, ArtScaler.Wash(banner), CoverColor.Dominant(sleeves[0]));
        }, ct);

        if (!made)
            return null;

        // The reader may be on somebody else's page by now. A mosaic kept under this key would be handed
        // straight back the next time the shelf is opened. So only a finished answer is remembered.
        if (face is not null && !ct.IsCancellationRequested)
            _shelfFace = (key, face);

        return face;
    }

    /// <summary>
    /// Which of the listener's own playlists already hold one of this artist's songs.
    ///
    /// <para><b>The songs, not the name.</b> The page has already decided which of the collection is theirs:
    /// a record filed under them, or a credit that names them on somebody else's. That answer is the one this
    /// shelf is built on. Asking the database about the NAME instead would be a second reading of the same
    /// question. It could disagree with the listing on the same screen.</para>
    ///
    /// <para><b>Both keys.</b> A playlist entry is kept by RPM id, with the relative path as the fallback.
    /// Either can be the only one that still finds a song. The paths are worked out here, not by the page,
    /// because the root they are relative to is the library's.</para>
    ///
    /// <para>This runs off the UI thread. It walks every entry in every playlist.</para>
    /// </summary>
    private static async Task<IReadOnlyList<PlaylistHolding>> LoadArtistPlaylistsAsync(
        IReadOnlyList<RPM.Core.Models.TrackInfo> songs, CancellationToken ct)
    {
        var library = App.Services.GetRequiredService<LibraryService>();

        // The shelf, in both halves. An artist page opened from somebody else's collection is a page about
        // THEIR records. The lists worth naming under it are the ones on that shelf. If this library were
        // asked instead, the songs would be matched against playlist entries that spell a path relative to a
        // different root. The answer would be whatever happened to collide.
        if (!library.IsOpen || library.ShelfRoot is not { } root)
            return [];

        var shelf = library.Shelf;

        var (asked, holding) = await Cancellable.RunAsync(() =>
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var song in songs)
            {
                if (song.RpmId is { Length: > 0 } id)
                    ids.Add(id);

                // Forward-slashed and relative to the root. That is how the mirror spells a path, and how a
                // playlist entry stores one. See PlaylistModel.RelativeTo, which does the same two steps on
                // the way IN.
                paths.Add(Path.GetRelativePath(root, song.FilePath)
                              .Replace(Path.DirectorySeparatorChar, '/'));
            }

            return library.Read(shelf, "the playlists holding an artist's songs",
                mirror => mirror.PlaylistsHolding(ids, paths));
        }, ct);

        // No answer draws no lists. A refusal has already put the problem screen up over the app. A closed
        // mirror is followed by a Changed event that reads the new one.
        return asked && holding is not null
            ? holding.Match(lists => lists, _ => [])
            : [];
    }

    /// <summary>What a record is called, from the shelf's own index: the album's tags where it has them, and
    /// the folder's last segment where it does not.</summary>
    private string? AlbumNameFor(string folder)
    {
        if (Model is { } model && model.LibraryAlbumFacts.TryGetValue(folder, out var facts)
            && !string.IsNullOrWhiteSpace(facts.Album))
            return facts.Album;

        var parts = folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : null;
    }

    // ── the artist page ──

    /// <summary>Opens one artist in the library card, over the wall they were pressed on.
    ///
    /// <para>Not a page in the frame, for the same reason the album page is not one either. The frame is
    /// the whole window below the artwork. Navigating it would take the deck (the transport, the cast pill
    /// and the cloud chip) away with it. An artist is somebody to read about while the music plays.</para>
    /// </summary>
    /// <param name="from">Which page this artist was reached from. It is stated at every call site, rather
    /// than read off what is on screen. The record's page is taken down before this runs, so by the time
    /// anything here could look, the thing worth knowing is already gone.</param>
    private void NavigateToArtistPage(ArtistWallItem artist, ArtistFrom from)
    {
        _artistOpenedFrom = from;
        Panes.OpenRightTab(RightTab.Artists);
        ArtistDetail.Load(artist);
        ShowArtistDetail(true);
    }

    /// <summary>The act on the wall a written credit names, or null where the collection has no page for
    /// it.
    ///
    /// <para>The whole credit is tried first. That is what a tagger wrote, and most of the time it is
    /// exactly somebody's name. "Simon &amp; Garfunkel" is on the wall whole. Failing that, the acts that
    /// the credit NAMES are tried, by the same rule the wall was built with. A record filed under "Calvin
    /// Harris feat. Ellie Goulding" is on both their pages. A reader who presses that line means the artist
    /// whose record it is, which is the first of them.</para>
    ///
    /// <para>It never invents an act. A credit that no act on the wall answers to, such as "Various
    /// Artists" over a compilation, has no page. The surface that asked is expected to draw a name, not a
    /// door.</para>
    /// </summary>
    private ArtistWallItem? ArtistPageFor(string credit)
    {
        if (Model is not { } model || string.IsNullOrWhiteSpace(credit))
            return null;

        var acts = model.ArtistShelf.Acts;
        if (Named(credit) is { } whole)
            return whole;

        foreach (var name in ArtistCredit.Split(credit))
            if (Named(name) is { } part)
                return part;

        return null;

        ArtistWallItem? Named(string name) =>
            acts.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Where an artist's page was opened from, which is where closing it goes back to. The other
    /// half of <see cref="AlbumFrom"/>: a reader who pressed the artist named under a sleeve, or the artist
    /// standing on a genre's page, is one press deep in that page, and back is that page, not a wall of
    /// artists they never came through.
    ///
    /// <para>This is one type, not a flag and a string beside it, for the reason <see cref="AlbumFrom"/>
    /// gives. Two fields that must never both be set are a state that nobody checks. The record travels as
    /// a FOLDER. Its page is closed on the way in (see the ArtistHooks in MainPage.AlbumWall), so nothing
    /// is left holding its name by the time back is pressed. A genre's page is left standing behind its
    /// tab, so its name is not needed.</para>
    /// </summary>
    private readonly record struct ArtistFrom(string? Album, bool Genre, bool AlbumWasHome)
    {
        /// <summary>Off the wall, a search, or a credit. The page closes back onto the artists
        /// wall.</summary>
        public static ArtistFrom Collection => default;

        /// <summary>Out of a record's page. The record is reopened on the way back.</summary>
        /// <param name="folder">The record's folder, or null where its page had none to give. Then back
        /// closes onto the wall, which is what <see cref="ArtistDetail_BackRequested"/> already does for a
        /// record it cannot find.</param>
        /// <param name="fromHome">Whether that record was itself opened off the home screen. Back out of
        /// the reopened record then goes to home. It is carried rather than re-read. The record's page is
        /// closed on the way in here, and closing it drops its own claim.</param>
        public static ArtistFrom AlbumPage(string? folder, bool fromHome) => new(folder, false, fromHome);

        /// <summary>Out of a genre's page, which is in the Genres view. Closing the artist moves the rail
        /// back as well as hiding the page, the same as for a record opened out of a genre.</summary>
        public static ArtistFrom GenrePage => new(null, true, false);
    }

    private ArtistFrom _artistOpenedFrom;

    private void ShowArtistDetail(bool show)
    {
        if (show)
        {
            // On the press, not at the end of the pane's move, see the same line in ShowAlbumDetail.
            PageBackdrop.Reading(ArtistDetail);

            ArtistDetail.Visibility = Visibility.Visible;
            Fade(ArtistDetail, 1);

            Fade(ArtistsWall, 0, then: () =>
            {
                // Only hide it if the artist is still up: a fast back would otherwise collapse the wall
                // the user has just returned to.
                if (ArtistDetail.Visibility == Visibility.Visible)
                    ArtistsWall.Visibility = Visibility.Collapsed;
            });

            Fade(ArtistsHeading, 0, then: () =>
            {
                if (ArtistDetail.Visibility == Visibility.Visible)
                    ArtistsHeading.Visibility = Visibility.Collapsed;
            });

            return;
        }

        if (ArtistDetail.Visibility != Visibility.Visible)
            return;

        // Back to the wall: the window goes back to its own backdrop, but only while this page is still the
        // one being read. A back that lands on a RECORD has already handed the window to that record's page
        // (see ArtistDetail_BackRequested, which opens it before taking this one down). Handing it back
        // here would put the app's wallpaper through the gap between two pages that both have a background.
        if (ReferenceEquals(PageBackdrop.ReadingPage, ArtistDetail))
            PageBackdrop.Reading(_artistOpenedFrom.Genre ? GenreDetail : null);

        // A record opened out of this artist has nothing to go back to once the artist's page is gone. So it
        // closes onto the album collection, like any other (see _albumOpenedFrom). Only THIS page's claim is
        // dropped. A record opened out of a genre is still owed its genre.
        if (_albumOpenedFrom == AlbumFrom.ArtistPage)
            _albumOpenedFrom = AlbumFrom.Collection;

        // Back to the genre this artist was opened out of, if that is where they came from. The Artists view
        // underneath is still put back in order below. It is simply not the one on screen, as the album page
        // does for a record opened out of a genre.
        var toGenre = _artistOpenedFrom.Genre;

        // The page this one was opened out of is not where this exit goes for anybody else. Every caller
        // except the back button lands here: the tab pressed on the rail, a search over the wall, or an
        // artist whose last record was deleted. All of them are asking for the wall.
        _artistOpenedFrom = ArtistFrom.Collection;

        ArtistDetail.Close();

        if (toGenre)
            Panes.OpenRightTab(RightTab.Genres);

        ArtistsWall.Visibility = Visibility.Visible;
        Fade(ArtistsWall, 1);
        ArtistsHeading.Visibility = Visibility.Visible;
        Fade(ArtistsHeading, 1);

        Fade(ArtistDetail, 0, then: () =>
        {
            if (ArtistDetail.Opacity == 0)
                ArtistDetail.Visibility = Visibility.Collapsed;
        });
    }

    /// <summary>Back from an artist: to the record they were named on, where the page was opened out of
    /// one, and to the wall otherwise.
    ///
    /// <para>The record is read before the page is hidden, because hiding it clears the answer. The page is
    /// then put up again, saying it was NOT opened from an artist. That is the truth by then, and it stops
    /// the two pages sending each other back and forth.</para></summary>
    private void ArtistDetail_BackRequested(object? sender, EventArgs e)
    {
        var record = _artistOpenedFrom.Album;
        var recordWasHome = _artistOpenedFrom.AlbumWasHome;
        if (record is { Length: > 0 } && RecordStillOnTheShelf(record))
        {
            // The record's page FIRST, and this one down after it. That order stops the window flashing its
            // own wallpaper on the way. Opening the record puts it back on the backdrop as open and
            // undressed (see AlbumDetailView.PeelGround). That is the one state the window holds its
            // current ground through. So the artist's colours stay up until the record's sleeve lands over
            // them. The record is opened saying where it stands NOW: not out of an artist, since that page
            // is being taken down. It is opened out of home if that is where the reader started. So a
            // second back off the record they came through lands them where they began, not on the album
            // wall.
            NavigateToAlbumPage(record, recordWasHome ? AlbumFrom.HomeScreen : AlbumFrom.Collection);
            ShowArtistDetail(false);
            return;
        }

        ShowArtistDetail(false);
    }

    /// <summary>Whether the collection still holds a record. It is asked at the moment somebody wants to go
    /// back to one, not remembered from when they left it. The record's own menu is on the artist's page,
    /// so the record can be deleted from the very page whose back button points at it. An empty album page
    /// is a worse answer than the wall.</summary>
    private bool RecordStillOnTheShelf(string folder)
    {
        var library = App.Services.GetRequiredService<Services.Library.LibraryService>();
        if (!library.IsOpen)
            return false;
        // No answer means there is no record to go back to. Back then lands on the wall: under the problem
        // screen when the mirror refused, or on the new collection when it was closed for one.
        return library.Read(library.Shelf, "whether a record is still on the shelf",
                shelf => shelf.QueryFolderPage(folder, 0, 1).Count > 0)
            .Match(held => held, _ => false);
    }
}
