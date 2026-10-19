using RPM.Core.Library;
using RPM.Core.Models;
using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Import;

/// <summary>
/// The workbench's filter bar: six chips and two lists that say what the queue is narrowed to.
///
/// <para>A chip shows the count of the rows it is about. That count is for the WHOLE queue, not only the
/// rows that are shown. A chip that counted only the rows the other chips had not hidden would drop to
/// zero as soon as it was pressed. That would look like a broken button, not a narrowed list.</para>
///
/// <para>The toolbar decides whether the strip is on screen, not this control. The filter key there shows
/// the strip and also shows how many filters are on, so a filter that is switched on is always visible.</para>
/// </summary>
public sealed partial class ImportFilterBar : UserControl
{
    /// <summary>One chip, described by its three parts: its control, its word, and the count it is about.
    /// The chips are kept as a list so that the chip row and the folded menu are built from ONE description.
    /// Two hand-written lists would drift apart the first time a chip was added.</summary>
    private sealed record Chip(
        ToggleButton Button, string LabelKey, Func<QueueChipCounts, int> Count, string Glyph, string MarkKey);

    private static readonly FontFamily Fa =
        new("ms-appx:///Assets/Fonts/fa-solid-900.ttf#Font Awesome 5 Pro Solid");

    private readonly List<Chip> _chips;

    /// <summary>True while the filter is being written in from outside, so rebuilding the controls to
    /// match it does not raise a change back at the caller that set it.</summary>
    private bool _loading;

    private QueueFilter _filter = QueueFilter.None;
    private QueueChipCounts _counts;

    /// <summary>The sources offered, in the order the list shows them. Index 0 of the list is "Any",
    /// so a selection at n is this list's n-1.</summary>
    private IReadOnlyList<string> _sources = [];
    private IReadOnlyList<string> _kinds = [];

    public ImportFilterBar()
    {
        InitializeComponent();

        // Each chip shows the same mark as the rows it counts, in that mark's own colour. The grid shows
        // "this row is a question mark", and the chip shows "there are 623 of those". The glyph is the same
        // in both places, so the two do not use different symbols for one fact.
        _chips =
        [
            new(ChipNotIdentified, "Queue_ChipNotIdentified", c => c.NeedsRecognition, "\uf059", "QueueMarkMissingBrush"),
            new(ChipDuplicates, "Queue_ChipDuplicates", c => c.Duplicates, "\uf0c5", "QueueMarkDuplicateBrush"),
            new(ChipUnreadable, "Queue_ChipUnreadable", c => c.Unreadable, "\uf071", "QueueMarkUnreadableBrush"),
            new(ChipSplit, "Queue_ChipSplitCandidates", c => c.SplitCandidates, "\uf51f", "QueueMarkAlbumFileBrush"),
            new(ChipNoData, "Queue_ChipNoData", c => c.NoData, "\uf0c2", "QueueMarkDatalessBrush"),
            new(ChipTicked, "Queue_ChipTicked", c => c.Ticked, "\uf14a", "QueueMarkTickBrush"),
        ];

        Refresh();
    }

    /// <summary>Raised when the user changes the filter. It is not raised when <see cref="Filter"/> is set
    /// from outside.</summary>
    public event Action<QueueFilter>? FilterChanged;

    /// <summary>What the bar is showing. Setting it re-reads every control; it does not raise.</summary>
    public QueueFilter Filter
    {
        get => _filter;
        set
        {
            _filter = value ?? QueueFilter.None;
            _loading = true;
            try
            {
                ChipNotIdentified.IsChecked = _filter.NeedsRecognition;
                ChipDuplicates.IsChecked = _filter.HideDuplicates;
                ChipUnreadable.IsChecked = _filter.Unreadable;
                ChipSplit.IsChecked = _filter.SplitCandidates;
                ChipNoData.IsChecked = _filter.NoData;
                ChipTicked.IsChecked = _filter.TickedOnly;
                SourceBox.SelectedIndex = _filter.Source is { } s ? IndexOfSource(s) : 0;
                KindBox.SelectedIndex = _filter.Extension is { } e ? IndexOfKind(e) : 0;
            }
            finally
            {
                _loading = false;
            }
            Refresh();
        }
    }

    /// <summary>How much of the queue each chip is about. Read with the tree, not per chip.</summary>
    public void SetCounts(QueueChipCounts counts)
    {
        _counts = counts;
        Refresh();
    }

    /// <summary>The sources actually behind the queued rows. A queue out of one folder gets no list:
    /// there is nothing to choose between.</summary>
    public void SetSources(IReadOnlyList<string> sources)
    {
        _sources = sources;
        Fill(SourceBox, sources, "Queue_FilterSource");
        SourceHost.Visibility = SourceBox.Visibility;
    }

    /// <summary>The file kinds actually in the queue, ".flac" and the rest, dot dropped for reading.</summary>
    public void SetKinds(IReadOnlyList<string> kinds)
    {
        _kinds = kinds;
        Fill(KindBox, kinds.Select(k => k.TrimStart('.').ToUpperInvariant()).ToList(), "Queue_FilterKind");
    }

    private void Fill(ComboBox box, IReadOnlyList<string> values, string headerKey)
    {
        // One entry is not a choice. Two or more, and the list leads with "Any" so there is a way back.
        if (values.Count < 2)
        {
            box.Visibility = Visibility.Collapsed;
            return;
        }

        _loading = true;
        try
        {
            box.PlaceholderText = Loc.Get(headerKey);
            box.ItemsSource = new[] { Loc.Get("Queue_FilterAny") }.Concat(values).ToList();
            box.SelectedIndex = 0;
            box.Visibility = Visibility.Visible;
        }
        finally
        {
            _loading = false;
        }
    }

    private int IndexOfSource(MetadataSource source)
    {
        var name = source.ToString();
        for (var i = 0; i < _sources.Count; i++)
        {
            if (string.Equals(_sources[i], name, StringComparison.OrdinalIgnoreCase))
                return i + 1;
        }
        return 0;
    }

    private int IndexOfKind(string extension)
    {
        for (var i = 0; i < _kinds.Count; i++)
        {
            if (string.Equals(_kinds[i], extension, StringComparison.OrdinalIgnoreCase))
                return i + 1;
        }
        return 0;
    }

    private void Chip_Click(object sender, RoutedEventArgs e) => Raise();

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => Raise();

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        // The search is not a chip, and clearing the chips does not clear it. A user who types a word and then
        // presses a chip expects the word to stay.
        Filter = new QueueFilter { Search = _filter.Search };
        FilterChanged?.Invoke(_filter);
    }

    private void Raise()
    {
        if (_loading)
            return;

        _filter = new QueueFilter
        {
            Search = _filter.Search,
            NeedsRecognition = ChipNotIdentified.IsChecked == true,
            HideDuplicates = ChipDuplicates.IsChecked == true,
            Unreadable = ChipUnreadable.IsChecked == true,
            SplitCandidates = ChipSplit.IsChecked == true,
            NoData = ChipNoData.IsChecked == true,
            TickedOnly = ChipTicked.IsChecked == true,
            Source = SourceBox.SelectedIndex > 0
                && Enum.TryParse<MetadataSource>(_sources[SourceBox.SelectedIndex - 1], true, out var s)
                    ? s
                    : null,
            Extension = KindBox.SelectedIndex > 0 ? _kinds[KindBox.SelectedIndex - 1] : null,
        };

        Refresh();
        FilterChanged?.Invoke(_filter);
    }

    /// <summary>How many filters are switched on. The toolbar's filter key reads this: it is the one
    /// number that says the grid is showing less than it holds.</summary>
    public int FiltersOn { get; private set; }

    /// <summary>Puts every chip's word, count and state back on it.</summary>
    private void Refresh()
    {
        var on = 0;
        foreach (var chip in _chips)
        {
            var count = chip.Count(_counts);
            var lit = chip.Button.IsChecked == true;
            if (lit)
                on++;

            chip.Button.Content = Label(chip, count, lit);
            // A chip already on stays pressable at zero, or there would be no way to turn it off. One
            // that is off and about nothing goes away: a button that can only ever do nothing is worse
            // than no button at all.
            chip.Button.Visibility = count > 0 || lit ? Visibility.Visible : Visibility.Collapsed;
            chip.Button.Background = Res(lit ? "ChipOnFaceBrush" : "ToolKeyFaceBrush");
            chip.Button.BorderBrush = Res(lit ? "ChipOnRimBrush" : "ToolKeyRimBrush");
            chip.Button.Foreground = Res(lit ? "ChipOnInkBrush" : "ToolKeyInkBrush");
        }

        FiltersOn = on;
        ClearButton.Visibility = _filter.AnyChip ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A chip's face: the grid's own mark, the word, then how many it is about, dimmer and in
    /// brackets. Runs in one line rather than three controls, so the count reads as a count and not as
    /// part of the name, and the mark sits on the word's baseline the way it does in a row.</summary>
    private static TextBlock Label(Chip chip, int count, bool lit)
    {
        var block = new TextBlock { Style = StyleFor("ToolKeyLabelStyle") };
        block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
        {
            // The gap belongs to the WORD, not to the mark: a space set in the icon face is drawn at
            // that face's idea of a space, which for Font Awesome is nothing at all.
            Text = chip.Glyph,
            FontFamily = Fa,
            FontSize = 11,
            // Lit, the whole chip is one ink: a mark in its own colour on a filled face reads as a
            // second state on top of the first.
            Foreground = Res(lit ? "ChipOnInkBrush" : chip.MarkKey),
        });
        block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "  " + Loc.Get(chip.LabelKey) });
        if (count > 0)
        {
            block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = $"  ({count})",
                Foreground = Res(lit ? "ChipOnInkBrush" : "ChipCountInkBrush"),
            });
        }
        return block;
    }


    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private static Style StyleFor(string key) => (Style)Application.Current.Resources[key];
}
