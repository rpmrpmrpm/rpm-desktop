using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Core.Models;

namespace RPM.Core.Cloud;

/// <summary>What one correction did, for the caller's log and counters.</summary>
public enum PullOutcome
{
    /// <summary>Taken in: the file, its place and the row all say what the other machine says.</summary>
    Applied,

    /// <summary>The file could not be written. Nothing is stamped, so the same correction is offered
    /// again on the next run and is neither lost nor half applied.</summary>
    TagsRefused,

    /// <summary>The mirror would not take the row. Nothing is stamped.</summary>
    RowRefused,
}

/// <summary>How the applier reaches the user's files. Passed in, so a test can stand in for a file that
/// will not open without making one on disk.</summary>
/// <param name="Write">Writes the fields a <see cref="TrackInfo"/> has values for and keeps the rest of the
/// file's tags (<see cref="TagReader.Write"/>).</param>
/// <param name="Read">Reads the file's own tags (<see cref="TagReader.Read"/>).</param>
/// <param name="LoadArt">Reads the file's embedded front cover, or null when it has none.</param>
/// <param name="WriteAll">Writes every field as it is in the <see cref="TrackInfo"/>, so an empty field
/// clears the tag (<see cref="TagReader.WriteAll"/>).</param>
public sealed record PullFiles(
    Action<string, TrackInfo> Write,
    Func<string, TrackInfo> Read,
    Func<string, byte[]?> LoadArt,
    Action<string, TrackInfo> WriteAll)
{
    /// <summary>The user's real files, through the tag reader.</summary>
    public static PullFiles OnDisk { get; } = new(
        TagReader.Write, TagReader.Read, path => TagReader.LoadArt(path)?.Bytes, TagReader.WriteAll);
}

/// <summary>
/// Taking a correction made on another of the user's machines and applying it to this one.
///
/// <para>
/// This is the second direction of sync. The library is the record and the cloud is a copy of it, so
/// an edit made on a second install would reach the store and stop there. Worse, this machine's next
/// catalogue pass would push its own older wording back over it. With two installs that is not a
/// backup arrangement but two libraries that disagree.
/// </para>
///
/// <para>
/// The order is essential and is not an implementation detail. First the file's own tags (the file
/// is the record, and a row corrected without it is reverted by the next sync, which re-reads
/// files). Then the row. Then the stamps, written after the file write, so that the modified time
/// the write produced does not read as an edit made here and get pushed straight back.
/// </para>
///
/// <para>
/// <b>Nothing here moves a file.</b> Where a file sits is decided by this machine's naming
/// template. If a pull re-filed the album to match the cloud's path, the cloud's memory of where a
/// file was would override the template that has since moved it. The re-filer would put the album
/// where the user asked, the pull would drag it back, one row per track, on every run. So the path
/// that a correction carries is not used.
/// </para>
///
/// <para>
/// <b>A rewritten sidecar is taken whole.</b> When the pull read the track's sidecar (see
/// <see cref="CloudPull.Sidecar"/>), every tag the sidecar states is written into the file, an empty one
/// included, and then the catalogue row's values over them. A tag the sidecar does not state keeps what the
/// file has. This is how a conductor, a lyric or an emptied genre changed in the web app reaches the file.
/// </para>
///
/// <para>This lives here and not in the sync service because it writes the user's files from data
/// that came from another machine, which is the last thing in the app to trust blindly.</para>
/// </summary>
public sealed class CloudPullApplier
{
    private readonly LibraryDatabase _db;
    private readonly string _root;

    /// <summary>The user's files. A write that throws means the file could not be written, and then the row
    /// is left alone.</summary>
    private readonly PullFiles _files;

    public CloudPullApplier(LibraryDatabase db, string root, PullFiles files)
    {
        _db = db;
        _root = root;
        _files = files;
    }

    /// <summary>Where a relative path lands on this disk.</summary>
    public string FullPath(string relativePath) =>
        Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>One correction: tags, then the row, then the stamps.</summary>
    /// <param name="sidecar">The track's sidecar, when the pull asked for it and the store has one. Null for
    /// a pull of the catalogue row alone.</param>
    public PullOutcome Apply(CloudPull pull, SidecarStatement? sidecar)
    {
        ArgumentNullException.ThrowIfNull(pull);

        return sidecar is null ? ApplyRow(pull) : ApplyWithSidecar(pull, sidecar);
    }

    /// <summary>The catalogue row's values, where it has them. Nothing is emptied: a blank in the row is
    /// "not known there".</summary>
    private PullOutcome ApplyRow(CloudPull pull)
    {
        var row = pull.Row;
        var updated = CloudSyncPlan.Apply(row, pull.Cloud);
        var path = FullPath(row.RelativePath);

        // A track that is only in the cloud has no file here, so for it the row is everything.
        if (File.Exists(path))
        {
            try
            {
                _files.Write(path, updated.ToTrackInfo(_root));
            }
            catch
            {
                return PullOutcome.TagsRefused;
            }

            // The row's own facts about the FILE stay this machine's: a size and a modified time describe
            // the file on this disk, and the tag write above has just changed the second of them.
            var info = new FileInfo(path);
            if (info.Exists)
                updated = updated with { SizeBytes = info.Length, ModifiedUtcTicks = info.LastWriteTimeUtc.Ticks };
        }

        return Settle(pull, updated);
    }

    /// <summary>
    /// The sidecar's stated tags over the file's own, then the catalogue row's values over those. The file
    /// is written only when that changes something, and with every field as stated, so an emptied field is
    /// cleared in the file too. A track with no file here gets the same values on its row.
    /// </summary>
    private PullOutcome ApplyWithSidecar(CloudPull pull, SidecarStatement sidecar)
    {
        var row = pull.Row;
        var path = FullPath(row.RelativePath);
        var onDisk = File.Exists(path);

        TrackInfo wanted;
        if (onDisk)
        {
            try
            {
                var before = _files.Read(path);
                wanted = CloudSyncPlan.Over(sidecar.Over(before), pull.Cloud);
                var art = sidecar.ArtOver(_files.LoadArt(path));

                if (wanted != before || art is not null)
                {
                    var mime = art is { Length: > 0 } ? sidecar.Sidecar.ArtMime ?? AlbumArt.SniffMime(art) : null;
                    _files.WriteAll(path, wanted with { PendingArt = art, PendingArtMime = mime });
                }
            }
            catch
            {
                return PullOutcome.TagsRefused;
            }
        }
        else
        {
            wanted = CloudSyncPlan.Over(sidecar.Over(row.ToTrackInfo(_root)), pull.Cloud);
        }

        var updated = CloudSyncPlan.Apply(row, pull.Cloud) with
        {
            Title = wanted.Title,
            Artist = wanted.Artist,
            AlbumArtist = wanted.AlbumArtist,
            Album = wanted.Album,
            Genre = wanted.Genre,
            Subgenre = wanted.Subgenre,
            Advisory = wanted.Advisory,
            TrackNumber = wanted.TrackNumber,
            DiscNumber = wanted.DiscNumber,
            DiscCount = wanted.DiscCount,
            DiscSubtitle = wanted.DiscSubtitle,
            Work = wanted.Work,
            MovementName = wanted.MovementName,
            MovementNumber = (int)wanted.MovementNumber,
            Composer = wanted.Composers,
            Year = wanted.Year,
            TagBpm = wanted.Bpm,
            TagKey = wanted.MusicalKey,
            ReleaseType = wanted.ReleaseType,
        };

        if (onDisk)
        {
            var info = new FileInfo(path);
            if (info.Exists)
                updated = updated with { SizeBytes = info.Length, ModifiedUtcTicks = info.LastWriteTimeUtc.Ticks };
        }

        TakePressing(row.Folder, sidecar);

        return Settle(pull, updated);
    }

    /// <summary>
    /// The pressing the sidecar states, kept on the album's own row. The catalogue push reads the pressing
    /// from there, and it sends a pressing the store does not have. Without this, a catalogue number
    /// emptied somewhere else would be sent back up by this machine on its next run.
    /// </summary>
    private void TakePressing(string? folder, SidecarStatement sidecar)
    {
        if (string.IsNullOrEmpty(folder))
            return;

        var stated = sidecar.Stated;
        var said = sidecar.Sidecar;
        var album = _db.GetAlbumMeta(folder);

        var catno = stated.Contains(nameof(TagSidecar.CatalogNumber)) ? Blank(said.CatalogNumber) : album.Catno;
        var barcode = stated.Contains(nameof(TagSidecar.Barcode)) ? Blank(said.Barcode) : album.Barcode;
        if (!string.Equals(catno, album.Catno, StringComparison.Ordinal)
            || !string.Equals(barcode, album.Barcode, StringComparison.Ordinal))
            _db.SetAlbumRelease(folder, catno, album.Released, barcode);

        if (stated.Contains(nameof(TagSidecar.MusicBrainzDiscId))
            && Blank(said.MusicBrainzDiscId) is var discId
            && !string.Equals(discId, album.DiscId, StringComparison.Ordinal))
            _db.SetAlbumDiscId(folder, discId);

        static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>The row, the favourite mark and the stamps, in that order, once the file is written.</summary>
    private PullOutcome Settle(CloudPull pull, LibraryTrack updated)
    {
        var row = pull.Row;

        // The row keeps the path it had, so this is an update in place and not a re-file. Move still
        // does the writing, because it is the one statement that puts a whole track row back.
        if (!_db.Move(row.RelativePath, updated))
            return PullOutcome.RowRefused;

        // The mark that the other machine made is written on its own. A move carries what the FILE says,
        // and a favourite is not something a file says: it belongs to the listener, and it must survive
        // every retag that goes through the same path. It is accepted and not set, so this machine does
        // not push the store's own value back at it.
        _db.AcceptTrackFavorite(updated.RelativePath, updated.IsFavorite, updated.FavoritedUtcTicks);

        _db.NoteCloudInStep(row.Id, pull.Cloud.UpdatedAt, DateTime.UtcNow.Ticks);

        return PullOutcome.Applied;
    }
}
