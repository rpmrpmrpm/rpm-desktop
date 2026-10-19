using RPM.Core.Threading;

namespace RPM.Core.Files;

/// <summary>
/// Waiting for a file nobody else is holding.
///
/// <para>Editing the record you are listening to is the ordinary case, not the exceptional one: you
/// hear the wrong spelling, so you fix it. But the player has that file open, and a tag write or a
/// re-file needs it to itself. The window that appears once the player is told to let go is short but
/// not instant: the handle closes on the audio thread. So the writer waits for it, rather than failing
/// on the first refusal and reporting the user's own music as "file in use".</para>
/// </summary>
public static class FileHandles
{
    /// <summary>How long to keep asking before giving up. Long enough for a decoder to unwind, short
    /// enough that a file genuinely held by something else does not hang a save.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(50);

    /// <summary>True when the file can be opened for writing with nobody else attached.</summary>
    public static bool IsWritable(string path)
    {
        try
        {
            if (!File.Exists(path))
                return true;   // nothing to be held: a caller creating it is not blocked
            using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Read-only or no permission. Waiting will not change either, so do not pretend it might.
            return true;
        }
    }

    /// <summary>
    /// True when some other handle has the file open for writing.
    ///
    /// <para>The reader's side of <see cref="IsWritable"/>. A read handle that shares only reads is
    /// refused while a writer is attached, and the player's own read handle does not count. ffmpeg
    /// opens with every share mode allowed, so without this check it reads straight through a tag
    /// write that is changing the audio frames underneath it.</para>
    ///
    /// <para>Only Windows enforces share modes. Elsewhere this always answers false, and
    /// <see cref="FileStamp"/> is what notices the write.</para>
    /// </summary>
    public static bool IsBeingWritten(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Waits until the file is writable, or the timeout runs out. Returns whether it came free.
    /// A false means the caller should report the file as refused, not write into it anyway.
    /// </summary>
    public static async Task<bool> WaitUntilWritableAsync(
        string path, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (true)
        {
            if (IsWritable(path))
                return true;
            if (DateTime.UtcNow >= deadline)
                return false;
            if (!await Cancellable.DelayAsync(Step, ct))
                return false;
        }
    }

    /// <summary>The same wait over several files, with one shared deadline. A mass edit of forty tracks
    /// must not spend forty separate timeouts.</summary>
    public static async Task<IReadOnlyList<string>> WaitUntilWritableAsync(
        IEnumerable<string> paths, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        var held = new List<string>();
        foreach (var path in paths)
        {
            while (!IsWritable(path))
            {
                if (DateTime.UtcNow >= deadline)
                {
                    held.Add(path);
                    break;
                }
                if (!await Cancellable.DelayAsync(Step, ct))
                {
                    // The wait was cancelled, not timed out. Every file still not writable counts as
                    // held, because the caller must not treat silence as permission to write.
                    held.Add(path);
                    return held;
                }
            }
        }
        return held;
    }
}
