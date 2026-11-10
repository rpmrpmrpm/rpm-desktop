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
/// record links the two playlists, and a second import only adds the source's new entries to the end.
/// It never removes or reorders, so edits made in rpm stay. A playlist the person deleted in rpm stays
/// deleted.</para>
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
                if (fresh.Count > 0)
                    db.AddSongsToPlaylist(node.Id, fresh.Select(f => f.Song), AlreadyInPlaylist.AddAnother);
                var entries = before.Entries.Concat(fresh.Select(f => f.SourceId)).ToList();
                record.NotePlaylist(playlist.SourceId, node.Id, entries);
                outcomes.Add(new PlaylistOutcome(node.Name, entries.Count, unmatched, playlist.Smart, false));
                continue;
            }

            var parent = db.EnsurePlaylistFolder(playlist.Folder);
            var name = string.IsNullOrWhiteSpace(playlist.Name) ? sourceLabel : playlist.Name.Trim();
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
