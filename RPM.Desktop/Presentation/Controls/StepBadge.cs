using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace RPM.Desktop.Presentation.Controls;

/// <summary>The mark in front of one step of a tool. It is a ring with the step's number, or a filled cyan
/// disc with a tick once the step is done.
///
/// <para>Code-only, like <see cref="CloudBadge"/>. It has two elements and one write per state change. The
/// step decides whether it is done. The badge only draws the answer.</para>
/// </summary>
public sealed partial class StepBadge : Grid
{
    private static readonly FontFamily Solid =
        new("ms-appx:///Assets/Fonts/fa-solid-900.ttf#Font Awesome 5 Pro Solid");

    private static readonly Brush Ring = new SolidColorBrush(Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Number = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
    private static readonly Brush DoneFill = Palette("RpmCyanBrush", Color.FromArgb(0xFF, 0x04, 0xC8, 0xFF));
    private static readonly Brush DoneInk = Palette("RpmInkBrush", Color.FromArgb(0xFF, 0x0E, 0x15, 0x20));

    /// <summary>A brush from the app's palette. The fallback is the dictionary's own value. See
    /// <see cref="CloudBadge"/>.</summary>
    private static Brush Palette(string key, Color fallback) =>
        Application.Current?.Resources is { } resources
        && resources.TryGetValue(key, out var found) && found is Brush brush
            ? brush
            : new SolidColorBrush(fallback);

    private readonly Ellipse _disc;
    private readonly TextBlock _mark;

    /// <summary>The font the number is set in: the text block's own default. It is kept so the number can
    /// go back to it after the tick has used the icon font.</summary>
    private readonly FontFamily _numberFont;

    public StepBadge()
    {
        Width = 20;
        Height = 20;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;

        _disc = new Ellipse { StrokeThickness = 1.2 };
        _mark = new TextBlock
        {
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _numberFont = _mark.FontFamily;
        Children.Add(_disc);
        Children.Add(_mark);
        Render();
    }

    public static readonly DependencyProperty StepProperty =
        DependencyProperty.Register(nameof(Step), typeof(int), typeof(StepBadge),
            new PropertyMetadata(1, (d, _) => ((StepBadge)d).Render()));

    /// <summary>The step's number. It is shown until the step is done.</summary>
    public int Step
    {
        get => (int)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public static readonly DependencyProperty IsDoneProperty =
        DependencyProperty.Register(nameof(IsDone), typeof(bool), typeof(StepBadge),
            new PropertyMetadata(false, (d, _) => ((StepBadge)d).Render()));

    /// <summary>Whether the step is done. The number then becomes a tick on a filled disc.</summary>
    public bool IsDone
    {
        get => (bool)GetValue(IsDoneProperty);
        set => SetValue(IsDoneProperty, value);
    }

    private void Render()
    {
        if (IsDone)
        {
            _disc.Fill = DoneFill;
            _disc.Stroke = DoneFill;
            _mark.FontFamily = Solid;
            _mark.FontSize = 9;
            _mark.Text = "\uf00c";
            _mark.Foreground = DoneInk;
        }
        else
        {
            _disc.Fill = null;
            _disc.Stroke = Ring;
            _mark.FontFamily = _numberFont;
            _mark.FontSize = 11;
            _mark.Text = Step.ToString();
            _mark.Foreground = Number;
        }
    }
}
