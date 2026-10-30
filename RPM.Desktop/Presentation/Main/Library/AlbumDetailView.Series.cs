using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Desktop.Presentation.Main.Library.Models;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// The series a record is one volume of, on the record's own page: the line under the title, and the
/// row along the foot that shows every volume, the ones still to find included.
///
/// <para><b>The row is the suggestions' row, showing something else.</b> The foot already has one row of
/// sleeves, and the page is sized, folded and slid around it (see AlbumDetailView.Density and
/// SimilarSlide). A second row would need all of that twice, and a card too short for one row would have
/// to choose between two. So the two share the foot: each has a heading, the headings are tabs, and the
/// row shows whichever was pressed. A record in a series opens on its series, because the series is the
/// question this record raises first; the suggestions are one press away.</para>
/// </summary>
public sealed partial class AlbumDetailView
{
    private enum FootShows { Series, Similar }

    /// <summary>Which row the reader last asked for. It stays for the page's whole life, so a reader who
    /// prefers the suggestions keeps them as they walk through the volumes.</summary>
    private FootShows _footWanted = FootShows.Series;

    /// <summary>Which row the foot is showing now: the one asked for, where this record has it.</summary>
    private FootShows _footShowing = FootShows.Similar;

    private IReadOnlyList<SimilarRow> _similarRows = [];
    private IReadOnlyList<SeriesRow> _seriesRows = [];

    /// <summary>A series row is drawn once there is more on it than this record alone.</summary>
    private bool HasSeriesRow => _seriesRows.Count > 1;

    /// <summary>
    /// The series' row, built from the shelf the suggestions were just read from: every volume the
    /// library holds, in order, and an empty sleeve for every number still to find (see
    /// <see cref="SeriesShelf.Lay"/>). A record whose row lingers in the library's notes but whose
    /// tracks are gone is not on the shelf, so it is not counted as held.
    /// </summary>
    private void BuildSeriesRow(
        AlbumSeries? series, IReadOnlyList<(string Folder, AlbumSeries Series)> members,
        IReadOnlyList<AlbumTraits> shelf, string root, CancellationToken ct)
    {
        if (series is null)
        {
            _seriesRows = [];
            return;
        }

        var named = new Dictionary<string, AlbumTraits>(StringComparer.OrdinalIgnoreCase);
        foreach (var album in shelf)
            named.TryAdd(album.Folder, album);

        var laid = SeriesShelf.Lay([.. members.Where(m => named.ContainsKey(m.Folder))]);

        _seriesRows = [.. laid.Slots.Select(slot => slot.Folder is { } folder
            ? new SeriesRow(
                folder,
                AlbumTitle.Split(named[folder].Album).Name is { Length: > 0 } name ? name : named[folder].Album,
                named[folder].Artist,
                VolumeLabel(slot.Volume),
                slot.Volume ?? "",
                string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase),
                _similarMetrics)
            : new SeriesRow(
                null, Loc.Get("Album_SeriesStillToFind"), "", VolumeLabel(slot.Volume), slot.Volume ?? "",
                isHere: false, _similarMetrics))];

        SeriesHeading.Text = series.Name;
        SeriesCount.Text = laid.Missing > 0
            ? Loc.Format("Album_SeriesHaveMissing", laid.Have, laid.Missing)
            : Loc.Format("Album_SeriesHave", laid.Have);

        foreach (var row in _seriesRows.Where(r => r.IsHeld))
            _ = FillSleeveAsync(row, row.Folder, row.Artist, row.Title, root, ct);
    }

    private static string VolumeLabel(string? volume)
        => volume is { Length: > 0 } v ? Loc.Format("Album_SeriesVolumeNumber", v) : "";

    /// <summary>
    /// Puts whichever row the reader asked for into the foot, and both headings in their state. The fold
    /// and the row's place on the page are still <see cref="ShowSimilar"/>'s: this only says whether
    /// the foot has anything, and what.
    /// </summary>
    private void PaintFoot()
    {
        var hasSeries = HasSeriesRow;
        var hasSimilar = _similarRows.Count > 0;

        _footShowing = hasSeries && (_footWanted == FootShows.Series || !hasSimilar)
            ? FootShows.Series
            : FootShows.Similar;
        var series = _footShowing == FootShows.Series;

        // The template is changed with nothing in the list. Changing it under a live list would re-draw
        // every suggestion as a series card for one frame before the new cards arrive.
        SimilarList.ItemsSource = null;
        SimilarList.ItemTemplate = (DataTemplate)Resources[series ? "SeriesTemplate" : "SimilarTemplate"];
        SimilarList.ItemsSource = series ? _seriesRows : _similarRows;

        SeriesToggle.Visibility = hasSeries ? Visibility.Visible : Visibility.Collapsed;
        SimilarToggle.Visibility = hasSimilar ? Visibility.Visible : Visibility.Collapsed;
        SeriesToggle.Opacity = series ? 1 : 0.5;
        SimilarToggle.Opacity = series ? 0.5 : 1;
        SeriesChevron.Visibility = series ? Visibility.Visible : Visibility.Collapsed;
        SimilarChevron.Visibility = series ? Visibility.Collapsed : Visibility.Visible;

        _hasSimilar = hasSeries || hasSimilar;
        ShowSimilar();

        SimilarScroller.ChangeView(0, null, null, disableAnimation: true);
        if (series)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ScrollToHere);
    }

    /// <summary>A record forty volumes into its series opens with its own sleeve in view, a little in
    /// from the left so the volumes before it show that there are some.</summary>
    private void ScrollToHere()
    {
        if (_footShowing != FootShows.Series)
            return;

        var at = _seriesRows.Select((row, i) => (row, i)).FirstOrDefault(p => p.row.IsHere).i;
        var card = _similarMetrics.Edge + SimilarTileMetrics.Padding * 2 + 6;
        SimilarScroller.ChangeView(Math.Max(0, (at - 1) * card), null, null, disableAnimation: true);
    }

    /// <summary>A heading that is not showing puts its own row up, unfolding the foot if it was folded.
    /// The heading that is showing folds and unfolds, as it always has.</summary>
    private void SeriesToggle_Click(object sender, RoutedEventArgs e) => FootTab(FootShows.Series);

    private void FootTab(FootShows tab)
    {
        if (_footShowing == tab)
        {
            ToggleFoot();
            return;
        }

        _footWanted = tab;
        PaintFoot();
        UnfoldFoot();
    }

    /// <summary>Opens the foot if it is folded, with the same slide and the same remembered answer as a
    /// press on its heading.</summary>
    private void UnfoldFoot()
    {
        if (SimilarRowState() == SimilarState.Open)
            return;
        ToggleFoot();
    }

    /// <summary>The line under the title, pressed: the series' row, in view.</summary>
    private void ShowSeriesRow()
    {
        if (!HasSeriesRow)
            return;
        if (_footShowing != FootShows.Series)
        {
            _footWanted = FootShows.Series;
            PaintFoot();
        }
        UnfoldFoot();
    }

    /// <summary>A volume the library holds opens in this same page, as a suggestion does. The record
    /// already showing does nothing; neither does a volume still to find, which has no record.</summary>
    private void Series_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SeriesRow { IsHeld: true, IsHere: false, Folder: var folder })
            Load(folder, AlbumOpen.Fresh);
    }

    /// <summary>
    /// The line under the title: "Volume 43 of The Complete Mozart Edition". The series' name is a door,
    /// drawn the way the artist's name is in the line below it (see <see cref="PaintSubtitle"/>): in the
    /// line's own ink, underlined, cyan under the pointer.
    /// </summary>
    private void PaintSeriesLine(AlbumSeries? series)
    {
        SeriesText.Inlines.Clear();
        if (series is null)
        {
            SeriesText.Visibility = Visibility.Collapsed;
            return;
        }

        // The sentence is translated whole and the name found inside it, because "Volume {0} of {1}" puts
        // the name last in English and first in Chinese.
        var line = series.Volume is { } volume
            ? Loc.Format("Album_SeriesVolume", volume, series.Name)
            : Loc.Format("Album_SeriesPart", series.Name);
        var at = line.IndexOf(series.Name, StringComparison.Ordinal);

        if (at < 0)
        {
            SeriesText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = line });
        }
        else
        {
            if (at > 0)
                SeriesText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = line[..at] });

            var link = new Microsoft.UI.Xaml.Documents.Hyperlink { Foreground = SeriesText.Foreground };
            link.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = series.Name });
            link.Click += (_, _) => ShowSeriesRow();
            SeriesText.Inlines.Add(link);

            if (at + series.Name.Length < line.Length)
                SeriesText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = line[(at + series.Name.Length)..] });
        }

        SeriesText.Visibility = Visibility.Visible;
    }
}
