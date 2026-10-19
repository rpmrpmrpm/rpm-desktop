using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Controls;

/// <summary>The wait, shown as a record on a turntable. It appears while something in the library is being
/// read, in place of the platform's ring. A 24-point ring is what most applications show, and it says
/// nothing about what is being waited for. This one says that a record is coming.
///
/// <para>It is driven by a timer that writes the angle. It is not driven by a looping Storyboard. A
/// <c>RepeatBehavior.Forever</c> dependent animation does not reliably stop on <c>Storyboard.Stop()</c>
/// under Uno. See <see cref="PlayingBars"/>, which avoids it for the same reason. A spinner that keeps
/// turning after its record has landed keeps the window redrawing itself. A timer always stops when it is
/// told to.</para>
///
/// <para>There is one timer and one clock for every spinner on screen. Several records can be reading at
/// once. Six timers, each a beat apart, would redraw the window six times where one redraw will do. The
/// cost is the redraw, not the arithmetic. A shared clock also keeps the spinners in step. That is right,
/// because they are all the same wait.</para>
/// </summary>
public sealed partial class RecordSpinner : UserControl
{
    /// <summary>Thirty-three and a third revolutions a minute, which is the speed an LP turns at.</summary>
    private const double DegreesPerSecond = 360 * 33.3333 / 60;

    /// <summary>How often the angle is written. This is not the display's rate. Every write redraws the
    /// window (see RenderTuning), and a disc this size looks smooth at thirty writes a second.
    ///
    /// <para>The interval is 30 milliseconds, not 33. Windows delivers a DispatcherTimer on its 15.6 ms
    /// system tick. An interval of 33 ms therefore fired every 46.9 ms (three ticks), about 21 times a
    /// second. 30 ms fits in two ticks, 31.25 ms, which is about 32 times a second. See CardRoomBeat, where
    /// the same rounding was measured.</para></summary>
    private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(30);

    /// <summary>The name this spinner reports under to <see cref="Services.Shell.FrameWatch"/>. A spinner
    /// is driven by a timer, not by the render tick, so it does not appear on that list. But it writes a
    /// property thirty times a second, and every write repaints the whole window. That is what the list
    /// exists to show. A record left turning behind a page nobody is reading is exactly what this name is
    /// for.</summary>
    private const string Watch = "record-spinner";

    private static readonly List<RecordSpinner> Turning = [];
    private static readonly Stopwatch Clock = new();
    private static DispatcherTimer? _timer;

    public RecordSpinner()
    {
        InitializeComponent();

        // Off, and out of the way, until something says otherwise. An unresolved binding also leaves it
        // this way. A wait mark that showed itself by default would be left turning over a pane with
        // nothing behind it.
        Visibility = Visibility.Collapsed;

        // A spinner whose block is recycled while it is reading must leave the clock. The property that
        // would turn it off is never written in that case. So the timer would keep running for a record
        // that nobody can see.
        Unloaded += (_, _) => Leave();
        Loaded += (_, _) => { if (Spinning) Join(); };
    }

    public static readonly DependencyProperty SpinningProperty =
        DependencyProperty.Register(
            nameof(Spinning), typeof(bool), typeof(RecordSpinner),
            new PropertyMetadata(false, OnSpinningChanged));

    /// <summary>Whether the record is turning. This is the ONLY thing a caller sets. The mark shows while
    /// it turns and hides when it stops. There is no second value to write, so nothing can disagree with
    /// the first and leave a record spinning over an answer that has already arrived.</summary>
    public bool Spinning
    {
        get => (bool)GetValue(SpinningProperty);
        set => SetValue(SpinningProperty, value);
    }

    private static void OnSpinningChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var spinner = (RecordSpinner)d;
        var spinning = (bool)e.NewValue;
        spinner.Visibility = spinning ? Visibility.Visible : Visibility.Collapsed;
        if (spinning)
            spinner.Join();
        else
            spinner.Leave();
    }

    private void Join()
    {
        if (Turning.Contains(this))
            return;

        Turning.Add(this);
        Services.Shell.FrameWatch.Began(Watch);
        if (Turning.Count > 1)
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
        if (!Turning.Remove(this))
            return;

        Services.Shell.FrameWatch.Ended(Watch);

        // Back to zero, so the next record does not start part-turned.
        Spin.Angle = 0;

        if (Turning.Count == 0)
            _timer?.Stop();
    }

    /// <summary>The angle is read from the shared clock, not added to on each beat. So a spinner that joins
    /// late is in step with the ones already turning. A beat that the timer misses under load is caught up,
    /// not dropped.</summary>
    private static void OnBeat(object? sender, object e)
    {
        var angle = Clock.Elapsed.TotalSeconds * DegreesPerSecond % 360;
        foreach (var spinner in Turning)
            spinner.Spin.Angle = angle;
    }
}
