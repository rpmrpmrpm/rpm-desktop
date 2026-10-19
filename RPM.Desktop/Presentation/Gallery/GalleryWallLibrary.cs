using RPM.Core.Library;

namespace RPM.Desktop.Presentation.Gallery;

/// <summary> The album-wall stress library: 1,000 albums with 12 tracks each. Every album folder holds
/// a real cover.jpg, drawn by <see cref="GalleryArt"/>. So the wall exercises its true art path at
/// scale (folder image, then bytes, then ThumbCache, then decode) instead of laying out a thousand
/// placeholders.
///
/// <para>It is generated once into a stable temp location and reused. The covers are the slow part,
/// since a thousand Skia renders take time. The loop skips any cover already on disk, so an interrupted
/// first run resumes rather than restarts. The audio files the rows name do not exist, because the
/// harness keeps sync off, as it does for the other generated libraries. The folders and their covers
/// are real, and that is all the wall reads.</para> </summary>
internal static class GalleryWallLibrary
{
    public const int AlbumCount = 1000;
    public const int TrackCount = AlbumCount * DemoLibrary.TracksPerAlbum;

    private static string Root => Path.Combine(Path.GetTempPath(), "rpm-gallery-wall");

    /// <summary>Makes sure the wall library and its covers exist, and returns the root. Meant to run
    /// off the UI thread, because the first run draws a thousand covers.</summary>
    public static string Ensure(IProgress<string>? progress = null)
    {
        Directory.CreateDirectory(Root);
        using (var db = new LibraryDatabase(MirrorLocation.InFolder(Root)))
        {
            if (db.Count < TrackCount)
            {
                progress?.Report($"Generating {TrackCount:N0} tracks…");
                db.UpsertMany(DemoLibrary.LargeTracks(TrackCount));
            }
        }

        // One cover per album folder. LargeTracks streams its tracks folder by folder, so the first
        // track of each album is where the folder changes.
        var done = 0;
        string? lastFolder = null;
        foreach (var track in DemoLibrary.LargeTracks(TrackCount))
        {
            if (track.Folder == lastFolder)
                continue;
            lastFolder = track.Folder;
            done++;

            var dir = Path.Combine(Root, track.Folder.Replace('/', Path.DirectorySeparatorChar));
            var cover = Path.Combine(dir, "cover.jpg");
            if (File.Exists(cover))
                continue;

            Directory.CreateDirectory(dir);
            File.WriteAllBytes(cover, GalleryArt.CoverJpeg(track.AlbumArtist, track.Album));
            if (done % 100 == 0)
                progress?.Report($"Drawing covers… {done:N0}/{AlbumCount:N0}");
        }

        return Root;
    }
}
