using RPM.Core.Import; // FolderArtWriter: the album folder's own disc art
using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Core.Models;
using RPM.Desktop.Services.Art;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation; // the ride SlideRecordInfo carries the panel up on
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// One record off the wall, opened: its sleeve, what it is, how much of it there is, what it has been
/// given, the three things you can do to it, and everything on it.
///
/// <para>What a press on a cover opens. It is a screen and not a fold under the row (see the note on
/// RecordLayer in the XAML), and it gives the same reading as the phone's album page: sleeve, name,
/// artist, how long, stars, one wide key to play it and two small ones beside that, then the tracks.</para>
///
/// <para>Everything here is read when the record is opened and dropped when it is left. The tracks are one
/// indexed query, the mark and the stars are one row out of the mirror, and the wall's average is a
/// question asked of the network that this screen does not wait for. Nothing is kept between openings: a
/// record over a visualizer is not a place to hold a second copy of the collection.</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>The colours the room is asked to take, or nothing for the room to go back to whatever is
    /// playing. Three because that is what the stage eases between (see IPegasusRoom.SetPalette); each is
    /// nullable because a sleeve the palette reader could not answer for has none of them.</summary>
    public readonly record struct RoomColours(Color? Tint, Color? Glow, Color? Shade);

    /// <summary>
    /// Raised with the colours the room should wear, and again with nothing when this screen is left.
    ///
    /// <para>The HOST acts on it, because the stage is the host's, see PegasusPlayer.OnRoomPalette. Null
    /// deliberately does not mean "go back to the colours you had". It means "ask what is playing NOW",
    /// which is the correct answer after however long somebody spent reading a record. A track that
    /// ended while this screen was up must not leave the room wearing it.</para>
    /// </summary>
    public event Action<RoomColours?>? RoomPalette;

    /// <summary>The record on screen, or nothing.</summary>
    private PegasusAlbumCard? _recordCard;

    /// <summary>Its tracks in play order, resolved once when it was opened, what every press on this
    /// screen plays some part of.</summary>
    private List<TrackInfo> _recordTracks = [];

    /// <summary>The opening that filled the screen. A record whose tracks come back off the disk to find
    /// another record open, or the screen left, drops what it found.</summary>
    private int _recordRead;

    /// <summary>Whether one record is on screen, as against the wall.</summary>
    public bool RecordUp { get; private set; }

    // ── what the window gives it ──

    /// <summary>The sleeve and the words are laid out again on a resize rather than rebuilt: the tracks
    /// under them are the expensive half of this screen and not one of them moves when the window does.
    /// Held so that pass has something to write to.</summary>
    private Border? _recordSleeve;
    private FrameworkElement? _recordSleeveArt;

    /// <summary>What a press on this record's page hands the player: the sleeve it shows, at whatever
    /// sharpness it has got to.</summary>
    private SleeveInHand RecordSleeve() => SleeveInHand.From(_recordSleeveArt as Image, _recordTracks);

    /// <summary>Watched on the LAYER rather than on the scroller. The scroller's width is one of this
    /// method's own answers, so listening to it would only repeat the answer, and each pass would
    /// set the width that woke the next one.</summary>
    private void RecordLayer_SizeChanged(object sender, SizeChangedEventArgs e) => SizeRecord();

    /// <summary>
    /// The page, the gap down the middle of it, the record's panel and the sleeve's edge, all four off
    /// the window, because all four are answers about how much screen there is.
    ///
    /// <para>The page is worked out from the whole window, and the Grid that holds it is centred in the
    /// window. The gap between the two halves is a share of that width with a small cap (see
    /// <see cref="RecordColumnGap"/>), so on a wide screen the record and its running order stand close
    /// together in the middle, and the spare room is at the outside edges.</para>
    ///
    /// <para>Everything after the page is worked out from what the gap leaves, so widening the gap takes
    /// from both halves rather than from the running order alone. The panel has a floor under it because
    /// it is not only the sleeve: the play key and the two beside it stand in it and stop being pressable
    /// long before a picture stops being legible. The sleeve is a fraction of the PANEL rather than of the
    /// window, so it stays a sleeve on the column it sits in. It is capped against the height as well,
    /// because a short wide window would otherwise open on a picture with the record's name below the
    /// fold.</para>
    /// </summary>
    private void SizeRecord()
    {
        // Off the LAYER, not off the scroller. The scroller is one fixed-width column, and asking it
        // how wide the window is would only repeat the answer this method gave it last time.
        var width = RecordLayer.ActualWidth > 0 ? RecordLayer.ActualWidth : Root.ActualWidth;
        var height = RecordLayer.ActualHeight > 0 ? RecordLayer.ActualHeight : Root.ActualHeight;
        if (width <= 1)
            return;

        // What there is to lay out in, and the two halves it would divide into.
        var available = Math.Max(320, width - RecordPageInset * 2);
        var gutter = Math.Clamp(available * RecordGapShare, 40, RecordColumnGap);
        var half = (available - gutter) / 2;

        // The record's panel, and the running order beside it, the second deliberately short of its own
        // half by RecordTrackTrim. A list of titles does not want the room a sleeve does: given half a
        // wide screen it becomes a track number at one end of the line and a length at the other.
        var column = Math.Clamp(half * RecordInfoShare, 300, RecordInfoCap);
        var tracks = Math.Max(RecordTracksFloor, half - RecordTrackTrim);

        // Neither is a share of the window, so the three together may not fit one. The gap gives
        // way first (it is empty space, and the panels are not), then the running order.
        var over = column + gutter + tracks - available;
        if (over > 0)
        {
            var fromGap = Math.Min(over, gutter - 40);
            gutter -= fromGap;
            over -= fromGap;
        }
        if (over > 0)
            tracks = Math.Max(200, tracks - over);

        RecordPage.ColumnSpacing = gutter;
        RecordInfoPanel.Width = column;
        RecordScroll.Width = tracks;

        // How tall either side may get. The page stretches to the room its margins leave, and both boxes
        // are centred in it and capped to it: short of the cap each is as tall as what is in it and sits
        // in the middle, and at the cap the running order fills the room and scrolls. Nothing here
        // positions either one, each is as wide as its own column, and the column is where the Grid puts
        // it.
        RecordTracks.MaxHeight = Math.Max(200, height - RecordTopStrip - RecordBottomAir);

        if (_recordSleeve is null)
            return;

        var edge = Math.Max(160, Math.Min(column, height * 0.36));
        _recordSleeve.Width = edge;
        _recordSleeve.Height = edge;
        if (_recordSleeveArt is not null)
        {
            _recordSleeveArt.Width = edge;
            _recordSleeveArt.Height = edge;
        }

        SizeRecordDisc(edge);
    }

    /// <summary>How much of its own half the record's panel takes. Short of the whole, so the page keeps
    /// some air of its own at the outside edges rather than reaching for them.</summary>
    private const double RecordInfoShare = 0.86;

    /// <summary>The widest the panel goes, however much screen there is. A sleeve, a name and three keys
    /// do not look better past this width; they only get further apart.</summary>
    private const double RecordInfoCap = 640;

    /// <summary>The space left either side of the page. Small: it only matters on a narrow window, where
    /// it stops the two panels touching the edges of the screen.</summary>
    private const double RecordPageInset = 40;

    /// <summary>The most that ever stands between the two panels, and what the gap settles at on an
    /// ordinary desktop screen: about 110 at 1920 wide. Close enough that the record and its running order
    /// read as one page, as on the TV's record page, which uses the same two figures.</summary>
    private const double RecordColumnGap = 120;

    /// <summary>How much of the page the gap takes before it reaches its cap. What keeps a small window
    /// readable: the gap gives way with the screen instead of eating the running order.</summary>
    private const double RecordGapShare = 0.06;

    /// <summary>How much narrower the running order is than the half it would otherwise have taken. A
    /// list of titles does not improve with room the way a sleeve does, past a point the number at one
    /// end and the length at the other stop being one line.</summary>
    private const double RecordTrackTrim = 200;

    /// <summary>Narrow enough to still be a list of songs. Below this a title is mostly ellipsis.</summary>
    private const double RecordTracksFloor = 260;

    /// <summary>Where the record stands once it is out of its sleeve. Worked out with the sleeve's own
    /// size (see SizeRecordDisc) and held, because both the way out and the way back in need it.</summary>
    private double _recordDiscOut;

    /// <summary>Whether the record on screen has come out of its sleeve yet. What lets a resize put it
    /// back where it is rather than where it will end up.</summary>
    private bool _recordDiscSlid;

    /// <summary>How long the record takes to come out of the sleeve, and to go back into it. Long enough
    /// to look like a movement and not a jump, short enough that it is over before somebody who came to
    /// press play has reached for the key.</summary>
    private const int RecordDiscSlideMs = 420;

    /// <summary>
    /// Takes the record out of its sleeve, or puts it back.
    ///
    /// <para>The screen shows the record coming out when it opens, and going back in when it closes.
    /// Closing with the record still out of a sleeve that is taken off the screen underneath it would
    /// look like the cover flashing away.</para>
    ///
    /// <para>A transform, so this is the compositor's alone: the disc's host is not clipped and nothing
    /// around it is measured again while it travels. Going out, it slows to a stop. Going in, it starts
    /// slowly and then speeds up.</para>
    /// </summary>
    private void SlideRecordDisc(bool outward, Action? then = null)
    {
        if (_recordDiscShift is not { } shift || _recordDiscOut <= 0.5)
        {
            then?.Invoke();
            return;
        }

        _recordDiscSlid = outward;

        var from = outward ? 0 : _recordDiscOut;
        var to = outward ? _recordDiscOut : 0;
        shift.X = from;

        var slide = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(RecordDiscSlideMs)),
            EasingFunction = new QuadraticEase
            {
                EasingMode = outward ? EasingMode.EaseOut : EasingMode.EaseIn,
            },
        };

        Storyboard.SetTarget(slide, shift);
        Storyboard.SetTargetProperty(slide, "X");

        var board = new Storyboard();
        board.Children.Add(slide);
        board.Completed += (_, _) =>
        {
            // Written first, stopped after. Stopping a board puts its property back to the value it held
            // BEFORE the board ran, so the value it should end on has to be the local one by then. In the
            // other order one frame shows the record back where it started.
            shift.X = to;
            board.Stop();
            then?.Invoke();
        };
        Services.Shell.Storyboards.Play(board);
    }

    /// <summary>What carries the record up when its panel grows. Made once and left on the panel, because
    /// the panel outlives every record shown in it.</summary>
    private TranslateTransform? _recordInfoSlide;

    /// <summary>How long the record takes to move up. Shorter than the words take to fade in, so the
    /// movement has finished and the page is still by the time there is something to read.</summary>
    private const int RecordSlideMs = 480;

    /// <summary>
    /// Moves the record up into its new middle.
    ///
    /// <para>The panel is centred, so a block landing under the play key makes it taller by that block and
    /// moves its top up by HALF of it. The layout does that in one frame, which would be a sudden jump.
    /// So the panel is put back down by exactly that half the moment it happens, before anything renders,
    /// and moved to zero from there. That is the same movement the layout would have made, spread over
    /// half a second.</para>
    ///
    /// <para>This is a transform, so it is the compositor's work and not the layout's: no measure, no
    /// arrange, no mask surface asked to copy the tracklist again. Animating the block's height instead
    /// would make the screen flicker.</para>
    /// </summary>
    private void SlideRecordInfo(double from)
    {
        if (from <= 0.5)
            return;

        _recordInfoSlide ??= new TranslateTransform();
        RecordInfoPanel.RenderTransform = _recordInfoSlide;
        _recordInfoSlide.Y = from;

        var ride = new DoubleAnimation
        {
            From = from,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(RecordSlideMs)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            // Held rather than stopped. A stopped storyboard puts the property back to what it was before
            // it ran (the offset it started from), and that frame is a jump back down.
        };

        Storyboard.SetTarget(ride, _recordInfoSlide);
        Storyboard.SetTargetProperty(ride, "Y");

        var board = new Storyboard();
        board.Children.Add(ride);
        Services.Shell.Storyboards.Play(board);
    }

    /// <summary>The strip off the top the page stands clear of: 58 of it is the back key's own, and the
    /// rest is empty space. Matches the margin on RecordPage in the XAML, and is subtracted here because
    /// the height this method is capping is measured before that margin is taken off.</summary>
    private const double RecordTopStrip = 108;

    /// <summary>And the space under it, so the page does not sit on the edge of the screen.</summary>
    private const double RecordBottomAir = 44;

    // ── opening one ──

    /// <summary>
    /// Opens the record a cover was pressed on: its tracks and its mark off the collection, the screen
    /// built from them, the wall crossed away, and the room asked to take the sleeve's colours.
    /// </summary>
    private async void ShowRecordAsync(PegasusAlbumCard card)
    {
        NoteRecordOpener();   // where the focus goes back to when the page is left (see PegasusChrome.Remote)
        var read = ++_recordRead;
        var scale = QueueScale;

        // Dropped if another record was opened meanwhile, or if the collection went down under the read:
        // a record screen put up with no collection under it has nothing to go back to.
        if (await ReadRecordAsync(card.Folder) is not { } found || read != _recordRead || !LibraryUp)
            return;

        PutRecordUp(card, found, read, scale);
    }

    /// <summary>
    /// Opens a record named by its folder: what home asks for with the key on a sleeve, a record on its
    /// banner, a record in its quick picks, and "Album page" in a record's or a song's menu. Pegasus's
    /// record screen is what the main window's album page is here.
    ///
    /// <para>The wall's own card when the wall has been read this run, so the sleeve and the room's
    /// colours are the ones already in hand. Otherwise a card is made from the tracks just read (see
    /// <see cref="CardFor"/>), and the record screen reads the sleeve itself.</para>
    /// </summary>
    private async void ShowRecordAsync(string folder)
    {
        NoteRecordOpener();
        var read = ++_recordRead;
        var scale = QueueScale;

        if (await ReadRecordAsync(folder) is not { } found || read != _recordRead || !LibraryUp)
            return;

        // The wall's card only where the wall is of this collection: the same folder name on another one is
        // another record (see WallOffAnotherCollection).
        var card = (WallOffAnotherCollection
                       ? null
                       : _libraryCards.Find(c => string.Equals(c.Folder, folder, StringComparison.OrdinalIgnoreCase)))
                   ?? CardFor(folder, found.Rows);
        PutRecordUp(card, found, read, scale);
    }

    /// <summary>One record's tracks in play order, its stored row, and the root the paths hang
    /// off.</summary>
    private sealed record RecordRead(List<LibraryTrack> Rows, AlbumMeta Meta, string Root);

    /// <summary>Reads a record off the collection, or null where there is no collection, nothing in the
    /// folder, or the read failed.</summary>
    private static async Task<RecordRead?> ReadRecordAsync(string folder)
    {
        var library = Library;
        if (!library.IsOpen || library.ShelfRoot is not { } root)
            return null;

        var db = library.Shelf;

        // Both off the thread that is driving the shader, and in one hop: the tracks and the row the
        // mark and the stars live on.
        var asked = await Task.Run(() => library.Read(db, "a record for Pegasus",
            mirror => (Tracks: mirror.FolderTracks(folder), Meta: mirror.GetAlbumMeta(folder))));

        // Unanswered leaves the screen where it is: a refusal has put the problem screen up over the
        // app, and a closed mirror is a collection that moved under the read.
        if (!asked.Answers(out var read, out _))
            return null;

        // In disc order: a multi-disc record otherwise interleaves its discs. Whatever order comes out of
        // this is the order the presses below play in, so what the third line says is what it does.
        var rows = read.Tracks
            .OrderBy(t => t.DiscNumber)
            .ThenBy(t => t.TrackNumber)
            .ThenBy(t => t.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return rows.Count == 0 ? null : new RecordRead(rows, read.Meta, root);
    }

    /// <summary>
    /// A card for a record opened by folder rather than pressed on the wall. Named the way the wall names
    /// one, off the album tag and else the folder, and taken from the first track because that is what
    /// was just read. It has no picture: the record screen reads the sleeve itself (see
    /// <see cref="ReadSleeveAsync"/>), and the room takes its colours from that read.
    /// </summary>
    private static PegasusAlbumCard CardFor(string folder, List<LibraryTrack> rows)
    {
        var parts = folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var first = rows[0];
        return new PegasusAlbumCard
        {
            Name = first.Album ?? (parts.Length > 0 ? parts[^1] : folder),
            Under = first.AlbumArtist ?? first.Artist ?? (parts.Length >= 2 ? parts[^2] : null),
            Folder = folder,
        };
    }

    /// <summary>Builds the record screen from what was read and crosses to it.</summary>
    private void PutRecordUp(PegasusAlbumCard card, RecordRead found, int read, double scale)
    {
        var rows = found.Rows;
        var meta = found.Meta;
        var root = found.Root;

        _recordCard = card;
        _recordTracks = rows.Select(t => t.ToTrackInfo(root)).ToList();

        BuildRecord(card, rows, meta, scale);
        RecordScroll.ChangeView(null, 0, null, disableAnimation: true);   // a new record starts at its top
        CrossToRecord();
        // With the remote in use, an album's page opens on its first track, as the TV's does: somebody who
        // opens a record has usually come to pick a song on it, and the play key is one Left away.
        FocusScreenLater(_recordFirstTrack ?? _recordPlayKey);

        // The record then comes out of the sleeve. This starts after the cross and not with it: a disc
        // moving while its layer is still fading up would be two movements at once, and neither is clear.
        SlideRecordDisc(outward: true);

        // The room takes the sleeve, and the wall's average is asked for. Neither is waited on: the screen
        // is already up and correct without them, and both land on it when they land.
        PaintRoomFromRecord(card, read);
        ReadAverageAsync(card, rows, read);
    }

    /// <summary>Takes the wall down and puts the record up. <see cref="LibraryUp"/> is left alone on
    /// purpose. It says the COLLECTION is on screen, which is still true, and it is what holds the host
    /// at the bare room and keeps the composition keys quiet (see PegasusPlayer.EffectiveView).</summary>
    private void CrossToRecord()
    {
        if (RecordUp)
            return;
        RecordUp = true;

        StopWheel();   // nothing may still be travelling under a wall that is on its way out

        RecordLayer.Visibility = Visibility.Visible;
        RecordLayer.IsHitTestVisible = true;
        _recordFade.To(1, ScreenCrossMs, null);
        SizeRecord();   // it may never have been laid out at this window's size

        LibraryLayer.IsHitTestVisible = false;
        _libraryFade.To(0, ScreenCrossMs, () =>
        {
            // Collapsed rather than left at nothing: a wall at zero opacity is a wall still being laid
            // out, and its rows are the most expensive layout in the app. Home goes with it, which is
            // what stops its banner stepping behind the record.
            if (RecordUp)
                SetLibraryLayerShown(false);
        });
    }

    /// <summary>
    /// Takes the record off the screen without crossing back to anything, for the COLLECTION going down
    /// under it: the view being left, or the wall being read again for another library.
    ///
    /// <para>Not <see cref="HideRecord"/>, which is a listener stepping back to the wall and therefore
    /// brings the wall back with it. Bringing a wall up inside a screen that is itself leaving would
    /// run two fades on one layer.</para>
    /// </summary>
    private void DropRecord()
    {
        if (!RecordUp)
            return;
        RecordUp = false;

        _recordCard = null;
        _recordRead++;
        _recordOpener = null;
        _recordPlayKey = null;
        _recordFirstTrack = null;
        _recordLastTrack = null;

        RecordLayer.IsHitTestVisible = false;
        RecordLayer.Visibility = Visibility.Collapsed;
        _recordFade.Set(0);
        RecordBody.Children.Clear();
        RecordInfoPanel.Children.Clear();
        _recordSleeve = null;
        _recordSleeveArt = null;
        _recordDisc = null;
        _recordDiscShift = null;
        _recordDiscSlid = false;

        RoomPalette?.Invoke(null);
    }

    /// <summary>The same in the other direction. The wall is re-seated and its place checked, because a
    /// screen that has been collapsed takes its next pass with no usable viewport, which is exactly the
    /// pass that loses a repeater's place. See CheckAnchor.</summary>
    public void HideRecord()
    {
        if (!RecordUp)
            return;
        RecordUp = false;

        _recordCard = null;
        _recordRead++;   // anything still in flight for the record just left has nowhere to land

        RecordLayer.IsHitTestVisible = false;

        // The record goes back into its sleeve as the screen dissolves. If the page were taken away with
        // the record still half out of the sleeve, the cover would seem to flash off. Both move at the
        // same time, so the last thing seen is a closed sleeve fading and not a picture cut off
        // mid-movement.
        SlideRecordDisc(outward: false);

        _recordFade.To(0, RecordDiscSlideMs, () =>
        {
            if (!RecordUp)
            {
                RecordLayer.Visibility = Visibility.Collapsed;
                RecordBody.Children.Clear();   // a screenful of track lines nobody is looking at
                RecordInfoPanel.Children.Clear();
                                        _recordSleeve = null;
                _recordSleeveArt = null;
                _recordDisc = null;
                _recordDiscShift = null;
                _recordDiscSlid = false;
        _recordDiscSlid = false;
            }
        });

        SetLibraryLayerShown(true);
        LibraryLayer.IsHitTestVisible = true;

        // The wall comes up over the same length of time the record goes down in. With the shorter cross
        // it would arrive while the record is still dissolving over it, and a bright wall rising behind
        // a half-faded sleeve looks like a flash.
        _libraryFade.To(1, RecordDiscSlideMs, null);
        CheckAnchorLater();
        UpdateLibraryFade();

        // With the remote in use, the focus goes back to what opened the page.
        FocusScreenLater(_recordOpener);
        _recordOpener = null;
        _recordPlayKey = null;
        _recordFirstTrack = null;
        _recordLastTrack = null;

        // The room goes back to whatever is playing AS IT STANDS NOW, which is not necessarily what was
        // playing when this screen went up. See RoomPalette. An artist's or a genre's page under the record
        // gives the room its own colours back instead (see PegasusChrome.ArtistsGenres).
        RoomPalette?.Invoke(PageRoomInFront);
    }

    private void RecordBack_Click(object sender, RoutedEventArgs e) => HideRecord();

    // ── the room ──

    /// <summary>
    /// Hands the room the sleeve's own colours, so the collection is lit by the record being read rather
    /// than by the one playing behind it.
    ///
    /// <para>The same three colours the header works out for a record being played. One reader is used, so
    /// a record looks the same lit here as it does played (see VinylPressing.PaletteFromArt). Read off
    /// the bytes the wall is already holding for the tile, on the pool, and kept on the card: a listener
    /// stepping in and out of four records must not decode four covers twice.</para>
    /// </summary>
    private void PaintRoomFromRecord(PegasusAlbumCard card, int read)
        => PaintRoomFromRecord(card, card.ResidentBytes, read);

    /// <summary>The same, off bytes that came from somewhere other than the wall: the sleeve the record
    /// screen read for a card the wall never held (see <see cref="CardFor"/> and
    /// <see cref="ReadSleeveAsync"/>).</summary>
    private async void PaintRoomFromRecord(PegasusAlbumCard card, byte[]? cover, int read)
    {
        if (card.Room is not { } held)
        {
            if (cover is not { Length: > 0 } bytes)
                return;   // no picture yet; the room keeps what it has rather than going grey

            try
            {
                var (_, tint, glow, shade) = await Task.Run(() => VinylPressing.PaletteFromArt(bytes));
                held = new RoomColours(tint, glow, shade);
            }
            catch
            {
                return;   // not a picture the reader knows; the room is left as it was
            }
            card.Room = held;
        }

        // Left while it was being read, or another record opened over it.
        if (read != _recordRead || !RecordUp)
            return;

        RoomPalette?.Invoke(held);
    }

    // ── building it ──

    private void BuildRecord(PegasusAlbumCard card, List<LibraryTrack> rows, AlbumMeta meta, double scale)
    {
        RecordBody.Children.Clear();
        RecordInfoPanel.Children.Clear();
        RecordBody.Spacing = 0;
        var read = _recordRead;
        _recordFirstTrack = null;
        _recordLastTrack = null;
        _recordSleeve = null;
        _recordSleeveArt = null;
        _recordDisc = null;
        _recordDiscShift = null;
        _recordDiscSlid = false;

        // ── the record, and the sleeve over it ──
        //
        // The same centrepiece the composition behind this screen is built around: the app's own
        // RecordDisc pushed right until a quarter of it shows past the sleeve, with the sleeve opaque over
        // the rest of it. Not a picture of a record, the same control, the same pressing colour, the same
        // cover on its label. So a record READ looks like a record PLAYED, which keeps this screen
        // consistent with the room behind it.
        //
        // In a frame, like every other cover in the app: a record with no picture shows a plain square
        // and not a hole. What goes in it at once is the best the tile is already holding, so the
        // screen is a record from the frame it opens; the read at the sleeve's own size lands over it.
        var art = new Image { Stretch = Stretch.UniformToFill, Source = card.Sharp ?? card.Cover };
        var sleeve = new Border
        {
            CornerRadius = new CornerRadius(6 * scale),
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x0A, 0x0A, 0x0A)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = art,
        };
        _recordSleeve = sleeve;
        _recordSleeveArt = art;

        _recordDiscShift = new TranslateTransform();
        _recordDiscSlid = false;   // a new record starts in its sleeve, whatever the last one was doing
        _recordDisc = new RecordDisc
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            RenderTransform = _recordDiscShift,
        };
        _recordDisc.Stand.Label.Source = card.Sharp ?? card.Cover;
        PressRecordDisc(card.Tint);

        // ── the two columns ──
        //
        // The record on the left and its running order on the right. With one column, a dozen titles
        // under a picture would push the sleeve, the name, the keys and the first track off the screen on
        // any window shorter than the page. Side by side, a record opens with everything it is and
        // everything on it in view at once.
        //
        // A Grid rather than two stacks, because the two are not independent: the running order takes
        // whatever the record does not, and the record's width is pinned in SizeRecord.
        // The record's own panel, which lives beside the scroller rather than in it (see the XAML): what
        // goes in it never scrolls, and what goes in RecordBody below is the running order alone.
        var info = RecordInfoPanel;

        // The sleeve AFTER the disc, so it draws over it. The host is not clipped, which is what lets the
        // quarter that is out of the sleeve overhang into the gap between the columns.
        var centre = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        centre.Children.Add(_recordDisc);
        centre.Children.Add(sleeve);
        info.Children.Add(centre);

        SizeRecord();
        ReadSleeveAsync(card, art, _recordRead);
        ReadRecordDiscAsync(card, _recordRead);

        // ── what it is ──
        info.Children.Add(new TextBlock
        {
            Text = card.Name,
            FontSize = 24 * scale,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 22 * scale, 0, 0),
        });

        if (!string.IsNullOrWhiteSpace(card.Under))
            info.Children.Add(new TextBlock
            {
                Text = card.Under,
                FontSize = RecordPageText * scale,
                Foreground = new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 4 * scale, 0, 0),
            });

        // ── when, and how much record there is ──
        //
        // The year, the count and the length on one line, because together they say what this is in time
        // and size. The year leads, as on the TV's record page. It is the year most of the tracks give, so
        // one bonus track from a later reissue does not change it, and a record whose files give no year
        // starts at the count. A middle dot and not a dash, which is the app's rule for user-facing text.
        var amount = string.Format(Strings.Loc.Get("Rpm_Album_Tracks"), rows.Count)
                   + "  ·  " + Duration(rows.Sum(t => t.DurationSeconds));
        info.Children.Add(new TextBlock
        {
            Text = RecordYear(rows) is { } year ? $"{year}  ·  {amount}" : amount,
            FontSize = RecordPageText * scale,
            Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 12 * scale, 0, 0),
        });

        // ── what it is made of ──
        //
        // "FLAC  ·  16/44.1  ·  5.1", from the first track's own row, with whichever of those the file
        // knows: the drawer's album screen gives the same line from the same describer (see
        // PegasusChrome.Album). One file speaks for the record because one release uses one encoding.
        var first = rows[0];
        var format = RPM.Core.Metadata.AudioFormat.Describe(
            first.Extension, first.Channels, first.CodecDescription,
            first.BitsPerSample, first.SampleRate, first.BitrateKbps);
        if (format.Count > 0)
            info.Children.Add(new TextBlock
            {
                Text = string.Join("  ·  ", format),
                FontSize = RecordPageText * scale,
                FontStyle = Windows.UI.Text.FontStyle.Italic,
                Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6 * scale, 0, 0),
            });

        // ── the stars ──
        //
        // The wall's average on every record, and the listener's own stars only on their own collection.
        // Stars are something the listener gives, and nothing is given to a borrowed record from this page
        // (see RecordBorrowed); what everybody thinks of it is still worth reading. What the last record
        // built for them is let go, so the wall's average cannot land on a control from another page.
        var borrowed = RecordBorrowed;
        _recordAverageRow = null;
        _recordAverageStars = null;
        _recordAverageText = null;
        _recordStars = null;
        info.Children.Add(BuildRecordStars(card, meta, borrowed, scale));

        // ── the things you can do to it ──
        info.Children.Add(BuildRecordKeys(card, meta, borrowed, scale));

        // ── and what is said about it ──
        //
        // UNDER the keys, which is where the waiting is shown as well: the ring that stands in for these
        // words while the catalogues are being asked has to be where the reader is already looking, and
        // words that faded in above the play key would appear where nobody is watching. The two share
        // one place so the panel grows downwards once and does not shift twice.
        var about = AboutSlot(scale);
        info.Children.Add(about);
        ShowAboutAsync(about, card.Under, card.Name, rows.Count, () => read == _recordRead, scale,
                       grew => SlideRecordInfo(grew / 2), couch: true);

        // ── and everything on it ──
        //
        // The right-hand column, level with the top of the sleeve: the running order is beside the
        // record, not under it.
        //
        // Spacing between the lines as well as padding inside them, and the two are not the same thing:
        // the padding is what a line's own hover wash covers, and the spacing is the gap between one
        // line's wash and the next. Without the gap, a list under the pointer looks like one long block
        // lighting up in sections.
        var list = RecordBody;
        list.Spacing = 4 * scale;

        // A box set says where each disc starts, the same way the library's own album grid does it: the
        // disc's caption over a hairline. Only where there is more than one, "CD 1" over the whole of a
        // single-disc record is a heading that says nothing.
        var discs = rows.Select(DiscOf).Distinct().Count();
        uint? open = null;

        // A classical record is listed as works with their movements, as the main window's album page and
        // the TV's record page list it (see ClassicalLines). Every other record is a plain list.
        var classical = meta.Classical == true || AlbumTracklist.ClassicalByGenre(rows, t => t.Genre)
            ? ClassicalLines(rows)
            : null;

        var titles = new List<TextBlock>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var disc = DiscOf(rows[i]);
            if (discs > 1 && disc != open)
            {
                open = disc;
                list.Children.Add(BuildDiscHeader(DiscCaption(rows, disc), scale, 16));   // the TV's 16
            }

            // A work's heading over its first movement, under the disc's caption where both open here.
            if (classical?[i].Opens is { } work)
                list.Children.Add(BuildWorkHeader(work, scale));

            // The index is the track's place in the RECORD, not its row in this column: a divider takes a
            // row and plays nothing, and the number handed over here is what a press on the line plays.
            var line = RecordTrack(rows[i], _recordTracks, i, scale, titles, inList: false, classical?[i]);
            if (i == 0)
                _recordFirstTrack = line as Control;
            list.Children.Add(line);
        }

        // The names are capped off the column they stand in, for the reason BuildBand's are: the name and
        // its E sit in one horizontal stack, and TextTrimming does not engage inside one.
        list.SizeChanged += (_, e) => CapRecordTitles(titles, e.NewSize.Width, scale);
    }

    // ── a classical record ──

    /// <summary>A work on a classical record, as its heading says it: its name, the composer where its
    /// movements agree on one, and how long the whole work runs.</summary>
    private sealed record RecordWork(string Title, string? Composer, double Seconds);

    /// <summary>How one track line of a classical record reads.</summary>
    /// <param name="Title">What the line says instead of the file's title, or null for the title itself.
    /// A movement says the movement alone, under the heading that names its work.</param>
    /// <param name="Subtitle">The small line under the title: the composer, on a piece that is a work on
    /// its own.</param>
    /// <param name="Movement">The track is a movement of the work above it, and is set in.</param>
    /// <param name="Piece">The track is a whole piece (a work of one track, or no work), and is set in the
    /// weight of a heading.</param>
    /// <param name="Opens">The work whose heading stands over this line, or null.</param>
    private sealed record RecordLine(string? Title, string? Subtitle, bool Movement, bool Piece, RecordWork? Opens);

    /// <summary>
    /// A classical record's lines, one per track and in the same order: works with their movements under
    /// them, and the pieces that are a work on their own.
    ///
    /// <para>The same reading as the main window's album page (AlbumTrackItem.Build) and the TV's record
    /// page. A run of consecutive tracks of one work (AlbumTracklist.ByWork), within one disc, gets a
    /// heading when it has more than one track, and its tracks are movements, named by
    /// AlbumTracklist.MovementOf. A work of one track needs no heading, because its line already gives
    /// the whole name. It is a piece, like a track of no work: drawn at the level of a heading and in its
    /// weight, with its composer under it, because it is another piece on the record and not part of the
    /// one above it.</para>
    ///
    /// <para>One line per track, so the line at index i is still the track the record plays at i. The
    /// headings are drawn between the lines and are no stops for the remote.</para>
    /// </summary>
    private static RecordLine[] ClassicalLines(List<LibraryTrack> rows)
    {
        var lines = new RecordLine[rows.Count];
        var at = 0;
        while (at < rows.Count)
        {
            // One disc at a time: a work does not run on into the next disc of a box.
            var disc = DiscOf(rows[at]);
            var end = at;
            while (end < rows.Count && DiscOf(rows[end]) == disc)
                end++;

            foreach (var (work, group) in AlbumTracklist.ByWork(rows.GetRange(at, end - at), t => t.Work))
            {
                if (work is not null && group.Count > 1)
                {
                    // The composer of the work where its movements agree on one. They may not, on a
                    // pasticcio or a badly tagged box, and printing the first one for all of them would name
                    // a composer nobody gave.
                    var composers = group.Select(t => t.Composer)
                                         .Distinct(StringComparer.CurrentCultureIgnoreCase)
                                         .ToList();
                    var heading = new RecordWork(
                        work,
                        composers is [{ } only] && !string.IsNullOrWhiteSpace(only) ? only : null,
                        group.Sum(t => t.DurationSeconds));

                    for (var k = 0; k < group.Count; k++)
                        lines[at + k] = new RecordLine(
                            AlbumTracklist.MovementOf(group[k].MovementName, group[k].Title, work),
                            Subtitle: null, Movement: true, Piece: false, Opens: k == 0 ? heading : null);
                }
                else
                {
                    for (var k = 0; k < group.Count; k++)
                        lines[at + k] = new RecordLine(
                            Title: null,
                            Subtitle: string.IsNullOrWhiteSpace(group[k].Composer) ? null : group[k].Composer,
                            Movement: false, Piece: true, Opens: null);
                }

                at += group.Count;
            }
        }

        return lines;
    }

    /// <summary>
    /// The line that opens a work: its name, up to two lines, the composer under it, and the whole work's
    /// length on the right, lined up with the track titles and lengths below it. Nothing plays from it,
    /// and it takes no focus, so the remote passes over it from one movement to the next.
    /// </summary>
    private static Border BuildWorkHeader(RecordWork work, double scale)
    {
        var grid = new Grid { ColumnSpacing = 12 * scale };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var words = new StackPanel { Spacing = 2 * scale, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock
        {
            Text = work.Title,
            FontSize = RecordPageText * scale,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (work.Composer is { } composer)
            words.Children.Add(new TextBlock
            {
                Text = composer,
                FontSize = 16 * scale,
                Opacity = 0.5,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        grid.Children.Add(words);

        if (work.Seconds > 0)
        {
            var length = new TextBlock
            {
                Text = Duration(work.Seconds),
                FontSize = RecordPageText * scale,
                Opacity = 0.5,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(length, 1);
            grid.Children.Add(length);
        }

        // Its words start where a track's title starts: the line's padding, the number's column and the
        // gap after it (see RecordTrack). Its length ends where a track's length ends.
        return new Border
        {
            Child = grid,
            Padding = new Thickness((12 + 38 + 12) * scale, 14 * scale, 12 * scale, 2 * scale),
        };
    }

    /// <summary>The year most of a record's tracks give, or null when none gives one. The commonest year
    /// and not the first track's, so a reissue's one later bonus track does not date the record.</summary>
    private static uint? RecordYear(List<LibraryTrack> rows) =>
        rows.Where(t => t.Year > 0)
            .GroupBy(t => t.Year)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .Select(g => (uint?)g.Key)
            .FirstOrDefault();

    /// <summary>
    /// What the record has been given: the wall's average, and the listener's own.
    ///
    /// <para>Both, in that order, because they answer different questions: what everybody thinks of
    /// this record, and what YOU think of it. The average is a question asked of the network and is not
    /// on the screen until it answers; a record nobody has rated, or a machine with no network, simply
    /// does not show that half. The listener's own rating comes from the mirror beside it, is there at
    /// once, and is the one that can be pressed.</para>
    ///
    /// <para>On a <paramref name="borrowed"/> record only the average is built: the listener gives nothing
    /// to somebody else's record from this page (see <see cref="RecordBorrowed"/>).</para>
    /// </summary>
    private StackPanel BuildRecordStars(PegasusAlbumCard card, AlbumMeta meta, bool borrowed, double scale)
    {
        var block = new StackPanel
        {
            Spacing = 6 * scale,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16 * scale, 0, 0),
        };

        _recordAverageRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8 * scale,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _recordAverageStars = new StarRating { GlyphSize = 17 * scale };
        _recordAverageText = new TextBlock
        {
            FontSize = 16 * scale,
            Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _recordAverageRow.Children.Add(_recordAverageStars);
        _recordAverageRow.Children.Add(_recordAverageText);
        block.Children.Add(_recordAverageRow);

        if (borrowed)
            return block;

        var mine = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9 * scale,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        mine.Children.Add(new TextBlock
        {
            Text = Strings.Loc.Get("Album_YourRating"),
            FontSize = RecordPageText * scale,
            Foreground = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
        });

        // The mirror keeps whole stars; the control works in halves, and the wall's own ratings are in
        // halves too. Shown as it is stored and rounded on the way back in, see RecordRating_Picked.
        // A tab stop here and nowhere else, so the remote can stand on it and turn it with Left and Right
        // (see PegasusChrome.Adjust). The control's own default keeps it out of the main window's tab
        // order.
        var stars = new StarRating
        {
            GlyphSize = 22 * scale,   // the TV's 22
            Value = meta.Rating,
            IsInteractive = true,
            IsTabStop = true,
        };
        var folder = card.Folder;
        stars.RatingPicked += (_, picked) => SaveRecordRating(folder, picked);
        mine.Children.Add(stars);
        block.Children.Add(mine);
        _recordStars = stars;

        return block;
    }

    private StackPanel? _recordAverageRow;
    private StarRating? _recordAverageStars;
    private TextBlock? _recordAverageText;

    /// <summary>The listener's own stars on the page, so Rate album in the record's menu can move them to
    /// what it saved. Null on a page with no stars (a list, or a borrowed record).</summary>
    private StarRating? _recordStars;

    /// <summary>
    /// Whether the record page shows somebody else's collection.
    ///
    /// <para>Pegasus follows the TV here: on a borrowed collection the page has no heart and none of the
    /// listener's own stars (the wall's average stays), the record's menu has no Favourite album and no
    /// Rate album, and a song's menu has no favourite and no Add to playlist (see <see cref="TrackMenu"/>).
    /// Hidden, not dimmed. The main window keeps the
    /// favourite on a borrowed song, as the listener's own opinion; Pegasus does not.</para>
    ///
    /// <para>The same question the main window's track menu asks (MainPage.BuildTrackMenuState): whether
    /// the collection on screen is a borrowed one. It is read when a page or a menu is built.</para>
    /// </summary>
    private static bool RecordBorrowed => Library.IsPeerView;

    /// <summary>The listener's own stars, into the mirror. The review beside them is carried through
    /// untouched: this screen sets a rating, and a rating is not a reason to lose what somebody wrote
    /// about the record on another screen.</summary>
    private void SaveRecordRating(string folder, double picked)
    {
        var library = Library;
        if (!library.IsOpen)
            return;

        try
        {
            var db = library.Db;
            var meta = db.GetAlbumMeta(folder);
            db.SetAlbumReview(folder, (int)Math.Round(picked), meta.ReviewMarkdown, DateTime.UtcNow.Ticks);
        }
        catch
        {
            // A collection that would not take it. The stars stay where the press put them and do not
            // jump back, which would look as if the press had not landed at all.
        }
    }

    /// <summary>
    /// What the wall says about this record, asked once and never waited for.
    ///
    /// <para>Read-only and signed out: this is a number to look at, not a thing to write, and asking for
    /// it must never be the reason a screen over a running shader takes a moment to open. Anything that
    /// goes wrong (no network, no entry, no account) leaves the line collapsed, which is what a
    /// record nobody has rated looks like.</para>
    /// </summary>
    private async void ReadAverageAsync(PegasusAlbumCard card, List<LibraryTrack> rows, int read)
    {
        var title = card.Name;
        if (string.IsNullOrWhiteSpace(title))
            return;

        var artist = card.Under ?? rows.Select(t => t.AlbumArtist ?? t.Artist)
                                       .FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));

        RpmAlbum? album;
        try
        {
            var community = App.Services.GetRequiredService<RpmCommunityClient>();
            album = await community.LookupAlbumAsync(artist, title, ct: CancellationToken.None);
        }
        catch
        {
            return;
        }

        if (read != _recordRead || !RecordUp || album is not { RatingCount: > 0 }
            || _recordAverageRow is null || _recordAverageStars is null || _recordAverageText is null)
            return;

        _recordAverageStars.Value = album.AverageRating;
        _recordAverageText.Text = $"{album.AverageRating:0.0} ({album.RatingCount})";
        _recordAverageRow.Visibility = Visibility.Visible;
    }

    // ── the keys ──

    /// <summary>
    /// Play it, the record's menu, and love it.
    ///
    /// <para>Play takes the space the other two do not need: it is what somebody came to this screen to
    /// do, and the other two are things they might do. The layout is one wide key and fixed ones, not
    /// equal keys. The phone's album page makes the same split, for the same reason.</para>
    ///
    /// <para>The key beside Play opens the record's menu (see <see cref="ShowRecordMenu"/>), as on the TV's
    /// record page: Shuffle, Add to queue and Play next, and on the listener's own collection Favourite
    /// album and Rate album. Shuffle is in the menu and has no key of its own. The heart stays a key on
    /// the listener's own collection, and is not there at all on a borrowed one (see
    /// <see cref="RecordBorrowed"/>).</para>
    /// </summary>
    private Grid BuildRecordKeys(PegasusAlbumCard card, AlbumMeta meta, bool borrowed, double scale)
    {
        var keys = new Grid { ColumnSpacing = 10 * scale, Margin = new Thickness(0, 20 * scale, 0, 0) };
        keys.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        keys.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (!borrowed)
            keys.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var height = 46 * scale;

        var play = RecordKey("", Strings.Loc.Get("AlbumGrid_PlayAll"), scale, height,
                             () => PlayFromDrawer(_recordTracks, 0, RecordSleeve()), out _);
        play.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(play, 0);
        _recordPlayKey = play;
        keys.Children.Add(play);

        _recordFavorite = meta.IsFavorite;
        _recordFavoriteFor = card;

        var menu = RecordMenuKey(scale, height, at => ShowRecordMenu(at, card, card.Name, borrowed));
        Grid.SetColumn(menu, 1);
        keys.Children.Add(menu);

        if (borrowed)
        {
            // No heart on somebody else's record: nothing may repaint one through this page.
            _recordFavoriteKey = null;
            _recordFavoriteGlyph = null;
            return keys;
        }

        // The mark on the RECORD, which is not the same thing as a mark on its songs: this screen's own
        // lines carry theirs. Read from the mirror when the screen was built and written straight back,
        // so the heart fills in under the press rather than after a re-read.
        var favorite = RecordKey("", null, scale, height, () => ToggleRecordFavorite(card),
                                  out _recordFavoriteGlyph);
        favorite.Width = 56 * scale;
        Grid.SetColumn(favorite, 2);
        _recordFavoriteKey = favorite;
        PaintRecordFavorite();

        keys.Children.Add(favorite);
        return keys;
    }

    private bool _recordFavorite;
    private Button? _recordFavoriteKey;

    /// <summary>The heart ITSELF, not the key it sits on.
    ///
    /// <para>Held because the fill has to be written to the glyph. The key carries a StackPanel with its
    /// own TextBlock in it, and that TextBlock is given a FontFamily of its own when it is built. A
    /// weight written to the BUTTON is inherited by nothing and changes nothing.</para></summary>
    private TextBlock? _recordFavoriteGlyph;

    /// <summary>The record the heart is about, for the line that says what was just done to it.</summary>
    private PegasusAlbumCard? _recordFavoriteFor;

    private void ToggleRecordFavorite(PegasusAlbumCard card)
    {
        var next = !_recordFavorite;

        // False means there was nowhere to write it. Nothing is said and the heart does not fill in, a
        // mark that was not stored must not be reported as one that was.
        if (!Library.SetAlbumFavorite(card.Folder, next, AlbumShelf.OnScreen))
            return;

        _recordFavorite = next;
        card.IsFavorite = next;   // …and the wall behind, which narrows itself on exactly this
        PaintRecordFavorite();

        // Said in words, as the visualizer's own heart does. Full screen there is no line to watch
        // fill in and no menu that just closed, so a press has to answer for itself: one sentence at
        // the foot of the screen, naming the record, gone a couple of seconds later. The same two
        // strings the song's heart uses: what is being named changes, what happened does not.
        Announced?.Invoke(string.Format(
            Strings.Loc.Get(next ? "Viz_FavoriteAdded" : "Viz_FavoriteRemoved"), card.Name));
    }

    /// <summary>Fills the heart in, or hollows it out, the same two weights of one glyph the rest of the
    /// app draws a favourite with, and the red it wears once it is filled.</summary>
    private void PaintRecordFavorite()
    {
        if (_recordFavoriteGlyph is { } glyph)
        {
            glyph.FontFamily = (FontFamily)Resources[_recordFavorite ? "PegasusFa" : "PegasusFaRegular"];
            glyph.Foreground = (Brush)Application.Current.Resources[
                _recordFavorite ? "FavoriteMarkBrush" : "FavoriteMarkIdleBrush"];
        }

        if (_recordFavoriteKey is { } key)
            IconName.Set(
                key, Strings.Loc.Get(_recordFavorite ? "Album_UnsetFavorite" : "Album_SetFavorite"));
    }

    /// <summary>One key: a glyph, and words beside it where there are any. The words are what makes the
    /// play key wide, so it is the only one that carries them.</summary>
    /// <param name="glyphBlock">The mark itself, handed back so a caller whose key CHANGES its mark has
    /// something to write to. The glyph carries its own FontFamily, it has to, the words beside it are
    /// in another face, so writing a weight to the button reaches nothing.</param>
    private Button RecordKey(string glyph, string? words, double scale, double height, Action pressed,
                             out TextBlock glyphBlock)
    {
        var face = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10 * scale,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        glyphBlock = new TextBlock
        {
            Text = glyph,
            FontFamily = (FontFamily)Resources["PegasusFa"],
            FontSize = 17 * scale,   // the TV's page key: the glyph 17, the words 19 (PAGE_TEXT)
            VerticalAlignment = VerticalAlignment.Center,
        };
        face.Children.Add(glyphBlock);
        if (words is not null)
            face.Children.Add(new TextBlock
            {
                Text = words,
                FontSize = RecordPageText * scale,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });

        var key = new Button
        {
            Content = face,
            Height = height,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(12 * scale),
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(0x2B, 0xFF, 0xFF, 0xFF)),

            // NOT the icon font. If it were set here as well as on the glyph, the words would inherit
            // it, and Font Awesome has letters but no space, so "Play album" would come out as
            // "Playalbum". The glyph above names its own font and is the only thing on this button that
            // wants one.
        };
        key.Click += (_, _) => pressed();
        return key;
    }

    // ── the record's menu ──

    /// <summary>The key beside Play that opens the page's menu: three bars, no words, as narrow as the
    /// heart. Its name for a screen reader and its tooltip are the main window's album page's own
    /// "More".</summary>
    /// <param name="pressed">What a press does, handed the key so the menu can open at it.</param>
    private Button RecordMenuKey(double scale, double height, Action<Button> pressed)
    {
        Button? key = null;
        key = RecordKey("", null, scale, height, () => pressed(key!), out _);
        key.Width = 56 * scale;
        IconName.Set(key, Strings.Loc.Get("Album_MoreActions"));
        return key;
    }

    /// <summary>
    /// The menu of the record or list on the page, opened at the key beside Play. The TV's record page has
    /// the same menu, in the same order (TrackPage.kt, pageChoices).
    ///
    /// <list type="bullet">
    /// <item>Shuffle: the whole record in an order nobody chose, the key this menu replaced.</item>
    /// <item>Add to queue: every track at the end of the queue, and nothing that is playing changes.</item>
    /// <item>Play next: every track straight after the song that is playing, in the record's order.</item>
    /// <item>On a record of the listener's own collection, and not on a list: Favourite album (or Remove
    /// from favourites), the same mark as the heart key, and Rate album, the same stars as the row under
    /// the name.</item>
    /// </list>
    ///
    /// <para>Built when it opens, so the favourite's wording and the ticked rating are what is true at that
    /// moment. At this view's size, see <see cref="ForThisView"/>. The same entries, with Play before them,
    /// make the record's menu away from its page (see <see cref="RecordMenuOf(PegasusAlbumCard)"/>).</para>
    /// </summary>
    /// <param name="card">The record, or null for a list.</param>
    /// <param name="name">What the line at the foot of the screen calls it after a queue entry.</param>
    /// <param name="borrowed">The record is on somebody else's collection (see
    /// <see cref="RecordBorrowed"/>): no Favourite album and no Rate album.</param>
    private void ShowRecordMenu(Button at, PegasusAlbumCard? card, string name, bool borrowed)
    {
        var (menu, _, _) = BuildRecordMenu(
            new RecordMenuSubject(
                name,
                IsList: card is null,
                Marks: card is not null && !borrowed,
                Favorite: _recordFavorite,
                Stars: (int)Math.Round(_recordStars?.Value ?? 0),
                WithSongs: use => use(_recordTracks, RecordSleeve()),
                ToggleFavorite: () =>
                {
                    if (card is not null)
                        ToggleRecordFavorite(card);
                },
                Rate: given =>
                {
                    if (card is null)
                        return;
                    SaveRecordRating(card.Folder, given);
                    if (_recordStars is { } row)
                        row.Value = given;
                }),
            withPlay: false);

        menu.Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft;
        ShowMenu(ForThisView(menu), at);
    }

    /// <summary>
    /// The menu of a record away from its page: Play, then the record page's own entries (see
    /// <see cref="ShowRecordMenu"/>), as the TV's record menu away from its page (TrackPage.kt,
    /// albumChoices). It is built and not shown, at this view's size, for whatever opens it: a held OK on a
    /// card of the album wall or of home's rows (see RemoteLine), or the album carousel. Each of them asks
    /// here and nowhere else, so the record's menu is one menu wherever it opens.
    ///
    /// <para>Nothing is read to build it, so it opens at once. The songs are read off the collection when an
    /// entry that needs them is chosen. The favourite's wording starts from the card, and the favourite
    /// and the ticked rating are corrected from the record's stored row when that read lands, which is
    /// usually before the menu is looked at.</para>
    /// </summary>
    private MenuFlyout RecordMenuOf(PegasusAlbumCard card) => RecordMenuAway(card.Folder, card.Name, card);

    /// <summary>The same menu for a record known by its folder and its name, such as a card in home's rows.
    /// The wall's own card is used where the wall has been read this run, so its heart stays true.</summary>
    private MenuFlyout RecordMenuOf(string folder, string name) =>
        RecordMenuAway(folder, name, _libraryByFolder.GetValueOrDefault(folder));

    private MenuFlyout RecordMenuAway(string folder, string name, PegasusAlbumCard? card)
    {
        var favored = card?.IsFavorite ?? false;
        var marks = !RecordBorrowed;

        var (menu, favorite, rate) = BuildRecordMenu(
            new RecordMenuSubject(
                name,
                IsList: false,
                Marks: marks,
                Favorite: favored,
                Stars: 0,
                WithSongs: use => UseRecordSongsAsync(folder, card, use),
                ToggleFavorite: () =>
                {
                    // False means there was nowhere to write it: nothing is said, as on the page's heart.
                    var next = !favored;
                    if (!Library.SetAlbumFavorite(folder, next, AlbumShelf.OnScreen))
                        return;
                    favored = next;
                    if (card is not null)
                        card.IsFavorite = next;
                    Announced?.Invoke(string.Format(
                        Strings.Loc.Get(next ? "Viz_FavoriteAdded" : "Viz_FavoriteRemoved"), name));
                },
                Rate: given => SaveRecordRating(folder, given)),
            withPlay: true);

        if (favorite is not null && rate is not null)
            CorrectRecordMarksAsync(favorite, rate);

        return ForThisView(menu);

        // The stored row, read off the thread that draws the view. A menu closed before the read lands is
        // only written to, which nobody sees.
        async void CorrectRecordMarksAsync(MenuFlyoutItem favoriteEntry, MenuFlyoutSubItem rateEntry)
        {
            var library = Library;
            if (!library.IsOpen)
                return;

            var db = library.Shelf;
            try
            {
                var asked = await Task.Run(() => library.Read(db, "a record's marks for Pegasus's menu",
                                                               mirror => mirror.GetAlbumMeta(folder)));
                if (!asked.Answers(out var meta, out _))
                    return;

                favored = meta.IsFavorite;
                favoriteEntry.Text = Strings.Loc.Get(favored ? "Album_UnsetFavorite" : "AlbumMenu_Favorite");
                favoriteEntry.Icon = FavoriteIcon(favored);
                var stars = 0;
                foreach (var item in rateEntry.Items.OfType<ToggleMenuFlyoutItem>())
                    item.IsChecked = ++stars == meta.Rating;
            }
            catch (Exception ex)
            {
                // Not expected: the read answers through Asked. It is noted, and the menu keeps what the
                // card said. Otherwise the exception would reach the dispatcher from an async void method.
                RPM.Core.Diagnostics.Faults.Note("pegasus", "a record's marks for its menu", ex);
            }
        }
    }

    /// <summary>A record's songs read off the collection, in play order, handed to <paramref name="use"/>
    /// with the card's cover as the sleeve in hand where the card has one. Nothing happens where the read
    /// does not answer.</summary>
    private static async void UseRecordSongsAsync(
        string folder, PegasusAlbumCard? card, Action<List<TrackInfo>, SleeveInHand> use)
    {
        if (await ReadRecordAsync(folder) is not { } found)
            return;

        var songs = found.Rows.Select(t => t.ToTrackInfo(found.Root)).ToList();
        SleeveInHand sleeve = (card?.Sharp ?? card?.Cover) is { } picture
            ? new SleeveInHand.Of(picture, RecordFiles.Of(songs))
            : new SleeveInHand.None();
        use(songs, sleeve);
    }

    /// <summary>What a record's menu acts on.</summary>
    /// <param name="Name">What the line at the foot of the screen calls it after a queue entry.</param>
    /// <param name="IsList">A list on the record screen rather than a record: its Shuffle has a list's
    /// words.</param>
    /// <param name="Marks">Favourite album and Rate album are offered: a record of the listener's own
    /// collection (see <see cref="RecordBorrowed"/>).</param>
    /// <param name="Favorite">Whether it is a favourite now, for the favourite's wording.</param>
    /// <param name="Stars">The rating ticked when the menu opens, or 0 for none.</param>
    /// <param name="WithSongs">Hands the songs, in play order, and the sleeve in hand to what an entry
    /// does with them.</param>
    /// <param name="ToggleFavorite">What Favourite album (or Remove from favourites) does.</param>
    /// <param name="Rate">What a choice under Rate album does with the stars chosen.</param>
    private sealed record RecordMenuSubject(
        string Name,
        bool IsList,
        bool Marks,
        bool Favorite,
        int Stars,
        Action<Action<List<TrackInfo>, SleeveInHand>> WithSongs,
        Action ToggleFavorite,
        Action<int> Rate);

    /// <summary>The entries of a record's menu, at the desktop's menu size (see <see cref="ShowRecordMenu"/>),
    /// with Play first where <paramref name="withPlay"/> is set. The favourite entry and Rate album are
    /// handed back too, or null where they are not offered, so a caller can correct them later.</summary>
    private (MenuFlyout Menu, MenuFlyoutItem? Favorite, MenuFlyoutSubItem? Rate) BuildRecordMenu(
        RecordMenuSubject on, bool withPlay)
    {
        var menu = new MenuFlyout();

        if (withPlay)
            menu.Items.Add(MenuEntry(Strings.Loc.Get("AlbumMenu_Play"),
                                     () => on.WithSongs((songs, sleeve) => PlayFromDrawer(songs, 0, sleeve)), PlayGlyph));
        menu.Items.Add(MenuEntry(Strings.Loc.Get(on.IsList ? "AlbumMenu_Shuffle" : "AlbumGrid_Shuffle"),
                                 () => on.WithSongs(PlayShuffledList), ShuffleGlyph));
        menu.Items.Add(MenuEntry(Strings.Loc.Get("Tv_Album_AddToQueue"),
                                 () => on.WithSongs((songs, _) => QueueSongs(songs, next: false, on.Name)), QueueGlyph));
        menu.Items.Add(MenuEntry(Strings.Loc.Get("AlbumMenu_PlayNext"),
                                 () => on.WithSongs((songs, _) => QueueSongs(songs, next: true, on.Name)), PlayNextGlyph));

        if (!on.Marks)
            return (menu, null, null);

        menu.Items.Add(new MenuFlyoutSeparator());
        var favorite = MenuEntry(Strings.Loc.Get(on.Favorite ? "Album_UnsetFavorite" : "AlbumMenu_Favorite"),
                                 on.ToggleFavorite);
        favorite.Icon = FavoriteIcon(on.Favorite);
        menu.Items.Add(favorite);
        var rate = RateRecordMenu(on.Stars, on.Rate);
        menu.Items.Add(rate);
        return (menu, favorite, rate);
    }

    /// <summary>
    /// Rate album: five entries, one to five stars, drawn as stars and not as words, with the rating the
    /// record has now (<paramref name="now"/>) ticked. The remote reaches them with Up, Down and OK, where
    /// the stars row on the page needs Left and Right on it. The choice goes to <paramref name="rate"/>,
    /// which saves it as the stars row saves it.
    /// </summary>
    private static MenuFlyoutSubItem RateRecordMenu(int now, Action<int> rate)
    {
        var sub = new MenuFlyoutSubItem
        {
            Text = Strings.Loc.Get("Tv_Album_Rate"),
            Icon = MenuIcon(StarGlyph),
            FontSize = 12,
            MinHeight = 26,
            Padding = new Thickness(11, 3, 11, 4),
        };

        for (var stars = 1; stars <= 5; stars++)
        {
            var given = stars;
            var item = new ToggleMenuFlyoutItem
            {
                Text = new string('★', given) + new string('☆', 5 - given),
                IsChecked = given == now,
                FontSize = 12,
                MinHeight = 26,
                Padding = new Thickness(11, 3, 11, 4),
            };
            item.Click += (_, _) => rate(given);
            sub.Items.Add(item);
        }

        return sub;
    }

    /// <summary>
    /// A whole record or list into the queue: after the song that is playing (Play next), or at the end
    /// (Add to queue). The same two inserts the main window's menus make on the same queue
    /// (RPaMpModel.InsertNext and AddTracks), so nothing that is playing is interrupted. With nothing on
    /// the deck, "next" is the end of an empty queue.
    ///
    /// <para>Said in words at the foot of the screen, as a swiped song is: full screen, there is no queue
    /// in view to watch it land in.</para>
    /// </summary>
    private void QueueSongs(List<TrackInfo> songs, bool next, string name)
    {
        if (_amp is not { } amp || songs.Count == 0)
            return;

        var tracks = songs.ToList();
        if (next)
            amp.InsertNext(tracks);
        else
            amp.AddTracks(tracks, startPlaying: false);

        Announced?.Invoke(string.Format(
            Strings.Loc.Get(next ? "Viz_QueueMovedNext" : "Atv_AddedToQueue"), name));
    }

    // ── the record out of its sleeve ──
    //
    // Everything below is the composition's own centrepiece at this screen's size. See Relayout, which
    // works the same geometry out for the record that is playing.
    //
    // This record stands still. It is a record being looked at and not heard: only a quarter of it shows
    // past the sleeve and its label is under the sleeve, so a turn would hardly be seen, and it would be
    // one more thing moving on a page where somebody is reading a tracklist. The TV's record page keeps
    // its record still for the same reason.

    private RecordDisc? _recordDisc;
    private TranslateTransform? _recordDiscShift;

    /// <summary>
    /// The disc's geometry off the sleeve's edge, exactly as the composition works out its own.
    ///
    /// <para>The control's box holds the disc AND everywhere its shadow can land, so the halo is not
    /// clipped square along the edges it overhangs; everything inside is then placed from the host's
    /// top-left corner, which keeps the disc's centre where the composition puts it rather than at the
    /// box's.</para>
    /// </summary>
    private void SizeRecordDisc(double edge)
    {
        if (_recordDisc is not { } record || _recordDiscShift is null)
            return;

        // A shade smaller than the sleeve, so its arc sits inside the frame's top and bottom.
        var disc = edge * 0.96;
        var halo = disc * PegasusLayout.DiscHaloScale;
        var throwX = disc * PegasusLayout.DiscShadowThrowX;
        var throwY = disc * PegasusLayout.DiscShadowThrowY;
        var reach = Math.Max(((halo - disc) / 2) + Math.Max(throwX, throwY), 0);
        var host = disc + (reach * 2);

        record.Width = host;
        record.Height = host;

        record.Box.Width = disc;
        record.Box.Height = disc;
        record.Box.HorizontalAlignment = HorizontalAlignment.Left;
        record.Box.VerticalAlignment = VerticalAlignment.Top;
        record.Box.Margin = new Thickness(reach, reach, 0, 0);

        record.ShadowHalo.Width = halo;
        record.ShadowHalo.Height = halo;
        record.ShadowHalo.HorizontalAlignment = HorizontalAlignment.Left;
        record.ShadowHalo.VerticalAlignment = VerticalAlignment.Top;
        record.ShadowHalo.Margin =
            new Thickness(((host - halo) / 2) + throwX, ((host - halo) / 2) + throwY, 0, 0);
        record.ShadowHalo.Opacity = 0.55;

        // Where it RESTS once it is out: the same fraction of the sleeve the composition uses, so the
        // quarter that shows is the quarter that shows over there.
        _recordDiscOut = edge * PegasusLayout.DiscOffsetFraction;

        // A resize does not re-run the slide: it puts the record wherever it already is, out if it has
        // come out, still in the sleeve if the screen has only just opened. Writing the resting offset
        // unconditionally would make a resize in the middle of the slide jump the rest of the way.
        _recordDiscShift.X = _recordDiscSlid ? _recordDiscOut : 0;
    }

    /// <summary>Presses this record in the sleeve's own colour, through the same cache the composition's
    /// disc comes out of, so a colour already on screen costs nothing. Fire and forget: until it lands
    /// the disc is bare, which looks better than an empty spindle.</summary>
    private async void PressRecordDisc(Color? hue)
    {
        if (_recordDisc is not { } record)
            return;

        var source = hue is { } colour
            ? await VinylPressing.PressAsync(colour)
            : await VinylPressing.PressBlackAsync();

        // The screen may have been left, or another record opened, while the press was in progress, and
        // both leave a DIFFERENT disc (or none) standing where this one was.
        if (source is not null && ReferenceEquals(record, _recordDisc))
            record.Stand.Vinyl.Source = source;
    }

    // ── the sleeve, at the size it is actually drawn ──

    /// <summary>
    /// Reads the record's cover again at the size THIS screen draws it.
    ///
    /// <para>What the tile is holding is cut for a tile, and this sleeve is several times larger. The
    /// tile's picture is shown first anyway, because a placeholder that is the right picture is better
    /// than a blank square. The larger read lands over it shortly after.</para>
    /// </summary>
    /// <summary>
    /// The album folder's own disc art, which replaces the drawn record rather than dressing it.
    ///
    /// <para>Two kinds of record can be on this screen: a folder with a photographed disc in it (disc.png,
    /// written by the album editor) shows THAT, and one without shows the app's own drawn record wearing
    /// the cover on its label. The player handles both too (see Apply, which is where the pair of writes
    /// below comes from), because the header hands it a disc already decoded. This screen opens straight
    /// off a wall tile and has no such answer in hand, so it has to ask for the disc art itself.</para>
    ///
    /// <para>Read at the sleeve's own size: it stands in the same square and is scaled the same way, so a
    /// second, larger decode of the same picture would cost a read and show nothing more.</para>
    /// </summary>
    private async void ReadRecordDiscAsync(PegasusAlbumCard card, int read)
    {
        if (card.Folder is not { Length: > 0 } folder || Library.ShelfRoot is not { } root)
            return;

        var edge = (int)Math.Ceiling((_recordSleeve?.Width ?? 320)
                                   * (XamlRoot?.RasterizationScale is > 0 and var s ? s : 1));
        edge = Math.Clamp(edge, ArtScaler.TinyEdge, ArtScaler.TileEdge);

        try
        {
            // Off the UI thread: this is a directory probe for three names and then a file read, and the
            // thread it would otherwise be on is the one driving the shader behind this screen.
            var bytes = await Task.Run(() =>
                FolderArtWriter.FindDiscImage(AbsoluteFolder(root, folder)) is { } path
                    ? File.ReadAllBytes(path)
                    : null);

            // Nothing in the folder, which for a BORROWED record means the folder is on another
            // computer and so is the picture. The sleeve on this screen is fetched that way, and the disc
            // must be too, or a shared shelf would draw the app's pressing for every record on it.
            bytes ??= await BorrowedDiscAsync(folder);

            if (bytes is not { Length: > 0 })
                return;   // no disc art, and the drawn record wearing the cover is the right answer

            // Cut out exactly as the header and the album page cut it (ArtDisplay.DecodeDiscBytesAsync,
            // AlbumDetailView.DiscScan): a photograph of a CD on a table becomes a round disc centred on
            // the picture, and Round says whether it is the whole disc or a vinyl label, which decides
            // whether it wears the CD overlay. Drawn as it came, a photograph would show the table around
            // the disc, and the disc would sit off the centre of the drawn record it replaces.
            var cut = await Task.Run(
                () => DiscStamp.Round(ThumbCache.Get(bytes, ArtScaler.TileEdge), vinyl: false));
            var bitmap = await DecodeArtAsync(cut.Bytes, edge);
            if (read != _recordRead || !RecordUp || _recordDisc is null)
                return;

            // The photographed disc IS the record, so the drawn label underneath goes with it (ShowCd
            // takes it down): left up, its centre would show through the middle of the picture.
            _recordDisc.Stand.ShowCd(bitmap, cut.CoversRecord);
        }
        catch
        {
            // A folder that has gone, or a picture that will not decode. The drawn record stands in, which
            // is what every record without disc art gets anyway.
        }
    }

    /// <summary>The disc art of a record whose files are not on this disk, off the computer that holds
    /// it. One row of the shelf says which computer that is. It is the same read, for the same reason, as
    /// the sleeve's <c>RemoteSleeveForAsync</c> on the album page.
    ///
    /// <para>Only for a record that is not here. An album sitting on this disk with no disc.png simply
    /// has none, and asking the network about each of those would be a wasted round trip.</para></summary>
    private static async Task<byte[]?> BorrowedDiscAsync(string folder)
    {
        var library = Library;
        if (!library.IsOpen)
            return null;

        var row = await Task.Run(() => library.Shelf.FolderTracks(folder).FirstOrDefault());
        if (row is null)
            return null;

        var file = Path.Combine(
            library.ShelfRoot ?? "", row.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(file) ? null : await RemoteSleeve.DiscForAsync(row);
    }

    private async void ReadSleeveAsync(PegasusAlbumCard card, Image art, int read)
    {
        var edge = (int)Math.Ceiling((_recordSleeve?.Width ?? 320)
                                   * (XamlRoot?.RasterizationScale is > 0 and var s ? s : 1));
        edge = Math.Clamp(edge, ArtScaler.TinyEdge, ArtScaler.TileEdge);

        try
        {
            if (await AlbumCovers.LoadAsync(card.Folder, CancellationToken.None, edge)
                is not { Length: > 0 } bytes)
                return;

            var bitmap = await DecodeArtAsync(bytes, edge);
            if (read != _recordRead || !RecordUp)
                return;

            art.Source = bitmap;

            // The label on the record behind it also wears this cover. It was given the tile's picture
            // when the screen was built, so a record opened before its tile had been read would keep a
            // blank label while the sleeve over it sharpened. Everywhere else the disc is built from art
            // already in hand; this is the one screen that can open before the picture exists.
            //
            // Only the LABEL. A folder with disc art of its own has the whole drawn record replaced by it
            // instead, and that arrives on its own (see ReadRecordDiscAsync). Writing the cover onto the
            // label underneath is harmless either way, since the label host is hidden when it lands.
            if (_recordDisc is not null)
                _recordDisc.Stand.Label.Source = bitmap;

            // A card the wall never held has no bytes of its own for the room to be lit from, so the room
            // is lit from this read. A wall card was lit from its own bytes when the screen went up.
            if (card.Room is null && card.ResidentBytes is null)
                PaintRoomFromRecord(card, bytes, read);
        }
        catch
        {
            // The tile's own picture stays up. A sleeve one size step too blurred can still be read.
        }
    }
}
