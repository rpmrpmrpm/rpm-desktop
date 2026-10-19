using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RPM.Desktop.Presentation.Controls;

/// <summary>Five stars, in halves.
///
/// <para>Halves are drawn, not faked with opacity. A half-lit star is its own glyph. So the shape reads at
/// eleven pixels, which a partly transparent star does not. The value the control holds is in stars (0 to
/// 5, in steps of .5), because that is what a reader sees. The wire format doubles it to an integer. That
/// doubling is the API's job, not this control's.</para>
///
/// <para>Interactive or not is a property, not two controls. The same row of stars shows an album's
/// average, echoes a review's verdict, and takes the user's own rating. The only difference is whether it
/// responds to the pointer.</para>
/// </summary>
public sealed partial class StarRating : Control
{
    private static readonly Color Gold = Color.FromArgb(0xFF, 0xF2, 0xC1, 0x4E);
    private static readonly Color Dim = Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF);

    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal };
    private readonly FontIcon[] _stars = new FontIcon[5];

    /// <summary>What the pointer is hovering over, or -1. It is never written to <see cref="Value"/>. A
    /// preview that stayed after the pointer left would be a rating that nobody chose.</summary>
    private double _preview = -1;

    public StarRating()
    {
        DefaultStyleKey = typeof(StarRating);
        IsTabStop = false;
        VerticalAlignment = VerticalAlignment.Center;

        for (var i = 0; i < _stars.Length; i++)
        {
            _stars[i] = new FontIcon
            {
                FontFamily = Solid,
                Foreground = new SolidColorBrush(Dim),
                VerticalAlignment = VerticalAlignment.Center,
            };
            _row.Children.Add(_stars[i]);
        }

        // The row of stars IS the control. The template in Themes/Generic.xaml only supplies the presenter
        // that the row hangs on.
        Draw();
    }

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(StarRating),
        new PropertyMetadata(0d, (d, _) => ((StarRating)d).Draw()));

    /// <summary>Stars, 0 to 5, in halves.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static readonly DependencyProperty GlyphSizeProperty = DependencyProperty.Register(
        nameof(GlyphSize), typeof(double), typeof(StarRating),
        new PropertyMetadata(14d, (d, _) => ((StarRating)d).Draw()));

    public double GlyphSize
    {
        get => (double)GetValue(GlyphSizeProperty);
        set => SetValue(GlyphSizeProperty, value);
    }

    public static readonly DependencyProperty IsInteractiveProperty = DependencyProperty.Register(
        nameof(IsInteractive), typeof(bool), typeof(StarRating),
        new PropertyMetadata(false, (d, _) => ((StarRating)d).Draw()));

    /// <summary>Whether the stars can be set by clicking them.</summary>
    public bool IsInteractive
    {
        get => (bool)GetValue(IsInteractiveProperty);
        set => SetValue(IsInteractiveProperty, value);
    }

    /// <summary>Raised when the user picks a rating. It is not raised when <see cref="Value"/> is set in
    /// code. So a control that shows a saved rating never reports it back.</summary>
    public event EventHandler<double>? RatingPicked;

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (GetTemplateChild("PART_Host") is ContentPresenter host)
            host.Content = _row;
    }

    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!IsInteractive) return;

        _preview = FromPointer(e.GetCurrentPoint(_row).Position.X);
        Draw();
    }

    protected override void OnPointerExited(PointerRoutedEventArgs e)
    {
        base.OnPointerExited(e);
        if (!IsInteractive) return;

        _preview = -1;
        Draw();
    }

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsInteractive) return;

        var picked = FromPointer(e.GetCurrentPoint(_row).Position.X);
        _preview = -1;
        Value = picked;
        RatingPicked?.Invoke(this, picked);
        e.Handled = true;
    }

    /// <summary>Moves the rating by whole stars, as the Left and Right keys on a remote do, and raises
    /// <see cref="RatingPicked"/>. A half star moves to the whole star in that direction first. The result
    /// stays between one and five. A key cannot clear a rating, and a click cannot either. It returns false
    /// when nothing moved: the control is not interactive, or the rating is already at that end.
    /// </summary>
    public bool Step(int stars)
    {
        if (!IsInteractive || Stepped(Value, stars) is not { } to)
            return false;

        _preview = -1;
        Value = to;
        RatingPicked?.Invoke(this, to);
        return true;
    }

    /// <summary>Where <see cref="Step"/> takes a rating of <paramref name="from"/> stars. It returns null
    /// where the rating does not move: no step, already five, or Left from one star or from none.</summary>
    internal static double? Stepped(double from, int stars)
    {
        if (stars == 0)
            return null;

        var to = stars > 0 ? Math.Floor(from) + stars : Math.Ceiling(from) + stars;
        if (to < 1)
            return null;
        to = Math.Min(to, 5);
        return to == from ? null : to;
    }

    /// <summary>Which rating a click at this x position means. Each star is two targets. So the left half
    /// of the first star is half a star, and there is no dead zone between whole values.</summary>
    private double FromPointer(double x)
    {
        var starWidth = _row.ActualWidth > 0 ? _row.ActualWidth / _stars.Length : GlyphSize * 1.4;
        var halves = (int)Math.Ceiling(x / (starWidth / 2));
        return Math.Clamp(halves, 1, 10) / 2d;
    }

    private void Draw()
    {
        var shown = _preview >= 0 ? _preview : Value;

        for (var i = 0; i < _stars.Length; i++)
        {
            var star = _stars[i];
            star.FontSize = GlyphSize;
            // The gap grows with the glyph, so a big rating is not a row of touching stars.
            star.Margin = new Thickness(0, 0, i == _stars.Length - 1 ? 0 : GlyphSize * 0.18, 0);

            var filled = shown - i;

            if (filled >= 1)
            {
                star.Glyph = "\uf005";   // star
                star.FontFamily = Solid;
                star.Foreground = new SolidColorBrush(Gold);
            }
            else if (filled >= 0.5)
            {
                star.Glyph = "\uf5c0";   // star-half-alt: a lit left half against an unlit right
                star.FontFamily = Solid;
                star.Foreground = new SolidColorBrush(Gold);
            }
            else
            {
                star.Glyph = "\uf005";
                star.FontFamily = Outline;
                star.Foreground = new SolidColorBrush(Dim);
            }
        }
    }

    private static FontFamily Solid { get; } =
        new("ms-appx:///Assets/Fonts/fa-solid-900.ttf#Font Awesome 5 Pro Solid");

    private static FontFamily Outline { get; } =
        new("ms-appx:///Assets/Fonts/fa-regular-400.ttf#Font Awesome 5 Pro Regular");

}
