using RPM.Core.Import;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>One line of either preview list: the file, and what the pattern makes of it. This is shared
/// by the two windows, which are the same window twice. One reads the pattern from the file's name, and
/// the other reads it from the file's tags.
///
/// <para><paramref name="Current"/> is the value the field holds now. The window that overwrites a
/// field uses it, so the old value is shown beside the new one, and the user sees what will be lost.
/// The window that reads a file name has no old value, so it leaves this empty.</para>
///
/// <para>This is a namespace-level type, not a nested one. The binding engine's generated metadata only
/// covers types declared at namespace level. A nested type falls back to reflection.</para>
/// </summary>
[RpmBindable]
public sealed partial record TagPreviewRow(string FileName, string Result, Brush Ink, string Current = "");

/// <summary>Reads tags back out of the file names, using a pattern written in the same words as the
/// naming templates.
///
/// <para>It is a window, not a button, because the failure is silent and costly. A pattern that is one
/// dash out matches nothing, or worse, matches wrongly. Either way it would write over the tags of
/// every ticked file. So the whole effect is shown first: every file, what each field would become, and
/// the files it cannot read, which are marked and counted.</para>
/// </summary>
public sealed partial class FilenameTagsDialog : ContentDialog
{
    private static readonly Brush MatchedInk =
        new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));

    /// <summary>The amber the rest of the app uses for "this one does not agree" (see
    /// AlbumMarker).</summary>
    private static readonly Brush UnmatchedInk =
        new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xC0, 0x61));

    private readonly IReadOnlyList<TrackRow> _rows;
    private bool _applied;

    /// <summary>True while the pattern box and the preset list are being updated to match. This stops
    /// the one being written from rewriting the other.</summary>
    private bool _syncing;

    private FilenameTagsDialog(IReadOnlyList<TrackRow> rows)
    {
        _rows = rows;

        InitializeComponent();
        Shell.AttachResize(this, minWidth: 560, minHeight: 440, maxWidth: 900, defaultHeight: 540);

        ChipHost.Content = PatternFields.Chips(PatternBox, PatternFields.InNames, columns: 4);

        PresetBox.ItemsSource = PatternFields.Rows(FilenameTags.Presets);
        // Opens on the preset that reads the most of the ticked files (see FilenameTags.BestPreset).
        // Setting the selection also fills the pattern box, and that updates the preview.
        PresetBox.SelectedIndex = FilenameTags.BestPreset(_rows.Select(r => r.FilePath));
    }

    /// <summary>What the dialog wrote, or 0 when it was cancelled or nothing matched.</summary>
    public int Written { get; private set; }

    private void Preset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The last line of the list is the custom entry. Picking it changes nothing. It only shows that
        // the pattern was typed by hand.
        if (_syncing || PresetBox.SelectedItem is not TagPresetRow { Pattern.Length: > 0 } preset)
            return;

        _syncing = true;
        PatternBox.Text = preset.Pattern;
        _syncing = false;
        RefreshPreview();
    }

    private void Pattern_TextChanged(object sender, TextChangedEventArgs e)
    {
        MarkPreset();
        RefreshPreview();
    }

    /// <summary>Points the preset list at whatever the box now holds: one of the presets, or the custom
    /// line when the pattern is the user's own.</summary>
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

    /// <summary>Runs the pattern over every ticked file and shows the result. It runs on each
    /// keystroke, so it never throws. A half-typed pattern is the normal state of this box.</summary>
    private void RefreshPreview()
    {
        var pattern = PatternBox.Text ?? "";
        var preview = new List<TagPreviewRow>(_rows.Count);
        var matched = 0;

        foreach (var row in _rows)
        {
            var name = Path.GetFileName(row.FilePath);
            if (FilenameTags.Parse(row.FilePath, pattern) is not { Count: > 0 } fields)
            {
                preview.Add(new TagPreviewRow(name, Loc.Get("FilenameTags_NoMatch"), UnmatchedInk));
                continue;
            }

            matched++;
            // Field by field, in the order the pattern names them, so the line reads like the pattern.
            // Each field uses the app's own name, not the token.
            preview.Add(new TagPreviewRow(
                name,
                string.Join(", ", fields.Select(f => $"{PatternFields.FieldName(f.Key)}: {f.Value}")),
                MatchedInk));
        }

        PreviewList.ItemsSource = preview;
        SummaryText.Text = Loc.Format("FilenameTags_Summary", matched, _rows.Count);
        ApplyButton.IsEnabled = matched > 0;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Hide();

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        _applied = true;
        Written = Apply(_rows, PatternBox.Text ?? "");
        Hide();
    }

    /// <summary>Writes the parsed fields onto the rows.
    ///
    /// <para>Only the fields the pattern actually names are written. A pattern about the track and the
    /// title says nothing about the album. Clearing the album because the pattern did not mention it
    /// would lose good tags through a careless pattern. A row the pattern cannot read is skipped as a
    /// whole, and is never partly written.</para>
    /// </summary>
    internal static int Apply(IReadOnlyList<TrackRow> rows, string pattern)
    {
        var written = 0;
        foreach (var row in rows)
        {
            if (FilenameTags.Parse(row.FilePath, pattern) is not { Count: > 0 } fields)
                continue;

            foreach (var (name, value) in fields)
            {
                switch (name)
                {
                    case "artist": row.Artist = value; break;
                    case "albumartist": row.AlbumArtist = value; break;
                    case "album": row.Album = value; break;
                    case "title": row.Title = value; break;
                    // "03" is a track number, not text. The leading zero comes from the file name.
                    // Without Unpad it would go into the tag, and then back out into the library.
                    case "track": row.Track = Unpad(value); break;
                    case "disc": row.Disc = Unpad(value); break;
                    case "year": row.Year = value; break;
                    case "genre": row.Genre = value; break;
                }
            }
            written++;
        }
        return written;
    }

    private static string Unpad(string digits)
    {
        var trimmed = digits.TrimStart('0');
        return trimmed.Length > 0 ? trimmed : "0";
    }

    /// <summary>Asks for a pattern and writes what it reads. Returns how many rows were written, 0 when
    /// the window was closed without applying.</summary>
    public static async Task<int> ShowAsync(XamlRoot xamlRoot, IReadOnlyList<TrackRow> rows)
    {
        var dialog = new FilenameTagsDialog(rows) { XamlRoot = xamlRoot };
        await DialogHost.ShowAsync(dialog);
        return dialog._applied ? dialog.Written : 0;
    }
}
