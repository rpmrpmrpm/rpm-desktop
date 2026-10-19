using System.Security.Cryptography;
using System.Text;

namespace RPM.Desktop.Services.Art;

/// <summary>
/// The photograph each genre is drawn on, kept on disk. A wall of shelves is resolved once, not once per launch.
///
/// <para><c>GenreFaces</c> keeps every answer for the life of the process, so scrolling a wall back over is free.
/// It does not help a launch, though. At launch the table is empty. Eighteen shelves each walk up to six artists,
/// look each one up, fetch a photograph, and run two models over it. All of that happens before a single card can
/// come up. On a borrowed library, the cards then arrive one or two at a time. This cache lets them come up from
/// disk instead.</para>
///
/// <para><b>On disk it is a file open and a few kilobytes.</b> This is the same approach as
/// <see cref="FigureCache"/> for made sleeves and <see cref="ThumbCache"/> for covers. It follows the same
/// pattern, and it is not a third mechanism.</para>
///
/// <para><b>Keyed by the whole question.</b> <c>GenreFaces</c> files each answer under the shelf, the shape asked
/// for, and the acts considered. This class is given the same key. A shelf whose artists changed is a different
/// question, so it is asked again. A shelf whose artists did not change keeps its picture. This also makes it safe
/// on a BORROWED shelf. The key includes the acts. So a peer's Rock and this machine's Rock share an entry only
/// when they have the same artists in the same order. In that case the picture really is the same one.</para>
///
/// <para><b>"Nobody has a photograph" is an answer, and it is kept too.</b> It is also the most expensive answer.
/// It means every act on the shelf was walked, looked up, and none of them had a face. If it were not kept, each
/// launch would repeat the whole walk for every shelf without a picture. On a borrowed library, that is most of
/// them. An empty file is how this answer is stored.</para>
///
/// <para><b>Derived data, so there is nothing to invalidate.</b> An entry made by an older framer is swept, not
/// converted. A cache that cannot be read or written makes the launch slower. It is not a fault.</para>
/// </summary>
public static class GenreFaceCache
{
    /// <summary>The folder a test supplies in place of <see cref="LocalData.Dir"/>. It is null in the running app.
    /// Otherwise a test could sweep this machine's real faces.</summary>
    internal static string? RootOverride { get; set; }

    private static string Dir => RootOverride ?? Path.Combine(LocalData.Dir, "genre-faces");

    /// <summary>Faces not used for this long belong to shelves that were retagged or removed. They are swept on
    /// the first use in a session. This is the same 90 days that made sleeves get.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(90);

    private static int _pruned;

    /// <summary>
    /// Which framer cut what is on disk. It is part of every entry's name.
    /// </summary>
    /// <remarks>
    /// The key answers "which picture is this?" only while the framing stays the same. Once the framing changes,
    /// the key gives the wrong answer. Nothing about a shelf changes when the saliency crop or the models behind it
    /// are improved. So every wall would keep showing the old cuts for the 90 days it takes them to age out.
    /// Increase this when the pictures come out differently. The sweep then removes the previous generation.
    /// </remarks>
    private const string Framer = "1";

    /// <summary>The container gives the format, so the entries carry none. Every name has one shape, whatever the
    /// pixels are. The sweep recognises every entry by this suffix.</summary>
    private const string Marker = ".face";

    /// <summary>
    /// What was answered for this question last time, or null where it has not been asked.
    /// </summary>
    /// <remarks>
    /// <b>Two different nothings.</b> A null return means "not asked before. Go and find out". An array of length
    /// zero means "asked before, and nobody on this shelf has a photograph". The caller must tell them apart.
    /// Otherwise the expensive answer is the one that is never kept.
    /// </remarks>
    public static byte[]? Known(string key)
    {
        if (Entry(key) is not { } path)
            return null;

        try
        {
            Directory.CreateDirectory(Dir);
            PruneOnce();

            if (!File.Exists(path))
                return null;

            Touch(path);
            return File.ReadAllBytes(path);
        }
        catch
        {
            // No cache directory, or a file that will not open. The face can still be resolved.
            return null;
        }
    }

    /// <summary>
    /// Files the result for this question, so the next launch does not resolve it again.
    /// </summary>
    /// <param name="key">The question, as <c>GenreFaces</c> words it.</param>
    /// <param name="face">The framed picture. Null means nobody on the shelf had one. That is kept as an empty
    /// entry, not dropped. See the note on this class.</param>
    public static void Keep(string key, byte[]? face)
    {
        if (Entry(key) is not { } path)
            return;

        try
        {
            Directory.CreateDirectory(Dir);

            // Write, then rename. A crash in the middle of a write leaves a .tmp file, which the sweep removes.
            // It never leaves a cut-off picture that would be served as a face.
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, face ?? []);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // A face that cannot be written makes the next start slower. It is not a fault.
        }
    }

    /// <summary>The file for one face: the question hashed, and the framer that cut it. Null if the name cannot be
    /// worked out.</summary>
    private static string? Entry(string key)
    {
        if (key.Length == 0)
            return null;

        try
        {
            var name = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(key.ToLowerInvariant())))[..16];
            return Path.Combine(Dir, $"{name}.{Framer}{Marker}");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Marks a face as in use, so the sweep leaves it alone. This is done at most once a day. The stamp
    /// only has to beat a 90 day cutoff. A wall is only a couple of dozen of these.</summary>
    private static void Touch(string path)
    {
        try
        {
            var now = DateTime.UtcNow;
            if (now - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(1))
                File.SetLastWriteTimeUtc(path, now);
        }
        catch { /* a stamp that cannot be written costs the entry its 90 days, nothing more */ }
    }

    private static void PruneOnce()
    {
        if (Interlocked.Exchange(ref _pruned, 1) == 1)
            return;

        try
        {
            var cutoff = DateTime.UtcNow - MaxAge;
            var mine = $".{Framer}{Marker}";
            foreach (var file in Directory.EnumerateFiles(Dir))
            {
                // A face that an earlier framer cut is a picture this app will never read again. So it is removed on
                // the first sweep, rather than aging out one by one over 90 days.
                var stale = file.EndsWith(Marker, StringComparison.Ordinal)
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
