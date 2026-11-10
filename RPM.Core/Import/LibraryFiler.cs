using RPM.Core.Analysis;
using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Core.Models;

namespace RPM.Core.Import;

/// <summary>How a batch of tracks is filed: the naming template, copy or move, how multi-disc records
/// are filed (merged into one album folder, or a folder per disc), and the library folder.</summary>
public sealed record FilingRules(NamingTemplate Template, OrganizeMode Mode, bool MergedDiscs, string Root);

/// <summary>What happened to one track of a batch: the file's outcome, and the library row it became
/// (null when it did not land).</summary>
public sealed record Filed(ApplyResult Result, LibraryTrack? Row);

/// <summary>Where a batch is: files done out of the total, and the file being filed now.</summary>
public readonly record struct FilingProgress(int Done, int Total, string FileName);

/// <summary>
/// Files a batch of tracks into the library. The import queue's import and an import from another
/// app both go through here, so a file lands in the same place whichever way it came in.
///
/// <para>The steps, in order: spell each artist the way the library already does; name multi-disc
/// records by the multi-disc setting; plan every file by the naming template (<see cref="Plan"/>
/// gives the same plan without filing); copy or move each file and write its tags; record what landed
/// in the library; bring earlier discs of a record up to the new disc count; write the folder
/// pictures. The library rows are written in a finally block, so whatever landed on disk is recorded
/// even when the batch stops part-way.</para>
/// </summary>
public static class LibraryFiler
{
    /// <summary>Files <paramref name="tracks"/>. Returns one entry per track, in order. A track listed
    /// twice with the same file is filed once, and both entries name the same row. When the disk is
    /// full the batch stops; that track and every one after it come back not filed, with
    /// <see cref="ApplyResult.DiskFull"/> set.</summary>
    /// <param name="sourceRoots">The folders the files were added from (the folders the person dropped on
    /// the queue). After a move, the folders under them that the move emptied are removed; these folders
    /// themselves stay.</param>
    /// <param name="landed">Called for each file the moment it lands, before the batch goes on. A caller
    /// that keeps its own record of what was filed (the queue, an import's record) writes it here, so
    /// that record is right even when the batch stops part-way with an exception.</param>
    public static IReadOnlyList<Filed> File(
        LibraryDatabase db, IReadOnlyList<TrackInfo> tracks, FilingRules rules, IReadOnlyCollection<string> sourceRoots,
        IProgress<FilingProgress>? progress, Action<TrackInfo, Filed> landed, CancellationToken ct)
        => File(db, tracks, rules, sourceRoots, progress, landed, ct, (s, root, mode) => OrganizeApplier.Apply(s, root, mode));

    /// <summary>The same, with the step that copies or moves one file replaced (for tests).</summary>
    internal static IReadOnlyList<Filed> File(
        LibraryDatabase db, IReadOnlyList<TrackInfo> tracks, FilingRules rules, IReadOnlyCollection<string> sourceRoots,
        IProgress<FilingProgress>? progress, Action<TrackInfo, Filed> landed, CancellationToken ct,
        Func<TrackSuggestion, string, OrganizeMode, ApplyResult> apply)
    {
        ArgumentNullException.ThrowIfNull(sourceRoots);
        ArgumentNullException.ThrowIfNull(landed);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(rules);

        // One file is filed once, however many times the batch names it.
        var distinct = tracks
            .GroupBy(t => t.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var (prepared, suggestions) = Prepare(db, distinct, rules.Template, rules.MergedDiscs, rules.Root);

        var byPath = new Dictionary<string, Filed>(StringComparer.OrdinalIgnoreCase);
        var mirrored = new List<LibraryTrack>(suggestions.Count);
        var done = 0;
        var diskFull = false;
        var lastReport = System.Diagnostics.Stopwatch.StartNew();
        progress?.Report(new FilingProgress(0, suggestions.Count, ""));

        try
        {
            foreach (var s in suggestions)
            {
                if (diskFull || ct.IsCancellationRequested)
                {
                    byPath[s.Track.FilePath] = new Filed(NotFiled(s, diskFull), null);
                    continue;
                }

                // A file waiting in the inbox is always moved, whatever the copy setting says. The inbox
                // is a staging area inside the library, not a source worth keeping: a copy would leave
                // the album there, to be offered again on every launch.
                var mode = LibraryInbox.IsInside(rules.Root, s.Track.FilePath) ? OrganizeMode.Move : rules.Mode;
                var result = apply(s, rules.Root, mode);
                done++;
                if (lastReport.ElapsedMilliseconds >= 60 || done == suggestions.Count)
                {
                    progress?.Report(new FilingProgress(done, suggestions.Count, Path.GetFileName(s.Track.FilePath)));
                    lastReport.Restart();
                }

                if (!result.Success)
                {
                    diskFull = result.DiskFull;
                    byPath[s.Track.FilePath] = new Filed(result, null);
                    continue;
                }

                // The row is read from the file that landed, with the tags just written, so the library
                // holds the file's truth and not the plan's.
                LibraryTrack row;
                try
                {
                    row = LibraryDatabase.FromTrackInfo(TagReader.Read(result.DestinationPath), rules.Root);
                }
                catch
                {
                    row = LibraryDatabase.FromTrackInfo(s.Track with { FilePath = result.DestinationPath }, rules.Root);
                }
                mirrored.Add(row);
                var filed = new Filed(result, row);
                byPath[s.Track.FilePath] = filed;
                foreach (var input in tracks.Where(t => string.Equals(t.FilePath, s.Track.FilePath, StringComparison.OrdinalIgnoreCase)))
                    landed(input, filed);
            }
        }
        finally
        {
            db.UpsertMany(mirrored);
        }

        var results = byPath.Values.Select(f => f.Result).ToList();

        if (rules.MergedDiscs)
            BackfillAlbumDiscCounts(db, prepared, rules.Root);

        // Moves leave empty folders behind in the places the files came from.
        if (rules.Mode == OrganizeMode.Move && results.Any(r => r.Success))
            foreach (var source in sourceRoots)
                OrganizeApplier.CleanupEmptyFolders(source);

        // Folder pictures. The carry runs first: a rip stages the full-size scan beside its tracks, and
        // WriteCovers would otherwise fill the folder from a track's smaller embedded copy.
        FolderArtWriter.CarryCoverImages(results);
        FolderArtWriter.WriteCovers(results);
        FolderArtWriter.CarryDiscImages(results);

        return [.. tracks.Select(t => byPath.TryGetValue(t.FilePath, out var filed)
            ? filed
            : new Filed(new ApplyResult { SourcePath = t.FilePath, DestinationPath = "", Success = false, Error = "not planned" }, null))];
    }

    /// <summary>Where each track would land, without filing anything. This is the same preparation
    /// <see cref="File"/> does, so a preview of the plan never differs from what the import does.</summary>
    public static IReadOnlyList<TrackSuggestion> Plan(
        LibraryDatabase db, IReadOnlyList<TrackInfo> tracks, NamingTemplate template, bool merged, string root)
        => Prepare(db, tracks, template, merged, root).Plan;

    private static ApplyResult NotFiled(TrackSuggestion s, bool diskFull) => new()
    {
        SourcePath = s.Track.FilePath,
        DestinationPath = "",
        Success = false,
        Error = diskFull ? "The disk is full." : "Stopped.",
        DiskFull = diskFull,
    };

    private static (List<TrackInfo> Prepared, IReadOnlyList<TrackSuggestion> Plan) Prepare(
        LibraryDatabase db, IReadOnlyList<TrackInfo> tracks, NamingTemplate template, bool merged, string root)
    {
        // Each artist is spelled the way the library already spells them, before anything decides where
        // a file goes, so "guns n roses" files onto the "Guns N' Roses" shelf.
        var artists = ArtistCanonicalizer.FromLibrary(db);
        var prepared = tracks.Select(artists.Apply).Select(t => DiscNamed(t, merged)).ToList();

        // Merged: a disc of a set lands in the folder its earlier discs already occupy.
        Func<TrackInfo, string?>? albumFolderOverride = merged
            ? t => t.DiscNumber > 0 && !string.IsNullOrWhiteSpace(t.Album)
                ? db.FindAlbumFolder(t.Album!, t.AlbumArtist)
                : null
            : null;

        // Merged discs share a folder, so the template names each file from the merged album's continuing
        // count, counting the discs already on the shelf too.
        var albumSets = merged ? BuildMergedAlbums(db, prepared, root) : null;
        Func<TrackInfo, TrackInfo>? namingView = albumSets is null
            ? null
            : t => albumSets.TryGetValue(MergedAlbumKey(t), out var siblings)
                ? MultiDiscImport.NamedInMergedAlbum(t, siblings, template)
                : t;

        return (prepared, RenamePlanner.Plan(prepared, template, root, albumFolderOverride, namingView));
    }

    /// <summary>What a track on a numbered disc is called for filing. Merged removes a disc marker the
    /// tagger wrote into the album name, so the discs agree which record they are; separate adds one, so
    /// the discs file apart. See <see cref="MultiDiscImport.MergedAlbumName"/>.</summary>
    private static TrackInfo DiscNamed(TrackInfo track, bool merged)
    {
        if (track.DiscNumber == 0 || track.DiscCount <= 1 || string.IsNullOrWhiteSpace(track.Album))
            return track;

        return track with
        {
            Album = merged
                ? MultiDiscImport.MergedAlbumName(track.Album!, track.DiscNumber, track.DiscCount)
                : MultiDiscImport.SeparateAlbumName(track.Album!, track.DiscNumber, track.DiscSubtitle),
        };
    }

    /// <summary>Which record a track belongs to, for merged filing: the album under the artist that it
    /// files under. The same key as the queue's tree and the disc backfill.</summary>
    private static (string Album, string Artist) MergedAlbumKey(TrackInfo track)
        => (AlbumResolver.NormalizeAlbum(track.Album ?? ""),
            (string.IsNullOrWhiteSpace(track.AlbumArtist) ? track.Artist : track.AlbumArtist) ?? "");

    /// <summary>Every track of each multi-disc record being filed, including the ones already in the
    /// library. Disc 1 is often filed already; without its rows, disc 2 would restart at 1 and land on
    /// top of it.</summary>
    private static Dictionary<(string Album, string Artist), List<TrackInfo>> BuildMergedAlbums(
        LibraryDatabase db, IReadOnlyList<TrackInfo> tracks, string root)
    {
        var sets = new Dictionary<(string Album, string Artist), List<TrackInfo>>();
        foreach (var track in tracks.Where(t => t.DiscNumber > 0 && !string.IsNullOrWhiteSpace(t.Album)))
        {
            var key = MergedAlbumKey(track);
            if (!sets.TryGetValue(key, out var members))
                sets[key] = members = [];
            members.Add(track);
        }

        foreach (var (key, members) in sets)
        {
            if (db.FindAlbumFolder(key.Album, key.Artist) is not { } folder)
                continue;
            foreach (var shelved in db.QueryUnderFolder(folder))
            {
                // Only what the count needs: which disc, which track, and how long that disc is. A library
                // row has no track count; DiscNumbering falls back to the highest number filed.
                members.Add(new TrackInfo
                {
                    FilePath = Path.Combine(root, shelved.RelativePath),
                    Extension = shelved.Extension,
                    DiscNumber = shelved.DiscNumber,
                    DiscCount = shelved.DiscCount,
                    TrackNumber = shelved.TrackNumber,
                });
            }
        }
        return sets;
    }

    /// <summary>After a disc joins an album that already held earlier discs, brings those earlier tracks
    /// up to the set's disc count (disc 1 where they carry none). Best effort, per file.</summary>
    private static void BackfillAlbumDiscCounts(LibraryDatabase db, IReadOnlyList<TrackInfo> imported, string root)
    {
        // The tracks already in a kept folder are never retagged, so a disc added to their album leaves
        // their disc counts as they are.
        if (KeptFiles.Covers(root))
            return;
        var albums = imported
            .Where(t => t.DiscNumber > 0 && t.DiscCount > 1 && !string.IsNullOrWhiteSpace(t.Album))
            .GroupBy(t => (Album: AlbumResolver.NormalizeAlbum(t.Album!), t.AlbumArtist));

        foreach (var album in albums)
        {
            uint discCount = album.Max(t => t.DiscCount);
            var folder = db.FindAlbumFolder(album.Key.Album, album.Key.AlbumArtist);
            if (folder is null)
                continue;

            var updated = new List<LibraryTrack>();
            foreach (var existing in db.QueryUnderFolder(folder))
            {
                var abs = Path.Combine(root, existing.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                TrackInfo info;
                try { info = TagReader.Read(abs); }
                catch { continue; }

                uint disc = info.DiscNumber > 0 ? info.DiscNumber : 1;
                if (info.DiscCount == discCount && info.DiscNumber == disc)
                    continue;   // already consistent

                try
                {
                    TagReader.WriteAll(abs, info with { DiscNumber = disc, DiscCount = discCount });
                    updated.Add(LibraryDatabase.FromTrackInfo(TagReader.Read(abs), root));
                }
                catch { /* leave this file as it was */ }
            }

            if (updated.Count > 0)
                db.UpsertMany(updated);
        }
    }
}
