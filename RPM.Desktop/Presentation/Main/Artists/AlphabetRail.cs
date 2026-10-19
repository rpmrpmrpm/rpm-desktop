using RPM.Core.Library;
using RPM.Desktop.Presentation.Main.Artists.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Main.Artists;

/// <summary>
/// The index down the right edge of the artists wall: A to Z, then a bucket for everything that
/// starts with neither.
///
/// <para>It works in both directions. Scrolling the wall lights the letter the wall is showing, so
/// the rail always shows where you are. Pressing a letter, or holding the pointer down and dragging
/// along the rail, moves the wall to that letter. The wall drives the rail and the rail drives the
/// wall. The two must never fight, so a jump lights its letter at once and does not wait for the
/// scroll it started to report back.</para>
///
/// <para>A letter with no artist under it is drawn faint and does nothing. An index that jumps to a
/// gap would be wrong, and the wall's letters change as the collection changes.</para>
///
/// <para>Built in code rather than in markup. The twenty-seven rows differ only in their character,
/// so a loop builds them. Each row has a fixed height, so a drag can find its letter by arithmetic
/// (see <see cref="LetterHeight"/>) instead of hit-testing twenty-seven elements.</para>
/// </summary>
public sealed partial class AlphabetRail : UserControl
{
    /// <summary>The height of one letter row. It is fixed for two reasons: the rail's total height
    /// is then predictable, and a drag turns the pointer's Y position into a letter by dividing by
    /// this value.</summary>
    private const double LetterHeight = 15;

    private readonly List<IndexLetter> _letters = [];
    private readonly StackPanel _column = new() { HorizontalAlignment = HorizontalAlignment.Center };

    /// <summary>A letter was reached, by a press or by a drag moving onto it. Raised only for letters
    /// with an artist under them, and only when the letter changes. Dragging down the rail therefore
    /// scrolls once per letter, not once per pointer report.</summary>
    public event Action<char>? Picked;

    public AlphabetRail()
    {
        foreach (var letter in ArtistWall.Alphabet)
        {
            var model = new IndexLetter(letter);
            _letters.Add(model);

            var block = new TextBlock
            {
                Text = model.Letter,
                FontSize = 9.5,
                Height = LetterHeight,
                DataContext = model,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,   // the rail takes every pointer, so a drag never falls between two letters
            };
            block.SetBinding(TextBlock.ForegroundProperty, new Microsoft.UI.Xaml.Data.Binding { Path = new PropertyPath("Ink") });
            block.SetBinding(TextBlock.FontWeightProperty, new Microsoft.UI.Xaml.Data.Binding { Path = new PropertyPath("Weight") });
            _column.Children.Add(block);
        }

        // The column sits in a transparent Border, not directly in the control. A UserControl's own
        // Background is not painted by its template, so the gaps between letters would let a press
        // pass through to the wall behind. Those gaps are most of the rail's area.
        Content = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            Child = _column,
        };
        Width = 22;
        VerticalAlignment = VerticalAlignment.Center;
        IsTabStop = false;

        PointerPressed += Rail_PointerPressed;
        PointerMoved += Rail_PointerMoved;
        PointerReleased += Rail_PointerReleased;
        PointerCaptureLost += (_, _) => _dragging = false;
    }

    private bool _dragging;
    private char _sent;

    /// <summary>Which letters the wall has an artist under. Other letters are drawn faint and do not
    /// respond.</summary>
    public void SetAvailable(IReadOnlySet<char> letters)
    {
        foreach (var letter in _letters)
            letter.Has = letters.Contains(letter.Letter[0]);
    }

    /// <summary>The letter the wall is showing. The wall sets it while it scrolls. This rail sets it
    /// at once when a letter is pressed, so a jump does not wait for the scroll it started.</summary>
    public void SetCurrent(char letter)
    {
        foreach (var model in _letters)
            model.Current = model.Letter[0] == letter;
    }

    private void Rail_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = CapturePointer(e.Pointer);
        _sent = '\0';
        Reach(e);
        e.Handled = true;
    }

    private void Rail_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
            return;
        Reach(e);
        e.Handled = true;
    }

    private void Rail_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    /// <summary>The letter under the pointer, if the wall has an artist under it and it is not the
    /// letter this drag sent last.</summary>
    private void Reach(PointerRoutedEventArgs e)
    {
        var y = e.GetCurrentPoint(_column).Position.Y;
        var index = (int)Math.Floor(y / LetterHeight);
        if (index < 0 || index >= _letters.Count)
            return;

        var model = _letters[index];
        if (!model.Has || model.Letter[0] == _sent)
            return;

        _sent = model.Letter[0];
        SetCurrent(_sent);
        Picked?.Invoke(_sent);
    }
}
