using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace RPM.Desktop.Presentation.Controls;

/// <summary>A tiny three-bar equaliser. It is the "now playing" mark that sits in front of the current
/// track. The bars bob at different rates, so the mark reads as sound, not as a progress bar. It has no
/// colour of its own. The caller sets <see cref="Foreground"/> to the row's own text colour, so the mark
/// matches the text. It animates only while <see cref="Active"/> is true. Collapsed rows cost nothing.
///
/// The bob is driven by a timer, NOT by a looping Storyboard. A RepeatBehavior.Forever dependent animation
/// does not reliably stop on Storyboard.Stop() under Uno. So the bars would keep bobbing after a stop,
/// wherever the element stays in the tree. Stopping the timer is an unconditional stop.
///
/// <para>It uses a timer, not <c>CompositionTarget.Rendering</c>. Subscribing to that event on Skia does
/// more than observe the frames. While ANY handler is attached, each finished frame asks for the next one.
/// So the app draws its whole window at the display's rate for as long as the handler lives, whether
/// anything changed or not. A few 2.5 px bars therefore cost tens of percent of a CPU for the length of
/// every track. Throttling inside the handler does not help, because the loop is the subscription, not the
/// writes. A timer lets the app sleep between bobs, and each bob changes one transform.</para>
///
/// <para>It bobs by SCALING a full-height bar, not by writing its <c>Height</c>. A height is layout. Every
/// bob would then dirty measure and arrange for the row, the list, and everything above them.</para>
/// </summary>
public sealed partial class PlayingBars : StackPanel
{
    private const double BarBase = 4;

    /// <summary>How far the bars' feet stand below the middle of the mark. This is the baseline of the text
    /// beside it: half a line box down, less the descender. At the 12 to 13 px that the mark is used at,
    /// where the line box is about 16 px, this is about 5.25.</summary>
    private const double BaselineDrop = 5.25;

    /// <summary>How often the bars are written. This is not the display's rate. See the class remarks.
    /// Every write repaints the whole window, and a bob looks smooth at this rate.</summary>
    private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(50);

    /// <summary>Starts the shared clock if it is not running yet. A grid that draws its own mark needs the
    /// clock, even when no PlayingBars element is on screen.</summary>
    internal static void EnsureClock()
    {
        if (!Clock.IsRunning)
            Clock.Start();
    }

    // One timer and one clock for every mark on screen, not one each. The playing track is marked in the
    // tracklist and on the album band at the same time. Two timers 50 ms apart would repaint the window
    // twice a beat, not once. The cost is the repaint, not the arithmetic. A shared clock also keeps the
    // two marks in step, which is right: it is the same sound.
    private static readonly List<PlayingBars> Marks = [];
    private static readonly Stopwatch Clock = new();

    /// <summary>How long the marks have been bobbing, in seconds. The grids read this. They draw their own
    /// mark rather than hosting one of these.
    ///
    /// <para>It is the same clock, so the mark on a row and the mark on the album band are in step. They
    /// are the same sound.</para>
    /// </summary>
    internal static double BeatSeconds => Clock.Elapsed.TotalSeconds;

    /// <summary>The interval the marks are written at. This is not the display's rate. See the class
    /// remarks. Every write repaints the whole window.</summary>
    internal static TimeSpan BeatInterval => Beat;
    private static DispatcherTimer? _timer;

    /// <summary>One entry per bar: its height at the tallest, and the sine that bobs it. The entry gives
    /// the period in seconds, the shortest height, and the point in the cycle where the bar starts. The
    /// three periods do not divide into one another. The starts are a third of a turn apart, so the group
    /// never falls into step.</summary>
    private static readonly (double Full, double Period, double Min, double Phase)[] Shape =
    [
        (13, 1.28, 4, 0),
        (12, 0.96, 5, 2 * Math.PI / 3),
        (10, 1.52, 4, 4 * Math.PI / 3),
    ];

    /// <summary>This beat's bar heights. There is one buffer for the whole class. It is filled at the top
    /// of each tick, and then written to every mark. The marks are the same sound, so they bob in
    /// step.</summary>
    private static readonly double[] Heights = new double[Shape.Length];

    private readonly Rectangle[] _bars = new Rectangle[Shape.Length];
    private readonly ScaleTransform[] _scales = new ScaleTransform[Shape.Length];

    public PlayingBars()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 2;
        VerticalAlignment = VerticalAlignment.Center;
        Visibility = Visibility.Collapsed;

        // The mark stands ON the baseline of the text beside it. The box is shaped so that this holds
        // wherever the mark is centred: the album wall's title, or the tracklist's glyph slot. The host
        // does not need to move it. A centred box puts its own middle on the middle of the text. The
        // baseline is BaselineDrop below that. So the box has padding under the feet of the bars, which
        // pushes them down.
        //
        // The padding is not the drop. It is twice the drop. Padding under a bar of height `full` makes the
        // box `full + pad` tall. Its middle moves down by pad/2 while the feet stay where they are. So the
        // feet end up only `full/2 - pad/2` below that middle. The padding is worked out from the drop that
        // is wanted, which is what this does.
        //
        // Bars anchored at the bottom, in a box as tall as the bars, would hang their feet a full half-box
        // low. The resting stub holds all of the mark's mass at its feet. So any such error is easy to see
        // at a glance.
        var full = Shape.Max(s => s.Full);
        var pad = full - 2 * BaselineDrop;

        Padding = new Thickness(0, 0, 0, pad);
        Height = full + pad;

        for (int i = 0; i < Shape.Length; i++)
        {
            _scales[i] = new ScaleTransform();
            _bars[i] = MakeBar(Shape[i].Full, _scales[i]);
            Children.Add(_bars[i]);
        }

        Rest();

        Unloaded += (_, _) => StopTick();

        // The Visibility set above is a LOCAL value, and something else can clear it.
        //
        // Setting Content on a ContentControl resets the local property values of the root of its
        // ContentTemplate. Visibility's default is Visible. A grid whose rows draw their columns without
        // cells recycles this element by doing exactly that. The call path is:
        //
        //   ContentControl.set_Content
        //     <- TableViewTemplateColumn.RefreshCellFreeElement
        //     <- TableViewLightCellsPanel.Show
        //     <- TableViewRowPresenter.ShowLightCells
        //     <- TableViewRow.RefreshCells
        //     <- TableViewRow.OnContentChanged
        //
        // So the mark came back on whatever row the element was recycled onto, at rest, with nothing
        // playing. It followed the screen position, not the track. Raising IsPlaying did not help, because
        // Active never changed. Only Visibility did.
        //
        // Active decides whether the mark is shown. So the callback below puts the reset back. It is here,
        // and not in the six templates that use this control. A value that every template must remember to
        // set is one that the next template will forget.
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (!Active && Visibility is Visibility.Visible)
                Visibility = Visibility.Collapsed;
        });
    }

    /// <summary>The bar colour. The track row sets this to its text brush, so the mark matches the
    /// text.</summary>
    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public static readonly DependencyProperty ForegroundProperty =
        DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(PlayingBars),
            new PropertyMetadata(new SolidColorBrush(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF)), OnForegroundChanged));

    private static void OnForegroundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bars = (PlayingBars)d;
        foreach (var bar in bars._bars)
            bar.Fill = (Brush)e.NewValue;
    }

    /// <summary>True on the playing row. It shows the mark and runs it. False hides it and stops
    /// it.</summary>
    public bool Active
    {
        get => (bool)GetValue(ActiveProperty);
        set => SetValue(ActiveProperty, value);
    }

    public static readonly DependencyProperty ActiveProperty =
        DependencyProperty.Register(nameof(Active), typeof(bool), typeof(PlayingBars),
            new PropertyMetadata(false, OnActiveChanged));

    private static void OnActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bars = (PlayingBars)d;

        if ((bool)e.NewValue)
        {
            bars.Visibility = Visibility.Visible;
            bars.StartTick();
        }
        else
        {
            bars.StopTick();
            bars.Visibility = Visibility.Collapsed;
        }
    }

    /// <param name="full">The bar's height at its tallest. The bar is laid out once at this height and
    /// scaled down from its foot for the bob. So a frame never changes the layout.</param>
    private Rectangle MakeBar(double full, ScaleTransform scale) => new()
    {
        Width = 2.5,
        Height = full,
        RadiusX = 1,
        RadiusY = 1,
        Fill = Foreground,
        VerticalAlignment = VerticalAlignment.Bottom,
        RenderTransformOrigin = new Windows.Foundation.Point(0.5, 1),   // grow up from the foot
        RenderTransform = scale,
    };

    private void StartTick()
    {
        if (Marks.Contains(this))
            return;

        Marks.Add(this);
        if (_timer is not null)
            return;

        Clock.Restart();
        _timer = new DispatcherTimer { Interval = Beat };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void StopTick()
    {
        if (!Marks.Remove(this))
            return;

        Rest();
        if (Marks.Count > 0 || _timer is not { } timer)
            return;

        _timer = null;
        timer.Stop();
        timer.Tick -= OnTick;
        Clock.Stop();
    }

    /// <summary>Every bar settled back to the resting stub.</summary>
    private void Rest()
    {
        for (int i = 0; i < _bars.Length; i++)
            _scales[i].ScaleY = BarBase / _bars[i].Height;
    }

    private static void OnTick(object? sender, object e)
    {
        var t = Clock.Elapsed.TotalSeconds;

        // Three sines with different periods, out of phase. The group then reads as sound, not as a
        // metronome. The heights are the same for every mark on screen, so they are worked out once and
        // then written to each mark.
        for (int i = 0; i < Shape.Length; i++)
        {
            var (full, period, min, phase) = Shape[i];
            Heights[i] = Wave(t, period, min, full, phase);
        }

        foreach (var mark in Marks)
            for (int i = 0; i < Heights.Length; i++)
                mark._scales[i].ScaleY = Heights[i] / mark._bars[i].Height;
    }

    /// <summary>A bar height that moves between <paramref name="min"/> and <paramref name="max"/> with the
    /// given period in seconds. The phase is in radians.</summary>
    private static double Wave(double t, double period, double min, double max, double phase)
    {
        var mid = (min + max) / 2;
        var amp = (max - min) / 2;
        return mid + amp * Math.Sin(2 * Math.PI * t / period + phase);
    }
}
