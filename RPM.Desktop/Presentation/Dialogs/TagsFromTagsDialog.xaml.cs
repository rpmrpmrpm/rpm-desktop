using RPM.Core.Import;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>
/// Builds one tag out of the others: a field to write, and a pattern in the app's own tokens saying
/// what to write into it. "{artist} - {title}" into the title, "{album} ({year})" into the comment.
///
/// <para>This is the counterpart of the tags-from-filename window, and the same window with a different
/// source. That window reads the pattern from the file's name. This one reads it from the file's own
/// tags. Both show every row they would touch before they change anything. A pattern that is one token
/// out would otherwise write over a field of every ticked file, with no way back.</para>
/// </summary>
public sealed partial class TagsFromTagsDialog : ContentDialog
{
    private static readonly Brush WrittenInk =
        new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));

    /// <summary>The amber the rest of the app uses for "this one is not like the others".</summary>
    private static readonly Brush EmptyInk =
        new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xC0, 0x61));

    /// <summary>A row where the pattern writes back the value already there. It counts as written, and
    /// it is dimmed. The rows that change are the ones the user needs to see, and many identical rows
    /// around them would hide those.</summary>
    private static readonly Brush UnchangedInk =
        new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));

    /// <summary>The fields offered as a target: everything a pattern can sensibly produce. Closed lists
    /// are left out, because an advisory flag has three values and a pattern cannot produce one of
    /// them. The lyric sheet is also left out, because nobody builds it from other fields.</summary>
    private static readonly IReadOnlyList<TagField> Targets =
    [
        .. TagFields.All.Where(f =>
            f.Options is null
            && f.Kind is not (TagFieldKind.Lyrics or TagFieldKind.RecordingId or TagFieldKind.ArtistId
                or TagFieldKind.ReleaseId or TagFieldKind.ReleaseGroupId or TagFieldKind.ReleaseArtistId)),
    ];

    private readonly IReadOnlyList<TrackRow> _rows;
    private bool _applied;

    /// <summary>True while the pattern box and the preset list are being made to agree. This stops the
    /// one being written from rewriting the other.</summary>
    private bool _syncing;

    private TagsFromTagsDialog(IReadOnlyList<TrackRow> rows)
    {
        _rows = rows;

        InitializeComponent();
        Shell.AttachResize(this, minWidth: 620, minHeight: 460, maxWidth: 900, defaultHeight: 560);

        ChipHost.Content = PatternFields.Chips(PatternBox, PatternFields.All, columns: 5);

        TargetBox.ItemsSource = Targets.Select(f => Loc.Get(f.LabelKey)).ToList();
        TargetBox.SelectedIndex = Targets.ToList().FindIndex(f => f.Kind == TagFieldKind.Title);

        PresetBox.ItemsSource = PatternFields.Rows(TagTemplate.Presets);
        PresetBox.SelectedIndex = 0;      // also fills the pattern box, and so the preview
    }

    /// <summary>What the dialog wrote, or 0 when it was cancelled or nothing came out.</summary>
    public int Written { get; private set; }

    private TagField Target => Targets[Math.Max(0, TargetBox.SelectedIndex)];

    private void Preset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The last line of the list is the typed-in value. Picking it changes nothing. It shows the
        // current state, and it is not a pattern of its own.
        if (_syncing || PresetBox.SelectedItem is not TagPresetRow { Pattern.Length: > 0 } preset)
            return;

        _syncing = true;
        PatternBox.Text = preset.Pattern;
        _syncing = false;
        RefreshPreview();
    }

    private void Target_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshPreview();

    private void Pattern_TextChanged(object sender, TextChangedEventArgs e)
    {
        MarkPreset();
        RefreshPreview();
    }

    /// <summary>Sets the preset list to match what the pattern box holds: one of the presets, or the
    /// line that says the pattern is the user's own.</summary>
    private void MarkPreset()
    {
        if (_syncing || PresetBox.ItemsSource is not List<TagPresetRow> presets)
            return;

        var pattern = PatternBox.Text ?? "";
        var at = presets.FindIndex(p => p.Pattern == pattern);

        _syncing = true;
        PresetBox.SelectedIndex = at >= 0 ? at : presets.Count - 1;
        _syncing = false;
    }

    /// <summary>Runs the pattern over every ticked row and shows what the field would become. It runs
    /// on each keystroke, so it must not throw. A half-typed pattern is the normal state of this
    /// box.</summary>
    private void RefreshPreview()
    {
        if (PatternBox is null || PreviewList is null)
            return;     // still being built: the two lists fill before the boxes exist

        var pattern = PatternBox.Text ?? "";
        var preview = new List<TagPreviewRow>(_rows.Count);
        var written = 0;

        var target = Target;
        foreach (var row in _rows)
        {
            var name = Path.GetFileName(row.FilePath);
            var now = target.Get(row) ?? "";
            var value = TagTemplate.Render(pattern, token => TagFields.TokenValue(row, token));
            if (value.Length == 0)
            {
                // When the pattern renders nothing, nothing is written. Clearing a field that already
                // has a value is not what "build it from the others" means.
                preview.Add(new TagPreviewRow(
                    name, Loc.Get("TagsFromTags_Empty"), EmptyInk, now));
                continue;
            }

            written++;
            preview.Add(new TagPreviewRow(
                name, value, value == now ? UnchangedInk : WrittenInk, now));
        }

        PreviewList.ItemsSource = preview;
        SummaryText.Text = Loc.Format("TagsFromTags_Summary", written, _rows.Count, Loc.Get(Target.LabelKey));
        ApplyButton.IsEnabled = written > 0;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Hide();

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        _applied = true;
        Written = Apply(_rows, Target, PatternBox.Text ?? "");
        Hide();
    }

    /// <summary>Writes the rendered value into the chosen field. A row the pattern renders empty for is
    /// left exactly as it was.</summary>
    internal static int Apply(IReadOnlyList<TrackRow> rows, TagField target, string pattern)
    {
        var written = 0;
        foreach (var row in rows)
        {
            var value = TagTemplate.Render(pattern, token => TagFields.TokenValue(row, token));
            if (value.Length == 0)
                continue;

            target.Set(row, value);
            written++;
        }
        return written;
    }

    /// <summary>Asks for a field and a pattern, and writes what it renders. Returns how many rows were
    /// written, 0 when the window was closed without applying.</summary>
    public static async Task<int> ShowAsync(XamlRoot xamlRoot, IReadOnlyList<TrackRow> rows)
    {
        var dialog = new TagsFromTagsDialog(rows) { XamlRoot = xamlRoot };
        await DialogHost.ShowAsync(dialog);
        return dialog._applied ? dialog.Written : 0;
    }
}
