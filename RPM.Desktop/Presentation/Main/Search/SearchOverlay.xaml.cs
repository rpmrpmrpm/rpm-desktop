using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using RPM.Core.Diagnostics;

namespace RPM.Desktop.Presentation.Main.Search;

/// <summary>
/// The search bar that Cmd or Ctrl+F opens over the whole window, with the library search under it.
/// </summary>
/// <remarks>
/// <para><b>The keyboard stays in the box.</b> The first answer is lit as soon as there is one; Up and Down
/// move the light, Enter does what a press on the lit row does, and Escape puts the search away. Typing goes on
/// while a row is lit.</para>
///
/// <para><b>It is the app's one library search.</b> Cmd or Ctrl+F and the deck's search key open it
/// (ShellPage.SearchLibrary), and the page gives it the lists it searches and the answers to a choice
/// (MainPage.FocusSearch).</para>
///
/// <para><b>Put away, it gives the keyboard back</b> to whatever had it when it opened, before the box is
/// hidden: after Escape, a press on the shade or on the panel's catcher, and after a choice. A focused box that
/// is hidden makes the framework move the keyboard to the first control in the window and draw its focus frame
/// there. Not when the window changes to another view (<see cref="Close"/>).</para>
/// </remarks>
public sealed partial class SearchOverlay : UserControl
{
    /// <summary>What had the keyboard when the bar opened. Null while the bar is put away.</summary>
    private Control? _returnTo;

    /// <summary>Whether the search going away gives the keyboard back. Only <see cref="Close"/> turns it off,
    /// for the one close it is asked for.</summary>
    private bool _giveKeyboardBack = true;

    /// <summary>How the keyboard is given back. Escape gives it back as a key would, with the focus frame
    /// drawn, because the person is working with the keys. A choice or a press gives it back as a pointer
    /// would, with no frame: the page has changed, and a frame on the control that had the keyboard before
    /// marks a control nobody is using.</summary>
    private FocusState _giveBackAs = FocusState.Pointer;

    public SearchOverlay()
    {
        InitializeComponent();

        Results.Closed += Results_Closed;
        Results.RecentPicked += asked =>
        {
            Field.Text = asked;
            Field.SelectionStart = asked.Length;
        };

        // On focus too: a box that was collapsed when it loaded had no template to look in then.
        Field.Loaded += (_, _) => HideOwnClearKey(Field);
        Field.GotFocus += (_, _) => HideOwnClearKey(Field);
    }

    /// <summary>The search under the bar, for the page to give its lists and its answers to a choice.</summary>
    public LibrarySearchView Answers => Results;

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>
    /// Puts the bar up over the window and the keyboard in it, searching <paramref name="sources"/>. Asked
    /// again while it is up, it selects what is typed, so the next letters replace it.
    /// </summary>
    public void Open(LibrarySearchSources sources)
    {
        if (IsOpen)
        {
            Field.SelectAll();
            var took = Field.Focus(FocusState.Programmatic);
            Said.Line("search", $"bar: already up, the words selected, the box {(took ? "has" : "did not take")} the keyboard");
            return;
        }

        _returnTo = XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as Control : null;

        // Drawn clear from its first frame. A storyboard sets its From value only when it next ticks, so
        // without this the bar shows once at full strength before it fades in.
        _rise?.Stop();
        Shade.Opacity = 0;
        Bar.Opacity = 0;
        BarShift.Y = -8;

        Visibility = Visibility.Visible;
        UpdateLayout();
        Results.Open(sources, Bar);
        Rise();
        Said.Line("search", $"bar: up, {ActualWidth:0}x{ActualHeight:0}, the bar {Bar.ActualWidth:0} wide");

        // After the layout this turn makes: a box that was collapsed a moment ago cannot take the keyboard yet.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsOpen)
                return;
            var took = Field.Focus(FocusState.Programmatic);
            Said.Line("search", $"bar: the box {(took ? "has" : "did not take")} the keyboard");
        });
    }

    /// <summary>
    /// Puts the bar away, with nothing chosen. <paramref name="giveKeyboardBack"/> says whether what had the
    /// keyboard before it opened gets it again: yes for Escape and a press on the shade, no when the window
    /// changes to another view.
    /// </summary>
    public void Close(bool giveKeyboardBack)
    {
        if (!IsOpen)
            return;

        _giveKeyboardBack = giveKeyboardBack;

        // Raises Closed, which takes the bar down (Results_Closed).
        Results.Close();
    }

    /// <summary>The search went away: after a choice, from the panel's catcher, or from <see cref="Close"/>.
    /// The keyboard goes back first (see the remarks), then the bar goes, and empties, so it opens on a new
    /// search.</summary>
    private void Results_Closed()
    {
        var back = _giveKeyboardBack ? _returnTo : null;
        var gave = back is { IsLoaded: true, Visibility: Visibility.Visible } && back.Focus(_giveBackAs);
        Said.Line("search", $"bar: down, the keyboard {(gave ? "given back" : "not given back")}");

        _giveKeyboardBack = true;
        _giveBackAs = FocusState.Pointer;
        _returnTo = null;
        Visibility = Visibility.Collapsed;
        Field.Text = "";
    }

    /// <summary>The bar comes down a few points and up from clear, with the shade, in the time a menu takes. The
    /// panel under it does the same on its own (LibrarySearchView.Rise).</summary>
    private void Rise()
    {
        var board = new Storyboard();
        _rise = board;

        foreach (var (target, property, from, to, ms) in new (DependencyObject, string, double, double, int)[]
                 {
                     (Shade, "Opacity", 0, 1, 140),
                     (Bar, "Opacity", 0, 1, 140),
                     (BarShift, "Y", -8, 0, 180),
                 })
        {
            var step = new DoubleAnimation
            {
                From = from, To = to, Duration = TimeSpan.FromMilliseconds(ms),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(step, target);
            Storyboard.SetTargetProperty(step, property);
            board.Children.Add(step);
        }

        // Landed: the values stay, and the storyboard lets go of them, so the next Open can set them clear.
        board.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_rise, board))
                return;
            board.Stop();
            Shade.Opacity = 1;
            Bar.Opacity = 1;
            BarShift.Y = 0;
            _rise = null;
        };

        board.Begin();
    }

    /// <summary>The bar's fade in, while it runs.</summary>
    private Storyboard? _rise;

    private void Shade_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Close(giveKeyboardBack: true);
        e.Handled = true;
    }

    private void Field_TextChanged(object sender, TextChangedEventArgs e)
    {
        ClearKey.Visibility = Field.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Emptied on the way out (Results_Closed): nothing to search.
        if (Results.IsOpen)
            Results.Ask(Field.Text);
    }

    /// <summary>On the way down, before the box acts on the key: in a box of one line, Up and Down would
    /// move the caret to the start or the end.</summary>
    private void Field_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                _giveBackAs = FocusState.Programmatic;
                Close(giveKeyboardBack: true);
                break;
            case Windows.System.VirtualKey.Down:
                Results.MoveLight(1);
                break;
            case Windows.System.VirtualKey.Up:
                Results.MoveLight(-1);
                break;
            case Windows.System.VirtualKey.Enter:
                Results.ChooseLit();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void ClearKey_Click(object sender, RoutedEventArgs e)
    {
        Field.Text = "";
        Field.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Takes the TextBox's own clear key out of the box: the cross beside it (ClearKey) clears, and two crosses
    /// side by side read as a mistake.
    /// </summary>
    /// <remarks>Zero wide rather than collapsed: the template's visual states set the key's Visibility as the
    /// box takes and loses the keyboard, and would bring a collapsed one back. Nothing sets its width.</remarks>
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
}
