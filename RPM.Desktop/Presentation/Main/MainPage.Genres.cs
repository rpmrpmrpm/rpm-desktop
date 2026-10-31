using RPM.Core.Library;
using RPM.Core.Metadata.Providers;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Main.Genres.Models;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using RPM.Desktop.Presentation.Main.RightPane;
using RPM.Desktop.Presentation.Main.Artists.Models;
using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;

namespace RPM.Desktop.Presentation.Main;

/// <summary>The Genres view: the wall of rows, the faces each row is drawn from, and the page that a
/// row opens.
///
/// <para>It uses the same shelf that the album and artist views read, from the same pass over the mirror.
/// A record imported while this view is open lands on its genre at the same moment it lands on the wall
/// of covers.</para>
///
/// <para>The library's search does NOT narrow this view, just as it does not narrow the artists wall. The
/// search field is over the album views, which is what it shows. This wall and the artists wall are ways
/// INTO the collection. A way in that is narrowed by something typed on another view, with no field here
/// to say so, looks like a collection that has lost most of its music.</para>
///
/// <para>The wall shows the app's own genre list, not the tags as typed. See <see cref="GenreWall"/>,
/// where the reason is written out.</para>
/// </summary>
public sealed partial class MainPage
{
    private bool _genresHooked;

    /// <summary>The inputs the wall was last built from. They are kept so that a rebuild which changes
    /// neither is not done twice. See <see cref="FeedGenres"/>.</summary>
    private IReadOnlyList<RPM.Core.Library.ArtistWallItem>? _fedArtists;

    private IReadOnlyDictionary<string, string>? _fedGenres;

    /// <summary>Whether the artist list had been read from the open collection when it was fed.</summary>
    private bool _fedRead;

    /// <summary>The wall as it stands. It is kept rather than rebuilt for each question, because two
    /// things on the genre page ask about the WHOLE wall, not about the one genre they show. The
    /// Subgenres card counts what the collection holds of each related genre. Pressing one of its rows
    /// must open that genre's own item. The wall is built only in <see cref="FeedGenres"/>, so there is
    /// one answer to which records are on which genre.</summary>
    private IReadOnlyList<GenreWallItem>? _wall;

    private void AttachGenres()
    {
        if (_genresHooked || Model is not { } model)
            return;
        _genresHooked = true;

        GenresWall.PortraitLoader = LoadGenreFaceAsync;
        GenresWall.GenreOpened += NavigateToGenrePage;

        // ── the genre's own page ──
        //
        // Each loader on the page is one the app already has, pointed at a genre: the sleeve loader the
        // album wall uses, the portrait loader the artists wall uses, and the one place where a
        // generated list's cover is drawn. A genre's page is a way INTO the collection, not a second
        // copy of it. So nothing here answers a question that something else already answers.
        GenreDetail.BannerLoader = LoadGenreBannerAsync;
        GenreDetail.ShelfLoader = BuildGenreShelfAsync;
        GenreDetail.RelationsLoader = GenreRelations;

        // ...and whether either of those has anything in it. That decides whether the genre gets a
        // column at all. This is asked here rather than on the page, because both lists live out here:
        // the encyclopedia's table of which genres it describes, and the family table that the
        // relations come from. "Other" and "Other / Unknown" are in neither, on purpose. They are where
        // records go when nobody could name them. They are not kinds of music with something written
        // about them.
        GenreDetail.AboutExists = genre =>
            EncyclopediaLookup.Describes(genre.Name) || GenreRelations(genre).Count > 0;
        GenreDetail.ArticleLoader = LoadGenreArticleAsync;
        GenreDetail.CoverLoader = LoadAlbumCoverAsync;
        GenreDetail.PortraitLoader = LoadArtistPortraitAsync;

        // ...and the colour the wall drew this shelf in, so the page header matches the card the reader
        // pressed. See GenreWallView.AccentFor.
        GenreDetail.AccentOnTheWall = GenresWall.AccentFor;
        GenreDetail.ListCoverLoader = LoadGenreListCoverAsync;

        // An artist opened from a genre's page belongs to that page. Closing the artist puts the genre
        // back, as it does for a record opened from one.
        GenreDetail.ArtistOpened += artist => NavigateToArtistPage(artist, ArtistFrom.GenrePage);
        GenreDetail.RelatedOpened += OpenRelatedGenre;
        // A record opened from a genre's page belongs to that page. Closing it puts the genre back,
        // rather than showing an album wall the reader never came through.
        GenreDetail.OpenRecord = folder => NavigateToAlbumPage(folder, AlbumFrom.GenrePage);
        GenreDetail.PlaySong = song => PlayAlbumTrack(song.Track);

        // ...and the button beside that card's heading plays the whole card, in the order it ranks
        // them. This uses the album wall's own queueing, so a set added from here and a set added from
        // a record go into the queue in the same way.
        GenreDetail.PlaySongs = songs =>
            PlayAlbumTracks([.. songs.Select(song => song.Track)], shuffle: false);

        // ...and a generated list opens where playlists live. The page is not closed on the way out, so
        // the genre stays behind the Genres tab. But the rail's own button now asks for the WALL, like
        // every other button on it. So the way back to this genre is the tab being restored by
        // something that knows about it (such as a record closing), not by the rail.
        GenreDetail.OpenList = id =>
        {
            EnsurePlaylistPanel();
            Panes.OpenRightTab(RightTab.Playlists);
            PlaylistPanel.OpenSmart(id, fromHome: false);
        };

        model.LibraryAlbumsChanged += OnGenresChanged;
        Unloaded += (_, _) => model.LibraryAlbumsChanged -= OnGenresChanged;

        FeedGenres();

        void OnGenresChanged() => DispatcherQueue.TryEnqueue(FeedGenres);
    }

    /// <summary>
    /// The wall's genres and the line that counts them.
    ///
    /// <para>Off the WHOLE collection, like the artists wall beside it: this is a way into the library
    /// rather than a view of what the album search matched, and a listener who looked up one record
    /// and then opened Genres should not find their music down to the two rows that record is on.
    /// See LibraryAlbumIndex.AllAlbumGenres.</para>
    ///
    /// <para>Run on every shelf rebuild, which is every library change and every keystroke of the
    /// library's search, and both of the things it reads keep the same instance until the COLLECTION
    /// changes, so a keystroke is answered by finding nothing to do rather than by folding every tag
    /// in the library again.</para>
    /// </summary>
    private void FeedGenres()
    {
        if (Model is not { } model)
            return;

        var shelf = model.ArtistShelf;
        var artists = shelf.Acts;
        var genres = shelf.AlbumGenres;
        var read = model.ArtistsRead;
        if (ReferenceEquals(artists, _fedArtists) && ReferenceEquals(genres, _fedGenres) && read == _fedRead)
            return;

        _fedArtists = artists;
        _fedGenres = genres;
        _fedRead = read;

        var wall = _wall = GenreWall.Build(artists, genres);
        GenresWall.SetGenres(wall, read);

        var shown = GenresWall.Shown;
        GenresSubheading.Text = !read ? ""
            : shown == 0
            ? Loc.Get("Stats_Empty")
            : Loc.Format(shown == 1 ? "Genres_CountOne" : "Genres_Count", shown);

        // A genre that is still open, but that the collection no longer has (its last record was
        // deleted, or it was retagged onto another shelf), has nothing left to show. A genre that
        // survives is opened again, not left alone, because the acts on it are what the page shows, and
        // the reader has just added or removed some.
        if (GenreDetail.Visibility != Visibility.Visible || GenreDetail.Genre is not { } showing)
            return;

        if (wall.FirstOrDefault(g => string.Equals(g.Name, showing.Name, StringComparison.OrdinalIgnoreCase))
                is not { } still)
        {
            ShowGenreDetail(false);
            return;
        }

        // Only reopen where the reading says something new about THIS genre. The wall is rebuilt in
        // full on any library change anywhere. A peer's catalogue arriving rebuilds it several times in
        // a row. Every rebuild makes a new item for every genre, so the check above passes even when
        // the shelf is unchanged. Reopening then replaces every shelf's items and rebuilds the decade
        // shelves in code. A reader who has scrolled into the page would see it empty and refill under
        // them. See GenreWallItem.SameAs for what counts as the same shelf.
        if (still.SameAs(showing))
            return;

        GenreDetail.Load(still);
    }

    // ── what a card is drawn on ──

    /// <summary>The photo that one genre's card shows: one of the acts filed under it, in their own
    /// colours.
    ///
    /// <para><b>One face, not five.</b> No single artist stands for a whole kind of music. Five press
    /// photos across a card a few hundred points wide are five small thumbnails, and a wall of them
    /// looks like texture rather than photographs. The kind of music is now said by the NAME, set in a
    /// face chosen for that genre (see <see cref="GenreWordmarks"/>). That leaves the picture free to
    /// be a picture.</para>
    ///
    /// <para><b>Whose face, and why it is the same one tomorrow.</b> The acts arrive biggest first.
    /// Always taking the first of them would put the same few famous people on the wall, and make a
    /// genre look like one artist's shelf. So the order is shuffled from the GENRE's own name (see
    /// <see cref="SoftFigure.Seed"/>). It is shuffled once and for all: the same collection draws the
    /// same faces on every machine and after every restart. A picture that changed on each launch would
    /// not be a wall, it would be a slideshow.</para>
    ///
    /// <para>Only a handful are tried before the card gives up. Every one that answers is a lookup and a
    /// download, and a genre where nobody has been photographed would otherwise walk two thousand names
    /// on a wall that is showing eighteen cards at once.</para>
    ///
    /// <para><b>The models are not used here, and that was measured rather than assumed.</b> Framing a
    /// candidate the way a playlist's band of faces is framed would run U²-Netp to find the subject,
    /// UltraFace to find the faces, and <c>SaliencyCrop</c> for the window round them. A picture with
    /// nobody in it could then be passed over. But a band is five faces once, and a wall is thirty-six
    /// genres each walking their acts. Each model is one session behind one semaphore. On a borrowed
    /// shelf, where nothing is cached and every act is cold, that made the app impossible to use, not
    /// just slow.</para>
    ///
    /// <para>So the card takes a centre cut of whichever photograph the catalogues settled on. The cost
    /// is stated plainly: a picture that is not OF the artist (a sleeve, a logo, a stage lit from
    /// behind) is not turned away, and a head high in a tall frame can lose its crown. Good framing is
    /// worth having, but not while somebody is waiting for the wall. It belongs in a separate pass made
    /// when nobody is waiting.</para>
    ///
    /// <para>Null where none of them answers. The card is then its colour and its name, which is a
    /// finished card. The colour is the genre's own and never came from the photograph.</para>
    /// </summary>
    private Task<byte[]?> LoadGenreFaceAsync(GenreWallItem genre, CancellationToken ct)
        => GenreFaces.ForAsync(genre, Rolled(genre), GenreCardMetrics.PictureShape, FrameOneAsync, ct);

    /// <summary>The photograph at the head of the genre's page: the one shipped for that shelf, read
    /// from disk.
    ///
    /// <para><b>Not an artist's photograph.</b> The head shows a fixed picture for the genre, not the
    /// act the card shows. Showing the act would need a second walk of the shelf, a second pair of
    /// catalogue lookups and a second cut, all started when the reader presses the genre. Warming them
    /// behind the card hid the cost, but a shelf where none of the acts has been photographed still got
    /// nothing back.</para>
    ///
    /// <para>These pictures are chosen once and cut to this panel (see <see cref="GenrePhotos"/>). So a
    /// page opens on a finished head with no lookup, no model and no network. It is the same head on
    /// every machine, on a borrowed shelf, and offline. The wall's CARDS still show the genre's own
    /// artists, because a card is a face somebody might recognise, and a head is a picture of what the
    /// shelf IS.</para>
    ///
    /// <para>Null where the shelf has no photograph. The head then draws its colour and its name, and no
    /// picture.</para>
    /// </summary>
    private static Task<byte[]?> LoadGenreBannerAsync(GenreWallItem genre, CancellationToken ct)
        => Genres.GenrePhotos.BytesAsync(genre.Name, ct);

    /// <summary>
    /// The first of a genre's acts anybody has a usable photograph of, framed.
    ///
    /// <para>Runs off the UI thread and off a gate (see <see cref="GenreFaces"/>). GenreFaces is the
    /// only caller, and it is the reason both are true. Everything here decodes or re-encodes an image
    /// or runs a model. Each of those steps must stay off the thread that draws the wall.</para>
    ///
    /// <para>The token belongs to the CACHE, never to a card. A card that scrolls away does not cancel
    /// the picture it asked for. It only stops waiting for it. So there is no cancellation to handle
    /// here beyond passing one through, and in practice none is ever passed.</para>
    /// </summary>
    /// <returns>Answered with the framed cut, or with null where none of the candidates has a usable
    /// photograph; unanswered where none was found and at least one candidate's photograph could not be
    /// fetched, because that one may have been the answer. GenreFaces keeps an answered null on disk,
    /// so a failure must never come back as one.</returns>
    private static async Task<Asked<byte[]?>> FrameOneAsync(
        IReadOnlyList<string> candidates, double shape, CancellationToken ct)
    {
        string? failed = null;

        foreach (var artist in candidates)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (!(await ArtistPortraits.ChooseAsync(artist, album: null, ct).ConfigureAwait(false))
                        .Answers(out var chosen, out var unchosen))
                {
                    failed = unchosen;
                    continue;
                }

                if (chosen is not { } url)
                    continue;

                // Use only the small copy from ArtistPortraits.ThumbAsync. A card draws one photograph
                // in its own colours. The drain, the wash and the two colour reads that PictureAsync
                // also does are work this path never uses. The wash alone is a Gaussian blur over a
                // 1920 px press shot, and a wall does it thirty-six times over.
                if (!(await ArtistPortraits.ThumbAsync(url, ct).ConfigureAwait(false)).Answers(out var cut, out var why))
                {
                    failed = why;
                    continue;
                }

                if (cut is not { Length: > 0 } thumb)
                    continue;

                // ── where the head is, which is what makes the card's slot safe ──
                //
                // U²-Netp finds where the subject is, UltraFace finds where the faces are, and
                // SaliencyCrop puts a window of the card's shape around them. The same framing is used
                // for the band of faces on a playlist, which is why FramedAsync is internal rather than
                // private. A centre cut cannot do this: the card's picture is wider than it is tall, a
                // press shot is a portrait, and the middle of a portrait is a chest.
                //
                // What keeps the models affordable is the gate above them: two genres at a time, and
                // one answer per genre for the life of the session (see GenreFaces). The models run on
                // a worker, never on the thread that draws the wall.
                //
                // Null is an answer, and it is the useful one. The detector looked at this picture and
                // found nobody in it. A sleeve, a logo or a stage lit from behind gets no face, so the
                // card tries the next act rather than standing on a photograph of nobody.
                if (await PlaylistCovers.FramedAsync(thumb, shape, ct).ConfigureAwait(false)
                        is not { } framed)
                    continue;

                return Asked.Answer<byte[]?>(
                    await Task.Run(() => PlaylistArt.Cut(framed, shape), ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // No answer for this candidate. The card takes whichever candidate does answer. A card
                // that ends with no answer is not the same as one where nobody has a photograph.
                failed = $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        return failed is null
            ? Asked.Answer<byte[]?>(null)
            : Asked.Failed<byte[]?>(failed);
    }

    /// <summary>How many of a genre's acts a card asks about before it settles for no
    /// photograph.</summary>
    private const int GenreFaceCandidates = 6;

    /// <summary>The acts a genre's card may be drawn from, in an order shuffled from the genre's name.
    ///
    /// <para>Drawn from the biggest names on the shelf rather than from all of them. An act with one
    /// track on a compilation is on the genre, but it is not what the genre looks like. The list is
    /// then shuffled, so two genres that share their biggest act do not share a face.</para>
    /// </summary>
    private static IReadOnlyList<string> Rolled(GenreWallItem genre)
    {
        var pool = genre.Artists.Take(GenreFaceCandidates * 3).Select(a => a.Name).ToArray();

        // A Fisher-Yates shuffle, seeded from the genre's name and nothing else. Random.Shared is not
        // used. It is seeded per process, so it would deal a different order on every launch, and this
        // order must stay the same.
        var roll = SoftFigure.Seed(genre.Name) | 1u;
        for (var at = pool.Length - 1; at > 0; at--)
        {
            roll ^= roll << 13;
            roll ^= roll >> 17;
            roll ^= roll << 5;

            var swap = (int)(roll % (uint)(at + 1));
            (pool[at], pool[swap]) = (pool[swap], pool[at]);
        }

        return [.. pool.Take(GenreFaceCandidates)];
    }

    // ── what the page's shelves hold ──

    /// <summary>Everything one genre's six shelves hold, collected in one pass over the library, on a
    /// worker.
    ///
    /// <para><b>Off the UI thread, and that is not a precaution.</b> This is arithmetic over
    /// dictionaries the shelf already holds, plus two indexed queries. That holds for the collection on
    /// this machine, whose mood cut and genre fold were built at startup by the push that sends the
    /// lists to the cloud. It does not hold for a shelf borrowed from somebody else. Nothing has warmed
    /// that shelf, so the first generated list asked for on it builds <c>SessionMoods</c> (three
    /// whole-table reads and a sort) and <c>FoldersOnGenre</c> (a fourth read and a fold of every tag
    /// in the collection) before it can answer. All of that would land on the thread drawing the page,
    /// which is why a borrowed shelf is noticeably slower than one's own.</para>
    ///
    /// <para>What is read HERE, on the thread that asked, and what is worked on out there. The shelf,
    /// the album facts and the words are read here, because the index replaces them wholesale, so a
    /// snapshot taken now cannot change under the worker. The mirror is resolved here, because a worker
    /// has no business reaching into the container. Everything after that is the worker's.</para>
    ///
    /// <para>The page shows a spinning record while this runs, and keeps the shelves it had before a
    /// rebuild. See <c>GenreDetailView.FillShelvesAsync</c>.</para>
    /// </summary>
    private Task<GenreShelf> BuildGenreShelfAsync(
        GenreWallItem genre,
        Library.Models.AlbumTileMetrics sleeves,
        ArtistTileMetrics boxes,
        CancellationToken ct)
    {
        if (Model is not { } model)
            return Task.FromResult(GenreShelf.Empty);

        var shelf = model.ArtistShelf;
        var facts = model.LibraryAlbumFacts;
        var library = App.Services.GetRequiredService<Services.Library.LibraryService>();
        var words = GenreWords.Read();

        return Task.Run(
            () => BuildGenreShelf(genre, shelf, facts, library, words, sleeves, boxes), ct);
    }

    /// <summary>
    /// The same page's worth of shelves, built from what it was handed and reaching for nothing else.
    ///
    /// <para>Static, and every one of its arguments is there to keep it that way: it runs on a worker, so
    /// it may not read <see cref="Model"/>, resolve a service or ask the resource table for a word. What
    /// it needs of those is taken on the UI thread by <see cref="BuildGenreShelfAsync"/> and handed
    /// in.</para>
    /// </summary>
    private static GenreShelf BuildGenreShelf(
        GenreWallItem genre,
        Library.Models.LibraryAlbumIndex.ArtistShelf shelf,
        IReadOnlyDictionary<string, RPM.Core.Cloud.AlbumFacts> facts,
        Services.Library.LibraryService library,
        GenreWords words,
        Library.Models.AlbumTileMetrics sleeves,
        ArtistTileMetrics boxes)
    {
        var mine = new HashSet<string>(genre.Folders, StringComparer.OrdinalIgnoreCase);

        // The album facts narrowed to this genre. Off the WHOLE collection's facts rather than the
        // search's, for the reason the artist page gives: a page opened while a word is typed in the
        // library's field must not be a page missing most of its records.
        var added = shelf.AlbumFacts
            .Where(a => mine.Contains(a.Key))
            .OrderByDescending(a => a.Value.AddedAt)
            .ThenBy(a => a.Key, StringComparer.OrdinalIgnoreCase)
            .Take(RowLength)
            .Select(a => Sleeve(shelf, facts, words, a.Key, sleeves))
            .ToList();

        var acts = genre.Artists.Take(RowLength)
                                .Select(a => new ArtistTile(a, boxes))
                                .ToList();

        return new GenreShelf(
            added,
            acts,
            PopularIn(genre, library),
            GenreLists(genre, SmartPlaylists.EssentialForAGenre, library, words, sleeves),
            GenreLists(genre, SmartPlaylists.BrokenOutByGenre, library, words, sleeves),
            ErasIn(genre, shelf, facts, words, sleeves));
    }

    /// <summary>
    /// The handful of sentences the shelves are captioned with, read off the resource table before the
    /// worker starts.
    ///
    /// <para>Here rather than at each call site because of where those call sites now run. The table is
    /// read on the UI thread and the captions are composed on a worker, so what crosses the line is the
    /// TEMPLATE, "{0} songs", and never the loader itself.</para>
    /// </summary>
    /// <param name="SongsOne">"1 song".</param>
    /// <param name="Songs">"{0} songs".</param>
    /// <param name="Decade">"{0}s", over a shelf of one decade's records.</param>
    private readonly record struct GenreWords(string SongsOne, string Songs, string Decade)
    {
        public static GenreWords Read() => new(
            Loc.Get("Stats_SongsOne"), Loc.Get("Stats_Songs"), Loc.Get("Genres_Decade"));

        /// <summary>How many songs, as a sentence, the one place the singular is chosen.</summary>
        public string Counted(int songs) => string.Format(
            System.Globalization.CultureInfo.CurrentCulture, songs == 1 ? SongsOne : Songs, songs);

        public string Era(int decade) => string.Format(
            System.Globalization.CultureInfo.CurrentCulture, Decade, decade);
    }

    /// <summary>How many records a shelf holds. The row scrolls sideways, so this is not limited by
    /// what fits on screen. It is how far down each order is worth going before the answer stops being
    /// useful. It is the home screen's own number, because it is the same question asked of a smaller
    /// shelf.</summary>
    private const int RowLength = HomeShelf.RowLength;

    /// <summary>How many songs the popular shelf lists: twenty-four, laid out as three columns of eight
    /// (see <c>GenreDetailView.FillPopular</c>). The card is as tall as one column of eight. The query
    /// returns one song per record, so the twenty-four songs come from twenty-four different records of
    /// the genre, not a deeper cut of the same few.</summary>
    private const int PopularSongs = 24;

    /// <summary>One record, as a card. The artist page's own caption, the year and the track count,
    /// because a record on that page and the same record here should not say two different things.</summary>
    private static ArtistRecord Sleeve(
        Library.Models.LibraryAlbumIndex.ArtistShelf shelf,
        IReadOnlyDictionary<string, RPM.Core.Cloud.AlbumFacts> facts,
        GenreWords words,
        string folder,
        Library.Models.AlbumTileMetrics metrics)
    {
        var year = shelf.AlbumFacts.TryGetValue(folder, out var album) ? album.Year : 0;
        var tracks = shelf.TrackCounts.TryGetValue(folder, out var count) ? count : 0;
        var marked = shelf.ExplicitAlbums.Contains(folder);

        // Without its edition, the way the wall's tiles and the artist page's shelves print a record
        // (see TitleAsides.NameOnly). A sleeve on a shelf is a picture with two short lines under it, and
        // three pressings of one record would otherwise be three lines that differ only in their
        // brackets. Nothing is hidden: the drawer this sleeve opens prints the whole title.
        var named = facts.TryGetValue(folder, out var known) && !string.IsNullOrWhiteSpace(known.Album)
            ? known.Album
            : folder.Split('/', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } parted
                ? parted[^1]
                : folder;
        var title = TitleAsides.NameOnly(named);

        var parts = new List<string>(2);
        if (year > 0)
            parts.Add(year.ToString(System.Globalization.CultureInfo.CurrentCulture));
        if (tracks > 0)
            parts.Add(words.Counted(tracks));

        return new ArtistRecord(folder, title, string.Join("  \u2022  ", parts), marked, metrics);
    }

    /// <summary>
    /// The songs of the genre the world plays most.
    ///
    /// <para>What the WORLD plays rather than what this listener does, which is the one ranking a genre's
    /// page can offer on a collection nobody has played yet, see
    /// <c>LibraryDatabase.QueryPopularUnder</c>. The listener's own plays are on the same page, in the
    /// generated lists beside this.</para>
    ///
    /// <para>Empty is ordinary and takes the shelf down: the world's play counts arrive record by record
    /// on a background sweep, so a fresh library has none of them yet.</para>
    /// </summary>
    private static IReadOnlyList<ArtistSong> PopularIn(
        GenreWallItem genre, Services.Library.LibraryService library)
    {
        if (!library.IsOpen || library.ShelfRoot is not { } root)
            return [];

        // A failed read takes the shelf down with the rest. A refusal has put the problem screen up
        // over the app. A closed mirror is followed by a Changed event, which reads the new one.
        return library.Read(library.Shelf, "a genre's popular songs",
                shelf => shelf.QueryPopularUnder(genre.Folders, PopularSongs))
            .Match<IReadOnlyList<ArtistSong>>(
                rows => [.. rows.Select(row => Song(row.ToTrackInfo(root), row.Folder))],
                _ => []);
    }

    /// <summary>One popular song, carrying the folder its record is in. The folder comes off the mirror's
    /// own row rather than being cut back out of the file path: the query already grouped by it, and the
    /// card beside the song's name is fetched by it.</summary>
    private static ArtistSong Song(RPM.Core.Models.TrackInfo track, string folder) => new(
        track,
        folder,
        string.IsNullOrWhiteSpace(track.Artist) ? track.Album ?? "" : track.Artist!,
        SongLength(track.DurationSeconds));

    private static string SongLength(double seconds)
    {
        if (seconds <= 0 || double.IsNaN(seconds))
            return "";

        var length = TimeSpan.FromSeconds(Math.Round(seconds));
        return length.TotalHours >= 1
            ? $"{(int)length.TotalHours}:{length.Minutes:00}:{length.Seconds:00}"
            : $"{length.Minutes}:{length.Seconds:00}";
    }

    /// <summary>
    /// The generated lists for one genre, of whichever set was asked for.
    ///
    /// <para>Only the ones with enough in them to be a list, by the same floor the playlists tree
    /// applies (<see cref="SmartPlaylists.LeastToShow"/>), a card that opened onto four songs would be
    /// a playlist in name only. So a genre the listener has never played has no essential lists and the
    /// shelf is simply not drawn, which is the honest answer.</para>
    /// </summary>
    private static List<GenreList> GenreLists(
        GenreWallItem genre,
        IReadOnlyList<SmartPlaylistKind> kinds,
        Services.Library.LibraryService library,
        GenreWords words,
        Library.Models.AlbumTileMetrics metrics)
    {
        if (!library.IsOpen)
            return [];

        // One reading of the clock for the whole shelf, as the playlists pane does: lists that answered
        // to two different "now"s would be a row that disagrees with itself about what is recent.
        var now = DateTime.UtcNow.Ticks;
        var cards = new List<GenreList>(kinds.Count);

        var shelf = library.Shelf;
        foreach (var kind in kinds)
        {
            // A mood square that was dropped is no list, here as anywhere. See MoodSets.
            if (MoodSets.IsDropped(kind, genre.Name))
                continue;

            var id = SmartPlaylistId.Within(kind, genre.Name);

            // A refusal is the whole shelf's, not this list's: the problem screen is up over the app,
            // and nothing is offered under it.
            if (!library.Read(shelf, "a genre's list sizes", mirror => mirror.SmartPlaylistTotals(id, now))
                       .Answers(out var totals, out _))
                return [];

            if (totals.Tracks < SmartPlaylists.LeastToShow(id))
                continue;

            cards.Add(new GenreList(
                id,
                SmartPlaylistFace.Name(id),
                words.Counted(totals.Tracks),
                metrics));
        }

        return cards;
    }

    /// <summary>
    /// The genre's records by decade, oldest decade first, newest record first inside each.
    ///
    /// <para>Records are used, not playlists. See <see cref="GenreEraShelf"/> for why. A decade with
    /// nothing in it is not drawn. So a genre whose collection holds only this century's music shows
    /// two shelves, not six with four empty ones.</para>
    ///
    /// <para>Off the shelf's own years, which is the reading the album wall captions its sleeves with: a
    /// record whose year the mirror does not know is on no decade rather than on the earliest, because a
    /// guess here would put every untagged record in the sixties.</para>
    /// </summary>
    private static IReadOnlyList<GenreEraShelf> ErasIn(
        GenreWallItem genre,
        Library.Models.LibraryAlbumIndex.ArtistShelf shelf,
        IReadOnlyDictionary<string, RPM.Core.Cloud.AlbumFacts> facts,
        GenreWords words,
        Library.Models.AlbumTileMetrics metrics)
    {
        var byDecade = new Dictionary<int, List<(string Folder, int Year)>>();

        foreach (var folder in genre.Folders)
        {
            if (!shelf.AlbumFacts.TryGetValue(folder, out var made) || made.Year <= 0)
                continue;

            var decade = made.Year / 10 * 10;
            if (!byDecade.TryGetValue(decade, out var records))
                byDecade[decade] = records = [];
            records.Add((folder, made.Year));
        }

        return [.. byDecade
            .OrderBy(era => era.Key)
            .Select(era => new GenreEraShelf(
                words.Era(era.Key),
                [.. era.Value
                    .OrderByDescending(record => record.Year)
                    .ThenBy(record => record.Folder, StringComparer.OrdinalIgnoreCase)
                    .Take(RowLength)
                    .Select(record => Sleeve(shelf, facts, words, record.Folder, metrics))]))];
    }

    /// <summary>
    /// The genres this one stands next to, narrowed to the ones the collection actually holds something
    /// of and ordered by how much.
    ///
    /// <para>Which genres are related at all is <see cref="RPM.Core.Metadata.Genres.Related"/>'s answer.
    /// It is a fact about music, not about this shelf. What this adds is the second half: how much of
    /// each is here. A card listing a kind of music the collection has none of would be an encyclopedia
    /// entry rather than a way through the library. Every row on it is a link into the collection.</para>
    /// </summary>
    private IReadOnlyList<GenreRelation> GenreRelations(GenreWallItem genre)
    {
        if (RPM.Core.Metadata.Genres.Related(genre.Name) is not { Count: > 0 } related)
            return [];

        // Counted off the wall as it stands, which is the one place the app decides which records are on
        // which genre, so the card and the wall cannot disagree.
        var held = (_wall ?? []).ToDictionary(
            g => g.Name, g => g.Folders.Count, StringComparer.OrdinalIgnoreCase);

        return [.. related
            .Where(held.ContainsKey)
            .Select(name => new GenreRelation(
                name,
                held[name],
                Loc.Format(held[name] == 1 ? "Genres_SubgenreAlbumOne" : "Genres_SubgenreAlbums", held[name])))
            .OrderByDescending(relation => relation.Albums)
            .ThenBy(relation => relation.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>What Wikipedia says about a kind of music. Only the English article is used. See
    /// <see cref="EncyclopediaLookup.GenreArticleAsync"/> for the reason.
    ///
    /// <para>This goes through the track info service rather than calling the lookup directly. The
    /// lookup is built in that service's constructor, with its HttpClient and user agent. There is no
    /// <see cref="EncyclopediaLookup"/> in the container to resolve.</para>
    ///
    /// <para>The result is null when the encyclopedia has no article or is not answering. The card is
    /// then not drawn.</para></summary>
    private static async Task<EncyclopediaArticle?> LoadGenreArticleAsync(string genre, CancellationToken ct)
    {
        try
        {
            return await App.Services.GetRequiredService<Services.Catalog.TrackInfoService>()
                            .GenreArticleAsync(genre, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;   // an article is augmentation; the column is finished without it
        }
    }

    /// <summary>One generated list's square, out of the one place a generated list's cover is ever
    /// drawn, so a card here and the head of that list's own tracklist show the same
    /// picture.</summary>
    private static async Task<byte[]?> LoadGenreListCoverAsync(SmartPlaylistId id, CancellationToken ct)
    {
        try
        {
            return await GeneratedCover.SquareAsync(id, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;   // the card keeps the made figure it opened with
        }
    }

    /// <summary>Opens one of the genres named in the Subgenres card. Off the wall as it stands rather
    /// than built here: a genre's page is the wall's own item, and one assembled at this call site
    /// would be a second answer about which records are on it.</summary>
    private void OpenRelatedGenre(string name)
    {
        if ((_wall ?? []).FirstOrDefault(
                g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)) is { } genre)
            NavigateToGenrePage(genre);
    }

    // ── the genre page ──

    /// <summary>Opens one genre in the library card, on top of the wall it was pressed on. It is not a
    /// page in the frame, for the same reason the artist page is not one. The frame is the whole window
    /// below the artwork. Navigating it would also remove the deck: the transport, the cast pill and
    /// the cloud chip.</summary>
    private void NavigateToGenrePage(GenreWallItem genre)
    {
        RPM.Desktop.Diagnostics.SwitchDiag.Begin($"genre {genre.Name}");
        GenreDetail.Load(genre);
        RPM.Desktop.Diagnostics.SwitchDiag.Mark("load");
        ShowGenreDetail(true);
        RPM.Desktop.Diagnostics.SwitchDiag.Mark("fade");
    }

    /// <summary>The genre's page up, or down and the wall back. The artists wall's own arrangement beside
    /// it: the page takes the panel whole, heading included, because the heading names the collection and
    /// means nothing over one kind of music.
    ///
    /// <para>The window's backdrop follows the page, as it does for an artist. The page stands on the
    /// colour of its own head photograph, and while it is open the window's backdrop uses that colour
    /// too.</para></summary>
    private void ShowGenreDetail(bool show)
    {
        if (show)
        {
            // Set on the press, not at the end of the pane's move. The artists wall does the same.
            PageBackdrop.Reading(GenreDetail);

            GenreDetail.Visibility = Visibility.Visible;
            Fade(GenreDetail, 1);

            Fade(GenresWall, 0, then: () =>
            {
                // Only hide it if the genre is still up: a fast back would otherwise collapse the wall
                // the user has just returned to.
                if (GenreDetail.Visibility == Visibility.Visible)
                    GenresWall.Visibility = Visibility.Collapsed;
            });

            Fade(GenresHeading, 0, then: () =>
            {
                if (GenreDetail.Visibility == Visibility.Visible)
                    GenresHeading.Visibility = Visibility.Collapsed;
            });

            return;
        }

        if (GenreDetail.Visibility != Visibility.Visible)
            return;

        // Back to the wall: the window returns to its own backdrop, but only while this page is still
        // the one being read. If a back has already handed the window to another page, it must not be
        // taken away again. The artists wall has the same guard.
        if (ReferenceEquals(PageBackdrop.ReadingPage, GenreDetail))
            PageBackdrop.Reading(null);

        // A record or an artist opened from this genre has nothing to go back to once the genre's page
        // is gone. So each one closes onto its own collection, as any other does. Only this page's
        // claims are dropped.
        if (_albumOpenedFrom == AlbumFrom.GenrePage)
            _albumOpenedFrom = AlbumFrom.Collection;
        if (_artistOpenedFrom.Genre)
            _artistOpenedFrom = ArtistFrom.Collection;

        GenreDetail.Close();

        GenresWall.Visibility = Visibility.Visible;
        Fade(GenresWall, 1);
        GenresHeading.Visibility = Visibility.Visible;
        Fade(GenresHeading, 1);

        Fade(GenreDetail, 0, then: () =>
        {
            if (GenreDetail.Opacity == 0)
                GenreDetail.Visibility = Visibility.Collapsed;
        });
    }

    /// <summary>Back from a genre is the wall of genres. There is nowhere else it can be: a genre's page
    /// is only ever reached from the wall, an artist opened out of it goes to the artists view, which
    /// has its own way back.</summary>
    private void GenreDetail_BackRequested(object? sender, EventArgs e) => ShowGenreDetail(false);
}
