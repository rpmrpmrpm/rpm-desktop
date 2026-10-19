using RPM.Core.Metadata;
using RPM.Core.Models;

namespace RPM.Core.Import;

/// <summary>Executes accepted suggestions: move or copy each file into the organised layout.</summary>
public static class OrganizeApplier
{
    /// <summary>
    /// Applies one suggestion. Creates destination folders as needed. Never overwrites:
    /// an existing destination is reported as a failure. Tags are written into the
    /// destination file when the metadata was recognised (fingerprint or filename guess) or
    /// hand-edited (<see cref="TrackInfo.TagsEdited"/>, a full write including pending
    /// cover art), and <paramref name="writeTags"/> is set.
    /// </summary>
    public static ApplyResult Apply(TrackSuggestion suggestion, string destinationRoot, OrganizeMode mode, bool writeTags = true)
    {
        string source = suggestion.Track.FilePath;
        string destination = Path.Combine(
            destinationRoot,
            suggestion.ProposedRelativePath.Replace('/', Path.DirectorySeparatorChar));

        try
        {
            if (!suggestion.IsAlreadyInPlace)
            {
                if (File.Exists(destination))
                    return new ApplyResult
                    {
                        SourcePath = source,
                        DestinationPath = destination,
                        Success = false,
                        Error = "destination already exists",
                    };

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                if (mode == OrganizeMode.Move)
                    File.Move(source, destination);
                else
                    File.Copy(source, destination);
            }

            if (writeTags)
            {
                try
                {
                    var track = suggestion.Track;

                    // A copy is a second file, so it needs an identity of its own. Inheriting the
                    // source's RPM id would leave two files pointing at one library row.
                    bool freshId = mode == OrganizeMode.Copy && !suggestion.IsAlreadyInPlace;
                    if (freshId)
                        track = track with { RpmId = RpmTag.New() };

                    if (track.TagsEdited || track.PendingArt is not null)
                        TagReader.WriteAll(destination, track); // full wanted state, incl. art
                    else if (track.Source is MetadataSource.Fingerprint or MetadataSource.FilenameGuess or MetadataSource.Manual
                             && track.HasUsableMetadata)
                        TagReader.Write(destination, track);
                    else
                        // The tags were already good and nothing was edited. This is the common case for
                        // a clean import, and the one path that writes no tags at all. The file still
                        // has to get an RPM id, or it would have no link to its row.
                        TagReader.StampRpmId(destination, freshId);
                }
                catch
                {
                    // Tag write-back is best effort. The file move already succeeded. A file that
                    // missed its RPM id here lands in the mirror with a null one. That is what makes
                    // the next sync pick it up and stamp it.
                }
            }

            return new ApplyResult { SourcePath = source, DestinationPath = destination, Success = true };
        }
        catch (Exception ex)
        {
            return new ApplyResult
            {
                SourcePath = source,
                DestinationPath = destination,
                Success = false,
                Error = ex.Message,
            };
        }
    }

    /// <summary>
    /// Deletes now-empty directories under <paramref name="root"/> after a Move run
    /// (deepest first). The root itself is kept.
    /// </summary>
    public static void CleanupEmptyFolders(string root)
    {
        if (!Directory.Exists(root))
            return;

        var dirs = Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
            .OrderByDescending(d => d.Length);
        foreach (string dir in dirs)
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch
            {
                // Locked or freshly repopulated folders are simply left in place.
            }
        }
    }
}
