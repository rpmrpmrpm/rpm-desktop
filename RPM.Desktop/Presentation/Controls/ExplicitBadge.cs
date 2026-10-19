using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Controls;

/// <summary>The E that marks explicit lyrics. It is shown beside a song's or a record's title. It is the
/// same mark the streaming services use. Its shape is the character it stands in for: a letter cut out of a
/// filled square.
///
/// <para>It is drawn, not typed. The character renders differently in different fonts. Some fonts draw a
/// plain boxed E, some draw a colour emoji, and some draw nothing where no font has the glyph. That would
/// give three different marks across the app's five languages. A control is one mark everywhere. It takes
/// its size from the line it sits on, and its fill from
/// <see cref="Microsoft.UI.Xaml.Controls.Control.Foreground"/>, like every other glyph here.</para>
///
/// <para><see cref="LetterBrush"/> is the letter cut out of that fill. So it must match the surface behind
/// the badge, not the text beside it. It defaults to the dark colour that the library's lists are drawn
/// on.</para>
///
/// <para>A list binds <see cref="IsExplicit"/> and the badge hides itself. This keeps the badge out of the
/// layout on the rows that have no mark, which is most rows. Binding
/// <see cref="Microsoft.UI.Xaml.UIElement.Visibility"/> directly does not work. A virtualized list realizes
/// rows before their data has arrived. The binding then finds no <c>IsExplicit</c> on the placeholder for
/// the row. A binding that resolves to nothing leaves the property at its default. For Visibility the
/// default is Visible, so every unloaded row showed an E. A bool defaults to false. So the same failure now
/// reads as "not explicit", which is the right answer for a row with no data.</para>
/// </summary>
public partial class ExplicitBadge : Control
{
    public ExplicitBadge()
    {
        DefaultStyleKey = typeof(ExplicitBadge);
        // A picture, not a control the user presses. A Control takes keyboard focus by default. On the TV
        // remote, focus stopped on this badge beside a list's name. So focus is turned off.
        IsTabStop = false;

        // Hidden until the binding sets a value. See the note above about rows that hold no data yet.
        Visibility = Visibility.Collapsed;
    }

    public static readonly DependencyProperty IsExplicitProperty =
        DependencyProperty.Register(
            nameof(IsExplicit), typeof(bool), typeof(ExplicitBadge),
            new PropertyMetadata(false, OnIsExplicitChanged));

    /// <summary>Whether the track this badge sits beside is marked explicit. It drives the badge's own
    /// visibility, so a list binds this and nothing else.</summary>
    public bool IsExplicit
    {
        get => (bool)GetValue(IsExplicitProperty);
        set => SetValue(IsExplicitProperty, value);
    }

    private static void OnIsExplicitChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ExplicitBadge)d).Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;

    public static readonly DependencyProperty LetterBrushProperty =
        DependencyProperty.Register(
            nameof(LetterBrush), typeof(Brush), typeof(ExplicitBadge), new PropertyMetadata(null));

    /// <summary>The colour of the E itself. The E shows the surface through its fill.</summary>
    public Brush LetterBrush
    {
        get => (Brush)GetValue(LetterBrushProperty);
        set => SetValue(LetterBrushProperty, value);
    }
}
