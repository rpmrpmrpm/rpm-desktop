using RPM.Core.Library;
using RPM.Core.Import;

namespace RPM.Core.Cloud;

/// <summary>
/// Turns the user's cloud catalogue into library rows, so an install shows the music it holds
/// <em>and</em> the music that their other installs hold.
///
/// <para>
/// The library is a mirror of one folder. A second computer signed into the same account would see
/// only what is on its own drive, and a library fully offloaded from the first computer would be
/// invisible everywhere. The cloud is the one place that knows the whole collection, so its rows are
/// adopted into the mirror. They are marked <see cref="CloudState.CloudOnly"/> and
/// <see cref="LibraryTrack.CloudAdopted"/>. From then on every view, every search, the queue and the
/// playlists treat them as what they are: tracks with no local file, which play by streaming.
/// </para>
///
/// <para>
/// <b>What one album is</b> does not change here. A cloud row carries the library path it was filed
/// under (<see cref="CloudTrack.RelativePath"/>), so an adopted row lands in the same folder that it
/// has on the machine that uploaded it. A folder with tracks in it is an album, on the wall, on the
/// phone and here. This is also why the same CD ripped on two machines is one album and not two: the
/// naming template puts both rips at the same relative path.
/// </para>
/// </summary>
public static class CloudLibraryMirror
{
    /// <summary>
    /// One cloud row, ready to be a library row. Nothing here reads a file. Everything comes from the
    /// catalogue, which is all that an install that has never held these bytes can know.
    /// </summary>
    public static LibraryTrack ToTrack(CloudTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);

        var relative = PathFor(track);
        var cut = relative.LastIndexOf('/');
        var name = cut < 0 ? relative : relative[(cut + 1)..];
        var tags = track.Tags;

        return new LibraryTrack
        {
            RelativePath = relative,
            Folder = cut < 0 ? "" : relative[..cut],
            FileName = name,
            Extension = Path.GetExtension(name).ToLowerInvariant(),

            // What the file is, as far as the catalogue knows. The size is the original's, which is what the
            // row means by size everywhere else. A mirror row has no modified time because there is no file
            // that could have written one.
            SizeBytes = track.OriginalSize,
            ModifiedUtcTicks = 0,
            AddedUtcTicks = track.AddedAt > 0
                ? DateTimeOffset.FromUnixTimeSeconds(track.AddedAt).UtcTicks
                : 0,

            DurationSeconds = tags?.DurationSeconds ?? 0,
            Title = Trimmed(track.Title),
            Artist = Trimmed(track.Artist),
            AlbumArtist = Trimmed(tags?.AlbumArtist),
            Album = Trimmed(track.Album),
            Genre = Trimmed(tags?.Genre),
            TrackNumber = Position(tags?.TrackNumber),
            DiscNumber = Position(tags?.DiscNumber),
            DiscCount = Position(tags?.DiscCount),
            Year = Position(tags?.Year),

            // Zero and not null: null means "never read", which sends a row back to its file to be measured,
            // and this row has no file. What the catalogue knows is all there will be until the track is
            // brought down onto this disk.
            Channels = tags?.Channels ?? 0,
            SampleRate = tags?.SampleRate ?? 0,
            BitsPerSample = tags?.BitsPerSample ?? 0,
            BitrateKbps = tags?.BitrateKbps ?? 0,
            CodecDescription = Trimmed(tags?.CodecDescription),

            // Which composition it is part of, so a classical album adopted from the cloud is drawn as works
            // and movements here too. The phone and this machine read one catalogue. The movement number is
            // zero for the reason above: there is no file to go back and read.
            Work = Trimmed(tags?.Work),
            MovementName = Trimmed(tags?.MovementName),
            MovementNumber = tags?.MovementNumber ?? 0,
            Composer = Trimmed(tags?.Composer),

            // How loud the recording is, so a track that has only ever been in the cloud is levelled with the
            // tracks around it. Null when the catalogue has not been told yet. For a row with no file here,
            // the backfill in the transcoder fills it, not this machine.
            LoudnessLufs = tags is { } t && t.HasLoudness ? t.LoudnessLufs : null,
            TruePeak = tags is { TruePeak: > 0 } p ? p.TruePeak : null,

            // The listener's own mark, as another of their machines left it. A second install that adopts a
            // library it has never held should find the favourites where they were left. This is the reason
            // the mark travels at all.
            IsFavorite = tags?.Favorite ?? false,
            FavoritedUtcTicks = tags is { Favorite: true } marked ? marked.FavoritedAt : 0,

            // The advisory flag, so the E on another machine's file is drawn here too. Null when the
            // catalogue states none, and the mirror's writes read that as "keep what the row has".
            Advisory = tags?.StatedAdvisory,

            RpmId = track.RpmId,
            ContentHash = Trimmed(track.Sha256),
            CloudState = CloudState.CloudOnly,

            // In the cloud, and so part of the selection, but not pinned: nobody at THIS computer chose it,
            // and a pin is a claim made by hand.
            CloudSelected = true,
            CloudPinned = false,
            CloudAdopted = true,

            // The store's stamp on the row this was built from. The next pass compares against it to know
            // whether the catalogue has changed since. See CloudSyncPlan.
            CloudUpdatedAt = track.UpdatedAt,
        };
    }

    /// <summary>
    /// The library path an adopted row takes: the one the catalogue carries, or a path built from its
    /// tags when it carries none.
    ///
    /// <para>The field is optional on the wire, so a row can have no path. It must still land somewhere
    /// definite, or it would be adopted again under a new name on every pass. Artist/Album/track is the
    /// library's own shape, so a path built this way reads as an album and not as a loose file at the
    /// root.</para>
    /// </summary>
    public static string PathFor(CloudTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (Normalize(track.RelativePath) is { Length: > 0 } stored)
            return stored;

        var artist = PathSanitizer.SanitizeSegment(
            string.IsNullOrWhiteSpace(track.Tags?.AlbumArtist) ? track.Artist : track.Tags!.AlbumArtist,
            "Unknown Artist");
        var album = PathSanitizer.SanitizeSegment(track.Album, "Unknown Album");
        var title = PathSanitizer.SanitizeSegment(track.Title, track.RpmId);
        var extension = Trimmed(track.Tags?.Extension) is { Length: > 0 } ext
            ? (ext.StartsWith('.') ? ext : "." + ext)
            : ".flac";

        var number = track.Tags?.TrackNumber is > 0 ? $"{track.Tags!.TrackNumber:00} " : "";
        return $"{artist}/{album}/{number}{title}{extension}";
    }

    /// <summary>
    /// The listing with one row per library path and one row per track id. When rows clash, the oldest
    /// keeps the path.
    ///
    /// <para>
    /// One path is one track. This is the "an album is a unique set of files" rule, applied where it
    /// shows. Two installs that rip the same CD produce the same files at the same relative path. If
    /// both were uploaded before either had seen the other, the account holds two catalogue rows for one
    /// track, and adopting both would show an album with every song twice.
    /// </para>
    ///
    /// <para>
    /// One id is one track. This is the same rule read the other way, and a mirror pass needs both. The
    /// id is stamped in the file, so a file copied on the machine that owns it carries the id twice, and
    /// a library has no unique index on the column, so a peer's listing can name one id at two paths.
    /// The pass keys the rows it already holds by id. A listing like that would adopt two rows on the
    /// first pass, and on the next pass it would try to move one onto the other's path, which the path
    /// index refuses. The temporary listing table already keeps one row per id (its primary key). This
    /// is the same answer, decided here, where the winner is chosen and not left to insertion order.
    /// </para>
    ///
    /// <para>A row with no id keeps its place. Neither rule drops it, because there is no id to compare
    /// it by.</para>
    /// </summary>
    public static IReadOnlyList<CloudTrack> OneRowPerTrack(IReadOnlyList<CloudTrack> cloud)
    {
        ArgumentNullException.ThrowIfNull(cloud);

        var byPath = new Dictionary<string, CloudTrack>(cloud.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var track in cloud)
        {
            var path = PathFor(track);

            if (!byPath.TryGetValue(path, out var standing) || Earlier(track, standing))
                byPath[path] = track;
        }

        var byId = new Dictionary<string, CloudTrack>(byPath.Count, StringComparer.Ordinal);
        var idless = new List<CloudTrack>();

        foreach (var track in byPath.Values)
        {
            if (string.IsNullOrEmpty(track.RpmId))
            {
                idless.Add(track);
                continue;
            }

            if (!byId.TryGetValue(track.RpmId, out var standing) || Earlier(track, standing))
                byId[track.RpmId] = track;
        }

        return [.. byId.Values, .. idless];
    }

    /// <summary>Which of two rows for one path the library keeps: the one that was uploaded first, or the
    /// lower id if they were uploaded together. The choice is arbitrary, but it gives the same answer
    /// on every machine and on every pass, and that is what matters.</summary>
    private static bool Earlier(CloudTrack candidate, CloudTrack standing)
        => candidate.AddedAt != standing.AddedAt
            ? candidate.AddedAt < standing.AddedAt
            : string.CompareOrdinal(candidate.RpmId, standing.RpmId) < 0;

    // ── which album a cloud row belongs to, and whether this library already has it ──

    /// <summary>The album a cloud row belongs to, as the whole app groups albums: by the folder it is
    /// filed in, or by its tags if it carries no path. See <see cref="CloudAlbumGrouping"/>.</summary>
    public static string AlbumKeyOf(CloudTrack track) => CloudAlbumGrouping.KeyFor(track);

    /// <summary>
    /// An album's identity by what it is and not where it sits: its album artist and its title, with
    /// the disc decoration removed. Null when neither is known, because a folder that says nothing about
    /// itself must not match every other folder that says nothing.
    /// </summary>
    public static string? TaggedAlbumKey(string? albumArtist, string? artist, string? album)
    {
        var owner = string.IsNullOrWhiteSpace(albumArtist) ? artist : albumArtist;

        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(album))
            return null;

        return CloudAlbumGrouping.KeyFor(relativePath: null, albumArtist, artist, album);
    }

    /// <summary>
    /// Whether this library already holds this album, filed somewhere else.
    ///
    /// <para>
    /// The path is checked first, and a match there is <b>not</b> this case. A cloud album in a folder
    /// that this library also uses is the same shelf, so its tracks are adopted into it and the album
    /// fills in and does not double. This is what an album that was half uploaded from another machine
    /// should do.
    /// </para>
    ///
    /// <para>
    /// This method exists for the other case. An album filed at a path that this library does not use,
    /// whose album artist and title match an album already on the shelf, is the same album under
    /// another name. It can come from a rip made on both machines, or from a library re-filed under a
    /// different naming template. Adopting it would draw the album twice, which nobody wants.
    /// </para>
    /// </summary>
    public static bool IsSameAlbumElsewhere(
        IReadOnlyList<CloudTrack> album, ISet<string> localFolders, ISet<string> localTagged)
    {
        ArgumentNullException.ThrowIfNull(album);
        ArgumentNullException.ThrowIfNull(localFolders);
        ArgumentNullException.ThrowIfNull(localTagged);

        if (album.Count == 0) return false;

        var folder = CloudAlbumGrouping.FolderOf(PathFor(album[0]));
        if (folder is not null && localFolders.Contains(folder))
            return false;   // the same shelf: adopt into it

        var owner = CloudAlbumGrouping.AlbumArtistFor(album, folder);
        var name = CloudAlbumGrouping.AlbumNameFor(album, folder);

        return TaggedAlbumKey(owner, owner, name) is { } key && localTagged.Contains(key);
    }

    /// <summary>
    /// Whether an adopted row already says what the catalogue says. It compares everything the library
    /// shows or sorts by. A pass that rewrote every row on every run would raise a library change, and
    /// with it a rebuild of the wall, every three minutes for ever.
    /// </summary>
    public static bool Same(LibraryTrack existing, LibraryTrack fresh)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(fresh);

        return string.Equals(existing.RpmId, fresh.RpmId, StringComparison.Ordinal)
            && string.Equals(existing.RelativePath, fresh.RelativePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.ContentHash, fresh.ContentHash, StringComparison.Ordinal)
            && string.Equals(existing.Title, fresh.Title, StringComparison.Ordinal)
            && string.Equals(existing.Artist, fresh.Artist, StringComparison.Ordinal)
            && string.Equals(existing.AlbumArtist, fresh.AlbumArtist, StringComparison.Ordinal)
            && string.Equals(existing.Album, fresh.Album, StringComparison.Ordinal)
            && string.Equals(existing.Genre, fresh.Genre, StringComparison.Ordinal)
            && existing.TrackNumber == fresh.TrackNumber
            && existing.DiscNumber == fresh.DiscNumber
            && existing.DiscCount == fresh.DiscCount
            // The composition a track belongs to is drawn (it is the heading a classical album is listed
            // under), so a row that learns its work has changed. Without this, a shelf taken before its
            // owner tagged the works would show a flat list for ever, because nothing else about those
            // rows changes when a work is named.
            && string.Equals(existing.Work, fresh.Work, StringComparison.Ordinal)
            && string.Equals(existing.MovementName, fresh.MovementName, StringComparison.Ordinal)
            && (existing.MovementNumber ?? 0) == (fresh.MovementNumber ?? 0)
            && string.Equals(existing.Composer, fresh.Composer, StringComparison.Ordinal)
            && existing.Year == fresh.Year
            && existing.SizeBytes == fresh.SizeBytes
            // The heart is shown, so a mark made on another computer has to count as a change here. An
            // adopted row left alone would keep the old answer until something else about it changed.
            && existing.IsFavorite == fresh.IsFavorite
            // Only where the catalogue states a flag. A catalogue with none keeps the row's own (see
            // UpdateAdoptedLocked), so counting that as a difference would rewrite the row every pass.
            && (fresh.Advisory is null || existing.Advisory == fresh.Advisory)
            && Math.Abs(existing.DurationSeconds - fresh.DurationSeconds) < 0.05;
    }

    private static string? Normalize(string? relativePath)
        => string.IsNullOrWhiteSpace(relativePath)
            ? null
            : relativePath.Trim().Replace('\\', '/').TrimStart('/');

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static uint Position(int? value) => value is > 0 ? (uint)value : 0u;
}
