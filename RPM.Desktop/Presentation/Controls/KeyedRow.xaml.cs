using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Main.Home;

namespace RPM.Desktop.Presentation.Controls;

/// <summary>A row of cards that scrolls sideways only by its two keys. Its two ends fade out where there
/// are more cards past them.
/// </summary>
/// <remarks><b>The home screen's rows, as one control.</b> The key step and the hem depth come from the
/// home screen (<see cref="HomeView.KeyStep"/>, <see cref="HomeView.RowHemDepth"/>). So a row on the artist
/// page steps exactly as a row on home does. Home still builds its rows inline from the same parts.
///
/// <para><b>Two things keep the pointer off the row.</b> Both of the scroller's scroll modes are Disabled
/// (see the markup for why nothing else works). So a vertical notch over the cards cannot move the row. It
/// passes up to the page, which scrolls as smoothly there as anywhere else. A sideways notch (a trackpad
/// swipe, a tilted wheel) is taken and dropped. Unlike home, the page is never scrolled by hand. See
/// <see cref="Cards_PointerWheel"/>.</para>
///
/// <para>The cards go in as the content: <c>&lt;ctl:KeyedRow&gt;&lt;ItemsRepeater
/// …/&gt;&lt;/ctl:KeyedRow&gt;</c>. <see cref="Rail"/> is the scroller, for a page that has to read the
/// row's offset.</para>
/// </remarks>
[ContentProperty(Name = nameof(Cards))]
public sealed partial class KeyedRow : UserControl
{
    public KeyedRow()
    {
        InitializeComponent();
        PlaceKeys();
    }

    /// <summary>The cards: whatever the row scrolls. Usually an ItemsRepeater on a horizontal
    /// StackLayout.</summary>
    public UIElement? Cards
    {
        get => (UIElement?)GetValue(CardsProperty);
        set => SetValue(CardsProperty, value);
    }

    public static readonly DependencyProperty CardsProperty = DependencyProperty.Register(
        nameof(Cards), typeof(UIElement), typeof(KeyedRow),
        new PropertyMetadata(null, (d, e) => ((KeyedRow)d).TakeCards(e.OldValue as UIElement, e.NewValue as UIElement)));

    /// <summary>The space inside the scroller around the cards. This is the scroller's own
    /// Padding.</summary>
    public Thickness CardPadding
    {
        get => (Thickness)GetValue(CardPaddingProperty);
        set => SetValue(CardPaddingProperty, value);
    }

    public static readonly DependencyProperty CardPaddingProperty = DependencyProperty.Register(
        nameof(CardPadding), typeof(Thickness), typeof(KeyedRow),
        new PropertyMetadata(default(Thickness), (d, _) => ((KeyedRow)d).Scroller.Padding = ((KeyedRow)d).CardPadding));

    /// <summary>How far the picture on a card is from the top and bottom of the row. The two keys centre on
    /// the picture, not on the picture and its text together. Only Top and Bottom are read.
    /// </summary>
    public Thickness KeyBand
    {
        get => (Thickness)GetValue(KeyBandProperty);
        set => SetValue(KeyBandProperty, value);
    }

    public static readonly DependencyProperty KeyBandProperty = DependencyProperty.Register(
        nameof(KeyBand), typeof(Thickness), typeof(KeyedRow),
        new PropertyMetadata(default(Thickness), (d, _) => ((KeyedRow)d).PlaceKeys()));

    /// <summary>The row's scroller, for a page that reads where the row has been scrolled to.</summary>
    public ScrollViewer Rail => Scroller;

    /// <summary>The two hems, built on the first load. This is null where the machine's composition stack
    /// cannot draw them. The ends then have no fade.</summary>
    private ScrollHem? _hem;
    private bool _noHem;

    private void PlaceKeys()
    {
        // Two points in from each end, as on the home row.
        BackKey.Margin = new Thickness(2, KeyBand.Top, 0, KeyBand.Bottom);
        OnKey.Margin = new Thickness(0, KeyBand.Top, 2, KeyBand.Bottom);
    }

    private void TakeCards(UIElement? old, UIElement? cards)
    {
        if (old is FrameworkElement was)
            was.SizeChanged -= Cards_SizeChanged;
        if (old is not null)
            old.PointerWheelChanged -= Cards_PointerWheel;

        Scroller.Content = cards;

        // Attached to the content, not the scroller, so a sideways notch is taken before the scroller's own
        // handler sees it. See HomeView.HoldTheWheel.
        if (cards is FrameworkElement fits)
            fits.SizeChanged += Cards_SizeChanged;
        if (cards is not null)
            cards.PointerWheelChanged += Cards_PointerWheel;
    }

    private void Row_Loaded(object sender, RoutedEventArgs e)
    {
        if (_hem is null && !_noHem)
        {
            _hem = ScrollHem.Attach(Scroller, Cut, Head, Foot, Orientation.Horizontal, HomeView.RowHemDepth);
            _noHem = _hem is null;
        }

        Show();
    }

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e) => Show();

    private void Scroller_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e) => Show();

    /// <summary>The cards were laid out or measured again. That decides whether the row overflows at all.
    /// The scroller's own size did not change, so it says nothing about that.</summary>
    private void Cards_SizeChanged(object sender, SizeChangedEventArgs e) => Show();

    /// <summary>Sets both hems and both keys from one reading of the scroller. They must agree, because
    /// they all say whether there are cards past each end. Reading the scroller twice could give two
    /// answers.</summary>
    private void Show()
    {
        var (head, foot) = ScrollHem.Depths(
            Scroller.HorizontalOffset, Scroller.ScrollableWidth, HomeView.RowHemDepth);

        _hem?.Show(head, foot);
        BackKey.Visibility = head > 0 ? Visibility.Visible : Visibility.Collapsed;
        OnKey.Visibility = foot > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BackKey_Click(object sender, RoutedEventArgs e) => Step(-1);

    private void OnKey_Click(object sender, RoutedEventArgs e) => Step(1);

    /// <summary>One press scrolls by the home row's step (see <see cref="HomeView.KeyStep"/>). The card at
    /// the edge stays on screen, so the reader keeps their place.</summary>
    private void Step(int direction)
    {
        if (Scroller.ActualWidth <= 0)
            return;

        Scroller.ChangeView(
            Scroller.HorizontalOffset + (direction * HomeView.KeyStep(Scroller.ActualWidth)), null, null);
    }

    /// <summary>A notch over the cards. A vertical notch is left alone. It reaches the page's own scroller
    /// and scrolls the page as a notch does anywhere else on it. A sideways notch (a trackpad swipe, a
    /// tilted wheel) is taken and dropped. The row moves only by its keys.
    /// </summary>
    /// <remarks><b>The row does not scroll the page by hand.</b> Taking every notch and moving the
    /// page with <c>ChangeView(disableAnimation: true)</c> is what the home screen's rows do. On the
    /// artist page that is choppy. With the pointer on a cover, the page moved in one 48-point jump per
    /// notch, with nothing in between. The same notches over the track listing moved the page in steps of 1
    /// to 17 points, which was smooth. The page's own scroller eases a notch on the compositor. A jump
    /// written from this thread cannot do that.
    ///
    /// <para><b>Safe to let through only because the row cannot take it.</b> The scroller's
    /// HorizontalScrollMode and VerticalScrollMode are both Disabled. So it has no axis to turn the notch
    /// into, and the notch passes to the page. If either mode is enabled, a notch over the row moves the
    /// row again.</para>
    /// </remarks>
    private void Cards_PointerWheel(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsHorizontalMouseWheel)
            e.Handled = true;
    }
}
