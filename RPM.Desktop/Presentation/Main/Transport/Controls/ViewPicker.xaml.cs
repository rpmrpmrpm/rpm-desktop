using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RPM.Desktop.Presentation.Main.Transport.Controls;

/// <summary>
/// The three ways RPM shows itself, offered as three scale models of themselves.
///
/// <para>It reports the choice and nothing else. <see cref="ViewModeService"/> decides which view is up,
/// and it tells this control the answer through <see cref="SetCurrent"/>. Like the other controls on the
/// deck, it asks, then paints whatever comes back.</para>
/// </summary>
internal sealed partial class ViewPicker : UserControl
{
    /// <summary>The edge around the model of the view being shown: the app's azure, running up to its cyan. It
    /// is the same pair as the mark under its name, and it is the only colour in the flyout that is not part
    /// of a picture.</summary>
    private static readonly Color LitFrom = Color.FromArgb(0xFF, 0x03, 0x71, 0xD0);
    private static readonly Color LitTo = Color.FromArgb(0xFF, 0x04, 0xC8, 0xFF);

    /// <summary>The thin edge around a view that can be chosen.</summary>
    private static readonly Color EdgeRest = Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF);

    private static readonly Color InkLit = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    private static readonly Color InkRest = Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF);

    /// <summary>How far back the two views you are not in are drawn. They are far enough back that the view you
    /// are in is seen first, and near enough that all three are still clear as pictures. The pictures are
    /// meant to tell the views apart without the words.</summary>
    private const double ArtRest = 0.55;

    public ViewPicker() => InitializeComponent();

    /// <summary>A card was pressed. The card of the current view does not raise this event. Going where you
    /// already are is not a move. A flyout that closed and did nothing would look like a missed press.</summary>
    public event Action<AppView>? Chosen;

    private AppView _current = AppView.Library;

    /// <summary>Paints the card of the view on screen and leaves the other two plain. It is called each time the
    /// flyout opens. It is not kept in step, because the flyout is closed most of the time.</summary>
    public void SetCurrent(AppView view)
    {
        _current = view;

        Paint(LibraryCard, LibraryFrame, LibraryArt, LibraryName, LibraryMark, view == AppView.Library);
        Paint(AmpCard, AmpFrame, AmpArt, AmpName, AmpMark, view == AppView.RPaMp);
        Paint(PegasusCard, PegasusFrame, PegasusArt, PegasusName, PegasusMark, view == AppView.FullScreen);

        static void Paint(Button card, Border frame, UIElement art, TextBlock name, UIElement mark, bool on)
        {
            // The edge, and only the edge: the frame keeps its thickness either way, so nothing in the
            // card moves between one view and the next.
            frame.BorderBrush = on
                ? new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(1, 1),
                    GradientStops =
                    {
                        new GradientStop { Offset = 0, Color = LitFrom },
                        new GradientStop { Offset = 1, Color = LitTo },
                    },
                }
                : new SolidColorBrush(EdgeRest);

            art.Opacity = on ? 1 : ArtRest;
            name.Foreground = new SolidColorBrush(on ? InkLit : InkRest);
            mark.Opacity = on ? 1 : 0;

            // It stays hit-testable, so the hover highlight and the tooltip still work. It does nothing when
            // pressed (see the Click handlers). Disabling it would grey out the whole card, and the card of the
            // view you are in should look the most active.
            card.IsTabStop = !on;
        }
    }

    private void Library_Click(object sender, RoutedEventArgs e) => Choose(AppView.Library);
    private void Amp_Click(object sender, RoutedEventArgs e) => Choose(AppView.RPaMp);
    private void Pegasus_Click(object sender, RoutedEventArgs e) => Choose(AppView.FullScreen);

    private void Choose(AppView view)
    {
        if (view == _current)
            return;
        Chosen?.Invoke(view);
    }
}
