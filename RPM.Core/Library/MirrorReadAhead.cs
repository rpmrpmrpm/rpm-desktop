namespace RPM.Core.Library;

/// <summary>
/// Reads the library's database file once, front to back, while the app is starting and before SQLite
/// opens it, so the pages the first screen reads are already in the system's file cache.
/// </summary>
/// <remarks>
/// <para><b>Why.</b> SQLite reads a table one 4 KB page at a time, in the order of its tree, not in the
/// order of the file. When the file is not in the cache, every page is a separate trip to the disk. On a
/// library of 41,306 tracks the two reads that home waits for most (the genre counts over <c>tracks</c>
/// and the sound embeddings) held the read gate for 466 and 427 ms with about 40 ms of processor each.
/// The same 283 MB read in order takes about 70 ms. Measured on a fresh copy of that file, building the
/// session's mood cut and profile took 1.2 s cold and 0.55 to 0.64 s after this read.</para>
///
/// <para><b>When.</b> It is started as soon as the settings say where the library is, about a second
/// before the library is opened. That second is the host and the shell being built on the drawing
/// thread, so this costs the startup nothing.</para>
///
/// <para><b>It must have stopped before SQLite opens the file.</b> On macOS and Linux, SQLite's locks are
/// POSIX locks, which belong to the process. Closing any handle to the file in the same process drops
/// all of them. A handle closed here while a connection held a lock would leave that connection
/// unprotected against another process writing the file. So <see cref="LibraryDatabase"/>'s constructor
/// calls <see cref="Finish"/> before it opens anything: the read is stopped and its handle closed first.
/// The rule is kept in that one place, so no caller has to remember it.</para>
///
/// <para>The <c>-wal</c> file is read too. The <c>-shm</c> file is not: it holds SQLite's locks.</para>
/// </remarks>
public static class MirrorReadAhead
{
    /// <summary>A file larger than this is not read. The read is worth it because it is short; on a much
    /// larger library it would fill the cache with pages nothing reads at startup.</summary>
    private const long MostBytes = 1L << 30;

    private static readonly object Lock = new();
    private static Task? _reading;
    private static CancellationTokenSource? _stop;

    /// <summary>Starts reading the database of the music folder at <paramref name="libraryRoot"/>, and its
    /// write-ahead log, on a worker. Only a folder on this computer's own disks whose database is already
    /// there is read (see <see cref="MirrorLocation.OwnDiskDatabase"/>). That question reaches the disk, so
    /// it is asked on the worker too. Does nothing if a read is already under way. Never throws.</summary>
    public static void Start(string libraryRoot)
    {
        lock (Lock)
        {
            if (_reading is { IsCompleted: false })
                return;

            var stop = new CancellationTokenSource();
            _stop = stop;
            _reading = Task.Run(() =>
            {
                if (MirrorLocation.OwnDiskDatabase(libraryRoot) is not { } databasePath)
                    return;

                Read(databasePath, stop.Token);
                Read(databasePath + "-wal", stop.Token);
            });
        }
    }

    /// <summary>Stops a read that is under way and waits until its handle is closed. Returns at once when
    /// there is none. Called before the database is opened (see the remarks above).</summary>
    public static void Finish()
    {
        Task? reading;
        lock (Lock)
        {
            reading = _reading;
            _stop?.Cancel();
        }

        // Read checks the token between chunks of a megabyte, so this waits for one chunk at most. A
        // failure inside it is not this caller's problem: the database opens without the read either way.
        try
        {
            reading?.Wait();
        }
        catch (AggregateException)
        {
        }
    }

    private static void Read(string path, CancellationToken stop)
    {
        try
        {
            using var file = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1, FileOptions.SequentialScan);
            if (file.Length > MostBytes)
                return;

            var chunk = new byte[1 << 20];
            while (!stop.IsCancellationRequested && file.Read(chunk, 0, chunk.Length) > 0)
            {
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing is lost: SQLite reads the pages itself, as it would without this.
        }
    }
}
