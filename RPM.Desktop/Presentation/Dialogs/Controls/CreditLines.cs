using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Dialogs.Controls;

/// <summary>
/// A credit that can name more than one person, one person a line, with a + beside the last line for
/// another.
///
/// <para>A classical record credits a quartet's four players and an opera's six singers. Typed into one box
/// with semicolons between them the names run off its edge, and a mistake in the third is found by reading
/// along a line wider than the box. A line each keeps every name in view.</para>
///
/// <para>The value going in and coming out is the tag's own text, the entries joined with "; ", so every
/// window reads and writes it the way it reads any other box (see <see cref="TagField.Lines"/>). An empty
/// line is dropped on the way out, so a + pressed and left empty writes nothing.</para>
/// </summary>
internal sealed partial class CreditLines : UserControl
{
    private readonly StackPanel _lines = new() { Spacing = 4 };
    private readonly Button _add;
    private readonly List<TextBox> _boxes = [];

    /// <summary>The line the + stands on now. Kept rather than read off the button: an element's Parent is
    /// not reliable before it is on screen, and adding the button to a second line while the first still
    /// holds it fails with 0x800F1000 (the album editor would not open on a record with two soloists).</summary>
    private Grid? _addHost;

    public CreditLines(string? value)
    {
        _add = new Button
        {
            Content = new FontIcon
            {
                Glyph = "\uf067",   // plus
                Style = DenseForm.Res<Style>("FaIcon"),
                FontSize = 10,
            },
            Style = DenseForm.Res<Style>("DenseButtonStyle"),
            VerticalAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(8, 0, 8, 0),
        };
        IconName.Set(_add, Loc.Get("Details_AddLine"));
        _add.Click += (_, _) => AddLine("", focus: true);

        HorizontalAlignment = HorizontalAlignment.Stretch;
        Content = _lines;
        Write(value);
    }

    /// <summary>Raised when any line is typed in, added or emptied.</summary>
    public event Action? Changed;

    /// <summary>The lines as the tag's text: the filled ones, in order, joined with "; ".</summary>
    public string Read() =>
        string.Join("; ", _boxes.Select(b => b.Text.Trim()).Where(t => t.Length > 0));

    /// <summary>Puts a tag's text in, one entry a line. Always at least one line, so there is a box to type
    /// the first name into.</summary>
    public void Write(string? value)
    {
        _boxes.Clear();
        _addHost?.Children.Remove(_add);
        _addHost = null;
        _lines.Children.Clear();

        var entries = (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var entry in entries)
            AddLine(entry, focus: false);
        if (_boxes.Count == 0)
            AddLine("", focus: false);
    }

    private void AddLine(string text, bool focus)
    {
        var box = new TextBox { Text = text };
        box.TextChanged += (_, _) => Changed?.Invoke();
        _boxes.Add(box);

        // The + stands beside the last line only: it is a way to add one more, so it sits where the next
        // one will appear, and a column of them would offer the same thing once a line.
        var row = new Grid { ColumnSpacing = 4 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(box);

        _addHost?.Children.Remove(_add);
        Grid.SetColumn(_add, 1);
        row.Children.Add(_add);
        _addHost = row;

        _lines.Children.Add(row);

        if (focus)
        {
            box.Focus(FocusState.Programmatic);
            Changed?.Invoke();
        }
    }
}
