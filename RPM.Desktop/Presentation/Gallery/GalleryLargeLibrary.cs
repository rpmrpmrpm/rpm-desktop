using RPM.Core.Library;

namespace RPM.Desktop.Presentation.Gallery;

/// <summary> The big stress library: a procedurally generated 200,000-track mirror, for exercising the
/// grid and the virtualised tree at scale. It is generated once into a stable temp location and reused
/// on every later run. Building a fifth of a million rows takes a few seconds, so it is worth doing
/// only the first time. As with the small demo, the files do not exist on disk, so the harness keeps
/// sync off. </summary>
internal static class GalleryLargeLibrary
{
    public const int TrackCount = 200_000;

    private static string Root => Path.Combine(Path.GetTempPath(), "rpm-gallery-large");

    /// <summary>Makes sure the big library exists on disk, and returns its root. It generates the
    /// library the first time and reuses it after. It is safe, and intended, to run off the UI thread,
    /// because it can take a few seconds.</summary>
    public static string Ensure(IProgress<string>? progress = null)
    {
        Directory.CreateDirectory(Root);
        using var db = new LibraryDatabase(MirrorLocation.InFolder(Root));
        if (db.Count >= TrackCount)
            return Root;   // Built on a previous run, so reuse it.

        progress?.Report($"Generating {TrackCount:N0} tracks…");
        db.UpsertMany(DemoLibrary.LargeTracks(TrackCount));
        return Root;
    }
}
