using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;

namespace RPM.Desktop.Presentation.Main.Search;

/// <summary>
/// The library search's box in a view's heading. Folded, it is the magnifier key alone; pressed, it slides
/// out to the left into a box and takes the keyboard. It only types: the page opens
/// <see cref="LibrarySearchView"/> under it and hands it what is typed.
/// </summary>
/// <remarks>
/// <b>It grows to the left because every heading it is in is laid out from the right</b>: the controls sit
/// at the heading's right edge, so a wider first control pushes its own left edge out and leaves the keys to
/// its right where they are. The width is animated in layout and not as a transform, so the heading's title
/// and the panel under it follow the edge as it moves.
/// </remarks>
public sealed partial class LibrarySearchBox : UserControl
{
    /// <summary>The folded width: the magnifier key and the slot's hairline.</summary>
    private const double FoldedWidth = 28;

    /// <summary>How long the slide takes, each way.</summary>
    private static readonly TimeSpan SlideTime = TimeSpan.FromMilliseconds(200);

    private double _openWidth = 150;
    private Storyboard? _sliding;

    public LibrarySearchBox()
    {
        InitializeComponent();
        Field.Loaded += (_, _) => HideOwnClearKey(Field);
    }

    /// <summary>
    /// Takes the TextBox's own clear key out of the box: the cross beside it (ClearKey) clears and folds,
    /// and two crosses side by side in a small box read as a mistake.
    /// </summary>
    /// <remarks>Zero wide rather than collapsed: the template's visual states set the key's Visibility as
    /// the box takes and loses the keyboard, and would bring a collapsed one back. Nothing sets its
    /// width.</remarks>
    private static void HideOwnClearKey(DependencyObject root)
    {
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Button { Name: "DeleteButton" } own)
            {
                own.Width = 0;
                own.MinWidth = 0;
                own.Padding = new Thickness(0);
                own.Margin = new Thickness(0);
                own.IsTabStop = false;
                own.Opacity = 0;
                return;
            }

            HideOwnClearKey(child);
        }
    }

    /// <summary>The width the box slides out to. The heading sets it (the album header narrows it on a
    /// narrow pane), and an open box follows it at once.</summary>
    public double OpenWidth
    {
        get => _openWidth;
        set
        {
            _openWidth = value;
            if (IsUnfolded && _sliding is null)
                Width = value;
        }
    }

    /// <summary>Whether the box is out, or sliding out.</summary>
    public bool IsUnfolded { get; private set; }

    /// <summary>The words in the box.</summary>
    public string Text
    {
        get => Field.Text;
        set
        {
            if (Field.Text == value)
                return;
            Field.Text = value;
            Field.SelectionStart = value.Length;
        }
    }

    /// <summary>The typing surface, for Ctrl+F (<c>FieldFocus</c>).</summary>
    internal TextBox Box => Field;

    /// <summary>The words changed.</summary>
    public event Action<LibrarySearchBox, string>? Typed;

    /// <summary>The box took the keyboard: the search opens, on the recent searches.</summary>
    public event Action<LibrarySearchBox>? Entered;

    /// <summary>The box gave the keyboard away.</summary>
    public event Action<LibrarySearchBox>? Left;

    /// <summary>Escape, or the cross: the search goes away and the box empties and folds.</summary>
    public event Action<LibrarySearchBox>? Dismissed;

    /// <summary>Down or Enter: the keyboard goes into what was found.</summary>
    public event Action<LibrarySearchBox>? IntoAnswers;

    /// <summary>Slides the box out and puts the keyboard in it.</summary>
    public void Unfold()
    {
        if (!IsUnfolded)
        {
            IsUnfolded = true;
            Field.Visibility = Visibility.Visible;
            Slide(OpenWidth, () => { });
        }

        // After the layout this turn makes: a box that was collapsed a moment ago cannot take the keyboard yet.
        DispatcherQueue.TryEnqueue(() => Field.Focus(FocusState.Programmatic));
    }

    /// <summary>Empties the box and slides it back to the magnifier.</summary>
    public void Fold()
    {
        Field.Text = "";
        if (!IsUnfolded)
            return;

        IsUnfolded = false;
        ClearKey.Visibility = Visibility.Collapsed;
        Slide(FoldedWidth, () =>
        {
            if (!IsUnfolded)
                Field.Visibility = Visibility.Collapsed;
        });
    }

    private void Slide(double to, Action landed)
    {
        _sliding?.Stop();

        var from = double.IsNaN(Width) ? ActualWidth : Width;
        var grow = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = SlideTime,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,   // Width is laid out, which is the point: see the remarks
        };
        Storyboard.SetTarget(grow, this);
        Storyboard.SetTargetProperty(grow, "Width");

        var board = new Storyboard();
        board.Children.Add(grow);
        board.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_sliding, board))
                return;
            _sliding = null;

            // Held at its base value, so a later OpenWidth from the heading is not overruled by a finished
            // animation still holding the property.
            board.Stop();
            Width = IsUnfolded ? OpenWidth : FoldedWidth;
            landed();
        };

        _sliding = board;
        board.Begin();
    }

    private void IconKey_Click(object sender, RoutedEventArgs e)
    {
        if (IsUnfolded)
            Field.Focus(FocusState.Programmatic);
        else
            Unfold();
    }

    private void Field_TextChanged(object sender, TextChangedEventArgs e)
    {
        ClearKey.Visibility = Field.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Typed?.Invoke(this, Field.Text);
    }

    private void Field_GotFocus(object sender, RoutedEventArgs e)
    {
        // Again here: a box that was folded when it loaded had no template to look in then.
        HideOwnClearKey(Field);
        Entered?.Invoke(this);
    }

    private void Field_LostFocus(object sender, RoutedEventArgs e) => Left?.Invoke(this);

    private void Field_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                Dismissed?.Invoke(this);
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Down:
            case Windows.System.VirtualKey.Enter:
                IntoAnswers?.Invoke(this);
                e.Handled = true;
                break;
        }
    }

    private void ClearKey_Click(object sender, RoutedEventArgs e) => Dismissed?.Invoke(this);
}
