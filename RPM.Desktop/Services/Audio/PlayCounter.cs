using RPM.Core.Library;
using RPM.Core.Playback;
using Microsoft.UI.Dispatching;

namespace RPM.Desktop.Services.Audio;

/// <summary>
/// Counts what has actually been listened to, into the library mirror.
///
/// <para>
/// The count is stored in the database and nowhere else. It is not a tag. A play is something the
/// listener did, not something the file is, and writing it into the audio would rewrite every track in
/// the library each time one finished. The album wall does not count plays either. It asks the database
/// how often a record has been played, the same way it asks for the record's name.
/// </para>
///
/// <para>The rule is in <see cref="Listen"/>. A play counts when half the track or four minutes has
/// been heard, whichever comes first. A track under thirty seconds never counts. The phone uses the
/// same rule, so both versions agree on what a play is. This class adds the desktop's part. The engine
/// is sampled while it plays, and <see cref="ListenTally"/> adds up the listening as each sample
/// arrives. The engine is sampled rather than asked at the moment of the change. By the time the track
/// end is reported, the engine is already on the next track and its counters have been reset.</para>
/// </summary>
public sealed class PlayCounter
{
    /// <summary>The sampling interval. It is short enough that the last sample before a changeover is
    /// within one second of the real end. That is well within the half-track rule. It is long enough to
    /// cost almost nothing.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private readonly PlaybackService _playback;
    private readonly LibraryService _library;
    private readonly DispatcherQueueTimer _timer;

    /// <summary>What has been heard of the record on the player. It is kept across seeks within the
    /// record.</summary>
    private readonly ListenTally _tally = new();

    /// <summary>Raised on the UI thread when a listen has been written to the log. It carries no data.
    /// A listener reads the log again when it is raised, and no listener needs to know which track it
    /// was.</summary>
    public event Action? Noted;

    public PlayCounter(PlaybackService playback, LibraryService library, DispatcherQueue dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _playback = playback;
        _library = library;

        _timer = dispatcher.CreateTimer();
        _timer.Interval = Tick;
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Sample();

        _playback.Changed += () => dispatcher.TryEnqueue(Sample);
        Sample();
    }

    /// <summary>
    /// Reads the engine, and records the listen that ended, if one ended.
    ///
    /// <para>Both parts matter. Sampling keeps the tally current while a record plays. The listen that
    /// the tally returns on a track change becomes a play. That also applies when the same track is
    /// started again from the top, as the repeat button does.</para>
    /// </summary>
    private void Sample()
    {
        var now = _playback.PlayingPath;

        var finished = _tally.Sample(
            now,
            _playback.PlaySession,
            _playback.StartOffsetSeconds,
            _playback.HeardSeconds,
            _playback.DurationSeconds);

        if (finished is { } listen)
            Settle(listen);

        if (now is null)
        {
            _timer.Stop();
            return;
        }

        _timer.Start();
    }

    /// <summary>
    /// Writes the finished listen to the log.
    ///
    /// <para>Every listen worth noting is written, not only the ones that counted. A track stopped
    /// after nine seconds says a lot about what the listener likes. <see cref="ListenEntry.At"/>
    /// decides which kind it is. It returns null for a track that was only passed over, so the log does
    /// not fill up while the pointer moves down an album wall.</para>
    ///
    /// <para>The time is a <see cref="DateTimeOffset"/>, not <c>DateTime.UtcNow</c>. The log keeps the
    /// hour on the listener's own clock. That hour can be read now, and the offset changes when the
    /// listener travels or the clocks change.</para>
    /// </summary>
    private void Settle(ListenTally.Listened listen)
    {
        if (!_library.IsOpen || _library.Root is not { } root)
            return;

        try
        {
            var relative = Path.GetRelativePath(root, listen.Path).Replace(Path.DirectorySeparatorChar, '/');
            // A file played from outside the library has no row to count against. Path.GetRelativePath
            // returns a path that climbs out of the root, and does not report an error.
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                return;

            if (ListenEntry.At(DateTimeOffset.Now, relative, listen.HeardSeconds, listen.LengthSeconds) is not { } entry)
                return;

            if (_library.Db.NoteListen(entry))
            {
                // This tells anything drawn from the log that it is out of date. The home screen is the
                // only listener today. Each row on it asks what has been played, so a new play is the
                // one change that can make it wrong. This runs on the UI thread, because it runs on a
                // dispatcher timer.
                Noted?.Invoke();
            }

            // The same play is also sent as a count to the shared catalogue. Only plays that counted
            // are sent. The catalogue answers which songs people choose, and a track stopped after nine
            // seconds says the opposite. The request is fire and forget. The service decides whether
            // anything is sent at all, because that is a setting. A play it does not send is missing
            // from the total.
            if (entry.Counted)
                App.Services.GetService<Catalog.PlayReportService>()?.Note(relative);
        }
        catch
        {
            // The library was closed during the track, or the database refused the write. A lost play
            // is not worth interrupting anything for.
        }
    }
}
