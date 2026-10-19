using Microsoft.UI.Dispatching;
using RPM.Core.Radio;

namespace RPM.Desktop.Services.Audio;

/// <summary>
/// Counts how long each radio station has really been listened to. The totals go into <see
/// cref="RadioService.Listens"/>.
///
/// <para>This works the same way as <see cref="PlayCounter"/>, for the same reason. When the app is told
/// that the station changed, the engine has already moved on to the next stream, and its counters are
/// reset. So the engine is sampled while it plays. <see cref="RadioTally"/> adds up the listening as each
/// sample arrives.</para>
///
/// <para><b>This is not part of the play counter</b>, although that might seem the obvious place. The play
/// counter writes to the library mirror, using a path relative to the library. A station has no such path.
/// A station is not on the shelf, its log stays even when a different library is open, and the rule for
/// what counts as a listen is different. The two share only the sampling, which is about a dozen
/// lines.</para>
///
/// <para><b>Everything here runs on the UI thread.</b> The timer is a dispatcher timer, and the playback
/// event is queued onto the dispatcher. So the tally is only used from one thread. The values it reads
/// (<see cref="PlaybackService.HeardSeconds"/>, the play session and the tuned station) are written on the
/// audio thread. That is why the tally never accepts a lower reading: see <see
/// cref="RadioTally.Sample"/>.</para>
/// </summary>
public sealed class RadioCounter
{
    /// <summary>How often the engine is read. Once a second, like the play counter. The last sample before
    /// a station changes is then within a second of everything that was heard. That is well inside the
    /// tolerance of the thirty-second minimum listen.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private readonly PlaybackService _playback;
    private readonly RadioService _radio;
    private readonly DispatcherQueueTimer _timer;
    private readonly RadioTally _tally = new();

    /// <summary>The station each uuid stood for when it was last sampled. The tally works only with uuids
    /// and holds no directory record. The log needs the whole station, so the station is kept here between
    /// the two. There is only one entry, because a listen ends when the station changes, so only one listen
    /// is open at a time.</summary>
    private RadioStation? _tuned;

    /// <summary>Whether <see cref="Close"/> has been called. The window is closing. A sample that was
    /// already queued on the dispatcher must not start a new listen. The tally is empty by then, so it
    /// would start one from the engine's current counter, which could be an hour of radio, and that listen
    /// could be written a second time if the station changed before the process ended.</summary>
    private bool _closed;

    public RadioCounter(PlaybackService playback, RadioService radio, DispatcherQueue dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _playback = playback;
        _radio = radio;

        _timer = dispatcher.CreateTimer();
        _timer.Interval = Tick;
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Sample();

        // Both ends of a listen arrive as one of these events. Tuning a station in raises one, and so does
        // the player moving on to something that is not the radio. The timer keeps the total current in
        // between.
        _radio.Changed += () => dispatcher.TryEnqueue(Sample);
        _playback.Changed += () => dispatcher.TryEnqueue(Sample);
        Sample();
    }

    /// <summary>Reads the engine and writes down the listen the reading ended, if it ended one.</summary>
    private void Sample()
    {
        if (_closed)
            return;

        var station = _radio.Current;

        // Settle the old listen BEFORE the station in hand is replaced. The listen the tally returns
        // belongs to the station that was tuned a moment ago. If the new station were taken first, there
        // would be nothing to write that listen against. It would be lost silently, not written wrongly.
        var finished = _tally.Sample(station?.Uuid, _playback.PlaySession, _playback.HeardSeconds);
        if (finished is { } listen)
            Settle(listen);

        _tuned = station;

        if (station is null)
        {
            _timer.Stop();
            return;
        }

        _timer.Start();
    }

    /// <summary>Ends the listen in progress when the window closes with a station still on. The tally is
    /// left empty, so the listen cannot be written twice.</summary>
    public void Close()
    {
        if (_tally.Close() is { } listen)
            Settle(listen);

        _closed = true;
        _tuned = null;
        _timer.Stop();
    }

    /// <summary>Writes the finished listen to the log. The log drops a listen that is too short to notice
    /// (see <see cref="RadioListens.NoticedSeconds"/>). That rule lives in one place, so both heads use the
    /// same one.</summary>
    private void Settle(RadioTally.Listened listen)
    {
        if (_tuned is not { } station
            || !string.Equals(station.Uuid, listen.Uuid, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            _radio.Listens.Note(station, listen.HeardSeconds);
        }
        catch
        {
            // A log that cannot be written is not a reason to stop the radio.
        }
    }
}
