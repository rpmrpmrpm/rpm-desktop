using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace RPM.Desktop.Presentation.Import;

/// <summary>
/// One tool on the workbench's rail: an icon at rest, and its words when the pointer is on it.
///
/// <para>The rail has nine tools. Nine keys with labels would not fit in the pane, and nine plain icons
/// would be hard to learn. So each key shows an icon, and its label slides out when the pointer is on
/// it and folds back when the pointer leaves. This does the job of a tooltip inside the toolbar, rather
/// than in a separate box that appears late below the key.</para>
///
/// <para>The pointer is watched on the wrapper, not on the key inside it. A disabled control takes no
/// pointer input. The rail is disabled whenever nothing is ticked, which is the state the pane opens in.
/// A key must say what it does before it can be used, so the wrapper stays enabled and the label slides
/// out whether or not the tool can act.</para>
/// </summary>
public sealed partial class ToolKey : UserControl
{
    /// <summary>The room between the icon and its word. This amount is also taken off the measured label
    /// width, because a measured width includes the margin.</summary>
    private const double Gap = 7;

    /// <summary>The padding and rim ToolKeyStyle puts around the icon: 9 either side, 1 of border.</summary>
    private const double KeyChrome = 20;

    private static readonly FontFamily Fa =
        new("ms-appx:///Assets/Fonts/fa-solid-900.ttf#Font Awesome 5 Pro Solid");

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(ToolKey),
        new PropertyMetadata("", (d, e) => ((ToolKey)d)._glyph.Text = e.NewValue as string ?? ""));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(ToolKey),
        new PropertyMetadata("", (d, e) => ((ToolKey)d).OnLabelChanged(e.NewValue as string ?? "")));

    /// <summary>Whether the tool has something to act on. Not <see cref="UIElement.IsEnabled"/>: that
    /// is inherited by everything inside, and the wrapper has to stay live to keep watching the
    /// pointer while the key itself is off.</summary>
    public static readonly DependencyProperty CanActProperty = DependencyProperty.Register(
        nameof(CanAct), typeof(bool), typeof(ToolKey),
        new PropertyMetadata(true, (d, e) => ((ToolKey)d)._key.IsEnabled = (bool)e.NewValue));

    private readonly Button _key = new();
    private readonly TextBlock _glyph = new();
    private readonly TextBlock _label = new();
    private readonly Border _host = new();

    private Storyboard? _slide;
    private double _natural;

    public ToolKey()
    {
        // Hit-testable on purpose: a wrapper with no brush is not there as far as the pointer is
        // concerned, and the whole point of it is to be the thing the pointer lands on.
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        VerticalAlignment = VerticalAlignment.Center;

        _glyph.FontFamily = Fa;
        _glyph.FontSize = 12;
        _glyph.VerticalAlignment = VerticalAlignment.Center;

        _label.FontSize = 12;
        _label.Margin = new Thickness(Gap, 0, 0, 0);
        _label.TextWrapping = TextWrapping.NoWrap;
        _label.VerticalAlignment = VerticalAlignment.Center;

        // The label keeps its own full width and the host is what narrows, so what happens is a
        // slide out from behind the icon rather than a word being squeezed thinner.
        _host.Child = _label;
        _host.Width = 0;
        _host.VerticalAlignment = VerticalAlignment.Center;
        _host.SizeChanged += (_, e) => _host.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height),
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(_glyph);
        row.Children.Add(_host);

        _key.Content = row;
        _key.Style = (Style)Application.Current.Resources["ToolKeyStyle"];
        _key.Click += (_, e) => Click?.Invoke(this, e);
        Content = _key;

        PointerEntered += (_, _) => Slide(true);
        PointerExited += (_, _) => Slide(false);
        PointerCaptureLost += (_, _) => Slide(false);
    }

    /// <summary>Pressed. Forwarded from the key inside, so the rail wires up the way it always did.</summary>
    public event RoutedEventHandler? Click;

    /// <summary>The Font Awesome character this tool is drawn as.</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>What the tool is called. The words that slide out, and what a screen reader is told.</summary>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <inheritdoc cref="CanActProperty"/>
    public bool CanAct
    {
        get => (bool)GetValue(CanActProperty);
        set => SetValue(CanActProperty, value);
    }

    /// <summary>What the key opens, for the one that opens a menu rather than doing a thing.</summary>
    public FlyoutBase? Flyout
    {
        get => _key.Flyout;
        set => _key.Flyout = value;
    }

    /// <summary>How wide the key is with its label folded away: the icon, the padding and the rim. It is
    /// measured from the glyph, not from the control, so the rail can ask about a key that is not on
    /// screen. Whether a key fits is the question asked before it is shown.</summary>
    public double CollapsedWidth
    {
        get
        {
            _glyph.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return Math.Ceiling(_glyph.DesiredSize.Width) + KeyChrome;
        }
    }

    /// <summary>How much room the label needs when it slides out.</summary>
    public double LabelWidth => Natural();

    private void OnLabelChanged(string text)
    {
        _label.Text = text;
        _label.Width = double.NaN;
        _natural = 0;
        AutomationProperties.SetName(_key, text);
    }

    /// <summary>How wide the words are. The width is measured once, on the first hover, and then kept.
    /// It is not measured when the label is set, because the font is loaded from a file. A measurement
    /// taken before the font loads would measure the fallback font.</summary>
    private double Natural()
    {
        if (_natural > 0)
            return _natural;
        if (_label.Text.Length == 0)
            return 0;

        _label.Width = double.NaN;
        _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var wanted = Math.Ceiling(_label.DesiredSize.Width);
        if (wanted <= Gap)
            return 0;

        _natural = wanted;
        _label.Width = wanted - Gap;    // The measurement includes the margin, so take it off.
        return _natural;
    }

    private void Slide(bool open)
    {
        var to = open ? Natural() : 0;
        var from = _host.ActualWidth;

        _slide?.Stop();
        if (Math.Abs(to - from) < 0.5)
        {
            _host.Width = to;
            return;
        }

        // Stopping puts the width back to the last value written. The current width must be set again
        // before the next animation starts, or the fold-back starts from the fully open width.
        _host.Width = from;

        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(open ? 150 : 110)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(anim, _host);
        Storyboard.SetTargetProperty(anim, "Width");

        var board = new Storyboard();
        board.Children.Add(anim);
        board.Completed += (_, _) => _host.Width = to;
        _slide = board;
        board.Begin();
    }
}
