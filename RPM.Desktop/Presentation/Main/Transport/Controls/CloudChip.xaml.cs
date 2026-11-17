// ProgressArc is in RPM.Core.Ripping because the rip chip's ring used it first. This chip is a
// second user.
using System.Globalization;
using RPM.Core.Ripping;
using RPM.Desktop.Services;
using RPM.Desktop.Strings;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI;

namespace RPM.Desktop.Presentation.Main.Transport.Controls;

/// <summary>
/// The deck's cloud chip: a cloud with a progress ring. It is shown only while the cloud engine has
/// work in progress. It is the twin of the rip chip, on purpose, so the deck has one shape for long
/// work that was started somewhere else.
///
/// <para>Everything it shows comes off <see cref="CloudJobService"/>, so the chip and the engine are
/// two views of one job rather than two copies of it.</para>
/// </summary>
public sealed partial class CloudChip : UserControl
{
    private static readonly Color Cyan = Color.FromArgb(0xFF, 0x04, 0xC8, 0xFF);
    private static readonly Color Red = Color.FromArgb(0xFF, 0xFF, 0x6B, 0x6B);

    /// <summary>Ring geometry, in the 24×24 button box.</summary>
    private const double Centre = 12;
    private const double Radius = 9;
    private const int RingSegments = 48;

    /// <summary>A batch that has just started still shows a small arc, so the ring never looks empty while
    /// it runs.</summary>
    private const double MinArc = 0.04;

    /// <summary>How long a finished batch stays on the deck. Long enough for the user to see that it is
    /// done, and short enough not to stay in the way.</summary>
    private static readonly TimeSpan LingerAfterFinish = TimeSpan.FromSeconds(2.5);

    private readonly CloudJobService _jobs;
    private readonly CloudSyncService _cloud;

    /// <summary>True while the flyout is open. A finished batch stays on the deck while this is set. A
    /// panel that empties under the pointer would hide the very information the user opened it to read.
    /// </summary>
    private bool _flyoutOpen;

    private DispatcherTimer? _linger;

    /// <summary>How much of the ring the turning arc covers while the engine is still looking.</summary>
    private const double LookingArc = 0.25;

    /// <summary>One turn of the looking arc.</summary>
    private static readonly TimeSpan LookingTurn = TimeSpan.FromSeconds(1.1);

    /// <summary>The arc going round, while it is. A render transform's angle, so the turn runs on the
    /// compositor and costs the UI thread nothing at startup, which is when it is mostly seen.</summary>
    private Storyboard? _turning;

    public CloudChip()
    {
        InitializeComponent();

        _jobs = App.Services.GetRequiredService<CloudJobService>();
        _cloud = App.Services.GetRequiredService<CloudSyncService>();
        JobList.ItemsSource = _jobs.Working;

        FillRing(RingTrack, 1);

        Loaded += (_, _) =>
        {
            _jobs.Changed += OnJobChanged;
            Sync();
        };
        Unloaded += (_, _) =>
        {
            _jobs.Changed -= OnJobChanged;
            CancelLinger();
            Turn(false);
        };
    }

    /// <summary>
    /// Marshalled to the UI thread, because <c>CloudSyncService.Changed</c> is raised on the upload worker,
    /// not on the UI thread. <c>NoteStorageFull</c> is one of the callers. <see cref="Sync"/> sets
    /// Visibility and hides a flyout, and only the UI thread may do that.
    /// </summary>
    private void OnJobChanged() => DispatcherQueue.TryEnqueue(Sync);

    private void Sync()
    {
        var show = _jobs.State != CloudJobState.Idle;
        Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            CancelLinger();
            Turn(false);
            StatusFlyout.Hide();
            return;
        }

        var done = _jobs.State == CloudJobState.Finished;
        var paused = _jobs.State == CloudJobState.Paused;
        var failed = _jobs.Failed > 0;

        // Red only when something was refused. A batch that finished with no failures stays cyan, because
        // a full ring is the good result.
        var ink = new SolidColorBrush(failed ? Red : Cyan);
        RingArc.Stroke = ink;
        CloudGlyph.Foreground = failed ? ink : new SolidColorBrush(Colors.White);

        // Still looking: a batch is open (the startup check of what changed in the cloud is one) but nothing
        // has been counted into it yet. There is no fraction to show, and the sliver that stands for "just
        // started" would sit still for as long as the look takes, which reads as stuck. A quarter arc turns
        // instead, until the first count arrives or the look ends with nothing to do.
        var looking = _jobs.State == CloudJobState.Running && _jobs.Total == 0;
        Turn(looking);

        // The ring is full only when the work is finished. A paused run keeps the arc at the fraction it
        // reached, because the user paused to see that fraction. A full ring over a queue that is still
        // mostly waiting would say the opposite of what happened.
        FillRing(RingArc, done ? 1 : looking ? LookingArc : Math.Max(_jobs.Fraction, MinArc));

        var count = _jobs.CountText;
        CountText.Text = count;
        IconName.Set(ChipButton, count);

        // Before the first track is taken up there is nothing to count. The sentence says what is being
        // prepared, and an empty bar would only say "0%".
        var counting = !_jobs.IsPreparing;
        var fraction = done ? 1 : _jobs.Fraction;
        RunBar.Visibility = Show(counting);
        RunBar.Value = fraction;
        PercentText.Visibility = Show(counting);
        PercentText.Text = Percent(fraction);

        // Shown only after something was refused. On a run with no errors, the line would send the user to
        // look for a problem that does not exist.
        ErrorsHint.Visibility = Show(failed);
        if (failed)
            ErrorsText.Text = Loc.Format("CloudChip_FailedCount", _jobs.Failed.ToString("N0", CultureInfo.CurrentCulture));

        // Only a running batch has tracks in progress. The list is empty after a pause or at the end, and the
        // check on the state keeps the heading off a list that has nothing in it.
        WorkingPanel.Visibility = Show(_jobs.State == CloudJobState.Running && _jobs.Working.Count > 0);

        // Pause is for work that is still moving. A paused run shows Resume. A finished run can only be
        // dismissed.
        PauseButton.Visibility = Show(!done && !paused);
        ResumeButton.Visibility = Show(paused);
        DismissButton.Visibility = Show(done || paused);

        // Only a finished batch disappears on its own. A paused batch stays, because the Resume button is on
        // this panel. If the chip faded out a few seconds after Pause, the user would lose the only way
        // back.
        if (done)
            StartLinger();
        else
            CancelLinger();

        static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Starts the timer that removes a finished batch. The batch shows its full ring for a short time and
    /// then goes. While the flyout is open nothing happens, and the timer starts when it closes.
    /// </summary>
    private void StartLinger()
    {
        if (_linger is not null || _flyoutOpen)
            return;

        var timer = new DispatcherTimer { Interval = LingerAfterFinish };
        timer.Tick += (_, _) =>
        {
            CancelLinger();
            if (!_flyoutOpen)
                _jobs.Dismiss();
        };
        _linger = timer;
        timer.Start();
    }

    private void CancelLinger()
    {
        _linger?.Stop();
        _linger = null;
    }

    private void Flyout_Opened(object? sender, object e)
    {
        _flyoutOpen = true;
        CancelLinger();   // stay open for as long as the user reads it
    }

    private void Flyout_Closed(object? sender, object e)
    {
        _flyoutOpen = false;
        if (_jobs.State == CloudJobState.Finished)
            StartLinger();
    }

    /// <summary>Starts or stops the looking arc going round. A stopped arc is put back at the top, where a
    /// counted arc starts from.</summary>
    private void Turn(bool on)
    {
        if (on == _turning is not null)
            return;

        if (!on)
        {
            _turning!.Stop();
            _turning = null;
            RingTurn.Angle = 0;
            return;
        }

        var spin = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(LookingTurn),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(spin, RingTurn);
        Storyboard.SetTargetProperty(spin, "Angle");
        _turning = new Storyboard();
        _turning.Children.Add(spin);
        _turning.Begin();
    }

    /// <summary>The fraction as a whole percentage, in the user's own format ("33%", "33 %"). It is rounded
    /// down, so a run with one track left never says 100%.</summary>
    private static string Percent(double fraction) =>
        (Math.Floor(fraction * 100) / 100).ToString("P0", CultureInfo.CurrentCulture);

    /// <summary>Sets the ring to a fraction of a full circle, as plain points from the top, going clockwise.
    /// Code-built arc geometry draws nothing under Uno-Skia, but Polyline points are drawn as given.</summary>
    private static void FillRing(Microsoft.UI.Xaml.Shapes.Polyline line, double fraction)
    {
        var points = new PointCollection();
        foreach (var (x, y) in ProgressArc.Points(fraction, Centre, Centre, Radius, RingSegments))
            points.Add(new Point(x, y));
        line.Points = points;
    }

    /// <summary>Stops the engine where it is and keeps it stopped. The queue mirrors the engine's state, so
    /// nothing is lost. The track in progress goes back into the line, and Resume picks it up.</summary>
    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        _cloud.Pause();

        // The flyout stays open after Pause. The result of Pause (where the run got to, and how to start
        // it again) is shown on this panel, so closing the panel would hide the answer.
    }

    /// <summary>Back into the queue, from wherever the pause left it.</summary>
    private void Resume_Click(object sender, RoutedEventArgs e) => _ = _cloud.ResumeAsync();

    /// <summary>Opens the settings page with the error reasons. The chip only shows that something was
    /// refused. The settings page shows what was said, with the button to send it in.</summary>
    private async void Errors_Click(object sender, RoutedEventArgs e)
    {
        StatusFlyout.Hide();
        if (XamlRoot is { } root)
            await Settings.SettingsDialog.ShowAsync(root, App.Services, "Cloud");
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        StatusFlyout.Hide();
        _jobs.Dismiss();
    }
}
