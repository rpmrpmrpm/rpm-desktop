using RPM.Core.Models;
using RPM.Desktop.Presentation.Main.Library.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// The drawer that opens on a record: its tracklist in one or two columns, the three ways to play
/// it, its sleeve, and the marks under the sleeve.
///
/// <para><b>This is a control, not a template, because there are two surfaces.</b> If it were
/// markup inside the album wall, every handler in it would be a method of the wall. An artist's page
/// could then not show one without copying the drawer, and a copied drawer becomes two drawers that
/// drift apart. What the record looks like on screen is set here. What a press means is
/// decided by the host, through the hooks below.</para>
///
/// <para><b>It draws a band and does not load one.</b> The cover, the tracks and the tint are loaded by
/// whoever owns the record. The wall has its own queue and priorities, and an artist's page reads two
/// shelves. Both write their answer onto the same <see cref="AlbumBand"/>. This control watches that
/// model and nothing else.</para>
/// </summary>
public sealed partial class AlbumBandView : UserControl
{
    public AlbumBandView() => InitializeComponent();

    /// <summary>The record this drawer is open on. A dependency property, because the wall binds it
    /// from inside a DataTemplate and rebinds it when a container is recycled for another record.</summary>
    public static readonly DependencyProperty BandProperty = DependencyProperty.Register(
        nameof(Band), typeof(AlbumBand), typeof(AlbumBandView),
        new PropertyMetadata(null, OnBandChanged));

    public AlbumBand? Band
    {
        get => (AlbumBand?)GetValue(BandProperty);
        set => SetValue(BandProperty, value);
    }

    /// <summary>A drawer pointed at a different record keeps the width it already has. The new
    /// tracklist is cut to the columns the drawer is in now, not to the columns of the last band. Without
    /// this, a recycled container shows a two-column split in a one-column drawer.</summary>
    private static void OnBandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AlbumBandView view && e.NewValue is AlbumBand band)
            band.TwoColumns = view._twoColumns;
    }

    // ── what a press does: the host decides, through these hooks ──

    /// <summary>Play one song off the list.</summary>
    public Action<TrackInfo>? PlayTrack { get; set; }

    /// <summary>Play the whole record, in order or shuffled.</summary>
    public Action<IReadOnlyList<TrackInfo>, bool>? PlayTracks { get; set; }

    /// <summary>Hand the whole record to the DJ.</summary>
    public Action<IReadOnlyList<TrackInfo>>? StartDj { get; set; }

    /// <summary>The heart under the sleeve. This control decides nothing. The page stores the mark and
    /// tells every view that shows the record. The mark then comes back to this band, the same way a
    /// mark made from the right-click menu does.</summary>
    public Action<AlbumMenuCommand, string>? MenuInvoked { get; set; }

    /// <summary>Everything a right-clicked song needs. It has its own set on purpose: a menu on a song
    /// and a menu on a record are menus on different things.</summary>
    public TrackMenuHooks? TrackMenuHooks { get; set; }

    /// <summary>Marks one song as a favourite, or clears the mark. It returns whether the library took
    /// the change, so a line fills in only when the mark was written.</summary>
    public Func<TrackInfo, bool, bool>? SetTrackFavorite { get; set; }

    /// <summary>Called when the drawer decides how many columns it is in. The wall keeps the last
    /// answer, so the NEXT drawer it shows starts in the same mode, rather than guessing and correcting
    /// a frame later.</summary>
    public Action<bool>? ColumnsChanged { get; set; }

    // ── How wide the sleeve is ──
    //
    // The cover sizes to the space it has, not to a fixed 196. It is a fraction of the drawer width,
    // capped so it is never taller than the tracklist beside it (a short album does not get a tall
    // cover), and never wider than BandArtMax. Below a minimum width it is hidden, so a narrow window
    // gives the whole width to the list. It reacts to the drawer's own SizeChanged, so it follows window
    // resizes. The open animation is a ScaleY on the host's root and does not change the cover size.

    public const double BandArtMax = 196;
    private const double BandArtMinVisible = 120;   // too little room for a cover, so it is hidden
    private const double BandArtShortFloor = 130;   // a short album still gets a usable cover, not a sliver
    private const double BandArtGap = 20;           // the cover's left margin
    private const double BandListMinWidth = 440;    // the width the two track columns need

    /// <summary>Whether this drawer is currently wide enough for two track columns.</summary>
    private bool _twoColumns = true;

    private void BandContent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The tracklist has priority. It keeps a minimum readable width, and the cover takes only what is
        // left over. The cover is capped by its maximum and by the height of the tracklist, but it never
        // goes below a floor for short albums. When what is left cannot fit even a minimum cover, the
        // names would have to clip to make room. So the cover hides completely and the list gets the
        // full width.
        var usable = Math.Max(0, e.NewSize.Width - 40);      // width inside the 20px content padding
        var forCover = usable - BandArtGap - BandListMinWidth;
        var heightCap = Math.Max(BandLeft.ActualHeight, BandArtShortFloor);
        var side = Math.Min(Math.Min(forCover, heightCap), BandArtMax);

        if (side < BandArtMinVisible)
        {
            // The cover is gone and the list has the whole drawer. Use two columns only while that is
            // wide enough to read them. Otherwise the list stacks into one column.
            BandArtColumn.Visibility = Visibility.Collapsed;
            SetColumns(usable >= BandListMinWidth);
            return;
        }

        BandArtColumn.Visibility = Visibility.Visible;
        // With a cover shown, the list always keeps BandListMinWidth (the cover only takes what is left),
        // so there is room for both columns.
        SetColumns(true);

        if (Math.Abs(BandArt.Width - side) > 0.5)
        {
            BandArt.Width = side;
            BandArt.Height = side;
        }
    }

    /// <summary>
    /// Puts the tracklist into one or two columns. The model cuts its lines again (the second column's
    /// items move under the first), and the elements follow. With one column, the spare column is
    /// removed and the list spans the full width. A stacked list uses the whole drawer, not half of it.
    ///
    /// <para>Public because a host that creates a drawer already knows how wide its drawers are. A drawer
    /// that waited for its own first SizeChanged would show one frame in the wrong mode.</para>
    /// </summary>
    public void SetColumns(bool twoColumns)
    {
        _twoColumns = twoColumns;
        if (Band is { } band)
            band.TwoColumns = twoColumns;

        Grid.SetColumnSpan(BandTrackLeft, twoColumns ? 1 : 2);
        BandTrackRight.Visibility = twoColumns ? Visibility.Visible : Visibility.Collapsed;

        ColumnsChanged?.Invoke(twoColumns);
    }

    // ── the presses ──

    private void Track_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AlbumTrackItem { Info: { } info })
            PlayTrack?.Invoke(info);
    }

    /// <summary>A right-click on one song in an opened record. It shows the song's own menu, not the
    /// record's menu, because the sleeve beside it already has the record's. An "Edit album" entry on
    /// a song would invite edits to every file on the record from one line.</summary>
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

    /// <summary>The pointer is over a line, so its empty heart appears and the mark can be made without
    /// a menu. The heart is hidden again when the pointer leaves.</summary>
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

    /// <summary>The heart itself. The line fills in at once, without waiting for the record to be read
    /// again. It fills in only if the library took the mark.</summary>
    private void TrackFavorite_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AlbumTrackItem { Info: { } info } line)
            return;
        var next = !line.IsFavorite;
        if (SetTrackFavorite?.Invoke(info, next) == true)
            line.IsFavorite = next;
    }

    private void PlayAll_Click(object sender, RoutedEventArgs e) => PlayBand(sender, shuffle: false);

    private void Shuffle_Click(object sender, RoutedEventArgs e) => PlayBand(sender, shuffle: true);

    private void PlayBand(object sender, bool shuffle)
    {
        if ((sender as FrameworkElement)?.Tag is not AlbumBand band || band.AllTracks.Count == 0)
            return;
        PlayTracks?.Invoke(band.AllTracks.Select(t => t.Info!).ToList(), shuffle);
    }

    private void Dj_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AlbumBand band || band.AllTracks.Count == 0)
            return;
        StartDj?.Invoke(band.AllTracks.Select(t => t.Info!).ToList());
    }

    /// <summary>The heart under the drawer's sleeve. This control decides nothing. See
    /// <see cref="MenuInvoked"/>.</summary>
    private void BandFavorite_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AlbumBand band)
            MenuInvoked?.Invoke(AlbumMenuCommand.ToggleFavorite, band.Card.Folder);
    }
}
