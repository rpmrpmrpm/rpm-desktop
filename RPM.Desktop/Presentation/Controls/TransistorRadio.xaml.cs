using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Controls;

/// <summary>The wait, shown as a set hunting along the band. It appears while the radio pane asks the
/// directory for stations, in place of <see cref="RecordSpinner"/>. That one says a RECORD is coming. That
/// is the right thing to say nearly everywhere in this window, and the wrong thing here, because nothing on
/// this pane is a record. A needle crossing a dial shows what this wait is.
///
/// <para>It is driven by a timer that writes the needle's position. It is NOT driven by a looping
/// Storyboard. The reason is the same as for <see cref="RecordSpinner"/>. A <c>RepeatBehavior.Forever</c>
/// dependent animation does not reliably stop on <c>Storyboard.Stop()</c> under Uno. A mark that keeps
/// moving after the stations have arrived keeps the window redrawing itself. Stopping a timer is an
/// unconditional stop.</para>
///
/// <para>There is one timer and one clock for every set on screen. The position is read from the shared
/// clock, not added to on each beat. So a set that joins late is in step with the sets already running. A
/// beat that is missed under load is caught up, not dropped.</para>
/// </summary>
public sealed partial class TransistorRadio : UserControl
{
    /// <summary>How long the needle takes to cross the band and come back.</summary>
    private static readonly TimeSpan Sweep = TimeSpan.FromSeconds(3);

    /// <summary>How often the position is written. This is not the display's rate. Every write redraws the
    /// window (see RenderTuning). A needle this size looks smooth at thirty writes a second.</summary>
    private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(33);

    /// <summary>The needle's left and right positions at each end of the band, in the drawing's
    /// coordinates. The scale runs from 15.5 to 84.5. The needle's box is 5 wide, so it is centred on the
    /// mark at both ends.</summary>
    private const double LeftEnd = 15.5 - 2.5;
    private const double RightEnd = 84.5 - 2.5;

    /// <summary>How far the knob turns between the two ends of the band. It is not a full turn. A tuning
    /// knob has stops. A knob that turned all the way round to where it started would look like a
    /// wheel.</summary>
    private const double KnobThrow = 140;

    private static readonly List<TransistorRadio> Tuned = [];
    private static readonly Stopwatch Clock = new();
    private static DispatcherTimer? _timer;

    public TransistorRadio()
    {
        InitializeComponent();

        Grille.Data = PunchedGrille();

        // The needle starts at the bottom of the band, before the first beat arrives. Its box is drawn at
        // the left edge of the canvas and the transform carries it into the dial. So a set shown for the
        // one frame before the timer ticks would show the needle out on the case.
        NeedleAt.X = LeftEnd;
        Knob.Angle = -KnobThrow;

        // Off, and out of the way, until something says otherwise. An unresolved binding also leaves it
        // this way. A wait mark that showed itself by default would keep running over a pane with nothing
        // behind it.
        Visibility = Visibility.Collapsed;

        // A mark whose block is recycled while it is searching must leave the clock. The property that
        // would turn it off is never written in that case. So the timer would keep running for a set nobody
        // can see.
        Unloaded += (_, _) => Leave();
        Loaded += (_, _) => { if (Tuning) Join(); };
    }

    /// <summary>The speaker's holes: a square grid of them, kept inside the well, as in a punched fascia.
    /// One <see cref="Path"/> holds all of them, so the whole grille costs one element, not one per hole.
    /// It never changes, so it is the cheapest part of the drawing to leave alone.</summary>
    private static Geometry PunchedGrille()
    {
        const double centreX = 31, centreY = 72, well = 17, step = 4.2, hole = 0.95;

        var holes = new GeometryGroup();
        var reach = (int)(well / step) + 1;
        for (var i = -reach; i <= reach; i++)
        for (var j = -reach; j <= reach; j++)
        {
            double x = centreX + i * step, y = centreY + j * step;
            if (Math.Sqrt((x - centreX) * (x - centreX) + (y - centreY) * (y - centreY)) > well - hole)
                continue;

            holes.Children.Add(new EllipseGeometry
            {
                Center = new Windows.Foundation.Point(x, y),
                RadiusX = hole,
                RadiusY = hole,
            });
        }

        return holes;
    }

    public static readonly DependencyProperty TuningProperty =
        DependencyProperty.Register(
            nameof(Tuning), typeof(bool), typeof(TransistorRadio),
            new PropertyMetadata(false, OnTuningChanged));

    /// <summary>Whether the set is hunting. This is the ONLY thing a caller sets. The mark shows while it
    /// hunts and hides when it stops. There is no second value to write, so nothing can disagree with the
    /// first and leave a needle running over an answer that has already arrived.</summary>
    public bool Tuning
    {
        get => (bool)GetValue(TuningProperty);
        set => SetValue(TuningProperty, value);
    }

    private static void OnTuningChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var radio = (TransistorRadio)d;
        var tuning = (bool)e.NewValue;
        radio.Visibility = tuning ? Visibility.Visible : Visibility.Collapsed;
        if (tuning)
            radio.Join();
        else
            radio.Leave();
    }

    private void Join()
    {
        if (Tuned.Contains(this))
            return;

        Tuned.Add(this);
        if (Tuned.Count > 1)
            return;

        if (!Clock.IsRunning)
            Clock.Start();

        _timer ??= new DispatcherTimer { Interval = Beat };
        _timer.Tick -= OnBeat;
        _timer.Tick += OnBeat;
        _timer.Start();
    }

    private void Leave()
    {
        if (!Tuned.Remove(this))
            return;

        // Back to the bottom of the band, so the next search does not start part-way across.
        NeedleAt.X = LeftEnd;
        Knob.Angle = -KnobThrow;

        if (Tuned.Count == 0)
            _timer?.Stop();
    }

    private static void OnBeat(object? sender, object e)
    {
        // The needle goes there and back on a cosine, not at a constant rate. A needle that turned round at
        // full speed would look like a bouncing ball. A hand working a dial slows down at each end of the
        // band.
        var phase = Clock.Elapsed.TotalSeconds / Sweep.TotalSeconds % 1;
        var across = (1 - Math.Cos(phase * 2 * Math.PI)) / 2;

        foreach (var radio in Tuned)
        {
            radio.NeedleAt.X = LeftEnd + (RightEnd - LeftEnd) * across;
            radio.Knob.Angle = -KnobThrow + 2 * KnobThrow * across;
        }
    }
}
