using RPM.Core.Library;

namespace RPM.Core.Migration;

/// <summary>What happened to one source playlist.</summary>
/// <param name="Name">The rpm playlist's name (with the source's name in brackets when it was renamed).</param>
/// <param name="Entries">Entries in the rpm playlist that came from the source.</param>
/// <param name="Unmatched">Entries that are not a track in rpm (not imported, or not in the source library).</param>
/// <param name="WasSmart">The source filled it by rules; it came along as a plain playlist.</param>
/// <param name="Renamed">A playlist the person made in rpm already had the name.</param>
public sealed record PlaylistOutcome(string Name, int Entries, int Unmatched, bool WasSmart, bool Renamed);

/// <summary>
/// Puts the source's playlists into rpm, after the tracks.
///
/// <para>The first time, each playlist is created in its folders with the source's entries, in order and
/// with repeats. A name a playlist the person made in rpm already uses gets the source's name in
/// brackets ("Road trip (MusicBee)"), so nothing the person made is changed. After that the import
/// record links the two playlists. While the rpm playlist still holds exactly what the import put in,
/// the source's new entries take their places in the source's order (a run stopped at "cloud full" and
/// continued later). Once the person has changed it, new entries only go on the end, so edits made in
/// rpm stay. A playlist the person deleted in rpm stays deleted.</para>
/// </summary>
public static class MigrationPlaylists
{
    /// <param name="sourceLabel">The name shown in brackets on a renamed playlist: the source's name, or
    /// for playlist files the translated label for them.</param>
    public static IReadOnlyList<PlaylistOutcome> Apply(
        LibraryDatabase db, SourceLibrary library, LibraryDatabase.MigrationRecord record, string sourceLabel)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(record);

        var trackIds = record.TrackIds();
        var bySource = library.Tracks.GroupBy(t => t.SourceId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var fallback = library.Kind == SourceKind.PlaylistFiles ? PathTails.Build(db) : null;
        var outcomes = new List<PlaylistOutcome>();

        foreach (var playlist in library.Playlists)
        {
            // Each entry as (source id, the song in rpm), or no song when it is not in rpm.
            var resolved = new List<(string SourceId, (string? RpmId, string RelPath) Song)>();
            var unmatched = playlist.Unresolved.Count;
            foreach (var entry in playlist.TrackIds)
            {
                if (Resolve(db, entry, trackIds, bySource, fallback) is { } song)
                    resolved.Add((entry, song));
                else
                    unmatched++;
            }

            var nodes = db.GetPlaylistNodes();
            if (record.Playlist(playlist.SourceId) is { } before)
            {
                var node = nodes.FirstOrDefault(n => n.Id == before.PlaylistId);
                if (node is null)
                {
                    outcomes.Add(new PlaylistOutcome(playlist.Name, 0, unmatched, playlist.Smart, false));
                    continue;
                }

                // The entries not imported before, counted with repeats, in the source's order.
                var left = before.Entries.GroupBy(e => e, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
                var fresh = new List<(string SourceId, (string? RpmId, string RelPath) Song)>();
                foreach (var item in resolved)
                {
                    if (left.TryGetValue(item.SourceId, out var n) && n > 0)
                        left[item.SourceId] = n - 1;
                    else
                        fresh.Add(item);
                }
                var entries = before.Entries.Concat(fresh.Select(f => f.SourceId)).ToList();
                if (fresh.Count > 0)
                {
                    var untouched = Untouched(db.GetPlaylistItems(node.Id), before.Entries, resolved);
                    db.AddSongsToPlaylist(node.Id, fresh.Select(f => f.Song), AlreadyInPlaylist.AddAnother);
                    // Noted in the order written, so a stop before the reorder below leaves a record that
                    // still matches the playlist.
                    record.NotePlaylist(playlist.SourceId, node.Id, entries);
                    if (untouched && InSourceOrder(db.GetPlaylistItems(node.Id), entries, resolved) is { } order)
                    {
                        db.ReorderPlaylistItems(node.Id, order);
                        entries = [.. resolved.Select(r => r.SourceId)];
                    }
                }
                record.NotePlaylist(playlist.SourceId, node.Id, entries);
                outcomes.Add(new PlaylistOutcome(node.Name, entries.Count, unmatched, playlist.Smart, false));
                continue;
            }

            var parent = db.EnsurePlaylistFolder(playlist.Folder);
            var name = string.IsNullOrWhiteSpace(playlist.Name) ? sourceLabel : playlist.Name.Trim();

            // Another device may have imported this playlist already (its record is its own), and the cloud
            // sync took it in: a playlist in the same folder, under this name or with the source's name in
            // brackets, holding exactly these songs in this order, is that one. It is linked instead of made
            // a second time.
            if (resolved.Count > 0 && nodes.FirstOrDefault(n => n.Kind == PlaylistNodeKind.Playlist && n.ParentId == parent
                    && (string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(n.Name, $"{name} ({sourceLabel})", StringComparison.OrdinalIgnoreCase))
                    && Holds(db.GetPlaylistItems(n.Id), resolved)) is { } twin)
            {
                record.NotePlaylist(playlist.SourceId, twin.Id, [.. resolved.Select(r => r.SourceId)]);
                outcomes.Add(new PlaylistOutcome(twin.Name, resolved.Count, unmatched, playlist.Smart, false));
                continue;
            }

            var taken = nodes.Any(n => n.Kind == PlaylistNodeKind.Playlist && n.ParentId == parent
                && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
            if (taken)
                name = $"{name} ({sourceLabel})";

            var id = db.CreatePlaylist(parent, name);
            db.AddSongsToPlaylist(id, resolved.Select(r => r.Song), AlreadyInPlaylist.AddAnother);
            record.NotePlaylist(playlist.SourceId, id, [.. resolved.Select(r => r.SourceId)]);
            outcomes.Add(new PlaylistOutcome(name, resolved.Count, unmatched, playlist.Smart, taken));
        }
        return outcomes;
    }

    /// <summary>Whether the playlist holds exactly the songs the import put in, in that order, and nothing else.</summary>
    private static bool Untouched(
        IReadOnlyList<PlaylistItem> items, IReadOnlyList<string> made, IReadOnlyList<(string SourceId, (string? RpmId, string RelPath) Song)> resolved)
    {
        if (items.Count != made.Count)
            return false;
        var songOf = new Dictionary<string, (string? RpmId, string RelPath)>(StringComparer.Ordinal);
        foreach (var (sourceId, song) in resolved)
            songOf.TryAdd(sourceId, song);
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].ItemKind != PlaylistItemKind.Song || !songOf.TryGetValue(made[i], out var song) || !Same(items[i], song))
                return false;
        }
        return true;
    }

    /// <summary>Whether the playlist holds exactly these songs, in this order, and nothing else.</summary>
    private static bool Holds(IReadOnlyList<PlaylistItem> items, IReadOnlyList<(string SourceId, (string? RpmId, string RelPath) Song)> resolved) =>
        items.Count == resolved.Count
        && items.Zip(resolved).All(p => p.First.ItemKind == PlaylistItemKind.Song && Same(p.First, p.Second.Song));

    private static bool Same(PlaylistItem item, (string? RpmId, string RelPath) song) =>
        item.RpmId is { Length: > 0 } id && song.RpmId is { Length: > 0 }
            ? string.Equals(id, song.RpmId, StringComparison.Ordinal)
            : string.Equals(item.RelPath, song.RelPath, StringComparison.Ordinal);

    /// <summary>The playlist's item ids in the source's order, or null when the items are not one per
    /// entry (something changed the playlist meanwhile). <paramref name="entries"/> names the source
    /// entry of each item, in the order the items are now.</summary>
    private static List<long>? InSourceOrder(
        IReadOnlyList<PlaylistItem> items, IReadOnlyList<string> entries, IReadOnlyList<(string SourceId, (string? RpmId, string RelPath) Song)> resolved)
    {
        if (items.Count != entries.Count || items.Count != resolved.Count)
            return null;
        var bySource = new Dictionary<string, Queue<long>>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            if (!bySource.TryGetValue(entries[i], out var ids))
                bySource[entries[i]] = ids = new Queue<long>();
            ids.Enqueue(items[i].Id);
        }
        var order = new List<long>(items.Count);
        foreach (var (sourceId, _) in resolved)
        {
            if (!bySource.TryGetValue(sourceId, out var ids) || !ids.TryDequeue(out var id))
                return null;
            order.Add(id);
        }
        return order;
    }

    private static (string? RpmId, string RelPath)? Resolve(
        LibraryDatabase db, string entry, IReadOnlyDictionary<string, long> trackIds,
        Dictionary<string, SourceTrack> bySource, PathTails? fallback)
    {
        if (trackIds.TryGetValue(entry, out var id) && db.FindById(id) is { } row)
            return (row.RpmId, row.RelativePath);
        if (fallback is null || !bySource.TryGetValue(entry, out var track))
            return null;
        return fallback.Find(track) is { } found ? (found.RpmId, found.RelativePath) : null;
    }

    /// <summary>
    /// Finds the library row for a playlist file entry that was not imported (its path is from another
    /// computer, for example). First by the end of the path, the last two folders and the file name,
    /// which is how most libraries are laid out (artist, album, file); then by the artist and title from
    /// the #EXTINF line, when exactly one row has them.
    /// </summary>
    private sealed class PathTails
    {
        private readonly Dictionary<string, LibraryTrack> _byTail = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<LibraryTrack>> _byArtistTitle = new(StringComparer.Ordinal);

        public static PathTails Build(LibraryDatabase db)
        {
            var tails = new PathTails();
            foreach (var row in db.Query())
            {
                tails._byTail.TryAdd(Tail(row.RelativePath), row);
                var key = MigrationEstimate.Key(row.Artist, "", row.Title);
                if (!tails._byArtistTitle.TryGetValue(key, out var rows))
                    tails._byArtistTitle[key] = rows = [];
                rows.Add(row);
            }
            return tails;
        }

        public LibraryTrack? Find(SourceTrack track)
        {
            if (track.File is LocalFile local && _byTail.TryGetValue(Tail(local.Path), out var byPath))
                return byPath;
            return track.Artist.Length > 0 && _byArtistTitle.TryGetValue(MigrationEstimate.Key(track.Artist, "", track.Title), out var rows) && rows.Count == 1
                ? rows[0]
                : null;
        }

        private static string Tail(string path)
        {
            var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            return string.Join('/', parts.Skip(Math.Max(0, parts.Length - 3)));
        }
    }
}
