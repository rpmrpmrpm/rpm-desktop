using RPM.Core.Library;
using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Dialogs.Controls;

/// <summary>
/// The tick under the release type that adds the type's note to every track title: "(Live)" for a live record,
/// "(Mixed)" for a mixtape (see <see cref="TitleNote"/>). The album editor and the import's tag panel both show
/// it, so a record can be marked while it is being imported and again at any time after.
///
/// <para><b>Shown only where it can do something.</b> It appears while the release type box names a type with
/// a note, and only when at least one title does not already carry that note. A tick that would change nothing
/// is not offered. A hidden tick is never applied, whatever it was left at.</para>
///
/// <para><b>It follows the type.</b> Its words name the note the type box now asks for. Changing the type to
/// another one with a note clears the tick: a tick given for "(Live)" is not an answer about "(Mixed)".</para>
///
/// <para><b>It is an edit like any other.</b> Nothing is written when it is ticked. The window it sits in
/// writes it on Save or Apply, with the rest of the form.</para>
/// </summary>
internal sealed class TitleNoteOffer
{
    private readonly ComboBox _type;
    private readonly IReadOnlyList<TagFieldOption> _options;
    private readonly CheckBox _tick;
    private readonly TextBlock _words;
    private IReadOnlyList<string> _titles;

    /// <summary>The note the type box asks for now, or null when its type has none.</summary>
    private TitleNote? _note;

    /// <param name="type">The release type's choice box. The offer follows it.</param>
    /// <param name="titles">The titles the tracks carry now, to know whether any still lacks the note.</param>
    /// <param name="changed">Called when the tick or its visibility changes, for a window that keeps its
    /// buttons in step with its pending edits.</param>
    public TitleNoteOffer(
        ComboBox type, IReadOnlyList<TagFieldOption> options, IEnumerable<string> titles, Action? changed = null)
    {
        _type = type;
        _options = options;
        _titles = [.. titles];

        // The tag panel's input column is narrow. The words wrap rather than run off its edge.
        _words = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _tick = new CheckBox { MinWidth = 0, MinHeight = 0, Content = _words };
        MassEditDialog.StyleCheck(_tick);
        _tick.Checked += (_, _) => changed?.Invoke();
        _tick.Unchecked += (_, _) => changed?.Invoke();
        type.SelectionChanged += (_, _) =>
        {
            Refresh();
            changed?.Invoke();
        };

        var host = new StackPanel { Spacing = 4 };
        host.Children.Add(type);
        host.Children.Add(_tick);
        Host = host;

        Refresh();
    }

    /// <summary>The release type box with the tick under it. Placed in the form where the box alone would
    /// have gone.</summary>
    public FrameworkElement Host { get; }

    /// <summary>The note Save or Apply should add, or null: ticked, and still offered.</summary>
    public TitleNote? Wanted => _tick.Visibility == Visibility.Visible && _tick.IsChecked == true ? _note : null;

    /// <summary>Starts again over the titles as they are now, after a window has written the note. The
    /// tick clears, and hides when every title now carries it.</summary>
    public void Reset(IEnumerable<string> titles)
    {
        _titles = [.. titles];
        _tick.IsChecked = false;
        Refresh();
    }

    private void Refresh()
    {
        var note = TitleNote.For(ReleaseTypes.FromStored(MassEditDialog.ReadChoice(_type, _options)));
        if (!ReferenceEquals(note, _note))
        {
            _tick.IsChecked = false;
            _note = note;
        }

        if (note is not null)
        {
            _words.Text = Loc.Format("Edit_MarkTitles", note.Text);
            ToolTipService.SetToolTip(_tick, Loc.Format("Edit_MarkTitlesTip", note.Text));
        }

        var unmarked = note is not null && _titles.Any(t => note.Marked(t) != t);
        _tick.Visibility = unmarked ? Visibility.Visible : Visibility.Collapsed;
    }
}
