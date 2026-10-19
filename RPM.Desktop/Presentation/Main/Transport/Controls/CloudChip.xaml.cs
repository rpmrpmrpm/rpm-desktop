// ProgressArc is in RPM.Core.Ripping because the rip chip's ring used it first. This chip is a
// second user.
using RPM.Core.Ripping;
using RPM.Desktop.Services;
using RPM.Desktop.Strings;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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

    public CloudChip()
    {
        InitializeComponent();

        _jobs = App.Services.GetRequiredService<CloudJobService>();
        _cloud = App.Services.GetRequiredService<CloudSyncService>();
        JobList.ItemsSource = _jobs.Rows;

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

        // The ring is full only when the work is finished. A paused run keeps the arc at the fraction it
        // reached, because the user paused to see that fraction. A full ring over a queue that is still
        // mostly waiting would say the opposite of what happened.
        FillRing(RingArc, done ? 1 : Math.Max(_jobs.Fraction, MinArc));

        SummaryText.Text = _jobs.Summary;
        IconName.Set(ChipButton, _jobs.Summary);

        // Show this only after something was refused. On a run with no errors, the line would send the user
        // to look for a problem that does not exist.
        ErrorsHint.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;

        // The chip is shown before the first track starts, so for a moment there is a summary and no list.
        // An empty box would look like there is nothing to do.
        JobList.Visibility = _jobs.Total > 0 ? Visibility.Visible : Visibility.Collapsed;

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
