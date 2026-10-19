using System.Security.Cryptography;
using System.Text;

namespace RPM.Desktop.Services.Art;

/// <summary>
/// Downscaled artwork, kept on disk. A later session reads these thumbs instead of shrinking every full
/// scan again.
///
/// <para>The key is the source bytes themselves (hashed), plus the edge. An edited cover has different bytes,
/// so it gets a different entry. There is nothing to invalidate. The old thumb is never asked for again, and
/// it ages out. A source that is already small enough is returned as it is and never written. The cache holds
/// only work worth keeping.</para>
///
/// <para>One thing the key cannot answer for is the CUTTER. Nothing about a record changes when the encoder is
/// improved. So the generation that made an entry is part of its name too. See <see cref="Cutter"/>.</para>
///
/// <para>Best effort throughout. If the cache cannot read or write, it returns the original bytes. That makes a
/// frame slower. It is not a failure.</para>
/// </summary>
public static class ThumbCache
{
    private static readonly string Dir = Path.Combine(LocalData.Dir, "thumbs");

    /// <summary>Thumbs untouched this long are leftovers that can be derived again, such as edited covers and
    /// removed albums. They are swept on the first use in a session.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(90);
    private static int _pruned;

    public static Task<byte[]> GetAsync(byte[] source, int maxEdge, CancellationToken ct = default)
        => Task.Run(() => Get(source, maxEdge));

    /// <summary>
    /// The downscaled copy of a picture that is ON DISK. The key is where the file lies, not what is in it.
    ///
    /// <para>The difference matters for the cost of filling a wall of covers. If the key were the content, it
    /// could only be worked out by READING the picture. Every tile would then load a three megabyte scan from
    /// disk and hash all of it, only to get back a thirty kilobyte thumb that was already cached. If the key is
    /// the file's path, length and modified time, a hit costs three values and one small read. The scan is never
    /// opened at all.</para>
    ///
    /// <para>The rule for invalidation is the same as for the content key. An edited cover has a new length or a
    /// new modified time, so it gets a different entry. The entry that nobody asks for any more ages out with the
    /// rest.</para>
    ///
    /// <para>Null when the file does not exist. Any other failure returns the picture itself. That is a slower
    /// frame, not an empty square.</para>
    /// </summary>
    public static byte[]? GetFile(string path, int maxEdge)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
                return null;

            Directory.CreateDirectory(Dir);
            PruneOnce();

            var stamp = $"{path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stamp)))[..16];
            var thumb = Entry(key, maxEdge);
            if (File.Exists(thumb))
            {
                Touch(thumb);
                return File.ReadAllBytes(thumb);
            }

            // A miss reads the scan. With the scan in hand, check whether the same picture is already stored under
            // its content key. That costs one hash. Without this check, a picture cached by content would be
            // shrunk again.
            var source = File.ReadAllBytes(path);
            var byContent = Entry(Convert.ToHexString(SHA256.HashData(source))[..16], maxEdge);
            if (File.Exists(byContent))
            {
                var known = File.ReadAllBytes(byContent);
                Write(thumb, known);
                return known;
            }

            var scaled = ArtScaler.Downscale(source, maxEdge);
            if (!ReferenceEquals(scaled, source))
                Write(thumb, scaled);
            return scaled;
        }
        catch
        {
            try { return File.ReadAllBytes(path); } catch { return null; }
        }
    }

    /// <summary>The downscaled copy of <paramref name="source"/>. It comes from disk when it is already there. It
    /// is derived and saved when it is not. Synchronous, so call it off the UI thread.</summary>
    public static byte[] Get(byte[] source, int maxEdge)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            PruneOnce();

            var path = Entry(Convert.ToHexString(SHA256.HashData(source))[..16], maxEdge);
            if (File.Exists(path))
            {
                Touch(path);
                return File.ReadAllBytes(path);
            }

            var scaled = ArtScaler.Downscale(source, maxEdge);
            if (!ReferenceEquals(scaled, source))
                Write(path, scaled);
            return scaled;
        }
        catch
        {
            return source;
        }
    }

    /// <summary>Write, then rename. A crash in the middle of a write leaves a .tmp file, which the sweep removes.
    /// It never leaves a cut-off thumb that would be served as a picture.</summary>
    private static void Write(string path, byte[] thumb)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, thumb);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Marks a thumb as in use, so the sweep leaves it alone. This is done at most once a day. The stamp
    /// only has to beat a 90 day cutoff. A wall of covers is a few hundred of these. Writing on every read would
    /// mean a few hundred metadata writes to record what one write already recorded that morning.</summary>
    private static void Touch(string path)
    {
        try
        {
            var now = DateTime.UtcNow;
            if (now - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(1))
                File.SetLastWriteTimeUtc(path, now);
        }
        catch
        {
            // A thumb that another process has open keeps the stamp it had. The sweep uses 90 days.
        }
    }

    /// <summary>The container gives the format. JPEG and PNG both decode by sniffing the bytes, so the entries
    /// carry no extension. Every name has one shape, whatever the pixels are.</summary>
    private const string ExtensionlessMarker = ".art";

    /// <summary>
    /// Which cutter made the thumbs on disk. It is part of every entry's name.
    ///
    /// <para>The key answers "is this the same picture at the same size?". That is the whole question while the
    /// cutter never changes. Once it changes, the key gives the wrong answer. Nothing about an album changes when
    /// the encoder is improved. So every wall would keep showing the old pictures for the 90 days it takes them
    /// to age out. Increase this when <see cref="ArtScaler.Downscale"/> starts producing different bytes. The sweep
    /// then removes the previous generation.</para>
    /// </summary>
    private const string Cutter = "3";

    /// <summary>The file for one thumb: the picture's key, the edge it was cut to, and the cutter that cut it.</summary>
    private static string Entry(string key, int maxEdge)
        => Path.Combine(Dir, $"{key}-{maxEdge}.{Cutter}{ExtensionlessMarker}");

    private static void PruneOnce()
    {
        if (Interlocked.Exchange(ref _pruned, 1) == 1)
            return;
        try
        {
            var cutoff = DateTime.UtcNow - MaxAge;
            var mine = $".{Cutter}{ExtensionlessMarker}";
            foreach (var file in Directory.EnumerateFiles(Dir))
            {
                // A thumb that an earlier cutter made is a picture this app will never read again. There are
                // thousands of them, so they are removed on the first sweep, rather than aging out one by one over
                // 90 days. This applies to thumbs only. The resident packs share this folder. They are named
                // differently, and this sweep does not judge them.
                var stale = file.EndsWith(ExtensionlessMarker, StringComparison.Ordinal)
                            && !file.EndsWith(mine, StringComparison.Ordinal);

                if (stale
                    || file.EndsWith(".tmp", StringComparison.Ordinal)
                    || File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch { /* a sweep that fails just leaves files for the next one */ }
    }
}
