using RPM.Core.Models;

namespace RPM.Core.Import;

/// <summary>
/// Turns analysed tracks into rename/move suggestions under a destination root,
/// resolving name collisions within the plan and against files already on disk.
/// </summary>
public static class RenamePlanner
{
    /// <param name="albumFolderOverride">Given a track, returns a library-relative folder its file must
    /// land in instead of the one the template would build. This is used to file a new disc of a
    /// multi-disc set into the album folder the earlier discs already occupy. Return null to let the
    /// template decide. The template still names the file; only its folder is replaced.</param>
    /// <param name="namingView">Given a track, returns the version of it the TEMPLATE reads. The file
    /// still carries the original's tags and moves from the original's path. This only changes what it
    /// is called. It exists for merged multi-disc filing, where disc 2 track 1 is named 9 while its tags
    /// keep saying disc 2, track 1 (see <see cref="Analysis.DiscNumbering"/>).</param>
    public static IReadOnlyList<TrackSuggestion> Plan(
        IEnumerable<TrackInfo> tracks,
        NamingTemplate template,
        string destinationRoot,
        Func<TrackInfo, string?>? albumFolderOverride = null,
        Func<TrackInfo, TrackInfo>? namingView = null)
    {
        var suggestions = new List<TrackSuggestion>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var track in tracks)
        {
            string relative = template.Render(namingView?.Invoke(track) ?? track);

            if (albumFolderOverride?.Invoke(track) is { Length: > 0 } folder)
            {
                int slash = relative.LastIndexOf('/');
                string file = slash >= 0 ? relative[(slash + 1)..] : relative;
                relative = folder.Replace('\\', '/').TrimEnd('/') + "/" + file;
            }
            string full = Path.GetFullPath(Path.Combine(destinationRoot, relative.Replace('/', Path.DirectorySeparatorChar)));

            bool alreadyInPlace = string.Equals(full, Path.GetFullPath(track.FilePath), StringComparison.OrdinalIgnoreCase);
            if (!alreadyInPlace)
            {
                relative = ResolveCollision(relative, destinationRoot, claimed, track.FilePath);
                full = Path.Combine(destinationRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            }
            claimed.Add(Path.GetFullPath(full));

            suggestions.Add(new TrackSuggestion
            {
                Track = track,
                ProposedRelativePath = relative,
                IsAlreadyInPlace = alreadyInPlace,
            });
        }

        return suggestions;
    }

    /// <summary>Appends " (2)", " (3)", … before the extension until the path is free.</summary>
    private static string ResolveCollision(string relative, string destinationRoot, HashSet<string> claimed, string sourcePath)
    {
        string candidate = relative;
        int n = 2;
        while (true)
        {
            string full = Path.GetFullPath(Path.Combine(destinationRoot, candidate.Replace('/', Path.DirectorySeparatorChar)));
            bool takenOnDisk = File.Exists(full)
                && !string.Equals(full, Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase);
            if (!claimed.Contains(full) && !takenOnDisk)
                return candidate;

            string ext = Path.GetExtension(relative);
            string stem = relative[..^ext.Length];
            candidate = $"{stem} ({n}){ext}";
            n++;
        }
    }
}
