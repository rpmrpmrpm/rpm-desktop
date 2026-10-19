using System.Security.Cryptography;
using System.Text;

namespace RPM.Desktop.Services.Art;

/// <summary>
/// The made sleeves, kept on disk. A shelf of records with no artwork is painted once, not once per session.
///
/// <para>A figure is six wide radial gradients, a shaft of light, and a blur of a seventh of the picture's
/// edge. All of it is drawn in Skia and then encoded. That takes tens of milliseconds for each record. The
/// wall needs a figure as soon as a tile is realised, and that happens while the wall is being scrolled.
/// Reading the figure from disk takes a file open and a few kilobytes. <see cref="ThumbCache"/> makes the
/// same trade for covers.</para>
///
/// <para><b>Keyed by the seed only.</b> The colours, the place of each blob, and the direction of the light
/// all come from the identity string and the palette. So the same identity gives the same picture on every
/// machine and in every session (see <see cref="SoftFigure.Seed"/>). That is why the file name is safe. It
/// is also why a BORROWED shelf shares this cache with the user's own collection. A record's identity is its
/// folder within whichever collection is open. Two collections that use the same folder name already draw the
/// same figure. Nothing here can answer for the wrong record, because the cache holds no record. It holds
/// only a picture made from a name.</para>
///
/// <para><b>Derived data, so there is nothing to invalidate.</b> An entry made by an older painter is swept,
/// not converted. A cache that cannot be read or written makes the wall slower. It is not an error.</para>
/// </summary>
public static class FigureCache
{
    /// <summary>The folder a test supplies in place of <see cref="LocalData.Dir"/>. It is null in the running
    /// app. Otherwise a test could sweep this machine's real figures. Every test that sets it must be in one
    /// xunit collection, so two test classes never race for the same folder.</summary>
    internal static string? RootOverride { get; set; }

    private static string Dir => RootOverride ?? Path.Combine(LocalData.Dir, "figures");

    /// <summary>Figures not used for this long belong to records that were retagged, given artwork or
    /// removed. They are swept on the first use in a session.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(90);

    private static int _pruned;

    /// <summary>
    /// The painter's version. It is part of every entry's name.
    ///
    /// <para>An identity answers "which picture is this?" only while the drawing stays the same. Once the
    /// drawing changes, the identity gives the wrong answer. Nothing about a record changes when
    /// <see cref="SoftFigure"/> is improved. So every wall would keep showing the old pictures for the ninety
    /// days it takes them to age out. Increase this when the figures come out differently. The sweep then
    /// removes the entries of the previous version.</para>
    /// </summary>
    private const string Painter = "1";

    /// <summary>
    /// The made sleeve for a record with no artwork. It is read from disk where it has been drawn before.
    /// Otherwise it is painted and saved. Returns null where there is no palette to draw from, or no Skia.
    ///
    /// <para>This method is synchronous, and both of its halves are synchronous: a file read and a Skia
    /// paint. Call it off the UI thread. <see cref="Converters.SleeveFigureConverter"/> does this on the
    /// wall's behalf.</para>
    /// </summary>
    public static byte[]? Sleeve(string identity)
    {
        if (identity.Length == 0)
            return null;

        var path = Entry(identity);
        try
        {
            Directory.CreateDirectory(Dir);
            PruneOnce();

            if (path is { } known && File.Exists(known))
            {
                Touch(known);
                return File.ReadAllBytes(known);
            }
        }
        catch
        {
            // No cache directory, or a file that will not open. The figure can still be drawn.
        }

        var figure = SoftFigure.Sleeve(identity);
        if (figure is not { Length: > 0 } || path is null)
            return figure;

        try
        {
            // Write, then rename. A crash in the middle of a write leaves a .tmp file, which the sweep removes.
            // It never leaves a cut-off picture that would be served as a figure.
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, figure);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // A figure that cannot be written makes the next start slower. It is not an error.
        }

        return figure;
    }

    /// <summary>The file for one figure: the seed hashed, plus the painter version. Null if the name cannot
    /// be worked out.</summary>
    private static string? Entry(string identity)
    {
        try
        {
            var key = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToLowerInvariant())))[..16];
            return Path.Combine(Dir, $"{key}.{Painter}{Marker}");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The file format is given by the container, so entries carry no format in their names. Every
    /// name has the same shape, whatever the pixels are. The sweep recognises every entry by this suffix.</summary>
    private const string Marker = ".fig";

    /// <summary>Marks a figure as in use, so the sweep leaves it alone. This is done at most once a day. The
    /// stamp only has to beat a 90 day cutoff. A wall of made sleeves is a few hundred files.</summary>
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
            // A figure that another process has open keeps the stamp it has. The sweep uses 90 days.
        }
    }

    private static void PruneOnce()
    {
        if (Interlocked.Exchange(ref _pruned, 1) == 1)
            return;
        try
        {
            var cutoff = DateTime.UtcNow - MaxAge;
            var mine = $".{Painter}{Marker}";
            foreach (var file in Directory.EnumerateFiles(Dir))
            {
                // A figure drawn by an earlier painter is a picture this app will never read again. So it is
                // removed on the first sweep, rather than aging out one by one over ninety days.
                var stale = file.EndsWith(Marker, StringComparison.Ordinal)
                            && !file.EndsWith(mine, StringComparison.Ordinal);

                if (stale
                    || file.EndsWith(".tmp", StringComparison.Ordinal)
                    || File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch { /* a sweep that fails leaves files for the next one */ }
    }
}
