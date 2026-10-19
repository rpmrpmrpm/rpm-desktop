using Microsoft.UI.Dispatching;

namespace RPM.Desktop.Presentation.Controls;

/// <summary>The wait mark that a main screen shows while its content is being read. It is the turning
/// record, 64 across, centred over the screen. The home screen shows the same mark while it reads again
/// (HomeView's ReadingState).
///
/// <para>It appears <see cref="Grace"/> after the read is known to be outstanding. So a read that finishes
/// sooner never shows it. It goes away as soon as the read has finished.</para>
///
/// <para>The screen does not say when a read starts or ends. Each time it asks whether its content has been
/// read from the open collection, it passes the answer to <see cref="Reflect"/>. So a call that arrives
/// late, or twice, cannot leave the record turning over content that is already there.</para>
///
/// <para>The caller passes false while its screen is not on view. That way a record never turns behind a
/// collapsed screen. See <see cref="RecordSpinner"/>, whose timer repaints the window.</para>
/// </summary>
public sealed partial class ShelfLoader : Grid
{
    /// <summary>The same grace period that the genre page and the playlist page use for their wait
    /// marks.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(180);

    private readonly RecordSpinner _spinner = new()
    {
        Width = 64,
        Height = 64,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // Held in a field. A DispatcherQueueTimer that nothing references is collected, and then it never
    // ticks.
    private DispatcherQueueTimer? _grace;
    private bool _reading;

    public ShelfLoader()
    {
        IsHitTestVisible = false;
        Children.Add(_spinner);
    }

    /// <summary>Whether the screen's content is being read, and the screen is on view.</summary>
    public void Reflect(bool reading)
    {
        if (reading == _reading)
            return;

        _reading = reading;

        if (!reading)
        {
            _grace?.Stop();
            _spinner.Spinning = false;
            return;
        }

        if (_grace is null)
        {
            _grace = DispatcherQueue.CreateTimer();
            _grace.IsRepeating = false;
            _grace.Tick += (_, _) => _spinner.Spinning = _reading;
        }

        _grace.Interval = Grace;
        _grace.Start();
    }
}
