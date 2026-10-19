using RPM.Core.Library;
using RPM.Core.Import;
using RPM.Core.Metadata;
using RPM.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// One thing off the shelf, opened: its sleeve, what it is, and what is on it. A record or a playlist. The
/// same screen serves both, because the question asked of each is the same.
///
/// <para>The screen between choosing something and hearing it. Pressing a row on the shelf does not start
/// it playing. A dozen songs under one picture is a question, not an answer, and the listener may want the
/// fourth track rather than the first. So the screen opens, and plays from wherever the listener chooses:
/// the whole of it from the top, the whole of it shuffled, or from any line in it.</para>
///
/// <para>Everything here is read when the record is opened, and thrown away when it is left. The tracks
/// come from one indexed query. The sleeve is one decode at a size worth looking at. The format line comes
/// from the first track's own row. Nothing is kept between openings, because a drawer over a visualizer is
/// not the place to keep a second copy of the collection.</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>The record on screen, so a resize can build it again at the new size.</summary>
    private BrowseEntry? _albumEntry;

    /// <summary>Its tracks, in play order, resolved once when it was opened, what every press on this
    /// screen plays some part of.</summary>
    private List<TrackInfo> _albumTracks = [];

    /// <summary>The picture in the open album's frame, for handing to the player when it is
    /// played.</summary>
    private Image? _albumSleeveArt;

    /// <summary>What a press on this screen hands the player: a record's own sleeve, or nothing for a list,
    /// whose cover is the list's and not any one song's.</summary>
    private SleeveInHand AlbumSleeve() => _albumEntry is { Cover: null, Folder: not null }
        ? SleeveInHand.From(_albumSleeveArt, _albumTracks)
        : new SleeveInHand.None();

    /// <summary>The number of the opening that filled the screen. It works like the shelf's own read: when
    /// the tracks come back from disk and another record has been opened in the meantime, the old result is
    /// dropped.</summary>
    private int _albumRead;

    /// <summary>
    /// Opens one row off the shelf: reads its tracks, lays the screen out, and crosses to it.
    /// </summary>
    private async void ShowDetailAsync(BrowseEntry entry)
    {
        var read = ++_albumRead;
        var scale = DrawerScale;

        var library = Library;
        if (!library.IsOpen || library.Root is not { } root)
            return;

        // A record is put into disc and track order. The folder query returns tracks by file name, and a
        // sleeve is read by disc and track. A PLAYLIST is left exactly as it is. Its order is the one
        // somebody made, and sorting it would overrule them.
        var byDisc = entry.Folder is not null;

        List<LibraryTrack> rows;
        try
        {
            // One indexed query, run off the UI thread. The order it returns is the order the play keys
            // use, so the list on screen matches what plays.
            rows = await Task.Run(() =>
            {
                var found = entry.Tracks();
                return byDisc
                    ? found.OrderBy(t => t.DiscNumber)
                           .ThenBy(t => t.TrackNumber)
                           .ThenBy(t => t.FileName, StringComparer.OrdinalIgnoreCase)
                           .ToList()
                    : found.ToList();
            });
        }
        catch
        {
            return;
        }

        if (read != _albumRead || rows.Count == 0)
            return;

        _albumEntry = entry;
        _albumTracks = rows.Select(t => t.ToTrackInfo(root)).ToList();

        // With the remote on the shelf, Back comes back to the row that was pressed, and the focus goes to
        // the record's play key. Read before the screen changes, while the row still has the focus.
        var keysOnShelf = FocusedWithin(BrowseRows) as Control;

        BuildDetail(entry, rows, root, scale);
        AlbumScroll.ChangeView(null, 0, null, disableAnimation: true);   // a new record starts at its top
        UpdateTopFade();   // …and a record at its top is not dissolving into anything
        ShowScreen(DrawerScreen.Album);

        if (keysOnShelf is not null)
        {
            _detailOpener = keysOnShelf;
            FocusScreenLater(_detailPlayKey);
        }
    }

    /// <summary>Lays the whole screen out: the sleeve and what it is, the keys, then the tracks.</summary>
    private void BuildDetail(BrowseEntry entry, List<LibraryTrack> rows, string root, double scale)
    {
        LightRow(null);   // the rows about to be thrown away include whichever one was lit
        AlbumBody.Children.Clear();
        AlbumBody.Spacing = 0;

        var first = rows[0];
        var seconds = rows.Sum(t => t.DurationSeconds);
        var read = _albumRead;

        // The wash.
        //
        // The same cover as the sleeve below, enlarged over the whole drawer and blurred until it is no
        // longer a clear picture (see ArtScaler.Wash and the AlbumBackdrop layer). A record's cover is read
        // from its folder here. A list's comes with its cover (see ShowListArtAsync), the picture behind
        // the cover's frame.
        if (entry.Cover is null)
            ShowWashAsync(entry.Folder is { } folder ? AbsoluteFolder(root, folder) : null, read);

        // The sleeve.
        //
        // As wide as the drawer allows. This makes the screen read as a record, not as another list with a
        // picture on it. The sleeve sits in a frame, like the sleeve in the middle of the screen behind it.
        // A record with no cover shows a plain square instead of a hole.
        //
        // A list shows its own cover: the picture its card on home and the playlists tree show, read
        // through the same door (see ListCoverDoor).
        var edge = Math.Max(120, (QueuePanel.Width > 0 ? QueuePanel.Width : 320) - 96 * scale);
        AlbumBody.Children.Add(new Border
        {
            Width = edge,
            Height = edge,
            Margin = new Thickness(0, 10 * scale, 0, 14 * scale),
            HorizontalAlignment = HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(4 * scale),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            Child = BuildSleeve(entry, root, read),
        });

        // What it is.

        AlbumBody.Children.Add(Line(entry.Name, 22 * scale, 1, Microsoft.UI.Text.FontWeights.SemiBold));
        if (!string.IsNullOrWhiteSpace(entry.Under))
            AlbumBody.Children.Add(Line(entry.Under!, 15 * scale, 0.75, Microsoft.UI.Text.FontWeights.Normal));

        // How much of it there is, in the words the phone's own album page uses. It is the same reading of
        // the same collection, so the two heads do not describe one record two ways.
        var count = string.Format(Strings.Loc.Get("Rpm_Album_Tracks"), rows.Count);
        AlbumBody.Children.Add(new Border
        {
            Margin = new Thickness(0, 10 * scale, 0, 0),
            Padding = new Thickness(12 * scale, 4 * scale, 12 * scale, 4 * scale),
            CornerRadius = new CornerRadius(12 * scale),
            Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = seconds > 0 ? $"{count} · {Runtime(seconds)}" : count,
                FontSize = 12.5 * scale,
                Opacity = 0.85,
            },
        });

        // What the record is made of, from the first track's own row: "FLAC · 16/44.1 · 5.1", using
        // whichever of those the file knows. The same describer is used by the album page and the wall
        // band.
        //
        // A record only. One line describing one file is a fair description of an album, whose tracks came
        // off one disc in one format. A playlist is a dozen records from a dozen places, and its first
        // track speaks for none of the others.
        if (entry.Folder is not null)
        {
            var format = AudioFormat.Describe(
                first.Extension, first.Channels, first.CodecDescription,
                first.BitsPerSample, first.SampleRate, first.BitrateKbps);
            if (format.Count > 0)
            {
                var line = Line(string.Join("  ·  ", format), 11.5 * scale, 0.5,
                                Microsoft.UI.Text.FontWeights.Normal);
                line.FontStyle = Windows.UI.Text.FontStyle.Italic;
                line.Margin = new Thickness(0, 6 * scale, 0, 0);
                AlbumBody.Children.Add(line);
            }
        }

        // What is said about it.
        //
        // A record only. A playlist is a dozen records from a dozen places, and no encyclopedia has an
        // article about somebody's Friday night. See PegasusChrome.About for what fills the slot.
        if (entry.Folder is not null)
        {
            var about = AboutSlot(scale);
            AlbumBody.Children.Add(about);
            ShowAboutAsync(about, entry.Under, entry.Name, rows.Count, () => read == _albumRead, scale);
        }

        // The keys.

        AlbumBody.Children.Add(BuildDetailKeys(entry, scale));

        // What is on it.
        //
        // A box set shows where each disc starts, as the library's own tracklist does: a caption over a
        // hairline. This is for a record only, and only one with more than one disc. "CD 1" over the whole
        // of a single-disc album would be a heading that says nothing. A playlist's rows come from a dozen
        // records, whose disc numbers mean nothing next to one another.
        var discs = entry.Folder is not null ? rows.Select(DiscOf).Distinct().Count() : 1;
        uint? open = null;

        for (var i = 0; i < rows.Count; i++)
        {
            var disc = DiscOf(rows[i]);
            if (discs > 1 && disc != open)
            {
                open = disc;
                AlbumBody.Children.Add(BuildDiscHeader(DiscCaption(rows, disc), scale));
            }
            AlbumBody.Children.Add(BuildDetailTrack(entry, rows[i], i, scale));
        }

        // A little air under the last line, so the end of a record is not flush with the end of a panel.
        AlbumBody.Children.Add(new Border { Height = 14 * scale });

        TextBlock Line(string text, double size, double opacity, Windows.UI.Text.FontWeight weight) => new()
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            Opacity = opacity,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(12 * scale, 0, 12 * scale, 0),
        };
    }

    /// <summary>Which disc a track is on. An untagged disc number is disc 1, the same reading
    /// AlbumTracklist gives it. So a two-disc rip where only the second disc was tagged still reads as CD 1
    /// and CD 2, not CD 0. Both album screens share this, so one box is not numbered two ways by two
    /// screens.</summary>
    private static uint DiscOf(LibraryTrack track) => track.DiscNumber == 0 ? 1u : track.DiscNumber;

    /// <summary>
    /// The line that opens a disc: the same caption the library's tracklist prints, off the same string, so
    /// one box is not numbered two ways by two screens.
    /// </summary>
    private static Border BuildDiscHeader(string caption, double scale) => new()
    {
        Margin = new Thickness(6 * scale, 12 * scale, 10 * scale, 2 * scale),
        BorderThickness = new Thickness(0, 0, 0, 1),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
        Padding = new Thickness(0, 0, 0, 5 * scale),
        Child = new TextBlock
        {
            Text = caption,
            FontSize = 11 * scale,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Opacity = 0.6,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        },
    };

    /// <summary>The caption itself: the disc's own title after its number where the release names it ("CD
    /// 2: B-Sides"), the number alone otherwise. A disc's tracks may not all carry the title, so the first
    /// one that does speaks for the disc.</summary>
    private static string DiscCaption(List<LibraryTrack> rows, uint disc)
    {
        var label = Strings.Loc.Format("Album_DiscHeader", disc);
        var subtitle = rows.Where(t => (t.DiscNumber == 0 ? 1u : t.DiscNumber) == disc)
                           .Select(t => t.DiscSubtitle)
                           .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
        return string.IsNullOrWhiteSpace(subtitle) ? label : $"{label}: {subtitle}";
    }

    /// <summary>
    /// The picture in the frame: a record's cover off its folder, or a list's own cover through its door.
    /// </summary>
    private FrameworkElement BuildSleeve(BrowseEntry entry, string root, int read)
    {
        var picture = new Image { Stretch = Stretch.UniformToFill };
        _albumSleeveArt = picture;
        if (entry.Cover is { } door)
            ShowListArtAsync(door, picture, read);
        else if (entry.Folder is { } folder)
            ShowSleeveAsync(picture, AbsoluteFolder(root, folder), read);
        return picture;   // nothing to show: the frame's own quiet square is the picture
    }

    /// <summary>
    /// A list's cover onto the drawer's sleeve, and the picture behind its frame onto the drawer's wash:
    /// the wash the list's own page stands on (PlaylistArt.Drawn.Wash), and else the cover blurred the way
    /// a record's is. Dropped if another record or list was opened in the drawer meanwhile.
    /// </summary>
    private async void ShowListArtAsync(ListCoverDoor door, Image target, int read)
    {
        try
        {
            var drawn = await DrawListCoverAsync(door);
            if (read != _albumRead)
                return;

            if (drawn.Cover is not { Length: > 0 } cover)
            {
                AlbumBackdropArt.ImageSource = null;   // no picture anywhere for this list
                return;
            }

            var bitmap = await DecodeArtAsync(cover, ArtScaler.TileEdge);
            if (read != _albumRead)
                return;
            target.Source = bitmap;

            var wash = drawn.Wash is { Length: > 0 } kept
                ? kept
                : await Task.Run(() => ArtScaler.Wash(cover));
            await PutWashAsync(wash, read);
        }
        catch
        {
            // A cover that would not draw or decode. The quiet square stands, as for a record.
        }
    }

    /// <summary>A folder as the library stores it, relative, forward slashes, where the disk wants
    /// it.</summary>
    private static string AbsoluteFolder(string root, string folder) =>
        Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// The big sleeve, decoded at a size worth showing.
    ///
    /// <para>It is NOT taken from the shelf's own cover cache. Those are 256 px thumbnails, cut for a 34 pt
    /// row, and this square is most of the width of the drawer. Drawn from one of those, it would be a
    /// sharp list beside a soft record. This is the one picture on the screen that is meant to be looked
    /// at, so it must not come from the smallest copy the app keeps.</para>
    ///
    /// <para>There is one picture per opening, so there is nothing to cache. The listener opens a record,
    /// looks at it, and goes somewhere else.</para>
    /// </summary>
    private async void ShowSleeveAsync(Image target, string folder, int read)
    {
        byte[]? bytes = null;
        try
        {
            bytes = await Task.Run(() => FolderArtWriter.FindFolderImage(folder) is { } image
                ? ArtScaler.Downscale(File.ReadAllBytes(image), ArtScaler.TileEdge)
                : null);
        }
        catch
        {
            // A folder that has gone, or a picture that will not read. The quiet square stays.
        }

        // Another record was opened while this one was being read off the disk.
        if (read != _albumRead || bytes is not { Length: > 0 })
            return;

        try
        {
            var stream = Presentation.Common.BitmapBytes.Stream(bytes);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (read == _albumRead)
                target.Source = bitmap;
        }
        catch
        {
            // Not a picture this decoder knows. The square stays quiet rather than half drawn.
        }
    }

    /// <summary>
    /// The wash behind the whole screen: this record's cover, blurred.
    ///
    /// <para>It is read off the disk at full size and blurred on the way back (see ArtScaler.Wash). It is
    /// not taken from the shelf's 256 px thumbnails, for the opposite reason to the sleeve. The sleeve
    /// needs the detail that those thumbnails have thrown away. The wash needs the colour. A thumbnail cut
    /// for a 34 pt row has already had its colour averaged into a handful of pixels by the same
    /// downscale.</para>
    ///
    /// <para>A record with no cover clears the wash rather than leaving the last one up. The drawer's own
    /// near-black is a finished surface. The colours of the wrong record behind the right record's name
    /// would be worse than no colours.</para>
    /// </summary>
    private async void ShowWashAsync(string? folder, int read)
    {
        byte[]? bytes = null;
        if (folder is not null)
        {
            try
            {
                bytes = await Task.Run(() => FolderArtWriter.FindFolderImage(folder) is { } image
                    ? ArtScaler.Wash(File.ReadAllBytes(image))
                    : null);
            }
            catch
            {
                // A folder that has gone, or a picture that will not read. The panel keeps its own colour.
            }
        }

        await PutWashAsync(bytes, read);
    }

    /// <summary>Puts a wash already blurred onto the drawer, or takes it off where there is none. Dropped
    /// if another record or list was opened meanwhile.</summary>
    private async Task PutWashAsync(byte[]? bytes, int read)
    {
        // Another record was opened while this one was being read.
        if (read != _albumRead)
            return;

        if (bytes is not { Length: > 0 })
        {
            AlbumBackdropArt.ImageSource = null;
            return;
        }

        try
        {
            var stream = Presentation.Common.BitmapBytes.Stream(bytes);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (read == _albumRead)
                AlbumBackdropArt.ImageSource = bitmap;
        }
        catch
        {
            AlbumBackdropArt.ImageSource = null;
        }
    }

    /// <summary>How long a record plays, the way the phone's album page says it.</summary>
    private static string Runtime(double seconds)
    {
        var total = TimeSpan.FromSeconds(seconds);
        return total.TotalHours >= 1
            ? Strings.Loc.Format("Stats_HoursMinutes", (int)total.TotalHours, total.Minutes)
            : Strings.Loc.Format("Stats_Minutes", Math.Max(1, (int)total.TotalMinutes));
    }

    /// <summary>
    /// The row of keys: play it, play it shuffled, and love it.
    ///
    /// <para>Play is the wide one and the others are square. That is the whole of the hierarchy. Most of
    /// the time the record was opened to be played from the top, and the shape of the row should show that
    /// before any of it is read.</para>
    ///
    /// <para>A playlist gets two of the three. The heart marks a RECORD. album_meta stores it, the wall
    /// draws its sticker from it, and the library header narrows the shelf to it. A playlist has no such
    /// row to mark. A key that could not write what it appears to write is worse than no key.</para>
    /// </summary>
    private Grid BuildDetailKeys(BrowseEntry entry, double scale)
    {
        var row = new Grid { Margin = new Thickness(0, 16 * scale, 0, 16 * scale), ColumnSpacing = 8 * scale };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var play = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10 * scale,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = "",   // play
                        FontFamily = (FontFamily)Resources["PegasusFa"],
                        FontSize = 13 * scale,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text = Strings.Loc.Get(entry.Folder is not null ? "AlbumGrid_PlayAll" : "Playlists_Play"),
                        FontSize = 14 * scale,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
            Height = 44 * scale,
            CornerRadius = new CornerRadius(8 * scale),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            MinWidth = 0,
            MinHeight = 0,
            Background = new SolidColorBrush(Color.FromArgb(0x2B, 0xFF, 0xFF, 0xFF)),
        };
        play.Click += (_, _) => PlayFromDrawer(_albumTracks, 0, AlbumSleeve());
        Grid.SetColumn(play, 0);
        _detailPlayKey = play;   // where the remote's focus starts on this screen

        var shuffle = Key("", Strings.Loc.Get("AlbumGrid_Shuffle"));   // random
        shuffle.Click += (_, _) => PlayShuffled();
        Grid.SetColumn(shuffle, 1);

        row.Children.Add(play);
        row.Children.Add(shuffle);

        // The record's own heart, which is a different mark from a song's. album_meta stores it, the wall's
        // sticker is drawn from it, and the header's toggle narrows the shelf to it.
        if (entry.Folder is { } folder)
        {
            var favorite = Key("", "");   // heart, dressed below
            favorite.Click += (_, _) => ToggleAlbumFavorite(folder, favorite, scale);
            PaintAlbumFavorite(favorite, Library.IsAlbumFavorite(folder, AlbumShelf.Own), scale);
            Grid.SetColumn(favorite, 2);
            row.Children.Add(favorite);
        }

        return row;

        Button Key(string glyph, string tip)
        {
            var key = new Button
            {
                Content = glyph,
                FontFamily = (FontFamily)Resources["PegasusFa"],
                FontSize = 14 * scale,
                Width = 52 * scale,
                Height = 44 * scale,
                CornerRadius = new CornerRadius(8 * scale),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                MinWidth = 0,
                MinHeight = 0,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(0x2B, 0xFF, 0xFF, 0xFF)),
            };
            if (tip.Length > 0)
                IconName.Set(key, tip);
            return key;
        }
    }

    /// <summary>
    /// The record, in an order nobody chose.
    ///
    /// <para>It is shuffled here and then played straight, rather than by turning on the player's own
    /// shuffle. That switch is a standing choice about everything that plays afterwards. This key is a
    /// one-off for this record. A listener who shuffles an album should not find the next album they pick
    /// coming out scrambled too.</para>
    /// </summary>
    private void PlayShuffled()
    {
        if (_albumTracks.Count == 0)
            return;

        var order = _albumTracks.ToList();
        for (var i = order.Count - 1; i > 0; i--)
        {
            var j = System.Random.Shared.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        PlayFromDrawer(order, 0, AlbumSleeve());
    }

    /// <summary>
    /// The record's heart, in the listener's OWN collection, which is what this wall lists, whatever the
    /// window behind it happens to be browsing. See <see cref="AlbumShelf"/>: a folder name off this wall
    /// and the same name off a borrowed shelf are two different records.
    /// </summary>
    private void ToggleAlbumFavorite(string folder, Button key, double scale)
    {
        var next = !Library.IsAlbumFavorite(folder, AlbumShelf.Own);
        if (Library.SetAlbumFavorite(folder, next, AlbumShelf.Own))
            PaintAlbumFavorite(key, next, scale);
    }

    /// <summary>Filled and lit while the record is loved, hollow while it is not, the same pair the song's
    /// own heart wears under the words.</summary>
    private void PaintAlbumFavorite(Button key, bool on, double scale)
    {
        key.FontFamily = (FontFamily)Resources[on ? "PegasusFa" : "PegasusFaRegular"];
        key.FontSize = 14 * scale;
        IconName.Set(key, Strings.Loc.Get(on ? "Album_UnsetFavorite" : "Album_SetFavorite"));
    }

    /// <summary>
    /// One line of the record: its number, its name, who it is by, and how long it runs.
    ///
    /// <para>Pressing it plays the record FROM there, not that song alone. Picking the fourth track off a
    /// sleeve means "start here", and a queue holding one song out of twelve would end four minutes later
    /// in silence.</para>
    ///
    /// <para>Pushing it sideways does the other two things anybody wants from a line: right to hear it
    /// after this one, left to keep it on a list. The row comes back wrapped in the frame that carries
    /// those marks, see PegasusChrome.Swipe.</para>
    /// </summary>
    private RemoteLine BuildDetailTrack(BrowseEntry entry, LibraryTrack track, int index, double scale)
    {
        var grid = new Grid { ColumnSpacing = 10 * scale };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28 * scale) });          // the number
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // the name
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                      // the time

        // A record numbers its songs, and a playlist numbers its positions. The seventh line of a playlist
        // is the seventh thing you will hear. Printing the track number that the song has on its own album
        // would put an unrelated number beside it.
        var number = new TextBlock
        {
            Text = (entry.Folder is not null && track.TrackNumber > 0
                ? track.TrackNumber
                : (uint)(index + 1)).ToString(),
            FontSize = 12 * scale,
            Opacity = 0.45,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(number, 0);

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        // The title and the explicit badge are on one line, so the badge sits after the last letter of the
        // name, not at the far end of the row. The queue's own rows do the same.
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 * scale };
        titleRow.Children.Add(new TextBlock
        {
            Text = track.Title ?? track.FileName,
            FontSize = 14 * scale,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (track.Advisory == ContentAdvisory.Explicit)
            titleRow.Children.Add(new ExplicitBadge
            {
                // Set here even though it is true only for explicit tracks. The badge starts collapsed and
                // shows itself when this property is set. A hand-built line has no binding to do that, so
                // without this the badge would be built and never shown.
                IsExplicit = true,
                Width = 13 * scale,
                Height = 13 * scale,
                VerticalAlignment = VerticalAlignment.Center,
                // The letter is cut out of the fill, so it must match the surface behind the badge. Here
                // that is the drawer's near-black, not the library's dark, which is the default.
                LetterBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x08, 0x08, 0x08)),
            });
        words.Children.Add(titleRow);

        // Under the title: who it is by, and, on a playlist (which crosses records), which record it came
        // off. On an album that second part would be the name at the top of this screen, written out a
        // dozen times down it.
        var under = entry.Folder is null && !string.IsNullOrWhiteSpace(track.Album)
            ? string.Join(" · ", new[] { track.Artist, track.Album }.Where(x => !string.IsNullOrWhiteSpace(x)))
            : track.Artist;

        if (!string.IsNullOrWhiteSpace(under))
            words.Children.Add(new TextBlock
            {
                Text = under,
                FontSize = 11.5 * scale,
                Opacity = 0.5,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        Grid.SetColumn(words, 1);

        var time = new TextBlock
        {
            Text = Duration(track.DurationSeconds),
            FontSize = 11.5 * scale,
            Opacity = 0.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(time, 2);

        grid.Children.Add(number);
        grid.Children.Add(words);
        grid.Children.Add(time);

        // A Border with its wash written by hand, like every other row in this drawer (see LightRow for
        // what a Button's own pointer-over state does to a list that scrolls under a still pointer).
        var row = new Border
        {
            Tag = index,
            Child = grid,
            Padding = new Thickness(6 * scale, 8 * scale, 10 * scale, 8 * scale),
            CornerRadius = new CornerRadius(6 * scale),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),

            // What a swipe moves. It is set from the moment the row is built, like the queue's own rows. A
            // gesture must never have to add one to a row the pointer is already on.
            RenderTransform = new TranslateTransform(),
        };
        row.PointerEntered += DrawerRow_PointerEntered;
        row.PointerExited += DrawerRow_PointerExited;
        row.PointerPressed += DetailTrack_PointerPressed;
        row.PointerMoved += DetailTrack_PointerMoved;
        row.PointerReleased += DetailTrack_Released;
        row.PointerCaptureLost += DetailTrack_CaptureLost;

        // Outside the frame, so the remote can stand on the line. OK plays from it, as a press does. A held
        // OK or the Menu key opens the song's menu, which is where a remote reaches the two things the
        // swipe does (play next, add to a playlist). It is outside the frame because the swipe reads the
        // frame as the row's parent (see MarksOf). The line takes no pointer input, so the swipe is
        // untouched.
        return new RemoteLine(
            SwipeFrame(row, scale),
            () => PlayFromDrawer(_albumTracks, index, AlbumSleeve()),
            () => index < _albumTracks.Count ? TrackMenu(_albumTracks[index]) : null,
            turn: null)
        {
            CornerRadius = row.CornerRadius,
        };
    }

    /// <summary>A track's length as a sleeve prints it.</summary>
    private static string Duration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{(int)span.TotalMinutes}:{span.Seconds:00}";
    }
}
