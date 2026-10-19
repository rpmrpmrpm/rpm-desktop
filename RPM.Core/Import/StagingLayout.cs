using RPM.Core.Models;

namespace RPM.Core.Import;

/// <summary>
/// Places freshly produced files (a CD rip, a vinyl split) under a staging folder using a
/// <see cref="NamingTemplate"/>, so what lands in the import queue already carries the library's
/// default names and folder layout. The queue shows the arrangement it will keep, not a flat pile.
///
/// The final move into the library still re-renders from the (possibly edited) metadata. So this is
/// a head start, not a commitment: it never has to be right, only close.
/// </summary>
public static class StagingLayout
{
    /// <summary>
    /// The absolute path <paramref name="track"/> should be written to under
    /// <paramref name="stagingRoot"/>. It is built from <paramref name="template"/> and kept unique within
    /// the batch (via <paramref name="claimed"/>) and against anything already on disk. Directories
    /// are not created. The caller owns that, since it is the one about to write the file.
    /// </summary>
    public static string ResolvePath(string stagingRoot, NamingTemplate template, TrackInfo track,
        ISet<string> claimed)
    {
        string relative = template.Render(track).Replace('/', Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(stagingRoot, relative));

        if (IsFree(full, claimed))
        {
            claimed.Add(full);
            return full;
        }

        // The same name twice in one batch (two untitled tracks, say): fall back to " (2)", " (3)", …
        string ext = Path.GetExtension(full);
        string stem = full[..^ext.Length];
        for (int n = 2; ; n++)
        {
            string candidate = $"{stem} ({n}){ext}";
            if (IsFree(candidate, claimed))
            {
                claimed.Add(candidate);
                return candidate;
            }
        }
    }

    private static bool IsFree(string full, ISet<string> claimed) =>
        !claimed.Contains(full) && !File.Exists(full);
}
