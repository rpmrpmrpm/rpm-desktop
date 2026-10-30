using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RPM.Core.Library;
using RPM.Desktop.Presentation.Dialogs;
using RPM.Desktop.Presentation.Dialogs.Controls;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Import;

/// <summary>
/// The series a queued record is one volume of, in the tag panel: a box to pick or name the series, and
/// one for the volume.
///
/// <para><b>Only for a whole record</b>, like the disc well (see <see cref="ImportTagPanel.Record"/>). A
/// series belongs to the record and is held against it until the import gives it a folder, so part of an
/// album, or tracks from three, have nothing to hold it on.</para>
///
/// <para><b>Picked the way a genre is.</b> The box lists the series already named, in the library and in
/// this queue, and anything else can be typed, which is how a new series is started. An empty box, or
/// &lt;Clear&gt;, is a record standing alone. There is no separate yes or no, as there is in the album
/// editor, because a panel this narrow has no room for a question whose answer the name already
/// gives.</para>
/// </summary>
public sealed partial class ImportTagPanel
{
    /// <summary>The series names to offer. The workbench sets this beside <see cref="Record"/>.</summary>
    public IReadOnlyList<string> KnownSeries { get; set; } = [];

    private ComboBox? _seriesName;
    private TextBox? _seriesVolume;

    /// <summary>What the two boxes showed when they were filled in, so an edit is a comparison, as it is for
    /// every other box in the panel (see Entry.Touched).</summary>
    private string _seriesLoaded = "";

    private void AddSeries(DenseFormGroup group)
    {
        if (Record is not { } record)
            return;

        _seriesName = MassEditDialog.SuggestBox(KnownSeries, record.Series?.Name);
        _seriesName.PlaceholderText = Loc.Get("AlbumEdit_SeriesNameHint");
        _seriesName.TextSubmitted += (_, _) => RefreshButtons();
        _seriesName.SelectionChanged += (_, _) => RefreshButtons();
        _seriesName.LostFocus += (_, _) => RefreshButtons();
        AddRow(group, "AlbumEdit_SeriesName", _seriesName);

        _seriesVolume = new TextBox
        {
            Text = record.Series?.Volume ?? "",
            PlaceholderText = Loc.Get("AlbumEdit_SeriesVolumeHint"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _seriesVolume.TextChanged += (_, _) => RefreshButtons();
        AddRow(group, "AlbumEdit_SeriesVolume", _seriesVolume);

        _seriesLoaded = SeriesShown();

        static void AddRow(DenseFormGroup group, string key, FrameworkElement input)
        {
            var label = DenseForm.Label(key);
            label.HorizontalAlignment = HorizontalAlignment.Left;
            group.Add(tick: null, label, input);
        }
    }

    /// <summary>The series the boxes say, or null for none.</summary>
    private AlbumSeries? SeriesNow()
        => _seriesName is null ? null
           : AlbumSeries.Of(MassEditDialog.ReadSuggest(_seriesName), _seriesVolume?.Text);

    private string SeriesShown()
        => SeriesNow() is { } s ? $"{s.Name}\u0001{s.Volume}" : "";

    private bool SeriesTouched => _seriesName is not null && SeriesShown() != _seriesLoaded;
}
