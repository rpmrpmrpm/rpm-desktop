using RPM.Core.Metadata;
using RPM.Core.Models;
using RPM.Core.Recording;
using RPM.Core.Scanning;
using RPM.Core.Splitting;

namespace RPM.Core.Ripping;

/// <summary>The two kinds of unfinished work that can be left half-finished in staging.</summary>
public enum PendingKind
{
    /// <summary>A ripped disc: already tracks, ready to import as-is.</summary>
    Rip,
    /// <summary>A vinyl recording: audio that still has to be split into tracks first.</summary>
    Vinyl,
}

/// <summary>How far along a vinyl session is. This decides what the session still needs.</summary>
public enum PendingVinylStage
{
    /// <summary>Not a vinyl session.</summary>
    None,
    /// <summary>Sides were recorded, and may be joined into one album, but the album has not been cut. It needs a split.</summary>
    Unsplit,
    /// <summary>The split has run, and its tracks are still in the session folder. It needs an import.</summary>
    TracksCut,
    /// <summary>Cut and imported. Only the source album kept in <c>Originals/</c> is left. Nothing is owed.
    /// It is listed so it can be split again with different boundaries, or deleted to free the disk space.</summary>
    Kept,
}

/// <summary>A ripped disc or vinyl recording that is still in staging. It has never been taken into the library.</summary>
/// <param name="Folder">The staging folder itself. This is what the import queue is given.</param>
/// <param name="Name">What to call it: the album from the tracks' own tags, or the title that the rip wrote
/// into its <see cref="RipManifest"/> before it started. Null when neither exists, for an untagged
/// recording that nobody has named. The GUID folder name is not a useful label, so the caller shows a
/// plain description instead.</param>
/// <param name="Kind">Which flow finishes it: a rip imports, a vinyl recording splits first.</param>
/// <param name="TrackCount">Tracks waiting to be imported: a rip's own files, or the tracks that a vinyl
/// split cut. The sides and the joined album they came from are not counted, because they are not tracks.</param>
/// <param name="PlannedTrackCount">How many tracks the rip planned to make, from its manifest. When this is
/// larger than <paramref name="TrackCount"/>, the rip stopped early. Zero when nothing recorded it.</param>
/// <param name="SizeBytes">Everything in the folder, including the sides and the kept album. This is the
/// space that deleting it gives back.</param>
/// <param name="RippedUtc">When it started, from the manifest. For a rip made before manifests existed,
/// this is the folder's creation time.</param>
/// <param name="Source">What it came off, when a manifest says: a CD, a DVD, a record.</param>
/// <param name="Stage">Vinyl only: what the session still owes (see <see cref="PendingVinylStage"/>).</param>
/// <param name="SideCount">Vinyl only: how many sides were recorded. They are counted wherever they are: at
/// the top level while a session is new, or in <c>Originals/</c> after they have been joined.</param>
public sealed record PendingRip(
    string Folder,
    string? Name,
    string? Artist,
    int TrackCount,
    long SizeBytes,
    DateTime RippedUtc,
    PendingKind Kind = PendingKind.Rip,
    PendingVinylStage Stage = PendingVinylStage.None,
    int SideCount = 0,
    int PlannedTrackCount = 0,
    RipSource? Source = null)
{
    /// <summary>The recording has been cut and imported. Only its kept source album is left.</summary>
    public bool SplitDone => Stage == PendingVinylStage.Kept;
}

/// <summary>
/// Unfinished work in <c>&lt;library&gt;/.rpm/</c>: ripped discs (<c>rip-&lt;guid&gt;/</c>) and vinyl
/// recordings (<c>vinyl-&lt;guid&gt;/</c>). Both are meant to be moved out by the import that follows.
/// Several things can leave them behind: an import set to <b>copy</b> rather than move, or a dialog or an
/// app that closed before the import ran.
///
/// <para>
/// In either case the expensive part is already done. A disc has been ripped, or a record has been played
/// through in real time. But nothing in the app shows that the files exist, because
/// <see cref="AudioFileScanner"/> skips dot-folders and never looks inside <c>.rpm</c>. The files are
/// hidden, and they are not free to recreate. This class finds them, so they can be finished without
/// doing that work again, or deleted.
/// </para>
///
/// <para>The two kinds need different things (see <see cref="PendingKind"/>). A rip is already tracks and
/// only needs to be imported. A vinyl recording is one long side, or a whole album, that must be split
/// first. A vinyl folder stays listed after its split, because its source album is kept in
/// <c>Originals/</c>. It can be split again without playing the record, and it can be deleted when it is
/// no longer needed.</para>
/// </summary>
public static class PendingRips
{
    /// <summary>The folder names that <see cref="CdRipper"/> and <see cref="DvdRipper"/> use for their staging folders.</summary>
    public const string FolderPrefix = "rip-";

    /// <summary>The folder names that the vinyl wizard uses for its session folders.</summary>
    public const string VinylFolderPrefix = "vinyl-";

    /// <summary>
    /// Lists every unfinished rip and vinyl session under <paramref name="stagingRoot"/>, newest first. An
    /// empty rip folder is not counted as pending. Its import has moved the files out and just failed to tidy up.
    /// </summary>
    public static IReadOnlyList<PendingRip> Scan(string stagingRoot, Func<string, TrackInfo>? readTrack = null)
    {
        readTrack ??= TagReader.Read;

        if (!Directory.Exists(stagingRoot))
            return [];

        var pending = new List<PendingRip>();
        foreach (var folder in Directory.EnumerateDirectories(stagingRoot, FolderPrefix + "*"))
            if (Read(folder, PendingKind.Rip, readTrack) is { } rip)
                pending.Add(rip);
        foreach (var folder in Directory.EnumerateDirectories(stagingRoot, VinylFolderPrefix + "*"))
            if (Read(folder, PendingKind.Vinyl, readTrack) is { } vinyl)
                pending.Add(vinyl);

        return pending.OrderByDescending(r => r.RippedUtc).ToList();
    }

    private static PendingRip? Read(string folder, PendingKind kind, Func<string, TrackInfo> readTrack)
    {
        // The session's own audio is not the album that a split keeps beside it. The scanner shows the
        // Originals folder now (it is a real folder in the import tree), so it is taken out of the count here.
        // A session's track count is its own tracks.
        var originals = AlbumFileSplitter.FindOriginalsDir(folder);
        IReadOnlyList<string> kept = originals is null ? [] : AudioFileScanner.Scan(originals).Files;
        var preserved = kept.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var top = AudioFileScanner.Scan(folder).Files.Where(f => !preserved.Contains(f)).ToList();

        if (top.Count == 0 && kept.Count == 0)
            return null;   // Already imported, or a rip that produced nothing.

        // Vinyl: the folder can hold three kinds of thing, and only one of them is tracks. The sides are the
        // recording. The joined album is what the split works on. What is left over is the tracks that the
        // split cut. Counting all three together would make a two-side record look like many sides.
        var stage = PendingVinylStage.None;
        int sides = 0;
        var tracks = top;

        if (kind == PendingKind.Vinyl)
        {
            var topSides = VinylImporter.SidesIn(folder);
            IReadOnlyList<string> keptSides = originals is null ? [] : VinylImporter.SidesIn(originals);
            bool joined = File.Exists(Path.Combine(folder, VinylImporter.AlbumFileName));

            var notTracks = topSides.Concat([Path.Combine(folder, VinylImporter.AlbumFileName)])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            tracks = top.Where(f => !notTracks.Contains(f)).ToList();

            // Sides are recorded at the top level, and they move into Originals once they are joined. So they
            // are counted wherever they are. An album with no sides beside it still counts as one side, because
            // that is the only thing that could have produced it.
            sides = topSides.Count > 0 ? topSides.Count
                : keptSides.Count > 0 ? keptSides.Count
                : joined ? 1 : 0;

            // A session whose own audio is gone still holds the album it was cut from. It is pending, because
            // that album can be split again without playing the record. An album still at the top level means
            // the split has not run, or is being run again. That is true whatever else is beside it, so it is
            // checked before the cut tracks.
            stage = top.Count == 0 ? PendingVinylStage.Kept
                : joined || tracks.Count == 0 ? PendingVinylStage.Unsplit
                : PendingVinylStage.TracksCut;

            if (stage != PendingVinylStage.TracksCut)
                tracks = [];
        }

        string? album = null, artist = null;
        var tagged = tracks.Count > 0 ? tracks : top.Count > 0 ? top : kept;
        try
        {
            var first = readTrack(tagged[0]);
            album = Blank(first.Album);
            artist = Blank(first.AlbumArtist) ?? Blank(first.Artist);
        }
        catch
        {
            // A half-written file from a rip that stopped during a track, or a raw side with no tags at all.
            // It is still pending, and what the rip wrote about itself still names it.
        }

        // What the rip knew before it started. The tags win when there are any, because the user may have
        // corrected them since. The manifest answers for everything the tags cannot: an untagged recording,
        // or a rip that stopped before it tagged a single file, and when it started.
        var manifest = RipManifest.Read(folder);

        // Size is everything in the folder, including the kept sides. That is what deleting it gives back.
        long size = 0;
        foreach (var file in top.Concat(kept))
        {
            try { size += new FileInfo(file).Length; } catch { }
        }

        return new PendingRip(
            Folder: folder,
            Name: album ?? Blank(manifest?.Title),
            Artist: artist ?? Blank(manifest?.Artist),
            TrackCount: tracks.Count,
            SizeBytes: size,
            RippedUtc: manifest?.CreatedUtc is { } started && started != default
                ? started
                : Directory.GetCreationTimeUtc(folder),
            Kind: kind,
            Stage: stage,
            SideCount: sides,
            PlannedTrackCount: manifest?.TrackCount ?? 0,
            Source: manifest?.Source);
    }

    /// <summary>
    /// Throws away the rip or vinyl session. This is guarded so it only deletes a folder that sits directly
    /// inside <paramref name="stagingRoot"/>. It deletes a directory tree, and the one folder it must never
    /// delete is the staging root itself, which is also where library.db lives.
    /// </summary>
    public static void Delete(string stagingRoot, string folder)
    {
        var root = Path.GetFullPath(stagingRoot);
        var target = Path.GetFullPath(folder);

        var name = Path.GetFileName(target);
        if (!name.StartsWith(FolderPrefix, StringComparison.Ordinal) &&
            !name.StartsWith(VinylFolderPrefix, StringComparison.Ordinal))
            throw new ArgumentException($"Not a rip or vinyl folder: {folder}", nameof(folder));

        var parent = Path.GetDirectoryName(target);
        if (parent is null || !string.Equals(Path.GetFullPath(parent), root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Not inside the staging folder: {folder}", nameof(folder));

        Directory.Delete(target, recursive: true);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
