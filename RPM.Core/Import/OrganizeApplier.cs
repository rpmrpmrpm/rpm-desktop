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
    public static ApplyResult Apply(TrackSuggestion suggestion, string destinationRoot, OrganizeMode mode, bool writeTags = true) =>
        Apply(suggestion, destinationRoot, mode, writeTags, (from, to) => File.Copy(from, to));

    /// <summary>The same, with the step that copies the bytes replaced (for tests).</summary>
    internal static ApplyResult Apply(TrackSuggestion suggestion, string destinationRoot, OrganizeMode mode, bool writeTags, Action<string, string> copy)
    {
        string source = suggestion.Track.FilePath;
        string destination = Path.Combine(
            destinationRoot,
            suggestion.ProposedRelativePath.Replace('/', Path.DirectorySeparatorChar));
        string? partial = null;

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

                var folder = Path.GetDirectoryName(destination)!;
                Directory.CreateDirectory(folder);

                if (mode == OrganizeMode.Move)
                {
                    File.Move(source, destination);
                }
                else
                {
                    // The bytes go to a hidden name in the same folder and are renamed only when complete.
                    // A copy that stops part-way (the app quits, the disk fills) never sits under the real
                    // name, where the library would read it as the song, cut off. The library scan skips
                    // names that start with a dot. The rename refuses to replace a file another import
                    // put there meanwhile.
                    RemoveLeftCopies(folder);
                    partial = Path.Combine(folder, $"{PartialPrefix}{Guid.NewGuid():N}{Path.GetExtension(destination)}");
                    copy(source, partial);
                    File.Move(partial, destination, overwrite: false);
                    partial = null;
                }
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
            // Only this copy's own hidden file is removed. The destination is never touched here: if a
            // file is there, another import put it there.
            if (partial is not null)
            {
                try { File.Delete(partial); }
                catch { /* nothing more can be done here; the result reports the failure */ }
            }

            return new ApplyResult
            {
                SourcePath = source,
                DestinationPath = destination,
                Success = false,
                Error = ex.Message,
                DiskFull = IsDiskFull(ex),
            };
        }
    }

    /// <summary>The start of the hidden name a copy is written under until it is complete.</summary>
    internal const string PartialPrefix = ".rpm-copy-";

    /// <summary>Removes hidden copies in a folder that a stop left behind. Only ones older than a day: a
    /// younger one may be another import's copy that is still running.</summary>
    private static void RemoveLeftCopies(string folder)
    {
        try
        {
            foreach (var left in Directory.EnumerateFiles(folder, PartialPrefix + "*"))
                if (File.GetLastWriteTimeUtc(left) < DateTime.UtcNow.AddDays(-1))
                    File.Delete(left);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Whether an exception says the disk is full: ENOSPC (28) on macOS and Linux, and
    /// ERROR_DISK_FULL or ERROR_HANDLE_DISK_FULL on Windows.</summary>
    internal static bool IsDiskFull(Exception ex) =>
        ex is IOException { HResult: 28 or unchecked((int)0x80070070) or unchecked((int)0x80070027) };

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
