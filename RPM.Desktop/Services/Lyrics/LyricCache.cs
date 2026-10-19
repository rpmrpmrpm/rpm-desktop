using System.Security.Cryptography;
using System.Text;
using RPM.Core.Lyrics;
using RPM.Desktop.Services.Platform;

namespace RPM.Desktop.Services.Lyrics;

/// <summary>
/// Saves lyrics on disk, in the local app cache folder of this user. A record played again later has
/// its words as soon as its view opens, with no network request.
///
/// <para>The entry holds the LRC text exactly as it arrived, not this app's reading of it. A later
/// parser that understands more of the format then gets the benefit of every sheet already saved.
/// A person can also open the file and see what it contains.</para>
///
/// <para><b>Absences are saved too, with a different rule.</b> Most records have no synced sheet anywhere.
/// Asking again on every play would cost time, so "nobody has written this one down" is saved. But that
/// answer only holds for a while. Sheets are added to LRCLIB every day, so a permanent "no" would hide a
/// sheet that was added later. An absence therefore has a limited life. Reading it does not refresh it,
/// and its file age ends it. A sheet cannot become wrong, so it is refreshed on every use and is kept
/// for as long as the record is played.</para>
///
/// <para>Every path is best effort. A cache that cannot be read or written only makes the answer slower.
/// Each path then falls back to asking the network, as if nothing were saved.</para>
/// </summary>
internal static class LyricCache
{
    private static readonly string Dir = AppCache.LyricsDir;

    /// <summary>How long a sheet that nobody has played is kept by the sweep. This is long, because an
    /// entry is a couple of kilobytes, and fetching one again needs a network round trip before a view
    /// can open.</summary>
    private static readonly TimeSpan SheetAge = TimeSpan.FromDays(180);

    /// <summary>How long an absence is believed before the lookup is asked again.</summary>
    private static readonly TimeSpan AbsenceAge = TimeSpan.FromDays(14);

    private const string SheetExt = ".lrc";
    private const string AbsenceExt = ".none";

    private static int _swept;

    /// <summary>
    /// What is remembered about this record: a sheet, <see cref="LyricSheet.Empty"/> for a remembered
    /// absence, or null for "never asked, or asked so long ago it is worth asking again".
    /// </summary>
    /// <remarks>Synchronous file IO. Call it off the UI thread.</remarks>
    public static LyricSheet? Get(string key)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            SweepOnce();

            var name = Name(key);

            var sheet = Path.Combine(Dir, name + SheetExt);
            if (File.Exists(sheet))
            {
                var parsed = LyricSheet.Parse(File.ReadAllText(sheet, Encoding.UTF8));
                if (parsed.IsSynced)
                {
                    // In use: refresh the file time so the sweep keeps it.
                    File.SetLastWriteTimeUtc(sheet, DateTime.UtcNow);
                    return parsed;
                }

                // On disk but not readable as a sheet, for example after a cut-off write or a hand edit
                // that is not valid LRC. Delete it and ask again.
                File.Delete(sheet);
                return null;
            }

            var absence = Path.Combine(Dir, name + AbsenceExt);
            if (File.Exists(absence))
            {
                // Not refreshed here on purpose. The file's age decides how long the absence is kept.
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(absence) < AbsenceAge)
                    return LyricSheet.Empty;
                File.Delete(absence);
            }
        }
        catch { /* fall through: an unreadable cache is the same as an empty one */ }

        return null;
    }

    /// <summary>Remembers what came back, sheet or absence.</summary>
    /// <remarks>Synchronous file IO. Call it off the UI thread.</remarks>
    public static void Put(string key, LyricSheet sheet)
    {
        try
        {
            Directory.CreateDirectory(Dir);

            var name = Name(key);
            var hit = Path.Combine(Dir, name + SheetExt);
            var absence = Path.Combine(Dir, name + AbsenceExt);

            if (sheet.IsSynced && sheet.Source is { Length: > 0 } text)
            {
                // Write to a temp file, then rename it. A crash during the write leaves a .tmp file for
                // the sweep to remove. A half-written sheet is never read back as a whole one.
                var tmp = hit + ".tmp";
                File.WriteAllText(tmp, text, Encoding.UTF8);
                File.Move(tmp, hit, overwrite: true);

                // The sheet exists now, so any saved absence is out of date and is removed.
                if (File.Exists(absence))
                    File.Delete(absence);
            }
            else
            {
                // The file name records the absence, so the file itself is empty.
                File.WriteAllBytes(absence, []);
            }
        }
        catch { /* A cache that cannot be written only means the next play looks the lyrics up again. */ }
    }

    /// <summary>The name of the file for one record. It is a hash, not the text, because artist and title
    /// are free text. They can hold slashes, colons, emoji and very long strings, and a cache should not
    /// have to work out what a file name can hold.</summary>
    private static string Name(string key)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..20];

    private static void SweepOnce()
    {
        if (Interlocked.Exchange(ref _swept, 1) == 1)
            return;
        try
        {
            var now = DateTime.UtcNow;
            foreach (var file in Directory.EnumerateFiles(Dir))
            {
                var age = now - File.GetLastWriteTimeUtc(file);
                var stale = file.EndsWith(".tmp", StringComparison.Ordinal)
                         || (file.EndsWith(AbsenceExt, StringComparison.Ordinal) && age > AbsenceAge)
                         || (file.EndsWith(SheetExt, StringComparison.Ordinal) && age > SheetAge);
                if (stale)
                    File.Delete(file);
            }
        }
        catch { /* If the sweep fails, the files are kept for the next sweep. */ }
    }
}
