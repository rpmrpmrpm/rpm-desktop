using RPM.Core.Library;

namespace RPM.Desktop.Services.Library;

/// <summary>
/// The collection that a record's heart belongs to. There is no default, because the two surfaces that ask
/// can be looking at different collections at the same moment.
///
/// <para>A folder name has a meaning only inside the collection it came from. The window's album wall, the
/// album list and the album page all draw the shelf: a borrowed shelf while one is open. A heart pressed
/// there is about a record on another computer's disk. The visualizer's wall always lists the listener's
/// own records, whatever the window is browsing. A heart pressed there is about one of the listener's own
/// records. A row of this library's own with the same folder name is a different record.</para>
///
/// <para>This is the same distinction that <c>AlbumCovers.LoadAsync</c> makes with <c>ownCollection</c>,
/// for the same reason. Asked of the wrong mirror, the answer is not an error. It is a plausible mark on
/// the wrong record.</para>
/// </summary>
public enum AlbumShelf
{
    /// <summary>The collection the window is showing.</summary>
    OnScreen,

    /// <summary>This computer's own collection, whatever is on screen.</summary>
    Own,
}

/// <summary>
/// Owns the open library: the SQLite mirror of the central music folder. There is one instance for the life
/// of the app. Pages query the mirror instead of walking the file system. Anything that changes the library
/// (import, sync) raises <see cref="Changed"/>, so the grid refreshes.
/// </summary>
public sealed partial class LibraryService : IDisposable
{
    private readonly object _gate = new();
    private readonly Account.AuthService _auth;

    /// <param name="auth">Who is signed in. This decides whether the mirror may show the records that exist
    /// only in the cloud. The rule is kept here, not in the cloud services, because it must be on the
    /// mirror before anything reads it. It is applied inside <see cref="Open"/>, before the <see
    /// cref="Changed"/> event sends every view to the database. So no view draws the wall with the wrong
    /// rule.</param>
    public LibraryService(Account.AuthService auth)
    {
        _auth = auth;
        _auth.Changed += OnAccountChanged;
    }

    /// <summary>The user's own mirror. It is never swapped for another mirror. See <see
    /// cref="Shelf"/>.</summary>
    private LibraryDatabase? _db;

    /// <summary>A borrowed mirror, used while one is on screen. Null at other times, which is most of the
    /// time.</summary>
    private LibraryDatabase? _shelf;

    /// <summary>Raised (possibly off the UI thread) after the mirror content changed.</summary>
    public event Action? Changed;

    /// <summary>Raised with the library root right after a library is opened. This is different from <see
    /// cref="Changed"/>, which fires for every edit. This event fires when the collection is a different
    /// one. Anything that watches the folder must re-aim at the new root.</summary>
    public event Action<string>? Opened;

    public bool IsOpen => _db is not null;

    /// <summary>
    /// Which computer's collection is on screen, or null for the user's own.
    ///
    /// <para>Only what is <em>drawn</em> follows this. See <see cref="Shelf"/>.</para>
    /// </summary>
    public string? ViewingPeer { get; private set; }

    /// <summary>Whether what is on screen belongs to somebody else.</summary>
    public bool IsPeerView => ViewingPeer is not null;

    /// <summary>The user's own library folder.</summary>
    public string? Root => _db?.Root;

    /// <summary>The user's own mirror. Everything that writes anything (the import, the sync, the cloud,
    /// the analysis passes, the play counts) uses this and nothing else.
    ///
    /// <para>It is never swapped for a borrowed mirror. Every pass that walks the library holds this
    /// object, and none of them check whose it is. If it were swapped while a friend's shelf was on screen,
    /// a cloud pass would write the user's own cloud rows into the friend's database. Nothing would be
    /// lost, but the write would still be wrong.</para>
    /// </summary>
    public LibraryDatabase Db =>
        _db ?? throw new InvalidOperationException("Library not opened yet.");

    /// <summary>
    /// The collection on screen: a borrowed one while one is open, the user's own otherwise.
    ///
    /// <para>This is the read side only. A shelf is something to look at, such as the wall, the track list
    /// or the album page. Nothing that only looks at a shelf should write to it.</para>
    /// </summary>
    public LibraryDatabase Shelf => _shelf ?? Db;

    /// <summary>The folder behind whatever <see cref="Shelf"/> is showing. A borrowed shelf's folder holds
    /// a mirror with no music in it. The music of a record on another computer stays on that computer's
    /// disk.</summary>
    public string? ShelfRoot => (_shelf ?? _db)?.Root;

    /// <summary>What <see cref="Shelf"/> holds. The wall's query.</summary>
    public IReadOnlyList<LibraryTrack> ShelfQuery(string? search = null) =>
        _shelf is { } shelf ? shelf.Query(search) : Query(search);

    /// <summary>The failure that the user's own mirror has hit since it was opened. Null while the mirror
    /// answers. It is set by the first refused <see cref="Read{T}"/>, and cleared by <see cref="Open"/>.
    /// Try again, Choose another folder and the Settings folder change all call Open.</summary>
    private LibraryOpening.Trouble? _trouble;

    /// <summary>
    /// What is wrong with the open collection, or null when nothing is.
    ///
    /// <para>This is read directly, not taken from the <see cref="Troubled"/> event. The shell asks this
    /// when it is about to show or remove the problem screen. If a Try again succeeds before the event is
    /// handled, there is nothing left to show.</para>
    /// </summary>
    public LibraryOpening.Trouble? Trouble
    {
        get { lock (_gate) return _trouble; }
    }

    /// <summary>Raised once per mirror, on the thread of the read that failed, when <see cref="Trouble"/>
    /// goes from null to a failure.</summary>
    public event Action? Troubled;

    /// <summary>
    /// Reads a mirror and says whether it answered. This is how a screen reads the collection.
    ///
    /// <para>A refusal is never an empty answer. When the user's own mirror refuses, the shell shows the
    /// problem screen over the app (<see cref="Troubled"/>). When a borrowed shelf refuses, the shelf is
    /// put down and the user's own collection comes back. Either way the caller gets <see
    /// cref="Asked{T}.Unanswered"/>, and draws nothing for it. The screen it would have drawn on is covered
    /// or replaced.</para>
    ///
    /// <para>A mirror that is closed during the read is also <see cref="Asked{T}.Unanswered"/>, and it says
    /// nothing else. The library was swapped, and the swap raises <see cref="Changed"/>, which sends the
    /// screen back to read the new one.</para>
    ///
    /// <para>Any other exception is not caught. It is a defect in the code inside
    /// <paramref name="read"/>, not a fact about the collection. It reaches the crash report instead of
    /// becoming an empty list.</para>
    /// </summary>
    /// <param name="mirror">The mirror to read, as the caller took it: <see cref="Shelf"/> for what is on
    /// screen, <see cref="Db"/> for the user's own. It is handed to <paramref name="read"/> so the read
    /// cannot reach for a different one. It is also compared after a failure, to decide what the failure
    /// means.</param>
    /// <param name="what">What was being read, a few words for the ring: "a genre's popular
    /// songs".</param>
    /// <param name="read">The read. Keep it to the mirror's own calls. The shaping of the answer goes
    /// after, so a defect there is not mistaken for a database fault.</param>
    public Asked<T> Read<T>(LibraryDatabase mirror, string what, Func<LibraryDatabase, T> read)
    {
        // From now on, a mirror that is read here is also watched for refusals. This includes a peer's
        // shelf that a playing record reads from.
        Watch(mirror);

        try
        {
            return Asked.Answer(read(mirror));
        }
        catch (Exception ex) when (MirrorFaults.Of(ex) is { } fault)
        {
            if (fault is MirrorFault.Broken)
                Refused(mirror, $"reading {what}", ex);

            return Asked.Failed<T>($"{what}: {fault}");
        }
    }

    /// <summary>The mirrors whose <see cref="LibraryDatabase.Refused"/> this service listens to. Weak,
    /// so a borrowed shelf the peer store lets go of is not kept alive by being listened to.</summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<LibraryDatabase, object> _watched = new();

    /// <summary>
    /// Listens to a mirror's refusals. This is done once per mirror. After that, each command on the mirror
    /// reaches <see cref="Refused"/> when SQLite refuses it. This applies whether or not the caller used
    /// <see cref="Read{T}"/>. Many call sites read or write the mirror with no wrapper around them.
    /// </summary>
    private void Watch(LibraryDatabase mirror)
    {
        if (_watched.TryAdd(mirror, Seen))
            mirror.Refused += (sql, error) => Refused(mirror, $"running {Brief(sql)}", error);
    }

    private static readonly object Seen = new();

    /// <summary>The start of a command's SQL on one line, for the ring. SQL only: the values are
    /// parameters and are not in it.</summary>
    private static string Brief(string sql)
    {
        var line = string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= 80 ? line : line[..80] + "...";
    }

    /// <summary>Refusals that have already been handled. The mirror reports a refusal as it leaves the
    /// command. Then the catch in <see cref="Read{T}"/> sees the same exception again, and it is handled
    /// only the first time. This table is weak, so it does not keep the exceptions alive.</summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Exception, object> _reported = new();

    /// <summary>Whether <paramref name="error"/> is a refusal that this service has already shown on screen
    /// (the problem screen, or a borrowed shelf put down) and written to the diagnostic ring. The app's
    /// last-chance handler asks this about an uncaught exception. Such a refusal is not a crash. Ending the
    /// process over it would also close the problem screen.
    /// </summary>
    public bool Reported(Exception error) => _reported.TryGetValue(error, out _);

    /// <summary>A mirror refused a command. The decision depends on which mirror it came from, not on when
    /// it arrived. The user's own mirror shows the problem screen. A borrowed shelf on screen is put down.
    /// Any other mirror (one replaced while the read ran, or a peer's shelf read by a playing record) is
    /// written to the ring and changes nothing.
    ///
    /// <para>This is called on the thread of the command, from <see cref="LibraryDatabase.Refused"/>. That
    /// thread still holds the mirror's gate. So nothing here reads a mirror. The <see cref="Changed"/>
    /// event that a put-down shelf raises is sent from a worker, not inline. Inline, its handlers would
    /// read the user's own mirror while the borrowed mirror's gate is held.</para>
    /// </summary>
    /// <param name="what">What was being done, for the ring line: "reading a genre's popular songs", "running
    /// SELECT ...".</param>
    private void Refused(LibraryDatabase mirror, string what, Exception error)
    {
        if (!_reported.TryAdd(error, Seen))
            return;

        // This is outside the lock. On a network volume that has gone away, checking whether the folder
        // exists can take as long as the share's own timeout.
        var root = mirror.Root;
        LibraryOpening.Trouble trouble = Directory.Exists(root)
            ? new LibraryOpening.Trouble.ReadFailed(root, error.Message)
            : new LibraryOpening.Trouble.FolderMissing(root);

        bool raise, repeat, putDown;
        lock (_gate)
        {
            var own = ReferenceEquals(mirror, _db);
            repeat = own && _trouble is not null;
            raise = own && _trouble is null;
            if (raise)
                _trouble = trouble;

            // The shelf is put down inside the same lock where it was compared. Calling ViewOwn after the
            // lock is released could put down a different shelf, if the user opened one in the meantime.
            putDown = ReferenceEquals(mirror, _shelf);
            if (putDown)
            {
                _shelf = null;
                ViewingPeer = null;
            }
        }

        // Not written again for a mirror that is already in trouble. A screen left up over it keeps reading
        // it, and each of those refusals is the same fact. A peer's shelf that is not on screen (see
        // TrackHome) changes nothing here, but it is still written down.
        if (!repeat)
            RPM.Core.Diagnostics.Faults.Note("library", what, error);

        if (putDown)
            Shell.Background.Run(() =>
            {
                Raise("a borrowed shelf that refused a read");
                return Task.CompletedTask;
            });

        if (raise)
            Troubled?.Invoke();
    }

    /// <summary>Opens (creating if needed) the mirror for the given library root: in the folder, or on
    /// this computer when the folder is on a network volume (<see cref="MirrorLocation.For"/>). Any
    /// library that was open before is closed once the new one stands, and a borrowed shelf is put
    /// down.</summary>
    /// <exception cref="Exception">Whatever the mirror could not do: the folder is read-only, the
    /// volume went away, the migration failed. The open library is left as it was.</exception>
    public void Open(string libraryRoot)
    {
        // The new mirror is built before the old one is released. Building it reaches the disk: it creates
        // the .rpm folder, opens two SQLite connections and migrates them. So it can fail. If the old
        // mirror were closed first and the build then failed, the field would hold a disposed mirror. Every
        // later read would throw ObjectDisposedException, and only a restart would recover.
        var opened = new LibraryDatabase(MirrorLocation.For(libraryRoot, LocalData.Libraries))
        {
            LocalRowsOnly = !_auth.IsLoggedIn,
        };
        Watch(opened);

        LibraryDatabase? previous;
        lock (_gate)
        {
            previous = _db;
            _db = opened;

            // A borrowed shelf may be on screen beside the collection that is now gone. It is dropped, not
            // disposed, because the peer store owns it. The peer store opened it and will close it.
            _shelf = null;
            ViewingPeer = null;

            // A refusal from the old mirror says nothing about the new one. This is cleared in the same
            // lock as the swap. So a refusal from the old mirror that arrives later finds a mirror that is
            // not _db, and changes nothing (see Refused).
            _trouble = null;
        }

        // Closing waits up to two seconds per gate for a pass that is still inside to finish. Open is
        // called from a button, so the close runs off the UI thread, after the swap. Background work that
        // still holds the old mirror is refused by name from then on.
        if (previous is not null)
            Shell.Background.Run(() =>
            {
                previous.Dispose();
                return Task.CompletedTask;
            });

        // The inbox is created before anything else looks at the folder. A user who opens the library in a
        // file manager can see where music should go, without being told.
        if (!GalleryMode.Active)
        {
            try { LibraryInbox.Ensure(libraryRoot); }
            catch { /* A read-only volume still opens. The inbox is optional. */ }
        }

        Raise("Open");
        Opened?.Invoke(libraryRoot);
    }

    /// <summary>Opens the configured central music folder and says what happened, as the one fact the
    /// opening decision is made from. Reaches the disk. <see cref="OpeningScreen.Choose"/> is the pure
    /// half.
    ///
    /// <para>Every way the open can fail has its own named answer. If the shell caught all failures and
    /// reported them as "no folder", an unmounted disk or a corrupt database would open the setup wizard,
    /// as if the app had never run. Nothing is thrown, so no caller can forget to handle a failure.</para>
    ///
    /// <para>The library is opened before the album count is asked, because the count reads its mirror. If
    /// any step fails, the library that was open before is left as it was.</para>
    /// </summary>
    /// <param name="configuredPath">The saved central music folder, or null when none is saved.</param>
    public LibraryOpening OpenForLanding(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return new LibraryOpening.NoFolder();

        if (!Directory.Exists(configuredPath))
            return new LibraryOpening.Trouble.FolderMissing(configuredPath);

        try
        {
            Open(configuredPath);
        }
        catch (LibraryTooNewException ex)
        {
            RPM.Core.Diagnostics.Scope.Said("boot",
                $"the library is newer than this build: version {ex.Found}, supported {ex.Supported}");
            return new LibraryOpening.Trouble.TooNew(configuredPath, ex.Found, ex.Supported);
        }
        catch (Exception ex)
        {
            RPM.Core.Diagnostics.Scope.Said("boot", $"the library could not be opened: {ex.Message}");
            return new LibraryOpening.Trouble.Unreadable(configuredPath, ex.Message);
        }

        try
        {
            // Use Anywhere, not OnThisComputer. A collection that the account holds in the cloud, and for
            // which this machine has no files, is still a collection. The wall draws it and it can play.
            // Opening the setup wizard over it would ask the user to set up something that is already set
            // up.
            return new LibraryOpening.Opened(configuredPath, Db.CountAlbumFolders(TrackScope.Anywhere));
        }
        catch (Exception ex)
        {
            // The mirror opened but does not answer queries. It gets the same screen as a mirror that will
            // not open. Nothing can be shown, and the user did not cause the problem.
            RPM.Core.Diagnostics.Scope.Said("boot", $"the library would not be counted: {ex.Message}");
            var unreadable = new LibraryOpening.Trouble.Unreadable(configuredPath, ex.Message);

            // This is stored as well as returned, but it is not raised as an event. The caller shows this
            // answer itself. Without the stored value, the mirror that refused the count would look
            // healthy. The shell would then remove a problem screen that it was just asked to keep (Open
            // raised Opened above).
            lock (_gate)
                _trouble ??= unreadable;

            return unreadable;
        }
    }

    /// <summary>
    /// Shows another computer's collection in place of this one's.
    ///
    /// <para>Nothing is closed and nothing is set aside. The user's own library stays open behind it. So
    /// the import keeps importing into it, the cloud keeps syncing it, and the machines it shares with are
    /// still offered it. Only what is drawn changes.</para>
    ///
    /// <para>This is deliberately not an <see cref="Opened"/> event. That event means "the collection this
    /// machine keeps is now that folder", and the inbox watcher and the sharing server re-aim at it.
    /// Neither should follow another computer's shelf. If they did, a borrowed listing, which is empty,
    /// would be offered to the network for as long as the user was looking at it.</para>
    /// </summary>
    public void ViewPeer(string deviceId, LibraryDatabase library)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(library);
        Watch(library);

        lock (_gate)
        {
            _shelf = library;
            ViewingPeer = deviceId;
        }

        Raise("ViewPeer");
    }

    /// <summary>Back to the user's own collection.</summary>
    public void ViewOwn()
    {
        lock (_gate)
        {
            if (_shelf is null)
                return;

            _shelf = null;
            ViewingPeer = null;
        }

        Raise("ViewOwn");
    }

    /// <summary>Reconciles the mirror with the folder on a background thread. Safe to call
    /// repeatedly; raises <see cref="Changed"/> when the pass altered anything.</summary>
    public async Task<SyncResult?> SyncAsync(IProgress<SyncProgress>? progress = null, CancellationToken ct = default)
    {
        // The gallery demo library (the --gallery option) names files that are not on disk. Reconciling it
        // against the empty folder would mark every row as deleted and drop the whole demo. See
        // GalleryMode.
        if (GalleryMode.Active)
            return null;
        if (_db is not { } db)
            return null;
        // Quarantine is on. Music copied straight into the collection goes to the inbox, to be identified
        // and filed. It is not adopted under the folder where it was dropped. The synchronizer skips the
        // first pass over an empty mirror by itself. That pass is a library being adopted, not music being
        // copied in.
        var result = await Task.Run(
            () => LibrarySynchronizer.Sync(db, readTrack: null, progress, ct, quarantineForeign: true));
        if (result.Added + result.Updated + result.Removed + result.Moved + result.Quarantined > 0)
            Raise(FormattableString.Invariant(
                $"SyncAsync (+{result.Added} ~{result.Updated} -{result.Removed} moved {result.Moved} held {result.Quarantined})"));
        return result;
    }

    public IReadOnlyList<LibraryTrack> Query(string? search = null) =>
        _db is { } db ? db.Query(search) : Array.Empty<LibraryTrack>();

    public int Count => _db?.Count ?? 0;

    /// <summary>Import and sync call this after touching the mirror directly.</summary>
    /// <remarks>
    /// The caller names itself through CallerMemberName, so the call sites need no arguments. Without this,
    /// the question "who changed the shelf just now?" has no answer. That is the question people ask when a
    /// home screen re-reads itself for no visible reason. Read the log with: rpmscope log --channel library
    /// </remarks>
    public void NotifyChanged(
        [System.Runtime.CompilerServices.CallerMemberName] string by = "",
        [System.Runtime.CompilerServices.CallerFilePath] string from = "") =>
        Raise($"{System.IO.Path.GetFileNameWithoutExtension(from)}.{by}");

    /// <summary>A borrowed shelf's own database was written. Raises <see cref="Changed"/> only when that
    /// shelf is the one on screen.
    /// </summary>
    /// <remarks>Every listener on <see cref="Changed"/> reads either <see cref="Db"/> or
    /// <see cref="Shelf"/>. A borrowed shelf that is not on screen is neither.
    /// If this event were raised anyway, the home screen, the wall and the playlist panel would re-read the
    /// user's own collection, which had not changed. Home would hide itself behind its loader after every
    /// pull from another computer.
    ///
    /// <para>This is safe if a switch to that shelf happens at the same moment. The reason is that the
    /// caller commits its write before it calls this method. <see cref="ViewPeer"/> raises its event after
    /// it sets <see cref="ViewingPeer"/>. Whichever of the two takes <c>_gate</c> second, a later read sees
    /// the write.</para></remarks>
    public void NotifyPeerShelfChanged(
        string deviceId,
        [System.Runtime.CompilerServices.CallerMemberName] string by = "",
        [System.Runtime.CompilerServices.CallerFilePath] string from = "")
    {
        bool onScreen;
        lock (_gate)
            onScreen = string.Equals(ViewingPeer, deviceId, StringComparison.OrdinalIgnoreCase);

        if (onScreen)
            Raise($"{System.IO.Path.GetFileNameWithoutExtension(from)}.{by} (borrowed shelf on screen)");
    }

    /// <summary>Says the shelf has moved, and says who says so.
    /// </summary>
    /// <remarks>Every raiser goes through here. <see cref="Changed"/> sends every view in the app back to
    /// the database. The home screen, for example, re-reads the whole shelf and fetches every picture on it
    /// again. So a raise that nobody can attribute is a cost that nobody can find.</remarks>
    private void Raise(string by)
    {
        RPM.Core.Diagnostics.Scope.Said("library", $"shelf changed, by {by}");
        Changed?.Invoke();
    }

    /// <summary>
    /// Somebody signed in or out, so what the mirror may show has changed.
    ///
    /// <para>Records that exist only in the cloud need an account to play. This is the only place that
    /// knows both facts at once. Nothing is deleted. The rows are the only record that the music was
    /// uploaded, and they come back when the account does. A borrowed shelf works the same way, through
    /// <see cref="LibraryDatabase.CloudRowsOnly"/>.</para>
    ///
    /// <para>This can run on any thread. The session is restored on a worker at startup, and signing out
    /// runs on the UI thread. Setting a bool and raising the event are both safe on any thread. What must
    /// not happen is a view reading the mirror before the rule is set. So <see cref="Open"/> applies the
    /// rule itself, and does not wait for this event.</para>
    /// </summary>
    private void OnAccountChanged()
    {
        LibraryDatabase db;
        lock (_gate)
        {
            if (_db is not { } open)
                return;   // Nothing to narrow yet. Open applies the rule when a library is open.
            db = open;
        }

        var hide = !_auth.IsLoggedIn;
        if (db.LocalRowsOnly == hide)
            return;   // The same answer as last time. A redraw is not needed.

        db.LocalRowsOnly = hide;
        Raise(hide ? "OnAccountChanged (signed out)" : "OnAccountChanged (signed in)");
    }

    // ── one song's heart ──
    //
    // A favourite is one row and one bit. It is written from three places: the heart at the end of a track
    // line, the song's own menu, and the key under the words in the full-screen player. It lives here, and
    // not in whichever screen was written first. Those screens can be on top of one another. The player is
    // full screen, with the album wall still behind it. A mark made on one screen, which the others only
    // learn about on their next rebuild, shows as full on one surface and empty on another.
    //
    // The mark is written to whichever collection holds the file. On a borrowed shelf this is the one thing
    // that is written, not only read. The mark is not the owner's data. It is what the listener at this
    // desk thinks of a record on somebody else's computer. It is kept here, in the mirror held for that
    // computer. It never travels back, because the listing goes one way only. It lives exactly as long as
    // the row does. So if the friend deletes a record, the heart goes with it. This is the honest result
    // for a mark on something that was never this machine's to keep.

    /// <summary>Raised with a song's absolute path and its new mark, right after the mark is written. It
    /// runs on the thread that made the write. In practice this is always the UI thread, because a heart
    /// changes only when the user presses it.
    /// </summary>
    public event Action<string, bool>? TrackFavoriteChanged;

    /// <summary>Writes one song's favourite mark and tells everything that is showing that song.
    /// </summary>
    /// <returns>False when nothing was written. That happens when no library is open, or when
    /// the file is played from outside the collection and has no row to hold the mark. The heart must not
    /// fill in either way.</returns>
    public bool SetTrackFavorite(string filePath, bool favorite)
    {
        if (HomeOf(filePath) is not var (mirror, relative)
            || !mirror.SetTrackFavorite(relative, favorite, DateTime.UtcNow.Ticks))
            return false;

        TrackFavoriteChanged?.Invoke(filePath, favorite);
        return true;
    }

    /// <summary>Whether one song is a favourite right now. False for a file that no collection here holds.
    /// Such a file cannot be marked, because there is nowhere to save the mark.</summary>
    public bool IsTrackFavorite(string filePath) =>
        HomeOf(filePath) is var (mirror, relative) && mirror.IsTrackFavorite(relative);

    /// <summary>Writes what the listener has said about a song in ONE KIND OF GENERATED LIST: loved,
    /// turned down, or no verdict.
    ///
    /// <para>The kind is part of what is said. A song can fit a calm list and not a dancefloor list. A
    /// verdict with no list attached would be an opinion about nothing. So there is no default kind. The
    /// caller must say which list it speaks for.</para>
    ///
    /// <para><b>No favourite event, on purpose.</b> This is not a heart. See <see cref="TrackVerdict"/>,
    /// which explains why the two are separate facts. So the surfaces that redraw a heart have nothing to
    /// redraw for a verdict. Only the transport draws a fit verdict, and it re-reads itself.</para>
    /// </summary>
    /// <returns>False when no collection here holds the file: a radio stream, or something
    /// dragged in off the desktop. There is nowhere to write a verdict about it.</returns>
    public bool SetTrackVerdict(string filePath, SmartPlaylistKind kind, TrackVerdict verdict) =>
        HomeOf(filePath) is var (mirror, relative)
        && mirror.SetTrackVerdict(relative, kind, verdict, DateTime.UtcNow.Ticks);

    /// <summary>Whether the listener said that one song fits one kind of list. Returns <see
    /// cref="TrackVerdict.None"/> for a file that no collection here holds. That is the same answer given
    /// for a song that nobody has an opinion about. A caller that needs to tell these apart should ask <see
    /// cref="HoldsTrack"/>.</summary>
    public TrackVerdict TrackVerdictOf(string filePath, SmartPlaylistKind kind) =>
        HomeOf(filePath) is var (mirror, relative)
            ? mirror.TrackVerdictOf(relative, kind)
            : TrackVerdict.None;

    /// <summary>Whether a collection here holds this file at all. This is the difference between a mark
    /// that is off and a mark that cannot be made. A file played from the desktop has no row to hold a
    /// mark. A control that offers one anyway would offer something that cannot happen.</summary>
    public bool HoldsTrack(string filePath) =>
        HomeOf(filePath) is var (mirror, relative) && mirror.FindByRelativePath(relative) is not null;

    /// <summary>
    /// Which mirror carries this file's row, and what it is keyed under there.
    ///
    /// <para>The shelf on screen is checked first, then this library's own. A borrowed shelf's paths sit
    /// under a folder of their own, so they cannot be mistaken for this collection's paths. The order still
    /// matters. A heart pressed on screen is about the shelf that is being looked at.</para>
    ///
    /// <para>The result is a tuple, which callers match with a pattern. There is no row for a radio stream
    /// or for a file dragged in from the desktop. In that case the result is null, and the heart must not
    /// fill in.</para>
    /// </summary>
    private (LibraryDatabase Mirror, string Relative)? HomeOf(string filePath)
    {
        if (!IsOpen || string.IsNullOrEmpty(filePath))
            return null;

        if (_shelf is { } shelf && Under(shelf.Root, filePath) is { } borrowed)
            return (shelf, borrowed);

        return Root is { } root && Under(root, filePath) is { } own ? (Db, own) : null;
    }

    /// <summary>A song's path as a mirror keys it: relative to that mirror's root, forward slashes.
    /// Null for anything outside it.</summary>
    private static string? Under(string? root, string filePath)
    {
        if (string.IsNullOrEmpty(root))
            return null;

        var relative = System.IO.Path.GetRelativePath(root, filePath)
                             .Replace(System.IO.Path.DirectorySeparatorChar, '/');
        return relative.StartsWith("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(relative)
            ? null
            : relative;
    }

    // ── a whole record's heart ──
    //
    // A record's heart is a different mark from a song's. It is on a different row: album_meta holds it,
    // the wall draws its sticker from it, and the heart in the library header narrows the shelf to it. It
    // is kept here for the same reason as the song's heart. It is written from the full-screen player as
    // well as from the shelf, and those two can be on top of one another.

    /// <summary>Raised with an album's folder and its new mark, right after one is written.</summary>
    public event Action<string, bool>? AlbumFavoriteChanged;

    /// <summary>Whether the listener has marked this record a favourite, in the collection the caller
    /// says it is looking at.</summary>
    public bool IsAlbumFavorite(string folder, AlbumShelf shelf) =>
        IsOpen && !string.IsNullOrEmpty(folder) && Mirror(shelf).GetAlbumMeta(folder).IsFavorite;

    /// <summary>
    /// Marks (or unmarks) a whole record, and tells everything showing it.
    ///
    /// <para>The mark goes into the shelf, for the same reason a song's mark goes into the mirror that
    /// holds it. A folder name has a meaning only inside the collection it came from. While a borrowed
    /// shelf is open, every album on screen is one of its folders. A row of this library's own with the
    /// same name is a different record.</para>
    ///
    /// <para>The mark never leaves this machine. A guest's listing carries no favourite in either
    /// direction. See PeerCatalogBuilder.AlbumAudience for what goes out, and ArrivingAlbumMarks for what
    /// comes in. So the mark written here belongs to the borrower, and the owner is never told.</para>
    /// </summary>
    /// <returns>False when there is no library open to write it to.</returns>
    public bool SetAlbumFavorite(string folder, bool favorite, AlbumShelf shelf)
    {
        if (!IsOpen || string.IsNullOrEmpty(folder))
            return false;

        Mirror(shelf).SetAlbumFavorite(folder, favorite);
        AlbumFavoriteChanged?.Invoke(folder, favorite);
        return true;
    }

    private LibraryDatabase Mirror(AlbumShelf shelf) => shelf is AlbumShelf.OnScreen ? Shelf : Db;

    /// <summary>
    /// The passes that hold the library across many calls, each with the call that stops it. See <see
    /// cref="StopBeforeClose"/>.
    /// </summary>
    private readonly List<Func<Task>> _holders = [];

    /// <summary>How long <see cref="Dispose"/> waits for the passes in <see cref="_holders"/> to stop before
    /// it closes the library anyway. The window has already gone, so this is time the person does not see.
    /// A pass that is still running after it is refused at the gate (<c>Gate.Take</c> throws
    /// <see cref="ObjectDisposedException"/>), which is the last line of defence and not the plan.</summary>
    internal TimeSpan HoldersWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Registers a pass that holds the library across many calls, such as the cloud sync run, with the call
    /// that stops it. <see cref="Dispose"/> calls <paramref name="stop"/> and waits for the task it returns
    /// before the library is closed. So a pass is never left in the middle of a read or a write on a
    /// database that has been closed under it.
    /// </summary>
    /// <param name="stop">Stops new work, cancels the work in progress, and completes when the pass no
    /// longer touches the library. It is run on a pool thread, and it must not need the UI thread: the
    /// window's close waits for it there.</param>
    public void StopBeforeClose(Func<Task> stop)
    {
        ArgumentNullException.ThrowIfNull(stop);
        lock (_holders)
            _holders.Add(stop);
    }

    /// <summary>Stops every pass registered with <see cref="StopBeforeClose"/>, and waits for them up to
    /// <see cref="HoldersWait"/>. A stop that throws counts as stopped.</summary>
    /// <returns>False when the wait ran out.</returns>
    private bool StopHolders()
    {
        Func<Task>[] holders;
        lock (_holders)
            holders = [.. _holders];

        if (holders.Length == 0)
            return true;

        // Each stop is started on the pool. A stop that ran on this thread and then needed it would wait for
        // the wait below.
        var stopping = Task.WhenAll(holders.Select(stop => Task.Run(async () =>
        {
            try { await stop().ConfigureAwait(false); }
            catch { /* a stop that failed has still stopped asking for the library */ }
        })));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (stopping.Wait(HoldersWait))
        {
            // Said on every close, so the wait above can be judged against real closes.
            RPM.Core.Diagnostics.Scope.Said("library",
                $"{holders.Length} passes stopped in {watch.ElapsedMilliseconds} ms before the close");
            return true;
        }

        RPM.Core.Diagnostics.Scope.Said("library",
            $"a pass was still running {HoldersWait.TotalSeconds:0} s after the close asked it to stop");
        return false;
    }

    public void Dispose()
    {
        _auth.Changed -= OnAccountChanged;

        // Before the library is closed, not after: a pass in the middle of a read or a write would otherwise
        // find the connection closed under it.
        StopHolders();

        lock (_gate)
        {
            _db?.Dispose();
            _db = null;
        }
    }
}
