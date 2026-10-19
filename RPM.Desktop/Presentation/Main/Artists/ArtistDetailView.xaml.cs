using RPM.Core.Cloud;
using RPM.Core.Threading;
using System.Collections.ObjectModel;
using RPM.Core.Library;
using RPM.Core.Metadata.Providers;
using RPM.Core.Models;
using RPM.Desktop.Presentation.Main.Artists.Models;
using RPM.Desktop.Presentation.Main.Library;
using RPM.Desktop.Presentation.Main.Library.Models;
using RPM.Desktop.Services.Shell;
using RPM.Desktop.Strings;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace RPM.Desktop.Presentation.Main.Artists;

/// <summary>
/// One artist, opened from the wall of artists. The left column is about the person. The right column
/// lists the collection under their name.
///
/// <para><b>Two columns.</b> The left column holds their photograph, the account of them from the
/// online catalogues and the links to their own pages. It does not change when the reader scrolls.
/// The right column holds the records they made, the records they appear on as a guest, and every
/// song of theirs in the library. Each column scrolls on its own. A list of four hundred songs would
/// otherwise sit beside a photograph that scrolled out of view after the first thirty.</para>
///
/// <para><b>Shelves of records.</b> A record filed under an artist is theirs. A song of theirs on a
/// compilation is one line of another artist's record. If that record were listed as theirs, the page
/// of any guest artist would fill with compilations. So the shelves are kept apart, and the songs are
/// gathered into one listing under all of them. That listing answers "which songs of theirs do I
/// own", which no single shelf can.</para>
///
/// <para><b>A view inside the library card, not a page in the frame.</b> The album page is opened in
/// the same way, for the same reason: the artwork band above and the deck below belong to the app and
/// not to the view. An artist page that removed them would stop the music it is about.</para>
///
/// <para><b>What arrives on its own schedule.</b> Several parts arrive independently. The map of them
/// is in <see cref="FillAsync"/>. Read it before changing anything below.</para>
/// </summary>
public sealed partial class ArtistDetailView : UserControl
{
    /// <summary>The search over the artist's songs. Cmd or Ctrl+F puts the keyboard focus in it (see
    /// MainPage.FocusSearch).</summary>
    internal TextBox TrackSearch => TrackSearchBox;

    private readonly ObservableCollection<ArtistRecord> _records = [];
    private readonly ObservableCollection<ArtistRecord> _guest = [];

    /// <summary>The records they made together with another artist. They are neither their own work
    /// alone nor another artist's record that they appear on. See ArtistWallItem.Together.</summary>
    private readonly ObservableCollection<ArtistRecord> _together = [];

    /// <summary>Their live records, soundtracks, compilations and singles. They are taken out of the
    /// shelves above by the release type that their files carry. Where the files carry none, the name of
    /// the record decides (ArtistShelves.ShelfOf).</summary>
    private readonly ObservableCollection<ArtistRecord> _live = [];

    /// <summary>Their EPs and mixtapes. Only a release type puts a record here: no title shows either
    /// reliably.</summary>
    private readonly ObservableCollection<ArtistRecord> _eps = [];

    private readonly ObservableCollection<ArtistRecord> _mixtapes = [];

    private readonly ObservableCollection<ArtistRecord> _soundtracks = [];

    private readonly ObservableCollection<ArtistRecord> _compilations = [];

    private readonly ObservableCollection<ArtistRecord> _singles = [];
    /// <summary>The songs shown under All tracks. This is every song of the artist until something is
    /// typed in the search field beside it. It is grouped by letter or year when it is long (see
    /// ArtistSongListing).</summary>
    private readonly ArtistSongListing _tracks;

    /// <summary>The listener's own playlists that already hold a song of this artist. This is the one
    /// shelf that is read from the user's playlists and not from the collection (see
    /// <see cref="PlaylistsAsync"/>).</summary>
    private readonly ObservableCollection<ArtistPlaylist> _playlists = [];

    /// <summary>Every song of the artist, whatever is typed in the search. The search narrows only what
    /// is shown. With the full list kept, a wider search does not have to read every folder again.</summary>
    private readonly List<ArtistSong> _everySong = [];

    /// <summary>Cancels the loads of the artist being left. Without it, a slow answer about the first
    /// artist could appear on the page of the third when the reader opens artists quickly.</summary>
    private CancellationTokenSource? _loading;

    /// <summary>The MusicBrainz id the wall was last read with, or null. A repeated read (for example
    /// after the user signs in) uses the same id as the first read, not the weaker read that the page
    /// opened with.</summary>
    private string? _wallMbid;

    /// <summary>The artist on screen, or null before the first load.</summary>
    public ArtistWallItem? Artist { get; private set; }

    /// <summary>Raised to go back to the wall of artists.</summary>
    public event EventHandler? BackRequested;

    /// <summary>The album cover at the given edge size, or null. It is the page's own loader, so a cover
    /// already cut for the wall is not cut again here.</summary>
    public Func<string, int, CancellationToken, Task<byte[]?>>? CoverLoader { get; set; }

    /// <summary>One record's tracks, in disc order.</summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<TrackInfo>>>? TracksLoader { get; set; }

    /// <summary>What the mirror knows about a record: its year, its number of songs, and whether any song
    /// on it is marked explicit. One hook reads all three from one index. It returns a tuple and not
    /// default values, so the compiler lists every caller when a field is added.</summary>
    public Func<string, (int Year, int Tracks, bool Explicit)>? FactsLoader { get; set; }

    /// <summary>The release type that the files of each record carry, by folder
    /// (LibraryDatabase.QueryReleaseTypesByFolder). When unset, no record has a type, and every record is
    /// placed on a shelf by its name.</summary>
    public Func<IReadOnlyDictionary<string, ReleaseType>>? ReleaseTypesLoader { get; set; }

    /// <summary>Plays one song.</summary>
    public Action<TrackInfo>? PlayTrack { get; set; }

    /// <summary>What the band is drawn from: the photograph and its colour, or the colour of the first
    /// record where there is no photograph. It is the wall's own loader, so opening a box does not fetch
    /// again the picture that the box already shows.</summary>
    public Func<ArtistWallItem, CancellationToken, Task<ArtistFace>>? PortraitLoader { get; set; }

    /// <summary>
    /// Every photograph the app can show of one artist, best first. The photograph that the band
    /// already shows is the first one. Empty for an artist with no photograph.
    ///
    /// <para>This is separate from <see cref="PortraitLoader"/> because it answers another question.
    /// <see cref="PortraitLoader"/> says which picture is this artist, and the answer is the same
    /// everywhere in the app. This loader says what other pictures exist, and only the band asks.</para>
    /// </summary>
    public Func<ArtistWallItem, CancellationToken, Task<Asked<IReadOnlyList<string>>>>? GalleryLoader { get; set; }

    /// <summary>
    /// The artist's own logo (the mark from their sleeves, on a transparent background), or null where
    /// no catalogue has one. Null is the normal answer and not an error: most artists have no logo, and
    /// the page shows the written name instead.
    ///
    /// <para>This is its own loader and not a field of <see cref="ArtistFace"/>, because it supplies
    /// another part of the page on another schedule. The face is the photograph and the colour of the
    /// whole page, and the band cannot be drawn until it arrives. The logo is only the title over the
    /// band, only this page uses it, and the page works without it.</para>
    /// </summary>
    public Func<ArtistWallItem, CancellationToken, Task<byte[]?>>? LogoLoader { get; set; }

    /// <summary>
    /// Which of the listener's own playlists hold any of these songs, and how many songs of each.
    ///
    /// <para>It takes the artist's songs and not their name. The page has already decided which songs
    /// are theirs (a record filed under them, or a credit that names them on another artist's record),
    /// and a playlist is on this shelf because it holds one of those songs. A name given to the database
    /// would be a second answer to a question that this page has already answered.</para>
    ///
    /// <para>Null on an app head with no playlists to read. The shelf is then never shown.</para>
    /// </summary>
    public Func<IReadOnlyList<TrackInfo>, CancellationToken,
               Task<IReadOnlyList<RPM.Core.Library.PlaylistHolding>>>? PlaylistsLoader { get; set; }

    /// <summary>The drawn cover of one of those playlists: its records laid out as tiles on a
    /// background of their colours. It is the same picture that heads the track list of that playlist,
    /// from the same source, so a playlist has one picture everywhere. Null for a playlist whose records
    /// have no artwork. The card then keeps its generated figure.</summary>
    public Func<ArtistPlaylist, CancellationToken, Task<byte[]?>>? PlaylistCoverLoader { get; set; }

    /// <summary>Opens one of those playlists. This happens in another view, the Playlists view, where a
    /// playlist is played, reordered and edited. This page only links to it.</summary>
    public Action<long>? OpenPlaylist { get; set; }

    /// <summary>Where the track list, cover and colour of an opened record are read from. It has the
    /// same type that the wall gives its own drawer, because it is the same drawer.</summary>
    public AlbumBandSources? BandSources { get; set; }

    /// <summary>What a press inside an opened record does: play a song, play the record, hand it to the
    /// DJ, or mark it. It is passed straight to the drawer when one opens.</summary>
    public Action<IReadOnlyList<TrackInfo>, bool>? PlayTracks { get; set; }

    public Action<IReadOnlyList<TrackInfo>>? StartDj { get; set; }

    /// <summary>Everything that the context menu of a song inside an opened record needs.</summary>
    public TrackMenuHooks? TrackMenuHooks { get; set; }

    /// <summary>Marks one song as a favourite, or clears the mark. Returns whether the library
    /// accepted it.</summary>
    public Func<TrackInfo, bool, bool>? SetTrackFavorite { get; set; }

    /// <summary>
    /// The path of the song that the deck is playing now, read whenever a line is built.
    ///
    /// <para>It is a function and not a stored value on purpose. This page fills one folder at a time
    /// and opens records some seconds after the press, so lines are still being built long after any
    /// "the song changed" message was sent. A line that reads the path when it is built is always
    /// right. A line that waits for a message can miss the only message it will get.
    /// <see cref="SetPlaying"/> is the other half: it updates the lines that already exist when the
    /// song changes.</para>
    /// </summary>
    public Func<string?>? PlayingPath { get; set; }


    /// <summary>
    /// Which collection is on screen: this computer's, or a shelf shown from another computer.
    ///
    /// <para>It is read each time and not stored, because the answer can change at any moment (the
    /// other computer is closed, or the listener goes back to their own music).</para>
    ///
    /// <para>It still decides what a cloud mark means. On a borrowed song the badge says how the music
    /// arrives (from that computer, or from its cloud storage), not whose disk holds it. The favourite
    /// hearts do not depend on it: a borrowed song has a row in the mirror that is kept for that
    /// computer, so the heart on it is the listener's own.</para>
    /// </summary>
    public Func<RPM.Core.Cloud.ShelfReading>? Shelf { get; set; }

    /// <summary>What the cloud holds of a whole record, for the mark under the cover in the drawer. The
    /// wall tiles and the album list mark their records from the same reading, so one record shows one
    /// answer wherever it is opened.</summary>
    public Func<string, RPM.Core.Library.AlbumCloudBadge>? CloudBadgeLoader { get; set; }

    /// <summary>
    /// The artist's best-known records from Discogs, given every Discogs artist number held for them.
    ///
    /// <para>It takes numbers and not the name. The number is the only reliable identifier at Discogs:
    /// a name search returns the nearest match with a confident score, and the nearest match to an
    /// artist name can be another artist. The numbers come from the Discogs links that the catalogues
    /// already returned for the Elsewhere box. This is why this card waits for the papers and not for
    /// the shelves.</para>
    ///
    /// <para>It takes all the numbers, not the first. One artist can have more than one Discogs page (a
    /// rename, another spelling, a side project filed separately), and the loader ranks across all of
    /// them. See MainPage.Artists, where the walk is written out.</para>
    /// </summary>
    public Func<IReadOnlyList<string>, CancellationToken, Task<PopularAnswer>>? PopularLoader { get; set; }

    /// <summary>Everything that the context menu of a record needs. The wall and the album list get the
    /// same hooks, so a record offers the same menu on an artist page as on the wall.</summary>
    public AlbumMenuHooks? MenuHooks { get; set; }

    /// <summary>What the free catalogues say about the artist: the biography, the outside pages and the
    /// members. The track pane reads the same cache, so an artist whose record has been playing for a
    /// long time opens with the card already filled in.</summary>
    public Func<string, CancellationToken, Task<TrackDossier>>? PapersLoader { get; set; }

    /// <summary>
    /// The same answer when it is already known, returned without a request and without an await.
    ///
    /// <para>The track pane works in the same way. A cold lookup takes seconds (MusicBrainz answers one
    /// request a second, and several are needed). A page that only waited would open empty and add the
    /// card under the reader every time, even for an artist whose biography is already in memory. When
    /// the card is painted on the first frame, an artist that was read once is shown at once.</para>
    /// </summary>
    public Func<string, TrackDossier?>? PapersCached { get; set; }

    public ArtistDetailView()
    {
        InitializeComponent();

        _tracks = new ArtistSongListing(
            App.Services.GetRequiredService<SettingsService>().ArtistTrackGrouping,
            Loc.Get("AlbumGroup_NoYear"));
        // The switch is shown only while there are headings to switch. The listing reports when that
        // changes, so the callers do not have to.
        _tracks.Changed += () => TrackGroupingSwitch.Visibility = _tracks.IsGrouped
            ? Visibility.Visible
            : Visibility.Collapsed;
        PaintTrackGrouping();

        Records.ItemsSource = _records;
        Guest.ItemsSource = _guest;
        Together.ItemsSource = _together;
        Eps.ItemsSource = _eps;
        Live.ItemsSource = _live;
        Mixtapes.ItemsSource = _mixtapes;
        Soundtracks.ItemsSource = _soundtracks;
        Compilations.ItemsSource = _compilations;
        Singles.ItemsSource = _singles;
        Tracks.ItemsSource = _tracks.Items;
        Playlists.ItemsSource = _playlists;

        WallFeedList.ItemsSource = _wall;

        // One timer for the pulse of the cloud marks inside the open record (CloudLineBreath). One is
        // enough because at most one drawer is open, and two timers would repaint twice for each step. It
        // reads both shelves, so it does not need to know which drawer is open.
        _lineBreath = new Common.CloudLineBreath(
            () => Drawers.Select(d => d.Band).OfType<AlbumBand>()
                         .SelectMany(b => b.Lines.Cast<Common.ICloudBadged>().Append(b.Card)));

        Unloaded += (_, _) =>
        {
            _loading?.Cancel();
            _lineBreath.Stop();   // a page that is not shown must not run a timer
            _bandLoading?.Cancel();
            _bandGesture++;   // a record that is still opening has nowhere to unfold

            // Every fade here runs on the render tick. A page removed during a fade would leave the app
            // asking for frames that it has nothing to draw.
            _turning?.Cancel();
            _groundFade?.Stop();
            foreach (var shelf in Shelves)
                shelf.Unfold.Stop();
            _slideshowTimer?.Stop();
            _portraitFade?.Stop();
            _portraitNextFade?.Stop();
        };
    }

    /// <summary>
    /// Shows one artist. Everything below the name arrives asynchronously. The page is useful before
    /// that, and it stays useful if a part never arrives: a record whose cover does not load is still
    /// on its shelf, and an artist unknown to the catalogues still has a collection.
    /// </summary>
    public void Load(ArtistWallItem artist)
    {
        _loading?.Cancel();
        var cts = _loading = new CancellationTokenSource();

        Artist = artist;

        // The written name is set first, on this frame, for every artist. A logo may replace it later
        // (see LogoAsync). The name belongs to this artist. If the logo of the previous artist stayed
        // while the new one loads, the page would be titled with another artist's name.
        ArtistName.Text = artist.Name;
        ArtistName.Visibility = Visibility.Visible;
        ArtistLogo.Visibility = Visibility.Collapsed;
        ArtistLogo.Source = null;

        // How much of the artist is in the collection. The card is hidden for an artist with nothing in
        // the collection (this cannot happen from the wall, but can happen from a link), because an
        // empty card under a heading is worse than no card.
        var counts = Stats(artist);
        ArtistStats.Text = counts;
        LibraryBlock.Visibility = counts.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        _records.Clear();
        _guest.Clear();
        _together.Clear();
        _eps.Clear();
        _live.Clear();
        _mixtapes.Clear();
        _soundtracks.Clear();
        _compilations.Clear();
        _singles.Clear();
        _tracks.Clear();
        _everySong.Clear();
        _playlists.Clear();

        // The wall belongs to the artist being left, and so does the entry it is attached to. They are
        // cleared here and not when the new data arrives, because a wall that stays during the load
        // would show another artist's wall under this artist's name.
        _wall.Clear();
        _wallArtist = null;
        _wallMbid = null;
        WallEmpty.Visibility = Visibility.Collapsed;
        WallBanner(null, canSignIn: false);
        WallEntry.Text = "";
        WallSend.IsEnabled = false;

        // A search belongs to the artist it was typed under. It is cleared directly and not through the
        // debounce: there is nothing to narrow yet, and a running timer would fire on the list of the
        // new artist 200 ms after it arrived.
        _trackSearchDebounce?.Stop();
        TrackSearchBox.Text = "";
        TrackSearchClear.Visibility = Visibility.Collapsed;
        NoTrackMatch.Visibility = Visibility.Collapsed;
        RecordsSection.Visibility = Visibility.Collapsed;
        GuestSection.Visibility = Visibility.Collapsed;
        TogetherSection.Visibility = Visibility.Collapsed;
        EpsSection.Visibility = Visibility.Collapsed;
        LiveSection.Visibility = Visibility.Collapsed;
        MixtapesSection.Visibility = Visibility.Collapsed;
        SoundtracksSection.Visibility = Visibility.Collapsed;
        CompilationsSection.Visibility = Visibility.Collapsed;
        SinglesSection.Visibility = Visibility.Collapsed;
        PlaylistsSection.Visibility = Visibility.Collapsed;
        TracksSection.Visibility = Visibility.Collapsed;

        // The record that was open belonged to the artist being left.
        ShutEvery();

        // Whether the name at the top of the page is a person. The compilations shelf is not. Two things
        // depend on this: the card that explains what the shelf is is shown, and the wait for the papers
        // is not started, because no papers will be requested. It uses the same test as the loaders that
        // decide not to ask the catalogues (see MainPage.NobodyInParticular). It is not a flag passed
        // with the artist, because it is a fact about the name and is true whenever it is asked.
        var shelf = ArtistCredit.IsVariousArtists(artist.Name);
        ShelfNoteBlock.Visibility = shelf ? Visibility.Visible : Visibility.Collapsed;

        AboutBlock.Visibility = Visibility.Collapsed;
        LinksBlock.Visibility = Visibility.Collapsed;
        MembersBlock.Visibility = Visibility.Collapsed;
        LinksRow.Children.Clear();
        MembersList.Children.Clear();
        PopularBlock.Visibility = Visibility.Collapsed;
        PopularConnect.Visibility = Visibility.Collapsed;
        PopularList.Children.Clear();
        _article = null;

        // The photographs belong to the artist being left, and so does the slideshow. It is stopped here,
        // and PhotosAsync starts it again when the list of this artist arrives. The hold is cleared too:
        // stopping the show for one artist does not apply to the next.
        Slideshow(false);
        _slideshowHeld = false;
        _turning?.Cancel();
        _photos.Clear();
        _photo = 0;
        PhotoControls.Visibility = Visibility.Collapsed;

        Portrait.Visibility = Visibility.Collapsed;
        PutPortrait(next: false, picture: null, runOff: null);
        PortraitNext.Visibility = Visibility.Collapsed;
        PutPortrait(next: true, picture: null, runOff: null);
        PortraitGround.Fill = Unlit;
        PortraitVeil.Fill = null;
        PageGround.Visibility = Visibility.Collapsed;
        PageWeight.Visibility = Visibility.Collapsed;
        PageWash.Visibility = Visibility.Collapsed;
        PageWashArt.ImageSource = null;
        Lend();   // also update the window: it must not keep the colours of the artist being left

        // Both scrollers go back to the top. The page is about another artist now, and a column left at
        // the end of the previous biography would open half way down.
        AboutScroll.ChangeView(null, 0, null, disableAnimation: true);
        BodyScroll.ChangeView(null, 0, null, disableAnimation: true);

        // Show what is already known about the artist, on this frame and before anything is awaited. The
        // fetch below still runs. It reads the same cache and repaints with anything learned since, but
        // the reader does not see the card appear twice.
        //
        // The busy indicator is shown only for an artist with nothing known. A spinner over a
        // biography that is already on screen would suggest work that is not happening.
        var known = PapersCached?.Invoke(artist.Name);
        var cached = known is { IsEmpty: false };
        if (cached)
            Papers(known!, artist.Name);

        // It is never shown for the shelf: its loader answers empty without leaving the computer, so a
        // spinner would suggest a catalogue read that is not made.
        PapersBusy.Spinning = !cached && !shelf && PapersLoader is not null;

        Busy.Spinning = true;

        _ = FillAsync(artist, cts.Token);
    }

    /// <summary>Leaving the page. The lists are kept: the page fades out, and emptying them during the
    /// fade would be visible.</summary>
    public void Close()
    {
        _loading?.Cancel();
        _bandLoading?.Cancel();
        _bandGesture++;   // a record that is still being read has nothing to open into

        // The band is about to fade out. A slideshow left running would keep changing photographs
        // behind the wall while the app runs, and would repaint the window each time.
        Slideshow(false);
        _turning?.Cancel();

        // The same applies to the cloud marks inside the record that was left open. The drawer is kept
        // in place during the fade (see below), and a pulsing mark behind a hidden page is a timer that
        // repaints the window twenty times a second. Coming back is a new Load, which closes the drawer
        // and restarts the pulse for whatever is open then.
        _lineBreath.Stop();

        // The two spinners are stopped as well. A load cancelled because the page is left keeps its own
        // spinner on: Waited does not stop the spinner of a cancelled read, because the Load of the next
        // artist has already decided it. There is no next artist here. The page is going, and a spinner
        // left turning would write its angle thirty times a second for the rest of the session and
        // repaint the whole window each time.
        Busy.Spinning = false;
        PapersBusy.Spinning = false;

        // The window goes back to its own backdrop as the page goes. The layers of the page are kept for
        // the same reason as the lists: the page fades out as a whole, and removing the layers during the
        // fade would be visible.
        PageBackdrop.Closed(this);
    }

    /// <summary>The number of records, appearances and songs, as text in the app's wording for counts.</summary>
    private static string Stats(ArtistWallItem artist)
    {
        var parts = new List<string>(3);
        if (artist.Albums.Count > 0)
            parts.Add(Count(artist.Albums.Count, "Stats_AlbumsOne", "Stats_Albums"));
        if (artist.Appearances.Count > 0)
            parts.Add(Count(artist.Appearances.Count, "Artists_AppearsOnOne", "Artists_AppearsOnMany"));
        if (artist.Tracks > 0)
            parts.Add(Count(artist.Tracks, "Stats_SongsOne", "Stats_Songs"));

        return string.Join("  •  ", parts);
    }

    private static string Count(int n, string one, string many) => Loc.Format(n == 1 ? one : many, n);

    /// <summary>
    /// Fills the page. Each part arrives on its own schedule, so this is the map of them.
    ///
    /// <para><b>What arrives independently.</b> (1) the band, from the shared portrait cache: at once
    /// for an artist already on the wall, a fetch otherwise; (2) the shelves and their covers, from
    /// disk and the thumbnail cache; (3) the songs, one folder query per record on a worker thread;
    /// (4) the papers, from MusicBrainz and Wikipedia, which take seconds and not milliseconds; (5) the
    /// width of the panel, which the user can change between any two of the others; (6) another
    /// <see cref="Load"/>, because the reader can go back and open another artist at any time;
    /// (7) the artist's logo, from the art sources, which is a fetch and a decode.</para>
    ///
    /// <para><b>Which of those can meet.</b> The papers and the logo run beside the other parts and not
    /// after them. If they were chained, the shelf of an artist would wait about four seconds for a
    /// rate-limited service, although the shelf does not depend on it. The parts meet only in the artist
    /// column, and they write different elements of it: the band writes the photograph layers, the
    /// papers write the cards under it, and the logo writes the two title elements and nothing else.
    /// The logo is safe because of one rule: <b>only <see cref="Load"/> and <see cref="LogoAsync"/>
    /// write <c>ArtistName</c> and <c>ArtistLogo</c></b>, and <see cref="Load"/> cancels the token that
    /// every <see cref="LogoAsync"/> checks. If the band is ever changed to write the title (for example
    /// a name over a generated figure), the two can race.</para>
    ///
    /// <para><b>Three more, once the band is up.</b> (8) the list of the artist's other photographs,
    /// from the saved order or the art sources. The band starts it and never awaits it. (9) one of those
    /// photographs being fetched because the reader pressed an arrow. It can be in flight while (8) is
    /// still arriving, and the next press can replace it. (10) the timer of the slideshow, which asks
    /// for (9) every fifteen seconds without any action on the page. A wide photo in (1) or (9) is also
    /// baked into a <see cref="RunOff"/> canvas on a worker before it is decoded. The same token check
    /// follows the bake.</para>
    ///
    /// <para><b>Which of those can meet.</b> (8) and (9) touch different things: the list and the index,
    /// and the two picture layers. (9) cannot run before (8) has arrived, because the controls that
    /// start it stay hidden until the list is in. Two instances of (9) can overlap. <c>_turning</c>
    /// handles that: the next press cancels the previous fetch, so a reader who presses an arrow many
    /// times cannot have four photographs arrive on the band in the order the network finishes them.
    /// (10) only calls (9), on the dispatcher, so it is the same arrival. <see cref="Load"/> and
    /// <see cref="Close"/> stop all three. This keeps a slideshow from changing photographs on a page
    /// that nobody sees.</para>
    ///
    /// <para><b>One the reader brings.</b> (15) a press on the band, which stops the slideshow. It can
    /// happen at any time. The case that matters is a press before (8) arrives: the show is started when
    /// the list of photographs arrives, so a press made while that fetch was still out would be
    /// overruled a moment later by a show that starts. <c>_slideshowHeld</c> prevents this. The press
    /// writes it, (8) reads it, and both run on the dispatcher, so neither can see it half written. The
    /// rule that keeps it safe: <b>the flag is cleared in <see cref="Load"/> and nowhere else</b>, so it
    /// means "this reader, this artist". If it were cleared where the show is stopped, for example in
    /// <see cref="Close"/>, a press would last only until the next fetch arrived.</para>
    ///
    /// <para><b>Four more, once a record is opened.</b> (11) the read of the record itself: its track
    /// list, its cover at the drawer size and its background. A press on a cover starts it, and it takes
    /// seconds on a cold folder. (12) the unfold or fold of the drawer, which runs on the render tick and
    /// continues after the press that started it. (13) the background crossing over what the drawer was
    /// standing on, which starts inside (11) and finishes after it. (14) the message from the cloud
    /// engine, which arrives at any time and makes <see cref="RefreshTrackBadges"/> read the folder of
    /// the open record again.</para>
    ///
    /// <para><b>Which of those can meet.</b> All of them, and with each other. The reader can press a
    /// second cover while the first record is still being read, press the same cover again to close it,
    /// press one on the other shelf, or leave the artist, at any point in any of them. Four rules make
    /// that safe. Each is a sentence that a later edit can break:</para>
    ///
    /// <para>· <b>Every press takes a number</b> (<c>_bandGesture</c>). <see cref="Load"/>,
    /// <see cref="Close"/> and Unloaded increase it again, and the code checks it after every await
    /// below. An answer with an older number belongs to a record that the reader has already moved past,
    /// and it touches nothing.</para>
    ///
    /// <para>· <b>Each shelf's drawer has its own walk</b> (<see cref="BandUnfold"/>). The two drawers
    /// move at the same time, for example the guest shelf folds while the records shelf opens. One walk
    /// for the whole page would let one drawer cancel the other where it stands. A walk replaces only
    /// the walk of its own drawer. This is why a fold that finishes asks <c>Owns</c> before it removes
    /// the band: a newer press may have taken that element over during the fold.</para>
    ///
    /// <para>· <b>The reading of the cloud state is numbered too</b> (<c>_badgePass</c>), and it checks
    /// that the drawer still holds the band that the reading was made for. (14) can arrive twice inside
    /// one folder query (a song finishes while another starts). It can also arrive in the middle of
    /// (11), while the drawer is being filled with a different record. The pulse is synced only from the
    /// newest pass, so an old reading cannot start a timer for marks that no longer exist.</para>
    ///
    /// <para>· <b>The cross-fade asks the drawer, not the press</b>, whether anybody is watching (see
    /// <see cref="CrossGround"/>). A record read while its drawer is still folded to nothing is dressed
    /// in one frame. A record read into a drawer that is already on screen cross-fades. The answer is
    /// taken from the state of the element and not remembered from the press, so it is right however
    /// long the read took and whatever happened during it.</para>
    ///
    /// <para><b>What makes a stale answer safe.</b> There is one token per load, and the next load
    /// cancels it. Every await in this file is followed by a check of the token before anything on
    /// screen is touched. A later edit can break this: an await whose continuation writes to a control
    /// without checking the token first lets the page of the third artist show the photograph of the
    /// first.</para>
    ///
    /// <para><b>The width.</b> <see cref="Page_SizeChanged"/> can move the whole artist column into the
    /// shelf scroller while any of these are in flight. This is safe because both run on the dispatcher
    /// and neither yields in the middle of a change, and because everything here is written by name
    /// (<c>LinksRow</c>, <c>AboutProse</c>), which follows the panel wherever it is parented. A
    /// parent-relative lookup would make the same code wrong.</para>
    /// </summary>
    private async Task FillAsync(ArtistWallItem artist, CancellationToken ct)
    {
        // Started first and not awaited here (see the map above). Each has its own catch, so nothing
        // that any of them does can come back to this method.
        _ = PapersAsync(artist, ct);
        _ = LogoAsync(artist, ct);
        _ = WallAsync(artist, mbid: null, ct);

        try
        {
            await BandAsync(artist, ct);
            await ShelvesAsync(artist, ct);
            await SongsAsync(artist, ct);

            // Last, and it has to be: it is a question about the songs above, so it cannot be asked
            // until they are all in. See PlaylistsAsync.
            await PlaylistsAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return;   // the reader went to another page
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                Busy.Spinning = false;
        }
    }

    /// <summary>The band across the top and the colour of the whole page, from the same cache as the
    /// boxes on the wall. The band shows the artist's square image where fanart.tv has one, while the
    /// box on the wall shows their backdrop. For those artists the picture of the band may still need a
    /// fetch when the page opens. For all other artists it is the picture of the box, which is already
    /// held.</summary>
    private async Task BandAsync(ArtistWallItem artist, CancellationToken ct)
    {
        if (PortraitLoader is not { } load)
            return;

        var face = await load(artist, ct);
        if (ct.IsCancellationRequested)
            return;

        // The colour of the page, from whichever of the two the artist has. The gradient is the one of
        // the wall, generated from their name, so the page that a box opens into is that box made wider.
        Dress(face.Sleeve ?? face.Tint, artist.Name);

        // With no photograph, the band has the colour of the first record of the artist, made in the
        // same way as their box on the wall. The same name gives the same result, so the box that the
        // reader pressed opens into a wider version of itself.
        if (face.Sleeve is { } sleeve)
        {
            PortraitGround.Fill = ArtistGround.From(sleeve, artist.Name) ?? Unlit;
            return;
        }

        // The artist's colour over the photograph, as on their box on the wall. It comes from the
        // dominant tone of the picture, snapped to the palette. The box and the page use the same two
        // steps, so they cannot show two colours for one artist.
        PortraitVeil.Fill = face.Tint is { } colour ? ArtistVeil.Page(RpmPalette.Nearest(colour)) : null;

        // The blurred copy behind the whole panel comes before the sharp one in the band. It is the
        // background of the page, and drawing the background after the foreground would show the page
        // being assembled.
        if (face.Wash is { Length: > 0 } blurred)
        {
            PageWashArt.ImageSource = await RemoteImage.DecodeAsync(blurred);
            if (ct.IsCancellationRequested)
                return;
            PageWash.Visibility = Visibility.Visible;
            Weigh();
        }

        // Choice of picture: the banner first, which only a generated figure has, because it is drawn
        // to the shape of this band and not to the shape of a box. Then the picture in its own colours.
        // The grey copy of the wall is used only if nothing else exists. The conversion to grey can fail
        // on a picture that decodes correctly, and a grey photograph is better than an empty band.
        if ((face.Banner ?? face.Original ?? face.Picture) is not { Length: > 0 } picture)
            return;

        // A wide photo is drawn at the width of the band with its reflection under it (see RunOff). Only
        // photos: a banner is a drawing made to the shape of the band.
        var baked = face.Banner is null ? await RunOff.BakeAsync(picture, ct) : null;
        if (ct.IsCancellationRequested)
            return;

        var first = await RemoteImage.DecodeAsync(baked?.Canvas ?? picture);
        if (ct.IsCancellationRequested)
            return;

        Show(first, baked?.Aspect, dissolve: false);

        // The rest of the photographs follow. They are started and not awaited: the picture above is
        // already on screen, and this waits for a catalogue read on its own schedule.
        _ = PhotosAsync(artist, ct);
    }

    // ── the title over the band ──

    /// <summary>The tallest a logo is drawn, in points. It is held here and not in the markup because
    /// the picture is cut to this height (see below): this number times the display scale is the number
    /// of pixels of mark that are requested. If the cut did not agree with the cap, the mark would be
    /// resized twice, once well and once at draw time.
    ///
    /// <para><see cref="ArtistPageLayout.LogoWidthShare"/> is scaled in the same proportion as this
    /// value. A mark can be limited by either bound, and scaling both reduces every mark by the same
    /// factor. The written name is not scaled, so a page with a logo reads smaller than a page with the
    /// written name.</para></summary>
    private const double LogoCap = 50;

    /// <summary>
    /// The artist's own logo over the band, in place of their name, where a catalogue has one.
    ///
    /// <para>It is its own arrival and can happen at any time: after the band is dressed, before it, or
    /// on a page that the reader has already left. Only <see cref="Load"/> also writes these two
    /// elements, and it cancels the token of this method first. So the only ordering that matters is the
    /// stale one, and the token check after the fetch handles it. The elements are written by name, so
    /// this is right on whichever side of the column's fold the panel is parented.</para>
    ///
    /// <para>It is silent about every failure. A logo that nobody has, a source that is down and a
    /// picture that cannot be decoded have the same result for the page: the artist's name in writing,
    /// which is already on screen.</para>
    /// </summary>
    private async Task LogoAsync(ArtistWallItem artist, CancellationToken ct)
    {
        if (LogoLoader is not { } load)
            return;

        try
        {
            if (await load(artist, ct) is not { Length: > 0 } bytes || ct.IsCancellationRequested)
                return;

            // The picture is cut to the height it is drawn at, here, by the app's own resampler (see
            // ArtScaler.FitHeight). Do not give the published picture to the bitmap decoder with a size.
            // A logo is the one picture in the app that reaches a surface with no cache behind it. It
            // comes from the catalogue about 800 pixels wide and is drawn about an inch tall, which is a
            // four-fold reduction. At that ratio the decoder reads sixteen source pixels for each output
            // pixel and drops the rest, which makes the lettering look compressed.
            var scale = XamlRoot?.RasterizationScale ?? 1;

            // The height at which a mark may be drawn is not the cap alone. A mark is drawn in its own
            // proportions, so a long name set as one word is several times wider than it is tall, and it
            // would cross the whole band at a height that is reasonable for a short name. The height is
            // therefore the lower of two limits: the cap, and the height at which the mark still fits
            // across ArtistPageLayout.LogoMaxWidth.
            //
            // It is taken from the source bounds, here, before the cut, because the cut is made to this
            // number. If it were decided afterwards, the picture would be cut to one height and drawn at
            // another, which is the double resize that the cut avoids.
            //
            // A band that has not been laid out gives no room. That is a stated answer, not a missing
            // one: the cap alone applies, and HeroBand_SizeChanged applies the limit as soon as there is
            // a width to read.
            var (wide, high) = ArtScaler.BoundsOf(bytes);
            var room = ArtistPageLayout.LogoMaxWidth(HeroBand.ActualWidth);
            var cap = room > 0 && wide > 0 && high > 0
                ? Math.Min(LogoCap, room * high / wide)
                : LogoCap;

            var wanted = (int)Math.Ceiling(cap * scale);
            if (await Cancellable.RunAsync(() => ArtScaler.FitHeight(bytes, wanted), ct)
                    is not { Ran: true, Value: { } fitted }
                || ct.IsCancellationRequested)
                return;

            var (_, tall) = ArtScaler.BoundsOf(fitted);
            var mark = await RemoteImage.DecodeAsync(fitted);
            if (ct.IsCancellationRequested)
                return;

            // Drawn at the height that came back from the cut, not at the cap that was requested. Most
            // marks are published taller than the cap and arrive cut to it exactly, one bitmap pixel for
            // each screen pixel, which is as sharp as a picture can be. A mark published shorter than the
            // cap keeps its own size, because enlarging it to the cap gives the same blur by another
            // route. A short logo and a tall one both sit on the foot of the band, so the difference
            // shows as a smaller mark and not as a mark in the wrong place.
            ArtistLogo.MaxHeight = tall > 0 ? Math.Min(cap, tall / scale) : cap;
            ArtistLogo.Source = mark;
            ArtistLogo.Visibility = Visibility.Visible;

            // Exactly one of the two is shown. The name is not only covered: it is two lines of 28-point
            // bold, and if it stayed under a mark it would set the height of the row.
            ArtistName.Visibility = Visibility.Collapsed;

            // The page still gives the name to anything that cannot see a picture.
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ArtistLogo, artist.Name);
        }
        catch (OperationCanceledException)
        {
            // The reader went to another artist.
        }
        catch
        {
            // No logo is shown. The name is already on screen and stays there.
        }
    }

    // ── the artist's photographs, and paging through them ──

    /// <summary>How long one photograph is held before the slideshow moves on. It is long enough for the
    /// page to read as a page with a picture and not as a screensaver.</summary>
    private const double PhotoHoldSeconds = 15;

    /// <summary>How long one picture takes to dissolve into the next.</summary>
    private const double PhotoDissolveMs = 420;

    /// <summary>Every photograph of the artist on screen. The one that the band opened with is first.
    /// With fewer than two, there is nothing to page between and the controls stay hidden.</summary>
    private readonly List<string> _photos = [];

    /// <summary>Which of them is shown.</summary>
    private int _photo;

    /// <summary>Whether the picture on screen is on <c>PortraitNext</c> and not on <c>Portrait</c>.
    /// The two layers take turns; neither is the permanent one.</summary>
    private bool _showingNext;

    private ChromeFade? _portraitFade;
    private ChromeFade? _portraitNextFade;

    /// <summary>Whether the slideshow is running. It is off each time a page opens, and
    /// <see cref="PhotosAsync"/> starts it when there is more than one photograph to turn.</summary>
    private bool _slideshow;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _slideshowTimer;

    /// <summary>Whether the reader has stopped the slideshow on this page by pressing the picture.
    ///
    /// <para>It is needed because two things race. The list of photographs is fetched (arrival 8), and
    /// the slideshow starts when it arrives. The press that stops the show can happen at any time,
    /// including before the list has arrived. Without this flag, a reader who pressed the picture during
    /// the fetch would see the show start a moment later. The page would overrule a decision that the
    /// reader already made.</para>
    ///
    /// <para>Only <see cref="Load"/> clears it, so it applies per artist and not per app. It records an
    /// answer about the page being read, and the next page has not been told anything.</para></summary>
    private bool _slideshowHeld;

    /// <summary>Cancels the fetch of the picture that is being moved to. It is separate from the token
    /// of the page because the next press cancels it as well as the next artist. A reader who presses an
    /// arrow many times must not have four photographs arrive on the band in the order they finish.</summary>
    private CancellationTokenSource? _turning;

    /// <summary>
    /// The list of photographs, once the catalogues have been read.
    ///
    /// <para>It checks the token after the await, like everything else in this file. The reader can be
    /// on the page of another artist when the list of one artist comes back, and controls that appeared
    /// for the wrong artist would page that page through photographs of a stranger.</para>
    /// </summary>
    private async Task PhotosAsync(ArtistWallItem artist, CancellationToken ct)
    {
        if (GalleryLoader is not { } load)
            return;

        try
        {
            var answer = await load(artist, ct);
            if (ct.IsCancellationRequested)
                return;

            // The list could not be read. The band keeps the one picture it has, with no controls and no
            // slideshow, as for an artist with one photo. Nothing is stored, so the list is asked for
            // again the next time the artist is opened.
            if (!answer.Answers(out var photos, out _))
                return;

            _photos.Clear();
            _photos.AddRange(photos);
            _photo = 0;

            // The loader puts the picture that is already on screen first, so index 0 is the one shown.
            PhotoControls.Visibility = _photos.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

            // The show starts here and not in Load, because this is the first moment when there is
            // anything to turn. If it started in Load, it would tick against an empty list, and the
            // fifteen seconds would be used before the photographs arrived.
            //
            // Two things must not be overruled, and the token check above covers only the first: the
            // reader having left for another artist. This check covers the reader still being here and
            // having already stopped the show (see _slideshowHeld).
            if (_photos.Count > 1 && !_slideshowHeld)
                Slideshow(true);
        }
        catch (OperationCanceledException)
        {
            // The reader went to another artist.
        }
        catch
        {
            // No catalogue answered. The band keeps the one picture it has and says nothing about it.
        }
    }

    private void PhotoPrevious_Click(object sender, RoutedEventArgs e) => Turn(-1, byHand: true);

    private void PhotoNext_Click(object sender, RoutedEventArgs e) => Turn(1, byHand: true);

    private void PhotoAuto_Click(object sender, RoutedEventArgs e)
    {
        _slideshowHeld = _slideshow;   // pressing it off is the same answer as pressing the picture
        Slideshow(!_slideshow);
    }

    /// <summary>
    /// A press on the picture stops the slideshow for as long as the reader stays on this artist.
    /// <see cref="_slideshowHeld"/> keeps it stopped, so a list of photographs that was still in flight
    /// when the press happened does not start it again.
    ///
    /// <para>It only stops. A press means "leave it where it is", said by a reader who is looking at the
    /// photograph that is shown. If it were a toggle, a second press to hold the page still would start
    /// the turning again, which is the opposite of what the first press asked for.</para>
    ///
    /// <para>The arrows and the back button never reach this method (a Button marks the press as
    /// handled), so this is a press on the photograph itself and nothing else.</para>
    /// </summary>
    private void HeroBand_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_slideshow)
            return;

        _slideshowHeld = true;
        Slideshow(false);
    }

    /// <summary>
    /// One step through the artist's photographs, forwards or back, wrapping at both ends.
    /// </summary>
    /// <param name="byHand">Whether a person pressed an arrow. A press while the slideshow is running
    /// restarts its clock. Without that, the show could move away from a photograph a second after the
    /// reader chose it.</param>
    private void Turn(int step, bool byHand)
    {
        if (_photos.Count < 2)
            return;

        _photo = ((_photo + step) % _photos.Count + _photos.Count) % _photos.Count;

        if (byHand && _slideshow && _slideshowTimer is { } timer)
        {
            timer.Stop();
            timer.Start();
        }

        _ = TurnToAsync(_photos[_photo], step);
    }

    /// <summary>
    /// Fetches one photograph (from the shared cache, so a picture already seen costs nothing) and
    /// dissolves the band into it.
    ///
    /// <para><b>Three outcomes, kept apart.</b> The picture: it is shown. No picture at the address (a
    /// 404, or a page instead of a picture): the photo is removed from the pager and the turn continues
    /// to the next one in the same direction, so a press always lands on a picture. Could not be
    /// fetched: the photo stays in the pager, the band keeps the picture it has, and the index has still
    /// moved, so the next press goes on and does not retry it.</para>
    ///
    /// <para>The first two outcomes are kept apart because a photo that no longer exists would otherwise
    /// stay in the pager as a page that never shows. A press on it would do nothing, and the slideshow
    /// would hold the previous photo twice as long.</para>
    /// </summary>
    private async Task TurnToAsync(string url, int step)
    {
        _turning?.Cancel();
        var cts = _turning = new CancellationTokenSource();
        var ct = cts.Token;

        try
        {
            var answer = await ArtistPortraits.PictureAsync(url, ct);
            if (ct.IsCancellationRequested)
                return;

            if (!answer.Answers(out var picture, out _))
                return;

            if (picture is null)
            {
                DropGonePhoto(url, step);
                return;
            }

            var baked = await RunOff.BakeAsync(picture.Thumb, ct);
            if (ct.IsCancellationRequested)
                return;

            var bitmap = await RemoteImage.DecodeAsync(baked?.Canvas ?? picture.Thumb);
            if (ct.IsCancellationRequested)
                return;

            Show(bitmap, baked?.Aspect, dissolve: true);
        }
        catch (OperationCanceledException)
        {
            // Another press, or another artist.
        }
        catch
        {
            // Not a picture that this computer can read.
        }
    }

    /// <summary>
    /// Removes a photo that the host says does not exist from the pager, and turns on to the next one in
    /// the same direction. Only the turn that is still current (its token has not fired) calls it, so
    /// the list is this artist's and no newer press has moved the index.
    ///
    /// <para>The photo is removed by address and not by index, and the index is then set from the
    /// position of the address: the photo after it now stands in that place. With fewer than two photos
    /// left, the controls are hidden and the slideshow stops, as for an artist with one photo.</para>
    /// </summary>
    private void DropGonePhoto(string url, int step)
    {
        var at = _photos.FindIndex(u => string.Equals(u, url, StringComparison.OrdinalIgnoreCase));
        if (at < 0)
            return;

        _photos.RemoveAt(at);
        PhotoControls.Visibility = _photos.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        if (_photos.Count < 2)
        {
            Slideshow(false);
            return;
        }

        // Turn adds the step, so stand one short of the photo that now fills the removed slot (going
        // forward) or on the slot itself (going back, where the next photo is the one before it).
        _photo = step > 0 ? at - 1 : at;
        Turn(step, byHand: false);
    }

    /// <summary>
    /// Puts a picture in the band, on whichever of the two layers is hidden. That layer then comes up as
    /// the other goes. The first picture of a page arrives without a dissolve: there is nothing under it
    /// to dissolve from, and a page that faded in its own photograph would show itself being assembled.
    /// </summary>
    /// <param name="runOff">The aspect of the photo when <paramref name="picture"/> is a
    /// <see cref="RunOff"/> canvas, and null for a picture that fills the band. It is required, because
    /// the depth of the foot dissolve depends on it.</param>
    private void Show(Microsoft.UI.Xaml.Media.Imaging.BitmapImage picture, double? runOff, bool dissolve)
    {
        var incomingIsNext = !_showingNext;
        var incoming = incomingIsNext ? PortraitNext : Portrait;
        var leaving = incomingIsNext ? Portrait : PortraitNext;

        PutPortrait(incomingIsNext, picture, runOff);
        incoming.Visibility = Visibility.Visible;
        _showingNext = incomingIsNext;

        var over = dissolve ? PhotoDissolveMs : 0;
        Dissolving(incoming).To(1, over);
        Dissolving(leaving).To(0, over);

        // The depth of the dissolve follows the picture that is now up. The size of the band may not
        // change, so the update is called here.
        UpdateHeroFade((float)HeroLayers.ActualWidth, (float)HeroLayers.ActualHeight);
    }

    /// <summary>The aspect of the photo for each band layer while it shows a <see cref="RunOff"/>
    /// canvas, and null while it shows a picture that fills the band. Only
    /// <see cref="PutPortrait"/> writes it.</summary>
    private double? _portraitRunOff;
    private double? _portraitNextRunOff;

    /// <summary>The same, for the layer that is up.</summary>
    private double? ShowingRunOff => _showingNext ? _portraitNextRunOff : _portraitRunOff;

    /// <summary>
    /// Writes the picture of one band layer and whether it is a reflected wide photo. It is the only
    /// place that writes either, so the picture of a layer and the dissolve depth read for it cannot
    /// disagree.
    ///
    /// <para>Both kinds are drawn in the same way, covering the band from the top (see the markup). For
    /// a reflected photo this means the canvas is scaled to the width of the band and the mirror runs
    /// off the bottom.</para>
    /// </summary>
    private void PutPortrait(bool next, ImageSource? picture, double? runOff)
    {
        (next ? PortraitNextArt : PortraitArt).ImageSource = picture;

        if (next)
            _portraitNextRunOff = runOff;
        else
            _portraitRunOff = runOff;
    }

    /// <summary>The opacity of one layer, eased on the render tick. See <see cref="ChromeFade"/> for
    /// why this is not a storyboard.</summary>
    private ChromeFade Dissolving(FrameworkElement layer)
        => layer == Portrait
            ? _portraitFade ??= new ChromeFade(Portrait)
            : _portraitNextFade ??= new ChromeFade(PortraitNext);

    /// <summary>Starts or stops the slideshow. It writes the glyph and the tooltip of the switch even
    /// though the switch is collapsed. The button is hidden and not removed, and a state that it does
    /// not carry while hidden would be wrong when it comes back. The picture shows the state: it is
    /// turning, or stopped where the reader left it.</summary>
    private void Slideshow(bool on)
    {
        _slideshow = on;
        PhotoAutoGlyph.Text = on ? "\uf04c" : "\uf04b";
        IconName.Set(PhotoAuto,
            Loc.Get(on ? "Artists_PhotoSlideshowStop" : "Artists_PhotoSlideshowStart"));

        if (!on)
        {
            _slideshowTimer?.Stop();
            return;
        }

        if (_slideshowTimer is null)
        {
            _slideshowTimer = DispatcherQueue.CreateTimer();
            _slideshowTimer.Interval = TimeSpan.FromSeconds(PhotoHoldSeconds);
            _slideshowTimer.IsRepeating = true;
            _slideshowTimer.Tick += (_, _) => Turn(1, byHand: false);
        }

        _slideshowTimer.Stop();
        _slideshowTimer.Start();
    }

    /// <summary>The artist's colour across the panel, or the surface of the panel where there is none.
    /// The weight is shown and hidden with the colour: over the bare surface it would be a black sheet
    /// over a grey one.</summary>
    private void Dress(Color? colour, string name)
    {
        var ground = colour is { } c ? ArtistGround.From(c, name) : null;

        PageGround.Fill = ground;
        PageGround.Visibility = ground is null ? Visibility.Collapsed : Visibility.Visible;
        Weigh();
    }

    /// <summary>
    /// The black layer is shown when there is something under it to darken, and hidden when there is
    /// not.
    ///
    /// <para>It checks both layers and not the colour alone. The blurred photograph is laid on at more
    /// than half strength because this layer is over it. An artist whose picture arrived but whose
    /// palette could not be read would otherwise show a half-strength photograph with nothing to hold
    /// down its bright areas, which gives white writing on a bright backdrop. Over the plain surface of
    /// the panel, the layer would be a black sheet over a grey one for no reason.</para>
    /// </summary>
    private void Weigh()
    {
        PageWeight.Visibility =
            PageGround.Visibility == Visibility.Visible || PageWash.Visibility == Visibility.Visible
                ? Visibility.Visible
                : Visibility.Collapsed;

        Lend();
    }

    /// <summary>
    /// Gives the ground of the page to the whole window, so that while this artist is open and no
    /// artwork covers the app, the app shows their colours and not its own backdrop.
    ///
    /// <para>It reads the layers themselves and not the answers that wrote them. That is what makes it
    /// safe to call from one place: the colour and the photograph arrive on their own schedules (see
    /// the map on <see cref="FillAsync"/>), so what the page shows is a question about the panel as it
    /// is now, not about which of the two arrived last. It gives nothing where the panel shows its own
    /// surface: <see cref="Weigh"/> has just asked the same question for the black layer.</para>
    /// </summary>
    private void Lend()
    {
        var lit = PageGround.Visibility == Visibility.Visible;
        var washed = PageWash.Visibility == Visibility.Visible;

        PageBackdrop.Opened(this, lit || washed
            ? new PageBackdrop.Ground(
                lit ? PageGround.Fill : null,
                washed ? PageWashArt.ImageSource : null,
                PageWash.Opacity,
                Veil: 0,   // the artist page darkens the photograph with a layer over it, not inside it
                PageWeight.Fill)
            : null);
    }

    /// <summary>
    /// The shelves: the records filed under the artist, the ones made with another artist, the live
    /// records, the soundtracks, the compilations, the singles, and the records of other artists that
    /// they appear on.
    ///
    /// <para>The shelf of a record is decided from two different things. Who made it comes from the
    /// collection (see ArtistWall, which decides it once for every view). What it is comes from its
    /// release type or its name (see ArtistShelves.ShelfOf), because no tag says reliably whether a
    /// record is live, written for a film, released alone, or put together from records that already
    /// existed.</para>
    ///
    /// <para>Names first and covers second, in that order: the shelves are on screen as soon as the
    /// folders are known, and the covers arrive into a page that is already laid out. The page does not
    /// grow one cover at a time.</para>
    /// </summary>
    private async Task ShelvesAsync(ArtistWallItem artist, CancellationToken ct)
    {
        // This method throws on a cancelled token and does not return quietly. This is the one place on
        // the page where it matters: both shelves are shared with the artist who is opened next, so an
        // old pass that got this far would add the records of one artist to the page of another. The
        // band above returns quietly on a cancelled token, so there is no throw to rely on from there.
        ct.ThrowIfCancellationRequested();

        // One record, one shelf: the release type that its files carry, and where they carry none what
        // its name says, in the order single, live, soundtrack, compilation (ArtistShelves.ShelfOf, the
        // same rule as the Pegasus artist page). A type set by the listener always wins, so "Live
        // Through This" set to Album stays with the albums.
        var types = ReleaseTypesLoader?.Invoke() ?? new Dictionary<string, ReleaseType>();
        IReadOnlyList<string> Kind(IEnumerable<string> folders, ReleaseType type)
            => [.. folders.Where(folder => ArtistShelves.ShelfOf(folder, types) == type)];

        Fill(_records, Kind(artist.Albums, ReleaseType.Album));
        Fill(_together, Kind(artist.Together, ReleaseType.Album));
        Fill(_eps, Kind(artist.Own, ReleaseType.Ep));
        Fill(_singles, Kind(artist.Own, ReleaseType.Single));
        Fill(_live, Kind(artist.Own, ReleaseType.Live));
        Fill(_mixtapes, Kind(artist.Own, ReleaseType.Mixtape));
        Fill(_soundtracks, Kind(artist.Own, ReleaseType.Soundtrack));
        Fill(_compilations, Kind(artist.Own, ReleaseType.Compilation));

        // Records of other artists that they appear on are not sorted by type. The shelf shows which
        // records they are on, and the name on the cover says nothing about this artist.
        Fill(_guest, artist.Appearances);

        RecordsSection.Visibility = _records.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        GuestSection.Visibility = _guest.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TogetherSection.Visibility = _together.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EpsSection.Visibility = _eps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LiveSection.Visibility = _live.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MixtapesSection.Visibility = _mixtapes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SinglesSection.Visibility = _singles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SoundtracksSection.Visibility =
            _soundtracks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CompilationsSection.Visibility =
            _compilations.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (CoverLoader is null)
            return;

        await SleevesAsync(_records, ct);
        await SleevesAsync(_together, ct);
        await SleevesAsync(_eps, ct);
        await SleevesAsync(_live, ct);
        await SleevesAsync(_mixtapes, ct);
        await SleevesAsync(_soundtracks, ct);
        await SleevesAsync(_compilations, ct);
        await SleevesAsync(_singles, ct);
        await SleevesAsync(_guest, ct);

        /// <summary>
        /// One shelf, newest record first.
        ///
        /// <para>The order is by year and not the order in which the folders come back, which is the
        /// order on disk. An alphabetical shelf would put a compilation of b-sides before the record that
        /// made the artist, and the first letter of an album name tells the reader nothing. Read from
        /// left to right, the shelf is the discography backwards: what the artist did last, then what
        /// they did before. That is usually what the reader of an artist page wants, and it puts the
        /// record they most likely want at the end that the shelf starts scrolled to.</para>
        ///
        /// <para>A record with no year goes to the end, and not to whichever end a zero sorts to. An
        /// untagged record is neither the newest nor the oldest of the artist. Its date is unknown, so it
        /// goes after everything that the collection can date. Records with the same year (the tags
        /// carry only a year, and an artist can release three in one) are sorted by name, so the shelf
        /// is the same every time it is built.</para>
        /// </summary>
        void Fill(ObservableCollection<ArtistRecord> shelf, IReadOnlyList<string> folders)
        {
            var found = new List<(int Year, string Title, ArtistRecord Record)>(folders.Count);

            foreach (var folder in folders)
            {
                ct.ThrowIfCancellationRequested();

                var facts = FactsLoader?.Invoke(folder) ?? (0, 0, false);

                // The title has no edition, as the wall tiles print a record (see TitleAsides.NameOnly).
                // A cover on a shelf is a picture with two short lines under it, and "(Deluxe Edition)"
                // matters little there. Three pressings of one record on one shelf would be three lines
                // that differ only in their brackets. Nothing is hidden: the drawer that this cover
                // opens is the album wall's own, and it prints the whole title with the aside faded.
                var title = TitleAsides.NameOnly(TitleOf(folder));
                found.Add((facts.Year, title, new ArtistRecord(
                    folder, title, Subtitle(facts.Year, facts.Tracks), facts.Explicit,
                    ShelfMetrics)));
            }

            foreach (var entry in found
                         .OrderByDescending(r => r.Year > 0)
                         .ThenByDescending(r => r.Year)
                         .ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase))
                shelf.Add(entry.Record);
        }
    }

    private async Task SleevesAsync(ObservableCollection<ArtistRecord> shelf, CancellationToken ct)
    {
        if (CoverLoader is not { } cover)
            return;

        foreach (var record in shelf.ToList())
        {
            if (ct.IsCancellationRequested)
                return;

            // Through CoverFill, which also records that a record has no artwork when nothing came
            // back. The generated cover depends on that fact, and with it a shelf of tagged records does
            // not paint a blurred figure on each card and then cover it. See CoverFill.
            await Services.Art.CoverFill.DressAsync(
                record,
                token => cover(record.Folder, Services.Art.ArtScaler.TileEdge, token),
                async bytes => await RemoteImage.DecodeAsync(bytes, ShelfDecodeEdge()),
                ct);
        }
    }

    /// <summary>
    /// Every song of the artist that the collection holds, from all shelves at once.
    ///
    /// <para>Their own records give every song on them. A record filed under an artist is theirs,
    /// including a guest verse or an interlude, and removing those would give a page that disagrees with
    /// the album. The record of another artist gives only the songs whose credit names them. A
    /// compilation is on this page because one of its twenty songs is theirs and the other nineteen are
    /// not.</para>
    ///
    /// <para>There is one folder query per record, on a worker thread, and each result is appended as it
    /// arrives. A prolific artist has about thirty of them. They are cheap and can be cancelled, and the
    /// listing fills one record at a time and does not stay empty until the last one answers.</para>
    /// </summary>
    private async Task SongsAsync(ArtistWallItem artist, CancellationToken ct)
    {
        if (TracksLoader is not { } load)
            return;

        ct.ThrowIfCancellationRequested();

        await Gather(artist.Albums, theirs: true);
        await Gather(artist.Appearances, theirs: false);

        async Task Gather(IReadOnlyList<string> folders, bool theirs)
        {
            foreach (var folder in folders)
            {
                if (ct.IsCancellationRequested)
                    return;

                var tracks = await load(folder, ct);
                if (ct.IsCancellationRequested)
                    return;

                foreach (var track in tracks)
                {
                    if (!theirs && !ArtistWall.Names(track.Artist, artist.Name))
                        continue;

                    var song = new ArtistSong(
                        track, folder, SongSubtitle(track, folder), Length(track.DurationSeconds))
                    {
                        // Read on the frame when the line is made. A long listing can still be filling
                        // minutes after it was opened, and the playing song can change in that time.
                        IsPlaying = Same(track.FilePath, PlayingPath?.Invoke()),
                    };
                    _everySong.Add(song);

                    // Records are still arriving while the reader types, and a song that arrives after a
                    // search was typed must match it too. Otherwise the listing would fill up again with
                    // everything that the search hides.
                    if (Matches(song, _trackSearch))
                        _tracks.Add(song);
                }

                if (_everySong.Count > 0)
                    TracksSection.Visibility = Visibility.Visible;

                ShowNoTrackMatch();
            }
        }
    }

    // ── the listener's own playlists, read under this artist ──

    /// <summary>
    /// The shelf of playlists that already hold a song of this artist.
    ///
    /// <para><b>Why it is last.</b> It is a question about <c>_everySong</c>, and that list fills one
    /// record at a time during <see cref="SongsAsync"/>. If it were asked half way through, the answer
    /// would be "the playlists that hold a song from whichever records had been read by then", which
    /// differs each time the page is opened. So it waits, and the shelf appears under the singles and
    /// does not grow while the reader watches.</para>
    ///
    /// <para><b>Both keys, because a playlist entry is kept by both.</b> A song is filed in a playlist
    /// by its RPM id, with the relative path as the fallback, and either can be the only one that still
    /// finds it (see LibraryDatabase.PlaylistsHolding). The loader is given the songs themselves and
    /// works out both, because the root that a path is relative to belongs to the library and not to
    /// this page.</para>
    ///
    /// <para><b>Whichever collection is open, on its own terms.</b> The songs and the playlists both
    /// come from the one shelf (see MainPage.LoadArtistPlaylistsAsync). On a borrowed shelf the page
    /// therefore says which of that computer's playlists this artist is in, which is one statement
    /// about one collection.</para>
    /// </summary>
    private async Task PlaylistsAsync(CancellationToken ct)
    {
        _playlists.Clear();
        PlaylistsSection.Visibility = Visibility.Collapsed;

        if (PlaylistsLoader is not { } load || _everySong.Count == 0)
            return;

        IReadOnlyList<RPM.Core.Library.PlaylistHolding> held;
        try
        {
            held = await load([.. _everySong.Select(song => song.Track)], ct);
        }
        // FillAsync handles the reader leaving, not this shelf, so the cancellation passes the filter
        // and is not caught and thrown again.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return;   // a library that closes under the page gives a shelf that is not shown, not a fault
        }

        if (ct.IsCancellationRequested)
            return;

        foreach (var playlist in held)
        {
            ct.ThrowIfCancellationRequested();

            _playlists.Add(new ArtistPlaylist(
                playlist.NodeId, playlist.Name,
                Count(playlist.Songs, "Stats_SongsOne", "Stats_Songs"), ShelfMetrics));
        }

        PlaylistsSection.Visibility = _playlists.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        await PlaylistCoversAsync(ct);
    }

    /// <summary>The covers on the playlist shelf, loaded one at a time in the same way as the record
    /// shelves. Each is the drawn cover of the playlist and not a cover taken from one of its records.
    /// It is the same picture that heads its track list. A list whose records have no artwork returns
    /// empty, and the card is told so. Only then does it draw its generated figure.</summary>
    private async Task PlaylistCoversAsync(CancellationToken ct)
    {
        if (PlaylistCoverLoader is not { } cover)
            return;

        foreach (var playlist in _playlists.ToList())
        {
            if (ct.IsCancellationRequested)
                return;

            await Services.Art.CoverFill.DressAsync(
                playlist,
                token => cover(playlist, token),
                async bytes => await RemoteImage.DecodeAsync(bytes, ShelfDecodeEdge()),
                ct);
        }
    }

    /// <summary>
    /// The playlist tree changed while a page is open: read the shelf again.
    ///
    /// <para>The host calls this on the report of the playlist model, which is raised by every add,
    /// remove, rename and reload. This includes the change that the reader just made from the track menu
    /// of this page, which is the case that matters: a shelf that still shows two playlists just after a
    /// third was filled from it cannot be trusted.</para>
    ///
    /// <para>It does nothing unless a page is being looked at. The report fires on every library change
    /// for the whole life of the window, and reading the playlists again for an artist that is not on
    /// screen would be a database read for each import.</para>
    /// </summary>
    public void RefreshPlaylists()
    {
        if (Artist is null || Visibility != Visibility.Visible || _loading is not { } cts)
            return;

        // It runs under the token of the page, so the artist being left takes its refresh with it. It is
        // started and not awaited: the caller is an event handler on the dispatcher and does not depend
        // on what the shelf shows. It has its own catch, because nothing above it is watching.
        _ = Refresh(cts.Token);

        async Task Refresh(CancellationToken ct)
        {
            try
            {
                await PlaylistsAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // The reader went to another artist during the read.
            }
        }
    }
    /// <summary>What a record is called: the last segment of its folder, which is the one name known
    /// without reading a file from the disk.</summary>
    private static string TitleOf(string folder)
    {
        var parts = folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : folder;
    }

    private static string Subtitle(int year, int tracks)
    {
        var parts = new List<string>(2);
        if (year > 0)
            parts.Add(year.ToString());
        if (tracks > 0)
            parts.Add(Count(tracks, "Stats_SongsOne", "Stats_Songs"));
        return string.Join("  •  ", parts);
    }

    /// <summary>What the song is on, and who else is on it. The record comes first, because that is what
    /// the reader is being told they own.</summary>
    private static string SongSubtitle(TrackInfo track, string folder)
    {
        var album = string.IsNullOrWhiteSpace(track.Album) ? TitleOf(folder) : track.Album!;
        return string.IsNullOrWhiteSpace(track.Artist) ? album : $"{album}, {track.Artist}";
    }

    private static string Length(double seconds)
    {
        if (seconds <= 0 || double.IsNaN(seconds))
            return "";
        var t = TimeSpan.FromSeconds(Math.Round(seconds));
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    // ── who they are ──

    /// <summary>The article on the card, kept so the "Read on Wikipedia" link has a page to open.</summary>
    private EncyclopediaArticle? _article;

    /// <summary>
    /// What the catalogues say about the artist: the biography, the outside links and the members.
    ///
    /// <para>The lookup uses the artist name and nothing else. The names on the wall are acts and not
    /// credits (the splitting that a track pane has to guess has already been done in
    /// <see cref="ArtistWall"/>), so there is no album to narrow the lookup with and none is wanted:
    /// this page is about the person and not about one of their records.</para>
    ///
    /// <para>Every exception is caught. This is the one part of the page that goes over the network, and
    /// an artist that nobody has written about, or a lookup made offline, is an ordinary case and not a
    /// fault. The column keeps the photograph and the name, and the shelves beside it do not depend on
    /// this.</para>
    /// </summary>
    private async Task PapersAsync(ArtistWallItem artist, CancellationToken ct)
    {
        if (PapersLoader is not { } load)
            return;

        TrackDossier papers;
        try
        {
            papers = await load(artist.Name, ct);
        }
        catch (OperationCanceledException)
        {
            return;   // the reader went to another artist; the next Load owns the spinner now
        }
        catch
        {
            Waited(ct);
            return;   // offline, or a catalogue is not working
        }

        if (ct.IsCancellationRequested)
            return;

        // The spinner is stopped before the cards are shown, in every case except a cancellation. An
        // artist that the catalogues do not know is an answer, and a spinner that kept turning over an
        // answer already received would wait for nothing.
        Waited(ct);

        Papers(papers, artist.Name);

        // The catalogues have named the artist. Where the wall found nothing under the spelling that
        // this library uses, this id finds them, because the library holds an artist under whichever
        // spelling the tags of the files use. It is used only then: for an artist already found it would
        // be one more call for an answer that nobody would read.
        _wallMbid = papers.Artist?.Mbid;
        if (_wallArtist is null && _wallMbid is { Length: > 0 })
            await WallAsync(artist, _wallMbid, ct);

        // Discogs is a third arrival that depends on the second. It needs the artist number, and the
        // only trustworthy source of that is the link that the catalogues just returned. It is chained
        // and not run beside the papers because it cannot start until they arrive, and it is awaited
        // here so that the one token governs both.
        await PopularAsync(papers, ct);
    }

    /// <summary>
    /// The records of the artist that most collectors keep a copy of.
    ///
    /// <para>Every exception is caught and every step may simply not happen: an artist with no Discogs
    /// link, a user with no Discogs account connected, or a reached rate limit. In all of these cases
    /// the card is not shown. That is the correct result and not an error to report: nothing else on
    /// the page depends on it, and a listener who has never connected Discogs has not asked for it.</para>
    /// </summary>
    private async Task PopularAsync(TrackDossier papers, CancellationToken ct)
    {
        if (PopularLoader is not { } load)
            return;

        // Every Discogs number that the catalogues gave for the artist, not the first one. An artist can
        // hold several (a rename, a second spelling, a side project filed separately, the writing credit
        // kept apart from the records), and MusicBrainz returns its relations in an order that cannot be
        // trusted. Taking the first one can show the card of a name variant with bootleg records while
        // the main page of the artist is second in the same list, and nothing on screen would say which
        // page was asked. The loader ranks across all of them.
        //
        // The numbers are read from the list of the profile and not from the links that the page shows.
        // Those links are one for each site, so "every Discogs number" would always be exactly one,
        // whichever one MusicBrainz put first. See ArtistProfile.DiscogsIds.
        var discogs = papers.Artist?.DiscogsIds ?? [];

        if (discogs.Count == 0)
            return;

        PopularAnswer answer;
        try
        {
            answer = await load(discogs, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            return;   // the loader should return an answer and not throw; if it throws, show nothing
        }

        if (ct.IsCancellationRequested)
            return;

        switch (answer)
        {
            case PopularAnswer.Found found:
                PaintPopular(found.Releases);
                break;

            // The card is shown with one line instead of a list. It is shown and not hidden, because this
            // is an answer that the reader can act on, and a card that is simply absent is a feature that
            // nobody knows exists.
            case PopularAnswer.NotConnected:
                PopularConnect.Visibility = Visibility.Visible;
                PopularBlock.Visibility = Visibility.Visible;
                break;
        }
    }

    /// <summary>
    /// One row per record: its rank, its name, its year and how many people have it. Pressing it opens
    /// the record on Discogs, where the answer came from and where the rest of the information is.
    /// </summary>
    private void PaintPopular(IReadOnlyList<PopularRelease> popular)
    {
        PopularList.Children.Clear();
        PopularConnect.Visibility = Visibility.Collapsed;

        var place = 0;
        foreach (var release in popular)
        {
            place++;

            var face = new Grid { ColumnSpacing = 10 };
            face.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            face.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // The rank, in the faintest ink of the pane: it is the shape of the list and not a fact about
            // the record, and a column of bright numbers on the left would look like track numbers on a
            // track list.
            var rank = new TextBlock
            {
                Text = place.ToString(),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Ink("InkFaint"),
                MinWidth = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(rank, 0);

            var words = new StackPanel { Spacing = 1 };
            words.Children.Add(new TextBlock
            {
                Text = release.Title,
                FontSize = 12,
                Foreground = Ink("InkStrong"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
            });

            // The year and the count on one faint line. The count is what the order is based on, so a
            // ranked list without it would not say what it is ranked by.
            var under = new List<string>(2);
            if (release.Year > 0)
                under.Add(release.Year.ToString());
            if (release.Have > 0)
                under.Add(Loc.Format("Artists_PopularHave", release.Have.ToString("N0")));

            if (under.Count > 0)
                words.Children.Add(new TextBlock
                {
                    Text = string.Join("  \u00b7  ", under),
                    FontSize = 10.5,
                    Foreground = Ink("InkFaint"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxLines = 1,
                });

            Grid.SetColumn(words, 1);
            face.Children.Add(rank);
            face.Children.Add(words);

            var button = new Button
            {
                Content = face,
                Padding = new Thickness(6, 4, 6, 4),
                MinHeight = 0,
                MinWidth = 0,
                CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Foreground = Ink("InkStrong"),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Tag = release.Url,
            };

            button.Resources["ButtonForegroundPointerOver"] = Ink("InkStrong");
            button.Resources["ButtonForegroundPressed"] = Ink("InkStrong");
            button.Resources["ButtonBackgroundPointerOver"] = Ink("InkPlateLit");
            button.Resources["ButtonBackgroundPressed"] = Ink("InkPlate");

            ToolTipService.SetToolTip(button, release.Url);
            button.Click += (sender, _) => Open((sender as FrameworkElement)?.Tag as string);
            PopularList.Children.Add(button);
        }

        PopularBlock.Visibility = Visibility.Visible;
    }

    /// <summary>Puts one dossier on the page: the biography, the links and the members. It is the one
    /// place where they are painted, so the answer that was already in hand and the answer that came
    /// back from the network cannot show two different pages.</summary>
    private void Papers(TrackDossier papers, string credited)
    {
        _article = papers.ArtistArticle;

        PaintAbout(papers.Artist, credited);
        PaintLinks(papers.Artist?.Pages ?? []);
        PaintMembers(papers.Artist);
    }

    /// <summary>The wait is over, however it ended. A cancelled load is left alone, because the
    /// <see cref="Load"/> of the next artist has already decided whether a spinner should turn for that
    /// artist, and stopping it here would stop the one that page just started.</summary>
    private void Waited(CancellationToken ct)
    {
        if (!ct.IsCancellationRequested)
            PapersBusy.Spinning = false;
    }

    /// <summary>The biography card. It is shown only when there is something on it: the summary of the
    /// catalogue, or the first paragraph of the encyclopedia, or both.</summary>
    private void PaintAbout(ArtistProfile? profile, string credited)
    {
        var facts = FactLine(profile);
        var worth = facts.Length > 0 || !string.IsNullOrWhiteSpace(_article?.Extract);

        AboutBlock.Visibility = worth ? Visibility.Visible : Visibility.Collapsed;
        if (!worth)
            return;

        PaintName(profile, credited);

        // The summary line of the catalogue (for example kind, area and years) is useful until the first
        // paragraph of the encyclopedia appears under it. That paragraph already says who the person
        // was, where they were from and when they lived. Two versions of one fact, with the shorter on
        // top, look like the page repeating itself. So the line is shown only where there is no article.
        AboutFacts.Text = facts;
        AboutFacts.Visibility = facts.Length > 0 && _article is null
            ? Visibility.Visible
            : Visibility.Collapsed;

        ApplyArticle();
    }

    /// <summary>
    /// The name that the catalogue uses for the artist, with a dagger after it where the artist has died.
    ///
    /// <para>The dagger is the mark printed beside a name in reference books. It says in one character
    /// what the card otherwise says only in a date range that the reader has to find, and often cannot,
    /// because the fact line is hidden as soon as there is an article to say it better. The mark is only
    /// for a person: MusicBrainz records the end of a group in the same two fields, and a group that
    /// ended broke up.</para>
    /// </summary>
    private void PaintName(ArtistProfile? profile, string credited)
    {
        AboutName.Inlines.Clear();
        AboutName.Inlines.Add(new Run
        {
            Text = profile?.Name ?? _article?.Title ?? credited,
        });

        var person = profile is { Kind: { Length: > 0 } kind }
                     && kind.Equals("Person", StringComparison.OrdinalIgnoreCase);
        var died = person && profile is not null
                   && (profile.HasEnded || profile.Ended is { Length: > 0 });

        if (!died)
        {
            ToolTipService.SetToolTip(AboutName, null);
            return;
        }

        AboutName.Inlines.Add(new Run { Text = "  †", Foreground = Ink("InkQuiet") });

        var years = Lifespan(profile!);
        ToolTipService.SetToolTip(AboutName, years.Length > 0 ? years : null);
    }

    private static string FactLine(ArtistProfile? artist)
    {
        if (artist is null)
            return "";

        var parts = new List<string>(4);
        if (artist.Disambiguation is { Length: > 0 } note) parts.Add(note);
        if (artist.Kind is { Length: > 0 } kind) parts.Add(kind);

        // Where they are from: spelled out where the catalogue gives the area, and as two letters where
        // it gives only the country. Either is better than nothing, and they are separate fields.
        if ((artist.Area ?? artist.Country) is { Length: > 0 } where)
            parts.Add(where);

        if (Lifespan(artist) is { Length: > 0 } years)
            parts.Add(years);

        return string.Join("  ·  ", parts);
    }

    /// <summary>"1967-", or "1967-1995" once it is over. It uses a hyphen, because no user-facing text
    /// in the app carries an en or em dash (see CLAUDE.md).</summary>
    private static string Lifespan(ArtistProfile artist)
    {
        if (artist.Began is not { Length: > 0 } began)
            return artist.Ended is { Length: > 0 } onlyEnd ? "-" + onlyEnd : "";

        return artist.Ended is { Length: > 0 } ended ? $"{began}-{ended}"
             : artist.HasEnded ? began + "-"
             : began;
    }

    /// <summary>The article in full, and the credit that says where it came from. There is no fold: see
    /// the markup for why this page shows the whole of what the track pane shows only the first
    /// paragraph of.</summary>
    private void ApplyArticle()
    {
        if (_article is not { } article)
        {
            AboutProse.Visibility = Visibility.Collapsed;
            AboutSourceButton.Visibility = Visibility.Collapsed;
            return;
        }

        AboutProse.Visibility = Visibility.Visible;
        AboutProse.Text = article.Extract;

        AboutSourceButton.Visibility = Visibility.Visible;
        AboutSourceButton.Content = Loc.Get("TrackInfo_ReadOnWikipedia");
    }

    /// <summary>
    /// The only sites this box offers, in the order they are shown. It is the list of the track pane, for
    /// the same reasons: the front page of the artist and the places where the artist speaks, and not
    /// every streaming service where the listener already has this music.
    ///
    /// <para>Wikipedia is left out on purpose although it is known: the card above this box is the
    /// account from the encyclopedia and has its own link to the same page.</para>
    /// </summary>
    private static readonly ReferenceSite[] ElsewhereSites =
    [
        ReferenceSite.OfficialSite,
        ReferenceSite.Discogs,
        ReferenceSite.Bandcamp,
        ReferenceSite.Facebook,
        ReferenceSite.X,
        ReferenceSite.Instagram,
    ];

    private void PaintLinks(IReadOnlyList<ReferenceLink> links)
    {
        LinksRow.Children.Clear();

        // The links are narrowed and then put in the order of this box and not the order of the
        // catalogue. The order above is the one the list is read in, and keeping the incoming order would
        // change the order of the links depending on which catalogue answered first.
        var shown = new List<ReferenceLink>();
        foreach (var site in ElsewhereSites)
            if (links.FirstOrDefault(l => l.Site == site) is { } found)
                shown.Add(found);

        LinksBlock.Visibility = shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var link in shown)
        {
            // The name, then where it goes. The name alone gives a row of labels that look alike. The
            // address beside it says which Discogs page it is.
            var face = new Grid { ColumnSpacing = 8 };
            face.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            face.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var name = new TextBlock
            {
                Text = SiteName(link),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(name, 0);

            // Faded and not recoloured: at 0.6 opacity it stands apart from the name, and no second
            // colour has to be chosen for it.
            var address = new TextBlock
            {
                Text = link.Url,
                FontSize = 11,
                Opacity = 0.6,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(address, 1);

            face.Children.Add(name);
            face.Children.Add(address);

            var button = new Button
            {
                Content = face,
                // The card padding that every other block in this column has, because this row is the
                // card: the Elsewhere block has no plate of its own, and each link is one.
                Padding = new Thickness(14, 6, 14, 6),
                MinHeight = 0,
                MinWidth = 0,
                CornerRadius = new CornerRadius(6),
                // Stretched, so the links form one column with one left edge and not a row of pills of
                // different widths, and so the address has a width to trim against.
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Foreground = Ink("InkStrong"),
                Background = Ink("InkPlate"),
                BorderThickness = new Thickness(0),
                Tag = link.Url,
            };

            // The pointer states come from the theme, and a visual state overrides anything set above, so
            // pointing at the row would bring back the white of the theme. They are replaced with the
            // page's own, so a row lights up by getting a little more plate and not by changing colour.
            button.Resources["ButtonForegroundPointerOver"] = Ink("InkStrong");
            button.Resources["ButtonForegroundPressed"] = Ink("InkStrong");
            button.Resources["ButtonBackgroundPointerOver"] = Ink("InkPlateLit");
            button.Resources["ButtonBackgroundPressed"] = Ink("InkPlate");

            ToolTipService.SetToolTip(button, link.Url);
            button.Click += (s, _) => Open((s as FrameworkElement)?.Tag as string);
            LinksRow.Children.Add(button);
        }
    }

    /// <summary>What a link is called. The named sites are brands and keep their own spelling in every
    /// language. Only "the artist's own site" is a description, and only it is translated. A link that
    /// is not recognised is labelled with its host, which is the most honest label available.</summary>
    private static string SiteName(ReferenceLink link) => link.Site switch
    {
        ReferenceSite.OfficialSite => Loc.Get("TrackInfo_OfficialSite"),
        ReferenceSite.Other => link.Host,
        ReferenceSite.LastFm => "Last.fm",
        ReferenceSite.AppleMusic => "Apple Music",
        ReferenceSite.AllMusic => "AllMusic",
        ReferenceSite.YouTube => "YouTube",
        ReferenceSite.SoundCloud => "SoundCloud",
        _ => link.Site.ToString(),
    };

    /// <summary>
    /// The members of the band. Only for a group with somebody in it: a person has no members, and a
    /// band whose entry nobody has filled in has none either, so the card would be a heading over an
    /// empty list.
    ///
    /// <para>The name is on the left and the role on the right, faded, because the name is what is read
    /// and the role is only glanced at. A dagger goes beside any member who has died, because nothing
    /// else on the line says it.</para>
    /// </summary>
    private void PaintMembers(ArtistProfile? profile)
    {
        var lineup = profile?.Lineup ?? [];
        var band = profile?.Kind is { Length: > 0 } kind
                   && kind.Equals("Group", StringComparison.OrdinalIgnoreCase);

        MembersList.Children.Clear();
        MembersBlock.Visibility = band && lineup.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (MembersBlock.Visibility != Visibility.Visible)
            return;

        foreach (var member in lineup)
        {
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                FontSize = 12,
                Foreground = Ink("InkBody"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                VerticalAlignment = VerticalAlignment.Center,
            };
            name.Inlines.Add(new Run { Text = member.Name });

            if (member.HasDied || member.Died is { Length: > 0 })
                name.Inlines.Add(new Run { Text = "  †", Foreground = Ink("InkQuiet") });

            Grid.SetColumn(name, 0);
            line.Children.Add(name);

            // What they played, where the catalogue says. It is not the birth date, because a date says
            // nothing about why that name is on the record, and a line with no role is better than a
            // line with the wrong fact. See BandRoles.
            if (member.Role is { Length: > 0 } role)
            {
                var years = new TextBlock
                {
                    Text = role,
                    FontSize = 11,
                    Foreground = Ink("InkBody"),
                    Opacity = 0.7,
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(years, 1);
                line.Children.Add(years);
            }

            MembersList.Children.Add(line);
        }
    }

    private SolidColorBrush Ink(string key) => (SolidColorBrush)Resources[key];

    // ══ the foot of the band, actually taken away ══
    //
    // A fade is not a gradient laid over something. A ramp to a flat colour works only while one flat
    // colour is behind it, and here there is none: under the band are the colour of the artist, a
    // blurred photograph of them and the surface of the panel, in whatever combination that artist
    // produces. A ramp to any one of those is a bar across the bottom of the picture for every artist
    // who is not that colour, which is nearly all of them.
    //
    // So the pixels are removed instead. XAML cannot do this (WinUI has no OpacityMask), but
    // Composition can. A CompositionVisualSurface holds a live copy of the rendering of the band, a
    // CompositionMaskBrush multiplies that copy by a ramp from white to transparent, and a sprite paints
    // the result in the band at the foot. What shows through is whatever is behind, because nothing is
    // painted over it.
    //
    // The live picture has to be cut away in the same band, or it would still be there at full
    // strength. The cut cannot go on the visual that is copied, because the copy is the rendering of
    // that visual, including its clip. It goes on the wrapper around it (HeroCut), which the copy does
    // not know about. This is the PhotoFootFade technique of the track pane, which has a longer
    // explanation.

    /// <summary>How deep the dissolve runs, as a share of the band, with a cap. It is deep enough that
    /// the picture is gone by the time the name is reached and does not end just above it: the name is
    /// meant to sit in the fade, on the page, and not on the last inch of a photograph. It is a share
    /// because the band is a share of the panel: a fixed depth would be half of a short band and a fifth
    /// of a tall one, and only one of those puts the name where it should be.</summary>
    private const double HeroFadeShare = 0.48;

    /// <summary>The deepest the dissolve ever runs, however tall the band grows. Beyond this the
    /// dissolve stops reading as the end of the picture and starts reading as a faint picture.</summary>
    private const double HeroFadeDepthMax = 190;

    private CompositionVisualSurface? _heroFootCopy;
    private SpriteVisual? _heroFootSprite;
    private InsetClip? _heroCut;
    private bool _noHeroFade;

    /// <summary>The copied visual has been arranged again. Its new size is passed straight through and
    /// not read back from the tree later, which is the reason for watching it here.</summary>
    private void HeroLayers_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateHeroFade((float)e.NewSize.Width, (float)e.NewSize.Height);

    /// <summary>Builds the copy, the ramp and the one cut that takes the live picture out of the band.
    /// It runs once, and never again on a computer whose composition stack cannot do it.</summary>
    private void EnsureHeroFade()
    {
        if (_heroFootSprite is not null || _noHeroFade)
            return;

        try
        {
            var compositor = ElementCompositionPreview.GetElementVisual(HeroFootFade).Compositor;

            var copy = compositor.CreateVisualSurface();
            copy.SourceVisual = ElementCompositionPreview.GetElementVisual(HeroLayers);

            // Nothing is left to the alignment of the brush: the offset says which band of the picture is
            // wanted, and an alignment ratio would be a second way to say the same thing that adds to it.
            var pixels = compositor.CreateSurfaceBrush(copy);
            pixels.Stretch = CompositionStretch.None;
            pixels.HorizontalAlignmentRatio = 0;
            pixels.VerticalAlignmentRatio = 0;

            // Only the alpha of the ramp is read: everything at the top of the band, nothing at the foot.
            var ramp = compositor.CreateLinearGradientBrush();
            ramp.MappingMode = CompositionMappingMode.Relative;
            ramp.StartPoint = new Vector2(0, 0);
            ramp.EndPoint = new Vector2(0, 1);
            ramp.ColorStops.Add(compositor.CreateColorGradientStop(0f, Microsoft.UI.Colors.White));
            ramp.ColorStops.Add(compositor.CreateColorGradientStop(1f, Microsoft.UI.Colors.Transparent));

            var mask = compositor.CreateMaskBrush();
            mask.Source = pixels;
            mask.Mask = ramp;

            var sprite = compositor.CreateSpriteVisual();
            sprite.Brush = mask;
            ElementCompositionPreview.SetElementChildVisual(HeroFootFade, sprite);

            var cut = compositor.CreateInsetClip();
            ElementCompositionPreview.GetElementVisual(HeroCut).Clip = cut;

            _heroFootCopy = copy;
            _heroFootSprite = sprite;
            _heroCut = cut;
        }
        catch
        {
            // No composition to do it with. The picture keeps a hard bottom edge. This is a worse band,
            // not a broken page.
            _noHeroFade = true;
        }
    }

    /// <summary>
    /// Points the copy at the last band of the picture and cuts that band out of the live one.
    ///
    /// <para>A surface renders the visual it was given from the origin of that visual, less the
    /// SourceOffset. The last band is therefore requested by offsetting down by everything above it.</para>
    /// </summary>
    private void UpdateHeroFade(float width, float height)
    {
        EnsureHeroFade();
        if (_heroFootCopy is null || _heroFootSprite is null || _heroCut is null)
            return;

        if (width <= 0 || height <= 0)
            return;

        // Whole points. The band and the cut are derived from one integer, so the compositor rounds both
        // to the device grid in the same way. Two edges rounded separately leave a hairline of the bottom
        // edge of the picture under the dissolve.
        //
        // Over a reflected wide photo the dissolve covers the space under the photo, which fades the
        // mirror out; HeroFadeShare of the band would reach up into the photo itself. See
        // RunOff.FadeDepth.
        var wanted = ShowingRunOff is { } aspect
            ? RunOff.FadeDepth(width, height, aspect)
            : Math.Min(Math.Min(height * HeroFadeShare, HeroFadeDepthMax), height);
        var depth = MathF.Round((float)wanted);

        HeroFootFade.Height = depth;
        _heroCut.BottomInset = depth;

        BandSurface.Fit(_heroFootCopy, _heroFootSprite, width, depth, MathF.Round(height) - depth);
    }

    /// <summary>Keeps the feed of the wall square at whatever width the column has. The block reserves
    /// room that is square before anything is drawn in it, and it stays square after. Setting the height
    /// raises this again with the same width, and the half-point check stops the second pass from
    /// causing a third.</summary>
    private void WallFeed_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(WallFeed.Height - e.NewSize.Width) > 0.5)
            WallFeed.Height = e.NewSize.Width;
    }

    /// <summary>
    /// What is on the wall of this artist, newest first. It is rebuilt completely on every read: a wall
    /// is a few dozen short lines, and a rebuilt list has nothing stale left in it.
    /// </summary>
    private readonly ObservableCollection<Models.WallRow> _wall = [];

    /// <summary>
    /// RPM's entry for the artist on screen, or null when RPM has never been asked about them or the
    /// API did not answer.
    ///
    /// <para>Null is the normal state for a quiet artist and not a failure: the wall draws empty and the
    /// box still accepts typing, because the entry is created by the first post. <see cref="WallAsync"/>
    /// and <see cref="WallSend_Click"/> write and read it on the dispatcher, so neither can see it half
    /// written.</para>
    /// </summary>
    private RpmArtist? _wallArtist;

    private static AuthService Auth => App.Services.GetRequiredService<AuthService>();

    private static RpmCommunityClient Community => App.Services.GetRequiredService<RpmCommunityClient>();

    /// <summary>The design harness is up, and the wall comes from <see cref="Gallery.GalleryCommunity"/>
    /// and not from the API. The reviews and comments of the album page come from the same stand-in
    /// under <c>--gallery</c>, so a screenshot of this page shows a wall with lines on it.</summary>
    private static bool Mock => Gallery.GalleryMode.Active;

    private static bool SignedIn => Mock || Auth.IsLoggedIn;

    /// <summary>Who the reader is, for the one row that offers a delete rather than a report.</summary>
    private static string? Me => Mock ? Gallery.GalleryCommunity.MeUserId : Auth.UserId;

    /// <summary>
    /// Reads the wall. It is the fifth part of this page that arrives on its own schedule.
    ///
    /// <para><b>What it can meet.</b> It runs beside the band, the shelves, the songs and the papers,
    /// and touches none of what they touch: the feed list, the empty line and the banner. Nothing else
    /// on the page writes those. Another <see cref="Load"/> can replace it at any time, which is what
    /// the token is for. It can also be running while the reader posts. <see cref="WallSend_Click"/>
    /// reads the wall again and does not append, so the two cannot disagree about what is on the
    /// wall.</para>
    ///
    /// <para><b>It is asked twice for one artist.</b> Once with the name, when the page opens, and
    /// again with the MusicBrainz id if the papers return one (see <see cref="PapersAsync"/>). The
    /// second read finds an artist whose name RPM holds under a spelling that this library does not
    /// use. It is skipped where the first read already found the artist, so the normal artist costs one
    /// call.</para>
    ///
    /// <para>Every exception is caught: a wall that does not load gives a card that says so, and the
    /// page still draws.</para>
    /// </summary>
    private async Task WallAsync(ArtistWallItem artist, string? mbid, CancellationToken ct)
    {
        try
        {
            var entry = Mock
                ? Gallery.GalleryCommunity.Artist(artist)
                : await Community.LookupArtistAsync(artist.Name, mbid, ct);

            if (ct.IsCancellationRequested)
                return;

            _wallArtist = entry;

            var lines = entry is null
                ? []
                : Mock
                    ? Gallery.GalleryCommunity.Wall(entry.ArtistId)
                    : await Community.GetWallAsync(entry.ArtistId, ct: ct);

            if (ct.IsCancellationRequested)
                return;

            ShowWall(lines);
        }
        catch (OperationCanceledException)
        {
            // The reader went to another artist; the Load of the next artist owns the card now.
        }
        catch
        {
            if (ct.IsCancellationRequested)
                return;

            ShowWall([]);
            WallBanner(Loc.Get("Artists_WallOffline"), canSignIn: false);
        }
    }

    /// <summary>Draws what came back, and shows what the box under it can do.</summary>
    private void ShowWall(IReadOnlyList<RpmWallEntry> lines)
    {
        var me = Me;
        var apiBase = Mock ? null : Community.BaseAddress;

        _wall.Clear();
        foreach (var line in lines)
            _wall.Add(new Models.WallRow(line, me, apiBase));

        WallEmpty.Visibility = _wall.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (SignedIn)
            WallBanner(null, canSignIn: false);
        else
            WallBanner(Loc.Get("Artists_WallSignIn"), canSignIn: true);

        ShowSendKey();
    }

    /// <summary>The line under the box: why the key does nothing, and the way out of that where there
    /// is one. Null hides it.</summary>
    private void WallBanner(string? message, bool canSignIn)
    {
        if (message is null)
        {
            WallBannerBlock.Visibility = Visibility.Collapsed;
            return;
        }

        WallBannerText.Text = message;
        WallSignIn.Visibility = canSignIn ? Visibility.Visible : Visibility.Collapsed;
        WallBannerBlock.Visibility = Visibility.Visible;
    }

    private async void WallSignIn_Click(object sender, RoutedEventArgs e) => await RequireSignInAsync();

    private void WallEntry_TextChanged(object sender, TextChangedEventArgs e) => ShowSendKey();

    /// <summary>The key is enabled when there is an account behind it and something to send. It is
    /// checked on every keystroke and again whenever the wall is drawn, because signing in changes the
    /// first condition without touching the box.</summary>
    private void ShowSendKey() => WallSend.IsEnabled = SignedIn && WallEntry.Text.Trim().Length > 0;

    private async void WallSend_Click(object sender, RoutedEventArgs e)
    {
        if (Artist is not { } artist) return;

        var body = WallEntry.Text.Trim();
        if (body.Length == 0) return;
        if (!await RequireSignInAsync()) return;

        try
        {
            if (Mock)
            {
                var mock = Gallery.GalleryCommunity.Artist(artist);
                Gallery.GalleryCommunity.AddWallPost(mock.ArtistId, body);
                _wallArtist = mock;
                WallEntry.Text = "";
                ShowWall(Gallery.GalleryCommunity.Wall(mock.ArtistId));
                return;
            }

            // The entry is created here and nowhere else: reading a wall must not create the entry of an
            // artist whose wall is empty. So the entry for an artist that nobody has written about is
            // the one made by the first person to write about them.
            var entry = _wallArtist ?? await Community.ResolveArtistAsync(artist.Name);
            _wallArtist = entry;

            await Community.PostToWallAsync(entry.ArtistId, body);
            WallEntry.Text = "";

            // Read back and not appended: the wall may have gained lines from other people while this
            // one was typed, and a list that grows only by what this reader added would disagree with
            // the server until the page is opened again.
            ShowWall(await Community.GetWallAsync(entry.ArtistId));
        }
        catch (Exception)
        {
            await Note(Loc.Get("Artists_WallOffline"));
        }
    }

    /// <summary>The overflow menu of a line: delete what is yours, report what is not. A comment on the
    /// album page carries the same two entries, in the same corner.</summary>
    private void WallMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Models.WallRow row } element) return;
        if (_wallArtist is not { } entry) return;

        var flyout = new MenuFlyout();

        if (row.IsMine)
        {
            var delete = new MenuFlyoutItem { Text = Loc.Get("Artists_WallDelete"), FontSize = 12 };
            delete.Click += async (_, _) => await DeleteWallPostAsync(entry, row);
            flyout.Items.Add(delete);
        }
        else
        {
            var report = new MenuFlyoutItem { Text = Loc.Get("Artists_WallReport"), FontSize = 12 };
            report.Click += async (_, _) => await ReportWallPostAsync(entry, row);
            flyout.Items.Add(report);
        }

        flyout.ShowAt(element);
    }

    private async Task DeleteWallPostAsync(RpmArtist entry, Models.WallRow row)
    {
        try
        {
            if (Mock)
            {
                Gallery.GalleryCommunity.DeleteWallPost(entry.ArtistId, row.EntryId);
                ShowWall(Gallery.GalleryCommunity.Wall(entry.ArtistId));
                return;
            }

            await Community.DeleteWallEntryAsync(entry.ArtistId, row.EntryId);
            ShowWall(await Community.GetWallAsync(entry.ArtistId));
        }
        catch (Exception)
        {
            await Note(Loc.Get("Artists_WallOffline"));
        }
    }

    private async Task ReportWallPostAsync(RpmArtist entry, Models.WallRow row)
    {
        if (XamlRoot is null) return;
        if (!await RequireSignInAsync()) return;

        var note = new TextBox
        {
            PlaceholderText = Loc.Get("Album_ReportPlaceholder"),
            // Prose that a moderator reads, so spell checking is on. The implicit style turns it off for
            // the name and path fields that make up the rest of the app.
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
                        Text = Loc.Get("Album_ReportCommentPrompt"),
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 13,
                        Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
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
            await Community.ReportWallPostAsync(
                entry.ArtistId, row.EntryId, reason: "wallpost", note: note.Text.Trim());

            await Note(Loc.Get("Album_ReportSent"));
        }
        catch (Exception)
        {
            await Note(Loc.Get("Artists_WallOffline"));
        }
    }

    /// <summary>
    /// Makes sure there is an account behind the next write, and asks for one if there is not.
    ///
    /// <para>When the user signs in, the wall of the artist that is already open is read again and the
    /// page is not reloaded. What changed is who is asking, and the only thing on this page that
    /// depends on that is which line offers a delete.</para>
    /// </summary>
    private async Task<bool> RequireSignInAsync()
    {
        if (SignedIn) return true;
        if (XamlRoot is null) return false;

        if (!await LoginDialog.ShowAsync(Auth, XamlRoot, Loc.Get("Artists_WallSignIn")))
            return false;

        if (Artist is { } artist && _loading is { } cts)
            await WallAsync(artist, _wallMbid, cts.Token);

        return true;
    }

    private async Task Note(string message)
    {
        if (XamlRoot is null) return;

        await PromptDialog.ShowAsync(XamlRoot, title: null, message, closeText: Loc.Get("Common_OK"));
    }

    // ── searching the listing ──

    /// <summary>What is typed in the field beside All tracks, trimmed. Empty is not a search.</summary>
    private string _trackSearch = "";

    /// <summary>Delays the search, for the same reason as the search of the wall: a keystroke rebuilds
    /// the bound collection, and on Skia a write repaints the window. A fast typist would otherwise
    /// rebuild a listing of several hundred rows six times while typing a three-letter word. The delay
    /// is the same 200 ms.</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _trackSearchDebounce;

    private void TrackSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        TrackSearchClear.Visibility = TrackSearchBox.Text.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (_trackSearchDebounce is null)
        {
            _trackSearchDebounce = DispatcherQueue.CreateTimer();
            _trackSearchDebounce.Interval = TimeSpan.FromMilliseconds(200);
            _trackSearchDebounce.IsRepeating = false;
            _trackSearchDebounce.Tick += (_, _) => ApplyTrackSearch();
        }

        _trackSearchDebounce.Stop();
        _trackSearchDebounce.Start();
    }

    /// <summary>Escape leaves a search, which is the key that everybody tries. Enter does nothing on
    /// purpose: the listing narrows as it is typed, so there is nothing for it to submit.</summary>
    private void TrackSearch_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape)
            return;

        ClearTrackSearch();
        e.Handled = true;
    }

    private void TrackSearchClear_Click(object sender, RoutedEventArgs e) => ClearTrackSearch();

    private void ClearTrackSearch()
    {
        if (TrackSearchBox.Text.Length == 0)
            return;

        TrackSearchBox.Text = "";
        // Applied directly and not through the debounce: clearing is one action and not a word being
        // typed, and a fifth of a second of the old listing after it looks like a control that stuck.
        _trackSearchDebounce?.Stop();
        ApplyTrackSearch();
    }

    /// <summary>Rebuilds the shown listing from every song of the artist. It is cleared and filled again
    /// and not patched: the order is that of the shelves (or by title, once grouped), and a narrowing
    /// that changed the collection in place would have to work out where each remaining song
    /// belongs.</summary>
    private void ApplyTrackSearch()
    {
        _trackSearch = TrackSearchBox.Text.Trim();

        _tracks.Show([.. _everySong.Where(song => Matches(song, _trackSearch))]);

        ShowNoTrackMatch();
    }

    /// <summary>Matched on the title only, which is the one thing the row shows. The record that a song
    /// is on is in its tooltip, and a hit whose visible words do not contain what was typed looks like a
    /// search that went wrong.</summary>
    private static bool Matches(ArtistSong song, string search)
        => search.Length == 0
           || song.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase);

    private void TracksByLetter_Click(object sender, RoutedEventArgs e) => GroupTracks(ArtistTrackGrouping.Letter);

    private void TracksByYear_Click(object sender, RoutedEventArgs e) => GroupTracks(ArtistTrackGrouping.Year);

    /// <summary>Regroups All tracks and remembers the choice for every artist's page.</summary>
    private void GroupTracks(ArtistTrackGrouping grouping)
    {
        _tracks.GroupBy(grouping);
        App.Services.GetRequiredService<SettingsService>().ArtistTrackGrouping = grouping;
        PaintTrackGrouping();
    }

    /// <summary>Marks the key of the current grouping, with the same wash as the view switch of the
    /// library.</summary>
    private void PaintTrackGrouping()
    {
        Common.SelectedSegment.Paint(TracksByLetter, _tracks.Grouping == ArtistTrackGrouping.Letter);
        Common.SelectedSegment.Paint(TracksByYear, _tracks.Grouping == ArtistTrackGrouping.Year);
    }

    /// <summary>The other empty listing: there are songs, but none matches what has been typed. It is
    /// kept apart from having no songs at all, because the way out of each is different: one needs the
    /// field cleared, the other needs music imported.</summary>
    private void ShowNoTrackMatch()
        => NoTrackMatch.Visibility = _trackSearch.Length > 0 && _tracks.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    // ── the two columns, and the one ──

    /// <summary>Whether the artist column has been folded into the shelves. It is stored and not worked
    /// out each time, so the panel is taken apart only at the crossing: a resize raises this handler on
    /// every pixel of the drag.</summary>
    private bool _folded;


    /// <summary>
    /// Folds the artist column into the head of the shelves when the panel is too narrow to hold both.
    ///
    /// <para>The column is moved and not hidden. The photograph, the biography and the links are half of
    /// what the page is, and a narrow window cannot do without them. On a narrow panel they become the
    /// top of the one remaining column, so the same page is read in one pass instead of two.</para>
    ///
    /// <para>It is moved and not duplicated, too: the panel is filled by name from
    /// <see cref="PapersAsync"/> and <see cref="BandAsync"/>, and a second copy would need a second set
    /// of those names, which the compiler would refuse, and a load would fill only one of them.</para>
    /// </summary>
    private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // A panel that is collapsed, or laid out for the first time, reports no width. Folding on that
        // would take the column apart every time the view is hidden.
        if (e.NewSize.Width <= 0)
            return;

        var (fold, width) = ArtistPageLayout.Columns(e.NewSize.Width);

        // Only the crossing takes the page apart; the width is written on every pixel of the drag. They
        // are separate steps because one early return for both would leave the column at a fixed width:
        // everything after the fold check would run once and never again.
        if (fold != _folded)
        {
            _folded = fold;
            Refold(fold);
        }

        AboutColumn.Width = new GridLength(width);
    }

    /// <summary>Moves the artist column into the shelves, or back out of them. The width it comes back
    /// at is decided by the caller, so there is one place that answers that question.</summary>
    private void Refold(bool fold)
    {
        if (fold)
        {
            AboutScroll.Content = null;
            AboutScroll.Visibility = Visibility.Collapsed;
            AboutColumn.Width = new GridLength(0);
            // Above the shelves, and the margin goes with it: the top gap of the body is for a heading,
            // and the photograph is meant to sit flush with the top of the panel.
            Body.Margin = new Thickness(0);
            Body.Children.Insert(0, AboutPanel);
            return;
        }

        Body.Children.Remove(AboutPanel);
        Body.Margin = new Thickness(0, 18, 0, 0);
        AboutScroll.Visibility = Visibility.Visible;
        AboutScroll.Content = AboutPanel;
    }

    // ── the band, and the sleeves ──

    /// <summary>The height of the band, from the width of the band itself and not from the column.
    /// When folded, the band has the width of the panel and not of the column, and asking the band
    /// itself gives the right answer in both states.</summary>
    private void HeroBand_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0)
            return;

        var height = ArtistPageLayout.BandHeight(e.NewSize.Width);
        if (Math.Abs(HeroBand.Height - height) > 0.5)
            HeroBand.Height = height;

        // The maximum width of the logo follows the band. LogoAsync cuts the picture to this same
        // number, so on a band that has not moved, this is the width the mark already has and nothing
        // happens. It matters when the band changes under a mark that is already shown (the about
        // column folds, or the window is made narrower). Otherwise the logo would keep the width it was
        // cut at and extend past the side of the photograph.
        ArtistLogo.MaxWidth = ArtistPageLayout.LogoMaxWidth(e.NewSize.Width);
    }

    /// <summary>
    /// Sizes every cover on both shelves, from the scroller of the shelves.
    ///
    /// <para>It uses the scroller and not the panel, for the same reason as the height of the band: this
    /// column is the whole panel when the page is folded and part of it when it is not, and its own
    /// width is the value that is correct in both cases. There is one shared measurement (see
    /// <see cref="ShelfMetrics"/>). It is written once here and read by however many covers exist, in
    /// either order: a resize before the shelves are filled leaves the right number waiting for them,
    /// and a resize after it raises a change that every bound cover follows.</para>
    /// </summary>
    private void BodyScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0)
            return;

        var (stride, art) = ArtistPageLayout.Sleeve(e.NewSize.Width);
        ShelfMetrics.Set(stride, art);

        // The covers were measured again, so the stride that places the triangle of the open drawer has
        // changed under it. A narrower panel also shows fewer covers, so the open record can leave the
        // screen without its shelf having been scrolled.
        PointAtOpenSleeve();
    }

    // ── what a press does ──

    /// <summary>Out to the wall of artists. The search under All tracks is cleared with the page:
    /// coming back to an artist and finding the listing still narrowed by a word typed minutes ago looks
    /// like a page that is missing most of their music.</summary>
    private void Back_Click(object sender, RoutedEventArgs e)
    {
        ClearTrackSearch();
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Pressing a cover opens the record where it stands, in the drawer of the album wall. The page does
    /// not go anywhere.
    ///
    /// <para>Exactly one drawer is open at a time across every shelf. The other drawers are closed here
    /// and not by each shelf for itself: a record they made, one made with another artist, a live
    /// record, a soundtrack, a compilation, a single and a record they appear on as a guest are seven
    /// shelves of one collection, and two open drawers under one artist would look like a page that has
    /// lost track of what is being read. The other drawers fold on their own schedule and are not waited
    /// for: the record being opened is the one the reader is looking at, and holding it back for a fifth
    /// of a second while something else closes is a press that did not answer.</para>
    ///
    /// <para>Pressing the record that is already open closes it. This is the way back out: the same
    /// press on the same cover.</para>
    ///
    /// <para>Every press takes a number (<see cref="_bandGesture"/>), and every await below checks that
    /// it is still the newest. A press has two waits (the folder is read, then the drawer unfolds), and a
    /// reader who presses three covers in that time would otherwise have three of these finishing on top
    /// of one another.</para>
    /// </summary>
    private async void Record_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ArtistRecord record })
            return;

        var mine = DrawerOf(record);
        var gesture = ++_bandGesture;

        foreach (var other in Shelves)
            if (!ReferenceEquals(other, mine))
                _ = FoldAsync(other, gesture);

        if (mine.Root.Visibility == Visibility.Visible
            && mine.View.Band is { } open
            && string.Equals(open.Card.Folder, record.Folder, StringComparison.OrdinalIgnoreCase))
        {
            // Cancel the read: a record that is being closed may still be being fetched.
            _bandLoading?.Cancel();
            await FoldAsync(mine, gesture);
            return;
        }

        // A record of their own is used whole. A record of another artist is on this page for their songs
        // on it, and those are what the drawer is about. A drawer that lists all hundred tracks of a
        // compilation opened under a guest would be about a record that they are barely on.
        await OpenAsync(mine, record, gesture,
             ReferenceEquals(mine.View, GuestBand) && Artist is { } who
                 ? BandTracks.CreditedTo(who.Name)
                 : BandTracks.Whole);
    }

    /// <summary>Which press this is. Every open, every fold and every page change increases it, and the
    /// code reads it after every await: an answer that comes back with an older number belongs to a
    /// record that the reader has already moved past.</summary>
    private int _bandGesture;

    /// <summary>
    /// One shelf's drawer: the element that a walk owns, the two things drawn inside it, the shelf it
    /// hangs under, and the walk itself.
    /// </summary>
    /// <remarks>
    /// <para>The root is what opens, not the control inside it. The triangle and the track list arrive
    /// as one thing: a mark that stands on the shelf ahead of the drawer it points at looks like the
    /// drawer opening twice. So the height, the clip and the opacity are written on the root, and the
    /// control sits inside it.</para>
    ///
    /// <para>The walk belongs to the shelf and not to the page. Two drawers can move at once (the
    /// singles shelf folds while the albums shelf opens), and a single walk would let one cancel the
    /// other where it stands.</para>
    ///
    /// <para><see cref="Rail"/> is the scroller of the shelf. It is found through the tree because the
    /// seven shelves are the same markup seven times, and naming them would add seven names for one
    /// fact. The triangle is placed against it. It is read when the mark is placed and not remembered
    /// from the press, because the shelf moves under the drawer afterwards.</para>
    /// </remarks>
    private sealed class ShelfDrawer(
        FrameworkElement root, AlbumBandView view, Polygon point, IReadOnlyList<ArtistRecord> records)
    {
        public FrameworkElement Root { get; } = root;

        public AlbumBandView View { get; } = view;

        public Polygon Point { get; } = point;

        /// <summary>The records on this shelf, for the place of the open one in it.</summary>
        public IReadOnlyList<ArtistRecord> Records { get; } = records;

        public BandUnfold Unfold { get; } = new();

        /// <summary>The record this drawer was opened for, or null while it is shut.</summary>
        public ArtistRecord? Opened { get; set; }

        /// <summary>The scroller of the shelf: the one in the panel where this drawer hangs. It is not
        /// cached when it is null, so a question asked while the page is detached does not become the
        /// answer.</summary>
        public ScrollViewer? Rail =>
            _rail ??= (Root.Parent as Panel)?.Children.OfType<RPM.Desktop.Presentation.Controls.KeyedRow>().FirstOrDefault()?.Rail;

        private ScrollViewer? _rail;

        /// <summary>This shelf has been asked to report when it moves. It subscribes on the first record
        /// opened on it and not when the table is built: the table is built by whoever asks first, and
        /// that can be the way out of the page (which stops every walk through it), where the scroller
        /// may not be reachable. A shelf subscribed then would never report a move again.</summary>
        public bool Watching { get; set; }
    }

    /// <summary>Every shelf's drawer, made once. A shelf added to the page without an entry here has a
    /// drawer that nothing folds, nothing points at and nothing stops.</summary>
    private IReadOnlyList<ShelfDrawer> Shelves => _shelvesMade ??= Build();

    private IReadOnlyList<ShelfDrawer>? _shelvesMade;

    private IReadOnlyList<ShelfDrawer> Build()
    {
        // In the order a record is asked for, so that a record can be on only one of them. Their own
        // records are last because that shelf is the answer for everything the others did not claim.
        ShelfDrawer[] shelves =
        [
            new(GuestDrawer, GuestBand, GuestPoint, _guest),
            new(TogetherDrawer, TogetherBand, TogetherPoint, _together),
            new(EpsDrawer, EpsBand, EpsPoint, _eps),
            new(LiveDrawer, LiveBand, LivePoint, _live),
            new(MixtapesDrawer, MixtapesBand, MixtapesPoint, _mixtapes),
            new(SoundtracksDrawer, SoundtracksBand, SoundtracksPoint, _soundtracks),
            new(CompilationsDrawer, CompilationsBand, CompilationsPoint, _compilations),
            new(SinglesDrawer, SinglesBand, SinglesPoint, _singles),
            new(RecordsDrawer, RecordsBand, RecordsPoint, _records),
        ];

        return shelves;
    }

    /// <summary>Which drawer a record belongs in. The shelves are asked in an order such that a record
    /// is on exactly one of them. The last is the answer for everything the others did not claim.</summary>
    private ShelfDrawer DrawerOf(ArtistRecord record)
        => Shelves.FirstOrDefault(shelf => shelf.Records.Contains(record)) ?? Shelves[^1];

    /// <summary>How long the drawer takes to open, to fold shut, and to grow or shrink between two
    /// records on the same shelf. They are the figures of the album wall, because it is the same drawer:
    /// a record opened here and the same record opened there must not move at two speeds.</summary>
    private const double UnfoldMs = 200;
    private const double FoldMs = 160;
    private const double MorphMs = 200;

    /// <summary>How long the background of the cover takes to cross over what the drawer was standing
    /// on. It is the figure of the album page and the wall: the surfaces dress themselves in the same
    /// cover at the same speed.</summary>
    private const double GroundCrossMs = 260;

    /// <summary>Folds a drawer shut and then releases the record it held. A band left on a hidden
    /// drawer keeps a track list, a decoded cover and a colour alive for a record that nobody looks
    /// at.</summary>
    private async Task FoldAsync(ShelfDrawer drawer, int gesture)
    {
        if (drawer.Root.Visibility != Visibility.Visible)
            return;

        // The ring goes with the press and not with the fold: the record stops being the open one when
        // another is pressed, and a ring that waited for the walk would be two rings for a sixth of a
        // second. The triangle folds with the drawer, because it is part of it.
        Unring(drawer);

        await drawer.Unfold.To(drawer.Root, toHeight: 0, toOpacity: 0, FoldMs, easeOut: false, releaseToAuto: false);

        // A newer press may have taken this drawer over while it folded. It owns the element now, and
        // removing the band from under it would make a drawer that opens onto nothing.
        if (gesture != _bandGesture || drawer.Unfold.Owns(drawer.Root))
            return;

        Shut(drawer);
    }

    /// <summary>Takes a drawer down in one frame, with no fold. The page is being left, or the artist
    /// is being changed, and nobody is watching this drawer.</summary>
    private void Shut(ShelfDrawer drawer)
    {
        drawer.Unfold.Stop();
        Unring(drawer);
        drawer.Root.Visibility = Visibility.Collapsed;
        drawer.Point.Visibility = Visibility.Collapsed;
        drawer.View.Band = null;

        // Never left folded: the element belongs to the shelf and is used again by the next record opened
        // on it, and a height of zero left behind would make a drawer that opens onto nothing.
        drawer.Root.Height = double.NaN;
        drawer.Root.Clip = null;
        drawer.Root.Opacity = 1;

        // The lines are going away, so the pulse must not outlive them. It is synced and not stopped,
        // because the drawer of another shelf may still be open with a record that is being uploaded.
        _lineBreath.Sync();
    }

    /// <summary>Closes every drawer and cancels the presses in flight.</summary>
    private void ShutEvery()
    {
        _bandGesture++;
        _bandLoading?.Cancel();
        _groundFade?.Stop();
        foreach (var drawer in Shelves)
            Shut(drawer);
    }

    /// <summary>Removes the ring from the cover that this drawer was opened for and forgets it. Calling
    /// it twice has the same result as calling it once: a fold and the shut that follows it both call
    /// it.</summary>
    private void Unring(ShelfDrawer drawer)
    {
        if (drawer.Opened is { } record)
            record.IsExpanded = false;

        drawer.Opened = null;
        if (ReferenceEquals(_open, drawer))
            _open = null;
    }

    /// <summary>The one drawer that is open, or null. It is stored and not searched for, because the
    /// triangle is placed from the record, shelf and offset of this drawer.</summary>
    private ShelfDrawer? _open;

    /// <summary>
    /// Puts the triangle of the open drawer over the cover it belongs to, or hides it where that cover
    /// is not on screen.
    /// </summary>
    /// <remarks>
    /// <para>It is called on open, on every move of any shelf, and whenever the covers are measured
    /// again. Each time it reads the current offset of the shelf, and it is not moved by whoever moved
    /// the shelf. A press on a scrollbar, a trackpad swipe and a resize of the panel all end in the same
    /// question, and a path that did not ask would leave the mark pointing at a record that has
    /// gone.</para>
    ///
    /// <para>It reads the record and shelf of the open drawer, so a call from the scroller of another
    /// shelf is harmless: there is one open drawer, and this places that one.</para>
    ///
    /// <para>There is no conversion between the two spaces: the scroller of the shelf and the root of
    /// the drawer both start at the left edge of the panel, so a middle measured across the shelf is
    /// already a margin here. <see cref="ArtistPageLayout.SleeveLeft"/>, passed to the calculation, puts
    /// the mark under the cover and not under the card around it.</para>
    /// </remarks>
    private void PointAtOpenSleeve()
    {
        if (_open is not { Opened: { } record, Rail: { } rail } drawer)
            return;

        var middle = AlbumGridLayout.CardMiddleOnScreen(
            drawer.Records.IndexOf(record),
            record.Metrics.Stride + ArtistPageLayout.ShelfSpacing,
            record.Metrics.Art,
            ArtistPageLayout.SleeveLeft,
            rail.HorizontalOffset, rail.ViewportWidth, BandPointer.TriangleHalf);

        if (middle is not { } x)
        {
            drawer.Point.Visibility = Visibility.Collapsed;
            return;
        }

        // Never left of the edge of the drawer, as the margin on the wall. This costs the last few points
        // of aim on the one cover that can be that far left, and nothing anywhere else.
        drawer.Point.Margin = new Thickness(Math.Max(0, x - BandPointer.TriangleHalf), 0, 0, 0);
        drawer.Point.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Opens one record in one drawer.
    ///
    /// <para><b>The record is read into the drawer before the drawer is shown.</b> It is put in the tree
    /// at its own width, folded to nothing and unlit, and the folder is read into it there. What unfolds
    /// a moment later is the finished drawer: the track list, the cover at the size it is drawn, and the
    /// background of the record, all arriving together. If the drawer were attached first and filled
    /// after, the reader would watch it dress itself: a plate that goes to a colour and a name that goes
    /// to a track list.</para>
    ///
    /// <para><b>A second record on the same shelf is a morph, not a new open.</b> The element and the
    /// room it has taken are kept, and what is inside is swapped. Then the height is walked from the
    /// length of the old record to the length of the new one. Folding one record away and unfolding the
    /// next would be two animations for one action of the reader.</para>
    /// </summary>
    private async Task OpenAsync(ShelfDrawer drawer, ArtistRecord record, int gesture, BandTracks which)
    {
        if (BandSources is not { } sources)
            return;

        _bandLoading?.Cancel();
        var cts = _bandLoading = new CancellationTokenSource();

        Wire(drawer.View);

        // Always column 0: the triangle is placed by the arithmetic of this page and not by the column
        // index of the band, which is a stride from a wall that these shelves are not (see
        // PointAtOpenSleeve).
        var card = new AlbumCard(record.Folder, LibraryName?.Invoke() ?? "", ShelfMetrics);
        var unfold = drawer.Unfold;
        var morph = drawer.Root.Visibility == Visibility.Visible && drawer.View.Band is not null;

        // Where the drawer stands now: the pinned height while a walk owns it, and the laid-out height
        // otherwise. A walk that is still unfolding the last record has written a height that
        // ActualHeight has not caught up with. It is read before the walk is stopped, because stopping
        // it leaves the element exactly where it was.
        var from = morph ? (double.IsNaN(drawer.Root.Height) ? drawer.Root.ActualHeight : drawer.Root.Height) : 0;
        unfold.Stop();

        AlbumBand band;
        if (morph && drawer.View.Band is { } showing)
        {
            // The same band, pointed at the new card. A new band would be a new DataContext, and the
            // drawer would be rebuilt under the reader, which is the collapse and expand that this
            // branch avoids.
            band = showing;
            band.Retarget(card, 0);

            // The background of the cover belongs to the cover being read. If it stayed, the colour of
            // the last record would be under the tracks of this one until the read arrives. When it is
            // taken away, the drawer stands on the plate of the app meanwhile, and the background of the
            // new record crosses in over it.
            band.GroundOn = 0;
            band.Ground = null;
            band.Wash = null;
            band.Veil = 0;
            band.Loading = true;

            // Pinned across the read, so that the length of the old record cannot be laid out again
            // underneath.
            drawer.Root.Height = from;
        }
        else
        {
            band = new AlbumBand(card, 0) { Title = record.Title };
            drawer.Root.Height = 0;
            drawer.Root.Clip = null;
            drawer.Root.Opacity = 0;
            drawer.Root.Visibility = Visibility.Visible;
            drawer.View.Band = band;
        }

        // The drawer opens on the plate of the app, and so do both marks that it makes outside itself.
        // All three agree on the frame it opens as much as on every frame after it, and the background
        // of the cover crosses all three in together once it has been read (see CrossGround).
        drawer.Point.Fill = BandPointer.Ramp(BandPointer.Plate);
        band.PointerBrush = drawer.Point.Fill;

        // The ring moves to this record, off whichever record of the shelf had it. Its colour belongs to
        // the record and is left alone: each record keeps the background that its cover turned out to be,
        // so a record opened a second time has a ring of the colour it had the first time.
        if (drawer.Opened is { } was && !ReferenceEquals(was, record))
            was.IsExpanded = false;
        drawer.Opened = record;
        record.IsExpanded = true;
        _open = drawer;

        // A shelf that moves under an open drawer must say so. It subscribes on the first record opened
        // on it, when the shelf is on screen and the scroller can be reached. It is done in code and not
        // in the markup, where this shelf is written seven times and one of the eight scrollers on the
        // page has no drawer under it.
        if (!drawer.Watching && drawer.Rail is { } rail)
        {
            rail.ViewChanged += (_, _) => PointAtOpenSleeve();
            drawer.Watching = true;
        }

        PointAtOpenSleeve();

        // What the shelf has already read, so the cover is not blank while the folder is queried. It is
        // cast and not converted: the shelf holds an ImageSource because a cover on it needs no more,
        // and the drawer wants the bitmap. A seed that is not a bitmap is not used, and the read at the
        // size of the drawer arrives a moment later either way.
        band.Cover = record.Cover as Microsoft.UI.Xaml.Media.Imaging.BitmapImage;

        var items = await AlbumBandLoader.FillAsync(band, sources, loadCover: true, which, cts.Token);

        if (gesture != _bandGesture || cts.IsCancellationRequested)
            return;

        // Which line is playing is asked for here and not pushed in when the deck changed: the read
        // above takes seconds on a cold folder, and the song can change during it.
        MarkPlaying(items);

        // Then what the cloud holds of the record as a whole, which is the mark under its cover. It is
        // asked here for the same reason and read in the same way as for the lines: a record opened while
        // it is being uploaded shows that on the frame it opens.
        if (CloudBadgeLoader is { } badge)
            band.Card.CloudBadge = badge(band.Card.Folder);

        // A record opened while some of it is being uploaded starts pulsing at once: the marks were
        // given to the lines as they were built.
        _lineBreath.Sync();

        drawer.Root.Height = double.NaN;
        drawer.Root.Clip = null;

        // The width of the panel stands in for the width of the drawer, and this fallback is the whole
        // unfold on a fresh open. The element is collapsed until the press, a collapsed element is never
        // measured, so it has no width of its own until a layout pass has run with it visible, and the
        // read above can finish before one has. Measured against zero, the drawer wanted nothing, and
        // "nothing to unfold" is read as "show it where it is": the drawer just appeared. The parent is
        // laid out whatever the drawer has been through, which is why the parent is asked and not the
        // drawer.
        var full = BandUnfold.Wanted(
            drawer.Root, fallbackWidth: (drawer.Root.Parent as FrameworkElement)?.ActualWidth ?? 0);
        if (full <= 1)
        {
            // Nothing to measure: let it lay out and be seen, and do not walk it to a length that nobody
            // could work out.
            drawer.Root.Opacity = 1;
            return;
        }

        drawer.Root.Height = morph ? from : 0;
        await unfold.To(drawer.Root, full, toOpacity: 1, morph ? MorphMs : UnfoldMs,
                        easeOut: true, releaseToAuto: true);
    }

    /// <summary>What a press inside the drawer does. It is written on every open and not once: the hooks
    /// are given to this page after it is built, and a drawer wired at construction would have a heart
    /// and menus that do nothing.</summary>
    private void Wire(AlbumBandView drawer)
    {
        drawer.PlayTrack = PlayTrack;
        drawer.PlayTracks = PlayTracks;
        drawer.StartDj = StartDj;
        drawer.MenuInvoked = MenuHooks?.Invoked;
        drawer.SetTrackFavorite = SetTrackFavorite;
        drawer.TrackMenuHooks = TrackMenuHooks;
    }

    /// <summary>
    /// The background of the record, put on the drawer once its cover has been read: the mean colour
    /// under everything, the cover blurred over it, and the veil inside the blur.
    ///
    /// <para>The background crosses in only where the drawer is on screen to be watched. The drawer is
    /// asked and the press is not remembered: a record opened fresh is read while its drawer is still
    /// folded to nothing, and a cross of a quarter of a second spent behind a folded drawer is a quarter
    /// of a second in which the record is undressed once the drawer is shown. A drawer that is pointed
    /// at a second record is being watched, and there the cross is the whole point.</para>
    ///
    /// <para>It is passed to the loader as the <see cref="AlbumBandSources.Sleeve"/> of this page, which
    /// is where a surface says what it does with the bytes of a cover beyond drawing them.</para>
    /// </summary>
    public async Task CrossGround(AlbumBand band, byte[] sleeve, CancellationToken ct)
    {
        if (await AlbumBandLoader.ReadGroundAsync(sleeve, ct) is not { } read
            || ct.IsCancellationRequested)
            return;

        // The drawer that shows this band, or none: a record read after the reader moved past it writes
        // its colours onto its own band and nowhere else.
        var drawer = Shelves.FirstOrDefault(d => ReferenceEquals(d.View.Band, band));
        var watched = drawer?.Root is { Opacity: > 0, ActualHeight: > 1 };

        band.Ground = new SolidColorBrush(read.Ground.Ground);
        band.Veil = read.Ground.Veil;
        band.Wash = read.Wash;

        // The triangle and the ring cannot carry the three layers of the drawer, so they take the single
        // colour that the three flatten to. Neither crosses by a second copy fading over the first: two
        // stacked ramps are one alpha over another and not the ramp itself, and the mark comes out
        // darker and with a different hue from the drawer it points at. Both are repainted instead, with
        // their colour walked from what the drawer opened with to what its cover turned out to be, on the
        // same number that the layers use.
        var wasPoint = BandPointer.ColourOf(band.PointerBrush) ?? read.Ground.Flat;
        var wasRing = read.Ground.Flat;
        SolidColorBrush? ring = null;
        if (drawer?.Opened is { } record)
        {
            // The brush of the ring is made once and its colour is written a frame at a time. The brush
            // of the triangle is rebuilt, because a gradient is its stops and rewriting them in place is
            // a repaint that the element does not hear about.
            wasRing = (record.Ring as SolidColorBrush)?.Color ?? read.Ground.Flat;
            record.Ring = ring = new SolidColorBrush(wasRing);
        }

        _groundFade?.Stop();
        _groundFade = new ChromeFade(
            v =>
            {
                band.GroundOn = v;
                band.PointerBrush = BandPointer.Ramp(BandPointer.Blend(wasPoint, read.Ground.Flat, v));
                if (drawer is not null)
                    drawer.Point.Fill = band.PointerBrush;
                if (ring is not null)
                    ring.Color = BandPointer.Blend(wasRing, read.Ground.Flat, v);
            },
            band.GroundOn);
        _groundFade.To(1, watched ? GroundCrossMs : 0);
    }

    /// <summary>The pulse of the cloud marks of an open record: a mark that is working pulses, and
    /// settles when it stops. <see cref="Shut"/> stops it through a plain <c>Sync</c>: with no drawer
    /// holding a band there is nothing busy to find, and the pulse stops its own timer.</summary>
    private readonly Common.CloudLineBreath _lineBreath;

    /// <summary>The one live background cross. At most one drawer is watched, so there is at most one
    /// of these. A record opened while one is running drops it where it stands.</summary>
    private ChromeFade? _groundFade;

    /// <summary>Cancels the read of a record that the reader has already moved past. It is separate from
    /// the token of the page: closing an artist must stop a folder query, but opening a second record
    /// must not stop the shelves that the page is still laying out around it.</summary>
    private CancellationTokenSource? _bandLoading;

    /// <summary>What the collection is called, for the one record that is the loose tracks at the root
    /// of a library. That card is the only one whose name comes from somewhere other than its own
    /// folder. It is read through a function and not stored: the library on screen can be replaced by
    /// another computer's library long after this page was built, and a stored name would caption that
    /// record with a collection it is not in.</summary>
    public Func<string>? LibraryName { get; set; }

    /// <summary>How wide a cover on this page is drawn, live. <see cref="BodyScroll_SizeChanged"/>
    /// writes it from the width of the shelves, and every cover on both shelves reads it through its
    /// binding, as does the card that an opened record is built from.
    ///
    /// <para>There is one for each page and not one for each process: it is a measurement of the shelves
    /// of this page, and a static one would let a second page resize the first without any
    /// sign.</para></summary>
    private readonly AlbumTileMetrics ShelfMetrics = new();

    /// <summary>The pixel edge at which the cover of a shelf is decoded: what the card actually draws,
    /// on this display. The bytes come from the cache at <see cref="Services.Art.ArtScaler.TileEdge"/>,
    /// which is sized for the widest card of any wall. A picture given to a square half that size would
    /// be reduced by the renderer and not by a decoder, with four samples for each pixel and no
    /// averaging. It is the same ladder that the wall of covers decodes by, so a cover has the same
    /// sharpness on both.</summary>
    private int ShelfDecodeEdge()
        => Services.Art.ArtScaler.DecodeEdgeFor(ShelfMetrics.Art, XamlRoot?.RasterizationScale ?? 1);

    /// <summary>A right-click on a cover, on either shelf. The shared factory builds the menu, new on
    /// every open, so the cloud, favourite and Discogs entries describe the record as it is at that
    /// moment and not as it was when the page was drawn.</summary>
    private void Record_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is not ArtistRecord record)
            return;

        // A record on the shelf of an artist is a record among others, so it gets the whole menu,
        // including the link to its own page, because from here that link goes somewhere.
        var menu = AlbumMenuFactory.Build(
            record.Folder,
            MenuHooks ?? new AlbumMenuHooks(null, null, null, null, null),
            (FontFamily)Resources["Fa"], (FontFamily)Resources["FaRegular"],
            AlbumMenuPlace.Collection);

        if (args.TryGetPosition(sender, out var point))
            menu.ShowAt(sender, new FlyoutShowOptions { Position = point });
        else if (sender is FrameworkElement fe)
            menu.ShowAt(fe);

        args.Handled = true;
    }

    private void Song_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ArtistSong song })
            PlayTrack?.Invoke(song.Track);
    }

    /// <summary>A press on one of the listener's playlists: open it where playlists are. The page stays
    /// behind the Playlists view, so the Artists button on the rail comes back to this artist and not to
    /// the wall.</summary>
    private void Playlist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ArtistPlaylist playlist })
            OpenPlaylist?.Invoke(playlist.NodeId);
    }
    /// <summary>A right-click on one of their songs. The menu is the menu of the song and not of the
    /// record: the shelves above already carry the menu of the record, and "Edit album" on a line here
    /// would let a listener change twelve files while looking at one. It uses the same factory as the
    /// album views, so a song offers the same entries on an artist page as on the wall.</summary>
    private void Song_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is not ArtistSong song)
            return;

        var menu = TrackMenuFactory.Build(
            song.Track, TrackMenuHooks ?? new TrackMenuHooks(null, null, null, null),
            (FontFamily)Resources["Fa"], (FontFamily)Resources["FaRegular"]);

        if (args.TryGetPosition(sender, out var point))
            menu.ShowAt(sender, new FlyoutShowOptions { Position = point });
        else if (sender is FrameworkElement fe)
            menu.ShowAt(fe);

        args.Handled = true;
    }

    /// <summary>The pointer is on a line: its empty heart appears, so the mark can be made without a
    /// menu. The heart disappears when the pointer leaves.</summary>
    private void Song_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ArtistSong song)
            song.IsHovered = true;
    }

    private void Song_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ArtistSong song)
            song.IsHovered = false;
    }

    /// <summary>The heart itself. The line is filled at once and does not wait for the artist to be read
    /// again, but only if the library accepted the mark.</summary>
    private void SongFavorite_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ArtistSong song)
            return;

        var next = !song.IsFavorite;
        if (SetTrackFavorite?.Invoke(song.Track, next) == true)
            song.IsFavorite = next;
    }

    /// <summary>
    /// Marks what the deck is playing on every line of this page that stands for it: the listing of
    /// their songs, and the lines inside whichever record is open under them.
    ///
    /// <para>The page is told, and it compares what it is told with everything it holds. A song that
    /// arrives after this call (the listing fills one folder at a time) asks for itself in
    /// <see cref="MarkPlaying"/>.</para>
    /// </summary>
    public void SetPlaying(string? filePath)
    {
        foreach (var song in _everySong)
            song.IsPlaying = Same(song.FilePath, filePath);

        foreach (var drawer in Drawers)
            if (drawer.Band is { } band)
                foreach (var line in band.AllTracks)
                    line.IsPlaying = line.IsTrack && Same(line.FilePath, filePath);
    }


    /// <summary>
    /// Reads the cloud again for whichever record is open, and starts the pulse or lets it settle.
    ///
    /// <para><b>Read again, not reused.</b> The lines were built from the folder as it was when the
    /// drawer opened, and what has changed since is exactly what is being asked about: a song that was
    /// queued is being uploaded, or one that was being uploaded has arrived. Badging the marks again
    /// from the tracks already on the lines would repaint them into the state they are already in, and
    /// the drawer would never change.</para>
    ///
    /// <para>The album views answer this signal with the same call, for the same reasons. Whose shelf it
    /// is decides what the marks mean (the mark of a borrowed song says how the music arrives, not
    /// whose disk it is on). A queue that is sending nothing gives a mark at rest and not one that
    /// pulses for days over a full store.</para>
    ///
    /// <para>Each drawer is checked after the read against the band it held before: the reader can close
    /// it, point it at another record or leave the artist during a folder query, and marks from a record
    /// they moved past are marks about something else.</para>
    /// </summary>
    public async void RefreshTrackBadges()
    {

        if (BandSources is not { Tracks: { } load } sources)
            return;

        var pass = ++_badgePass;

        foreach (var drawer in Drawers)
        {
            if (drawer.Band is not { } band || band.Lines.Count == 0)
                continue;

            IReadOnlyList<TrackInfo> tracks;
            try
            {
                tracks = await load(band.Card.Folder, CancellationToken.None);
            }
            catch
            {
                continue;   // a folder that cannot be opened keeps the marks it already had
            }

            if (pass != _badgePass || !ReferenceEquals(drawer.Band, band))
                continue;

            AlbumTrackItem.ApplyCloud(
                band.Lines, tracks,
                sources.UploadsStopped?.Invoke() == true,
                Shelf?.Invoke() ?? RPM.Core.Cloud.ShelfReading.Own);

            // The mark of the record is updated together with the marks of its songs and not on a signal
            // of its own: the cover shows what the whole record is doing and the lines show which of its
            // songs are doing it, and if the two were read at different moments the record would disagree
            // with its own track list.
            if (CloudBadgeLoader is { } badge)
                band.Card.CloudBadge = badge(band.Card.Folder);
        }

        if (pass == _badgePass)
            _lineBreath.Sync();
    }

    /// <summary>Which reading of the cloud this is. The engine can report again while a folder is still
    /// being read for its previous report, and the older answer is about a cloud that has changed.</summary>
    private int _badgePass;

    /// <summary>A mark made anywhere else (the menu, the full-screen player, another computer), put on
    /// the copies of that song on this page.</summary>
    public void ApplyTrackFavorite(string filePath, bool favorite)
    {
        foreach (var song in _everySong)
            if (Same(song.FilePath, filePath))
                song.IsFavorite = favorite;

        foreach (var drawer in Drawers)
            if (drawer.Band is { } band)
                foreach (var line in band.AllTracks)
                    if (line.IsTrack && Same(line.FilePath, filePath))
                        line.IsFavorite = favorite;
    }

    /// <summary>The lines of a record that has just been read, compared with what is playing at this
    /// moment. See <see cref="PlayingPath"/> for why it is read here and not remembered.</summary>
    private void MarkPlaying(IReadOnlyList<AlbumTrackItem> items)
    {
        var playing = PlayingPath?.Invoke();
        foreach (var item in items)
            item.IsPlaying = item.IsTrack && Same(item.FilePath, playing);
    }

    /// <summary>Every drawer, so that all of them are treated in the same way: a mark that reached one
    /// drawer and not the others would be one song with two answers. A shelf added to the page without a
    /// drawer here is not reached by the calls that go through all drawers.</summary>
    private IEnumerable<AlbumBandView> Drawers => Shelves.Select(shelf => shelf.View);

    /// <summary>Whether two names are the same file. Two empty values are not equal: a page with no song
    /// playing must not mark every line whose path the library has forgotten.</summary>
    private static bool Same(string? a, string? b)
        => a is { Length: > 0 } && b is { Length: > 0 }
           && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void AboutSource_Click(object sender, RoutedEventArgs e) => Open(_article?.Url);

    /// <summary>Opens a link from catalogue data, http or https only (see WebLink).</summary>
    private static async void Open(string? url)
    {
        if (RPM.Core.Net.WebLink.Parse(url) is { } link)
            await Windows.System.Launcher.LaunchUriAsync(link.Uri);
    }

    /// <summary>The dark colour of the app, under a band that has nothing else to draw.</summary>
    private static readonly Brush Unlit = new SolidColorBrush(Color.FromArgb(0xFF, 0x1A, 0x1A, 0x20));
}
