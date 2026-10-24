using RPM.Core.Import;
using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Core.Metadata.Providers;
using RPM.Core.Models;
using RPM.Core.Threading;
using RPM.Desktop.Presentation.Dialogs;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using RPM.Desktop.Presentation.NowPlaying;
using RPM.Desktop.Services;
using RPM.Desktop.Strings;
using RPM.Fingerprint.Catalog;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// The way from a credit on the album page to the artist's own page.
///
/// <para>Both halves are needed together, because a door is only drawn where it opens onto something.
/// The page names the record's artist as the tags spell it. The collection's wall is built from credits
/// that are SPLIT into the acts they name. So "Inna" is a door and "Various Artists" is not. Only the wall
/// knows which is which. The check is made before the name is drawn, not after it is pressed. A link that
/// does nothing is worse than a name that never claimed to be one.</para>
/// </summary>
/// <param name="Exists">Whether the collection has a page for this credit.</param>
/// <param name="Open">Opens it. Only ever called for a credit <paramref name="Exists"/> said yes to.</param>
public sealed record ArtistPageHooks(Func<string, bool> Exists, Action<string> Open);

/// <summary>
/// The album page: the record on the left, what it holds in the middle, what everybody thought of it
/// on the right.
///
/// <para>This is a view inside the library card, not a page over the window. Opening an album replaces the
/// tracklist or the wall and nothing else. The artwork band above it and the deck below it belong to the
/// app, not to what is being looked at. A record that swept them away would stop the music it is about.</para>
///
/// <para>There are two halves with two sources. The left and middle come from the user's own library: the
/// folder, its tags, its cover and the rating they gave it. They draw with no network at all. The right is
/// RPM's shared wall for the same record. It fills in when the API answers. The page is useful before that,
/// and it stays useful if it never happens. A failed review fetch leaves an album page, not an error page.</para>
///
/// <para>The join between the two is <see cref="RpmCommunityClient.ResolveAlbumAsync"/>. It is one call that
/// turns this folder's tags into the id that everyone else's copy resolves to. When the user is signed out,
/// the read-only lookup is used instead. It finds an existing entry, but it never creates one.</para>
/// </summary>
public sealed partial class AlbumDetailView : UserControl
{
    private const int PageSize = 10;

    private string _folder = "";
    private IReadOnlyList<TrackInfo> _tracks = [];

    /// <summary>The record's title as it was tagged, split into the name the heading shows and the edition
    /// drawn under it. It is kept whole, not read back off the heading. The heading does not carry the edition.
    /// A review filed under "Short N' Sweet" is a review of a different pressing from one filed under
    /// "Short N' Sweet (Deluxe)".</summary>
    private AlbumTitle _title = AlbumTitle.Split("");

    /// <summary>The library-relative folder the page is showing, or "" before the first load. The host needs
    /// it when something changes a record: it checks whether the record that changed is the one on screen.</summary>
    public string Folder => _folder;

    private RpmAlbum? _album;
    private RpmReviewSort _sort = RpmReviewSort.Votes;
    private int _page = 1;

    /// <summary>Cancels the loads of the album being left. Without this, an answer about the last record could
    /// land on the page that shows the next one.</summary>
    private CancellationTokenSource? _loading;

    public AlbumDetailView()
    {
        InitializeComponent();
        _lineBreath = new Common.CloudLineBreath(
            () => TrackList.ItemsSource as IEnumerable<AlbumTrackItem> ?? []);

        BuildGrooveTabs();
        WatchTrackHems();
        WatchCorner();
        WatchPapersHead();
        WatchShelf();

        // What the two empty tabs say before a record is on the page. It changes with the record after that.
        // See ShowGrooveEmpty.
        ShowGrooveEmpty(linked: true);

        // A page that nobody is looking at must not keep a timer running for a mark on it.
        Unloaded += (_, _) => _lineBreath.Stop();
    }

    // ── the groove's tabs ──

    /// <summary>
    /// Builds the strip over the groove's panels. It is the same control the metadata editors use, for the same
    /// gesture: one tab is up, and picking another swaps what is under it.
    ///
    /// <para>It is built once, in the constructor, not for each record. The tabs are the same for every album.
    /// Rebuilding them on each load would send the reader back to Reviews every time they opened a record from
    /// the suggestions row while reading the comments on the last one.</para>
    /// </summary>
    /// <summary>The groove's own strip. It is kept, because on a card too narrow for the groove to have its own
    /// column, the strip over the tracks drives it from outside (see AlbumDetailView.Density).</summary>
    private Dialogs.Controls.RailTabStrip? _grooveStrip;

    private void BuildGrooveTabs()
    {
        var strip = new Dialogs.Controls.RailTabStrip(
            // A star for the verdicts and a speech bubble for the talk. These are the same two glyphs that the
            // rail beside the sleeve uses for writing one of each.
            new(GrooveTabs.Reviews, "Album_ReviewsHeading", "\uf005"),
            new(GrooveTabs.Comments, "Album_CommentsHeading", "\uf075"));

        strip.Register(GrooveTabs.Reviews, ReviewsPanel);
        strip.Register(GrooveTabs.Comments, CommentsPanel);

        // The sort key belongs to the reviews, not to the groove. Comments are a conversation and run in the
        // order they were had. A tab with nothing to sort shows no sort control.
        strip.SelectionChanged += tab => SortSlot.Visibility = tab == GrooveTabs.Reviews
            ? Visibility.Visible
            : Visibility.Collapsed;

        GrooveTabHost.Child = strip;
        strip.Select(GrooveTabs.Reviews);
        _grooveStrip = strip;
    }

    /// <summary>The timer for the record's track marks. It is the shared timer that the wall's tiles and the
    /// playlist rows use, so nothing is seen out of step with anything else.</summary>
    private readonly Common.CloudLineBreath _lineBreath;

    /// <summary>
    /// Re-reads the cloud mark on every line of the record being shown. This is the page's half of the wall's
    /// badge refresh, on the same signal.
    ///
    /// <para>It reads straight from the mirror, not through a loader. The page reads its own record from the
    /// library anyway, and one folder's rows is the same query that it already runs to draw the list.</para>
    /// </summary>
    public void RefreshTrackBadges()
    {
        if (_folder.Length == 0 || TrackList.ItemsSource is not IReadOnlyList<AlbumTrackItem> lines)
            return;

        var library = Library;
        if (!library.IsOpen || library.ShelfRoot is not { } root)
            return;

        AlbumTrackItem.ApplyCloud(
            lines, [.. library.Shelf.FolderTracks(_folder).Select(t => t.ToTrackInfo(root))],
            Cloud.UploadsStopped, Shelf);
        _lineBreath.Sync();
    }

    private static LibraryService Library => App.Services.GetRequiredService<LibraryService>();

    /// <summary>The cloud sync engine. The page asks it one thing: whether anything is going up. A queued line
    /// under a full store is waiting, not working, so it must not show as busy.</summary>
    private static Services.Cloud.CloudSyncService Cloud =>
        App.Services.GetRequiredService<Services.Cloud.CloudSyncService>();

    /// <summary>Whose collection this record is on, which is what its cloud marks mean. It is asked for each
    /// time, not stored. A page can stay open while the computer that lends it goes off the network, and the
    /// answer changes with that.</summary>
    private static RPM.Core.Cloud.ShelfReading Shelf =>
        Library.ViewingPeer is not { } lender ? RPM.Core.Cloud.ShelfReading.Own
        : App.Services.GetRequiredService<Services.Sharing.SharingService>().IsReachable(lender)
            ? RPM.Core.Cloud.ShelfReading.BorrowedFromOnline
            : RPM.Core.Cloud.ShelfReading.BorrowedFromOffline;

    private static RPaMpService Amp => App.Services.GetRequiredService<RPaMpService>();
    private static AuthService Auth => App.Services.GetRequiredService<AuthService>();
    private static RpmCommunityClient Community => App.Services.GetRequiredService<RpmCommunityClient>();

    /// <summary>
    /// The design harness is running, so the wall comes from <see cref="Gallery.GalleryCommunity"/> instead of
    /// the API.
    ///
    /// <para>Only the community half is replaced. The record itself, its tracks, its sleeve and the listener's
    /// own stars come from the seeded library, the same as with a real one. So what is shown under
    /// <c>--gallery</c> is the album page itself, not a picture of it.</para>
    /// </summary>
    private static bool Mock => Gallery.GalleryMode.Active;

    /// <summary>Whether there is an account behind a write. The harness has one, but it is made of nothing. When
    /// signed out, every button on the right half of the page is drawn disabled. A page shown that way has half
    /// its design hidden.</summary>
    private static bool SignedIn => Mock || Auth.IsLoggedIn;

    /// <summary>Raised by the view's own back link. The host decides what "back" means. Here it is the library
    /// view that the album was opened from.</summary>
    public event EventHandler? BackRequested;

    /// <summary>Drops whatever is still loading. The host calls this when the view is put away. This stops an
    /// answer about the album just left from landing on the album opened next.</summary>
    public void Close()
    {
        _loading?.Cancel();

        // …and so is the sleeve read. The token above does not cover it. That read is stamped rather than
        // cancelled (see PeelGround), and a stamp is only dropped by a newer one. If it kept running, it would
        // land on a page nobody is looking at and re-dress it. The album just left would then give the window its
        // colours from behind the page that replaced it.
        _paint++;

        // The window goes back to its own backdrop as the page goes. The page's own layers are left standing,
        // because the page is fading out as a whole, and a ground peeled away under that fade would be seen
        // leaving twice. What the page lent the app behind it is no longer about anything on screen.
        PageBackdrop.Closed(this);
    }

    // ── the record ──

    /// <summary>
    /// Shows a library-relative album folder. It is safe to call again for another album, and safe to call
    /// again for the SAME one. See <see cref="AlbumOpen"/>, which is what the second argument is for, and why
    /// it has no default.
    /// </summary>
    public void Load(string folder, AlbumOpen how)
    {
        var library = Library;
        if (string.IsNullOrEmpty(folder) || !library.IsOpen || library.ShelfRoot is not { } root) return;

        var db = library.Shelf;
        // Disc order, not file-name order. Otherwise a multi-disc album interleaves its discs.
        _tracks = AlbumTracklist.InDiscOrder(db.FolderTracks(folder).Select(t => t.ToTrackInfo(root)).ToList());
        if (_tracks.Count == 0) return;

        _folder = folder;

        // The last record's promoted review and its suggestions are not this record's. Both are filled by loads
        // that land later. If they stayed in place, they would be read as this album's until those loads land.
        //
        // On a re-read they ARE this record's. The record has not changed, only what the shelf says about it has.
        // Taking them down here would blank two panels that already have the right answer, then put them back a
        // second later from a lookup that returns the same thing.
        if (how is AlbumOpen.Fresh)
        {
            _hasTopReview = false;
            _hasSimilar = false;
            ShowTopReview();
            ShowSimilar();
        }

        // A record borrowed from another person's computer keeps the whole rail. Nothing here writes to the
        // borrowed files. But a review, a comment and a report are about the ALBUM. They go to RPM's own wall,
        // which is the same wall whichever disk the copy sits on.
        //
        // The heart is kept too. A borrowed record has a row here, in the mirror held for that computer. The
        // mark on it is the listener's own. It records what they think of a friend's record. It is kept at this
        // desk for as long as the record is on the shelf, and it is never sent back.
        var borrowed = library.IsPeerView;

        var first = _tracks[0];
        var parts = folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var title = !string.IsNullOrWhiteSpace(first.Album) ? first.Album!
            : parts.Length > 0 ? parts[^1] : library.ShelfRoot;
        var artist = first.AlbumArtist ?? first.Artist ?? (parts.Length >= 2 ? parts[^2] : "");

        // The heading is the record's name. The edition goes on its own line under it. The whole title is kept,
        // because everything this page passes ON needs the record's name as it is tagged, not as it is drawn.
        // The heading over a review and the subtitle built below both use it. The title is masked only on the way
        // to the blocks, in the same way as the lyric sheet. See TitleCensor. _title itself is not changed, for
        // the reason above.
        _title = AlbumTitle.Split(title);
        TitleText.Text = TitleCensor.Shown(_title.Name);
        EditionText.Text = string.Join("  ·  ", _title.Editions);
        EditionText.Visibility = _title.Editions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Any track counts, not most of them. One explicit song is a thing worth knowing about a record.
        ExplicitMark.Visibility = _tracks.Any(t => t.IsExplicit)
            ? Visibility.Visible
            : Visibility.Collapsed;
        // The badge is part of what the name is cut to, and it is decided here, after the name is
        // written: the width has to be asked for again or the record before this one's answer stands.
        FitName();

        var facts = new List<string>(3);
        if (first.Year > 0) facts.Add(first.Year.ToString());
        facts.AddRange(AudioFormat.Describe(
            first.Extension, first.Channels, first.CodecDescription,
            first.BitsPerSample, first.SampleRate, first.BitrateKbps));
        PaintSubtitle(artist, facts);

        // The year is what tags carry. A full release date and a catalogue number come from the linked Discogs
        // release. Until there is one, those lines stay hidden.
        ReleasedText.Text = first.Year > 0 ? $"{Loc.Get("Album_Released")} {first.Year}" : "";
        ReleasedText.Visibility = first.Year > 0 ? Visibility.Visible : Visibility.Collapsed;
        CatalogText.Visibility = Visibility.Collapsed;

        // There is nothing yet, and there may never be anything. The copyright is in the files, not in the
        // mirror. So the card stays hidden until a tag read returns one.
        ShowCopyright(null);
        _ = FillCopyrightAsync(folder, _tracks);

        LoadCover(root);
        // A record marked classical, or one filed under Classical or Opera, is drawn as works and movements.
        // The work tags alone decide nothing (see AlbumTracklist.ClassicalByGenre). Every other record is a
        // plain numbered list.
        TrackList.ItemsSource = AlbumTrackItem.Build(
            _tracks, db.GetAlbumMeta(folder).Classical == true || AlbumTracklist.ClassicalByGenre(_tracks),
            Cloud.UploadsStopped, Shelf);

        // The star on the track the world plays most is a mark ON those lines, so it waits for them to exist.
        // The record is named here, from the tags in hand. It is not read from _identity. That is filled by the
        // community lookup further down, and it still holds the album this page was showing a moment ago.
        _ = FillPopularAsync(
            folder,
            artist,
            // The record's name without the edition on the end: "Lost on You", not "Lost on You (Deluxe)". The
            // catalogue searches for the record by its name. It files the pressing as a separate fact.
            _title.Name is { Length: > 0 } named ? named : title);

        // A record opened while some of its tracks are uploading starts its timer straight away. The marks were
        // given to the lines as they were built.
        _lineBreath.Sync();

        // The equaliser is set on a record opened while it is playing. This is asked here, rather than pushed in
        // by whoever opened the page. The lines have only just been made. What is playing is correct whenever it
        // is asked.
        ApplyPlaying();

        var meta = Mine(db.GetAlbumMeta(folder), borrowed);
        ApplyMeta(meta);

        // The page is opened at whatever size the card is. The rung is chosen before the record is drawn, not on
        // the first resize after it.
        ApplyDensity();

        // The corner it shares with the pane on the left, if that pane is open. The corner is part of the ground
        // this record arrives on, not something that happens to it. See ShowCorner.
        ShowCorner(animate: false);

        // The four lookups below ask about the words this record answers to. A re-read that leaves those words
        // alone is one the catalogues have already answered. A sleeve has been chosen, a heart set, or a song
        // deleted. Asking again would spend a MusicBrainz request, which is limited to one a second, to be told the
        // same thing. It would also blink two filled panels. A re-read that changes the words is a different
        // question, and it is asked afresh. Fixing a misspelled artist is an example.
        var words = (artist, title, first.Year);
        var asking = how is AlbumOpen.Fresh || words != _asked;
        _asked = words;

        // The face the page was drawn from, so the shelf's next change has something to compare against. It is
        // written here, at the end, because this is the point where what is on screen and what is on the shelf
        // agree. See OnShelfChanged.
        _face = FaceOf(_tracks, root, folder);

        if (!asking)
            return;

        _loading?.Cancel();
        _loading = new CancellationTokenSource();
        var ct = _loading.Token;

        // Four loads, none waiting on the others. The record is already drawn, and whatever arrives late fills in
        // around it. The papers are the slowest of the four by a wide margin, because MusicBrainz allows one
        // request a second. That is why they are not awaited, and why they paint from the cache before they ask
        // anybody anything.
        _ = LoadCommunityAsync(artist, title, (int)first.Year, meta.Catno, ct);
        _ = LoadReleaseFactsAsync(folder, ct);
        _ = LoadSimilarAsync(title, artist, ct);
        _ = PapersAsync(artist, title, _tracks.Count, ct);
    }

    /// <summary>The words the catalogues were last asked about. A re-read can then tell a record that has been
    /// RENAMED from one that has only been touched. The default is for a page that has shown nothing. No real
    /// record matches it.</summary>
    private (string Artist, string Title, uint Year) _asked;

    // ── the pressing: catalogue number and release date, off the linked Discogs release ──

    /// <summary>
    /// Fills the two lines tags cannot carry, and keeps what it finds.
    ///
    /// <para>The result is cached on the album's row. It is not fetched each time the page opens. A release
    /// lookup uses the user's own Discogs rate limit, and a catalogue number does not change. An album with no
    /// linked release shows no lines. Nothing is searched for on the user's behalf here. Guessing at a pressing
    /// is what the release picker is for.</para>
    /// </summary>
    private async Task LoadReleaseFactsAsync(string folder, CancellationToken ct)
    {
        var library = Library;
        if (!library.IsOpen) return;

        var meta = library.Shelf.GetAlbumMeta(folder);
        _pressing = meta;
        ShowPressing(meta);

        // Only Discogs is read for facts here. A MusicBrainz link already brought its catalogue number in through
        // the import. Asking a second catalogue for what the first one supplied would spend a rate limit to learn
        // nothing.
        if (meta.DiscogsReleaseId is not { Length: > 0 } releaseId) return;

        // Already asked. Draw what was kept, and spend nothing.
        //
        // The edition decides, and it is the last of these facts to be asked for. A record whose catalogue number
        // was cached before this page could describe a pressing has never been asked the question. The catalogue
        // number alone would wrongly say that it had. An empty edition means Discogs describes the record in no
        // words at all. That is an answer. Null means nobody has asked yet.
        if (meta.Edition is not null)
        {
            ShowRelease(meta.Catno, meta.Released);
            _styles = [];
            return;
        }

        try
        {
            // Nothing back means Discogs cannot be asked at the moment. That happens when the user is signed out, or
            // no account is connected. Nothing is written in that case. The edition stays null, so opening this
            // record once they are signed in asks the question properly, rather than showing a blank that the page
            // never revisits.
            if (await App.Services.GetRequiredService<DiscogsService>().ReleaseAsync(releaseId, ct)
                    is not { } json)
                return;
            if (ct.IsCancellationRequested) return;

            var facts = DiscogsJson.ParseReleaseFacts(json);

            // The release's values are used, falling back to what the row already holds. This read is the one that
            // fills in the barcode. A release that names no barcode must not wipe the barcode that the import wrote
            // from the file's own tags.
            library.Shelf.SetAlbumRelease(
                folder, facts.Catno ?? meta.Catno, facts.Released ?? meta.Released,
                facts.Barcode ?? meta.Barcode);

            // An empty edition, not null, is written when Discogs describes the pressing in no words. Null means the
            // question was never asked, and it is what brings the page back here next time.
            library.Shelf.SetAlbumEdition(folder, facts.Edition ?? "");

            // These are kept as well as shown. They are the only words anywhere that say what a record is, beyond its
            // one broad shelf label. They arrive in a read that has already been paid for, and MoodFlavour needs them
            // (see SetReleaseMeta). They are stored against the RELEASE. That way this page and the background sweep
            // write the same row, and a corrected link takes the answer with it. An empty list is still written. It
            // means the record was asked and filed under no style. That keeps the sweep from coming back to this
            // record for ever.
            library.Shelf.SetReleaseMeta(DiscogsJson.ProviderId, releaseId, facts.Genres, facts.Styles);
            library.Shelf.SetAlbumStyles(folder, facts.Styles);
            _styles = facts.Styles;

            if (_folder != folder) return;

            ShowRelease(facts.Catno, facts.Released);

            // The row is read again, now that it holds all four values. The well draws the ids and the description
            // from it. This is the moment they became true.
            _pressing = library.Shelf.GetAlbumMeta(folder);
            ShowPressing(_pressing);

            // The catalogue number is the sharpest thing RPM matches a pressing on. It arrived after the entry was
            // already asked for. Asking again with it merges this copy onto the wall that somebody else's copy,
            // tagged differently, started.
            if (facts.Catno is { Length: > 0 })
                await LoadCommunityAsync(_identity.Artist, _identity.Title, _identity.Year, facts.Catno, ct);
        }
        catch (Exception)
        {
            // No connected account, no network, or a rate limit. The page keeps the year from the tags.
        }
    }

    /// <summary>Artist, title and year of the album on screen. They are kept so the shared entry can be asked for
    /// again once the catalogue number arrives, or once somebody signs in.</summary>
    private (string Artist, string Title, int Year) _identity;

    /// <summary>The catalogue number the entry was last asked for with, if one is known.</summary>
    private string? _catno;

    /// <summary>What Discogs files this record under, once the release has been read. It feeds the similar row's
    /// search. It is empty until then, which is why that row searches by artist first.</summary>
    private IReadOnlyList<string> _styles = [];

    /// <summary>The album's own row as the page last read it, showing which pressing it is linked to, if any. It
    /// is kept so the link control knows whether it offers a first link or a change.</summary>
    private AlbumMeta? _pressing;

    /// <summary>
    /// Draws the pressing block: what this record is linked to, or that it is not.
    ///
    /// <para>An unlinked album is the ordinary case for anything that did not come from a catalogue: a vinyl
    /// rip, a DVD rip, or an album typed in by hand. So the empty state says what linking gives the user. It does
    /// not treat the state as an error.</para>
    /// </summary>
    private void ShowPressing(AlbumMeta meta)
    {
        var linked = meta.IsLinkedToPressing;

        // What this page may offer about that link. It depends on whose shelf the record is on as much as on the
        // link itself. The rule is in LinkOffer, and everything below reads it from there rather than testing the
        // shelf again. It is asked for each time, like every other borrowed question on this page, because the
        // collection on screen can change under it.
        var offer = LinkOffer.For(linked, borrowed: Library.IsPeerView);

        // Who has not linked it, where the record belongs to somebody else and they are known by name. Otherwise
        // it is the bare fact. PeerNames returns what has been fetched so far and never waits. This page does not
        // subscribe to it. So a name that arrives after the record is drawn shows up the next time the record is
        // opened.
        var lender = offer.NameTheLender ? Services.Sharing.PeerNames.Lender() : "";
        NotLinkedText.Text = lender.Length > 0
            ? Loc.Format("Album_NotLinkedShared", lender)
            : Loc.Get("Album_NotLinked");
        NotLinkedText.Visibility = linked ? Visibility.Collapsed : Visibility.Visible;

        ShowGrooveEmpty(linked);
        ShowGroove(offer.Groove);

        LinkPressingButton.Visibility = offer.LinkKey ? Visibility.Visible : Visibility.Collapsed;
        LinkPressingText.Text = Loc.Get(linked ? "Album_ChangeRelease" : "Album_FindRelease");
        ToolTipService.SetToolTip(LinkPressingButton, LinkPressingText.Text);

        // The card is laid out around the key. Taking the key away does not change the card's width. So the card
        // keeps the shape it was made for, a button that is no longer there, which leaves a row of air along its
        // foot. See FitPressingWell, which has a shape for a card with no key.
        FitPressingWell(PressingWell.ActualWidth);

        // Which catalogue this copy is linked to, as a fact about the LINK. The MusicBrainz id is not printed here.
        // It is a line of machinery in the middle of a card about a record, and a reader cannot act on it. The one
        // id worth printing is Discogs', because it is what a person searches with. It has its own line below.
        ShowIdentifiedOn(
            musicBrainz: meta.MusicBrainzReleaseId is { Length: > 0 },
            discogs: meta.DiscogsReleaseId is { Length: > 0 });

        ShowFact(DiscogsIdText, "Album_DiscogsId", meta.DiscogsReleaseId);
        ShowFact(BarcodeText, "Details_Barcode", meta.Barcode);
        ShowFact(PressingFormatText, "Album_Format", meta.Edition);
    }

    /// <summary>One "Label: value" line of the pressing well. It is hidden, not blank, where the record has no such
    /// value. The well is a stack, and an empty line would leave a gap in it.</summary>
    private static void ShowFact(TextBlock line, string labelKey, string? value)
    {
        var said = !string.IsNullOrWhiteSpace(value);
        line.Text = said ? $"{Loc.Get(labelKey)}: {value}" : "";
        line.Visibility = said ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// "Identified on SonoVault", "Identified on Discogs", or both catalogues on the one line.
    ///
    /// <para>The catalogues are named, not translated. They are the places themselves, and a reader looking for
    /// the entry searches for the name on the site.</para>
    ///
    /// <para>SonoVault is what the screen says. The id in the tag is a MusicBrainz release id. That is not a
    /// mismatch to correct here. The catalogue's data reaches RPM under SonoVault's licence, so SonoVault is the
    /// name the app shows. The parameter keeps the tag's own name, because that is what the field in the file is
    /// called.</para>
    /// </summary>
    private void ShowIdentifiedOn(bool musicBrainz, bool discogs)
    {
        var on = new List<string>(2);
        if (musicBrainz) on.Add("SonoVault");
        if (discogs) on.Add("Discogs");

        AliasesText.Text = on.Count == 0 ? "" : Loc.Format("Album_IdentifiedOn", string.Join(", ", on));
        AliasesText.Visibility = on.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The copyright off the files' own tags, in a card of its own under the pressing.</summary>
    private void ShowCopyright(string? copyright)
    {
        var said = !string.IsNullOrWhiteSpace(copyright);
        CopyrightText.Text = said ? $"{Loc.Get("Details_Copyright")}: {copyright}" : "";
        CopyrightWell.Visibility = said ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// What the two empty tabs say, which depends on whether the record is linked to a pressing.
    ///
    /// <para>An unlinked album has no shared page that anybody's writing can appear on. So "No reviews yet, be
    /// the first" would invite a reader to write into a place nobody else can find. What the reader can usefully
    /// do instead is link the record. The empty tab is where that is worth saying, because it is the one place
    /// that somebody is already looking when they find nothing there.</para>
    ///
    /// <para>The pressing card does not repeat this line. An explanation beside a catalogue number would show
    /// before anybody has asked the question, so the card leaves it out.</para>
    /// </summary>
    private void ShowGrooveEmpty(bool linked)
    {
        // The tab's own glyph shows while the tab is empty for its own reason. The broken link shows when it is
        // empty for a reason that has nothing to do with reviews or comments. The template strikes through
        // whichever one applies. See GrooveEmptyTemplate.
        var invitation = linked ? Loc.Get("Groove_BeTheFirst") : "";
        var unlinked = Loc.Get("Album_NotLinkedHelp");

        // The key under the invitation belongs to the reviews tab alone. It only appears where there is something
        // to invite. An unlinked record is told to link itself, and the door for that is in the pressing card,
        // beside the record it is about.
        ReviewsEmpty.Content = new Models.GrooveEmptyState(
            linked ? "\uf005" : "\uf0c1",
            linked ? Loc.Get("Album_NoReviews") : unlinked,
            invitation,
            linked ? Loc.Get("AlbumMenu_WriteReview") : null);
        CommentsEmpty.Content = new Models.GrooveEmptyState(
            linked ? "\uf075" : "\uf0c1",
            linked ? Loc.Get("Album_NoComments") : unlinked,
            invitation,
            Action: null);
    }

    /// <summary>
    /// The ids the shared entry answers to, used to fill the lines this machine has no answer for.
    ///
    /// <para>It only fills in. It never overwrites. An id on the entry came from somebody's copy of this pressing.
    /// That is good enough to print when this copy has none. It is not good enough to print over the number in
    /// this library's own row, which is the one about the record on the shelf.</para>
    ///
    /// <para>The "identified on" line above is not changed. It says which catalogue THIS copy is linked to.
    /// Another owner's id does not mean that this copy is linked to anything.</para>
    /// </summary>
    private void ShowSharedIds(IReadOnlyList<RpmAlbumAlias> aliases)
    {
        if (DiscogsIdText.Visibility != Visibility.Visible)
            ShowFact(DiscogsIdText, "Album_DiscogsId", Alias("discogs"));
        if (BarcodeText.Visibility != Visibility.Visible)
            ShowFact(BarcodeText, "Details_Barcode", Alias("barcode"));

        string? Alias(string ns) => aliases
            .FirstOrDefault(a => string.Equals(a.Namespace, ns, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    /// <summary>The names of the other pressings whose reviews have appeared on this page, by album id. They are
    /// kept for the life of the view. A reader paging through an album's reviews meets the same handful of
    /// records again and again.</summary>
    private readonly Dictionary<string, string> _pressingNames = new(StringComparer.Ordinal);

    /// <summary>
    /// What to print beside a review written about a different pressing of this album. For a review about the
    /// record on screen, it is empty.
    /// </summary>
    /// <remarks>It is the catalogue number where there is one, because that is what tells two pressings apart on
    /// a shelf. Otherwise it is the year. A record that has neither is not worth a badge that says nothing.</remarks>
    private async Task<string> PressingNameAsync(string albumId, CancellationToken ct)
    {
        if (_album is null || albumId == _album.AlbumId) return "";
        if (_pressingNames.TryGetValue(albumId, out var known)) return known;

        var name = "";
        try
        {
            if (Mock)
                name = Gallery.GalleryCommunity.Album(albumId)?.Catno ?? "";
            else if (await Community.GetAlbumAsync(albumId, ct) is { } other)
                name = other.Catno is { Length: > 0 } catno ? catno
                    : other.Year > 0 ? other.Year.ToString()
                    : "";
        }
        catch (Exception)
        {
            // A name nobody could fetch is a badge nobody sees. The review itself is already drawn.
        }

        _pressingNames[albumId] = name;
        return name;
    }

    /// <summary>Every catalogue the shared entry answers to. That is more than this machine knows. An id from
    /// somebody else's copy can name this record too. Seeing it lets a user check that they are on the page for
    /// the record they own.</summary>
    private async Task RefreshAliasesAsync(string albumId, CancellationToken ct)
    {
        try
        {
            var aliases = await Community.GetAlbumAliasesAsync(albumId, ct);
            if (ct.IsCancellationRequested || aliases.Count == 0) return;

            ShowSharedIds(aliases);
        }
        catch (Exception)
        {
            // The lines this machine already knows stay on screen.
        }
    }

    /// <summary>
    /// Links this album to a pressing the user picks.
    ///
    /// <para>The picker is the one that the rip and split paths already use, so a record is identified the same
    /// way whichever way it came in. What is new is the second half. The pick is written to the album's row
    /// <i>and</i> sent to the shared entry. That moves this copy onto the page that other owners of the same
    /// record are already on.</para>
    /// </summary>
    private async void LinkPressing_Click(object sender, RoutedEventArgs e)
    {
        if (_folder is not { Length: > 0 } folder || !Library.IsOpen || XamlRoot is not { } xamlRoot)
            return;

        // Both halves of this need the account. The catalogues are searched on the user's own session, and the
        // pick is then sent to the shared entry. The account is checked before the picker opens, not after it is
        // filled in.
        if (!await Dialogs.SignInGate.AllowAsync(xamlRoot, Loc.Get("Login_ForLookup")))
            return;

        // Asks for the catalogue service itself, not the page model that happens to hold one. The page model is
        // built per page with ActivatorUtilities and was never registered, so asking the container for one threw.
        var catalog = App.Services.GetRequiredService<CatalogService>();

        // Linking a pressing and naming this record's tracks from it are often one errand. So the picker offers the
        // second as a step, not as another trip through the menus. The offer starts UNticked. What the button says
        // it does is link a pressing. Rewriting twelve titles should not happen just because somebody wanted the
        // catalogue number.
        //
        // These rows are made here, and nothing else holds them. So this is the only write to them. That is what
        // makes the step safe here. The files are re-filed under the naming template, and the page reloads onto
        // wherever they landed.
        var rows = LibraryRowsForFill(folder);
        var step = rows.Count == 0
            ? Dialogs.Models.TrackMetadataStep.None
            : Dialogs.Models.TrackMetadataStep.For(
                rows, ticked: false, commit: () => WriteFilledRowsAsync(folder, rows));

        var picked = await Dialogs.ReleaseLookupDialog.ShowAsync(
            xamlRoot, catalog, step, Array.Empty<double>(),
            _identity.Title, _identity.Artist, ReleaseMedia.Any,
            country: null, year: _identity.Year > 0 ? (uint)_identity.Year : 0);

        if (picked is null) return;

        var db = Library.Shelf;
        db.SetAlbumPressing(folder, picked.Release.Ref.Provider, picked.Release.Ref.Id);

        // The catalogue number came back with the pick, so it is kept without a second lookup. It is also the
        // sharpest thing the shared entry matches on.
        var meta = db.GetAlbumMeta(folder);
        if (picked.Release.CatalogNumber is { Length: > 0 } catno && meta.Catno is null)
            db.SetAlbumRelease(folder, catno, meta.Released, meta.Barcode);

        _pressing = db.GetAlbumMeta(folder);
        ShowPressing(_pressing);
        ShowRelease(_pressing.Catno, _pressing.Released);

        try
        {
            _album = await Community.LinkAlbumAsync(
                _identity.Artist, _identity.Title, _identity.Year,
                _pressing.Catno, _pressing.DiscogsReleaseId,
                _pressing.MusicBrainzReleaseId, _pressing.ReleaseGroupId,
                barcode: _pressing.Barcode, discId: _pressing.DiscId);

            db.SetAlbumRpmId(folder, _album.AlbumId);
            _catno = _pressing.Catno ?? _catno;

            ApplyAlbumNumbers();
            await RefreshAliasesAsync(_album.AlbumId, CancellationToken.None);
            await RefreshReviewsAsync(CancellationToken.None);
            await RefreshCommentsAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            // The link is written locally either way. Only the shared half failed.
            await Note(Loc.Get("Album_LinkedFailed"));
        }
    }

    /// <summary>This record's files as editable rows. These are what the picker's track step fills in. The list is
    /// empty when the shelf is not open, which is also when there is nothing to offer.</summary>
    private IReadOnlyList<TrackRow> LibraryRowsForFill(string folder)
    {
        var library = Library;
        if (!library.IsOpen || library.ShelfRoot is not { } root)
            return [];

        // Never on a borrowed shelf. What is on screen then is somebody else's record, held on somebody else's
        // disk. The mirror under it has no music beneath it at all (see LibraryService.Shelf, which is the read
        // side and only the read side). Rows made from it would name files that are not here. The write would then
        // look for them under this library's own root. So the offer is not made.
        if (library.IsPeerView)
            return [];

        // An unanswered offer gives no rows. A refusal has put the problem screen up over the app.
        return library.Read(library.Shelf, "a record's rows for the track step",
                shelf => shelf.FolderTracks(folder))
            .Match<IReadOnlyList<TrackRow>>(
                tracks => [.. tracks.Select(t => new TrackRow(t.ToTrackInfo(root)))],
                _ => []);
    }

    /// <summary>
    /// Writes rows the track step filled: tags into the files, each file re-filed under the naming
    /// template, the mirror rows moved with them.
    ///
    /// <para>A record whose name or artist has just changed lands in a different folder, and this page is showing
    /// the old one. The folder that still has tracks in it is the one to reload from. The write returns both ends
    /// for exactly this reason.</para>
    /// </summary>
    private async Task WriteFilledRowsAsync(string folder, IReadOnlyList<TrackRow> rows)
    {
        var touched = await Dialogs.MassEdit.WriteHandMadeRowsAsync(rows);
        if (!Library.IsOpen)
            return;

        var landed = touched.FirstOrDefault(f => !string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        var next = Library.Shelf.FolderTracks(folder).Count > 0 || landed is null ? folder : landed;

        // The same record with its tags rewritten, or a different folder that the edit moved the songs into. The
        // first is a re-read. It asks the catalogues again by itself if the words changed, which a naming edit is
        // exactly the case for. The second is another record, and it is opened as one.
        Load(next, string.Equals(next, folder, StringComparison.OrdinalIgnoreCase)
            ? AlbumOpen.Again
            : AlbumOpen.Fresh);
    }

    /// <summary>
    /// The copyright line, read off the files themselves.
    ///
    /// <para>It is read from the disk, not from the mirror. The mirror has no column for it. It is one string per
    /// record that nothing sorts, searches or matches on. A column for it would be written on every import, for the
    /// sake of one line on one page.</para>
    ///
    /// <para>The first track that carries one gives the answer for the record. A copyright is a fact about the
    /// release, printed once on the sleeve. A compilation whose tracks each name their own licensor still has a
    /// first line that is the true one for the album.</para>
    ///
    /// <para>Abandoned silently if the page moved on while the disk was being read.</para>
    /// </summary>
    private async Task FillCopyrightAsync(string folder, IReadOnlyList<TrackInfo> tracks)
    {
        // A handful of tracks, not the whole record. A rip whose first tracks carry no copyright is not worth
        // twenty file reads to find out that none of them do.
        var paths = tracks.Take(5).Select(t => t.FilePath).Where(File.Exists).ToList();
        if (paths.Count == 0)
            return;   // a record that is entirely in the cloud has no tags here to read

        var copyright = await Task.Run(() =>
        {
            foreach (var path in paths)
            {
                try
                {
                    if (TagReader.Read(path).Copyright is { Length: > 0 } said)
                        return said;
                }
                catch
                {
                    // unreadable, gone, or a format TagLib will not open: the next track answers
                }
            }
            return null;
        });

        if (_folder != folder)
            return;   // Another record is on the page now. This line is about the record that left.

        ShowCopyright(copyright);
    }

    /// <summary>
    /// Puts the star on the song this record is best known for.
    ///
    /// <para>The mirror's row answers first, and usually on its own. The lookup costs three calls to two
    /// rate-limited services. So it is made once per record, and the title is kept. An empty answer is still an
    /// answer. It means the record was asked about, and no one song stood out. That stops a record with no clear
    /// favourite being asked about on every visit.</para>
    ///
    /// <para>Lines are matched by name, not by number, because the two lists are not the same list. The
    /// catalogue's tracklist can be an edition that the shelf does not hold, in another order. A rip can also be
    /// missing a song. Names are compared with allowances for different spellings (NameMatch). That is what makes
    /// the catalogue's "When We’re High" match the file's "When We're High".</para>
    ///
    /// <para>Abandoned silently if another record is on the page by the time an answer arrives.</para>
    /// </summary>
    /// <param name="artist">Who the shelf says made it, and <paramref name="album"/> what the shelf calls it. The
    /// caller states both, from the tags it has just read. This runs while the page is still being built. The
    /// page's own idea of which record it shows is not filled in until the community lookup lands.</param>
    private async Task FillPopularAsync(string folder, string? artist, string? album)
    {
        if (!Library.IsOpen)
            return;

        var meta = _pressing ?? Library.Shelf.GetAlbumMeta(folder);

        // What is already known goes up at once, whether or not it is about to be asked again. A star
        // that blinks off for three seconds every week while the services are re-asked would be the
        // refresh made visible, and there is nothing in it for the reader to see.
        if (meta.PopularTrack is { } known)
            MarkPopular(known);

        // A borrowed shelf is read, never written. The lender's own row already carries their answer where they
        // have one. Asking on their behalf would write to a mirror that is not this machine's to write.
        //
        // The gallery asks nobody anything. Its records are scripted, and a screenshot run must not depend on two
        // services being up.
        if (Library.IsPeerView || Mock)
            return;

        // It was answered recently enough. What a record is best known for changes over years. So a week between
        // questions is already far finer than the thing being measured.
        if (meta.PopularTrack is not null
            && DateTime.UtcNow.Ticks - meta.PopularAskedUtcTicks < PopularityGoodFor.Ticks)
            return;

        var asked = DateTime.UtcNow.Ticks;

        var answer = await App.Services.GetRequiredService<Services.Catalog.PopularityService>()
            .MostPlayedTrackAsync(
                artist, album, _tracks.Count, meta.MusicBrainzReleaseId, CancellationToken.None);

        if (!Library.IsOpen)
            return;

        // Nobody at the catalogues was reached. RPM's own listeners can still answer, for a record whose shared
        // entry this library knows. Their answer is about the same thing, counted the same way. So it is worth
        // exactly as much as the answer that could not be fetched.
        if (answer is PopularTrackAnswer.Unavailable)
            answer = await RpmListenersAsync(meta.RpmAlbumId);

        // If the listeners had nothing either, nothing is known that was not known before. Writing here would make
        // one bad minute permanent. For example, a 503 from MusicBrainz would become "this record has no favourite",
        // and the star would never come back. The next visit asks again.
        if (answer is PopularTrackAnswer.Unavailable)
            return;

        var title = answer is PopularTrackAnswer.Found found ? found.Title : "";

        // The answer is kept whatever it says, including an empty answer. That is the record that would otherwise
        // be asked about on every visit. It is stamped with the moment the question was sent, not the moment it was
        // answered. A lookup that took four seconds is not four seconds fresher for having been slow.
        try { Library.Shelf.SetAlbumPopularTrack(folder, title, asked); }
        catch (Exception) { /* a store that will not take it means the next visit asks again */ }

        if (_folder != folder)
            return;   // another record is on the page, and this star is not about it

        if (_pressing is { } row && row.Folder == folder)
            _pressing = row with { PopularTrack = title, PopularAskedUtcTicks = asked };

        MarkPopular(title);
    }

    /// <summary>
    /// What RPM's own listeners play on this record, used when the catalogues could not be asked.
    ///
    /// <para>This is the last resort, on purpose. It is the same measure (plays, added up), but over a far
    /// smaller group of listeners. So it needs more before it will say anything. A song has to have been played
    /// a handful of times, and lead the rest. Otherwise a record that two people have heard would hand out stars
    /// on one listen each.</para>
    ///
    /// <para>It is only used for a record whose shared entry is already known here. Resolving one to answer this
    /// would create a catalogue row for a record that nobody has looked at. That is not this feature's job.</para>
    /// </summary>
    private async Task<PopularTrackAnswer> RpmListenersAsync(string? rpmAlbumId)
    {
        if (rpmAlbumId is not { Length: > 0 } albumId || Mock)
            return PopularTrackAnswer.NotAsked;

        try
        {
            var played = await Community.GetAlbumPlaysAsync(albumId, CancellationToken.None);
            if (played.Count == 0)
                return PopularTrackAnswer.NotAsked;   // Nobody here has played it either, so ask again

            // The server returns the list in order, most played first. The position stands in for the track
            // number, and it only settles ties.
            return PopularTrackAnswer.Of(TrackPopularity.MostPlayedOf(
                [.. played.Select((p, i) => (p.Title, i, p.Plays))],
                TrackPopularity.AlbumPageLead,
                TrackPopularity.RpmListenersMinimum));
        }
        catch (Exception)
        {
            return PopularTrackAnswer.NotAsked;
        }
    }

    /// <summary>How long an answer about the world's listening is treated as current. That is one week. The thing
    /// measured moves over years, and the question costs four calls to two rate-limited services. A listener who
    /// leaves an album page open all evening should also not be able to tell that anything is refreshing behind it.</summary>
    private static readonly TimeSpan PopularityGoodFor = TimeSpan.FromDays(7);

    /// <summary>Puts the star on the line whose name this is, and takes it off every other line. It is keyed by
    /// item, not by row. The lines are rebuilt on every load, and the line this is about may have been replaced
    /// while the answer was in the air.</summary>
    private void MarkPopular(string? title)
    {
        if (TrackList.ItemsSource is not IEnumerable<AlbumTrackItem> lines)
            return;

        // Only the FIRST line that answers to the name gets the star. A shelf can hold the same song twice on one
        // record. For example, a rip can sit next to an older copy of itself, or a single can be tacked onto the end
        // of the album it came from. Two stars would read as two different songs, each being the one the world plays
        // most.
        var wanted = !string.IsNullOrWhiteSpace(title);
        var marked = false;
        foreach (var line in lines)
        {
            var mine = wanted && !marked && line.IsTrack && NameMatch.Same(line.Title, title);
            line.IsMostPopular = mine;
            marked |= mine;
        }
    }

    private void ShowRelease(string? catno, string? released)
    {
        if (!string.IsNullOrWhiteSpace(released))
        {
            ReleasedText.Text = $"{Loc.Get("Album_Released")} {FormatReleaseDate(released)}";
            ReleasedText.Visibility = Visibility.Visible;
        }

        if (!string.IsNullOrWhiteSpace(catno))
        {
            CatalogText.Text = $"{Loc.Get("Album_CatalogId")}: {catno}";
            CatalogText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Discogs gives a date as <c>2005-06-06</c> or as <c>2005</c>. The first is written in the reader's
    /// own locale's date format. The second is left as the year it is.</summary>
    private static string FormatReleaseDate(string released)
        => DateTime.TryParse(released, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var date) && released.Length > 4
            ? DateText.Long(date, System.Globalization.CultureInfo.CurrentCulture)
            : released;

    // ── more like this ──

    private async Task LoadSimilarAsync(string title, string artist, CancellationToken ct)
    {
        var library = Library;
        if (!library.IsOpen) return;

        // The whole shelf, described the way the ranking needs it. Nothing is asked of Discogs or anybody else.
        // Every card in this row is a record the listener can play. So the only question is which of their own
        // records goes best beside this one.
        var shelf = await Task.Run(() => ReadShelfTraits(library.Shelf));

        if (ct.IsCancellationRequested) return;

        // The subject comes from the same read as the shelf, not from the page. A record described one way and a
        // shelf described another way would score an answer against a different question. The page's title is what
        // the tags spell. The mirror's title is what the album's tracks agree on. If the mirror does not have the
        // subject yet, for example for a record opened the moment it was imported, the page's own words stand in.
        // They come with no genres and no year. That is the "cannot answer" that every axis already handles.
        var subject = shelf.FirstOrDefault(
                a => string.Equals(a.Folder, _folder, StringComparison.OrdinalIgnoreCase))
            ?? new AlbumTraits(_folder, title, artist, [], 0);

        var rows = SimilarAlbums.Pick(subject, shelf, SimilarAlbums.DefaultCount)
            .Select(a => new SimilarRow(a, _similarMetrics))
            .ToList();

        // The sleeve size is a question about the WIDTH of the row (see SimilarTileMetrics). It is not measured
        // here before the hand-over. A measurement made here would depend on the number of cards. A new record with
        // a different number of suggestions would then arrive at the wrong size, and correct itself a frame later,
        // which looks like a flinch. The width has not changed, so the answer already on the metrics is the answer
        // these cards need.
        SimilarList.ItemsSource = rows;
        _hasSimilar = rows.Count > 0;
        ShowSimilar();

        foreach (var row in rows)
            _ = FillSimilarCoverAsync(row, library.ShelfRoot ?? "", ct);
    }

    /// <summary>
    /// Every album on the shelf, reduced to what "similar" can be asked of: what it is called, who
    /// made it, what it is filed under and when it came out.
    ///
    /// <para>It makes three reads rather than one join. They are the reads the wall already makes. The year rule
    /// is <c>AlbumSort.YearFrom</c>, so a record has the same year here as on the wall. The genres go through
    /// <see cref="RPM.Core.Metadata.Genres.Match"/>, so "Hip Hop", "Rap" and "Pop Rap" count as one shelf, not three.
    /// A similarity built on raw tag text would call two records different only because two different people
    /// tagged them.</para>
    ///
    /// <para>ALL of an album's genres are used, not only the majority genre that the wall files it under. A record
    /// that is half jazz and half blues is both, when the question is what to play next. It can only be on one shelf
    /// when the question is where to file it.</para>
    /// </summary>
    private static IReadOnlyList<AlbumTraits> ReadShelfTraits(LibraryDatabase db)
    {
        var years = new Dictionary<string, List<(int Year, int Count)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, year, count) in db.QueryAlbumYearCounts())
        {
            if (!years.TryGetValue(folder, out var list))
                years[folder] = list = [];
            list.Add((year, count));
        }

        var genres = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, genre, _) in db.QueryAlbumGenreCounts())
        {
            // A tag that matches none of the app's own genres is dropped, not kept as itself. Two records sharing a
            // typo is not evidence of anything. One unmatched label per record would quietly outweigh the labels that
            // do mean something.
            if (RPM.Core.Metadata.Genres.Match(genre) is not { } canonical)
                continue;

            if (!genres.TryGetValue(folder, out var list))
                genres[folder] = list = [];
            if (!list.Contains(canonical))
                list.Add(canonical);
        }

        var shelf = new List<AlbumTraits>();
        foreach (var row in db.QueryAlbumFacts())
        {
            // The folder stands in wherever the tags say nothing. This is the same fallback the wall uses. So an untagged
            // record is still a record with a name and a maker, not two blanks.
            var parts = row.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries);

            shelf.Add(new AlbumTraits(
                row.Folder,
                row.Album ?? (parts.Length > 0 ? parts[^1] : ""),
                row.AlbumArtist ?? (parts.Length >= 2 ? parts[^2] : ""),
                genres.TryGetValue(row.Folder, out var filed) ? filed : [],
                years.TryGetValue(row.Folder, out var counted) ? AlbumSort.YearFrom(counted) : 0));
        }

        return shelf;
    }

    /// <summary>How wide a suggestion's sleeve is drawn. It is shared by every card in the row. There is one object
    /// for the page, not one per card. A resize sets it once, and every card follows.</summary>
    private readonly Models.SimilarTileMetrics _similarMetrics = new();

    /// <summary>
    /// The row divides its width into whole sleeves, at the size the track info pane draws one. So the suggestions
    /// END on the box's right edge, instead of trailing off a third of the way across it. The number of cards is
    /// not asked for here. See SimilarTileMetrics.
    /// </summary>
    /// <remarks>
    /// <b>The width it divides is the row's width with the groove beside it, not the width that a fold has widened.</b>
    /// Folding took the row from 818 points to 1220. That re-cut every sleeve from 143 to 131 points, and moved every
    /// card. This happened in the frame before the slide, so the row jumped and then slid. A fold does not change how
    /// big a suggestion is worth drawing. What the wider row shows is MORE of the same cards. That is what a reader
    /// would find by scrolling it. The last card is cut off at the edge, exactly as it is when they scroll.
    ///
    /// <para>The width is taken off the row, not computed for it. So the padding and the lane between SimilarBlock
    /// and this scroller cancel out, and do not have to be restated here.</para>
    /// </remarks>
    private void SimilarRow_SizeChanged(object sender, SizeChangedEventArgs e)
        => _similarMetrics.Set(SimilarJustifyWidth(e.NewSize.Width));

    /// <summary>The row's width with nothing folded away. The cards are cut to fill this width. Where the rung gives
    /// the groove no column, the row's own width is used, because the fold moves nothing there.</summary>
    private double SimilarJustifyWidth(double rowWidth)
    {
        if (AlbumPageDensity.SimilarRoom(ActualWidth, _rung) is not { } room)
            return rowWidth;

        var freed = Math.Max(0, SimilarBlock.ActualWidth - room);
        return Math.Min(rowWidth, rowWidth - freed);
    }

    /// <summary>The record's own sleeve, read off disk. Where this shelf is borrowed, or the album is offloaded, it
    /// comes from whoever holds it. Each card reads its own, so a slow one does not hold up the row.</summary>
    private async Task FillSimilarCoverAsync(SimilarRow row, string root, CancellationToken ct)
    {
        try
        {
            // It goes through one door. That puts the picture up. Where there is no picture, it says so. That is what
            // lets the card draw its made sleeve. It also stops the card drawing one for the five cards in the row
            // whose artwork is about to land. See CoverFill and SimilarRow.FigureSeed.
            await Services.Art.CoverFill.DressAsync(
                row,
                token => SimilarSleeveAsync(row, root, token),
                async bytes => await RemoteImage.DecodeSmoothAsync(
                    bytes, SimilarDecodeEdge(row, bytes), RemoteImage.Cut.Photograph),
                ct);
        }
        catch { /* a card without a sleeve still names the record */ }
    }

    /// <summary>Where one suggestion's bytes come from, and nothing else. They come from the picture in the folder
    /// on this disk, then from whoever holds the record. In the harness, a drawn one is used. What an empty answer
    /// MEANS is not decided here. See <see cref="Services.Art.CoverFill"/>.</summary>
    private static async Task<byte[]?> SimilarSleeveAsync(SimilarRow row, string root, CancellationToken ct)
    {
        if (row.Folder is { Length: > 0 } folder)
        {
            var full = Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));
            if (FolderArtWriter.FindFolderImage(full) is { } image)
                return await Task.Run(() => ThumbCache.Get(File.ReadAllBytes(image), ArtScaler.TileEdge));

            // No sleeve is under the folder. A borrowed shelf is a mirror with no music beneath it, so this is EVERY
            // suggestion on a shared library, not the odd record. An offloaded album of one's own has none either.
            // The page's own cover takes the other route in that case (see FillRemoteCoverAsync). Without this
            // fallback, the row of suggestions would read only the disk and draw blank squares.
            return await RemoteSleeveForAsync(folder, row.Artist, row.Title, ct) is { Length: > 0 } bytes
                ? await Task.Run(() => ThumbCache.Get(bytes, ArtScaler.TileEdge))
                : null;
        }

        // The harness has no disk under its shelf, so it draws the sleeve in bytes. Otherwise every card in the row
        // would be blank.
        return Mock ? await Task.Run(() => Gallery.GalleryArt.CoverPng(row.Artist, row.Title)) : null;
    }

    /// <summary>The sleeve of an album whose folder holds no picture. It comes from the computer that has the
    /// record, or from the store. One row of the shelf is enough to say which. See <see cref="RemoteSleeve"/>,
    /// which the page's own cover goes through for the same reason.</summary>
    private static async Task<byte[]?> RemoteSleeveForAsync(
        string folder, string artist, string title, CancellationToken ct)
    {
        var library = Library;
        if (!library.IsOpen)
            return null;

        if (await Cancellable.RunAsync(() => library.Shelf.FolderTracks(folder).FirstOrDefault(), ct)
                is not { Ran: true, Value: { } row })
            return null;

        // Only for a record that is not on this disk. That means a borrowed one, or one of the reader's own that has
        // been offloaded. An album sitting here with no cover.jpg in its folder simply has no artwork. Asking the
        // network about each of six of those on every page opened would be six round trips for nothing. This is the
        // same test the page's own cover makes.
        var file = Path.Combine(library.ShelfRoot ?? "", row.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(file))
            return null;

        return await RemoteSleeve.ForAsync(row, artist, title, ct);
    }

    /// <summary>
    /// The edge one suggestion's sleeve is cut to, in physical pixels.
    ///
    /// <para>The bytes arrive at <see cref="ArtScaler.TileEdge"/>, which is 640: the thumb kept on disk. The card
    /// draws them at <see cref="SimilarTileMetrics.Edge"/>, which is about 128. If the picture were decoded whole,
    /// the reduction would happen in the renderer at draw time. That is a plain bilinear read of a picture five
    /// times the size of the square. It aliases on anything with fine detail, which is most sleeves. Cut to the
    /// drawn size first, the reduction goes through <see cref="ArtScaler.SamplingFor(double)"/> instead. That is
    /// the same rule the page's own cover is cut by (see <see cref="SleeveDecodeEdge"/>).</para>
    ///
    /// <para>The bucket is used, not the exact side. <see cref="SimilarTileMetrics.Edge"/> moves with the row's
    /// width. A card is dressed once, and a decode pinned to the width at that moment would be cut again on every
    /// pane move. It is widened for a sleeve that is not square, because the card crops with UniformToFill, and
    /// what fills the square is the SHORT side.</para>
    /// </summary>
    private int SimilarDecodeEdge(SimilarRow row, byte[] bytes)
    {
        var edge = ArtScaler.DecodeEdgeFor(row.Metrics.Edge, XamlRoot?.RasterizationScale ?? 1);
        var (w, h) = ArtScaler.BoundsOf(bytes);
        return ArtScaler.LongEdgeForShortSide(w, h, edge);
    }

    /// <summary>Every suggestion is a record on this shelf, so every press opens an album in this same view. No
    /// card leaves the app.</summary>
    private void Similar_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SimilarRow { Folder: { Length: > 0 } folder })
            Load(folder, AlbumOpen.Fresh);
    }

    private void LoadCover(string root)
    {
        // The last record's colours are not this one's, and they sit under the whole card. They are taken down
        // before the new sleeve is even looked for. What arrives later paints over the peel.
        PeelGround();

        var fullFolder = Path.Combine(root, _folder.Replace('/', Path.DirectorySeparatorChar));

        // The record behind the sleeve runs on its own clock. It is a second file from the same folder, and it has
        // nothing to wait for.
        _ = LoadDiscAsync(fullFolder, _folder);

        if (FolderArtWriter.FindFolderImage(fullFolder) is { } image)
        {
            // The made sleeve is shown now, and the picture lands when the disk answers. The square is cleared, so it
            // does not keep showing the record just closed. The fill sets the source itself, because the picture it
            // sets is not the same object it started with. It is cut to size on its way past Skia.
            Cover.Source = null;
            ForgetStage(StagePicture.Sleeve);
            ShowMadeSleeve(false);
            _ = FillCoverAsync(image, _folder);
            return;
        }

        Cover.Source = null;
        ForgetStage(StagePicture.Sleeve);
        ShowMadeSleeve(true);

        // A record that is not on this disk has no folder here to hold a cover.jpg. This covers a record that was
        // offloaded, and one borrowed from another computer. Both the machine holding it and the store keep a sleeve
        // per album for exactly that case. The placeholder stays until the sleeve arrives.
        if (_tracks.Count > 0 && _tracks[0] is { RpmId.Length: > 0 } first && !File.Exists(first.FilePath))
            _ = FillRemoteCoverAsync(first);
    }

    /// <summary>
    /// The record standing out of the sleeve. This is the album's OWN disc scan where the folder has one.
    /// Otherwise it is the app's pressing.
    ///
    /// <para>Disc art is a folder file (disc.png, written at import or by the editor). There are two kinds of
    /// picture. A photographed CD is the whole record, with grooves, sheen, label and all. It replaces the drawn
    /// disc. A vinyl label is the middle third of one. It is worn ON the pressing. The pressing stays, tinted by
    /// <see cref="PressDiscAsync"/> like any other record's. Otherwise the label would hang in the air with nothing
    /// behind it. Which kind this is comes from the picture itself (see <see cref="DiscStamp.Covers"/>). The deck
    /// follows the same rule for the record that is playing.</para>
    ///
    /// <para>The black pressing stays until <see cref="PressDiscAsync"/> has a sleeve to take a colour from. It is
    /// not a placeholder for the scan. A folder with no disc.png has no scan coming, and the pressing is the answer
    /// there. It is also not painted over a colour that has already landed. The two reads race, and which of them is
    /// the slower one depends on what the folder holds.</para>
    /// </summary>
    /// <param name="opened">The folder this load was started for. The page may have moved to another record by the
    /// time the disk answers. A disc from the last record painting over this one is the one failure that this must
    /// not allow.</param>
    private async Task LoadDiscAsync(string fullFolder, string opened)
    {
        _discIsWholeScan = false;
        _pressedFolder = null;
        CoverDiscScan.Opacity = 0;
        CoverCdDress.Opacity = 0;
        CoverDisc.Opacity = 1;

        // The last record's disc pictures are not cut again for this one.
        ForgetStage(StagePicture.DiscScan);
        ForgetStage(StagePicture.Pressing);

        try
        {
            var scan = await Task.Run(() => DiscScan(fullFolder));

            if (!ReferenceEquals(_folder, opened) && _folder != opened)
                return;

            // Nothing is on this disk. For a BORROWED record, that is not the same as having no disc art. The folder
            // is on the computer that holds it, and so is the picture. It is asked only when the disk had nothing, so
            // a record of this collection's own never touches the network for it.
            if (scan is null)
            {
                scan = await BorrowedDiscAsync();
                if (_folder != opened)
                    return;
            }

            if (scan is { Bytes.Length: > 0 } picture)
            {
                // Cut to exactly the size the record is drawn at, the same way as the sleeve in front of it. See
                // RemoteImage.DecodeArtAsync, and StageEdge for which size.
                var edge = StageEdge();
                var disc = await RemoteImage.DecodeArtAsync(picture.Bytes, edge);

                // This is asked again on the far side of the cut. It takes two more turns of the dispatcher, and the
                // reader can open another record in the meantime.
                if (_folder != opened)
                    return;

                CoverDiscScan.Source = disc;
                ShownOnStage(StagePicture.DiscScan, new StageCut(opened, picture.Bytes, edge));
                CoverDiscScan.Opacity = 1;

                // A whole record covers the pressing, and the pressing goes down with it. Otherwise it would show through
                // the spindle hole that the stamp punched. A label leaves the pressing standing. The press below then
                // tints it in the sleeve's own colour.
                _discIsWholeScan = picture.CoversRecord;
                CoverDisc.Opacity = picture.CoversRecord ? 0 : 1;
                CoverCdDress.Opacity = picture.CoversRecord ? 1 : 0;
                if (picture.CoversRecord)
                    return;
            }
        }
        catch
        {
            // Decoration. A disc that will not read leaves the pressing below in its place.
        }

        // Black stays until a sleeve gives a colour, and only until then. This read and the press next door start
        // together (see LoadCover). They finish on their own clocks, so the press may already have landed by the time
        // this gets here. A label scan makes this the slow one. A folder with no disc art makes it the fast one. The
        // check is made on what is on the record, not on how long anything took.
        if (_folder != opened || _pressedFolder == opened)
            return;

        // As bytes, so it is cut to the size it is drawn at, like the other two pictures.
        if (await VinylPressing.PressBlackBytesAsync() is not { } blackBytes)
            return;

        var blackEdge = StageEdge();
        var black = await RemoteImage.DecodeArtAsync(blackBytes, blackEdge);

        // Asked again after the cut. The press next door may have landed while it ran.
        if (_folder == opened && _pressedFolder != opened)
        {
            CoverDisc.Source = black;
            ShownOnStage(StagePicture.Pressing, new StageCut(opened, blackBytes, blackEdge));
        }
    }

    /// <summary>The record whose own colour is on the disc, or null while it is still the house black. It is
    /// written where the press lands, so it is what the disc IS wearing.</summary>
    private string? _pressedFolder;

    /// <summary>
    /// The folder's disc art, cut down and cut OUT: a round record with a hole in it, whatever kind of
    /// picture the folder happened to hold.
    ///
    /// <para>disc.png is a transparent picture of the record, and it needs nothing done to it. disc.jpg and
    /// disc.jpeg are also names that this app accepts (see <see cref="FolderArtWriter.FindDiscImage"/>). A JPEG
    /// cannot carry transparency at all. What it holds is a photograph of a CD on a table, corners and all. Drawn
    /// as it came, it stood out from behind the sleeve as a white square with a disc printed on it.</para>
    ///
    /// <para>So the transparency is MADE, the same way as it is made for a disc that comes from the catalogues (see
    /// <see cref="ArtworkService.DiscArtBytesAsync"/> and <see cref="DiscStamp"/>). The circle is found, cut out, and
    /// the hole is punched. A picture with no disc in it is handed back as it came. The round border it is drawn in
    /// clips the corners off anyway, and a flat record is better than none.</para>
    ///
    /// <para>The alpha is read from the CUT-DOWN copy, not from the file. That is the picture that will be shown, and
    /// the stage reads it the same way. It also means the whole scan is decoded once, by the cache, rather than
    /// twice.</para>
    ///
    /// <para>It runs off the UI thread. Both halves of it decode pictures.</para>
    /// </summary>
    /// <summary>
    /// The disc art of a record whose files are not on this disk. It comes from the computer that holds the files.
    /// It is cut, and it is asked the same question as the folder's own (see <see cref="DiscScan"/>).
    ///
    /// <para>It is null for a record of this collection's own, for a computer that is not on the network, and for
    /// most records, which have no disc art anywhere. All three give the same answer on screen: the app's own
    /// pressing, tinted by the sleeve.</para>
    /// </summary>
    private async Task<DiscPicture?> BorrowedDiscAsync()
    {
        if (_tracks.Count == 0 || _tracks[0] is not { RpmId.Length: > 0 } first
            || File.Exists(first.FilePath))
            return null;

        // The library row is used, not the track on screen. Which computer holds this record is not something a
        // TrackInfo carries, and it decides where the picture is asked for. It is the same read that the sleeve makes
        // next door (see FillRemoteCoverAsync).
        if (!Library.IsOpen || Library.Shelf.FindByRpmId(first.RpmId!) is not { } row)
            return null;

        if (await RemoteSleeve.DiscForAsync(row) is not { Length: > 0 } bytes)
            return null;

        return await Task.Run(
            () => DiscStamp.Round(ThumbCache.Get(bytes, ArtScaler.TileEdge), vinyl: false));
    }

    private static DiscPicture? DiscScan(string fullFolder)
    {
        if (FolderArtWriter.FindDiscImage(fullFolder) is not { } path)
            return null;

        var thumb = ThumbCache.Get(File.ReadAllBytes(path), ArtScaler.TileEdge);
        return thumb is { Length: > 0 } ? DiscStamp.Round(thumb, vinyl: false) : null;
    }

    /// <summary>
    /// The app's pressing in the sleeve's own colour, once a sleeve has landed.
    ///
    /// <para>It is the same disc that the deck, the header and the visualizer press, from the same hue. So a record
    /// looks like itself wherever the app draws it. A sleeve with no colour in it presses nothing, and keeps the black
    /// already on screen.</para>
    ///
    /// <para>It does nothing where the album brought its own scan. A photograph of the actual record beats a render
    /// of the app's. A hue pressed over it would throw away the better picture that the page already found.</para>
    /// </summary>
    private async Task PressDiscAsync(byte[] coverBytes, string opened)
    {
        if (_discIsWholeScan)
            return;

        try
        {
            var hue = await Task.Run(() => VinylPressing.HueFromArt(coverBytes));
            if (hue is not { } colour || _discIsWholeScan || _folder != opened)
                return;

            // As bytes, so it is cut to the size it is drawn at, with the smooth bicubic. It is not shrunk from the
            // 1024-pixel render by the renderer. See RemoteImage.DecodeArtAsync.
            if (await VinylPressing.PressBytesAsync(colour) is not { } bytes || _discIsWholeScan || _folder != opened)
                return;

            var edge = StageEdge();
            var pressed = await RemoteImage.DecodeArtAsync(bytes, edge);

            if (_discIsWholeScan || _folder != opened)
                return;

            CoverDisc.Source = pressed;
            _pressedFolder = opened;
            ShownOnStage(StagePicture.Pressing, new StageCut(opened, bytes, edge));
        }
        catch
        {
            // Decoration, again: the black pressing stays.
        }
    }

    /// <summary>Whether the disc on screen is the album's own scan of the WHOLE record. It stops a late hue press
    /// from painting the app's render over a photograph of the actual record. It is false for a label scan. A label
    /// is worn on the pressing, and that pressing is tinted like every other record's.</summary>
    private bool _discIsWholeScan;

    /// <summary>The made sleeve and the disc over it are shown or hidden together. One call does both, rather than
    /// two assignments at each of the three places where a cover is settled. They are one state: this record has no
    /// artwork. Two lines can be forgotten, and one line cannot.</summary>
    private void ShowMadeSleeve(bool show)
    {
        var state = show ? Visibility.Visible : Visibility.Collapsed;
        CoverFigure.Visibility = state;
        CoverPlaceholder.Visibility = state;

        // The picture itself is asked for HERE, where the record is known to have no artwork. It is never asked on
        // the way into a page whose sleeve has not been looked for yet. The picture is drawn from the folder, with
        // the same seed as the wall's tile and the album list. The reader has just come from one of those. A record
        // whose made sleeve changed on the way in would look like a different record.
        if (show)
            CoverFigure.Background = Converters.SleeveFigureConverter.For(_folder);
    }

    private async Task FillRemoteCoverAsync(TrackInfo track)
    {
        try
        {
            // The library row is used, not the track on screen. Which computer holds this record is not something a
            // TrackInfo carries, and it decides where the sleeve is asked for.
            if (!Library.IsOpen || Library.Shelf.FindByRpmId(track.RpmId!) is not { } row) return;

            if (await RemoteSleeve.ForAsync(row, track.Artist, track.Album)
                is not { Length: > 0 } bytes) return;

            // The page may have moved to another record while this was in the air.
            if (_tracks.Count == 0 || !ReferenceEquals(_tracks[0], track)) return;

            ShowSleeveGroundAsync(bytes);
            _ = PressDiscAsync(bytes, _folder);

            var thumb = await Task.Run(() => ThumbCache.Get(bytes, ArtScaler.TileEdge));

            // Cut to exactly the size it is drawn at, with the smooth bicubic. See StageEdge.
            var edge = StageEdge();
            var sleeve = await RemoteImage.DecodeArtAsync(thumb, edge);

            // This is checked again on the far side of the cut, for the same reason as the check above.
            if (_tracks.Count == 0 || !ReferenceEquals(_tracks[0], track)) return;

            Cover.Source = sleeve;
            ShownOnStage(StagePicture.Sleeve, new StageCut(_folder, thumb, edge));
            ShowMadeSleeve(false);
        }
        catch { /* Decoration. The square stays empty rather than failing the page. */ }
    }

    /// <param name="opened">The folder this read was started for. A twelve-megabyte scan takes a while to read from
    /// disk, and the reader can open another record in that time. The sleeve now lands ON the page, not in a bitmap
    /// that the page has already let go of. So the record just left has to be stopped here, or it paints over the one
    /// being looked at. This is the same guard, for the same reason, as in <see cref="LoadDiscAsync"/>.</param>
    private async Task FillCoverAsync(string path, string opened)
    {
        try
        {
            // One read from the disk, two uses. The square in the rail needs the sleeve's detail, and the ground under
            // the card needs its colour. Reading the file twice would mean decoding a scan that can be twelve megabytes
            // twice.
            var bytes = await Task.Run(() => File.ReadAllBytes(path));
            ShowSleeveGroundAsync(bytes);

            // The folder this read was for is used, not whatever the page shows by now. The hue belongs to this sleeve.
            // Pressed onto a record opened since, it would be the wrong colour on the right disc.
            _ = PressDiscAsync(bytes, opened);

            var thumb = await Task.Run(() => ThumbCache.Get(bytes, ArtScaler.TileEdge));

            // Cut to exactly the size it is drawn at, with the smooth bicubic, before anything is decoded. See
            // RemoteImage.DecodeArtAsync, and StageEdge for which size.
            var edge = StageEdge();
            var sleeve = await RemoteImage.DecodeArtAsync(thumb, edge);

            if (_folder != opened)
                return;

            Cover.Source = sleeve;
            ShownOnStage(StagePicture.Sleeve, new StageCut(opened, thumb, edge));
        }
        catch { /* Decoration. The square stays empty rather than failing the page. */ }
    }

    /// <summary>
    /// The reader's own marks on a record, and nobody else's.
    ///
    /// <para>On a borrowed shelf, the stars and the review that are read back belong to the LENDER. Shown on this
    /// page, they would look like the reader's own. The pressing link stays. Which release this is, is a fact about
    /// the record, not a mark on it.</para>
    ///
    /// <para>The heart is not in this list, and that is the point. A guest's listing never carries one. So the mark
    /// on a borrowed row was put there at this desk, and it is the reader's own. The stars and the review are the
    /// opposite case. Those columns can hold the owner's answer on a shelf taken from one of the listener's own
    /// machines. A borrowed page must not show them as the reader's.</para>
    /// </summary>
    private static AlbumMeta Mine(AlbumMeta meta, bool borrowed)
        => borrowed
            ? meta with { Rating = 0, ReviewMarkdown = null }
            : meta;

    /// <summary>The parts of the page that come out of the local mirror: favourite, own stars, the
    /// Discogs item in the menu.</summary>
    private void ApplyMeta(AlbumMeta meta)
    {
        // One heart, on the round key under the sleeve. The word it stands for is in the key's tooltip.
        var favorite = meta.IsFavorite;
        FavoriteButtonIcon.FontFamily = (FontFamily)Resources[favorite ? "Fa" : "FaRegular"];
        IconName.Set(
            FavoriteButton, Loc.Get(favorite ? "Album_UnsetFavorite" : "Album_SetFavorite"));

        // The local rating is out of five whole stars. The wall's rating is in halves. It is shown as it is here,
        // and doubled on the way up.
        MyStars.Value = meta.Rating;

        // A record with no linked pressing has no Discogs page to show. So the item is hidden, not left there to do
        // nothing.
        DiscogsMenuItem.Visibility = string.IsNullOrEmpty(meta.DiscogsReleaseId)
            ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── the wall ──

    private async Task LoadCommunityAsync(
        string artist, string title, int year, string? catno, CancellationToken ct)
    {
        _identity = (artist, title, year);
        _catno = catno ?? _catno;

        UpdateWriteAffordances();

        if (Mock)
        {
            _album = Gallery.GalleryCommunity.Album(artist, title, year, _catno);
            ApplyAlbumNumbers();
            await RefreshReviewsAsync(ct);
            await RefreshCommentsAsync(ct);
            return;
        }

        try
        {
            // Every id this copy carries goes up with the question. The entry is matched on these ids before it is
            // matched on its text. A stranger who arrives with only one of them finds this record by that id.
            var pressing = _pressing;

            // Signed in, asking for the album creates an entry if RPM has never seen this record. That is how a wall
            // starts. Signed out, the same question is read-only.
            _album = SignedIn
                ? await Community.ResolveAlbumAsync(
                    artist, title, year, catno, pressing?.DiscogsReleaseId,
                    musicBrainzReleaseId: pressing?.MusicBrainzReleaseId,
                    musicBrainzReleaseGroupId: pressing?.ReleaseGroupId,
                    barcode: pressing?.Barcode, discId: pressing?.DiscId, ct: ct)
                : await Community.LookupAlbumAsync(artist, title, year, catno, ct);
        }
        catch (Exception)
        {
            if (ct.IsCancellationRequested) return;
            ShowBanner(Loc.Get("Album_CommunityOffline"), canSignIn: false);
            return;
        }

        if (ct.IsCancellationRequested) return;

        ApplyAlbumNumbers();
        UpdateWriteAffordances();

        if (_album is null)
        {
            // Nobody has written about this record yet, and nobody is signed in to be the first.
            _reviewTotal = 0;
            _commentTotal = 0;
            ApplyGrooveCounts();
            BuildPager(1);
            ReviewsEmpty.Visibility = Visibility.Visible;
            CommentsEmpty.Visibility = Visibility.Visible;
            return;
        }

        // The link is remembered, so the next visit opens on the entry, rather than matching this record from its
        // text again. It also lets the rest of the app tell a linked album from an unlinked one.
        if (_folder is { Length: > 0 } linkedFolder && Library.IsOpen && _pressing?.RpmAlbumId != _album.AlbumId)
        {
            Library.Shelf.SetAlbumRpmId(linkedFolder, _album.AlbumId);
            _pressing = Library.Shelf.GetAlbumMeta(linkedFolder);
        }

        await RefreshAliasesAsync(_album.AlbumId, ct);
        await RefreshReviewsAsync(ct);
        await RefreshCommentsAsync(ct);
    }

    private void ApplyAlbumNumbers()
    {
        // The average is shown in words, under the reader's own stars. A row of stars for the average would be a
        // second control that cannot be pressed, placed directly under one that can. A number with a count beside it
        // is also clearer. Five star glyphs must be measured by eye before they say 4.2 out of 128.
        AverageCountText.Text = _album is { RatingCount: > 0 } a
            ? $"{Loc.Get("Album_AverageRating")}  {a.AverageRating:0.0} ({a.RatingCount})"
            : Loc.Get("Album_NotRatedYet");

        if (!string.IsNullOrWhiteSpace(_album?.Catno))
        {
            CatalogText.Text = $"{Loc.Get("Album_CatalogId")}: {_album.Catno}";
            CatalogText.Visibility = Visibility.Visible;
        }
    }

    private async Task RefreshReviewsAsync(CancellationToken ct)
    {
        if (_album is null) return;

        RpmReviewPage page;
        if (Mock)
        {
            page = Gallery.GalleryCommunity.Page(_album.AlbumId, _sort, _page, PageSize);
        }
        else
        {
            try
            {
                page = await Community.GetReviewsAsync(_album.AlbumId, _sort, _page, PageSize, ct: ct);
            }
            catch (Exception)
            {
                if (!ct.IsCancellationRequested)
                    ShowBanner(Loc.Get("Album_CommunityOffline"), canSignIn: false);
                return;
            }
        }

        if (ct.IsCancellationRequested) return;

        var me = Mock ? Gallery.GalleryCommunity.MeUserId : Auth.UserId;
        var apiBase = Community.BaseAddress;

        // A review of another pressing of this album says so. The name is looked up lazily, once per record on the
        // page. Most reviews of an album are of the record being looked at. Asking the API for a name that is never
        // shown would be a call per page for nothing.
        var rows = new List<ReviewRow>(page.Items.Count);
        foreach (var review in page.Items)
            rows.Add(new ReviewRow(review, me, apiBase, await PressingNameAsync(review.AlbumId, ct)));

        ReviewList.ItemsSource = rows;
        ReviewsEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        _reviewTotal = page.Total;
        ApplyGrooveCounts();
        BuildPager(page.PageCount);

        // The top review is the highest-scoring one. That is only known from the votes tab. Any other sort leaves
        // whatever was already there, rather than promoting an accident.
        if (_sort == RpmReviewSort.Votes && _page == 1 && rows.Count > 0)
        {
            var lead = TopReviewLead(rows[0].Body);
            TopReviewBody.Text = lead;
            TopReviewAuthor.Text = rows[0].AuthorName;
            TopReviewStars.Value = rows[0].Stars;
            _hasTopReview = lead.Length > 0;
            ShowTopReview();
        }

        // A new page of reviews is a new list. The scroller should not stay where the last page was read.
        ReviewScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    /// <summary>
    /// The opening paragraph of a review, which is all the card in the papers column shows.
    ///
    /// <para>A paragraph ends at a blank line. This is the same rule that the encyclopedia cards use (see
    /// <see cref="RPM.Core.Metadata.Providers.EncyclopediaArticle.Lead"/>). A single newline is a line break inside
    /// a paragraph, not the end of one. The lead leaves out the postscript that people write under a verdict, such as
    /// the favourite tracks or a note about the pressing. That is worth reading, but it is not worth the top of a
    /// column. So it stays in the groove with the rest of the review.</para>
    ///
    /// <para>The five-line trim in the markup is the other half of this, and it does not replace it. Five lines of a
    /// wide card are a good deal more prose than five lines of a narrow one. Where the review ends should not depend on
    /// the window's width.</para>
    /// </summary>
    private static string TopReviewLead(string body)
    {
        var text = body.Replace("\r\n", "\n").Trim();
        var end = text.IndexOf("\n\n", StringComparison.Ordinal);
        return (end < 0 ? text : text[..end]).TrimEnd();
    }

    private async Task RefreshCommentsAsync(CancellationToken ct)
    {
        if (_album is null) return;

        try
        {
            var comments = Mock
                ? Gallery.GalleryCommunity.Said(_album.AlbumId)
                : await Community.GetCommentsAsync(_album.AlbumId, ct: ct);
            if (ct.IsCancellationRequested) return;

            var me = Mock ? Gallery.GalleryCommunity.MeUserId : Auth.UserId;
            var apiBase = Community.BaseAddress;
            var rows = comments.Select(c => new CommentRow(c, me, apiBase)).ToList();

            CommentList.ItemsSource = rows;
            CommentsEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            _commentTotal = rows.Count;
            ApplyGrooveCounts();
        }
        catch (Exception)
        {
            // The comments are the smallest thing on the page. If they fail to load, the empty state says so quietly.
            if (!ct.IsCancellationRequested)
                CommentsEmpty.Visibility = Visibility.Visible;
        }
    }

    // ── what the groove holds, and how much of it fits on a page ──

    private int _reviewTotal;
    private int _commentTotal;
    private int _pageCount = 1;

    /// <summary>The line under the groove's name. It says what is in the groove for this record. It is the same kind
    /// of subheading that the rest of the app uses, showing how much of the thing named above it there is.</summary>
    private void ApplyGrooveCounts()
        => GrooveSubheading.Text = Loc.Format("Groove_Counts", _reviewTotal, _commentTotal);

    /// <summary>
    /// The pager under the reviews: back, where you are, forward.
    ///
    /// <para>A row with one number per page grows with the record. A well-reviewed album would wear twenty of them
    /// across the foot of the column. Three keys say the same thing at any depth, and they are the app's own keys.</para>
    /// </summary>
    private void BuildPager(int pageCount)
    {
        _pageCount = Math.Max(1, pageCount);
        _page = Math.Clamp(_page, 1, _pageCount);

        // One page is no choice, so nothing is drawn at all.
        PagerSlot.Visibility = _pageCount <= 1 ? Visibility.Collapsed : Visibility.Visible;
        PageText.Text = $"{_page} / {_pageCount}";
        PagePrev.IsEnabled = _page > 1;
        PageNext.IsEnabled = _page < _pageCount;
    }

    /// <summary>What a signed-out reader is told, and what the write buttons do about it.</summary>
    private void UpdateWriteAffordances()
    {
        var signedIn = SignedIn;

        MyStars.IsInteractive = signedIn;
        SendComment.IsEnabled = signedIn && CommentBox.Text.Trim().Length > 0;
        CommentBox.IsEnabled = signedIn;

        if (!signedIn)
            ShowBanner(Loc.Get("Album_SignInToWrite"), canSignIn: true);
        else
            SignInBanner.Visibility = Visibility.Collapsed;
    }

    private void ShowBanner([Shown] string text, bool canSignIn)
    {
        SignInText.Text = text;
        SignInButton.Visibility = canSignIn ? Visibility.Visible : Visibility.Collapsed;
        SignInBanner.Visibility = Visibility.Visible;
    }

    // ── playing ──

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_tracks.Count > 0) Amp.AddTracks(_tracks, play: true);
    }

    private void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        if (_tracks.Count > 0) Amp.AddTracks([.. _tracks.OrderBy(_ => Random.Shared.Next())], play: true);
    }

    private void Track_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AlbumTrackItem { Info: { } info })
            Amp.PlayAlbumFrom(info);
    }

    /// <summary>Everything the track right-click menu needs. The album's own actions are the rail's on
    /// this page, so nothing here repeats them.</summary>
    public TrackMenuHooks? TrackMenuHooks { get; set; }

    /// <summary>The way from the artist named under the sleeve to their own page. See
    /// <see cref="ArtistPageHooks"/>; unset, the name is drawn as the plain text it has always been.</summary>
    public ArtistPageHooks? ArtistHooks { get; set; }

    /// <summary>
    /// The line under the record's name: who made it, when, and what the file is.
    ///
    /// <para>It is written as INLINES, not as one string, because the artist is a door and the rest of the line is
    /// not. It is still one text block, so the line keeps wrapping and centring as one. A name lifted into a control
    /// of its own would break out of the middle of its own sentence the first time a long one wrapped.</para>
    /// </summary>
    private void PaintSubtitle(string artist, IReadOnlyList<string> facts)
    {
        var named = !string.IsNullOrWhiteSpace(artist);

        // The line is kept as one string, because two places read it: the heading over a review, and the share
        // text. Neither of them wants a door.
        _subtitle = string.Join("  ·  ", named ? facts.Prepend(artist) : facts);

        SubtitleText.Inlines.Clear();

        if (named)
        {
            if (ArtistHooks is { } door && door.Exists(artist))
            {
                // Underlined, and in the line's own ink, not the app's link colour. This sits directly under the
                // record's name. A second colour there would read as a second heading. The underline says that this can
                // be pressed, without saying it twice.
                //
                // That is the resting state. Under the pointer, it turns cyan like every other link in the app. That
                // comes from HyperlinkForegroundPointerOver in App.xaml. The ink set here wins the normal state, and that
                // brush wins the other two. It has to be a FLAT entry there, not a themed one. Otherwise
                // XamlControlsResources' own hyperlink blue answers first. The note in App.xaml says why.
                var link = new Microsoft.UI.Xaml.Documents.Hyperlink
                {
                    Foreground = SubtitleText.Foreground,
                };
                link.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = artist });
                link.Click += (_, _) => door.Open(artist);
                SubtitleText.Inlines.Add(link);
            }
            else
            {
                SubtitleText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = artist });
            }

            if (facts.Count > 0)
                SubtitleText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "  ·  " });
        }

        if (facts.Count > 0)
            SubtitleText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = string.Join("  ·  ", facts),
            });
    }

    /// <summary>The subtitle as plain words. See <see cref="PaintSubtitle"/>.</summary>
    private string _subtitle = "";

    /// <summary>Everything the RECORD's right-click menu needs, which is the menu on the sleeve. It is the same
    /// object that the wall, the album list and an artist's shelves are given. A record right-clicked on its own page
    /// and the same record right-clicked on the grid are one record. Two sets of answers about it are how the two come
    /// to disagree.</summary>
    public AlbumMenuHooks? MenuHooks { get; set; }

    /// <summary>
    /// The sleeve's menu: the shared one, built fresh on each open so the cloud, the favourite and the
    /// Discogs entries answer for the record as it is at that moment.
    ///
    /// <para>Nothing is remembered between opens, and nothing is wired to this page's own state. So the page does
    /// nothing itself about a delete. The host already closes it when the record it is showing goes (see
    /// MainPage.Delete).</para>
    /// </summary>
    private void Cover_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (_folder is not { Length: > 0 } folder)
            return;

        var menu = AlbumMenuFactory.Build(
            folder,
            MenuHooks ?? new AlbumMenuHooks(null, null, null, null, null),
            (FontFamily)Resources["Fa"],
            (FontFamily)Resources["FaRegular"],
            AlbumMenuPlace.OwnPage);

        if (args.TryGetPosition(sender, out var point))
            menu.ShowAt(sender, new FlyoutShowOptions { Position = point });
        else if (sender is FrameworkElement element)
            menu.ShowAt(element);

        args.Handled = true;
    }

    // ── the heart at the end of a track line ──
    //
    // The page reads and writes the library itself. The whole rail does, and so does the album's own favourite.
    // So the mark on one of its songs is written here, not handed to the host.

    private void Track_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AlbumTrackItem line)
            line.IsHovered = true;
    }

    private void Track_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AlbumTrackItem line)
            line.IsHovered = false;
    }

    private void TrackFavorite_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AlbumTrackItem { Info: { } info } line)
            return;

        // Written through the service, like every other heart in the app. The write is one line either way. The
        // service means that surfaces this page cannot see, such as the full-screen player over it, hear about a mark
        // made here. See LibraryService.SetTrackFavorite.
        var next = !line.IsFavorite;
        if (Library.SetTrackFavorite(info.FilePath, next))
            line.IsFavorite = next;
    }

    /// <summary>
    /// The equaliser in front of the song the player is on.
    ///
    /// <para>It is read from the player rather than handed in, and it is read afresh every time it is asked. The
    /// page can be opened at any point in a track, and the track can change while the page stands open. A remembered
    /// path would be one more thing to keep in step with both. The caller only has to say WHEN to look: on the
    /// playback signal, and when the lines are built.</para>
    /// </summary>
    public void ApplyPlaying()
    {
        if (TrackList.ItemsSource is not IEnumerable<AlbumTrackItem> lines)
            return;

        var playing = App.Services.GetRequiredService<PlaybackService>().PlayingPath;

        foreach (var line in lines)
            if (line.IsTrack)
                line.IsPlaying = playing is { } path
                    && string.Equals(line.FilePath, path, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One song's heart, re-marked on the open record. The favourite entry in the song's right-click menu
    /// lands here.</summary>
    public void ApplyTrackFavorite(string filePath, bool favorite)
    {
        if (TrackList.ItemsSource is not IEnumerable<AlbumTrackItem> lines)
            return;
        foreach (var line in lines)
            if (line.IsTrack && string.Equals(line.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                line.IsFavorite = favorite;
    }

    private void Track_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is not AlbumTrackItem { Info: { } info })
            return;

        var menu = TrackMenuFactory.Build(
            info, TrackMenuHooks ?? new TrackMenuHooks(null, null, null, null),
            (FontFamily)Resources["Fa"], (FontFamily)Resources["FaRegular"]);
        if (args.TryGetPosition(sender, out var point))
            menu.ShowAt(sender, new FlyoutShowOptions { Position = point });
        else if (sender is FrameworkElement fe)
            menu.ShowAt(fe);
        args.Handled = true;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    // ── the rail ──

    /// <summary>
    /// The record's heart, on whichever shelf this page is a page OF. On a borrowed shelf, the mark is the reader's
    /// own, about somebody else's record.
    ///
    /// <para>It is written through the service, not straight to the mirror. That way the wall standing behind this
    /// page hears about it. The sticker on a sleeve and the heart on its page are one mark. A page that wrote it
    /// privately would leave the wall showing yesterday's answer until the next rebuild.</para>
    /// </summary>
    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        var library = Library;
        if (!library.IsOpen || _folder.Length == 0) return;

        library.SetAlbumFavorite(
            _folder, !library.IsAlbumFavorite(_folder, AlbumShelf.OnScreen), AlbumShelf.OnScreen);

        ApplyMeta(library.Shelf.GetAlbumMeta(_folder));
    }

    private async void Discogs_Click(object sender, RoutedEventArgs e)
    {
        var id = Library.IsOpen && _folder.Length > 0 ? Library.Shelf.GetAlbumMeta(_folder).DiscogsReleaseId : null;
        if (!string.IsNullOrEmpty(id))
            await Windows.System.Launcher.LaunchUriAsync(new Uri($"https://www.discogs.com/release/{id}"));
    }

    /// <summary>
    /// Puts the caret in the comment box.
    ///
    /// <para>It asks for an account first, because the box is disabled without one. A rail action that focuses a
    /// control nobody can type into is a click that does nothing, and says nothing about why. Signing in reloads the
    /// album, so the focus is taken afterwards.</para>
    /// </summary>
    private async void FocusComment_Click(object sender, RoutedEventArgs e)
    {
        if (!await RequireSignInAsync()) return;

        // The box is on the comments tab, which is not necessarily the one that is up. On a narrow card the groove
        // itself may be behind the strip over the tracks. So the tab is opened first. Focusing a field that nobody
        // can see is a click that does nothing.
        OpenGrooveTab(GrooveTabs.Comments);

        CommentBox.IsEnabled = true;
        CommentBox.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Writes the user's review. The dialog is the local one, and what it saves is pushed to the wall when there
    /// is an account to sign it with. The local copy is the record either way. A review written offline is not
    /// lost. It is just not shared yet.
    /// </summary>
    private async void WriteReview_Click(object sender, RoutedEventArgs e)
    {
        var library = Library;
        if (!library.IsOpen || _folder.Length == 0 || XamlRoot is null) return;

        var db = library.Shelf;
        var borrowed = library.IsPeerView;
        var subtitle = string.IsNullOrWhiteSpace(_subtitle) ? _title.Full
            : $"{_title.Full}  ·  {_subtitle}";

        var result = await AlbumReviewDialog.ShowAsync(
            XamlRoot, subtitle, Mine(db.GetAlbumMeta(_folder), borrowed));
        if (result is null) return;

        // The wall takes it either way. The mirror takes it only where there is a row of the reader's own to keep it
        // on. A borrowed shelf is the read side (see LibraryService.Shelf), and the folder named here is a folder on
        // somebody else's disk.
        if (!borrowed)
        {
            db.SetAlbumReview(_folder, result.Rating, result.Markdown, DateTime.UtcNow.Ticks);
            ApplyMeta(db.GetAlbumMeta(_folder));
        }
        else
        {
            MyStars.Value = result.Rating;
        }

        await PublishReviewAsync(result.Rating, result.Markdown);
    }

    /// <summary>The key under "Be the first!" on an empty reviews tab. It is the same door as the rail's own
    /// "Write a review". There is one review editor, and this is a second way to reach it.</summary>
    private void WriteFirstReview_Click(object sender, RoutedEventArgs e)
        => WriteReview_Click(sender, e);

    private async void MyRating_Picked(object? sender, double stars)
    {
        var library = Library;
        if (!library.IsOpen || _folder.Length == 0) return;

        var db = library.Shelf;
        var borrowed = library.IsPeerView;
        var meta = Mine(db.GetAlbumMeta(_folder), borrowed);
        var whole = (int)Math.Round(stars);

        // As with the written review, the wall always gets it, and the mirror gets it only on the reader's own shelf.
        // The star the reader just pressed stays lit either way. It is the control's own state.
        if (!borrowed)
            db.SetAlbumReview(_folder, whole, meta.ReviewMarkdown, DateTime.UtcNow.Ticks);

        await PublishReviewAsync(whole, meta.ReviewMarkdown);
    }

    /// <summary>Sends the local review up. It does nothing when signed out. The star was still saved.</summary>
    private async Task PublishReviewAsync(int stars, string? markdown)
    {
        if (!SignedIn || stars <= 0) return;
        if (await EnsureAlbumAsync() is not { } album) return;

        if (Mock)
        {
            Gallery.GalleryCommunity.WriteReview(album.AlbumId, Math.Clamp(stars * 2, 1, 10), markdown);
            _album = Gallery.GalleryCommunity.Album(album.AlbumId);

            ApplyAlbumNumbers();
            await RefreshReviewsAsync(CancellationToken.None);
            return;
        }

        try
        {
            // Local ratings are whole stars. The wall's ratings are halves. The doubling happens here, and nowhere else.
            await Community.WriteReviewAsync(album.AlbumId, Math.Clamp(stars * 2, 1, 10), markdown);
            _album = await Community.GetAlbumAsync(album.AlbumId) ?? album;

            ApplyAlbumNumbers();
            await RefreshReviewsAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            ShowBanner(Loc.Get("Album_CommunityOffline"), canSignIn: false);
        }
    }

    /// <summary>
    /// The shared album entry, minting one if the page has not got one yet.
    ///
    /// <para>Signing in happens on the page, after the entry was already asked for as a signed-out reader. That
    /// question never creates an entry. Without this, the first thing a new account did would silently do nothing.
    /// Null means the API did not answer, and the caller says so.</para>
    /// </summary>
    private async Task<RpmAlbum?> EnsureAlbumAsync()
    {
        if (_album is not null) return _album;
        if (!SignedIn) return null;

        try
        {
            _album = await Community.ResolveAlbumAsync(
                _identity.Artist, _identity.Title, _identity.Year, _catno);

            ApplyAlbumNumbers();
            return _album;
        }
        catch (Exception)
        {
            await Note(Loc.Get("Album_CommunityOffline"));
            return null;
        }
    }

    private async void ReportAlbum_Click(object sender, RoutedEventArgs e)
        => await ReportAsync("album", null, Loc.Get("Album_ReportAlbumPrompt"));

    // ── reviews ──

    /// <summary>
    /// How the reviews are ordered, picked from a menu off one key.
    ///
    /// <para>It is the library's own sort picker, down to the tick. The choice in force wears a check instead of its
    /// own glyph, so the menu reserves no gutter for one. Five choices written across the top of the column would read
    /// as a sentence rather than as a control, and would take a line that the reviews need.</para>
    /// </summary>
    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };

        // A glyph each. The one in force wears a tick, because five words in a column all look alike.
        Add(RpmReviewSort.Votes, "Album_SortVotes", "\uf164");        // a thumb, as the arrows are
        Add(RpmReviewSort.Date, "Album_SortDate", "\uf073");          // a calendar
        Add(RpmReviewSort.Length, "Album_SortLength", "\uf036");      // lines of prose
        Add(RpmReviewSort.Positive, "Album_SortPositive", "\uf118");  // a smile
        Add(RpmReviewSort.Negative, "Album_SortNegative", "\uf119");  // a frown

        menu.ShowAt(SortButton);

        void Add(RpmReviewSort sort, string key, string glyph)
        {
            var chosen = sort == _sort;
            var item = new MenuFlyoutItem
            {
                Text = Loc.Get(key),
                FontSize = 12,
                MinHeight = 26,
                Padding = new Thickness(11, 3, 11, 4),
                Icon = new FontIcon
                {
                    Style = (Style)Application.Current.Resources["FaMenuIcon"],
                    Glyph = chosen ? "\uf00c" : glyph,
                },
            };
            item.Click += (_, _) =>
            {
                if (chosen) return;
                _sort = sort;
                _page = 1;
                _ = RefreshReviewsAsync(CancellationToken.None);
            };
            menu.Items.Add(item);
        }
    }

    private void PagePrev_Click(object sender, RoutedEventArgs e) => TurnPage(-1);

    private void PageNext_Click(object sender, RoutedEventArgs e) => TurnPage(1);

    private void TurnPage(int by)
    {
        var wanted = Math.Clamp(_page + by, 1, _pageCount);
        if (wanted == _page) return;

        _page = wanted;
        _ = RefreshReviewsAsync(CancellationToken.None);
    }

    private void VoteUp_Click(object sender, RoutedEventArgs e) => Vote(sender, 1);

    private void VoteDown_Click(object sender, RoutedEventArgs e) => Vote(sender, -1);

    private async void Vote(object sender, int value)
    {
        if ((sender as FrameworkElement)?.Tag is not ReviewRow row || _album is null) return;
        if (!await RequireSignInAsync()) return;

        // Pressing the arrow you are already on takes the vote back. The API means zero by that. So the button is a
        // toggle, not a one-way commitment.
        var wanted = row.MyVote == value ? 0 : value;

        if (Mock)
        {
            // The harness keeps its pools, so a vote cast here survives a sort, a page turn, and
            // leaving the record and coming back to it.
            if (Gallery.GalleryCommunity.Vote(_album.AlbumId, row.UserId, wanted) is { } voted)
                row.Apply(voted);
            return;
        }

        try
        {
            if (await Community.VoteAsync(_album.AlbumId, row.UserId, wanted) is { } updated)
                row.Apply(updated);
        }
        catch (Exception)
        {
            await Note(Loc.Get("Album_CommunityOffline"));
        }
    }

    private async void ReviewMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ReviewRow row } element || _album is null) return;

        var flyout = new MenuFlyout();

        if (row.IsMine)
        {
            var delete = new MenuFlyoutItem { Text = Loc.Get("Album_DeleteReview"), FontSize = 12 };
            delete.Click += async (_, _) => await DeleteMyReviewAsync();
            flyout.Items.Add(delete);
        }
        else
        {
            var report = new MenuFlyoutItem { Text = Loc.Get("Album_ReportReview"), FontSize = 12 };
            report.Click += async (_, _) =>
                await ReportAsync("review", row.UserId, Loc.Get("Album_ReportReviewPrompt"));
            flyout.Items.Add(report);
        }

        flyout.ShowAt(element);
        await Task.CompletedTask;
    }

    private async Task DeleteMyReviewAsync()
    {
        if (_album is null || !await RequireSignInAsync()) return;

        if (Mock)
        {
            Gallery.GalleryCommunity.DeleteMyReview(_album.AlbumId);
            _album = Gallery.GalleryCommunity.Album(_album.AlbumId);

            ApplyAlbumNumbers();
            await RefreshReviewsAsync(CancellationToken.None);
            return;
        }

        try
        {
            await Community.DeleteMyReviewAsync(_album.AlbumId);
            _album = await Community.GetAlbumAsync(_album.AlbumId) ?? _album;

            ApplyAlbumNumbers();
            await RefreshReviewsAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            await Note(Loc.Get("Album_CommunityOffline"));
        }
    }

    /// <summary>
    /// Files a report, after asking what is actually wrong.
    ///
    /// <para>The note is the whole point. A queue of reports that say only "album 41 was reported" cannot be worked
    /// through. The note is optional all the same. Somebody who cannot put the problem into words should still be able
    /// to raise a hand.</para>
    /// </summary>
    /// <param name="targetId">Null for the album itself. Otherwise it is the review author's id, or the comment id.</param>
    private async Task ReportAsync(string targetType, string? targetId, string prompt)
    {
        if (XamlRoot is null) return;
        if (!await RequireSignInAsync()) return;
        if (await EnsureAlbumAsync() is not { } album) return;

        var note = new TextBox
        {
            PlaceholderText = Loc.Get("Album_ReportPlaceholder"),
            // This is prose that a moderator reads, so it is spell checked. The implicit style turns spell check off
            // for the name-and-path fields that make up the rest of the app.
            IsSpellCheckEnabled = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 110,
            MaxLength = 2000,
        };

        var answer = await PromptDialog.ShowAsync(
            XamlRoot,
            Loc.Get("Album_ReportTitle"),
            new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = prompt,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 13,
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                    },
                    note,
                },
            },
            primaryText: Loc.Get("Album_ReportSubmit"),
            closeText: Loc.Get("Common_Cancel"),
            defaultButton: ContentDialogButton.Primary);

        if (answer != ContentDialogResult.Primary) return;

        if (Mock)
        {
            await Note(Loc.Get("Album_ReportSent"));
            return;
        }

        try
        {
            await Community.ReportAsync(
                targetType, album.AlbumId, targetId ?? album.AlbumId, reason: targetType,
                note: note.Text.Trim());

            await Note(Loc.Get("Album_ReportSent"));
        }
        catch (Exception)
        {
            await Note(Loc.Get("Album_CommunityOffline"));
        }
    }

    // ── comments ──

    private void CommentBox_TextChanged(object sender, TextChangedEventArgs e)
        => SendComment.IsEnabled = SignedIn && CommentBox.Text.Trim().Length > 0;

    private async void SendComment_Click(object sender, RoutedEventArgs e)
    {
        var body = CommentBox.Text.Trim();
        if (body.Length == 0) return;
        if (!await RequireSignInAsync()) return;
        if (await EnsureAlbumAsync() is not { } album) return;

        if (Mock)
        {
            Gallery.GalleryCommunity.AddComment(album.AlbumId, body);
            CommentBox.Text = "";
            await RefreshCommentsAsync(CancellationToken.None);
            return;
        }

        try
        {
            await Community.AddCommentAsync(album.AlbumId, body);
            CommentBox.Text = "";
            await RefreshCommentsAsync(CancellationToken.None);
            await RefreshReviewsAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            await Note(Loc.Get("Album_CommunityOffline"));
        }
    }

    /// <summary>
    /// A comment's overflow menu: the same corner, the same glyph and the same two entries a review
    /// carries. Delete what is yours, report what is not.
    ///
    /// <para>Every comment has a menu, not only your own. Otherwise somebody else's comment could not be reported
    /// at all, on a card where strangers write.</para>
    /// </summary>
    private void CommentMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CommentRow row } element || _album is null) return;

        var flyout = new MenuFlyout();

        if (row.IsMine)
        {
            var delete = new MenuFlyoutItem { Text = Loc.Get("Album_DeleteComment"), FontSize = 12 };
            delete.Click += async (_, _) => await DeleteCommentAsync(row);
            flyout.Items.Add(delete);
        }
        else
        {
            var report = new MenuFlyoutItem { Text = Loc.Get("Album_ReportComment"), FontSize = 12 };
            report.Click += async (_, _) =>
                await ReportAsync("comment", row.CommentId, Loc.Get("Album_ReportCommentPrompt"));
            flyout.Items.Add(report);
        }

        flyout.ShowAt(element);
    }

    private async Task DeleteCommentAsync(CommentRow row)
    {
        if (_album is null) return;

        if (Mock)
        {
            Gallery.GalleryCommunity.DeleteComment(_album.AlbumId, row.CommentId);
            await RefreshCommentsAsync(CancellationToken.None);
            return;
        }

        try
        {
            await Community.DeleteCommentAsync(_album.AlbumId, row.CommentId);
            await RefreshCommentsAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            await Note(Loc.Get("Album_CommunityOffline"));
        }
    }

    // ── signing in ──

    private async void SignIn_Click(object sender, RoutedEventArgs e) => await RequireSignInAsync();

    /// <summary>
    /// Makes sure there is an account behind the next write, asking for one if there is not. Coming back signed in
    /// runs the album's load again. A signed-in reader gets an entry created, their own votes marked, and the buttons
    /// switched on.
    /// </summary>
    private async Task<bool> RequireSignInAsync()
    {
        if (SignedIn) return true;
        if (XamlRoot is null) return false;

        if (!await LoginDialog.ShowAsync(Auth, XamlRoot, Loc.Get("Album_SignInToWrite")))
            return false;

        // This is a fresh load of the same record. What has changed is not the shelf but WHO is asking. The answer
        // that has to be asked again is the wall's.
        Load(_folder, AlbumOpen.Fresh);
        return true;
    }

    private async Task Note(string message)
    {
        if (XamlRoot is null) return;

        await PromptDialog.ShowAsync(XamlRoot, title: null, message, closeText: Loc.Get("Common_OK"));
    }
}
