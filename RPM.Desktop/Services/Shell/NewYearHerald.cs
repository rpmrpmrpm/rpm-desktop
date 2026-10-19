using RPM.Core.Models;
using RPM.Desktop.Presentation.Pegasus;
using RPM.Desktop.Services.Library;
using Microsoft.UI.Dispatching;

namespace RPM.Desktop.Services.Shell;

/// <summary>
/// Watches the clock for the whole life of the app. At midnight on the first of January, it plays
/// ABBA's "Happy New Year" and brings Pegasus up with a greeting over the room.
///
/// <para>This is the only feature in rpm that starts playing music without being asked, so it is kept
/// narrow. The rule for when (<see cref="NewYear"/>) covers one minute of one night a year. The rule for
/// what is played is one record that must already be in the collection. A library without that record
/// shows nothing: no message, no offer, and no note that it was nearly midnight. An easter egg that
/// announces its own absence would not be one.</para>
///
/// <para><b>Polled, not scheduled.</b> A timer set to fire in six months cannot be relied on. The machine
/// may sleep or hibernate. Its clock changes by an hour twice a year, and the user can change it at any
/// time. So a countdown started in June would be wrong by midnight in December. Checking the wall clock
/// every <see cref="Interval"/> is correct by design. Whatever the machine did in between, the question
/// is always "what time is it now".</para>
///
/// <para>The ordinary tick is cheap. On 364 days of the year the work is one comparison against today's
/// date. The library is not opened at all.</para>
/// </summary>
public sealed class NewYearHerald
{
    /// <summary>How often to look at the clock. This is well inside <see cref="NewYear.Grace"/>, so
    /// midnight cannot fall between two polls. It is cheap at this rate, because on every day but one
    /// the poll stops at a date comparison.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);

    private readonly LibraryService _library;
    private readonly ViewModeService _views;
    private readonly DispatcherQueue _dispatcher;
    private readonly NewYearGate _gate = new();

#if DEBUG
    /// <summary>Dev shortcut: <c>--new-year</c> makes the herald act as though the clock had just struck.
    /// This lets something that happens for one minute a year be checked on any day. It waits for the
    /// same conditions as everything else: a built window, and the record actually being in the
    /// collection. Then it fires once. This is for debug builds only, and a shipped build cannot reach
    /// it.</summary>
    private bool _rehearsing = Environment.GetCommandLineArgs().Contains("--new-year");

    /// <summary>The gallery's way in. It is the same rehearsal that <c>--new-year</c> asks for, but a
    /// button starts it after the app is already up.
    /// <para>It runs the check at once, rather than setting the flag and waiting for the next poll.
    /// Twenty seconds is a long time to wait in front of a button. Everything after the clock check is
    /// the real path. The record is looked for in whatever library is open, using the same rule, and it
    /// is played the same way.</para>
    /// </summary>
    /// <returns>True if the year was seen in. False means a condition was not met. This is most likely
    /// a library with no ABBA in it. The flag stays armed, so switching to one that has it will fire on
    /// the next poll rather than needing the button again.</returns>
    internal Task<bool> RehearseAsync()
    {
        _rehearsing = true;
        return TryGreetAsync();
    }
#endif

    /// <summary>The player is fetched at midnight, and not before. This service is built at startup,
    /// because it has to be watching before midnight. Taking the player here would start the whole
    /// transport, with its idle timers, on every launch. Most launches never play anything.
    /// <see cref="ViewModeService"/> is handled the same way, for the same reason.</summary>
    private readonly Lazy<RPaMpService> _amp;

    public NewYearHerald(LibraryService library, ViewModeService views, DispatcherQueue dispatcher,
        IServiceProvider services)
    {
        _library = library;
        _views = views;
        _dispatcher = dispatcher;
        _amp = new Lazy<RPaMpService>(services.GetRequiredService<RPaMpService>);

        _ = WatchAsync();
    }

    private async Task WatchAsync()
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            await TryGreetAsync();
        }
        while (await timer.WaitForNextTickAsync());
    }

    /// <summary>
    /// One look at the clock, and everything that follows from it. The whole of a poll, and in a debug
    /// build, the whole of a rehearsal too, which is why it is a method rather than the loop's body.
    /// </summary>
    /// <returns>True if the year was seen in on this pass. False is the usual answer, because it is the
    /// answer on 364 days a year. It also covers conditions that are not met yet. None of these settle
    /// anything, and the next pass asks again.</returns>
    private async Task<bool> TryGreetAsync()
    {
        try
        {
            // Local time, because midnight is when the people in the room say it is.
            var now = DateTimeOffset.Now;
            var due = _gate.Due(now);
#if DEBUG
            due |= _rehearsing;
#endif
            if (!due)
                return false;

            // The app can start inside the window, for example on a machine switched on a minute after
            // midnight. For the first moments of a launch there is no shell, so there is nothing to show
            // a greeting on. Nothing is settled in that case. The next poll finds a built window, and the
            // greeting is still due.
            if (!_views.CanShowFullScreen)
                return false;

            // The library is asked only now, and off the UI thread. This is a SQLite query. It runs at
            // the moment the screen is about to show something.
            var record = await Task.Run(FindRecord);
            if (record is null)
                return false;

            // This is marked before the hand-off, not after it. The gate remembers that this year has
            // been answered, and it has been. Everything after this point carries out the answer on
            // another thread. That step cannot fail in a way that is worth doing twice.
            _gate.Celebrated(now);
#if DEBUG
            _rehearsing = false;
#endif

            _dispatcher.TryEnqueue(() =>
            {
                // The record is played first. That way the deck already holds it when the screen asks
                // what is playing. This is also what shows the mirror ball on the opening frame (see
                // MirrorBall).
                _amp.Value.PlayNow(record);
                _views.ShowNewYear(record.FilePath);
            });

            return true;
        }
        catch
        {
            // A greeting is not worth a crash, a log line, or a second attempt. If the one night a
            // year goes wrong, it goes quietly.
            return false;
        }
    }

    /// <summary>
    /// The record, if the collection has it. Null for a library that is not open yet, and null for a
    /// library that does not own the song. Most libraries do not.
    /// </summary>
    private TrackInfo? FindRecord()
    {
        if (_library.Root is not { } root)
            return null;

        // The title narrows the query to a row or two out of a hundred thousand. The rule then decides,
        // because a LIKE match is broad. "Happy New Year's Day" by another artist also matches.
        foreach (var row in _library.Query(NewYear.Title))
        {
            if (NewYear.IsTheRecord(row.Title, row.Artist))
                return row.ToTrackInfo(root);
        }

        return null;
    }
}
