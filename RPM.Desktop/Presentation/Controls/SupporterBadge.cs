using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RPM.Desktop.Services.Account;
using RPM.Desktop.Strings;
using RPM.Desktop.Presentation.Common;

namespace RPM.Desktop.Presentation.Controls;

/// <summary>The mark beside a supporter's name: a heart and the word, struck in gold.
///
/// <para><b>Gold, not the app's cyan. Gold is used nowhere else in rpm.</b> Cyan marks "this is the one you
/// picked": the lit backdrop tile and the current row. A badge in cyan would look like a selection. Gold is
/// used nowhere else, so it means one thing wherever it appears. It is the same gold as the supporter's
/// backdrop.</para>
///
/// <para><b>The glimmer does not animate.</b> It is a fixed diagonal sheen across the pill, as a metal
/// surface catches a light that is not moving. A moving shine would have to run from
/// CompositionTarget.Rendering. Uno's Skia renderer has no dirty rectangles. So every frame would repaint
/// the whole window, forever, for a pill forty pixels wide. The badge sits on a profile page and in a list
/// of shared libraries. Both are otherwise still.</para>
///
/// <para><b>It only ever appears.</b> There is no "not a supporter" state to draw. A badge that said so
/// would be an insult with a tick beside it. So the control is collapsed for everybody else, and it takes
/// no room in the line it sits on.</para>
///
/// <para><b>By default it asks, rather than being told.</b> Everything else in the app that shows the
/// standing has to be given it, and then kept in step. This badge reads
/// <see cref="SupporterService.Standing"/> when it loads. It follows <see cref="SupporterService.Changed"/>
/// until it unloads. So putting the badge in a new place takes one element in the markup and no
/// code-behind. Support bought or lapsed during a session reaches every badge on screen at once, including
/// the debug switch. That is the point of testing it at all.</para>
///
/// <para><b>Unless it is about somebody else.</b> A row that leads to a friend's shelf shows THEIR mark.
/// This machine's account has nothing to do with it. So that caller sets <see cref="ReadsAccount"/> to
/// false, and binds <see cref="IsSupporter"/> instead. This is a bool, not a Visibility binding. The reason
/// is in the remarks on <see cref="ExplicitBadge"/>. A virtualized row is realized before its data arrives.
/// A Visibility binding that resolves to nothing keeps the property's default, which is Visible. Then every
/// empty row would wear a badge. False is the right answer for a row that holds nothing.</para>
///
/// <para>It is composed, not templated, unlike <see cref="ExplicitBadge"/> and <see cref="AdsBadge"/>.
/// Those two DRAW a letterform, which has to be geometry to look like one mark at every size and in every
/// locale. This is a pill with a glyph and a translated word in it. That is plain markup. A template and a
/// generic style would be two more files that say what four elements already say.</para>
/// </summary>
public sealed partial class SupporterBadge : BorderBase
{
    private readonly TextBlock _word;

    public SupporterBadge()
    {
        Visibility = Visibility.Collapsed;
        CornerRadius = new CornerRadius(9);
        Padding = new Thickness(8, 2, 8, 2);
        VerticalAlignment = VerticalAlignment.Center;
        // The metal runs across the pill, not down it. A struck surface catches its light on a diagonal.
        // The stops are uneven on purpose. Bronze is in the corner the light turns away from. Then comes
        // gold, then a narrow band of champagne where the light lands, then gold again. An even ramp looks
        // like a painted gradient. The tight pair at 0.42 and 0.52 is what makes it look like a surface.
        // The gold is the same as the backdrop (GOLD_PALETTE in scripts/make_vinyl_table.py).
        Background = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 1),
            EndPoint = new Windows.Foundation.Point(1, 0),
            GradientStops =
            {
                new GradientStop { Color = Gold(0x3A, 0x9A, 0x6A, 0x18), Offset = 0.00 },
                new GradientStop { Color = Gold(0x46, 0xEF, 0xAF, 0x12), Offset = 0.30 },
                new GradientStop { Color = Gold(0x72, 0xEB, 0xD0, 0x7A), Offset = 0.42 },
                new GradientStop { Color = Gold(0x46, 0xEF, 0xAF, 0x12), Offset = 0.52 },
                new GradientStop { Color = Gold(0x38, 0x9A, 0x6A, 0x18), Offset = 1.00 },
            },
        };

        // The rim catches the light too, one stop brighter. That keeps the pill from looking like a flat
        // shape with a picture painted inside it.
        BorderBrush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 1),
            EndPoint = new Windows.Foundation.Point(1, 0),
            GradientStops =
            {
                new GradientStop { Color = Gold(0x66, 0xB9, 0x8B, 0x1E), Offset = 0.00 },
                new GradientStop { Color = Gold(0xCC, 0xEB, 0xD0, 0x7A), Offset = 0.45 },
                new GradientStop { Color = Gold(0x66, 0xB9, 0x8B, 0x1E), Offset = 1.00 },
            },
        };
        BorderThickness = new Thickness(1);

        // The lettering is the lightest gold, not white. White on gold looks like a label stuck to metal.
        // The mark should look as if it were struck from the same piece.
        var ink = new SolidColorBrush(Gold(0xFF, 0xF2, 0xDC, 0x9A));

        _word = new TextBlock
        {
            Text = Loc.Get("Rpm_Profile_PlanSupporter"),
            FontSize = 10,
            Foreground = ink,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new FontIcon
                {
                    Style = (Style)Application.Current.Resources["FaIcon"],
                    Glyph = "\uf004",   // fa-heart, the same one every supporter thing wears
                    FontSize = 9,
                    Foreground = ink,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                _word,
            },
        };

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>The word beside the heart. It defaults to "Supporter". A caller with less room can clear it
    /// and keep only the heart.</summary>
    public string Word
    {
        get => _word.Text;
        set
        {
            _word.Text = value ?? "";
            _word.Visibility = string.IsNullOrEmpty(_word.Text) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>Whether this badge decides for itself by reading the account. True for the badge beside
    /// YOUR name. False where it is somebody else's, and <see cref="IsSupporter"/> gives the
    /// answer.</summary>
    public static readonly DependencyProperty ReadsAccountProperty =
        DependencyProperty.Register(
            nameof(ReadsAccount), typeof(bool), typeof(SupporterBadge),
            new PropertyMetadata(true, OnToldChanged));

    public bool ReadsAccount
    {
        get => (bool)GetValue(ReadsAccountProperty);
        set => SetValue(ReadsAccountProperty, value);
    }

    /// <summary>Whether the person this badge is about supports rpm. It is read only when
    /// <see cref="ReadsAccount"/> is false.</summary>
    public static readonly DependencyProperty IsSupporterProperty =
        DependencyProperty.Register(
            nameof(IsSupporter), typeof(bool), typeof(SupporterBadge),
            new PropertyMetadata(false, OnToldChanged));

    public bool IsSupporter
    {
        get => (bool)GetValue(IsSupporterProperty);
        set => SetValue(IsSupporterProperty, value);
    }

    private static void OnToldChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SupporterBadge)d).Dress();

    private SupporterService? _supporter;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Resolved on load, not in the constructor. A control made by the XAML parser can be built before
        // the host is up. A badge is not worth a startup order that could go wrong.
        if (!ReadsAccount)
        {
            // Being told. There is nothing to subscribe to and nothing to look up. A row in a list must not
            // hold the account service open, and there may be a hundred of these.
            Dress();
            return;
        }

        _supporter ??= App.Services?.GetService(typeof(SupporterService)) as SupporterService;
        if (_supporter is null) return;

        _supporter.Changed += OnStandingChanged;
        Dress();

        // Ask as well, in case nothing this session has read the account yet. The call is cheap. It is what
        // makes the badge appear on a cold start, and not only after some other screen has read the
        // account.
        _ = _supporter.GetAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_supporter is null) return;
        _supporter.Changed -= OnStandingChanged;
    }

    private void OnStandingChanged() => DispatcherQueue.TryEnqueue(Dress);

    /// <summary>One of the metal's colours at a given opacity. Naming it makes the stops above read as a
    /// palette, not as twenty loose bytes.</summary>
    private static Windows.UI.Color Gold(byte alpha, byte r, byte g, byte b) =>
        Microsoft.UI.ColorHelper.FromArgb(alpha, r, g, b);

    private void Dress() =>
        Visibility = Wears() ? Visibility.Visible : Visibility.Collapsed;

    private bool Wears() => ReadsAccount
        ? SupporterReach.For(_supporter?.Standing).MayWearTheBadge
        : IsSupporter;
}
