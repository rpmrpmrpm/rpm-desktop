using System.IO;
using Microsoft.UI.Dispatching;
using RPM.Core.Library;
using RPM.Core.Scanning;

namespace RPM.Desktop.Services.Library;

/// <summary>Watches the library's inbox (<see cref="LibraryInbox"/>) and reports when music has finished
/// arriving in it.
///
/// <para>This makes the inbox an entry point, not just a folder. A user can drag an album in from anywhere,
/// such as a file manager, a download or another machine's share. The queue picks it up while the app is
/// running, with no menu and no picker. The watcher moves to the new inbox whenever a different library is
/// opened.</para>
///
/// <para>Copying is not atomic. A folder of forty FLAC files arrives as hundreds of events, spread over the
/// time the disk takes. The last files are still being written while the first ones can already be read. If
/// the first event started a scan, the scan would cover half an album and read incomplete tags. So all
/// events are held until the folder has been <b>quiet</b> for <see cref="QuietMilliseconds"/>. Only then is
/// one notification raised. This is also why a 100 GB drag causes exactly one scan.</para>
/// </summary>
public sealed partial class InboxWatchService : IDisposable
{
    /// <summary>How long the inbox must stay untouched before what landed in it counts as finished. Long
    /// enough to cover the pauses in a slow copy, and short enough that a single dropped file is noticed
    /// quickly.</summary>
    private const int QuietMilliseconds = 2500;

    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _settle;

    private FileSystemWatcher? _watcher;
    private string? _inboxPath;

    public InboxWatchService(LibraryService library, DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;

        _settle = dispatcher.CreateTimer();
        _settle.Interval = TimeSpan.FromMilliseconds(QuietMilliseconds);
        _settle.IsRepeating = false;
        _settle.Tick += (_, _) => Raise();

        library.Opened += Watch;
        if (library.Root is { } open)
            Watch(open);
    }

    /// <summary>The inbox of the library currently being watched, or null when there is none.</summary>
    public string? InboxPath => _inboxPath;

    /// <summary>
    /// Music has arrived in the inbox and stopped arriving. Raised on the UI thread, with the inbox
    /// folder. The caller scans it as an ordinary source folder, because that is all it is.
    /// </summary>
    public event Action<string>? Arrived;

    /// <summary>Points the watcher at a library's inbox, creating it if it is not there. Watching a second
    /// library replaces the first. Only one library is watched at a time.</summary>
    public void Watch(string libraryRoot)
    {
        Stop();
        if (GalleryMode.Active)
            return;

        try
        {
            _inboxPath = LibraryInbox.Ensure(libraryRoot);
            _watcher = new FileSystemWatcher(_inboxPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
            };
            _watcher.Created += OnTouched;
            _watcher.Changed += OnTouched;
            _watcher.Renamed += OnTouched;
            // A watcher stops working when its folder is deleted or its buffer overflows. Watching the same
            // root again also creates the folder, which fixes the common case: the user removed the inbox.
            _watcher.Error += (_, _) => _dispatcher.TryEnqueue(() => Watch(libraryRoot));
            _watcher.EnableRaisingEvents = true;
        }
        catch
        {
            // A library on a volume that cannot be watched (some network shares) still works. It just does
            // not notice a drop until the next scan. The watcher is optional, so nothing else depends on
            // it.
            Stop();
        }
    }

    /// <summary>Whether the inbox holds music to queue. This is checked at startup, when there is no event
    /// to wait for, because files may have been dropped while the app was closed.</summary>
    public bool HasMusic() =>
        _inboxPath is { } path && Directory.Exists(path) && AudioFileScanner.Scan(path).Files.Count > 0;

    private void OnTouched(object sender, FileSystemEventArgs e)
    {
        // Directory events have no extension, but they must still restart the timer. An album folder
        // appears before the files inside it do.
        var extension = Path.GetExtension(e.Name ?? "");
        if (extension.Length > 0 && !AudioFileScanner.AudioExtensions.Contains(extension))
            return;

        // The timer runs on the UI thread, so the change is passed there through the dispatcher.
        _dispatcher.TryEnqueue(() =>
        {
            _settle.Stop();
            _settle.Start();
        });
    }

    private void Raise()
    {
        if (_inboxPath is { } path && HasMusic())
            Arrived?.Invoke(path);
    }

    public void Stop()
    {
        _settle.Stop();
        if (_watcher is { } watcher)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            _watcher = null;
        }
        _inboxPath = null;
    }

    public void Dispose() => Stop();
}
