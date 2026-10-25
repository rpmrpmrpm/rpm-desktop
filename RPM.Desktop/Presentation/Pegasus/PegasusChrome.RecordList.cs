using RPM.Core.Library;
using RPM.Desktop.Services.Art;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// A list pressed on home, opened on the record screen: the same page a record gets, with the parts that
/// only a record has left out.
///
/// <para>The drawer has its own list screen (PegasusChrome.Album), which the Playlists tab opens.</para>
///
/// <para><b>What it shows.</b> The sleeve is the list's own cover, drawn through the same door its card on
/// home and the playlists tree draw through (<see cref="ListCoverDoor"/>), so the list opens wearing the
/// picture it was pressed on. The record behind it wears the same
/// picture on its label, and the room and the disc take their colours from the picture behind the
/// cover's frame. Then the name, the folder it sits in for a list of the listener's own, how many songs
/// and how long, and two keys: play, and the list's menu (Shuffle, Add to queue, Play next). The
/// running order on the right numbers its positions and says under each title who it is by and which
/// record it came off.</para>
///
/// <para><b>What it leaves out</b>, because each is a record's and a list has none: the stars, the heart,
/// the catalogues' words about it, and disc art out of a folder.</para>
///
/// <para><b>Order.</b> Everything that lands later (the tracks, the cover, the room's colours) is dropped
/// unless <see cref="_recordRead"/> is still the opening that asked for it and the screen is still up:
/// the same rule the record path keeps, on the same counter, so a list and a record opened one after the
/// other cannot land on each other's screen.</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>
    /// Where a list's cover comes from: the doors home's cards draw through (HomeView.ListCoverAsync), so
    /// the card, this page and the drawer's own list screen are one picture. Carried by the list's
    /// BrowseEntry, which states it at every row.
    /// </summary>
    private abstract record ListCoverDoor
    {
        /// <summary>A generated list: its cover is made from the list itself (see
        /// GeneratedCover).</summary>
        public sealed record Generated(SmartPlaylistId Id) : ListCoverDoor;

        /// <summary>A list somebody made and kept: drawn from its records, or the picture chosen for it
        /// (see PlaylistCovers). <paramref name="Shelf"/> is whose tree the node was read from.</summary>
        public sealed record Kept(long NodeId, string Name, PlaylistCover Cover, PlaylistShelf Shelf)
            : ListCoverDoor;
    }

    /// <summary>The cover and the picture behind its frame, through the door.</summary>
    private static Task<PlaylistArt.Drawn> DrawListCoverAsync(ListCoverDoor door) => door switch
    {
        ListCoverDoor.Generated g => GeneratedCover.PageAsync(g.Id, CancellationToken.None),
        ListCoverDoor.Kept k => PlaylistCovers.MadeAsync(k.NodeId, k.Name, k.Cover, k.Shelf, CancellationToken.None),
        _ => Task.FromResult(PlaylistArt.Drawn.None),
    };

    /// <summary>Opens one list on the record screen: its tracks read off the collection, the page built
    /// from them, the collection crossed away. A list's entry carries its cover's door; a record's has
    /// none and is not opened here (see ShowRecordAsync).</summary>
    private async void ShowListRecordAsync(BrowseEntry entry)
    {
        if (entry.Cover is not { } cover)
            return;

        NoteRecordOpener();   // where the focus goes back to when the page is left (see PegasusChrome.Remote)
        var read = ++_recordRead;
        var scale = QueueScale;

        var library = Library;
        if (!library.IsOpen || library.ShelfRoot is not { } root)
            return;

        List<LibraryTrack> rows;
        try
        {
            // One query off the thread that is driving the shader. A list is left in its own order: its
            // order IS the thing somebody made (see ShowDetailAsync).
            rows = await Task.Run(() => entry.Tracks().ToList());
        }
        catch
        {
            return;   // a collection that moved under the read; the screen stays where it is
        }

        // Dropped if another record or list was opened meanwhile, or the collection went down under the
        // read, exactly as a record's read is.
        if (read != _recordRead || !LibraryUp || rows.Count == 0)
            return;

        _recordCard = null;
        _recordTracks = rows.Select(t => t.ToTrackInfo(root)).ToList();

        BuildListRecord(entry, cover, rows, scale);
        RecordScroll.ChangeView(null, 0, null, disableAnimation: true);   // a new list starts at its top
        CrossToRecord();
        SlideRecordDisc(outward: true);
        FocusScreenLater(_recordPlayKey);   // with the remote in use: the page opens on its play key
    }

    /// <summary>Lays the record screen out for a list. The same pieces, sizes and places as
    /// <see cref="BuildRecord"/>, so the two pages read as one page.</summary>
    private void BuildListRecord(BrowseEntry entry, ListCoverDoor cover, List<LibraryTrack> rows, double scale)
    {
        RecordBody.Children.Clear();
        RecordInfoPanel.Children.Clear();
        RecordBody.Spacing = 0;
        var read = _recordRead;

        // A list's page opens on its play key: a list is mostly put on whole. No line of the last page
        // may be where Right from the keys goes back to.
        _recordFirstTrack = null;
        _recordLastTrack = null;

        // A list has no heart and no stars. What a record built for them is let go, so nothing that
        // repaints a heart or lands an average can reach the last record's controls through this page.
        _recordFavoriteKey = null;
        _recordFavoriteGlyph = null;
        _recordFavoriteFor = null;
        _recordAverageRow = null;
        _recordAverageStars = null;
        _recordAverageText = null;
        _recordStars = null;

        // ── the record, and the list's cover over it ──
        //
        // One picture: the list's own cover, as its card shows it. Until it lands the frame shows a
        // plain square, as a record's sleeve does before its picture is read.
        var art = new Image { Stretch = Stretch.UniformToFill };

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
        _recordDiscSlid = false;
        _recordDisc = new RecordDisc
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            RenderTransform = _recordDiscShift,
        };
        PressRecordDisc(null);   // black until the cover's colour is read (see ReadListCoverAsync)

        var info = RecordInfoPanel;
        var centre = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        centre.Children.Add(_recordDisc);
        centre.Children.Add(sleeve);
        info.Children.Add(centre);

        SizeRecord();
        ReadListCoverAsync(cover, art, read);

        // ── what it is ──
        info.Children.Add(new TextBlock
        {
            Text = entry.Name,
            FontSize = 24 * scale,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 22 * scale, 0, 0),
        });

        // The folders a list of the listener's own sits in. A generated list has none.
        if (!string.IsNullOrWhiteSpace(entry.Under))
            info.Children.Add(new TextBlock
            {
                Text = entry.Under,
                FontSize = RecordPageText * scale,
                Foreground = new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 4 * scale, 0, 0),
            });

        // ── how much there is ──
        info.Children.Add(new TextBlock
        {
            Text = string.Format(Strings.Loc.Get("Rpm_Album_Tracks"), rows.Count)
                 + "  ·  " + Duration(rows.Sum(t => t.DurationSeconds)),
            FontSize = RecordPageText * scale,
            Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 12 * scale, 0, 0),
        });

        // ── the two things you can do to it ──
        //
        // Play, and the key beside it that opens the list's menu: Shuffle, Add to queue and Play next (see
        // ShowRecordMenu). Split as the record's are (play takes the wider space), without the heart: a
        // list cannot be loved the way a record is.
        var keys = new Grid { ColumnSpacing = 10 * scale, Margin = new Thickness(0, 20 * scale, 0, 0) };
        keys.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        keys.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var height = 46 * scale;

        var play = RecordKey("", Strings.Loc.Get("Playlists_Play"), scale, height,
                             () => PlayFromDrawer(_recordTracks, 0, RecordSleeve()), out _);
        play.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(play, 0);
        _recordPlayKey = play;

        var name = entry.Name;
        var menu = RecordMenuKey(scale, height, at => ShowRecordMenu(at, card: null, name, borrowed: false));
        Grid.SetColumn(menu, 1);

        keys.Children.Add(play);
        keys.Children.Add(menu);
        info.Children.Add(keys);

        // ── and everything on it ──
        var list = RecordBody;
        list.Spacing = 4 * scale;

        var titles = new List<TextBlock>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
            list.Children.Add(RecordTrack(rows[i], _recordTracks, i, scale, titles, inList: true));

        // Capped off the column they stand in, as the record's titles are (see BuildRecord).
        list.SizeChanged += (_, e) => CapRecordTitles(titles, e.NewSize.Width, scale);
    }

    /// <summary>
    /// The list's cover, through its door, onto the sleeve and onto the disc's label; and the room and the
    /// disc in the colours of the picture behind the cover's frame.
    ///
    /// <para>The colours come off that picture (the wash) rather than off the cover, for the reason home's
    /// plate gives (HomeView.LoadListFaceAsync): the cover carries the app's drawing, a border and the
    /// list's name inked in colours that are not the picture's. A cover with no wash is read as it is.
    /// Every landing asks whether this opening is still the one on screen.</para>
    /// </summary>
    private async void ReadListCoverAsync(ListCoverDoor door, Image art, int read)
    {
        var edge = (int)Math.Ceiling((_recordSleeve?.Width ?? 320)
                                   * (XamlRoot?.RasterizationScale is > 0 and var s ? s : 1));
        edge = Math.Clamp(edge, ArtScaler.TinyEdge, ArtScaler.TileEdge);

        try
        {
            var drawn = await DrawListCoverAsync(door);
            if (read != _recordRead || !RecordUp)
                return;
            if (drawn.Cover is not { Length: > 0 } cover)
                return;   // no picture anywhere for this list; the frame's plain square stays

            var bitmap = await DecodeArtAsync(cover, edge);
            if (read != _recordRead || !RecordUp)
                return;

            art.Source = bitmap;
            if (_recordDisc is not null)
                _recordDisc.Stand.Label.Source = bitmap;

            PaintRoomFromList(drawn.Wash is { Length: > 0 } wash ? wash : cover, read);
        }
        catch
        {
            // A cover that would not draw or decode. The plain square stays, as for a record.
        }
    }

    /// <summary>The room, and the disc's pressing, in the colours of the list's picture: the record path's
    /// reader (VinylPressing.PaletteFromArt) on the pool, landed only on the opening that asked.</summary>
    private async void PaintRoomFromList(byte[] cover, int read)
    {
        RoomColours colours;
        try
        {
            var (_, tint, glow, shade) = await Task.Run(() => VinylPressing.PaletteFromArt(cover));
            colours = new RoomColours(tint, glow, shade);
        }
        catch
        {
            return;   // not a picture the reader knows; the room is left as it was
        }

        if (read != _recordRead || !RecordUp)
            return;

        RoomPalette?.Invoke(colours);
        PressRecordDisc(colours.Tint);
    }
}
