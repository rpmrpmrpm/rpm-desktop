using RPM.Fingerprint.Catalog;
using System.Globalization;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media.Animation;
using RPM.Core.Analysis;
using RPM.Core.Import;
using RPM.Core.Library;
using RPM.Core.Lyrics;
using RPM.Core.Metadata;
using RPM.Core.Metadata.Providers;
using RPM.Core.Models;
using RPM.Desktop.Services.Sharing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Services;
using RPM.Desktop.Services.Art;
using RPM.Desktop.Services.Catalog;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Main.LeftPane;

/// <summary>One line of the fact sheet.</summary>
[RpmBindable]
public sealed partial record TrackInfoRow(string Label, string Value);

/// <summary>One block of the fact sheet: a heading and the lines under it.</summary>
/// <remarks>Rows has its own list type because it is bound to an ItemsSource. Under Native AOT the
/// binding needs a type that CsWinRT has generated WinRT interfaces for. See
/// <see cref="Common.TrackInfoRowList"/> (RPM007). A plain List&lt;TrackInfoRow&gt; does not have them, so the credits heading would show with nothing
/// under it.</remarks>
[RpmBindable]
public sealed partial record TrackInfoSection(string Heading, Common.TrackInfoRowList Rows);

/// <summary>
/// The left pane's track sheet: everything this app knows about the record playing, and what the free
/// encyclopedias add to it.
///
/// <para>Three sources, arriving at three speeds, and the view is built so that each one paints as it
/// lands rather than waiting for the slowest. The library's row is already in memory. The file's own
/// tags are a disk read, off the UI thread. They hold the fields the mirror has no column for: the
/// composer, the conductor and the ISRC. Those are half the reason to open the pane. MusicBrainz and
/// Wikipedia are seconds away, behind a limit of one request per second, so they fill in last, under a
/// spinner that says so.</para>
///
/// <para>Everything from outside is augmentation. A lookup that fails leaves the pane exactly as it
/// was, with the local facts on it. That is still the answer to most of what was asked.</para>
/// </summary>
public sealed partial class TrackInfoView : UserControl
{
    private string _path = "";
    private string _folder = "";
    private CancellationTokenSource? _lookup;

    /// <summary>
    /// The song as it was TAGGED. This is not always what the heading says, because the heading drops
    /// any featuring credit: the guest is named on the line under it.
    ///
    /// <para>It is asked of the build when it is wanted, rather than copied into a field when some
    /// layer happens to be painted. The two readers here are the lyric lookup, which must ask a
    /// provider in the file's own words, and the artist pager, which reads the guests out of the title.
    /// They run off different arrivals. A field would make each of them depend on a paint having gone
    /// first. The build IS the record of what the pane is showing.</para>
    /// </summary>
    private string TitleTagged => _build?.Title ?? "";

    private EncyclopediaArticle? _artistArticle;
    private EncyclopediaArticle? _albumArticle;
    private bool _artistExpanded;
    private bool _albumExpanded;
    private string _lyrics = "";

    public TrackInfoView()
    {
        InitializeComponent();
        ShowEmpty();

        // The strip's height is animated, so it changes on every layout pass of the slide and the sheet
        // has to be placed against each one. See FitHeroToStrip.
        BandPhotoBox.SizeChanged += (_, _) => FitHeroToStrip();

        // A pane nobody is looking at must not keep a timer running for a line nobody can see.
        Unloaded += (_, _) => StopLyricTick();
        Loaded += (_, _) =>
        {
            if (_sheet.IsSynced)
                StartLyricTick();
        };

        // A heart pressed on a track line, or in the full screen view, while this pane is showing that
        // same song. Subscribed on Loaded rather than here once, because a pane taken off the tree must
        // not keep the service holding a reference to it. It is re-read on the way back in, since the
        // mark can have moved while nothing was listening. The -= before the += stops a second Loaded
        // from stacking a second handler.
        Loaded += (_, _) =>
        {
            Library.TrackFavoriteChanged -= OnTrackFavoriteChanged;
            Library.TrackFavoriteChanged += OnTrackFavoriteChanged;
            ReadFavorite(_path.Length > 0 ? _path : null);
        };
        Unloaded += (_, _) => Library.TrackFavoriteChanged -= OnTrackFavoriteChanged;
    }

    /// <summary>Raised when the reader asks to open the whole record. The pane does not own the album
    /// page. It lives over the library card, so the request goes up rather than out.</summary>
    public event Action<string>? AlbumRequested;

    /// <summary>One sentence for the window to show along its foot, as the visualizer does. See
    /// <c>MainPage.ShowFlash</c>. This is raised for a press that has no visible answer. "Another
    /// version" that finds nothing else leaves the words exactly as they were. A button that changes
    /// nothing on screen has to say why, or it reads as a key that did not work.</summary>
    public event Action<string>? Announced;

    private static LibraryService Library => App.Services.GetRequiredService<LibraryService>();
    private static TrackInfoService Dossiers => App.Services.GetRequiredService<TrackInfoService>();

    /// <summary>The file the sheet is currently about, or "" while the empty state is up.</summary>
    public string Path => _path;

    /// <summary>
    /// Show the sheet for one audio file. Re-showing the same file is a no-op, which is what keeps the
    /// pane still while playback ticks its position twice a second.
    /// </summary>
    public void Show(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            ShowEmpty();
            return;
        }

        if (string.Equals(path, _path, StringComparison.Ordinal))
            return;

        _path = path;

        // The words belong to the record that just left. Dropped before anything is painted, so the
        // well is never showing one song's lyric under another song's name.
        _lyricLookup?.Cancel();
        _lookup?.Cancel();
        _sheet = LyricSheet.Empty;
        _lyricsArrive = true;
        StopLyricTick();

        ContentScroller.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
        ContentScroller.ChangeView(null, 0, null, disableAnimation: true);   // the new record starts at the top

        var (row, folder) = LibraryRowFor(path);

        // Whether the sleeve on screen is this record's too is asked BEFORE _folder is overwritten. The
        // album folder is the key rather than the album's name. Two records can share a title, but no
        // two share a folder, and the sleeve is read out of that folder in the first place. The answer
        // is kept only where the last read actually answered. A read that threw leaves the square on
        // its placeholder. Keeping that for every remaining track of the album would turn one
        // unreadable file into an album with no cover.
        var sameRecord = folder.Length > 0
                      && _sleeveAnswered
                      && string.Equals(folder, _folder, StringComparison.Ordinal);
        _folder = folder;

        // Only for a record in the collection on screen: the album page draws that one, so offering it
        // for a borrowed record playing under the user's own wall would open an album with nothing in
        // it.
        OpenAlbumButton.Visibility = folder.Length > 0 && TrackHome.For(path) is { OnScreen: true }
            ? Visibility.Visible
            : Visibility.Collapsed;

        // The heart beside it asks a different question of a different collection. The album page is
        // drawn from whichever shelf is on screen, but a mark can only be written to the user's OWN
        // collection. See TrackInfoView.Favorite.
        ReadFavorite(path);

        // From here nothing paints on arrival. The pane is emptied first and each errand hands what it
        // found to the build, which puts the layers back in one order whatever order they land in. See
        // TrackInfoView.Build. How much of the pane this invalidates is decided off the MIRROR's row,
        // because that is the only answer available on this frame. The file's own tags are a disk read
        // away, and the peel cannot wait for them without the pane standing still on the last record.
        // Where the tags turn out to disagree, the build is started again at the wider scope. See
        // FillFromFileAsync.
        var build = Rebuild(path, ScopeFor(PaneArtist(null, row)));
        if (sameRecord)
            build.KeepSleeve();

        // The mirror's row answers on this frame and the file's own tags replace it a moment later.
        // Both go into the same slot; whichever is latest when the credits are due is what goes up.
        build.GotFacts(BuildSections(null, row), TitleFor(null, row), row?.Artist ?? "", "");

        // The sleeve is read from wherever this record keeps it. It is not asked for at all where the
        // record has not changed, which is every track but the first of an album.
        if (!build.SleeveKept)
            _ = LoadCoverAsync(build, row);

        _ = FillFromFileAsync(build, row);     // the file's own tags follow
    }

    /// <summary>
    /// Starts a rebuild of the pane at <paramref name="scope"/>, replacing whatever was building
    /// before.
    ///
    /// <para>The one place a <see cref="PaneBuild"/> is made, so there is one place the peel is started
    /// from and one place the old build is dropped. Everything in flight for the old one is superseded
    /// by identity the moment <c>_build</c> is written.</para>
    /// </summary>
    private PaneBuild Rebuild(string path, PaneScope scope)
    {
        var build = new PaneBuild(path, scope);

        // A pane taken down when the music stopped has nothing to take down again.
        if (_bare)
            build.Order.AlreadyEmpty();
        _bare = false;

        _build = build;
        Peel(build);
        return build;
    }

    /// <summary>Whether the pane is standing empty behind the empty state, which is the one state a
    /// build may start from without peeling first. Written where the pane is emptied and read where the
    /// next one begins, a fact about the pane, not a window of time.</summary>
    private bool _bare;

    /// <summary>Whose pane this is now, and what it is about. Compared against the next record to work
    /// out whether the photograph, the ground and the artist card have to change at all.</summary>
    private string? _paneArtist;

    /// <summary>Whether <paramref name="artist"/> is the one the pane is already about. A pane that has
    /// never had an artist rebuilds in full, and so does one whose next record has none: "no artist" is
    /// not a person two records can have in common.</summary>
    private PaneScope ScopeFor(string? artist)
        => !string.IsNullOrWhiteSpace(artist)
        && string.Equals(artist, _paneArtist, StringComparison.OrdinalIgnoreCase)
            ? PaneScope.Record
            : PaneScope.Artist;

    /// <summary>What the header calls this record: the tag, and the file's own name where there is no
    /// tag to use. Split out of the painting because the answer is settled when the tags are READ and
    /// the painting happens when the layer is due, which are two different moments now.</summary>
    private string TitleFor(TrackInfo? tags, LibraryTrack? row)
        => (tags?.Title ?? row?.Title) is { Length: > 0 } title
            ? title
            : System.IO.Path.GetFileNameWithoutExtension(_path);

    /// <summary>Drops the record and shows the empty state. The pane follows the music, and here there
    /// is none.</summary>
    public void ShowEmpty()
    {
        _build = null;      // nothing is being built, so nothing that lands may paint
        _paneArtist = null;
        StopBeat();
        ShowWait(PaneWait.None);
        _lookup?.Cancel();
        _lyricLookup?.Cancel();
        _sheet = LyricSheet.Empty;
        StopLyricTick();
        _path = "";
        _folder = "";
        ReadFavorite(null);          // no record, so no mark to offer
        ContentScroller.Visibility = Visibility.Collapsed;
        ApplySheetFades();   // The two bands go with the sheet they belong to
        EmptyState.Visibility = Visibility.Visible;

        // The last record's photograph and the last record's colour both go with it: the pane is back
        // to glass, and a picture of somebody nobody is playing has no business on it.
        ClearBandPhoto();

        // The sheet itself is cleared too. See Bare, and the flag it sets, which lets the next record
        // start building on its first beat.
        Bare();
        _bare = true;
    }

    // ── the local facts ──

    /// <summary>The mirror's row for a file, and the album folder it sits in. Both are blank for
    /// something playing from outside the library, which is ordinary: a radio stream, or a file dragged
    /// in.</summary>
    ///
    /// <remarks>This works for any collection the file is in, not only the user's own. A borrowed
    /// record is a row in somebody else's mirror, under a root of its own. Asking the user's mirror
    /// about it would answer nothing. With no row and no file to read either, the sheet has only a file
    /// name to show: no artist, no album, and so no lyric, no band photo and no encyclopedia
    /// entry.</remarks>
    private static (LibraryTrack? Row, string Folder) LibraryRowFor(string path)
    {
        return TrackHome.For(path) is { } home
            ? (TrackHome.RowIn(home, path), home.Folder)
            : (null, "");
    }

    /// <summary>The file's tags, read off the UI thread, then the sheet repainted with them and the
    /// encyclopedias asked. Abandoned silently if the record changed while the disk was being
    /// read.</summary>
    private async Task FillFromFileAsync(PaneBuild build, LibraryTrack? row)
    {
        var path = build.Path;
        var tags = await Task.Run(() =>
        {
            try
            {
                return TagReader.Read(path);
            }
            catch
            {
                return null;   // unreadable, gone, or a format TagLib will not open
            }
        });

        if (!ReferenceEquals(_build, build))
            return;

        var artist = PaneArtist(tags, row);
        var album = tags?.Album ?? row?.Album;

        // The row said one artist and the file says another. It happens on a record whose tags have
        // been edited since the last scan, and it means the build now running is keeping a photograph
        // of somebody who is not on this track. Started again at the wider scope rather than patched:
        // the peel has already run against the wrong answer, and half a rebuild is exactly the state
        // this whole sequence exists to make impossible.
        if (build.Order.Scope == PaneScope.Record && ScopeFor(artist) == PaneScope.Artist)
        {
            _paneArtist = artist;
            build = Rebuild(build.Path, PaneScope.Artist);
        }
        else
        {
            _paneArtist = artist;
        }

        build.GotFacts(BuildSections(tags, row), TitleFor(tags, row),
                       tags?.Artist ?? row?.Artist ?? "", tags?.Lyrics ?? "");
        Advance(build);

        // The photo, the timed lyric and the encyclopedias are separate errands to separate services;
        // none should wait on the others, and the band's picture is much the faster of the three. Not
        // asked for at all where the artist has not changed: the answer is the picture already on
        // screen, and the sequence was told so when the build was made.
        if (!build.Order.Keeps(PaneLayer.Photo))
            _ = LoadBandPhotoAsync(build, artist, album);
        _ = LoadSyncedLyricsAsync(
            build, tags?.Artist ?? row?.Artist, tags?.Title ?? row?.Title, album,
            tags?.DurationSeconds ?? row?.DurationSeconds ?? 0);
        // Both credits go in. The lookup is the RECORD's artist, which is what the album card and the
        // links are about. The pager wants the track's line too, because "Travis Scott / Travis Scott,
        // The Weeknd" is the ordinary shape of a guest appearance. The album artist names the act whose
        // record it is, and the track artist is the only field that mentions the guest at all. Also the
        // number of songs on the record. That is what tells an album from a single with the same name.
        // The count comes off the tags, because the mirror's row has no count. The tag states the
        // record's own track total, rather than counting the files in the folder.
        // The conductor goes with the papers: asked beside them, handed over with them, put up with them.
        await LookUpAsync(build, artist, album, tags?.Artist ?? row?.Artist, (int)(tags?.TrackCount ?? 0),
                          tags?.Conductor);
    }

    /// <summary>
    /// Who the pane is ABOUT: the photograph, the encyclopedia article, and the links.
    ///
    /// <para>This is the record's artist, usually, because that is the act the reader is listening to.
    /// It survives the track changing within the album. A compilation has no such act. Its album artist
    /// is "Various Artists", which is not a person. It is the absence of one. A pane that asked the
    /// catalogues about that name would get a photograph of nobody, an article about the phrase, and a
    /// row of links to a page that is not about this music. On those records the track's own artist is
    /// the only real name in the tags, so it is the one asked about. The pane then changes with the
    /// track, as the listener does.</para>
    /// </summary>
    private static string? PaneArtist(TrackInfo? tags, LibraryTrack? row)
    {
        var album = tags?.AlbumArtist ?? row?.AlbumArtist;
        var track = tags?.Artist ?? row?.Artist;

        var pick = IsVariousArtists(album) ? track ?? album : album ?? track;

        // If that is still the marker (a compilation whose tracks carry no artist of their own), the
        // pane has no artist to be about. Nothing is better than a card headed "Various Artists" over
        // whatever a catalogue returns when asked about the phrase.
        return IsVariousArtists(pick) ? null : pick;
    }

    /// <summary>Whether a name is the compilation marker rather than somebody; see
    /// <see cref="ArtistCredit.IsVariousArtists"/>.</summary>
    private static bool IsVariousArtists(string? name) => ArtistCredit.IsVariousArtists(name);

    private static List<TrackInfoSection> BuildSections(TrackInfo? tags, LibraryTrack? row)
    {
        var facts = TrackFacts.Build(tags, row, Formats);

        // The groups keep the order the sheet was built in. The view puts a heading between them and
        // never re-sorts, so a reader who has learned where the ISRC lives finds it there next time.
        //
        // Credits only. The song's own numbers, the file's and the library's, are left out. They are
        // the three groups whose every line is written somewhere the reader already looks. The title
        // and the artist are at the top of this very pane. The format and the bitrate are on the
        // track's row in the grid. The play count and the date added are columns in it. What is left is
        // the one group nothing else in the app shows: who wrote it, who played on it, who produced it,
        // and the numbers that identify the recording rather than the file.
        return [.. facts
            .Where(f => f.Group == TrackFactGroup.Credits)
            .GroupBy(f => f.Group)
            .Select(group => new TrackInfoSection(
                Loc.Get(HeadingKey(group.Key)),
                [.. group.Select(f => new TrackInfoRow(Loc.Get(f.LabelKey), f.Value))]))];
    }

    private static string HeadingKey(TrackFactGroup group) => group switch
    {
        TrackFactGroup.Song => "TrackInfo_GroupSong",
        TrackFactGroup.Credits => "TrackInfo_GroupCredits",
        TrackFactGroup.File => "TrackInfo_GroupFile",
        _ => "TrackInfo_GroupLibrary",
    };

    /// <summary>The four values that cannot be worded without the user's language and time
    /// zone.</summary>
    private static readonly TrackFactFormats Formats = new(
        Duration: Clock,
        Size: ByteSize.Human,
        Timestamp: ticks =>
        {
            try
            {
                return new DateTime(ticks, DateTimeKind.Utc).ToLocalTime()
                    .ToString("g", CultureInfo.CurrentCulture);
            }
            catch
            {
                return "";   // a tick count from a corrupt row; a blank value is dropped from the sheet
            }
        },
        OfCount: (position, count) => count > 0
            ? Loc.Format("TrackInfo_OfCount", position, count)
            : position.ToString(CultureInfo.CurrentCulture));

    /// <summary>A running time, as a player says it: "4:32", and "1:04:32" once there is an hour in
    /// it.</summary>
    private static string Clock(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }

    // ── the words ──

    /// <summary>The timed sheet for the record on screen, or <see cref="LyricSheet.Empty"/> when nobody
    /// has stamped this one. What decides whether the well follows the singer or is simply a wall of
    /// words to scroll.</summary>
    private LyricSheet _sheet = LyricSheet.Empty;

    /// <summary>The rows actually drawn, top to bottom. NOT one per line of the sheet: a line with
    /// nothing to read on it is given no row at all, so this is shorter than the sheet by however many
    /// breaks it has. <see cref="_lyricFor"/> is what crosses between the two.</summary>
    private TextBlock[] _lyricLines = [];

    /// <summary>
    /// Which row each line of the sheet is drawn as: an index into <see cref="_lyricLines"/> per line
    /// of <see cref="_sheet"/>, and -1 for a break before the first row exists.
    ///
    /// <para>A line that was dropped points at the row BEFORE it, which is what holds the sheet still
    /// through a break: the clock moves on to a line nobody is singing, the lookup lands back on the
    /// last line that was, and it simply stays lit until the next words arrive. Without the map the
    /// highlight would run off the end of a shorter array, and dropping the lines without holding the
    /// words is what would make the well flicker dark at every instrumental.</para>
    /// </summary>
    private int[] _lyricFor = [];

    /// <summary>Which line was lit last. Repainting every line is cheap but not free, and this is what
    /// keeps it to a few times a minute instead of four times a second.</summary>
    private int _lyricAt = -2;

    /// <summary>Cancels the lyric lookup of the record being left, so an answer about the last track
    /// cannot land on the pane showing the next one.</summary>
    private CancellationTokenSource? _lyricLookup;

    private DispatcherTimer? _lyricTick;

    /// <summary>
    /// The two inks a lyric is written in: the line being sung, and the rest.
    ///
    /// <para>These are the pane's own brushes, not a pair of whites of their own. A fixed white is
    /// right on most records and unreadable on the rest. A pale artist photograph turns every other
    /// line in this pane black (see <see cref="PaintPanelTint"/>), and the well keeps its near-white
    /// card to match. White words on a near-white card cannot be read. Pointed at the shared brushes,
    /// the lyric ink flips with everything else.</para>
    /// </summary>
    private SolidColorBrush LyricLit => Ink("InkStrong");

    private SolidColorBrush LyricDim => Ink("InkBody");

    /// <summary>The file's own words, kept so the well can fall back to them if a timed sheet never
    /// arrives. A record that has both is not redrawn from scratch when the timed sheet
    /// lands.</summary>
    private void PaintLyrics(string lyrics)
    {
        _lyrics = lyrics.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

        // A timed sheet outranks the file's plain text and is already up: leave it alone. The tags are
        // read twice on the way into a record (the mirror's row, then the file itself) and the second
        // pass must not throw away a sheet that arrived in between.
        if (_sheet.IsSynced)
            return;

        // The file's words may not be plain words at all. A lyrics tag holding "[00:04.11] Hello,
        // hello" is a timed sheet that somebody has already written down. Printing it verbatim would
        // show the clock down the left margin, on every line, following nothing. Read as what it is, it
        // becomes the well's sheet. The stamps go, and the words move with the singer, using the file
        // alone, with nobody asked and nothing waited for. Ordinary lyrics parse to nothing and fall
        // straight through to the wall of text they always are.
        if (LyricSheet.Parse(_lyrics) is { IsSynced: true } own)
            _sheet = own;

        BuildLyricWell();
    }

    /// <summary>
    /// Fills the well: one line per row, from the timed sheet where there is one and from the file's
    /// own lyrics where there is not.
    ///
    /// <para>Built as separate blocks either way, rather than as one wrapped paragraph. That is what
    /// makes a line something that can be lit. It costs nothing for the unsynced case, which simply
    /// lights none of them.</para>
    /// </summary>
    private void BuildLyricWell()
    {
        // The well's height before it is emptied. It is zero on a change of record, which asks for the
        // arrival rather than the growth. The growth is the one that matters here. A song opens on the
        // file's plain words, or on nothing at all, and a timed sheet lands seconds later at a
        // different length. Without the animation the sheet would snap, with the whole sheet below it
        // moving at once.
        var was = _lyricsArrive ? 0 : LyricPage.ActualHeight;
        _lyricsArrive = false;

        LyricStack.Children.Clear();
        _lyricAt = -2;

        var mask = App.Services.GetRequiredService<SettingsService>().MaskProfanity;

        // The plain wall is unstamped line by line as it is drawn. Anything that parsed is already a
        // sheet by now (see PaintLyrics); what reaches here with a clock on it is a tag the reader
        // could not make a sheet OF, a stamp with no brackets, one in round brackets, a half-timed
        // file, and those words are still worth reading without the numbers in front of them.
        var lines = _sheet.IsSynced
            ? _sheet.Lines.Select(l => l.Text).ToArray()
            : _lyrics.Length > 0 ? [.. _lyrics.Split('\n').Select(LyricSheet.Unstamp)] : Array.Empty<string>();

        var rows = new List<TextBlock>(lines.Length);
        _lyricFor = new int[lines.Length];

        for (var i = 0; i < lines.Length; i++)
        {
            // A break gets no row of its own, and the well closes over it.
            //
            // Only on a TIMED sheet. A blank line in a wall of plain words is the gap between two
            // verses and is the only thing separating them, whereas on a sheet it is a stretch of time
            // with nothing sung in it, the words either side are consecutive, and a reader following
            // the singer down a 150-point window should not lose a third of it to a rest. The mark a
            // writer puts on such a line, a note on its own, says exactly what the empty one does and
            // goes with it: see LyricSheet.IsWordless.
            if (_sheet.IsSynced && LyricSheet.IsWordless(lines[i]))
            {
                _lyricFor[i] = rows.Count - 1;
                continue;
            }

            // Masked here, at the moment of drawing, exactly as the visualizer does it: the sheet in
            // hand and the copy on disk stay as they were written. See LyricCensor.
            var block = new TextBlock
            {
                Text = LyricCensor.Mask(lines[i], mask),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                LineHeight = 18,
                Foreground = LyricDim,
                // A timed sheet starts dim and is lit a line at a time; a wall of plain words is all
                // there is to read and is shown at full strength.
                Opacity = _sheet.IsSynced ? 0.45 : 1,
                IsTextSelectionEnabled = !_sheet.IsSynced,
            };
            _lyricFor[i] = rows.Count;
            rows.Add(block);
            LyricStack.Children.Add(block);
        }

        _lyricLines = [.. rows];

        // What is on show decides whether there is a well at all: a sheet of nothing but rests is a
        // sheet with nothing to read, and an empty card headed "Lyrics" is worse than no card.
        LyricsBlock.Visibility = _lyricLines.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Whose well it is. A timed sheet belongs to the record. It moves with the singer and takes no
        // hand at all, which is what the full-screen player does, and the reason a line is never in the
        // wrong place there. The well is deaf to the pointer rather than merely barless. That stops the
        // wheel, the drag and the text cursor in one go. It leaves ChangeView working, because that is
        // a property of the scroller and not of the pointer. It also lets a wheel turn over the words
        // reach the sheet underneath, which is what a reader spinning the wheel down this pane actually
        // means.
        //
        // Plain words keep their hand. There is no playhead to follow, and a 150-point window onto
        // three verses that cannot be moved shows the first verse and hides the song.
        LyricScroller.IsHitTestVisible = !_sheet.IsSynced;
        LyricScroller.ChangeView(null, 0, null, disableAnimation: true);

        // The alternatives are only ever a question about a TIMED sheet. Over plain words the button
        // would be offering to fix an alignment that does not exist.
        LyricAnotherButton.Visibility = _sheet.IsSynced ? Visibility.Visible : Visibility.Collapsed;

        // The listener's own file stands whatever is up, and stands hardest when nothing is: a record
        // the catalogues have never heard of shows no sheet, no alternatives, and no way to give it
        // one. Only that there IS a song is known. Without a length there is no key to file it under.
        LyricOwnButton.Visibility = _path.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_sheet.IsSynced)
            StartLyricTick();
        else
            StopLyricTick();

        MoveCard(LyricMotion, LyricsBlock.Visibility == Visibility.Visible, way: 0, was);
    }

    /// <summary>Whether the next fill of the well belongs to a new record. Set where the last record's
    /// words are dropped; see <see cref="MoveCard"/> for what the difference buys.</summary>
    private bool _lyricsArrive = true;

    private CardMotion? _lyricMotion;
    private CardMotion LyricMotion => _lyricMotion ??= new CardMotion(LyricsBlock, LyricPage, LyricPageShift);

    /// <summary>
    /// Asks what is being sung.
    ///
    /// <para>Asked because the pane showing the words is on screen, and only then. The host paints this
    /// view while the left pane is open on it, so no lookup happens during ordinary listening. A record
    /// with no length is not asked about at all. A radio stream has no duration to match on and no
    /// stable title, so a lookup for one is a request that cannot succeed.</para>
    /// </summary>
    private async Task LoadSyncedLyricsAsync(PaneBuild build, string? artist, string? title, string? album, double seconds)
    {
        var path = build.Path;
        _lyricLookup?.Cancel();

        // Every way out of this method has to say something, including the ways that find nothing. The
        // lyric well is the last block the pane puts up and the credits underneath it wait on the whole
        // text region being settled, so an errand that returns quietly holds the bottom half of the
        // pane empty for ever.
        void Settle(LyricSheet sheet)
        {
            if (!ReferenceEquals(_build, build))
                return;
            build.GotWords(sheet);
            Advance(build);
        }

        // Asked in the deck's own words when this is the record playing, which it always is today.
        //
        // The answer is remembered under exactly what was ASKED (see LyricsService.Key), and the
        // visualizer asks with the deck. A pane that asked with the file's tags instead would be a
        // second question about one song, the moment the two disagreed about its length by a second.
        // They routinely do, since one number comes off the decoder and the other out of a tag. That
        // would be two round trips, two cache entries, and a visualizer that still had to wait for its
        // own answer on a record this pane had the words for all along. One question, one answer, and
        // whichever of the two opens first warms the other.
        if (App.Services.GetRequiredService<RPaMpService>().Transport.Deck is { } deck
            && string.Equals(deck.FilePath, path, StringComparison.Ordinal))
        {
            artist = deck.Artist;
            title = deck.Title;
            album = deck.Album;
            seconds = deck.DurationSeconds;
        }

        if (seconds <= 0 || string.IsNullOrWhiteSpace(title))
        {
            Settle(LyricSheet.Empty);
            return;
        }

        var cts = new CancellationTokenSource();
        _lyricLookup = cts;

        LyricSheet sheet;
        try
        {
            sheet = await App.Services.GetRequiredService<LyricsService>()
                .ForAsync(artist, title, album, seconds, path, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;   // the record moved on and a newer lookup owns the well
        }
        catch
        {
            Settle(LyricSheet.Empty);   // nothing came back; the file's own words are the answer
            return;
        }

        // The record may have changed while the question was in the air.
        if (cts.IsCancellationRequested || !ReferenceEquals(_build, build))
            return;

        // No timed sheet is the ordinary answer, and it is still an answer: the well goes up on the
        // file's own words instead.
        Settle(sheet);
    }

    /// <summary>
    /// Puts up the next timed sheet this record has, for the reader who can see that the one showing is
    /// not it.
    ///
    /// <para>The words running a beat ahead of the singer the whole way down is the shape of the fault.
    /// It is not a fault in the sheet. It is the right transcription, timed against a different cut of
    /// the song, and anything much covered or much remastered has several cuts. Nothing in the timings
    /// says which cut is playing. That is why the automatic path takes the first one that fits the
    /// length, and it is right nearly always. So the only instrument that can settle it is somebody
    /// looking at the screen. See <see cref="LyricsService.AnotherAsync"/>, which goes round the list,
    /// so this is never a dead end, and the sheet it started on is always one more press away.</para>
    /// </summary>
    private async void LyricAnother_Click(object sender, RoutedEventArgs e)
    {
        if (_lyricBusy)
            return;   // one question at a time; the button is a press, not a rate

        var path = _path;
        var artist = _artistCredit;
        var title = TitleTagged is { Length: > 0 } tagged ? tagged : _titleRaw;
        var album = _albumName;
        var seconds = 0.0;

        // Asked in the deck's own words when this is the record playing, for the same reason the first
        // lookup is: one question about one song, and an answer both this pane and the visualizer can
        // use. See LoadSyncedLyricsAsync.
        if (App.Services.GetRequiredService<RPaMpService>().Transport.Deck is { } deck
            && string.Equals(deck.FilePath, path, StringComparison.Ordinal))
        {
            artist = deck.Artist;
            title = deck.Title;
            album = deck.Album;
            seconds = deck.DurationSeconds;
        }

        if (seconds <= 0 || string.IsNullOrWhiteSpace(title))
            return;

        _lyricBusy = true;
        LyricAnotherButton.IsEnabled = false;
        try
        {
            var next = await App.Services.GetRequiredService<LyricsService>()
                .AnotherAsync(artist, title, album, seconds);

            // The choice is kept for this record whether or not it is still on screen, so the other
            // devices are told about it in both cases.
            if (next.IsSynced)
                App.Services.GetRequiredService<LyricSheetShare>().Chosen(path, next);

            // The record changed while the question was in the air. The answer belongs to a song that
            // is no longer on screen, and there is nothing to say about it: nobody is waiting on it
            // now.
            if (!string.Equals(path, _path, StringComparison.Ordinal))
                return;

            // An empty answer means there was nothing else to show. This is the only sheet anybody has,
            // or the lookup could not be reached. The words stay exactly where they are, which is why
            // it is said out loud. The screen would look identical to how it looked before the press,
            // which is indistinguishable from a button that does not work.
            if (!next.IsSynced)
            {
                Announced?.Invoke(Strings.Loc.Get("TrackInfo_LyricsNoOtherVersion"));
                return;
            }

            _sheet = next;
            BuildLyricWell();
        }
        catch
        {
            // Decoration on a record that is already playing. A lookup that will not answer leaves the
            // sheet that is up alone.
        }
        finally
        {
            _lyricBusy = false;
            LyricAnotherButton.IsEnabled = true;
        }
    }

    /// <summary>Whether a question about the alternatives is already in the air. The button is a press
    /// rather than a rate: holding it down should not open five lookups.</summary>
    private bool _lyricBusy;

    /// <summary>
    /// Takes a sheet the listener has themselves, out of an .lrc file on their disk.
    ///
    /// <para>The record with no words at all is exactly the record somebody has a file for. Examples
    /// are a bootleg, a live cut, a private recording, or anything LRCLIB has never been asked about.
    /// So this stands whether or not the pane found a sheet, and it is the only way those records ever
    /// get one.</para>
    ///
    /// <para>Refused if the file carries no TIMINGS. The well is a sheet that follows the singer, and a
    /// plain text file of the words would go up as a wall that never moves, which reads as a sheet that
    /// is broken rather than one that was never timed. Said out loud, because from the outside a
    /// refusal and a press that did nothing look identical.</para>
    /// </summary>
    private async void LyricOwn_Click(object sender, RoutedEventArgs e)
    {
        if (_lyricBusy)
            return;

        var path = _path;
        var artist = _artistCredit;
        var title = TitleTagged is { Length: > 0 } tagged ? tagged : _titleRaw;
        var album = _albumName;
        var seconds = 0.0;

        // Asked in the deck's own words when this is the record playing, exactly as the lookup beside
        // it is: the sheet is kept under those words, and a file filed under any others would be found
        // by nobody afterwards.
        if (App.Services.GetRequiredService<RPaMpService>().Transport.Deck is { } deck
            && string.Equals(deck.FilePath, path, StringComparison.Ordinal))
        {
            artist = deck.Artist;
            title = deck.Title;
            album = deck.Album;
            seconds = deck.DurationSeconds;
        }

        if (seconds <= 0 || string.IsNullOrWhiteSpace(title))
            return;

        _lyricBusy = true;
        LyricOwnButton.IsEnabled = false;
        try
        {
            if (await PickLyricFileAsync() is not { } lrc)
                return;   // nothing chosen, or the file would not read

            var sheet = LyricSheet.Parse(lrc);
            if (!sheet.IsSynced)
            {
                Announced?.Invoke(Strings.Loc.Get("TrackInfo_LyricsNotTimed"));
                return;
            }

            // The record changed while the picker was open. The picker is a dialog, and a listener can
            // let a song finish behind it. Filing the sheet is still right, since it is filed under the
            // words this began with, and not under whatever is playing now. Putting it on SCREEN is not
            // right.
            var taken = App.Services.GetRequiredService<LyricsService>()
                .Adopt(artist, title, album, seconds, sheet);
            if (taken.IsSynced)
                App.Services.GetRequiredService<LyricSheetShare>().Chosen(path, taken);

            if (!taken.IsSynced || !string.Equals(path, _path, StringComparison.Ordinal))
                return;

            _sheet = taken;
            BuildLyricWell();
        }
        catch
        {
            // A file that would not open or would not parse. The sheet that is up stays up.
        }
        finally
        {
            _lyricBusy = false;
            LyricOwnButton.IsEnabled = true;
        }
    }

    /// <summary>The .lrc the listener chose, as text. Null for a cancelled picker, a file that would
    /// not read, and a platform with no picker at all.</summary>
    private static async Task<string?> PickLyricFileAsync()
    {
        try
        {
            // A timed sheet is often saved as plain .txt by whoever wrote it. The brackets inside are
            // what decide whether it is timed, not the three letters after the dot. It is offered, and
            // then judged on its contents like any other.
            var picker = Services.Shell.Pickers.OpenFile(
                Windows.Storage.Pickers.PickerLocationId.MusicLibrary, ".lrc", ".txt");
            return await picker.PickSingleFileAsync() is { } file
                ? await RPM.Core.Files.ForeignText.ReadFileAsync(file.Path, CancellationToken.None)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Follows the singer: lights the line being sung and brings it to the middle of the well.
    ///
    /// <para>Four times a second off a timer, not per frame. The visualizer reads the playhead every
    /// frame because text is sliding continuously past a shader there; here a line changes a few times
    /// a minute and lands on a scroller, and a quarter of a second is under what an eye notices.</para>
    /// </summary>
    private void TickLyrics()
    {
        if (_lyricLines.Length == 0 || !_sheet.IsSynced)
            return;

        var playback = App.Services.GetRequiredService<PlaybackService>();

        // The pane follows the music, but the sheet in it belongs to a file: a record that is no longer
        // the one playing has no playhead of its own and must not be scrolled by somebody else's.
        if (!string.Equals(playback.PlayingPath, _path, StringComparison.Ordinal))
            return;

        // The clock answers in the sheet's own lines. The well is drawn in rows, and the two are not
        // the same list once the rests have been taken out. A rest maps back to the row before it, so
        // the last words sung stay lit until the next ones are. See _lyricFor.
        var line = _sheet.IndexAt(playback.PositionSeconds);
        var at = line >= 0 && line < _lyricFor.Length ? _lyricFor[line] : -1;

        if (at == _lyricAt)
            return;
        _lyricAt = at;

        for (var i = 0; i < _lyricLines.Length; i++)
        {
            // Neighbours are the rows either side, not the lines either side, because that is what the
            // reader sees. A rest between two verses would otherwise spend this tier on a row with
            // nothing written on it, and the line coming next would be dimmed with the rest of the
            // song.
            var sung = i == at;
            _lyricLines[i].Opacity = sung ? 1 : Math.Abs(i - at) == 1 ? 0.6 : 0.35;
            _lyricLines[i].Foreground = sung ? LyricLit : LyricDim;
        }

        if (at >= 0 && at < _lyricLines.Length)
            ScrollToLyric(at);
    }

    /// <summary>Puts one line in the middle of the well. Measured off the stack rather than summed from
    /// the font, because how many rows a line takes depends on where its words happen to
    /// break.</summary>
    private void ScrollToLyric(int index)
    {
        var block = _lyricLines[index];
        if (block.ActualHeight <= 0 || LyricScroller.ViewportHeight <= 0)
            return;

        var y = block.TransformToVisual(LyricStack).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
        var target = y - (LyricScroller.ViewportHeight - block.ActualHeight) / 2;
        LyricScroller.ChangeView(null, Math.Max(0, target), null);
    }

    private void StartLyricTick()
    {
        _lyricTick ??= Build();
        _lyricTick.Start();
        TickLyrics();

        DispatcherTimer Build()
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (_, _) => TickLyrics();
            return timer;
        }
    }

    private void StopLyricTick() => _lyricTick?.Stop();

    // ── the band, and the sleeve on the corner of their photo ──

    /// <summary>How far the sleeve hangs BELOW the foot of the photograph. This is the fixed end of the
    /// sleeve. The sleeve hangs from this line and grows upward into the picture, so a bigger sleeve
    /// reaches further over the photograph and never further down the pane.</summary>
    ///
    /// <remarks>Which end is fixed is the whole design. The foot of the sleeve is the fixed end, so the
    /// growth lands on the photograph. If the overlap were the fixed value, the top edge would stay
    /// where it was, and all the extra points would go out of the bottom, past the picture and down
    /// over the words. Everything under the sleeve, such as the heart and the way into the record,
    /// keeps its place as the sleeve grows.</remarks>
    private const double HeroSleeveDrop = 46;

    /// <summary>How much of the sleeve sits over the photograph: whatever is left of it above the line
    /// it hangs from, so it reads as resting on the corner rather than as a second picture butted
    /// against the first.</summary>
    private double HeroOverlap => CoverBox.Height - HeroSleeveDrop;

    /// <summary>The gap between the foot of the photo and the top of the track's name.</summary>
    private const double HeroTitleGap = 12;

    /// <summary>
    /// The header, with the band's photograph or without it. Only the top strip comes and goes: the
    /// name and the sleeve keep their places either way, so the header does not rearrange itself under
    /// the reader when a photo arrives a moment after the track did.
    /// </summary>
    // ══ the foot of the photograph, actually taken away ══
    //
    // A fade is not a gradient laid over the top of something. A gradient ramps to the pane's own
    // colour, and that works only while the pane is one flat colour. Once a blurred wash is behind it
    // (see PaneWash), there is no single colour to ramp to. The reader would see a grey bar across the
    // bottom of every photograph.
    //
    // So the pixels are removed instead. XAML cannot do that, because WinUI has no OpacityMask, but
    // Composition can. A CompositionVisualSurface holds a live copy of the photograph's own rendering.
    // A CompositionMaskBrush multiplies that copy by a white-to-transparent ramp. A sprite paints the
    // result in the band at the foot. Whatever is behind (the wash, the colour, the glass) shows
    // through, because nothing has been painted over it.
    //
    // The live picture has to be cut away in the same band, or it would still be under there at full
    // strength. The cut cannot go on the visual being copied, because the copy is that visual's own
    // rendering, clip included. It goes on the wrapper around it (PhotoCut), which the copy knows
    // nothing about. This is PegasusChrome.TopFade's technique. The long form of the argument is there.

    /// <summary>How deep the dissolve runs. Deep enough that it finishes level with the song's title:
    /// the picture is gone by the line that names the record, rather than a third of the way into
    /// it.</summary>
    private const double PhotoFadeDepth = 172;

    /// <summary>
    /// How far the painted band reaches back over the live picture. It is zero: the band and the cut
    /// meet exactly.
    ///
    /// <para>An overlap looks like the simpler fix, but it is the wrong one. The pane is not opaque.
    /// Its content stands on a thin white frost, so where two layers overlap, the frost is painted
    /// twice. That makes a bright hairline at the band's depth, and a wider overlap only makes the
    /// hairline wider. The gap that a rounding difference could leave is prevented at the source
    /// instead. Every depth is rounded to a whole point before it is used, so the cut and the copy come
    /// from ONE integer. See UpdatePhotoFade.</para>
    /// </summary>
    private const float SeamOverlap = 0f;

    private CompositionVisualSurface? _photoFootCopy;
    private SpriteVisual? _photoFootSprite;
    private InsetClip? _photoCut;
    private bool _noPhotoFade;

    /// <summary>The copied visual has been re-arranged. Its NEW size is handed straight through, rather
    /// than read back off the tree a moment later: this fires many times a second while the strip is
    /// sliding, and the whole point of watching it here is to have the size that belongs to this
    /// pass.</summary>
    private void PhotoLayers_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdatePhotoFade((float)e.NewSize.Width, (float)e.NewSize.Height);

    /// <summary>Builds the copy, the ramp and the one cut that takes the live picture out of the band.
    /// Once, and never again on a machine whose composition stack will not do it.</summary>
    private void EnsurePhotoFade()
    {
        if (_photoFootSprite is not null || _noPhotoFade)
            return;

        try
        {
            var compositor = ElementCompositionPreview.GetElementVisual(PhotoFootFade).Compositor;

            var copy = compositor.CreateVisualSurface();
            copy.SourceVisual = ElementCompositionPreview.GetElementVisual(PhotoLayers);

            // Nothing is left to the brush's own alignment: the offset below says which band of the
            // picture is wanted, and an alignment ratio would be a second way of saying the same thing
            // that compounded with it.
            var pixels = compositor.CreateSurfaceBrush(copy);
            pixels.Stretch = CompositionStretch.None;
            pixels.HorizontalAlignmentRatio = 0;
            pixels.VerticalAlignmentRatio = 0;

            // Only the ALPHA of the ramp is read: everything at the top of the band, nothing at the
            // bottom of it.
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
            ElementCompositionPreview.SetElementChildVisual(PhotoFootFade, sprite);

            var cut = compositor.CreateInsetClip();
            ElementCompositionPreview.GetElementVisual(PhotoCut).Clip = cut;

            _photoFootCopy = copy;
            _photoFootSprite = sprite;
            _photoCut = cut;
        }
        catch
        {
            // No composition is available for this. The photograph keeps a hard bottom edge. That is a
            // plainer picture, not a broken pane.
            _noPhotoFade = true;
        }
    }

    /// <summary>
    /// Points the copy at the last band of the picture and cuts that band out of the live one.
    ///
    /// <para>A surface renders the visual it was given from that visual's own origin, less whatever
    /// SourceOffset says, so the LAST band is asked for by offsetting down by everything above
    /// it.</para>
    /// </summary>
    private void UpdatePhotoFade(float width, float height)
    {
        if (BandPhotoBox.Visibility != Visibility.Visible)
            return;

        EnsurePhotoFade();
        if (_photoFootCopy is null || _photoFootSprite is null || _photoCut is null)
            return;

        if (width <= 0 || height <= 0)
            return;

        // While the strip is sliding open the picture is shorter than the dissolve wants to be, and a
        // band deeper than the thing it is fading would ask the copy for pixels above the picture's own
        // top. It simply dissolves the whole of what there is until there is enough. Whole points, for
        // the reason written out in SetSheetFades: the strip's height is animated, so it is fractional
        // on every frame of the slide, and a cut and a copy that round it separately leave a line
        // between them.
        //
        // Over a reflected wide photo the dissolve covers the space under the photo, which fades the
        // mirror out; PhotoFadeDepth would reach up into the photo itself. See RunOff.FadeDepth, which
        // also stays within the strip while it slides.
        var wanted = ShowingRunOff is { } aspect
            ? RunOff.FadeDepth(width, height, aspect)
            : Math.Min(PhotoFadeDepth, height);
        var depth = MathF.Round((float)wanted);

        // The band meets the cut exactly, with no overlap. See SeamOverlap. Both edges are rounded to
        // whole points first, so they land on the same device line. A sub-pixel mismatch would
        // otherwise leave a hairline of the photograph's bottom edge showing under the dissolve, and
        // that line moves while the sheet is scrolled.
        var cut = Math.Max(0, depth - SeamOverlap);

        PhotoFootFade.Height = depth;
        _photoCut.BottomInset = cut;

        BandSurface.Fit(_photoFootCopy, _photoFootSprite, width, depth, MathF.Round(height) - depth);
    }

    // ══ the band photograph, and the crossing between two of them ══

    /// <summary>Whose photograph is up. What lets a change of track by the same artist do nothing at
    /// all, which is most changes of track.</summary>
    private string? _photoArtist;

    /// <summary>Which photograph that is, as opposed to whose. Two names can settle on one picture, a
    /// credit and the lead name it falls back to (see LoadBandPhotoAsync), and this is what tells a
    /// change of artist from a change of the thing on screen.</summary>
    private string? _photoUrl;

    /// <summary>Which of the two layers is the one currently showing.</summary>
    private bool _photoOnA;

    private ChromeFade? _photoFadeA, _photoFadeB;

    /// <summary>How long one photograph takes to become the next. Slow enough to read as a dissolve
    /// rather than as a change that has already happened by the time the viewer looks.</summary>
    private const int BandPhotoFadeMs = 420;

    /// <summary>
    /// Crosses to a new photograph: the incoming layer comes up, the outgoing one goes down, and the
    /// strip opens underneath if this is the first picture the pane has had.
    /// </summary>
    /// <param name="runOff">The photo's aspect when <paramref name="bitmap"/> is a <see cref="RunOff"/>
    /// canvas, and null for a picture that fills the strip. Required, because the foot dissolve's depth
    /// depends on it.</param>
    private void ShowBandPhoto(ImageSource bitmap, string artist, double? runOff)
    {
        _photoArtist = artist;

        var inFade = _photoOnA ? PhotoFadeB : PhotoFadeA;
        var outFade = _photoOnA ? PhotoFadeA : PhotoFadeB;
        PutBandPhoto(onA: !_photoOnA, bitmap, runOff);
        _photoOnA = !_photoOnA;

        // The strip opens before the fade rather than with it. Coming up FROM nothing there is no
        // outgoing picture to cross with, and a 150-point row growing while a photograph fades into it
        // is two changes competing for the same beat.
        ArrangeHero(hasPhoto: true);

        inFade.To(1, BandPhotoFadeMs);
        outFade.To(0, BandPhotoFadeMs);

        // The dissolve's depth follows the picture now up, and an open strip does not change size to
        // trigger it.
        UpdatePhotoFade((float)PhotoLayers.ActualWidth, (float)PhotoLayers.ActualHeight);
    }

    /// <summary>The photo's aspect for each strip layer while it shows a <see cref="RunOff"/> canvas,
    /// and null while it shows a picture that fills the strip. Written only by
    /// <see cref="PutBandPhoto"/>.</summary>
    private double? _runOffA, _runOffB;

    /// <summary>The same, for the layer that is up.</summary>
    private double? ShowingRunOff => _photoOnA ? _runOffA : _runOffB;

    /// <summary>Writes one strip layer's picture and whether it is a reflected wide photo. The only
    /// place either is written, so a layer's picture and the dissolve depth read for it cannot
    /// disagree. Both kinds are drawn the same way, covering the strip from the top-left corner; see
    /// the markup.</summary>
    private void PutBandPhoto(bool onA, ImageSource? picture, double? runOff)
    {
        if (onA)
        {
            BandPhotoArtA.ImageSource = picture;
            _runOffA = runOff;
        }
        else
        {
            BandPhotoArtB.ImageSource = picture;
            _runOffB = runOff;
        }
    }

    /// <summary>Takes the strip down and forgets the picture on it. This applies to an artist with no
    /// photograph, or to no artist playing. Both layers go. The one underneath would otherwise be the
    /// last artist's, waiting to be uncovered by the next crossing. The wash behind the whole pane goes
    /// too, and so does the colour. With no picture there is nothing for the pane to be the colour of,
    /// so it goes back to the app's frosted glass rather than keeping the last artist's tint.</summary>
    private void ClearBandPhoto()
    {
        _photoArtist = null;
        _photoUrl = null;
        PhotoFadeA.To(0, 0);
        PhotoFadeB.To(0, 0);
        PutBandPhoto(onA: true, picture: null, runOff: null);
        PutBandPhoto(onA: false, picture: null, runOff: null);
        ArrangeHero(hasPhoto: false);

        ClearPaneWash();
        PaintPanelTint(null, null);
    }

    /// <summary>Takes the wash off the pane: no picture to put there, or one too pale to write on. The
    /// source goes with the fade, so the last artist's blur is not still in memory waiting to be faded
    /// back up by a record that should not have one.</summary>
    private void ClearPaneWash()
    {
        PaneWashFade.To(0, BandPhotoFadeMs);
        PaneWashArt.ImageSource = null;
    }

    /// <summary>
    /// How much of the wash is let through: about a third, and the pane's own colour is the rest.
    ///
    /// <para>Letting through nearly all of it would treat the blurred photograph as the pane's surface.
    /// That does not work, because a blur keeps a photograph's EXPOSURE. A lamp, a sky or a white wall
    /// behind a band stays exactly as bright once it is blurred. It is only shapeless. So the pane took
    /// the picture's brightest patch and put it behind a paragraph. On any daylight photograph the
    /// whole sheet went pale grey with white writing on it.</para>
    ///
    /// <para>A third is the lever that fixes this, rather than the veil. Mixing towards a flat colour
    /// takes the RANGE out of the picture, not just its level. The bright patch is carried two thirds
    /// of the way to the pane's own dark, and the dark parts barely move. That is the difference
    /// between a wash and a photograph. What is left is the light and dark of the picture, moving in
    /// the record's colour.</para>
    /// </summary>
    private const double PaneWashStrength = 0.35;

    /// <summary>
    /// Whether this photograph may go behind the sheet. This is a question about the SHEET, which is
    /// why it is asked here and not where the blurred copy is made.
    ///
    /// <para>A pale photograph may not go behind it. A blur keeps a photograph's exposure, so a white
    /// studio backdrop stays exactly as bright once it is shapeless. At a third of a layer, it turns
    /// the whole sheet pale grey with white writing on it. The test uses the picture's MEAN, not its
    /// dominant colour, for the reason set out in PaintPanelTint. A face lit against a dark room is
    /// bright in one and dark in the other. The tone the ink actually lands on is what decides.</para>
    ///
    /// <para>Surfaces that do not write on the wash, such as an artist's page (which wears it at a
    /// tenth as a tint), do not ask this. That is the reason the question lives here.</para>
    /// </summary>
    private static bool WashesWell(PhotoAnswer.Found found)
        => found.Wash is not null
           && (found.Behind is not { } mean || !ArtistPortraits.PrefersBlackInk(mean));

    private ChromeFade? _paneWashFade;
    private ChromeFade PaneWashFade => _paneWashFade ??= new ChromeFade(PaneWash);

    private ChromeFade? _paneGroundFade;

    /// <summary>The flat colour under the wash. Faded rather than written, so the pane's ground joins
    /// the photograph's own layer instead of snapping a beat before it.</summary>
    private ChromeFade PaneGroundFade => _paneGroundFade ??= new ChromeFade(PaneGround);

    private ChromeFade PhotoFadeA => _photoFadeA ??= new ChromeFade(BandPhotoA);
    private ChromeFade PhotoFadeB => _photoFadeB ??= new ChromeFade(BandPhotoB);

    /// <summary>The heading's own height as last measured, exclusive of its margin. Measured rather
    /// than assumed: it is a title over a subtitle strip, and neither is one fixed number.</summary>
    private double _headingRoom;

    private void HeadingBlock_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _headingRoom = e.NewSize.Height;
        ArrangeHero(BandPhotoBox.Visibility == Visibility.Visible);
    }

    /// <summary>The height of the box the pane's title sits in, as <c>RpmPaneHeadingBox</c> sets it for
    /// every pane in the app. Quoted rather than measured: it is a fixed number in the style, and what
    /// it is wanted for here is where the TYPE ends, which is not the same as where the heading block
    /// ends, the reserved subtitle strip below it is air.</summary>
    private const double HeadingBoxHeight = 36;

    /// <summary>
    /// How far down the pane the sheet has to start to clear the heading, measured from the pane's own
    /// top edge.
    ///
    /// <para>The heading's own top MARGIN counts. Leaving it out gives about twelve points of overlap.
    /// The padding is applied to a scroller that begins at the pane's edge, while the size reported for
    /// the heading begins at the heading's own edge. The value is read live, not taken from the last
    /// measurement. The first <c>SizeChanged</c> arrives after the pane has already arranged itself
    /// once. Until it does, <c>_headingRoom</c> is zero, the padding is zero, and the record's title is
    /// drawn straight on top of the word "track info".</para>
    /// </summary>
    private double HeadingRoom
    {
        get
        {
            var measured = _headingRoom > 0 ? _headingRoom : HeadingBlock.ActualHeight;
            return measured > 0 ? measured + HeadingBlock.Margin.Top + HeadingGap : 0;
        }
    }

    /// <summary>
    /// No extra space is added here. The air under the heading is the reserved subtitle strip's own
    /// bottom margin, which is twelve points. Every other pane's first row sits the same twelve points
    /// below its heading. Six more points here would put this pane's content lower than the library's,
    /// the radio's and the playlists'.
    /// </summary>
    private const double HeadingGap = 0;

    /// <summary>What every block in the sheet insets itself by, because the scroller insets nothing.
    /// Matches the Margin on each of them in the markup.</summary>
    private const double BlockInset = 16;

    /// <summary>
    /// How much photograph there is: a 4 by 3 box, three quarters of the width the strip is drawn at.
    ///
    /// <para>This is a RATIO, not a fixed height. The pane can be resized, and a fixed height is one
    /// shape at one width. At 462 points across, which is about the width the pane opens at, a height
    /// of 260 is 16:9. The heading floats over the strip's first sixty-odd points, and
    /// <see cref="PhotoFadeDepth"/> dissolves off its foot. Both of those are flat numbers. So what the
    /// ratio gives is entirely in the middle band: the part that is shown whole grows with the pane,
    /// instead of staying one slice of a changing picture.</para>
    ///
    /// <para>It is measured off <c>ContentScroller</c>, not off <c>BandPhotoBox</c>. The strip runs the
    /// full width of the scroller. It carries no side padding and no scrollbar rail. The scroller is
    /// always arranged, but the box is collapsed whenever there is no photograph and reports a width of
    /// zero. Read off the box, the first open of the strip would compute a height of zero.</para>
    /// </summary>
    private double PhotoStripHeight
    {
        get
        {
            var width = ContentScroller.ActualWidth;
            return double.IsNaN(width) || width <= 0 ? 0 : Math.Round(width * 3 / 4);
        }
    }

    /// <summary>How long the strip takes to open or close. Slow enough to read as the pane making room,
    /// quick enough that a run of records with no photographs between them is not a pane
    /// breathing.</summary>
    private static readonly TimeSpan StripSlide = TimeSpan.FromMilliseconds(320);

    private Storyboard? _stripSlide;

    /// <summary>Whether the strip is meant to be up: the last thing <see cref="SlideStrip"/> was asked
    /// for, not what the box measures this frame. A resize reads it, because a strip on its way DOWN to
    /// nought is still visible and must not be snapped back to full height.</summary>
    private bool _stripOpen;

    /// <summary>
    /// Opens the strip, or closes it, over <see cref="StripSlide"/> rather than in one frame.
    ///
    /// <para>The row must not change height in one frame. If it did, the whole sheet under it would
    /// jump that far down the pane. The direction says something too: down for a record whose artist
    /// has a photograph, and up for one who has none. A movement long enough to be seen is the
    /// difference between the pane making room and the pane glitching.</para>
    ///
    /// <para>The BOX's height is animated, not the row's. A GridLength is not an animatable type, and a
    /// row left on Auto follows the box inside it for nothing.</para>
    /// </summary>
    private void SlideStrip(bool open)
    {
        _stripOpen = open;
        var to = open ? PhotoStripHeight : 0;

        // Visible throughout the close, or there would be nothing left on screen to slide away.
        if (open)
            BandPhotoBox.Visibility = Visibility.Visible;

        var from = BandPhotoBox.ActualHeight;
        if (double.IsNaN(from) || from < 0)
            from = 0;

        if (Math.Abs(from - to) < 0.5)
        {
            BandPhotoBox.Height = to;
            BandPhotoBox.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        _stripSlide?.Stop();

        var slide = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(StripSlide),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            // Height is a layout property, so the animation has to say so out loud or it is dropped.
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(slide, BandPhotoBox);
        Storyboard.SetTargetProperty(slide, "Height");

        var board = new Storyboard();
        board.Children.Add(slide);
        board.Completed += (_, _) =>
        {
            // Taken out of the tree once it has actually gone, not before: a collapsed box mid-slide is
            // the snap this exists to replace.
            if (!open && Math.Abs(BandPhotoBox.Height) < 0.5)
                BandPhotoBox.Visibility = Visibility.Collapsed;
        };
        _stripSlide = board;
        board.Begin();
    }

    private void ArrangeHero(bool hasPhoto)
    {
        SlideStrip(hasPhoto);
        FitHeroToStrip();

        // Over a photograph the sleeve needs an edge, or a dark record on a dark picture has no outline
        // at all. On its own it needs none.
        CoverEdge.BorderThickness = new Thickness(hasPhoto ? 1 : 0);
        CoverEdge.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
    }

    /// <summary>
    /// Places the sheet against however much photograph there is AT THIS INSTANT, rather than against
    /// whether a photograph is coming.
    ///
    /// <para>The two numbers are not set when a picture is announced. The strip is still sliding open
    /// from nothing at that moment. If they were set then, the sheet would start at the very top,
    /// because with a photograph the heading is drawn ON it. The foot would also be pulled up by an
    /// overlap over a picture that is not there yet. The title would go under the floating heading, and
    /// the pane would open with its own name struck through. So the sheet follows the picture that is
    /// there now.</para>
    ///
    /// <para>The room under the heading is what the strip has not covered yet. The overlap is only as
    /// deep as there is picture to overlap. The first line of the sheet therefore sits at the same
    /// height as it does with no photograph, until the picture has grown past it. After that it rides
    /// down with the picture. The top edge stays the same throughout.</para>
    /// </summary>
    private void FitHeroToStrip()
    {
        var strip = BandPhotoBox.Visibility == Visibility.Visible ? BandPhotoBox.ActualHeight : 0;
        if (double.IsNaN(strip) || strip < 0)
            strip = 0;

        ContentScroller.Padding = new Thickness(0, Math.Max(0, HeadingRoom - strip), 0, 24);

        // The foot row rides up over the photo. The name is pushed back down out of the overlap, so it
        // clears the picture whether it runs to one line or three.
        //
        // It also carries the sheet's 16-point side inset. Every other block carries that inset itself.
        // The scroller insets nothing, so that the photograph above can run edge to edge. The foot is
        // the one part of the hero that is words rather than picture.
        var lift = Math.Min(HeroOverlap, strip);
        HeroFoot.Margin = new Thickness(BlockInset, -lift, BlockInset, 0);
        TitleBlock.Margin = new Thickness(0, strip > 0 ? lift + HeroTitleGap : 0, 0, 0);
    }

    /// <summary>
    /// A photograph of the band, from the same source and in the same order as the artwork band behind
    /// the window, so the picture here is the one the app has already settled on for this artist rather
    /// than a second opinion about them.
    ///
    /// <para>The saved order is read first and is usually the whole answer: by the time a track is
    /// playing, the art display has already fetched, ranked and stored the artist's photos, and this
    /// only has to take the first one that has not been hidden. Asking the service is the cold path, a
    /// pane opened on an artist whose photos have never been ranked.</para>
    /// </summary>
    private async Task LoadBandPhotoAsync(PaneBuild build, string? artist, string? album)
    {
        switch (await AskForPhotoAsync(artist, album, () => ReferenceEquals(_build, build)))
        {
            case PhotoAnswer.Gone:
                return;   // a newer question owns the strip; it will report for itself

            case PhotoAnswer.Nobody:
                build.NoPhoto();
                break;

            // The pane is drawn without the strip, as for Nobody. The build's NoPhoto forgets the
            // artist (PaintPhoto clears _photoArtist), so the next record by them asks again.
            case PhotoAnswer.Unfetched:
                build.NoPhoto();
                break;

            // The picture already up is this record's too, the next track by the same artist, which is
            // most changes of track. The layer is still put back by the build: it was faded down with
            // the rest of the pane, and a photograph left standing through a peel would be the one
            // thing on screen belonging to two records at once.
            case PhotoAnswer.Already:
                build.KeepPhoto();
                break;

            case PhotoAnswer.Found found:
                build.GotPhoto(found.Art, found.RunOff, found.Artist, found.Url, found.Tint, found.Behind, found.Wash);
                break;
        }

        Advance(build);
    }

    /// <summary>
    /// The same errand, for the pager rather than for a change of record: the pane is already built and
    /// standing in front of the reader, so this one PAINTS as it lands.
    ///
    /// <para>A separate door rather than a flag on the one above, because the two want opposite things
    /// and the difference is not a detail. A build must not paint, its whole purpose is that nothing
    /// goes up out of turn, while a dot the reader has just pressed has nothing to wait for and must
    /// answer at once. Handed the same answer, they do different things with it.</para>
    /// </summary>
    private async Task SwapBandPhotoAsync(string path, string? artist, string? album)
    {
        switch (await AskForPhotoAsync(artist, album, () => string.Equals(path, _path, StringComparison.Ordinal)))
        {
            case PhotoAnswer.Nobody:
                ClearBandPhoto();
                return;

            // A dot the reader pressed whose photograph could not be fetched: the strip keeps the
            // picture it has rather than being cleared as if this artist had none.
            case PhotoAnswer.Unfetched:
                return;

            case PhotoAnswer.Found found:
                _photoUrl = found.Url;
                ShowBandPhoto(found.Art, found.Artist, found.RunOff);
                PaintPanelTint(found.Tint, found.Behind);
                PaneWashArt.ImageSource = found.Wash;
                PaneWashFade.To(WashesWell(found) ? PaneWashStrength : 0, BandPhotoFadeMs);
                return;

            default:
                return;   // superseded, or the picture already up
        }
    }

    /// <summary>
    /// What asking for an artist's photograph came back with.
    ///
    /// <para>There are four answers rather than a nullable picture. The three that are not a picture
    /// have different meanings. "Nobody has photographed this one" takes the strip down. "It is already
    /// up" leaves the strip exactly where it is. "A newer question owns the strip" must do neither. A
    /// single null would merge them into one answer, and no one behaviour for a null is right for all
    /// three.</para>
    /// </summary>
    private abstract record PhotoAnswer
    {
        /// <summary>Nobody has photographed this credit under any of its names.</summary>
        public sealed record Nobody : PhotoAnswer;

        /// <summary>The photograph is named and could not be fetched. Drawn like <see cref="Nobody"/>
        /// (no strip), and like it forgets the artist, so the next record by them asks again; kept
        /// apart from it so that nothing reads a failed fetch as "nobody has photographed
        /// them".</summary>
        public sealed record Unfetched(string Why) : PhotoAnswer;

        /// <summary>The picture on the strip is already this one.</summary>
        public sealed record Already : PhotoAnswer;

        /// <summary>Superseded: the reader moved on, or the pager asked about somebody else.</summary>
        public sealed record Gone : PhotoAnswer;

        /// <summary>A photograph, and everything drawn from it, decoded and ready to go up in one
        /// synchronous step. <c>RunOff</c> is the photo's aspect when <c>Art</c> is a
        /// <see cref="Services.Art.RunOff"/> canvas (a wide photo with its reflection), and null when
        /// <c>Art</c> is the photo itself.</summary>
        public sealed record Found(
            string Url, string Artist, ImageSource Art, double? RunOff,
            Windows.UI.Color? Tint, Windows.UI.Color? Behind, ImageSource? Wash) : PhotoAnswer;
    }

    /// <summary>
    /// Finds the artist's photograph and everything taken from it. Fetches and decodes; paints nothing.
    /// </summary>
    /// <param name="ours">Whether the answer is still wanted, asked afresh at every await, a fact about
    /// the pane now, never a flag set when the question went out.</param>
    private async Task<PhotoAnswer> AskForPhotoAsync(string? artist, string? album, Func<bool> ours)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return new PhotoAnswer.Nobody();

        if (string.Equals(artist, _photoArtist, StringComparison.OrdinalIgnoreCase))
            return new PhotoAnswer.Already();

        // Who the strip is FOR from this moment. Two of these run for one record. The whole credit is
        // asked about as the tags are read, and the pager asks again for the first name it settles on.
        // The first of those two answers last as often as not. Without an owner, a credit naming two
        // artists (which nobody has photographed) comes back empty a second after the pager has put the
        // first artist up, and takes that artist's photograph down with it.
        _photoWanted = artist;
        bool Mine() => string.Equals(_photoWanted, artist, StringComparison.OrdinalIgnoreCase) && ours();

        // Which photograph this artist gets, shaped for the strip and settled for good; see
        // ChoosePhotoAsync.
        var chosen = await ChoosePhotoAsync(artist, album);
        if (!chosen.Answers(out var url, out var unchosen))
            return Mine() ? new PhotoAnswer.Unfetched(unchosen) : new PhotoAnswer.Gone();

        // Nobody has photographed the credit as it is WRITTEN. It may still name somebody who has been
        // photographed: "Aretha Franklin, The Royal Philharmonic Orchestra" is a pairing no catalogue
        // carries, and a first name every catalogue does.
        //
        // The whole credit is asked first and this is only ever the second guess, which is the order
        // SplitArtists is built for: taken first, the same rule turns "Earth, Wind & Fire" into "Earth"
        // and puts a stranger's photograph over a record by a band the catalogues know perfectly well
        // under the name as typed. Reaching this line is the evidence that they do not know THIS one.
        if (url is null && FirstCredited(artist) is { } lead && Mine())
        {
            if (!(await ChoosePhotoAsync(lead, album)).Answers(out url, out unchosen))
                return Mine() ? new PhotoAnswer.Unfetched(unchosen) : new PhotoAnswer.Gone();
        }

        if (!Mine())
            return new PhotoAnswer.Gone();

        if (url is null)
            return new PhotoAnswer.Nobody();

        // The picture already up, arrived at under a second name for it. Where the fallback above has
        // resolved the one name to the other, both questions have the same answer. Crossing a
        // photograph with itself is not nothing on screen: two half-lit copies of one picture let a
        // quarter of the pane through between them, so the strip dips and comes back for no change.
        if (string.Equals(url, _photoUrl, StringComparison.Ordinal))
        {
            _photoArtist = artist;
            return new PhotoAnswer.Already();
        }

        var fetched = await PictureAsync(url);
        if (!Mine())
            return new PhotoAnswer.Gone();
        if (!fetched.Answers(out var picture, out var why))
            return new PhotoAnswer.Unfetched(why);
        if (picture is null)
            return new PhotoAnswer.Nobody();

        // A wide photo, which the pane only gets for an artist with no square one, is drawn at the
        // strip's width with its reflection under it. A square fills the strip as before. See RunOff.
        var baked = await RunOff.BakeAsync(picture.Thumb, CancellationToken.None);
        if (!Mine())
            return new PhotoAnswer.Gone();

        var bitmap = await RemoteImage.DecodeAsync(baked?.Canvas ?? picture.Thumb);

        // The blurred copy is decoded HERE rather than where it is put up. Everything the layer needs
        // has to be in hand before the layer is due, or the reveal is one more thing arriving on its
        // own clock, which is the fault this whole sequence exists to remove.
        var washed = picture.Wash is { } wash ? await DecodeWashAsync(wash) : null;

        return Mine()
            ? new PhotoAnswer.Found(url, artist, bitmap, baked?.Aspect, picture.Tint,
                                    washed is null ? null : picture.Mean, washed)
            : new PhotoAnswer.Gone();
    }

    /// <summary>The blurred copy as a bitmap, or nothing where it will not decode, in which case the
    /// pane simply stands on its flat colour, which is most of the effect.</summary>
    private static async Task<ImageSource?> DecodeWashAsync(byte[] washed)
    {
        try
        {
            var stream = Presentation.Common.BitmapBytes.Stream(washed);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// One photograph, and the choice of which of an artist's it is: both the shared cache's
    /// (<see cref="ArtistPortraits"/>), because the artists wall is looking at the same picture of the
    /// same person and a second cache here would be a second download of it, and, since the choice is
    /// made once per artist and then kept, a real chance of the two settling on different pictures.
    ///
    /// <para>What the pane still owns is the ink: whether a photograph is dark enough to write a
    /// paragraph over is a question about this pane's type, not about the picture, and the cache is
    /// asked for the blurred copy either way (it makes one only for a picture worth writing on, which
    /// is the same test by the same rule).</para>
    /// </summary>
    private static Task<Asked<ArtistPortraits.Portrait?>> PictureAsync(string url)
        => ArtistPortraits.PictureAsync(url);

    /// <summary>The strip's picture: fanart.tv's square artist thumb where there is one, and the
    /// backdrop the rest of the app wears where there is not. A square fills the strip. A 1920 by 1080
    /// backdrop is drawn at the strip's width with its reflection under it (see RunOff), because
    /// cropped to fill the strip it kept only a band across the middle of the photo.</summary>
    private static Task<Asked<string?>> ChoosePhotoAsync(string artist, string? album)
        => ArtistPortraits.SquareAsync(artist, album);

    // ── the cover ──

    /// <summary>
    /// The sleeve, from wherever this particular record keeps it: the file's own embedded art, then the
    /// album folder's cover image, then, for a record that has been offloaded and has no file at all,
    /// the copy stored in the cloud beside it.
    ///
    /// <para>The same order the artwork band behind the window uses, and for the same reason. Looking
    /// only in the album folder, as this pane first did, is right until the record is not on this disk:
    /// a cloud-only track has no folder to look in, and the pane sat on its placeholder next to a
    /// perfectly good sleeve the app already had. It also misses a local album that carries its art
    /// embedded in the files and never had a cover.jpg written beside them.</para>
    /// </summary>
    private async Task LoadCoverAsync(PaneBuild build, LibraryTrack? row)
    {
        var path = build.Path;
        try
        {
            var bytes = await Task.Run(() => LocalCover(path));

            // No file to read, because there is no file: a record that was offloaded, or one on another
            // computer. The machine holding it is asked first, then the cloud, which keeps a sleeve per
            // album keyed by the artist and album the row carries.
            if (bytes is null && row is not null && !File.Exists(path))
                bytes = await RemoteSleeve.ForAsync(row);

            if (!ReferenceEquals(_build, build))
                return;

            if (bytes is null)
            {
                _sleeveAnswered = true;
                build.NoSleeve();   // an answer: this record has no sleeve here
                Advance(build);
                return;
            }

            // Cut down to the bucket the panes share before it is decoded: an album scan is 3000px, and
            // this is the copy every pane in the app reads, so it is cached at the largest of them.
            var thumb = await Task.Run(() => ThumbCache.Get(bytes, ArtScaler.TileEdge));

            // Then it is decoded at the size THIS square draws it. Left to itself, the bitmap decodes
            // all 640 pixels of the thumb, and the renderer shrinks them at draw time. That holds 1.6
            // MB for a sleeve an inch across, and the picture comes out sampled down rather than
            // resized. See RemoteImage.DecodeAsync(bytes, edge).
            var bitmap = await RemoteImage.DecodeAsync(
                thumb, ArtScaler.DecodeEdgeFor(CoverBox.Width, XamlRoot?.RasterizationScale ?? 1));

            if (!ReferenceEquals(_build, build))
                return;

            _sleeveAnswered = true;
            build.GotSleeve(bitmap);
            Advance(build);
        }
        catch
        {
            _sleeveAnswered = false;
            // Decoration, but the build is still waiting on it: an errand that throws has to say so, or
            // the pane holds the last record's picture for ever on one unreadable file.
            if (ReferenceEquals(_build, build))
            {
                build.NoSleeve();
                Advance(build);
            }
        }
    }

    /// <summary>Whether the sleeve on screen is a settled answer rather than the leftovers of a read
    /// that failed. What decides whether the next track of the same record may keep it.</summary>
    private bool _sleeveAnswered;

    /// <summary>The track's own art off this disk: embedded first, then the album folder's cover image.
    /// Null for a record whose file is not here.</summary>
    private static byte[]? LocalCover(string path)
    {
        try
        {
            if (TagReader.LoadArt(path) is { } embedded)
                return embedded.Bytes;

            return System.IO.Path.GetDirectoryName(path) is { Length: > 0 } directory
                && FolderArtWriter.FindFolderImage(directory) is { } image
                    ? File.ReadAllBytes(image)
                    : null;
        }
        catch
        {
            return null;   // unreadable, or a path with no file behind it at all
        }
    }

    // ── what the encyclopedias add ──

    private async Task LookUpAsync(
        PaneBuild build, string? artist, string? album, string? credited, int tracks, string? conductor = null)
    {
        var path = build.Path;
        _lookup?.Cancel();
        _artistCredit = artist ?? "";
        _trackCredit = credited ?? "";
        _albumName = album;
        ClearDossier();

        // Started now, and waited for by every way this lookup settles, so the section never goes up without
        // the conductor and the conductor never arrives after it. A record with no conductor answers at once.
        var conducted = ConductorFor(conductor);

        async void Settle(TrackDossier? dossier)
        {
            var (who, photo) = await conducted;
            if (!ReferenceEquals(_build, build))
                return;
            build.GotConductor(who, photo);
            build.GotPapers(dossier);
            Advance(build);
        }

        if (string.IsNullOrWhiteSpace(artist))
        {
            Settle(null);   // nothing to ask about: an answer, and the pane moves on it
            return;
        }

        // Already known. The sequence runs whether or not the answer had to be waited for. A pane that
        // assembles itself in one order on a cold record and in another on a warm one is two panes.
        if (Dossiers.Cached(artist, album) is { } known)
        {
            Settle(known);
            return;
        }

        var cancellation = new CancellationTokenSource();
        _lookup = cancellation;

        try
        {
            // A failure settles at null, which draws no papers and says nothing about the record. An
            // empty dossier would be read as "nobody knows this credit" and split it into a row of
            // artists, which is a claim the pane cannot make about a question that got no answer.
            var asked = await Dossiers.GetAsync(artist, album, tracks, cancellation.Token);
            Settle(asked.Answers(out var dossier, out _) ? dossier : null);
        }
        catch (OperationCanceledException)
        {
            // The reader moved on to another record; the sheet already belongs to that one.
        }
        catch
        {
            Settle(null);   // a fault of our own: the same as no answer, drawn as no papers
        }
        finally
        {
            if (ReferenceEquals(_lookup, cancellation))
                _lookup = null;
            cancellation.Dispose();
        }
    }

    /// <summary>Takes the three encyclopedia blocks down while a new record is being asked about. Not
    /// <see cref="ShowDossier"/> with an empty dossier: that one reads the empty answer as "nobody
    /// knows this credit" and opens the pager on it, which would fire a lookup per credited artist on
    /// every change of track and flash a row of dots onto records that turn out not to need
    /// one.</summary>
    private void ClearDossier()
    {
        _artistLookup?.Cancel();
        _artistNames = [];
        _artistAt = 0;
        _artistArticle = null;
        _artistChoice = ArtistChoice.None;
        _albumArticle = null;


        // The record's own dossier goes with the record. It is where the Elsewhere box gets the album's
        // half of its doors (see PaintLinksFor), and the last record's album is not a door on this one.
        _primary = TrackDossier.Empty;
        _artistExpanded = false;
        _albumExpanded = false;

        // Both cards are about to be filled again from scratch, and they should ARRIVE rather than
        // appear. Marked here rather than worked out from the height, because a record whose answer is
        // already cached paints on this very frame, before the emptied cards have been laid out, and
        // measuring then reports the last record's height, which reads as a card that never went away.
        _artistArrives = true;
        _albumArrives = true;

        // Put away without a fade of their own. The pane does that: the peel takes every layer down in
        // one order before anything is asked for (see TrackInfoView.Build). A card fading itself out at
        // the same time would be two hands on one Opacity. Its completion handler would write it back
        // to 1, which would put the block straight back up in the middle of the peel.
        HideCard(ArtistMotion);
        HideCard(AlbumMotion);
        HideCard(LinksMotion);
    }

    /// <summary>Whether the next fill of each card is a new record's rather than a fuller answer about
    /// the one already showing. See <see cref="MoveCard"/>: one rises into place, the other
    /// grows.</summary>
    private bool _artistArrives = true;

    private bool _albumArrives = true;

    private void ShowDossier(TrackDossier dossier)
    {
        _albumArticle = dossier.AlbumArticle;
        _albumExpanded = false;

        PaintAlbumPage(dossier.Album);

        // The whole field is checked first, because the catalogues are the only ones who can settle it.
        // A field holding one of the separators is not proof of two artists. "Simon & Garfunkel",
        // "Earth, Wind & Fire" and "AC/DC" all read as several names to any splitter ever written, and
        // all three are one act with one article. If MusicBrainz and Wikipedia both know the string as
        // it stands, it IS an act. That is the answer, without a list of exceptions to keep.
        //
        // So the pager appears only where the credit is NOT one act, and then it splits. That is
        // exactly the shape of "A feat. B". Nobody has an article about the pair, and both halves have
        // one of their own.
        //
        // "Came back with something" is not the test. A catalogue search is fuzzy by design. Asked
        // about "UB40 featuring Chrissie Hynde", it answers "UB40", because that is the closest thing
        // it has. So an answer arrives for almost every credit ever typed, and the pager would never
        // appear at all. What settles it is whether the answer is the same NAME that was asked about.
        _primary = dossier;
        _artistNames = CreditedArtists(dossier);

        if (_artistNames.Count > 1)
        {
            SelectArtist(0);
            WarmTheRest();
            return;
        }

        _artistNames = [];
        _artistArticle = dossier.ArtistArticle;

        // The record's own artist, asked about WITH the album, which is the key any correction to this
        // card has to be filed under. See ArtistChoice.
        _artistChoice = new ArtistChoice(_artistCredit, _albumName, dossier.Shortlist);
        _artistExpanded = false;
        PaintArtistDots();

        // No side to come in from, nobody asked for this page, it is the record's own, but the card
        // still GROWS into it. This is the answer landing a few seconds after the pane opened, and the
        // fact sheet below it should be nudged down rather than kicked.
        PaintArtistPage(dossier.Artist, 0);
        PaintLinksFor(dossier);
    }

    /// <summary>
    /// The Elsewhere box for whoever is on the artist card, over the record that is playing.
    ///
    /// <para>The album's doors come from the RECORD's own dossier and never change: the record is the
    /// record whichever of its artists is being read about. The artist's doors come from the page in
    /// front of the reader, because a row of links is only useful if it goes where the card says.
    /// Paging to the guest on a feature and then pressing Discogs would open the host's page, which is a
    /// door that lies about where it leads.</para>
    /// </summary>
    private void PaintLinksFor(TrackDossier artistSide)
        => PaintLinks(ReferenceLinks.Merge(
            _primary.Album?.Pages ?? [],
            artistSide.Artist?.Pages ?? []));

    /// <summary>
    /// Everybody this track is credited to, across both fields that can name one.
    ///
    /// <para>The record's artist comes first. That is the act whose record this is, and the one the
    /// pane has already looked up. Then comes whatever the TRACK's own line adds. That second field is
    /// the whole reason this is not a one-field question. A guest appearance is filed as album artist
    /// "Travis Scott" and track artist "Travis Scott, The Weeknd", so the album artist alone never
    /// names the guest. Splitting it can only ever return the one name.</para>
    ///
    /// <para>Each field is split only where the catalogues do not know it whole. That is what keeps
    /// "Simon &amp; Garfunkel" as one card. The track's line is taken as read when it says the same
    /// thing as the record's, which is the ordinary case.</para>
    ///
    /// <para><b>Then the song's own name, which on many records is the only place a guest is named at
    /// all.</b> "Field Trip (ft. Don Toliver, Kodak Black &amp; Playboi Carti)" is filed under album
    /// artist and track artist "Kanye West &amp; Ty Dolla $ign" alike. Neither field has heard of the
    /// three people the title names, so a pager built from the fields alone would offer cards for the
    /// hosts and none for the guests. This comes last, because it is the least authoritative of the
    /// three. A title is prose, and the fields are lists. So a name the tags already carry keeps its
    /// place in the row and is not added again from the song's name.</para>
    /// </summary>
    private IReadOnlyList<string> CreditedArtists(TrackDossier dossier)
    {
        var names = new List<string>(4);

        Add(NamesTheSameAct(dossier, _artistCredit) ? [_artistCredit] : SplitArtists(_artistCredit));

        if (!SameName(_trackCredit, _artistCredit))
            Add(SplitArtists(_trackCredit));

        // Split by the same rule the tags are, rather than on the commas in the phrase: "(feat. Earth,
        // Wind & Fire)" names one act, and the app has exactly one idea about where a name ends.
        foreach (var credit in TitleAsides.Credits(TitleTagged))
            Add(SplitArtists(credit));

        return names;

        void Add(IReadOnlyList<string> found)
        {
            foreach (var name in found)
                if (names.Count < ArtistCredit.MaxCredited && !names.Any(had => Twice(had, name)))
                    names.Add(name);
        }

        // Two dots showing one person. Spelling first, which is what a tag varies; and then the
        // record's own artist under a second of their names, "Kanye West" in the artist field and "Ye"
        // in the song's, which are one act and two strings nothing textual could ever match. The
        // dossier in hand is what settles that: it carries both the catalogue's name for them and their
        // article's title, and a credit matching either IS them. Only for the record's own artist,
        // because theirs is the one dossier this has. The rest are settled when their answers land, see
        // SettleTheRest.
        bool Twice(string had, string name)
            => SameName(had, name)
               || (NamesTheSameAct(dossier, had) && NamesTheSameAct(dossier, name));
    }

    /// <summary>The record's own dossier, kept so that paging back to the artist it was about costs
    /// nothing: it is the one name on the pager the pane has already asked about.</summary>
    private TrackDossier _primary = TrackDossier.Empty;

    /// <summary>
    /// Asks about everybody else on the pager while the reader is still reading the first one.
    ///
    /// <para>A dot has to answer the moment it is pressed, and a cold lookup cannot: MusicBrainz allows
    /// one request a second and an artist costs several, so a card that starts asking when the dot is
    /// pressed sits on a name for a good few seconds. Nobody presses a second dot after that. Asked up
    /// front, the answer is in the service's own cache before the finger arrives and the page is a
    /// repaint.</para>
    ///
    /// <para>Not cancelled when the record changes, and not awaited by anything. These are questions
    /// whose answers are worth having whenever they arrive. The cache is the point of asking. The pane
    /// has already painted whatever it could without them.</para>
    /// </summary>
    private void WarmTheRest()
    {
        var album = _albumName;
        var asked = new List<Task>(_artistNames.Count);

        foreach (var name in _artistNames)
        {
            // Their PICTURE is warmed even for the record's own artist, whose article is already in
            // hand: the strip shows one artist at a time and the others are a page away, so the bytes
            // are worth having before the dot is pressed rather than after.
            _ = WarmPhotoAsync(name, album);

            if (SameName(name, _artistCredit))
                continue;   // the record's own artist; already asked, already in hand

            asked.Add(WarmAsync(name));
        }

        // Once they have all answered, the row can be told whether any two of them are one person. This
        // is not awaited by the caller either. It is still a background task, and the pager has been
        // usable since the dots were painted.
        _ = SettleTheRest(_build, asked);

        static async Task WarmAsync(string name)
        {
            try
            {
                await Dossiers.GetAsync(name, null);
            }
            catch
            {
                // Offline, or the service said no. The dot still works; it simply asks again.
            }
        }

        // The same two steps the loader takes, stopped one short of the screen: which photograph this
        // artist gets, and then everything the pane needs out of it. Both are settled once and kept
        // (see ChoosePhotoAsync and PictureAsync), so the paged load that follows finds the choice made
        // and the pixels ready, and the strip crosses to the next artist in one beat.
        static async Task WarmPhotoAsync(string name, string? album)
        {
            try
            {
                // Warming only: an answer that did not come is simply not warmed.
                if ((await ChoosePhotoAsync(name, album)).Match(found => found, _ => null) is { } url)
                    await PictureAsync(url);
            }
            catch
            {
                // A photograph nobody can fetch is a strip that stays as it is; the page still turns.
            }
        }
    }

    /// <summary>
    /// Collapses the dots that turned out to be one person, once every name on the row has an answer.
    ///
    /// <para><b>Why it cannot be done when the row is built.</b> "Kanye West" and "Ye" are one act and
    /// two strings, and nothing that looks only at the letters will ever say so. Only the catalogue
    /// knows, and asking it is one lookup per name over a rate-limited service. So the row goes up on
    /// the names the tags gave, instantly, which is what the reader needs. It is corrected here once
    /// the answers are in. On a record whose artists are already cached, "once the answers are in" is
    /// the same frame.</para>
    ///
    /// <para><b>Once, not as each one lands.</b> If every answer rewrote the row on its own clock, the
    /// dots would shuffle for several seconds while somebody tried to press one. There is one settle,
    /// after the last answer.</para>
    ///
    /// <para><b>The reader keeps the artist they were on</b>, not the index they were on. The whole
    /// point of the collapse is that the indices move.</para>
    /// </summary>
    private async Task SettleTheRest(PaneBuild? build, IReadOnlyList<Task> asked)
    {
        if (asked.Count > 0)
            await Task.WhenAll(asked).ConfigureAwait(true);

        // The record moved on while the catalogues were answering. Whoever owns the row now built it
        // from their own names, and this is a correction to a row that is no longer on screen.
        if (build is null || !ReferenceEquals(_build, build) || _artistNames.Count < 2)
            return;

        var settled = new List<string>(_artistNames.Count);
        var acts = new List<string>(_artistNames.Count);

        foreach (var name in _artistNames)
        {
            var act = ActOf(name);
            if (acts.Contains(act, StringComparer.Ordinal))
                continue;

            acts.Add(act);
            settled.Add(name);
        }

        if (settled.Count == _artistNames.Count)
            return;   // nobody was on the row twice, which is the ordinary record

        var showing = _artistAt >= 0 && _artistAt < _artistNames.Count ? _artistNames[_artistAt] : null;

        // Down to one act: the row was a pager over one person, so it is not a pager. The card itself
        // is already showing them and is left exactly as it is.
        if (settled.Count < 2)
        {
            _artistNames = [];
            _artistAt = 0;
            PaintArtistDots();
            return;
        }

        _artistNames = settled;

        var stillAt = showing is null ? -1 : settled.FindIndex(name => SameName(name, showing));
        _artistAt = stillAt >= 0 ? stillAt : 0;
        PaintArtistDots();

        // The artist the reader was on was one of the duplicates, and the dot they were standing on is
        // gone. The row's first card is what is left, and it has to be put up, the card on screen is
        // one this row no longer offers.
        if (stillAt < 0)
            SelectArtist(0);
    }

    /// <summary>
    /// Which act a credited name turns out to be, as a comparison key: the article's title where there
    /// is one, else the catalogue's own name for them, else the name as tagged.
    ///
    /// <para><b>Only where the answer actually NAMES the artist asked about.</b> A catalogue search
    /// scores every candidate and hands back its closest guess, so a name it has never heard of comes
    /// back as somebody else entirely, and keyed on that, a real guest would be collapsed into the
    /// host's dot and disappear off the row. An answer that fails the test is not used: the name stands
    /// for itself, which is what every name did before any of this.</para>
    /// </summary>
    private static string ActOf(string name)
        => Dossiers.Cached(name, null) is { } known && NamesTheSameAct(known, name)
            ? NameMatch.Squash(known.ArtistArticle?.Title ?? known.Artist?.Name ?? name)
            : NameMatch.Squash(name);

    // ── the credited artists, one card at a time ──

    /// <summary>What the record's artist field said, whole. The pager's own input: what is split, and
    /// what is asked about first.</summary>
    private string _artistCredit = "";

    /// <summary>And what the TRACK's own artist field said, which on a guest appearance is the only one
    /// of the two that names the guest.</summary>
    private string _trackCredit = "";

    /// <summary>The record's name, kept for the lookups the pager makes after the fact: a photograph is
    /// asked for per artist, and the catalogue finds an artist more easily when it is told which record
    /// of theirs is playing.</summary>
    private string? _albumName;

    /// <summary>Whose photograph the strip is currently for. Set by whichever request is newest; see
    /// LoadBandPhotoAsync, where it is what stops a slower answer about a different name from landing
    /// on top of one already up.</summary>
    private string? _photoWanted;

    /// <summary>The names in that field, once it has been established that it names more than one act.
    /// Empty for the ordinary record by one artist, which is what hides the dots.</summary>
    private IReadOnlyList<string> _artistNames = [];

    /// <summary>Which of them the card is showing.</summary>
    private int _artistAt;

    /// <summary>
    /// What the card in front of the reader could be INSTEAD: the question that produced it, and the
    /// other artists the catalogue offered when that question was asked.
    ///
    /// <para>The question is carried rather than worked out again when the button is pressed, and that
    /// is the whole reason this is a type. The card is a pager, the record's own artist was asked about
    /// with the album, everybody else on the row was asked about alone, so "which key does this card's
    /// answer live under" has two answers depending on which dot is lit, and a correction filed under
    /// the wrong one corrects a card nobody is looking at. It is stated when the card is painted, by
    /// whoever painted it, because that is the only moment it is known for certain.</para>
    /// </summary>
    private readonly record struct ArtistChoice(
        string Name, string? Album, IReadOnlyList<ArtistProfile> Shortlist)
    {
        /// <summary>A card with nothing to choose between: no answer yet, or an artist the RECORD
        /// identified, where there was one candidate and never a list.</summary>
        public static readonly ArtistChoice None = new("", null, []);

        /// <summary>
        /// Whether the card can be asked who else answers to this name. A name is the whole of what it
        /// takes.
        ///
        /// <para>A shortlist is not needed. A shortlist only comes with an artist that was found by
        /// SEARCHING the name, and the cards that took that route are the minority. The ordinary card
        /// is identified through the record the user owns, and it carries no shortlist. Without one,
        /// the card had no way to correct itself. Asking costs one request, and it is made only when
        /// somebody asks. See <see cref="TrackInfoService.ShortlistAsync"/>.</para>
        /// </summary>
        public bool Offered => Name.Length > 0;
    }

    /// <summary>What the artist card on screen could be instead. It travels with
    /// <see cref="_artistArticle"/>, because both are facts about the page in front of the reader
    /// rather than about the record. It is set in each of the four places where the article is
    /// set.</summary>
    private ArtistChoice _artistChoice = ArtistChoice.None;

    /// <summary>Stops a second press while the first is still in the air, exactly as the lyrics' "try
    /// another" does: the button is a press, not a rate.</summary>
    private bool _artistChoosing;

    /// <summary>Cancels the lookup of the artist being paged away from, so a slow answer about the
    /// second name cannot land on the card after the reader has gone back to the first.</summary>
    private CancellationTokenSource? _artistLookup;

    /// <summary>
    /// Puts one of the credited artists on the card.
    ///
    /// <para>Asked about alone, the name and no album. A featured artist did not make this record, so
    /// pairing them with it asks the catalogues a question with no answer; and asking about a name on
    /// its own means one cached answer per artist, reused on every record they turn up on, instead of
    /// one per artist and record.</para>
    /// </summary>
    private async void SelectArtist(int at)
    {
        if (at < 0 || at >= _artistNames.Count)
            return;

        _artistLookup?.Cancel();

        // Which way the page travels: forward for a dot to the right of the one lit, back for one to
        // the left. Worked out before the index moves, because after it there is nothing left to
        // compare to.
        var way = at == _artistAt ? 0 : at > _artistAt ? 1 : -1;

        _artistAt = at;
        _artistExpanded = false;

        // The dot moves on the click, not on the answer. The lookup behind it can take seconds, and a
        // pager that does not move until the network does is a pager people press twice.
        PaintArtistDots();

        var name = _artistNames[at];
        var path = _path;

        // The strip at the top of the pane is this artist's too. A credit that names five people has no
        // photograph of its own, because nobody has photographed the whole group. So the pane would
        // open on no picture at all and stay there. Asked per artist instead, every one of them has a
        // photograph. The photograph changes with the page, which makes the pager read as a pane about
        // a person rather than a card that swaps its text.
        _ = SwapBandPhotoAsync(path, name, _albumName);

        // The record's own artist is already in hand: it is what the pane looked up in the first
        // place. Paging back to them is a repaint rather than a question.
        if (SameName(name, _artistCredit) && _primary.ArtistArticle is not null)
        {
            ShowArtistCard(_primary, new ArtistChoice(_artistCredit, _albumName, _primary.Shortlist), way);
            return;
        }

        if (Dossiers.Cached(name, null) is { } known)
        {
            ShowArtistCard(known, new ArtistChoice(name, null, known.Shortlist), way);
            return;
        }

        // Nothing is known yet: the name alone, so the card is never blank while the answer is on its
        // way. The page still travels, because the reader pressed something and it has to answer. The
        // card grows into the article when it lands, which is the second half of the same movement. The
        // doors go with it: the ones on screen belong to the artist being paged away from.
        _artistArticle = null;
        _artistChoice = ArtistChoice.None;   // nothing has answered yet; there is nothing to choose between
        PaintArtistPage(null, way);
        PaintLinksFor(TrackDossier.Empty);

        var cancellation = new CancellationTokenSource();
        _artistLookup = cancellation;

        try
        {
            // Not answered: the card keeps the name alone, which is what it shows while it waits, and
            // the next time the dot is pressed it asks again.
            if (!(await Dossiers.GetAsync(name, null, ct: cancellation.Token)).Answers(out var dossier, out _))
                return;

            // No slide this time. The page is already the one on screen and the reader has been looking
            // at the name for a second or two; sliding it again would read as a second press.
            //
            // Whose page is up, not which index is lit. The row can be rewritten while this answer is
            // in the air, two of its names turning out to be one person collapses it and moves every
            // index after the join (see SettleTheRest), and an index that still reads the same then
            // points at somebody else, which is this artist's article painted onto their dot.
            if (string.Equals(path, _path, StringComparison.Ordinal) && SameName(CreditedName(), name))
                ShowArtistCard(dossier, new ArtistChoice(name, null, dossier.Shortlist), 0);
        }
        catch (OperationCanceledException)
        {
            // Paged on, or the record changed; whoever owns the card now has already painted it.
        }
        finally
        {
            if (ReferenceEquals(_artistLookup, cancellation))
                _artistLookup = null;
            cancellation.Dispose();
        }
    }

    /// <summary>The artist half of a dossier, on the card. The album half is not touched: the record is
    /// the record whichever of its artists is being read about.</summary>
    /// <param name="choice">Which
    /// question this dossier is the answer to, stated by the caller. Not derived from the dossier: the
    /// answer knows who it is ABOUT and cannot know who was asked after, and it is the asking that a
    /// correction has to be filed under. See <see cref="ArtistChoice"/>.</param>
    private void ShowArtistCard(TrackDossier dossier, ArtistChoice choice, int way)
    {
        _artistArticle = dossier.ArtistArticle;
        _artistChoice = choice;
        _artistExpanded = false;
        PaintArtistPage(dossier.Artist, way);
        PaintLinksFor(dossier);
    }

    /// <summary>
    /// Paints one artist onto the card and MOVES the card to it: the page in from the side it was asked
    /// for, and the card's own height from what it was to what the new page needs.
    ///
    /// <para>Both halves matter and they are one gesture. Without the slide a dot press changes six
    /// lines of text in place, which reads as a glitch rather than as a page turning. Without the
    /// height the card snaps between a two-line artist and a ten-line one, and everything below it, the
    /// album, the links, the whole fact sheet, jumps down the pane at the same instant.</para>
    ///
    /// <para><paramref name="way"/> is +1 for a page from the right, -1 from the left, 0 for one that
    /// simply arrived: a lookup landing on the page already showing, or the card's first fill.</para>
    /// </summary>
    private void PaintArtistPage(ArtistProfile? artist, int way)
    {
        // What the card is NOW, measured before the new words go in. Its own height rather than the
        // border's, since that is what the animation holds, and nought where the record changed, which
        // is what asks for the arrival rather than the growth.
        var was = _artistArrives ? 0 : ArtistPage.ActualHeight;
        _artistArrives = false;

        PaintArtist(artist);
        AnimateArtistPage(way, was);
    }

    /// <summary>The same for the record's own card, which has no pager and therefore no side to come in
    /// from: it either arrives with the record or grows into what the catalogues sent back.</summary>
    private void PaintAlbumPage(AlbumProfile? album)
    {
        var was = _albumArrives ? 0 : AlbumPage.ActualHeight;
        _albumArrives = false;

        PaintAlbum(album);
        AnimateAlbumPage(was);
    }

    private void AnimateArtistPage(int way, double was)
        => MoveCard(ArtistMotion, ArtistBlock.Visibility == Visibility.Visible, way, was);

    /// <summary>The album card's own page, moved for the same three reasons the artist's is: it arrives
    /// on a change of record, it grows into its article when the lookup lands, and it grows again when
    /// somebody presses "Show more".</summary>
    private void AnimateAlbumPage(double was)
        => MoveCard(AlbumMotion, AlbumBlock.Visibility == Visibility.Visible, way: 0, was);

    /// <summary>
    /// Moves one card to whatever is on it now.
    ///
    /// <para>Three movements, and which of them runs is decided here rather than by the caller, because
    /// the caller only knows what it just painted:</para>
    ///
    /// <para>A page ASKED FOR travels in from the side it was asked from, a dot to the right hands the
    /// next artist in from the right, because six lines of text changing in place reads as a glitch
    /// rather than as a page turning.</para>
    ///
    /// <para>A card that was not there a moment ago RISES into place instead: a change of record
    /// empties both cards and fills them again seconds later, and a paragraph appearing out of nothing
    /// at full strength is the pop this exists to remove. There is no side it came from, so it comes
    /// from under.</para>
    ///
    /// <para>And in every case the card's HEIGHT travels from what it was to what the new content
    /// needs, which is the movement everything below it feels: without it the album card, the links and
    /// the whole fact sheet jump down the pane the instant a longer article lands.</para>
    /// </summary>
    private void MoveCard(CardMotion card, bool visible, int way, double was)
    {
        // This movement owns the block from here, which cancels any fade-out still counting down on it.
        card.Turn++;

        // Nothing to travel to: the block is down.
        if (!visible)
        {
            card.Board?.Stop();
            card.Page.Height = double.NaN;
            card.Shift.X = card.Shift.Y = 0;
            card.Page.Opacity = card.Block.Opacity = 1;
            return;
        }

        // Whatever a fade-out left it at. The block carries the leaving, the page carries the arriving,
        // and a block still part-way out is a card that arrives dim.
        card.Block.Opacity = 1;

        // Stopping a storyboard puts its properties back to what they were BEFORE it ran, and what they
        // were before the last one ran is "off to the side, invisible", so the page has to be put back
        // where it belongs by hand, or a dot pressed twice quickly leaves a card nobody can see.
        card.Board?.Stop();
        card.Shift.X = card.Shift.Y = 0;
        card.Page.Opacity = 1;

        var board = new Storyboard();
        var arriving = was <= 0.5;

        if (way != 0 || arriving)
        {
            // Set before the animation as well as animated from, so the first frame is already off its
            // mark: a storyboard's own first tick is a frame away, and that frame is the pop.
            if (way != 0)
                card.Shift.X = way * PageTravel;
            else
                card.Shift.Y = CardRise;

            card.Page.Opacity = 0;

            board.Children.Add(Move(card.Shift, way != 0 ? "X" : "Y",
                                    way != 0 ? way * PageTravel : CardRise, 0, dependent: false));
            board.Children.Add(Move(card.Page, "Opacity", 0, 1, dependent: false));
        }

        // The height is measured off the page's own new content. The height is released to Auto first,
        // because a fixed height left over from the last page is the height Measure would report back.
        //
        // This is only for a card that is GROWING. A card that is arriving has just been shown for the
        // first time. It has never been laid out, so it has no width to measure a paragraph against,
        // and nothing to grow FROM either. Asking anyway made this whole method give up before it
        // started. That is why the answer to a lookup still snapped in while everything else in the
        // pane moved smoothly.
        card.Page.Height = double.NaN;
        var wants = 0.0;

        if (!arriving && card.Page.ActualWidth > 0)
        {
            card.Page.Measure(new Windows.Foundation.Size(card.Page.ActualWidth, double.PositiveInfinity));
            wants = card.Page.DesiredSize.Height;
        }

        if (!arriving && wants > 0 && Math.Abs(was - wants) > 0.5)
        {
            card.Page.Height = was;
            board.Children.Add(Move(card.Page, "Height", was, wants, dependent: true));

            // Back to Auto at the end, always. A card left at the height of one artist's biography
            // would clip the next one, and the pane's own resize would never reach it.
            board.Completed += (_, _) => card.Page.Height = double.NaN;
        }

        if (board.Children.Count == 0)
            return;

        card.Board = board;
        board.Begin();

        static DoubleAnimation Move(DependencyObject target, string property, double from, double to, bool dependent)
        {
            var move = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(PageTravelTime),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                // Height is a layout property. An animation of it has to say so, or the animation is
                // dropped.
                EnableDependentAnimation = dependent,
            };
            Storyboard.SetTarget(move, target);
            Storyboard.SetTargetProperty(move, property);
            return move;
        }
    }

    /// <summary>What one block needs to move: the block itself (heading and card), the page inside it
    /// that slides and grows, the transform that carries the page, and whatever is moving any of it at
    /// the moment.</summary>
    private sealed class CardMotion(FrameworkElement block, FrameworkElement page, TranslateTransform shift)
    {
        /// <summary>Everything the reader sees as one thing. A fade-out is applied to this, not to the
        /// page. A card dissolving under a heading that stays behind, over dots that vanish on their
        /// own beat, is three things leaving at once rather than one.</summary>
        public FrameworkElement Block { get; } = block;

        public FrameworkElement Page { get; } = page;

        public TranslateTransform Shift { get; } = shift;

        public Storyboard? Board { get; set; }

        /// <summary>Which movement owns this block. A fade-out ends by taking the block down. By then
        /// the answer for the NEXT record may already have arrived and been painted, since a cached
        /// answer arrives on the very frame the fade begins. Whoever moved the block last owns it.
        /// Anyone else's completion is a message from a moment that has passed.</summary>
        public int Turn { get; set; }
    }

    private CardMotion? _artistMotion;
    private CardMotion ArtistMotion => _artistMotion ??= new CardMotion(ArtistBlock, ArtistPage, ArtistPageShift);

    private CardMotion? _albumMotion;
    private CardMotion AlbumMotion => _albumMotion ??= new CardMotion(AlbumBlock, AlbumPage, AlbumPageShift);

    /// <summary>
    /// Puts a block away: its motion stops, its page resets, and the block itself collapses.
    ///
    /// <para>The block does not fade itself out here. The pane does that, with every layer going down
    /// in one order before a single question is asked about the new record. Nothing is repainted. The
    /// stale text stays exactly as it was, under a block nobody can see, and whichever movement fills
    /// the block next owns it.</para>
    /// </summary>
    private static void HideCard(CardMotion card)
    {
        card.Board?.Stop();
        card.Turn++;

        card.Page.Height = double.NaN;
        card.Shift.X = card.Shift.Y = 0;
        card.Page.Opacity = 1;
        card.Block.Visibility = Visibility.Collapsed;

        // The block's opacity is deliberately not written. It belongs to the pane's own peel. A card
        // that reset it here would put itself back up halfway through the pane going down.
    }

    /// <summary>The window each card's page moves inside, cut to the window's own size. A geometry
    /// cannot be bound to the size of the thing it clips, so it is rebuilt whenever that size changes.
    /// During a height animation that is every frame, which is exactly the point: the clip is what
    /// turns a card that grows into a longer article into a reveal rather than an overflow.</summary>
    private void CardClip_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement window)
            window.Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height),
            };
    }

    /// <summary>How far off to the side a page starts. Far enough to read as a direction, and short
    /// enough that the words are never off the card long enough to be missed.</summary>
    private const double PageTravel = 26;

    /// <summary>How far under itself a card starts when it has just arrived. This is smaller than the
    /// sideways travel, because this is a card settling into place, not a page being turned.</summary>
    private const double CardRise = 10;

    private static readonly TimeSpan PageTravelTime = TimeSpan.FromMilliseconds(220);

    /// <summary>
    /// The row of dots: one per credited artist, with the current one lit.
    ///
    /// <para>Dots are used rather than a name or an arrow, because the pane is 320 wide and three names
    /// do not fit on a line inside a card that already carries a paragraph. Each dot has the artist's
    /// name as its tooltip, so the reader can find the one they want without paging through the
    /// others.</para>
    /// </summary>
    private void PaintArtistDots()
    {
        ArtistDots.Children.Clear();
        ArtistDots.Visibility = _artistNames.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        if (_artistNames.Count <= 1)
            return;

        for (var i = 0; i < _artistNames.Count; i++)
        {
            var at = i;
            var lit = i == _artistAt;

            var button = new Button
            {
                // A six-point dot inside a target big enough to hit. The dot is the thing seen. The
                // button around it is the thing pressed, and the two are not the same size on purpose.
                Content = new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = Ink("InkStrong"),
                    Opacity = lit ? 1 : 0.3,
                },
                Width = 20,
                Height = 18,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Tag = at,
            };

            // The theme's own pointer state is a grey pill. Around a six-point dot that is a grey pill
            // with a dot in it. The pane's plate is used instead, so the pointer lands on the same
            // frost as every other pressable thing here.
            button.Resources["ButtonBackgroundPointerOver"] = Ink("InkPlateLit");
            button.Resources["ButtonBackgroundPressed"] = Ink("InkPlate");

            ToolTipService.SetToolTip(button, _artistNames[i]);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, _artistNames[i]);
            button.Click += (_, _) => SelectArtist(at);
            ArtistDots.Children.Add(button);
        }
    }

    /// <summary>
    /// Whether the catalogues returned the credit ITSELF, rather than something that happens to be
    /// inside it.
    ///
    /// <para>This is the one thing standing between "Simon &amp; Garfunkel" and two useless cards. Both
    /// the catalogue's artist and the encyclopedia's article are checked. Either one recognising the
    /// whole string settles it. A duo with a Wikipedia page under the name as typed is a duo.</para>
    /// </summary>
    private static bool NamesTheSameAct(TrackDossier dossier, string credit)
        => SameName(dossier.Artist?.Name, credit) || SameName(dossier.ArtistArticle?.Title, credit);

    /// <summary>Whether two names are the same name, allowing for how differently people write one. The
    /// catalogues' own answers are checked the same way; see <see cref="NameMatch"/>.</summary>
    private static bool SameName(string? one, string? other) => NameMatch.Same(one, other);

    /// <summary>The names in one artist credit. See <see cref="ArtistCredit.Split"/>, which the
    /// header's photograph reads too. Splitting is the second guess, never the first. It is only asked
    /// about a credit the catalogues have already failed to recognise as one act. The note in
    /// <see cref="ShowDossier"/> is where that is established for the pane.</summary>
    private static IReadOnlyList<string> SplitArtists(string credit) => ArtistCredit.Split(credit);

    /// <summary>The lead name in a credit that names more than one act, or null where there is no
    /// second name to be had; see <see cref="ArtistCredit.First"/>. What the strip's photograph falls
    /// back to.
    /// </summary>
    private static string? FirstCredited(string credit) => ArtistCredit.First(credit);

    // ══ the two edges the sheet dissolves at ══

    private void ContentScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        => ApplySheetFades();

    /// <summary>The sheet grew or shrank under the scroller. See the note on SheetStack. The change is
    /// queued rather than answered inline, because ScrollableHeight belongs to the layout pass this
    /// size change is part of. Reading it here would read the value from the pass before.</summary>
    private void SheetStack_SizeChanged(object sender, SizeChangedEventArgs e)
        => DispatcherQueue.TryEnqueue(ApplySheetFades);

    /// <summary>
    /// Which of the two bands is up: the head as the sheet climbs the heading, the foot until the end
    /// has been reached.
    ///
    /// <para>The two are not the same kind of thing. The FOOT is a dissolve: the sheet runs off the
    /// bottom edge and fades as it goes. The HEAD is a hole. The sheet has to be gone where the pane's
    /// title is, and what belongs in that space is the pane's own background, which is already there. A
    /// band painted over the top instead was an opaque slab over both the photograph and the wash. The
    /// ground alone is not what the pane looks like. The pane is the ground with a picture over
    /// it.</para>
    ///
    /// <para>The hole is only as wide as the words need. Its band is cut out whole and then painted
    /// back down its own height (see the head's ramp in EnsureSheetFades). It is gone at the top, where
    /// the title stands, and back by the bottom. Its bottom edge stays a hard line, because it is a cut
    /// and not a dissolve, for as far down as the hole goes.</para>
    ///
    /// <para>Both grow out of the scroll rather than switching on. The head grows to the heading's own
    /// depth as the sheet's first line climbs from the foot of the title to the top of it. That is
    /// exactly the distance over which it needs to disappear. The foot grows over its own depth, so
    /// that a sheet nudged a few points does not put a hard wash over its last line.</para>
    /// </summary>
    private void ApplySheetFades()
    {
        if (ContentScroller.Visibility != Visibility.Visible)
        {
            SetSheetFades(0, 0);
            return;
        }

        var above = ContentScroller.VerticalOffset;
        var below = ContentScroller.ScrollableHeight - above;

        // The head band changes its DEPTH, not its opacity. The difference is the whole reason this
        // reads right. A gradient at half strength is still a wash over the same 100 points. A mask at
        // half strength is a veil over the whole band: every word in it goes half transparent,
        // including the ones nowhere near the edge. So a band that is not needed yet is SHORT rather
        // than faint. It grows to its full depth as the sheet is scrolled.
        // ── when the heading's own ground arrives ──
        //
        // Both ends of the ramp are measured off the heading, not chosen. It starts the moment the
        // sheet's first line reaches the bottom of the pane's title, and it is full by the time that
        // line is level with the top of the title.
        //
        // So nothing happens for the first stretch of scroll. The sheet begins one heading below the
        // pane's edge, and the lower part of that is the reserved subtitle strip. That strip is air
        // with nothing printed in it. A reader who has nudged the wheel a few points has not reached
        // the words "track info", and should see no band at all. Starting on the first point would make
        // the pane look as if it slams shut.
        //
        // The ramp is written straight, not eased. It tracks the scroll. A fade that chases the wheel
        // arrives after the words it was meant to sit under.
        var touches = Math.Max(0, HeadingRoom - HeadingBlock.Margin.Top - HeadingBoxHeight);
        var covers = Math.Max(touches + 1, HeadingRoom);
        var head = HeadingRoom * Math.Clamp((above - touches) / (covers - touches), 0, 1);

        SetSheetFades(head, Math.Clamp(below, 0, SheetFadeDepth));
    }

    private void ContentScroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplySheetFades();

        // A 4:3 strip in a pane that can be dragged wider. The height follows the width. See
        // PhotoStripHeight, and FitStripToWidth for why it is not animated.
        if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5)
            FitStripToWidth();
    }

    /// <summary>
    /// Puts the strip back at three quarters of the pane's width, straight to that size. It does not go
    /// through <see cref="StripSlide"/>.
    ///
    /// <para><b>Not animated, and the slide is stopped.</b> A resize is already a movement the reader
    /// is making. A strip that eases after the pointer would lag behind it. Stopping the slide is also
    /// what lets the height take effect at all. An animation holds its end value over a local value, so
    /// a height written while a slide is still running would not be the height on screen.</para>
    ///
    /// <para>There is nothing to do where there is no photograph, or where the strip is on its way down
    /// to zero. <see cref="SlideStrip"/> reads the new width the next time one arrives.</para>
    /// </summary>
    private void FitStripToWidth()
    {
        if (!_stripOpen || BandPhotoBox.Visibility != Visibility.Visible)
            return;

        var wanted = PhotoStripHeight;
        if (wanted <= 0 || Math.Abs(BandPhotoBox.ActualHeight - wanted) < 0.5)
            return;

        _stripSlide?.Stop();
        _stripSlide = null;
        BandPhotoBox.Height = wanted;
    }

    /// <summary>The bands' own depth at full stretch, and the distance either has to be scrolled to
    /// reach it.</summary>
    private const double SheetFadeDepth = 100;

    private CompositionVisualSurface? _sheetHeadCopy;
    private SpriteVisual? _sheetHeadSprite;
    private CompositionVisualSurface? _sheetBottomCopy;
    private SpriteVisual? _sheetBottomSprite;
    private InsetClip? _sheetCut;
    private bool _noSheetFade;

    /// <summary>
    /// Builds the two copies, the two ramps, and the one cut that takes both bands out of the live
    /// sheet.
    ///
    /// <para>This uses the same five pieces as PhotoFootFade, applied twice. The long explanation of
    /// why XAML cannot do this is written there and in PegasusChrome.TopFade. One cut serves both
    /// bands. An InsetClip takes an inset on each side independently, and the sheet needs the same
    /// thing done at its top and its foot.</para>
    /// </summary>
    private void EnsureSheetFades()
    {
        if (_sheetHeadSprite is not null || _noSheetFade)
            return;

        try
        {
            var compositor = ElementCompositionPreview.GetElementVisual(SheetBottomFade).Compositor;
            var sheet = ElementCompositionPreview.GetElementVisual(ContentScroller);

            // The foot's band dissolves DOWNWARDS. The words run off the bottom edge of the pane and
            // fade as they go, so the ramp is vertical and clears at the very foot.
            (_sheetBottomCopy, _sheetBottomSprite) = Band(SheetBottomFade, head: false);

            // The head's band clears DOWNWARDS, on the same axis as the foot's and in the opposite
            // direction. The band is taken out of the live sheet either way. That hole is what the
            // pane's title stands on. This puts the sheet back below the title, where nothing is
            // printed over it.
            (_sheetHeadCopy, _sheetHeadSprite) = Band(SheetHeadFade, head: true);

            _sheetCut = compositor.CreateInsetClip();
            ElementCompositionPreview.GetElementVisual(SheetCut).Clip = _sheetCut;

            (CompositionVisualSurface Copy, SpriteVisual Sprite) Band(UIElement host, bool head)
            {
                var copy = compositor.CreateVisualSurface();
                copy.SourceVisual = sheet;

                // The brush has no alignment of its own. The offset says which band of the sheet is
                // wanted. An alignment ratio would be a second way of saying the same thing, and the
                // two would compound.
                var pixels = compositor.CreateSurfaceBrush(copy);
                pixels.Stretch = CompositionStretch.None;
                pixels.HorizontalAlignmentRatio = 0;
                pixels.VerticalAlignmentRatio = 0;

                // Only the ALPHA of the ramp is read. White is the sheet painted back. Transparent is
                // the band left as a hole, with the pane's own ground showing through it.
                var ramp = compositor.CreateLinearGradientBrush();
                ramp.MappingMode = CompositionMappingMode.Relative;

                if (head)
                {
                    // Straight DOWN, like the foot's. A diagonal ramp across a band that is many times
                    // wider than it is tall is horizontal in every way that shows. The eye saw the
                    // sheet fade out to the RIGHT under an untouched heading, which nobody asked for.
                    //
                    // The band's job is one-dimensional. The sheet has to be gone where the title is,
                    // and present below it. That is a question about height alone, so the ramp asks
                    // about height alone. It still holds the hole over the first third rather than
                    // clearing from the first pixel. The words need clean ground under the whole of
                    // themselves, not a wash that starts thinning behind their second syllable.
                    ramp.StartPoint = new Vector2(0, 0);
                    ramp.EndPoint = new Vector2(0, 1);
                    ramp.ColorStops.Add(compositor.CreateColorGradientStop(0f, Microsoft.UI.Colors.Transparent));
                    ramp.ColorStops.Add(compositor.CreateColorGradientStop(0.34f, Microsoft.UI.Colors.Transparent));
                    ramp.ColorStops.Add(compositor.CreateColorGradientStop(1f, Microsoft.UI.Colors.White));
                }
                else
                {
                    // Straight down, and it runs towards the edge the words are leaving by. There is
                    // nothing at the very foot of the band.
                    ramp.StartPoint = new Vector2(0, 0);
                    ramp.EndPoint = new Vector2(0, 1);
                    ramp.ColorStops.Add(compositor.CreateColorGradientStop(0f, Microsoft.UI.Colors.White));
                    ramp.ColorStops.Add(compositor.CreateColorGradientStop(1f, Microsoft.UI.Colors.Transparent));
                }

                var mask = compositor.CreateMaskBrush();
                mask.Source = pixels;
                mask.Mask = ramp;

                var sprite = compositor.CreateSpriteVisual();
                sprite.Brush = mask;
                ElementCompositionPreview.SetElementChildVisual(host, sprite);
                return (copy, sprite);
            }
        }
        catch
        {
            // No composition is used to do this. The sheet keeps sharp edges, which is what the pane
            // would have without this effect: a plainer pane, not a broken one, and better than a bar
            // across it.
            _noSheetFade = true;
        }
    }

    /// <summary>
    /// Points each copy at its own band of the sheet, and cuts both bands out of the live one.
    ///
    /// <para>A surface renders the visual it was given from that visual's own origin, less whatever
    /// SourceOffset says. So the head's band is asked for at zero, and the foot's band is asked for by
    /// offsetting down by everything above it.</para>
    /// </summary>
    private void SetSheetFades(double top, double bottom)
    {
        EnsureSheetFades();
        if (_sheetCut is null || _sheetBottomCopy is null || _sheetBottomSprite is null
            || _sheetHeadCopy is null || _sheetHeadSprite is null)
            return;

        var width = (float)ContentScroller.ActualWidth;
        var height = (float)ContentScroller.ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        // Neither band may be deeper than the sheet it dissolves, and the two together may not be
        // either. A pane only slightly taller than one band would have its head asking for pixels that
        // its foot had already claimed, and every line in the middle would be masked twice.
        //
        // The values are rounded to whole points. This is the first half of keeping the seam invisible.
        // The scroller's height and its offset are both fractional, because a scroll lands wherever the
        // wheel left it. An ActualHeight under a fractional display scale is rarely a whole number. The
        // cut, the copy and the band's own layout would each round the fraction in their own way. Three
        // roundings of one number make a visible line. Rounded once, here, they all land on the same
        // grid.
        var head = MathF.Round((float)Math.Clamp(top, 0, height));
        var foot = MathF.Round((float)Math.Clamp(bottom, 0, Math.Max(0, height - head)));
        var floor = MathF.Round(height);

        SheetHeadFade.Height = head;
        SheetBottomFade.Height = foot;

        // The head's cut takes the whole band, and its copy is painted back over the same points, so
        // the two meet EXACTLY, with no overlap. The head's ramp is transparent at its left edge, so an
        // overlap there would leave undissolved sheet showing above the line rather than under it. That
        // edge lands on the heading's own bottom line, which is the line every pane starts its content
        // on.
        //
        // The foot uses SeamOverlap, which is zero, so its copy also meets its cut exactly. Whatever
        // rounding is left belongs to the device pixel, which the app does not choose. Rounding every
        // depth to a whole point (above) is what keeps the two edges on the same line.
        _sheetCut.TopInset = head;
        _sheetCut.BottomInset = Math.Max(0, foot - SeamOverlap);

        // A band of zero depth is reached on any record whose facts fit the pane. That is most records,
        // and it is every record before the sheet has been scrolled at all. Such a band must not be
        // handed to the compositor. See BandSurface.
        BandSurface.Fit(_sheetHeadCopy, _sheetHeadSprite, width, head, 0);
        BandSurface.Fit(_sheetBottomCopy, _sheetBottomSprite, width, foot, floor - foot);
    }

    // ══ the pane's colour, and the ink on it ══

    /// <summary>The pane's own dark, for a record with no photograph to take a colour from.</summary>
    private static readonly Windows.UI.Color PaneDefault =
        Windows.UI.Color.FromArgb(0xFF, 0x10, 0x10, 0x14);

    /// <summary>One of the pane's shared brushes, by name: the ink, the plates and the ground. Anything
    /// drawn in code takes these brushes rather than making a colour of its own.
    /// <see cref="PaintPanelTint"/> writes the whole set, and every user of a brush follows.</summary>
    private SolidColorBrush Ink(string key) => (SolidColorBrush)Resources[key];

    /// <summary>
    /// Dresses the whole pane in one colour, and decides whether the writing on it is black or white.
    ///
    /// <para>The colour is dimmed hard before it is used. A dominant tone read off a photograph is the
    /// colour of the biggest THING in it, such as a red jacket or a blue sky, at the strength it was
    /// photographed. A pane painted in that colour cannot be read as a paragraph. Dimmed, it is the
    /// colour of a room lit by that photograph, which is what this is meant to look like.</para>
    ///
    /// <para>The ink is then chosen against what the ground actually ended up being, not against what
    /// the photograph was. A pale yellow dimmed far enough is a dark olive. Ink picked off the yellow
    /// would be black on a dark pane. See <see cref="ReadableOn"/>.</para>
    /// </summary>
    /// <param name="tint">The photograph's dominant colour, which the pane is painted in.</param>
    /// <param name="behind">The tone of the wash where one is up, and null where none is. It is the
    /// mean of the whole picture, because that is what a blurred copy of the picture looks
    /// like.</param>
    private void PaintPanelTint(Windows.UI.Color? tint, Windows.UI.Color? behind)
    {
        // The tint is brought into the app's own ten colours before it is dimmed. It is not used as
        // read. A dominant tone is the colour of the biggest thing in one photograph, which across a
        // library means every hue at every strength. Without this, each record gave the pane a
        // different arbitrary colour, and none of them were RPM's own. Moved most of the way to its
        // nearest relative in the palette, the tint still answers to the picture (a red sleeve gives a
        // red pane), and it is a colour the rest of the app uses too. See CoverColor.Family.
        var ground = tint is { } colour
            ? CoverColor.Dim(CoverColor.Family(colour), PaneDimness)
            : PaneDefault;

        // ── glass, or a colour ──
        //
        // A colour comes from a PHOTOGRAPH, and the photograph is what puts it on this pane. With no
        // photograph (nothing playing, an artist nobody has photographed, or a picture that would not
        // decode) there is no colour for the pane to be. It goes back to being a plain pane:
        // transparent, so the shell's own glass shows through, as it does behind the playlists and the
        // radio.
        //
        // Painting the last artist's colour on regardless was wrong. It made this the one view in the
        // left column with a flat coloured rectangle where the others have glass. On a record whose
        // artist has no picture, the colour was the PREVIOUS artist's, so the pane showed the last
        // record's photograph. That is a statement about the wrong record.
        var plain = tint is null;
        Ink("InkGround").Color = plain || _path.Length == 0
            ? Windows.UI.Color.FromArgb(0, 0, 0, 0)
            : ground;

        // ── which way round the writing goes ──
        //
        // The test is made on what the reader is ACTUALLY looking at, which depends on whether there is
        // a wash. With a wash, that is the photograph itself at nineteen twentieths (see
        // PaneWashStrength), and the ground underneath barely shows. With no wash, the ground IS the
        // surface.
        //
        // Testing the ground either way was the readability fault. A white artist photograph gives a
        // dark ground, a dark ground asks for white ink, and the white ink went on a white picture. The
        // error was not a threshold set a little wrong. It was the wrong surface being tested.
        //
        // The test uses the wash's OWN tone, not the photograph's dominant colour. Those are two
        // different measurements, and they disagree on exactly the pictures that matter. A portrait lit
        // on the face against a dark room has a bright dominant and a dark mean. It earned a wash,
        // which is decided on the mean (see ArtistPortraits). Then black ink was chosen off the face.
        // That gave black writing on a dark room, and the record's own title was unreadable at the top
        // of its own pane.
        //
        // One measure is used for both halves. The half that decides the ink is the one the ink is set
        // on.
        //
        // Pale pictures are kept off the pane altogether, so in practice both branches are dark and the
        // ink is white. The flip stays because the RULE is right, not because of the answer it gives
        // today. If the dimming changes and a lighter ground gets through, the ink follows without
        // anyone having to remember to change it.
        // ── the wash's exposure ──
        //
        // How far the blurred picture must be darkened so that white writing reads on it. This uses the
        // app's own rule, the same one the now playing window uses to veil its backdrop. The value is
        // zero where there is no wash, so a pane standing on its flat colour is not darkened for a
        // picture it does not have. The value is applied inside the wash, so it is the amount at full
        // fade.
        var veil = behind is { } tone ? CoverColor.Veil(tone) : 0;
        PaneVeil.Opacity = veil;

        // ── the shadow the heading stands in ──
        //
        // The shadow uses the photograph's own colour rather than black. Its strength matches the
        // header's scrim: ArtDisplay dims the dominant colour by the same fraction before it paints its
        // ramp. Black is used when there is no picture, which is when the crown is not on screen
        // anyway.
        var shade = tint is { } lamp ? CoverColor.Dim(CoverColor.Family(lamp), CrownDim) : Windows.UI.Color.FromArgb(0xFF, 0, 0, 0);
        foreach (var stop in new[] { CrownStop0, CrownStop1, CrownStop2, CrownStop3 })
            stop.Color = Windows.UI.Color.FromArgb(stop.Color.A, shade.R, shade.G, shade.B);

        // What the reader actually sees: the wash, veiled, then let through over the ground at the
        // wash's own strength. These are the two sums the renderer is about to do. They are done here
        // so that the ink is chosen against the result, not against the photograph.
        var surface = behind is { } lit
            ? Over(Over(Windows.UI.Color.FromArgb(0xFF, 0, 0, 0), lit, veil), ground, PaneWashStrength)
            : ground;

        // What the pane looks like where it meets the neighbouring pane, and which record it belongs
        // to. The neighbour uses this to carry the colour into its own facing corner while it shows the
        // same album (see PaneCorner). This is the composite, not the ground, because the composite is
        // what the reader sees. It is the same answer the ink is chosen against, one line below. A pane
        // standing on glass lends nothing, because there is no colour there to join to.
        PaneCorner.Wearing(plain || _path.Length == 0 ? null : surface, _folder);

        var dark = ReadableOn(surface);
        Ink("InkStrong").Color = Pen(dark, 0xFF);
        Ink("InkBody").Color = Pen(dark, 0xDD);
        Ink("InkQuiet").Color = Pen(dark, 0xAA);
        Ink("InkFaint").Color = Pen(dark, 0x88);

        // Nothing is written for the frost the boxes stand on. That is deliberate: the frost is white
        // in the markup, and it stays white. The app's glass is a LIFT off whatever is behind it, and a
        // lift is white on this pane, as it is in every dialog and on every button in RPM.
        //
        // The frost can be thin. A card needs to be nearly solid only while a bright photograph ran
        // under the sheet. Bright pictures are kept off the sheet entirely (see ArtistPortraits), so
        // whatever is under a card is the record's own dark colour or the shell's own glass. Neither
        // needs protecting from the writing.

        // The sheet's two edges are not given a colour to ramp to. The ground is the average colour of
        // the wash, and the wash is a blurred photograph. A band that ramps to that average does not
        // match the picture and looks like a bar. The edges are faded by taking their pixels away, the
        // same way as the foot of the photograph (see SheetBottomFade in the XAML).

        static Windows.UI.Color Pen(bool dark, byte alpha) => dark
            ? Windows.UI.Color.FromArgb(alpha, 0x0A, 0x0A, 0x0A)
            : Windows.UI.Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF);

        /// <summary>One colour laid over another at <paramref name="alpha"/>. This is the sum the
        /// renderer does, done here so that the ink can be chosen against the result.</summary>
        static Windows.UI.Color Over(Windows.UI.Color top, Windows.UI.Color bottom, double alpha)
            => Windows.UI.Color.FromArgb(
                0xFF,
                (byte)Math.Clamp((top.R * alpha) + (bottom.R * (1 - alpha)), 0, 255),
                (byte)Math.Clamp((top.G * alpha) + (bottom.G * (1 - alpha)), 0, 255),
                (byte)Math.Clamp((top.B * alpha) + (bottom.B * (1 - alpha)), 0, 255));
    }

    /// <summary>How far the photograph's colour is taken down before the pane is painted in
    /// it.</summary>
    private const double PaneDimness = 0.28;

    /// <summary>How far it is taken down for the crown: the same fraction the now playing header dims
    /// its dominant colour by before it paints its scrim (ArtDisplay's ScrimDim). The two shadows in
    /// the app are therefore the same shadow.</summary>
    private const double CrownDim = 0.42;


    /// <summary>Whether BLACK writing reads better than white on this ground. This is the shared rule
    /// (see <see cref="ArtistPortraits.PrefersBlackInk"/>). It also decides whether a photograph is
    /// dark enough to be blurred into a wash at all. There is one sum, so the pane's ink and the
    /// cache's blur cannot reach opposite conclusions about the same picture.</summary>
    private static bool ReadableOn(Windows.UI.Color ground) => ArtistPortraits.PrefersBlackInk(ground);

    /// <summary>
    /// Whether a card has anything on it worth a card.
    ///
    /// <para>A name is not something that is found out. Both these cards are headed by an artist or a
    /// record whose name is already printed at the top of the pane. A card that holds nothing but that
    /// name is a heading, a box and a repetition. On every record the catalogues know nothing about,
    /// the listener would see a blank box. Something must have been learned: a line of facts, a genre,
    /// or the article itself. The lyric block already works this way, which is why an empty one is
    /// never shown.</para>
    /// </summary>
    private static bool WorthACard(string facts, IReadOnlyList<string> tags, EncyclopediaArticle? article)
        => facts.Length > 0 || tags.Count > 0 || !string.IsNullOrWhiteSpace(article?.Extract);

    /// <summary>The artist card counts no genres, because it shows none.</summary>
    private static bool WorthAnArtistCard(string facts, EncyclopediaArticle? article)
        => facts.Length > 0 || !string.IsNullOrWhiteSpace(article?.Extract);

    /// <summary>The name on the card when the catalogues have not supplied one. It is the name the tags
    /// credit, which is the only thing the pane knows about this artist that is still worth
    /// printing.</summary>
    private string CreditedName()
        => _artistAt >= 0 && _artistAt < _artistNames.Count ? _artistNames[_artistAt] : "";

    private void PaintArtist(ArtistProfile? artist)
    {
        var facts = ArtistFactLine(artist);

        // A card the reader is paging through stays up even when this artist turns out to be unknown,
        // and it stays up while the answer is still being fetched. If it were taken away, the dots
        // would go with it. The dots are the way back to the artist the reader was looking at a moment
        // ago. A pager that disappears under the hand is worse than a card with only a name on it.
        var paging = _artistNames.Count > 1;
        var worth = WorthAnArtistCard(facts, _artistArticle) || paging;

        ArtistBlock.Visibility = worth ? Visibility.Visible : Visibility.Collapsed;

        // The line-up is its own block beside this card, not part of it. It is painted whether or not
        // the card is worth showing. If it were painted at the foot of this method, it would sit behind
        // the early return below. An artist with nothing to say (the usual case while a profile that is
        // not cached is still being fetched) would never reach it. The last artist's members would stay
        // on screen, under a heading about a band that is no longer the one shown.
        PaintMembers(artist);

        if (!worth)
            return;

        PaintArtistName(artist?.Name ?? _artistArticle?.Title ?? CreditedName(), artist);

        // The catalogue's fact line (kind, place and years, for example "Person · Place · 1983-2011")
        // is worth having until the encyclopedia's first paragraph appears under it. That paragraph
        // opens by saying who the artist was, where they were from and when they lived. Two versions of
        // one fact, with the terser one on top, read as the pane repeating itself. So the line stands
        // only where there is no article to say it better.
        ArtistFacts.Text = facts;
        ArtistFacts.Visibility = facts.Length > 0 && _artistArticle is null
            ? Visibility.Visible
            : Visibility.Collapsed;

        // The artist card has no chips. See the note in the markup.
        ApplyArticle(_artistArticle, _artistExpanded, ArtistProse, ArtistMoreButton, ArtistSourceButton);

        // Painted AFTER the article and separately from it, because the two say different things. The
        // article's controls say how much of this artist to read. This one says whether it is the right
        // artist at all. An act nobody has written up can be the wrong act just as easily as one who
        // has.
        PaintArtistChoice();
    }

    /// <summary>Whether the card offers a choice of artist. It does so exactly when there is one to
    /// offer. See <see cref="ArtistChoice.Offered"/>.</summary>
    private void PaintArtistChoice()
        => ArtistChooseButton.Visibility = _artistChoice.Offered ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// The acts that answer to this name, for a reader to say which one was meant.
    ///
    /// <para>This is a list, not a "try the next one" button. The lyrics button beside it works that
    /// way. The difference is what can be judged from the screen. Two timed lyric sheets look alike and
    /// can only be told apart by playing them, so the reader steps through them. Artists can be told
    /// apart by the line under each name, at a glance and without pressing anything. "American
    /// metalcore band · Group · Collinsville · 1995-2002" is not the Japanese group of the same name,
    /// and nobody has to try it to find out.</para>
    ///
    /// <para>The artist currently shown is on the list too, and it is marked. A reader who opens this
    /// to check, and finds the card was right all along, should be able to close it knowing that. They
    /// should not have to count the rows against the name above them.</para>
    /// </summary>
    private async void ArtistChoose_Click(object sender, RoutedEventArgs e)
    {
        var choice = _artistChoice;
        if (!choice.Offered || _artistChoosing || sender is not FrameworkElement anchor)
            return;

        // The list is fetched now, if it is not already in hand. That is the usual case, because a card
        // identified through the record carries none. There is one request, sent behind the press that
        // asks for it.
        if (choice.Shortlist.Count == 0)
        {
            var path = _path;
            _artistChoosing = true;
            ArtistChooseButton.IsEnabled = false;
            try
            {
                var asked = await Dossiers.ShortlistAsync(choice.Name, choice.Album);

                // The reader moved on while the request was pending. Nobody is waiting for this list.
                if (!string.Equals(path, _path, StringComparison.Ordinal) || !SameName(CreditedName(), choice.Name))
                    return;

                // Shown as a failure, never as "no other artists". That text would be an answer, and
                // the catalogue gave none.
                if (!asked.Answers(out var found, out _))
                {
                    Announced?.Invoke(Strings.Loc.Get("TrackInfo_ArtistsUnanswered"));
                    return;
                }

                choice = choice with { Shortlist = found };
                _artistChoice = choice;
            }
            catch
            {
                Announced?.Invoke(Strings.Loc.Get("TrackInfo_ArtistsUnanswered"));
                return;   // the card keeps the artist it has
            }
            finally
            {
                _artistChoosing = false;
                ArtistChooseButton.IsEnabled = true;
            }
        }

        // Nobody else answers to this name. The message is shown rather than an empty list, because a
        // button that opens nothing looks the same as a button that does not work.
        if (choice.Shortlist.Count < 2)
        {
            Announced?.Invoke(Strings.Loc.Get("TrackInfo_NoOtherArtists"));
            return;
        }

        var showing = ArtistName.Text;
        var rows = new StackPanel { Spacing = 2 };

        foreach (var candidate in choice.Shortlist)
        {
            if (candidate.Mbid is not { Length: > 0 } mbid)
                continue;   // nothing to ask about. SearchArtistAsync already drops these.

            var lit = SameName(candidate.Name, showing);
            var row = new StackPanel { Spacing = 1 };

            var name = new TextBlock
            {
                Text = candidate.Name,
                FontSize = 12,
                FontWeight = lit ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = Ink(lit ? "InkStrong" : "InkBody"),
                TextWrapping = TextWrapping.Wrap,
            };
            row.Children.Add(name);

            // The line that settles it. It is faint, because it is read second. That is why this is a
            // list and not a next button.
            if (ArtistFactLine(candidate) is { Length: > 0 } facts)
                row.Children.Add(new TextBlock
                {
                    Text = facts,
                    FontSize = 11,
                    Foreground = Ink("InkFaint"),
                    TextWrapping = TextWrapping.Wrap,
                });

            var button = new Button
            {
                Content = row,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 8, 10, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                MinWidth = 0,
                MinHeight = 0,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, candidate.Name);

            var chosen = mbid;
            button.Click += (_, _) =>
            {
                ArtistChooseFlyout.Hide();
                _ = ChooseArtistAsync(choice, chosen);
            };

            rows.Children.Add(button);
        }

        if (rows.Children.Count == 0)
            return;

        ArtistChooseFlyout.Content = new ScrollViewer
        {
            Content = rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            MaxHeight = ChoiceListHeight,
        };
        ArtistChooseFlyout.ShowAt(anchor);
    }

    /// <summary>The maximum height of the artist list before it scrolls. Five rows of two lines each is
    /// every artist a name search offers (see EncyclopediaLookup.MaxArtistsOffered). So in practice it
    /// never scrolls, and it cannot run off the bottom of the window if a name comes back with long
    /// entries.</summary>
    private const double ChoiceListHeight = 320;

    /// <summary>The flyout the list lives in. It is made once and refilled. A flyout rebuilt on every
    /// press would leak the old one's handlers into a control that is still on screen.</summary>
    private readonly Flyout ArtistChooseFlyout = new() { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };

    /// <summary>
    /// Puts the reader's answer on the card, and keeps it.
    ///
    /// <para>The card is repainted from what comes back, not from the row that was pressed. A row in
    /// the list is a search hit with a name and a line of facts. The card needs the genres, the outside
    /// pages and the article. Those are a second question about the artist, asked now that there is no
    /// doubt which artist it is.</para>
    ///
    /// <para>The card is painted only if the reader is still looking at the page they pressed on. A
    /// record can change, and a dot can be pressed while the lookup is in flight. An answer about an
    /// artist that somebody asked about ten seconds ago must never land on whoever is up now. That
    /// would put a WRONG artist on a card that was right, which is the fault this button exists to
    /// repair.</para>
    /// </summary>
    private async Task ChooseArtistAsync(ArtistChoice choice, string mbid)
    {
        if (_artistChoosing)
            return;

        _artistChoosing = true;
        ArtistChooseButton.IsEnabled = false;

        var path = _path;
        try
        {
            var asked = await Dossiers.ChooseArtistAsync(choice.Name, choice.Album, mbid);

            if (!string.Equals(path, _path, StringComparison.Ordinal) || !SameName(CreditedName(), choice.Name))
                return;

            // The card keeps the artist it has. The reader is told the choice did not go through,
            // rather than being left with a press that did nothing.
            if (!asked.Answers(out var dossier, out _))
            {
                Announced?.Invoke(Strings.Loc.Get("TrackInfo_ArtistUnanswered"));
                return;
            }

            // If this was the record's own artist, the record's own dossier goes with it. That way,
            // paging away and back does not bring the old answer out of the field it was kept in.
            if (SameName(choice.Name, _artistCredit) && choice.Album == _albumName)
                _primary = dossier;

            // No side is given to come in from, because nobody paged anywhere. The card grows from the
            // artist it had into the one it should have had. This is the same movement a late-landing
            // article makes.
            ShowArtistCard(dossier, choice with { Shortlist = dossier.Shortlist }, 0);
        }
        catch
        {
            // A fault on our side. The card keeps the artist it has. That is wrong, but it is the wrong
            // the reader was already looking at, and it can be pressed again.
            Announced?.Invoke(Strings.Loc.Get("TrackInfo_ArtistUnanswered"));
        }
        finally
        {
            _artistChoosing = false;
            ArtistChooseButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// The band's line-up, under the card about the band.
    ///
    /// <para>Only for a GROUP with somebody in it. A person has no members, and a band whose entry
    /// nobody has filled in has none either. Otherwise the card would be a heading over a gap in a
    /// database. The line-up goes with the artist card in every sense. It is painted from the same
    /// profile, so paging the credited artists changes the line-up with the name above it. A solo
    /// artist on the next dot simply has no card here.</para>
    ///
    /// <para>A dagger goes beside anyone who has died. It is the same mark, with the same rule, as the
    /// name at the top of the card. It earns its place here, because the years beside a member show the
    /// birth year only, and nothing else on the line would show a death.</para>
    /// </summary>
    private void PaintMembers(ArtistProfile? artist)
    {
        var lineup = artist?.Lineup ?? [];
        var band = artist?.Kind is { Length: > 0 } kind
                   && kind.Equals("Group", StringComparison.OrdinalIgnoreCase);

        MembersList.Children.Clear();
        MembersBlock.Visibility = band && lineup.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (MembersBlock.Visibility != Visibility.Visible)
            return;

        // A face for each, in a row that wraps: four across the pane. The row is painted afresh for every
        // artist, and a photograph that lands after the reader has moved on is dropped (see MemberFace).
        var painting = ++_membersPainted;
        foreach (var member in lineup)
            MembersList.Children.Add(MemberFace(member, painting));
    }

    /// <summary>Counts the line-ups painted, so a photograph fetched for one is not put on the next.</summary>
    private int _membersPainted;

    /// <summary>
    /// One member: a round photograph over their name. Until the photograph comes, and where there is
    /// none, the circle carries their initials. The role, where the catalogue gives one, is the tooltip:
    /// the name is what the row is for, and a second line under each face would double its height.
    /// </summary>
    private StackPanel MemberFace(BandMember member, int painting)
    {
        const double Edge = 64;

        var face = new Grid { Width = Edge, Height = Edge, HorizontalAlignment = HorizontalAlignment.Center };
        face.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Fill = Ink("InkPlateLit") });
        face.Children.Add(new TextBlock
        {
            Text = Initials(member.Name),
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Ink("InkQuiet"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var picture = new ImageBrush { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Top };
        var photo = new Microsoft.UI.Xaml.Shapes.Ellipse { Fill = picture, Opacity = 0 };
        face.Children.Add(photo);

        var name = new TextBlock
        {
            FontSize = 11,
            Foreground = Ink("InkBody"),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        name.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = member.Name });
        if (member.HasDied || member.Died is { Length: > 0 })
            name.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = " †", Foreground = Ink("InkQuiet") });

        var tile = new StackPanel { Width = 76, Spacing = 6 };
        tile.Children.Add(face);
        tile.Children.Add(name);
        if (member.Role is { Length: > 0 } role)
            ToolTipService.SetToolTip(tile, role);

        if (member.Photo is { Length: > 0 } url)
            _ = DressFaceAsync(url, picture, photo, painting);

        return tile;
    }

    /// <summary>The photograph, through the app's picture cache, which identifies the app the way
    /// Wikimedia asks. Shown only if this line-up is still the one on screen.</summary>
    private async Task DressFaceAsync(string url, ImageBrush picture, Microsoft.UI.Xaml.Shapes.Ellipse photo, int painting)
    {
        try
        {
            if ((await RemoteImage.LoadAsync(url)).Answers(out var bitmap, out _) && bitmap is not null
                && painting == _membersPainted)
            {
                picture.ImageSource = bitmap;
                photo.Opacity = 1;
            }
        }
        catch
        {
            // The initials stay. A face that does not come is not a fault worth a word.
        }
    }

    /// <summary>The initials of a name: "Carol Decker" is CD, "Bono" is B.</summary>
    private static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Where(w => char.IsLetter(w[0])).ToList();
        return words.Count switch
        {
            0 => "",
            1 => char.ToUpperInvariant(words[0][0]).ToString(),
            _ => $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[^1][0])}",
        };
    }

    /// <summary>
    /// The artist's name, with a dagger after it where they have died.
    ///
    /// <para>The mark is the one printed beside a name in reference books. It says in one character
    /// what the card otherwise says only in a date range, which the reader has to find and read. The
    /// date range is often not shown at all, because the fact line stands down when there is an article
    /// to say it better. The dagger is quieter than the name, since it is a note about the name and not
    /// part of it. The years themselves are on the name's tooltip for anyone who wants them.</para>
    ///
    /// <para>Only for a PERSON. MusicBrainz records the end of a group in the same field, and a group
    /// that ended has broken up. A dagger next to a band that broke up would be an obituary for living
    /// people.</para>
    /// </summary>
    private void PaintArtistName(string name, ArtistProfile? artist)
    {
        ArtistName.Inlines.Clear();
        ArtistName.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = name });

        var lifespan = artist is null ? "" : Lifespan(artist);
        if (!HasDied(artist))
        {
            ToolTipService.SetToolTip(ArtistName, null);
            return;
        }

        ArtistName.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
        {
            Text = "  †",
            Foreground = Ink("InkQuiet"),
        });

        ToolTipService.SetToolTip(ArtistName, lifespan.Length > 0 ? lifespan : null);
    }

    /// <summary>
    /// Whether this artist has died, as against having stopped.
    ///
    /// <para>A life-span with an end on it means one thing for a person and another for a band.
    /// MusicBrainz keeps both in the same two fields. Only a person can die. Everything else with an
    /// end date has finished.</para>
    /// </summary>
    private static bool HasDied(ArtistProfile? artist)
        => artist is { Kind: { Length: > 0 } kind }
           && kind.Equals("Person", StringComparison.OrdinalIgnoreCase)
           && (artist.HasEnded || artist.Ended is { Length: > 0 });

    private void PaintAlbum(AlbumProfile? album)
    {
        var facts = AlbumFactLine(album);
        var tags = album?.Tags ?? [];
        var worth = WorthACard(facts, tags, _albumArticle);

        AlbumBlock.Visibility = worth ? Visibility.Visible : Visibility.Collapsed;
        if (!worth)
            return;

        AlbumName.Text = TitleCensor.Shown(album?.Title ?? _albumArticle?.Title) ?? "";
        AlbumFacts.Text = facts;
        AlbumFacts.Visibility = facts.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        FillChips(AlbumGenres, tags);
        ApplyArticle(_albumArticle, _albumExpanded, AlbumProse, AlbumMoreButton, AlbumSourceButton);
    }

    /// <summary>"Group · Liverpool · 1960-1970": what kind of act it is, where it is from, and for how
    /// long. Only the parts the catalogue actually knows are shown.</summary>
    private static string ArtistFactLine(ArtistProfile? artist)
    {
        if (artist is null)
            return "";

        var parts = new List<string>(4);
        if (artist.Disambiguation is { Length: > 0 } note) parts.Add(note);
        if (artist.Kind is { Length: > 0 } kind) parts.Add(kind);

        // Where they are from. The place is shown in full where the catalogue has the full name, and as
        // a two-letter code where it has only that. Either is better than nothing. The two are separate
        // fields.
        if ((artist.Area ?? artist.Country) is { Length: > 0 } where)
            parts.Add(where);

        if (Lifespan(artist) is { Length: > 0 } years)
            parts.Add(years);

        return string.Join("  ·  ", parts);
    }

    /// <summary>"1967-", or "1967-1995" once it is over. A hyphen is used here, because user-facing
    /// text in the app does not use en or em dashes.</summary>
    private static string Lifespan(ArtistProfile artist)
    {
        if (artist.Began is not { Length: > 0 } began)
            return artist.Ended is { Length: > 0 } onlyEnd ? "-" + onlyEnd : "";

        return artist.Ended is { Length: > 0 } ended ? $"{began}-{ended}"
             : artist.HasEnded ? began + "-"
             : began;
    }

    /// <summary>"Album · Live · 1977-02-04": what kind of record it is, and when it first came out.
    /// This is the release date people mean, not the date printed on the copy the listener happens to
    /// own.</summary>
    private static string AlbumFactLine(AlbumProfile? album)
    {
        if (album is null)
            return "";

        var parts = new List<string>(4);
        if (album.Disambiguation is { Length: > 0 } note) parts.Add(note);
        if (album.PrimaryType is { Length: > 0 } type) parts.Add(type);
        parts.AddRange(album.Kinds);

        // Only the year is shown, never the full date. A day and a month say which pressing a
        // cataloguer filed first, which is not what a reader means by when an album came out. The line
        // has no label such as "first released", because the other entries on it have none.
        if (ReleaseYear(album.FirstReleased) is { Length: > 0 } year)
            parts.Add(year);

        return string.Join("  ·  ", parts);
    }

    /// <summary>
    /// The year out of a catalogue's release date. The date may be "YYYY-MM-DD", "YYYY-MM" or "YYYY",
    /// depending on how much of the date the cataloguer knew.
    ///
    /// <para>The year is read from the front of the text, not parsed as a date. The field is not always
    /// a whole date, and a partial date is a good answer to the only question asked of it. A value that
    /// does not start with four digits is not a year, so the result is null.</para>
    /// </summary>
    private static string? ReleaseYear(string? released)
        => released is { Length: >= 4 } date && date[..4].All(char.IsAsciiDigit) ? date[..4] : null;

    /// <summary>The lead paragraph, the control that offers the rest, and the credit that names the
    /// source. Wikipedia asks to be named, and a reader should know where the text comes
    /// from.</summary>
    private void ApplyArticle(EncyclopediaArticle? article, bool expanded,
                              TextBlock prose, Button more, Button source)
    {
        if (article is null)
        {
            prose.Visibility = Visibility.Collapsed;
            more.Visibility = Visibility.Collapsed;
            source.Visibility = Visibility.Collapsed;
            return;
        }

        prose.Visibility = Visibility.Visible;
        prose.Text = expanded ? article.Extract : article.Lead;

        more.Visibility = article.HasMore ? Visibility.Visible : Visibility.Collapsed;
        more.Content = Loc.Get(expanded ? "TrackInfo_ShowLess" : "TrackInfo_ShowMore");

        source.Visibility = Visibility.Visible;
        source.Content = Loc.Get("TrackInfo_ReadOnWikipedia");
    }

    private void ArtistMore_Click(object sender, RoutedEventArgs e)
    {
        // Measured before the paragraph changes length, and grown into afterwards. A jump from four
        // lines to twenty is the biggest single move this pane makes, and the reader is looking at it
        // when it happens.
        var was = ArtistPage.ActualHeight;

        _artistExpanded = !_artistExpanded;
        ApplyArticle(_artistArticle, _artistExpanded, ArtistProse, ArtistMoreButton, ArtistSourceButton);
        AnimateArtistPage(0, was);
    }

    private void AlbumMore_Click(object sender, RoutedEventArgs e)
    {
        var was = AlbumPage.ActualHeight;

        _albumExpanded = !_albumExpanded;
        ApplyArticle(_albumArticle, _albumExpanded, AlbumProse, AlbumMoreButton, AlbumSourceButton);
        AnimateAlbumPage(was);
    }

    private void ArtistSource_Click(object sender, RoutedEventArgs e) => Open(_artistArticle?.Url);

    private void AlbumSource_Click(object sender, RoutedEventArgs e) => Open(_albumArticle?.Url);

    // ── chips ──

    /// <summary>
    /// The chips, in the app's OWN genres, not the catalogue's.
    ///
    /// <para>A catalogue shows whatever the people who filed the record typed. "blue-eyed soul", "pop
    /// soul", "neo soul" and "r&amp;b" are four chips for one shelf, and none of the four is a shelf in
    /// this app. Each value goes through <see cref="RPM.Core.Metadata.Genres.Match"/>, which returns
    /// the one genre that the rest of the app files the record under. That is the same word the tag
    /// editor offers and the same word the library groups by. Six chips collapse to two that mean
    /// something.</para>
    ///
    /// <para>A value that will not map is dropped, not shown as typed. A chip that nothing else in the
    /// app knows about is a label the reader cannot act on. It matches no shelf, no filter and nothing
    /// in the picker.</para>
    /// </summary>
    private void FillChips(WrapRow row, IReadOnlyList<string> values)
    {
        var shelved = new List<string>();
        foreach (var value in values)
            // Written in full. This file is in Presentation.Main, where the genres wall's namespace
            // (Main.Genres) is nearer than the Core class with the same name.
            if (RPM.Core.Metadata.Genres.Match(value) is { } genre
                && !shelved.Contains(genre, StringComparer.OrdinalIgnoreCase))
                shelved.Add(genre);
        values = shelved;

        row.Children.Clear();
        row.Visibility = values.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // The pane's plate and the pane's ink are both shared. A chip made of its own two whites would
        // be a white pill with white writing on it, as soon as a pale photograph turns the rest of the
        // pane black.
        foreach (var value in values)
            row.Children.Add(new Border
            {
                Background = Ink("InkPlate"),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(9, 3, 9, 3),
                Child = new TextBlock
                {
                    Text = value,
                    FontSize = 11,
                    Foreground = Ink("InkBody"),
                },
            });
    }

    /// <summary>
    /// The only sites this box offers, in the order they are shown.
    ///
    /// <para>The list is short on purpose. The catalogue knows about every streaming service, every
    /// lyrics site and every unnamed page filed under "other". That is a dozen links to places the
    /// listener already plays this music. What is left is the artist's own website and the places where
    /// the artist speaks for themselves. Those pages are worth leaving the app for, and nothing else in
    /// RPM can show them.</para>
    ///
    /// <para>Wikipedia is left out on purpose, even though it is known. The two cards above this box
    /// already show the encyclopedia's account of the artist and the record. Each card has its own
    /// "read the rest" link to the same page this box would repeat.</para>
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

        // Narrowed, then put in THIS box's order rather than the catalogue's. The order of
        // ElsewhereSites is the order the list is read in. A filter that kept the incoming order would
        // shuffle the links around, depending on which catalogue answered first.
        var shown = new List<ReferenceLink>();
        foreach (var site in ElsewhereSites)
            if (links.FirstOrDefault(l => l.Site == site) is { } found)
                shown.Add(found);

        LinksBlock.Visibility = shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var link in shown)
        {
            // The name, then where it actually goes. A row of names alone looks the same on every line.
            // The address beside the name shows WHICH page it is, for example which Discogs page. It is
            // the one thing that tells a link to the artist from a link to the record.
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

            // The address is held back rather than recoloured. At 60% opacity it stands apart from the
            // name whichever way round the pane's ink currently is. A second colour would have to be
            // chosen twice, once for white writing and once for black. See PaintPanelTint.
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
                Padding = new Thickness(10, 5, 10, 5),
                MinHeight = 0,
                MinWidth = 0,
                CornerRadius = new CornerRadius(6),
                // Stretched, so the links form one column with one left edge, not pills of different
                // widths. The stretched width also gives the address something to trim against.
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                // The pane's ink and the pane's plate. A Button uses the THEME's colours by default,
                // which are white writing on a grey fill (this view is dark). The text inside inherits
                // its colour from the Button, so the Button must carry the pane's ink. Otherwise the
                // box is unreadable on a pale photograph while the cards around it change colour.
                Foreground = Ink("InkStrong"),
                Background = Ink("InkPlate"),
                BorderThickness = new Thickness(0),
                Tag = link.Url,
            };

            // The pointer states come from the theme too. A visual state outranks anything set above,
            // so being under the pointer would put the white back. They are swapped for the pane's own
            // colours, so a row lights up by getting a little more plate, not by changing colour.
            button.Resources["ButtonForegroundPointerOver"] = Ink("InkStrong");
            button.Resources["ButtonForegroundPressed"] = Ink("InkStrong");
            button.Resources["ButtonBackgroundPointerOver"] = Ink("InkPlateLit");
            button.Resources["ButtonBackgroundPressed"] = Ink("InkPlate");

            ToolTipService.SetToolTip(button, link.Url);
            button.Click += (s, _) => Open((s as FrameworkElement)?.Tag as string);
            LinksRow.Children.Add(button);
        }

        // Always arrives: this row is built from nothing each time it is painted, so there is never a
        // height to grow from. It lands on the same frame as the cards and moves with them.
        MoveCard(LinksMotion, LinksBlock.Visibility == Visibility.Visible, way: 0, was: 0);
    }

    private CardMotion? _linksMotion;
    private CardMotion LinksMotion => _linksMotion ??= new CardMotion(LinksBlock, LinksRow, LinksShift);

    /// <summary>The label for a link. Named sites are brands, so they keep their own spelling in every
    /// language. Only "the artist's own site" is a description, so only that one is translated. A link
    /// that is not recognised is labelled with its host, which is the most honest label
    /// available.</summary>
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

    /// <summary>Opens a link from catalogue data, http or https only (see WebLink).</summary>
    private static async void Open(string? url)
    {
        if (RPM.Core.Net.WebLink.Parse(url) is { } link)
            await Windows.System.Launcher.LaunchUriAsync(link.Uri);
    }

    private void OpenAlbum_Click(object sender, RoutedEventArgs e)
    {
        if (_folder.Length > 0)
            AlbumRequested?.Invoke(_folder);
    }
}
