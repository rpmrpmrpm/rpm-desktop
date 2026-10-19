using RPM.Core.Ripping;
using Microsoft.UI.Dispatching;

namespace RPM.Desktop.Services.Library;

/// <summary>
/// Watches the optical drive for as long as the app runs. When a disc is inserted, it asks the user
/// whether to rip the disc.
///
/// <para>This polls the drive. It does not subscribe to an insertion event, because on macOS and
/// Windows that event goes only to the app the user made the default handler, not to every running app.
/// An app that is already open and wants to notice a disc has to look for it. The check is the
/// cheapest one that each platform allows: a mounted volume on macOS, and the cdfs stub files on
/// Windows. No drive handle is opened and no table of contents is read until the user says yes, so an
/// idle drive can stay asleep.</para>
///
/// <para>Being the OS default handler helps here. The OS launches the app, and the disc is already in
/// the drive when the watcher takes its first look.</para>
/// </summary>
public sealed class DiscWatcher
{
    /// <summary>How often to look. Often enough that the question is shown by the time the drive has spun
    /// the disc up and the user has looked at the screen. Rare enough to cost almost nothing.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(4);

    private readonly RipperService _ripper;
    private readonly RipJobService _rips;
    private readonly SettingsService _settings;
    private readonly DispatcherQueue _dispatcher;
    private readonly DiscOfferGate _gate = new();

    public DiscWatcher(RipperService ripper, RipJobService rips, SettingsService settings,
        DispatcherQueue dispatcher)
    {
        _ripper = ripper;
        _rips = rips;
        _settings = settings;
        _dispatcher = dispatcher;

        // A platform with no way to read a disc has nothing to watch for.
        if (_ripper.IsSupported)
            _ = WatchAsync();
    }

    private async Task WatchAsync()
    {
        // The first look happens straight away. The app may have been launched because a disc was
        // inserted, and waiting one interval would show the question late.
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                // Without a library there is nowhere to put a disc, and the first-run screen is not the
                // place to ask about one. The disc is not forgotten. The gate is only told about discs, so
                // a disc still in the drive is offered as soon as there is a library.
                if (_settings.LibraryPath is null)
                    continue;

                // The check reads the file system, so it runs off the UI thread, even though it is cheap.
                var present = await Task.Run(_ripper.CdPresent);

                // The gate is read and the question is shown in one step, on the thread that owns the rip
                // job. This way the busy state given to the gate is still true when the gate answers.
                _dispatcher.TryEnqueue(() =>
                {
                    if (_gate.Poll(present, _rips.State != RipJobState.Idle))
                        _rips.Offer(Loc.Get("Rip_OfferTitle"));
                });
            }
            catch
            {
                // A drive that answers badly is not worth a crash or a log line every four seconds.
            }
        }
        while (await timer.WaitForNextTickAsync());
    }
}
