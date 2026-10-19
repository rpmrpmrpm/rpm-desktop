using RPM.Core.Library;

namespace RPM.Desktop.Services.Library;

/// <summary>What came of asking for another music folder while the app runs.</summary>
public enum FolderChange
{
    /// <summary>The picker was closed without a folder. Nothing changed.</summary>
    Cancelled,

    /// <summary>The folder holds files and no collection. Nothing changed.</summary>
    NotEmpty,

    /// <summary>The folder is inside the open collection's folder, or the other way round. Nothing
    /// changed.</summary>
    Overlaps,

    /// <summary>The folder could not be opened. The collection that was open is still open, and is
    /// still the saved one.</summary>
    Failed,

    /// <summary>The folder is the collection now, and the saved one.</summary>
    Opened,
}

/// <summary>
/// Picking another music folder with the app running: Settings' Change, and Choose another folder on
/// the problem screen over the library. One place, so both apply the same rules and save the folder
/// at the same point.
/// </summary>
public static class LibraryFolderChange
{
    /// <summary>
    /// Asks for a folder and opens it as the collection. The open raises <c>Opened</c> and
    /// <c>Changed</c> on the calling thread, which is the interface thread for both callers.
    /// </summary>
    public static async Task<FolderChange> PickAndOpenAsync(
        FolderPickerService picker, LibraryService library, SettingsService settings)
    {
        // The picker's "New Folder" button lets the user make an empty folder on the spot. First-run setup
        // offers the same thing, and it is the answer to the rule below.
        var folder = await picker.PickFolderAsync(canCreateDirectories: true);
        if (string.IsNullOrWhiteSpace(folder))
            return FolderChange.Cancelled;

        // First-run setup uses the same rule, from the same code. An empty folder becomes a new collection.
        // A folder with a mirror in it is reopened. Any other folder is someone's existing file tree. See
        // LibraryFolder for what adopting a file tree does to it.
        if (!LibraryFolder.Adoptable(folder))
            return FolderChange.NotEmpty;

        if (library.Root is { } current && LibraryFolder.Overlap(current, folder))
            return FolderChange.Overlaps;

        // Opening the new root builds or reuses its mirror. The old library's mirror is left where it is.
        // It lives inside that folder, so it is still there, and intact, if the app points back at it
        // later.
        try
        {
            library.Open(folder);
        }
        catch (Exception ex)
        {
            // Possible causes: a read-only volume, a mount that went away between the pick and the open, or
            // a failed migration. The library that was open is still open and still the saved one.
            RPM.Core.Diagnostics.Scope.Said("library", $"the music folder could not be changed: {ex.Message}");
            return FolderChange.Failed;
        }

        // Saved after the open, not before. A folder that could not be opened must not be the one the next
        // launch tries.
        settings.LibraryPath = folder;
        return FolderChange.Opened;
    }

    /// <summary>The status line for an outcome that changed nothing. Returns null when there is nothing to
    /// say: the picker was closed, or the folder opened.</summary>
    public static string? StatusKey(this FolderChange change) => change switch
    {
        FolderChange.NotEmpty => "Settings_MusicFolderNotEmpty",
        FolderChange.Overlaps => "Settings_MusicFolderOverlaps",
        FolderChange.Failed => "Settings_MusicFolderFailed",
        FolderChange.Cancelled or FolderChange.Opened => null,
        _ => throw new ArgumentOutOfRangeException(nameof(change), change, "Unknown folder change."),
    };
}
