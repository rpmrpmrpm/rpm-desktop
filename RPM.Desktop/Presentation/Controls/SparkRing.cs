using System.Diagnostics;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

using Windows.Foundation;

namespace RPM.Desktop.Presentation.Controls;

/// <summary>A circular progress indicator that is worth watching. It has a cyan arc, a bright head at the
/// leading tip, and sparks flying off that head.
///
/// <para>It is used on the locked surfaces, where it shows how close a collection is to being big enough
/// for a feature. It shows progress that is not work in flight. It can be read from across a room.</para>
///
/// <para><b>It runs on a timer, and only while it is on screen.</b> Both halves matter on the Skia head.
/// There are no dirty rectangles there, and any write repaints the whole window.
/// <c>CompositionTarget.Rendering</c> is worse than a timer, not cheaper. While a handler is attached,
/// every finished frame asks for the next one. So the app draws its whole window at the display's rate,
/// whether anything changed or not. The beat and the reasoning are the same as for
/// <see cref="Common.BadgeBreath"/>.</para>
///
/// <para><see cref="Live"/> is the caller's promise that this is visible. An indicator that keeps animating
/// behind a screen nobody is looking at repaints the whole window every 50 ms, for nothing.</para>
/// </summary>
public sealed partial class SparkRing : Grid
{
    /// <summary>How often a frame is written. This is not the display's rate. Every write repaints the
    /// window, and nothing here moves fast enough to need more than twenty writes a second.</summary>
    private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(50);

    /// <summary>How long one spark lives, in seconds.</summary>
    private const double SparkSeconds = 1.6;

    /// <summary>How far the furthest spark travels from the head before it is gone.</summary>
    private const double SparkReach = 38;

    /// <summary>How many sparks are in the air.
    /// </summary>
    /// <remarks>Seven, and each one leaves in its own direction. Three sparks going the same way read as a
    /// drip, not as a spray.
    /// </remarks>
    private const int SparkCount = 7;

    private readonly Ellipse _track;
    private readonly Ellipse _arc;
    private readonly Ellipse _head;
    private readonly Canvas _sparkLayer;
    private readonly Ellipse[] _sparks = new Ellipse[SparkCount];

    private readonly Stopwatch _clock = new();
    private DispatcherTimer? _timer;

    public SparkRing()
    {
        IsHitTestVisible = false;

        _track = new Ellipse
        {
            Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)),
        };

        // Drawn as a dashed circle. The first dash is the finished share, and the gap is the rest. The
        // circle is turned back a quarter, so it starts at the top. An arc would need a Path with an
        // ArcSegment. A dashed Ellipse is drawn the same way on both heads.
        _arc = new Ellipse
        {
            Stroke = new LinearGradientBrush
            {
                StartPoint = new Point(0, 1),
                EndPoint = new Point(1, 0),
                GradientStops =
                {
                    new GradientStop { Offset = 0.0, Color = Windows.UI.Color.FromArgb(0xFF, 0x03, 0x71, 0xD0) },
                    new GradientStop { Offset = 1.0, Color = Windows.UI.Color.FromArgb(0xFF, 0x04, 0xC8, 0xFF) },
                },
            },
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform { Angle = -90 },
            Opacity = 0,
        };

        // The bright bead that the sparks leave from. It is on the same layer as the sparks, so neither is
        // clipped by the ring they belong to.
        _head = new Ellipse
        {
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xDF, 0xF6, 0xFF)),
            Opacity = 0,
        };

        _sparkLayer = new Canvas { IsHitTestVisible = false };
        for (var i = 0; i < SparkCount; i++)
        {
            _sparks[i] = new Ellipse
            {
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xBF, 0xEE, 0xFF)),
                Opacity = 0,
            };
            _sparkLayer.Children.Add(_sparks[i]);
        }

        _sparkLayer.Children.Add(_head);

        Children.Add(_track);
        Children.Add(_arc);
        Children.Add(_sparkLayer);

        SizeChanged += (_, _) => Lay();
        Unloaded += (_, _) => Stop();
        Loaded += (_, _) => Settle();

        Lay();
    }

    /// <summary>How thick the ring is drawn, in points. The default is 9.</summary>
    public double Thickness
    {
        get => _thickness;
        set
        {
            if (Math.Abs(_thickness - value) < 0.01) return;
            _thickness = value;
            Lay();
        }
    }

    private double _thickness = 9;

    /// <summary>How far along, from zero to one.</summary>
    public double Fraction
    {
        get => _fraction;
        set
        {
            var clamped = Math.Clamp(double.IsFinite(value) ? value : 0, 0, 1);
            if (Math.Abs(_fraction - clamped) < 0.0005) return;
            _fraction = clamped;
            Lay();
        }
    }

    private double _fraction;

    /// <summary>Whether this is on screen and should move.
    ///
    /// <para>The caller decides this, because the ring cannot tell for itself. An element can be loaded,
    /// sized and laid out while the panel that holds it is collapsed behind something else.</para>
    /// </summary>
    public bool Live
    {
        get => _live;
        set
        {
            if (_live == value) return;
            _live = value;
            Settle();
        }
    }

    private bool _live;

    /// <summary>The ring's radius, measured through the middle of the stroke. It is zero before
    /// layout.</summary>
    private double Radius => (Math.Min(ActualWidth, ActualHeight) - _thickness) / 2;

    /// <summary>The arc's length, as a dash and a gap.
    /// </summary>
    /// <remarks>The dash array is in multiples of the stroke thickness, not in points. That is easy to get
    /// wrong. A zero-length dash with a round cap still draws a dot. So a gate with nothing done yet is
    /// drawn as no ring at all, not as a bead at twelve o'clock.
    /// </remarks>
    private void Lay()
    {
        _track.StrokeThickness = _thickness;
        _arc.StrokeThickness = _thickness;
        _head.Width = _head.Height = _thickness + 3;

        for (var i = 0; i < SparkCount; i++)
        {
            var side = 2.0 + (i % 3);
            _sparks[i].Width = _sparks[i].Height = side;
        }

        var radius = Radius;
        if (radius <= 0)
            return;

        double circumference = 2 * Math.PI * radius;
        double done = circumference * _fraction;

        _arc.Opacity = done > 0.5 ? 1 : 0;
        _arc.StrokeDashArray =
        [
            done / _thickness,
            Math.Max(0.001, (circumference - done) / _thickness),
        ];
    }

    private void Settle()
    {
        if (_live && IsLoaded)
            Start();
        else
            Stop();
    }

    private void Start()
    {
        if (_timer is not null) return;

        _clock.Restart();
        _timer = new DispatcherTimer { Interval = Beat };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void Stop()
    {
        if (_timer is null) return;

        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
        _clock.Stop();

        // Hidden where they cannot be seen. A ring that stops in the middle of a spray would otherwise
        // leave a scatter of dots frozen around it.
        _head.Opacity = 0;
        foreach (var spark in _sparks)
            spark.Opacity = 0;
    }

    private void OnTick(object? sender, object e)
    {
        var radius = Radius;
        if (radius <= 0 || _fraction <= 0)
        {
            _head.Opacity = 0;
            foreach (var spark in _sparks)
                spark.Opacity = 0;
            return;
        }

        double seconds = _clock.Elapsed.TotalSeconds;
        double cx = ActualWidth / 2;
        double cy = ActualHeight / 2;

        // The leading tip of the arc. The arc starts at twelve o'clock and runs clockwise.
        double sweep = (-90 + 360 * _fraction) * Math.PI / 180;
        double tipX = cx + radius * Math.Cos(sweep);
        double tipY = cy + radius * Math.Sin(sweep);

        // The head breathes instead of sitting still. That keeps the ring alive, even at a fraction too
        // small to throw many sparks.
        double breath = 0.72 + 0.28 * Math.Sin(seconds * 2.6);
        _head.Opacity = breath;
        Canvas.SetLeft(_head, tipX - _head.Width / 2);
        Canvas.SetTop(_head, tipY - _head.Height / 2);

        for (var i = 0; i < SparkCount; i++)
        {
            // Each spark is a fixed share of a cycle behind the last. So they leave one after another, not
            // together.
            double life = ((seconds + i * SparkSeconds / SparkCount) % SparkSeconds) / SparkSeconds;

            // Fast in, slow out. A spark that fades in as slowly as it fades out looks like a smudge, not a
            // spark.
            double opacity = life < 0.18 ? life / 0.18 : 1 - ((life - 0.18) / 0.82);
            _sparks[i].Opacity = Math.Clamp(opacity, 0, 1);

            // Each spark has its own direction, fanned around the way the arc points. The spread is the
            // whole difference between a spray and a drip. If every spark left on the same heading, they
            // would form a line of dots falling off the end.
            double fan = (i - (SparkCount - 1) / 2.0) * (140.0 / SparkCount) * Math.PI / 180;
            double heading = sweep + fan;

            // Each spark also has its own reach, so they do not all stop at the same distance. The step
            // must be COPRIME with the count (3 and 7 here). Otherwise the sequence repeats early, and some
            // sparks get the same reach. If all seven shared one reach, they would bunch into one clot at
            // the tip.
            double reach = SparkReach * (0.5 + 0.5 * ((i * 3 % SparkCount) / (double)(SparkCount - 1)));
            double travelled = life * reach;

            Canvas.SetLeft(_sparks[i], tipX + travelled * Math.Cos(heading) - _sparks[i].Width / 2);
            Canvas.SetTop(_sparks[i], tipY + travelled * Math.Sin(heading) - _sparks[i].Height / 2);
        }
    }
}
