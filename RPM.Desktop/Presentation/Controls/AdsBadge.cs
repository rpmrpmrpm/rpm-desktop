using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Controls;

/// <summary>The mark that says a station carries advertising. It is shown beside the station's name. It is
/// built to match <see cref="ExplicitBadge"/>, so the two look like one family where a list shows both.
///
/// <para><b>What it claims, and what it does not.</b> A station gets this mark when its own directory entry
/// says it is commercial or a public broadcaster (see <see cref="RPM.Core.Radio.RadioAds"/>). Most stations
/// say nothing either way. So a missing mark means nothing. It is not a promise of no advertising, and no
/// text in the app may suggest that it is.</para>
///
/// <para>The mark is drawn, not typed. That keeps it one shape at every size and in every locale. It takes
/// its fill from <see cref="Microsoft.UI.Xaml.Controls.Control.Foreground"/>, like every other glyph
/// here.</para>
///
/// <para><see cref="LetterBrush"/> is the colour of the letters cut out of that fill. It must match the
/// surface the badge sits on, not the text beside it. See the note on ExplicitBadge.</para>
///
/// <para>A list binds <see cref="HasAds"/> and the badge hides itself. Binding
/// <see cref="Microsoft.UI.Xaml.UIElement.Visibility"/> instead does not work. A virtualized row that is
/// realized before its data arrives resolves the binding to nothing. The default Visibility is Visible, so
/// every empty row would show the badge.</para>
/// </summary>
public partial class AdsBadge : Control
{
    public AdsBadge()
    {
        DefaultStyleKey = typeof(AdsBadge);
        Visibility = Visibility.Collapsed;
        IsTabStop = false;   // A picture, not a control the user presses. See ExplicitBadge.
    }

    public static readonly DependencyProperty HasAdsProperty =
        DependencyProperty.Register(
            nameof(HasAds), typeof(bool), typeof(AdsBadge),
            new PropertyMetadata(false, OnHasAdsChanged));

    /// <summary>Whether the station this sits beside carries advertising. It drives the badge's own
    /// visibility, so a list binds this and nothing else.</summary>
    public bool HasAds
    {
        get => (bool)GetValue(HasAdsProperty);
        set => SetValue(HasAdsProperty, value);
    }

    private static void OnHasAdsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((AdsBadge)d).Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;

    public static readonly DependencyProperty LetterBrushProperty =
        DependencyProperty.Register(
            nameof(LetterBrush), typeof(Brush), typeof(AdsBadge), new PropertyMetadata(null));

    /// <summary>The colour of the letters. The letters show the surface behind the fill.</summary>
    public Brush LetterBrush
    {
        get => (Brush)GetValue(LetterBrushProperty);
        set => SetValue(LetterBrushProperty, value);
    }
}
