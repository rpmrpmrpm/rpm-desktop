using Microsoft.Data.Sqlite;
using RPM.Core.Art;
using RPM.Core.Cloud;
using RPM.Core.Models;

namespace RPM.Core.Library;

/// <summary>One track row in the library mirror: what the grid and search need, not the
/// full tag surface (details are read from the file itself when a dialog wants them).</summary>
public sealed record LibraryTrack
{
    public long Id { get; init; }

    /// <summary>Path relative to the library root, forward slashes.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Relative folder ("" = library root), forward slashes.</summary>
    public required string Folder { get; init; }

    public required string FileName { get; init; }

    /// <summary>Lower-case extension including the dot.</summary>
    public required string Extension { get; init; }

    public long SizeBytes { get; init; }

    /// <summary>File mtime (UTC ticks). With size, it is the cheap change detector for sync.</summary>
    public long ModifiedUtcTicks { get; init; }

    /// <summary>
    /// When this track joined the library (UTC ticks). Nothing on disk records it: an mtime is when
    /// the file was last written, and a retag rewrites every file on an album, so a wall ordered by
    /// mtime would show whatever was edited last as new.
    ///
    /// <para>Zero on a row built from a file (<see cref="FromTrackInfo"/> cannot know it); the insert
    /// then stamps the current time. Carried over when it is known, for example a track that comes
    /// back from the cloud with its original date. Never written by an update.</para>
    /// </summary>
    public long AddedUtcTicks { get; init; }

    /// <summary>How many times this track has been listened to, by <see cref="Playback.Listen"/>'s rule,
    /// counted by <see cref="LibraryDatabase.NoteListen"/>. Owned by the row, never by the file.</summary>
    public int Plays { get; init; }

    /// <summary>When it was last listened to (UTC ticks), or zero.</summary>
    public long LastPlayedUtcTicks { get; init; }

    /// <summary>The listener has marked this song a favourite. Owned by the row and never by the file,
    /// for the same reason as a play count: it is something a person did, not a property of the
    /// audio.</summary>
    public bool IsFavorite { get; init; }

    /// <summary>When it was made a favourite (UTC ticks), or zero. The favourites list is ordered by
    /// this, newest first.</summary>
    public long FavoritedUtcTicks { get; init; }

    /// <summary>
    /// The tempo the FILE claims, or zero where it claims none.
    ///
    /// <para>Read from the tag on every scan and stored as it arrived. It is not the app's own
    /// reading, which lives in track_analysis with a confidence value and is discarded whenever the
    /// analyser version changes. This value is what came with the music, stored where a query can
    /// reach it. A library tagged by Rekordbox, Mixed In Key, Serato or beets has it on nearly every
    /// file. A library ripped from CDs has it on none.</para>
    ///
    /// <para>Trust it only as far as the program that wrote it: taggers disagree about the octave as
    /// much as any detector does (see Analysis/TempoOctave), so a 174 and an 87 may be the same
    /// record.</para>
    /// </summary>
    public double TagBpm { get; init; }

    /// <summary>The key the file claims, in whatever form its tagger wrote it: "Am", "8A", "A minor".
    /// Null where the file says nothing. Kept as written, not parsed on the way in: the reader does
    /// the parsing, so a form that this app does not recognise now can be read later.</summary>
    public string? TagKey { get; init; }

    /// <summary>What kind of release the record is, as the file says, or null where it says nothing.
    /// See <see cref="ReleaseType"/>.</summary>
    public ReleaseType? ReleaseType { get; init; }

    /// <summary>The id stamped in the file's tags, which survives a move made outside the app.
    /// Null when the file has never been stamped (a row from before the id existed);
    /// <see cref="Metadata.RpmTag.NoCarrier"/> when it cannot hold one at all.</summary>
    public string? RpmId { get; init; }

    public double DurationSeconds { get; init; }
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Album { get; init; }
    public string? Genre { get; init; }

    /// <summary>Which sub-division of its genre this record belongs to: "Deep House" under House.
    /// Mirrors the file's own tag, like the genre above it. Null for the many records that have
    /// only a genre.</summary>
    public string? Subgenre { get; init; }

    /// <summary>
    /// Whether the file's own advisory flag marks the lyrics as explicit.
    ///
    /// <para>Null means "never read" (see the v24 schema note) and makes sync read the tags of the
    /// row once. <see cref="ContentAdvisory.Unknown"/> is the settled answer for a file that carries
    /// no flag, which is most of them. See <see cref="Metadata.AdvisoryTag"/>.</para>
    /// </summary>
    public ContentAdvisory? Advisory { get; init; }

    public uint TrackNumber { get; init; }
    public uint DiscNumber { get; init; }
    public uint DiscCount { get; init; }

    /// <summary>The disc's own title within a multi-disc set, when the release names it.</summary>
    public string? DiscSubtitle { get; init; }

    // The composition this track is part of. See the v25 schema note.

    /// <summary>The work: "Symphony No. 5 in C minor, Op. 67". Null for most of a library, which is
    /// music that is not part of anything larger.</summary>
    public string? Work { get; init; }

    /// <summary>This track's movement of it: "I. Allegro con brio".</summary>
    public string? MovementName { get; init; }

    /// <summary>
    /// The movement number (1-based). It is also the sentinel for the whole block: null means the
    /// file's tags have never been read for any of these fields, and sync reads them once.
    ///
    /// <para>A number is the settled answer, zero included: "asked, and this track is part of no
    /// work". The text columns cannot show that difference (empty and unknown look the same), so the
    /// number does it for all four columns. <see cref="Channels"/> does the same for the stream
    /// facts and <see cref="Advisory"/> for the explicit flag.</para>
    /// </summary>
    public int? MovementNumber { get; init; }

    /// <summary>Who wrote it. A classical shelf cannot be sorted or browsed without this credit,
    /// which is why it is a column and not a tag read per album.</summary>
    public string? Composer { get; init; }

    public uint Year { get; init; }

    // What the file IS, read from the stream. Null means never read; see the v12 schema note.
    public int? Channels { get; init; }
    public int? SampleRate { get; init; }
    public int? BitsPerSample { get; init; }
    public int? BitrateKbps { get; init; }

    /// <summary>The decoder's own name for the stream. It is the only value that tells Apple Lossless
    /// from AAC, which both live in a .m4a. See <see cref="Metadata.AudioFormat"/>.</summary>
    public string? CodecDescription { get; init; }

    // How loud it is. Null means never measured; see the v27 schema note.

    /// <summary>Programme loudness in LUFS (ITU-R BS.1770-4), from the file's own ReplayGain tag or
    /// measured here. Used to compute the gain that plays one record at the level of the last one.</summary>
    public double? LoudnessLufs { get; init; }

    /// <summary>The track's true peak as a linear amplitude, where 1.0 is full scale. A boost is
    /// limited by it, so that turning a quiet record up never clips.</summary>
    public double? TruePeak { get; init; }

    /// <summary>
    /// SHA-256 of the track's canonical (tag-stripped) audio. It is the track's key in the cloud
    /// store and makes two users' copies of the same encoding one object. Null until the track is
    /// first queued for upload; see <see cref="Cloud.CanonicalAudio"/>.
    /// </summary>
    public string? ContentHash { get; init; }

    public CloudState CloudState { get; init; } = CloudState.Local;

    /// <summary>The user's per-track choice, which the "selected music" sync mode acts on.</summary>
    public bool CloudSelected { get; init; }

    /// <summary>
    /// The user picked this track themselves, through an album or the track itself, and not because a
    /// synced playlist pulled it in. Taking that playlist out of the cloud leaves a pinned track
    /// alone, because the user did not ask for the album to be removed.
    /// </summary>
    public bool CloudPinned { get; init; }

    public long CanonicalSizeBytes { get; init; }
    public long CloudSyncedUtcTicks { get; init; }

    /// <summary>Why the last upload failed, for the UI. Null unless <see cref="CloudState"/> is Error.</summary>
    public string? CloudError { get; init; }

    /// <summary>
    /// Why this track is in the cloud but not complete there. Today that means a head clip that will
    /// not encode. Null when nothing is wrong.
    ///
    /// <para>A separate field from <see cref="CloudError"/> because the two mean opposite things for
    /// the music: a track with this one IS in the store and plays from it. It also takes the track
    /// off the rendition catch-up, so the reason is recorded once and the file is not encoded again
    /// at every launch.</para>
    /// </summary>
    public string? CloudPartialError { get; init; }

    /// <summary>How many times the upload has failed. The retry backoff grows with it.</summary>
    public int CloudAttempts { get; init; }

    /// <summary>True when the bytes are not on disk. The row plays by streaming.</summary>
    public bool IsOffloaded => CloudState == CloudState.CloudOnly;

    /// <summary>
    /// The row was read from the user's cloud catalogue and not from this disk: another install of
    /// the user's uploaded it, and this machine has never held the file.
    ///
    /// <para>For playback an offloaded row and an adopted row are the same: bytes in the cloud, no
    /// file here. The difference matters only when a row is created and removed. An adopted row is
    /// created and dropped by the cloud listing (<see cref="Cloud.CloudLibraryMirror"/>), so signing
    /// out removes all of them. An offloaded row is this library's own and stays.</para>
    /// </summary>
    public bool CloudAdopted { get; init; }

    /// <summary>
    /// The row came from another of the user's own computers over the local network, and the file is
    /// on that computer and not here.
    ///
    /// <para>Adopted for the same reason as a cloud row (one collection on every machine) and by the
    /// same rules, so a record that is already in this library is never shown twice. The difference
    /// is where the bytes are. A cloud row streams from anywhere. A peer row plays only while that
    /// computer is on the same network. When it is not, the row stays in the library and is shown as
    /// not playable.</para>
    /// </summary>
    public bool PeerAdopted { get; init; }

    /// <summary>Which peer holds the file; see <see cref="Sharing.PeerIdentity.DeviceId"/>. Null for
    /// every row of this machine's own.</summary>
    public string? PeerDeviceId { get; init; }

    /// <summary>
    /// When this machine last changed the row's own metadata (UTC ticks), or zero.
    ///
    /// <para>
    /// A track with a file needs no such column: the file's mtime is the answer, and sync compares it
    /// with <see cref="CloudSyncedUtcTicks"/>. A track that exists only in the cloud has no file to
    /// carry a date, so an edit made here could not be told from no edit: the next read of the
    /// catalogue would write the old values back over it.
    /// </para>
    ///
    /// <para>A value ahead of <see cref="CloudSyncedUtcTicks"/> means "edited here, not sent yet".
    /// The mirror leaves such a row alone and the catalogue pass sends it.</para>
    /// </summary>
    public long MetadataEditedUtcTicks { get; init; }

    /// <summary>
    /// The store's own stamp on this track's catalogue row, as this machine last accepted it (unix
    /// seconds; zero when never seen or never stamped).
    ///
    /// <para>
    /// It shows whose copy is newer. A catalogue row with a bigger stamp has been written since this
    /// library last agreed with it, by another install. That is a fact about the server's clock
    /// alone. Comparing it with any value from this machine's clock would import clock skew; see
    /// <see cref="Cloud.CloudSyncPlan"/>.
    /// </para>
    /// </summary>
    public long CloudUpdatedAt { get; init; }

    /// <summary>The row as a TrackInfo (absolute path under <paramref name="libraryRoot"/>). It feeds
    /// playback and the tree grid.</summary>
    public TrackInfo ToTrackInfo(string libraryRoot) => new()
    {
        FilePath = Path.Combine(libraryRoot, RelativePath.Replace('/', Path.DirectorySeparatorChar)),
        Extension = Extension,
        DurationSeconds = DurationSeconds,
        Title = Title,
        Artist = Artist,
        AlbumArtist = AlbumArtist,
        Album = Album,
        Genre = Genre,
        Subgenre = Subgenre,
        Advisory = Advisory ?? ContentAdvisory.Unknown,
        TrackNumber = TrackNumber,
        DiscNumber = DiscNumber,
        DiscCount = DiscCount,
        DiscSubtitle = DiscSubtitle,
        Work = Work,
        MovementName = MovementName,
        MovementNumber = (uint)Math.Max(MovementNumber ?? 0, 0),
        Composers = Composer,
        Year = Year,
        Channels = Channels,
        SampleRate = SampleRate,
        BitsPerSample = BitsPerSample,
        BitrateKbps = BitrateKbps,
        CodecDescription = CodecDescription,
        LoudnessLufs = LoudnessLufs,
        TruePeak = TruePeak,

        // Converted to the shape every tracklist in the app is built from, so what came from the file's
        // tags survives the round trip through the mirror. A row that is read back and written again
        // must carry the same tempo, or the first edit of any field would clear it.
        Bpm = (uint)Math.Max(0, Math.Round(TagBpm)),
        MusicalKey = TagKey,
        ReleaseType = ReleaseType,
        RpmId = RpmId,
        // What the cloud holds of the track. Every tracklist is built from TrackInfo, and this is the
        // one place a mirror row becomes one.
        CloudState = CloudState,
        CloudSelected = CloudSelected,
        // What is wrong with it in the cloud, if anything. The mark on the line is drawn from this.
        CloudPartialError = CloudPartialError,
        CloudAttempts = CloudAttempts,
        // Whether it is a favourite, so every line in every tracklist can show its heart without the
        // view having to ask the library.
        IsFavorite = IsFavorite,
        Source = MetadataSource.Tags,
    };
}

/// <summary>What sync needs to know about a mirror row without reading its file: whether the
/// file changed (size/mtime), which file it is (RPM id), whether it is supposed to have one at
/// all (cloud state: an offloaded track has none by design), and whether the stream's own facts
/// have ever been read (channels; null means never, see the v12 schema note).</summary>
public readonly record struct SnapshotRow(
    long Id,
    long SizeBytes,
    long ModifiedUtcTicks,
    string? RpmId,
    CloudState CloudState,
    int? Channels,
    int? Advisory,
    int? MovementNumber,
    bool PeerAdopted = false)
{
    /// <summary>
    /// The row's file is somewhere other than this disk, and is meant to be.
    ///
    /// <para>Offloaded to the cloud, or held by another of the user's computers. Sync asks this
    /// and not either question alone: both mean "missing on purpose", and a pass that knew about
    /// only one would treat the other's rows as tracks the user deleted.</para>
    /// </summary>
    public bool Elsewhere => CloudState == CloudState.CloudOnly || PeerAdopted;
}

/// <summary>
/// SQLite mirror of the central music folder, stored inside it (.rpm/library.db), or on this
/// computer when the folder is on a network volume (see <see cref="MirrorLocation"/>). The
/// library can hold tens of thousands of tracks, so search and sort run as indexed queries
/// instead of walking the file system or holding full tag state in memory. The mirror is
/// 1:1 with the folder: <see cref="LibrarySynchronizer"/> reconciles it on open, and every
/// import updates it together with the file move.
/// </summary>
public sealed partial class LibraryDatabase : IDisposable
{
    /// <summary>Starts with a dot so <see cref="Scanning.AudioFileScanner"/> never ingests it.</summary>
    public const string DataFolderName = ".rpm";
    public const string DataFileName = "library.db";

    /// <summary>The previous name of the data folder. A library root is the user's own folder and may
    /// be on a drive that the app has not seen for a long time, so the old folder is moved to the new
    /// name on open. Otherwise the mirror would be stranded, and with it the rips and vinyl
    /// recordings staged inside (see <see cref="Ripping.PendingRips"/>).</summary>
    private const string LegacyDataFolderName = ".grooves";

    /// <summary>
    /// Schema the code expects, held in the db's <c>PRAGMA user_version</c>. Bump it and add a
    /// step to <see cref="Migrate"/> whenever the shape changes. A mirror created by an older
    /// build is opened in place and never recreated (rebuilding it would lose row identity).
    /// v1 = original tracks table. v2 = + rpm_id. v3 = + the cloud columns.
    /// v4 = + the artist backdrop order (the art chooser's saved, user-editable ordering).
    /// v5 = + disc_count / disc_subtitle (multi-disc: "disc N of M" and the disc's own title).
    /// v6 = + artist_art_pref.rank_version (the chooser's algorithm stamp, so a change re-ranks cached orders).
    /// v7 = + album_meta (per-album favourite / rating / review / discogs release id, the local seed of
    ///      the album social schema; keyed by album folder, since an album is a folder here).
    /// v8 = + pl_node / pl_item (the user's playlist tree: folders + playlists + song/nested-playlist
    ///      items, with a pinned "Now playing" node the player always plays).
    /// v9 = + track_analysis (the DJ analyser's findings per track: tempo, beat grid, key, energy and
    ///      cue points, stamped with the file's size/mtime so a changed file is analysed again).
    /// v10 = + track_analysis.downbeat_confidence (how sure the bar line is; a changeover falls back
    ///      to aligning on beats when it is not).
    /// v11 = + the cloud's half of the playlist tree (pl_node.cloud_id / cloud_selected /
    ///      cloud_synced_utc_ticks) and tracks.cloud_pinned, which tells a track the user picked
    ///      themselves from one that a synced playlist brought along.
    /// v12 = + what the file IS rather than what it says: channels, sample rate, bit depth, bitrate
    ///      and the decoder's own name for the stream. Nullable ON PURPOSE: null means "never read",
    ///      which makes an existing mirror fill itself on its next sync, as a null rpm_id does.
    ///      See Metadata/AudioFormat.
    /// v13 = + cloud_activity (the cloud's log: one row per finished upload / removal / restore,
    ///      pruned to a rolling month; see Cloud/CloudActivity).
    /// v14 = + added_utc_ticks: when a track entered the library, which nothing on disk records. A
    ///      file's mtime is when it was last WRITTEN, and retagging an album rewrites every file on
    ///      it, so ordering a wall by mtime would put whatever was edited most recently at the front
    ///      and show it as new. Written on INSERT only (see UpsertLocked). An existing mirror is
    ///      filled from mtime once, the best estimate for tracks that exist before the column.
    /// v15 = + plays / last_played_utc_ticks: what has actually been listened to. Not on disk, and
    ///      deliberately not a tag: a play is something the listener did, not a property of the
    ///      file. Written by NoteListen alone and, like added_utc_ticks, never changed by an upsert.
    /// v16 = + renditions_utc_ticks: when this machine last made the cloud's head clip and free rate
    ///      for the track. It is the one piece of cloud work that leaves no other trace, so without
    ///      it the work could never be known to be finished; see NoteRenditions.
    /// v17 = + album_meta.catno / released: the catalogue number and release date from the linked
    ///      Discogs release. Tags carry a year at best, and the catalogue number is what RPM's shared
    ///      album entry uses to match a pressing. They are kept so the lookup is not repeated on
    ///      every visit to the album.
    /// v18 = + track_fingerprint (each track's Chromaprint fingerprint, computed quietly in the
    ///      background and offered to the recognition service; see Services.FingerprintService in
    ///      the app. Stamped with the file's size/mtime like track_analysis, so a changed file is
    ///      fingerprinted again).
    /// v19 = no new shape: the stream facts of every track that has no length are cleared back to
    ///      null ("never read"), so the next sync reads those files again, this time with ffmpeg
    ///      behind TagLib, which gives a length to the containers that TagLib cannot read.
    /// v22 = + cloud_updated_at: the store's own stamp on this row as this machine last accepted it.
    ///      It is compared with the stamp that the catalogue now carries (server clock against server
    ///      clock). This tells an install whether the cloud has changed since it last looked, or
    ///      whether it has edited the row and the cloud has not. No code here compares one machine's
    ///      clock with another's.
    /// v21 = + metadata_edited_utc_ticks: when this machine last changed the row's own metadata. For a
    ///      track whose file is on this disk the file's mtime says that. A track that exists only in
    ///      the cloud has no file to carry a date, and without this column an edit made here would be
    ///      overwritten by the next catalogue read before it was sent. Zero on every row at upgrade,
    ///      which reads as "never edited here".
    /// v20 = + cloud_adopted: the row came from the user's cloud catalogue and not from this disk.
    ///      A second install has none of the first one's files, so without this column its library
    ///      would be only what is on its own drive and the cloud would be invisible to it. Zero on
    ///      every existing row, which is correct for a library that has never adopted anything.
    /// v23 = the RPM id column is renamed in place (tracks.rpm_id and pl_item.rpm_id, from the old
    ///      name grooves_id). It is renamed and not added with a backfill because the values ARE
    ///      the ids stamped in the user's files, and a new column would give every track a new
    ///      identity and upload the library again.
    /// v24 = + advisory: whether the file's own flag marks the lyrics as explicit, so the E badge
    ///      comes from a column and not from a tag read per row on every scroll. Nullable ON PURPOSE,
    ///      as in v12 for the stream facts: null means "never looked", sync reads any row that says
    ///      so, then writes a number (Unknown included) and the row is settled.
    ///      A changed-file check cannot do this. A file whose flag was already correct is never
    ///      rewritten, so its size and mtime still match the mirror. It would not be read and would
    ///      never show a badge, while every file that merely changed would.
    /// v28 = + track_analysis.tags_written_utc_ticks: when this machine last wrote the measured tempo
    ///      and key into the file's own tags. Zero on every row at upgrade, which reads as "never
    ///      written", so the first pass offers the whole library once and after that only the rows
    ///      it has not done. Without it the only way to know is to open every file and read its tags.
    ///      That is the expensive half of the job and would be paid on every launch.
    ///      See Analysis/AnalysisTags.
    /// v29 = + track_analysis.sample_hash: what the track SOUNDS like, as opposed to what the file on
    ///      this disk happens to be. Every other part of the row's stamp belongs to one file on one
    ///      machine, so a retag discards a beat grid and a second install analyses again a library
    ///      for which it already has every answer. Null on every existing row ("never hashed"), set
    ///      when the row is next analysed. See Analysis/AudioKey.
    /// v31 = + track_analysis.audio_start/audio_end/beat_first/beat_last: where the record actually
    ///      begins and ends, and which part of the beat grid was measured and not extended at the
    ///      known tempo. Zero on every existing row, which reads as "never measured"; every use of
    ///      these values falls back to the default behaviour when they are zero. See Analysis/AudioEdges.
    /// v30 = + library_meta: a key/value note that the app keeps about this library. The first one
    ///      records whether the one-off pass that publishes which names stand for one record has run.
    ///      These are not settings: settings belong to the person and follow them to their next
    ///      library, while this belongs to the collection and stays with it.
    /// v33 = + album_meta.favorite_edited_utc_ticks / favorite_synced_utc_ticks /
    ///      favorite_cloud_updated_at: the three stamps that let a record's own favourite travel
    ///      between a user's machines. They work as the stamps on the track rows do: each side is
    ///      compared only with itself, so no two computers' clocks are ever compared.
    ///      Zero on every row at upgrade, meaning a mark that nobody has sent yet.
    /// v35 = + track_analysis.bass_curve/mid_curve/top_curve/vocal_curve and the four figures taken
    ///      from them (phrase length and phase, tempo stability, the drops, and the level at each
    ///      cue). A changeover needs the bottom end on its own to swap the bass, and needs the
    ///      phrases to land where the record changes. Empty and zero on every existing row, which
    ///      reads as "never measured". The analyser version is raised with this step, so a row
    ///      without these values is read again and not left half-filled.
    /// v32 = + `favorite` / favorited_utc_ticks: the songs the listener has loved. Stored beside plays
    ///      and not in the tags, for the same reason: a favourite is something a person did, and
    ///      writing it into the file would put one listener's taste into every copy of that file.
    ///      Zero on every existing row (nothing is a favourite yet) and, like plays, never changed
    ///      by an upsert, so a retag cannot clear it. The date orders the generated favourites list.
    /// v34 = no new shape: every album is written as not classical. A record is classical only when
    ///      somebody marks it in the album editor, and otherwise it is an ordinary record. The value
    ///      is written to every row, and the reader does not work it out, so the mirror states
    ///      plainly what the app believes.
    /// v36 = music that is on another of the user's own computers. A row for it here, marked with the
    ///      computer that holds it, so one collection reads the same on every machine the user signs
    ///      into. This is the same approach as the cloud's adopted rows, for files that were never
    ///      uploaded.
    /// v37 = + album_meta.release_provider / release_id / barcode / rpm_album_id: the pressing an
    ///      album is linked to, whichever catalogue named it, and RPM's own id for that pressing.
    ///      discogs_release_id can hold the answer of one catalogue only, so a library where the
    ///      user prefers MusicBrainz, or rips vinyl, could not be matched to the shared album entry.
    ///      The old column is copied into the new pair and then left alone: nothing reads it after
    ///      this step.
    /// v39 = + album_meta.release_group_id: the album above its pressings, as MusicBrainz names it.
    ///      A pressing says which record somebody owns and splits a popular album across every cut of
    ///      it. The group joins a 1977 UK pressing and a 2013 remaster on one page. Read from the
    ///      tags, which carry it for every library that Picard has processed.
    /// v38 = + album_meta.disc_id: the MusicBrainz disc ID of the CD an album was ripped from. The
    ///      ripper computes one when it can read a table of contents and writes it to the staging
    ///      folder's rip.json. That file does not follow the music into the library, so this column
    ///      carries the identifier to the shared album entry.
    /// v40 = + tracks.bpm_octave: which way the listener counts this record, when they have said so.
    ///      Whether a tempo is 70 or 140 is a matter of counting and not of speed, and nothing in the
    ///      audio settles it, so the app asks and does not rewrite the number; see
    ///      Analysis/TempoOctave. Kept beside the favourite and not in track_analysis, for the same
    ///      reason: it is the person's answer, and track_analysis is a cache that is discarded
    ///      whole whenever the analyser version changes. 1 on every existing row (no answer given).
    /// v41 = + tracks.cloud_partial_error: why a track that IS in the cloud is not complete there.
    ///      The head clip is a transcode on this machine, and a file that it cannot encode (for
    ///      example thirty channels mixed down to stereo) fails silently and is picked up again by
    ///      every later run. Kept apart from cloud_error because the two are different facts:
    ///      cloud_error means the music is not up there, and this means it is, without its clip.
    ///      Null on every existing row.
    /// v43 = + the listen table: what was played, when, and whether it counted. tracks.plays says
    ///      how often but never when, so lists with a time window ("on repeat", "late night", "you
    ///      played this every day last spring") cannot be answered from it, and a counter cannot
    ///      answer them later. Skips are written too, because a listen that ends after nine seconds
    ///      is the only evidence the app gets that somebody dislikes a track. Empty on every existing
    ///      mirror: the totals on the track rows stay as they are and keep answering the same
    ///      questions, and the windowed lists stay hidden until the log has content.
    /// v44 = + artist_art_frame: where a person has placed an artist's photograph in the header by
    ///      hand. It is a table of its own and not columns on artist_art, on purpose: the saved
    ///      ORDER is rewritten whole on every choice, re-rank and reconcile (SaveArtistArt deletes
    ///      the rows and writes them again), so a framing stored in it would have to survive half a
    ///      dozen paths that build ArtistBackdrop records from scratch, and the first path that
    ///      forgot would lose it. Nothing here touches the order: a photograph either has a framing
    ///      filed against it or it does not, and every picture without one is framed by the
    ///      automatic placement. Empty on every existing mirror.
    /// v45 = + artist_art_frame.cover: how much of the header's picture area a hand-placed photograph
    ///      covers. Three numbers can give where the window sits and how close in, but not that the
    ///      picture stops short of the edge, which the automatic placement does whenever centring a
    ///      face costs it width. Without this value a hand-made framing could not reproduce the
    ///      chooser's own answer, so the picture would move when the placement window opens and every
    ///      adjustment would be made against something other than what is on screen. Existing rows
    ///      take 1 (edge to edge), which is what a framing made before this column meant.
    /// v46 = + pl_node.cover: how a playlist's picture was chosen, so that the picker can reopen on it
    ///      and a chosen-records cover can tell when it is out of date. Purely additive; null is the
    ///      automatic cover, which every existing playlist has.
    /// v47 = + pl_cloud_gone: the cloud playlists this library has let go of. Playlists travel in BOTH
    ///      directions (a phone can make one, and a sync run takes in any cloud playlist that no node
    ///      here claims; see CloudPlaylistIntake). Without this table, deleting a synced playlist
    ///      would delete the row, and the next run would find the same playlist again and add it
    ///      back. A note that the user let it go is the only fact that survives the row it describes,
    ///      and it must also survive being offline, because the cloud copy may not be removed yet, or
    ///      at all. Empty on every existing mirror, which says nothing was let go.
    /// v49 = + pl_node.cloud_sent: the track ids this machine last PUBLISHED for a playlist, in order.
    ///      This is the common ancestor that a three-way merge is measured against (see
    ///      Cloud/PlaylistMerge). Without it, a song present here and absent in the cloud cannot be
    ///      told from a song added here and not yet sent, and either reading can let one head's edit
    ///      silently undo the other's. Null on every existing row, which means "not known" and NOT
    ///      "was empty": the merge falls back to a union there, which loses nobody's work. The first
    ///      push writes the real value.
    ///
    /// v50 = + album_meta.edition: what the linked release IS, in the catalogue's own words: "Vinyl,
    ///      LP, Limited Edition, Red Translucent". Stored beside catno and barcode because it is the
    ///      same kind of fact and comes from the same lookup: which pressing of this album the
    ///      library holds. Null on every existing row until the next Discogs read fills it in.
    ///
    /// v51 = + album_meta.popular_track: which song on the record the world plays most, as the title
    ///      that ListenBrainz and MusicBrainz together name. It is a cached ANSWER and not a fact
    ///      about the record: the lookup costs three calls to two rate-limited services, and the star
    ///      it draws has to be on screen when the page opens. Empty means asked and no song stood
    ///      out; null means never asked.
    ///
    /// v52 = + album_meta.popular_asked_utc_ticks: when the star above was last asked for. Zero on
    ///      every existing row, which reads as "long ago" and sends the first page that opens back to
    ///      the services once. What a record is best known for changes slowly: the page asks again a
    ///      week after the last answer and shows the old one until the new one arrives.
    ///
    /// v53 = all stars asked for before this step are cleared. The stored answers had two faults:
    ///      they were found through the pressing's own recording ids and not the ids that listening
    ///      is counted under (which gave 43 plays for "Sultans of Swing"), and a service that could
    ///      not be reached was stored as "this record has no favourite". Both faults are fixed in the
    ///      code. The old answers are not worth keeping, and asking again costs one lookup per
    ///      record the next time its page is opened.
    /// v54 = + tracks.tag_bpm and tracks.tag_key: the tempo and the key the FILE carries, which every
    ///      scan reads. TagLib gives them to <c>TrackInfo.Bpm</c> and <c>TrackInfo.MusicalKey</c>,
    ///      and the track row needs columns to keep them. A library that has been through Rekordbox,
    ///      Mixed In Key, Serato or beets carries both. Zero and null on every existing row; the next
    ///      scan of each file fills them in, and no extra scan is added to do it.
    /// v55 = + tracks.mood_brightness, mood_onsets, mood_dynamics: what a track sounds like on the
    ///      arousal axis, taken from the decode that the loudness pass already makes. These are the
    ///      RAW measurements and not a mood: the recipe that turns three numbers into a band is
    ///      arithmetic over a whole shelf and will change, and a stored band would be a stale answer.
    ///      Null on every existing row, filled by the same unhurried pass that levels the collection
    ///      (see Analysis/MoodMeter and Analysis/LoudnessScanner.Scan).
    /// v56 = + tracks.subgenre: which sub-division of its genre a record belongs to: "Deep House"
    ///      under House. Mirrors the file's own tag, as the genre beside it does. Null on every
    ///      existing row and NOT filled by a separate pass, because unlike a mood there is nothing to
    ///      work out: the answer is already in the file, and each track picks it up the next time
    ///      its tags are read.
    /// v57 = + tracks.liked_utc_ticks, disliked_utc_ticks: whether the listener said a song FITS the
    ///      kind of list the library builds. Zero in both means nothing was said, which is what every
    ///      existing row means and what almost every row stays. Stored beside the heart and never
    ///      merged with it: `favorite` is a statement about the song, and this is a statement about the
    ///      PICK, and a record can be a favourite and a bad fit for a calm list at once. The two
    ///      stamps are never both set, which TrackVerdict and SetTrackVerdict ensure.
    /// v58 = + tracks.world_plays: how much of the world plays each song, as ListenBrainz counts it.
    ///      NULLABLE, and that is the point: zero means the world was asked and does not play this
    ///      song, null means nobody has asked yet. If they were the same, a library that has fetched
    ///      nothing would report that none of it was ever popular, and the sweep could not tell what
    ///      it had already done. PopularTrackAnswer makes the same distinction between "no favourite"
    ///      and "not asked". Filled by an unhurried pass, like the loudness and the mood.
    /// v59 = + album_meta.styles: the narrow labels that Discogs files a release under: "Deep House",
    ///      "Boom Bap", "Northern Soul". They are what <see cref="MoodFlavour"/> needs: a CONTROLLED
    ///      vocabulary of sub-genres, where the canonical genre is one broad word and a typed-in tag
    ///      can be anything. Separated by semicolons, because a style may contain a comma. Null means
    ///      nobody has asked; empty means Discogs files the record under a genre and no style, which
    ///      is an answer and stops it being asked again.
    /// v64 = + tracks.lyric_valence: how positive or negative a song's WORDS are, where they could
    ///      be read and trusted. See LyricValence for the two gates and why they exist. It adds to
    ///      the measurements and never decides: a song without one is ranked by brightness, onsets
    ///      and dynamics, because the audio measurements carry no information about the lyric
    ///      reading (r = +0.06 on brightness, +0.01 on onsets, -0.05 on dynamics, over 1,184 songs).
    ///      There is no audio fallback for valence, so none is pretended.
    /// v63 = + recommend_refusal: the same statement as a dislike, made about an ACT or a RECORD
    ///      and not about a song: "don't recommend artist" and "don't recommend album" from a
    ///      generated list, kept for the pool that the list is drawn from. Keyed by the scope, the
    ///      subject and the kind of list. The kind is part of the key for the reason given at v62:
    ///      an act that nobody wants at a party may be exactly who they want to fall asleep to. Not
    ///      merged into track_verdict: that table is keyed by a path, and these refusals must outlive
    ///      the songs they were made about. A record bought again, or the second disc of an album
    ///      imported later, is refused without the listener saying so twice.
    /// v66 = + pl_node.cloud_adopted: a playlist that came DOWN from the account and was never this
    ///      machine's. It is the same fact that tracks.cloud_adopted holds, for the same reason:
    ///      signing out drops the rows that the cloud lent this disk, and without a way to tell them
    ///      from the listener's own, sign-out could only keep all of them or remove all of them.
    ///      Zero on every existing row, which reads as "this machine's own". That is the safe
    ///      answer: a wrong guess this way leaves a playlist that outlasts its account, and a wrong
    ///      guess the other way deletes one.
    /// v67 = + track_plays: how often each device has played each track, and how much of that this
    ///      machine has sent. tracks.plays is the MERGED total from here on: this machine's own
    ///      plays plus what the listener's phone and other computers report, so every existing read
    ///      of it counts that listening without being changed. Seeded from the plays already on the
    ///      tracks, which at the moment of the migration are all this machine's.
    /// (no version) tracks.release_type: what kind of release the record is, as the file's RELEASETYPE tag says.
    ///      Added on every open without a version step, so older builds that share the file keep
    ///      opening it; see Migrate.
    /// v68 = + album_meta.classical_edited_utc_ticks / classical_synced_utc_ticks /
    ///      classical_cloud_updated_at: the classical mark travels to the cloud on the same three
    ///      stamps as the album favourite (v33), so the phone can draw a classical record as works and
    ///      movements. Zero on every existing row. Marks made before this version are still sent: the
    ///      sync sends any record that is classical here and has no answer in the store.
    /// v62 = + track_verdict: a listener's verdict is about a LIST and not about a song, so it is
    ///      keyed by the track AND the kind of list it was made in. Two stamps on the track row can
    ///      only say "everywhere": turning a song down in Chill would take it out of Dancefloor,
    ///      Focus, Recently added and every decade as well. A dislike means only that the song is
    ///      not a good fit for this list, and it CAN be a fit for the other lists. The old stamps
    ///      are copied in as a row per kind, because everywhere is what was meant when they were
    ///      pressed, and are then set to zero: two columns that no longer decide anything must not
    ///      keep an answer that somebody could read and trust.
    ///
    ///      <para>The columns THEMSELVES stay. Dropping them is a shape change, and a shape change
    ///      can break a database that another line of work has already shipped. Work lands on this
    ///      file from more than one line.</para>
    /// v61 = + release_meta: what a catalogue says a RELEASE is, keyed by the catalogue and its id
    ///      and not by the album folder. The styles of v59 are keyed to a path in album_meta, which
    ///      leaves a stale answer behind when a release link is corrected, asks twice when two
    ///      folders hold the same record, and makes a re-map after a change of the style list walk
    ///      every folder that points at a release and not the release itself. It holds the provider's
    ///      own words AND RPM's mapping of them, with the version of the list that did the mapping.
    ///      MoodMeter follows the same rule by storing three numbers and no range: when the recipe
    ///      changes, re-map from the column and do not ask the catalogue again.
    /// v60 = the columns of v56 and v57 again, for the databases that took one line's numbering and
    ///      not the other's. NOT a shape change: a repair of the same kind that v57 was on the
    ///      branch it came from.
    ///
    /// <para><b>A step that has run is finished, and editing it changes nothing.</b> Two lines of work
    /// numbered a step 56 at the same time (the style beside a track's genre on one, the listener's
    /// verdict on the other), and each shipped a database that says 56 and holds only its own columns.
    /// Renumbering the later one is not enough: a database already at 57 skips every step below it,
    /// and would not get the column that the other line added. So the ambiguous columns are added
    /// once more at the top, where no database has been yet. A migration is a record of what HAS
    /// happened, so anything added after the fact needs a version of its own, however small.</para>
    /// </summary>
    private const int SchemaVersion = 68;

    /// <summary>The connection that every change to a row goes through, and the only one that ever
    /// opens a transaction. Held under <see cref="_gate"/>.</summary>
    private readonly SqliteConnection _connection;

    /// <summary>
    /// The connection that every question goes through, held under <see cref="_readGate"/>.
    ///
    /// <para>It is separate from the writing connection, and that is its purpose. SQLite in WAL mode
    /// lets a reader read while a writer writes, but only a reader on ITS OWN connection: one
    /// connection is one place in the file, and asking it a question during a commit means waiting
    /// for the commit. With a single connection, the library's queries would queue behind the sync's
    /// batch, and a query that costs a millisecond would cost most of two seconds while forty
    /// thousand rows go in. The user would see a wall of blank cards.</para>
    ///
    /// <para>Reads still take a lock against each other, because a connection is one conversation
    /// and cannot hold two. What they do not take is the writer's lock.</para>
    /// </summary>
    private readonly SqliteConnection _read;

    /// <summary>Serialises the writing connection. It is a <see cref="Gate"/> and not a bare object, so
    /// that a hold long enough to be felt can say whose it was. See that class for what a bare
    /// object cannot answer.</summary>
    private readonly Gate _gate = new("write");

    /// <summary>Serialises the reading connection. Never taken while <see cref="_gate"/> is held, and
    /// never the other way round: nothing here needs both, and taking both is how two locks become a
    /// deadlock.</summary>
    private readonly Gate _readGate = new("read");

    /// <summary>
    /// What the reading connection is doing right now, and the same for the writing one.
    ///
    /// <para>It is asked from OUTSIDE, by a watchdog on its own thread, because that is the only way
    /// to answer usefully. A reader holds the gate for as long as its query runs and every other
    /// reader waits. A query worth a millisecond can wait most of two seconds behind a sync's batch.
    /// A UI thread that waits behind one has no managed frames of its own to show where the time
    /// went, so something that is not stuck has to look.</para>
    ///
    /// <para>The value carries the name of whoever is inside the gate and how long they have been
    /// there, not only a yes or no.</para>
    /// </summary>
    public Gate.Look ReadGate => _readGate.Now;

    /// <inheritdoc cref="ReadGate"/>
    public Gate.Look WriteGate => _gate.Now;

    /// <summary>
    /// How many writing holds this collection has finished. Never goes down, costs a field read, and
    /// takes no lock.
    ///
    /// <para><b>What it answers.</b> "Could anything in this database have changed since the number
    /// I am holding?" A different number means yes or maybe; the same number means no. It covers the
    /// whole database and not one table, because the writing gate does not know which rows a hold
    /// touched. So a caller that watches the tracks is also woken by a listen being recorded. That is
    /// the price of a signal that no write site can fail to send: see <see cref="Gate.Turns"/>, which
    /// explains why the one door is the only place where this can be counted correctly.</para>
    ///
    /// <para><b>Read it BEFORE the work it guards, never after.</b> A write that lands while a
    /// reader is mid-read has already changed the number by the time the reader finishes, so a number
    /// taken afterwards would say "current" about an answer built across the change. Taken first, the
    /// worst case is that the reader repeats work that was not needed.</para>
    ///
    /// <para>It counts only writes to THIS database. Two things change what a caller reads without
    /// touching a row: <see cref="CloudRowsOnly"/> and <see cref="LocalRowsOnly"/>. They narrow every
    /// query and are plain properties, so a caller that caches against this number has to track them
    /// itself.</para>
    /// </summary>
    public long WriteTurns => _gate.Turns;

    public string Root { get; }

    /// <summary>The database file, in the music folder or on this computer.</summary>
    public string DatabasePath { get; }

    /// <summary>True when the folder already is an RPM library: it has a mirror db under either
    /// name (a library that only an older build of the app has opened is still a library),
    /// or the id file of a network folder whose database is kept on each computer.</summary>
    public static bool ExistsUnder(string libraryRoot) =>
        File.Exists(Path.Combine(libraryRoot, DataFolderName, DataFileName)) ||
        File.Exists(Path.Combine(libraryRoot, DataFolderName, MirrorLocation.IdFileName)) ||
        File.Exists(Path.Combine(libraryRoot, LegacyDataFolderName, DataFileName));

    /// <summary>
    /// Moves a <c>.grooves</c> folder (the previous data folder name) to <c>.rpm</c>, once, on open.
    /// It does this only when the new name does not exist at all. If both exist, the old one is a
    /// leftover from an older build and the current mirror is the one to keep.
    /// </summary>
    internal static void CarryLegacyFolderOver(string libraryRoot)
    {
        var legacy = Path.Combine(libraryRoot, LegacyDataFolderName);
        var current = Path.Combine(libraryRoot, DataFolderName);
        if (!Directory.Exists(legacy) || Directory.Exists(current)) return;
        try
        {
            Directory.Move(legacy, current);
        }
        catch (IOException)
        {
            // A locked file (another install of the app, a syncing cloud drive) leaves the old
            // folder where it is. The library then opens on a fresh mirror and syncs again from the
            // files, as happens to a library that is copied to a new machine.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Opens (creating if needed) the mirror at <paramref name="location"/>.</summary>
    public LibraryDatabase(MirrorLocation location)
    {
        Root = location.Root;
        DatabasePath = location.DatabasePath;
        var path = DatabasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Before the first connection, because a connection would create an empty file at the path.
        if (location.CarryFrom is { } from && !File.Exists(path))
            Carry(from, path);

        // A MirrorConnection, so a command that SQLite refuses is reported (Refused) before it leaves.
        _connection = new MirrorConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString(), ReportRefusal);
        _connection.Open();
        Execute("PRAGMA journal_mode=WAL");
        try
        {
            Upgrade.Run(this);
        }
        catch
        {
            // A constructor that throws is never disposed, so the connection is closed here.
            _connection.Dispose();
            throw;
        }

        // After the migration, not before: the reader opens a file that is already at this
        // version, so it never sees a table that is half a step behind. Opened ReadWriteCreate like
        // the writer, because a read-only SQLite connection cannot bring up the WAL shared-memory
        // index on its own. It is then told to refuse writes, so "reads only" is a property of the
        // connection and not a promise by the callers.
        _read = new MirrorConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString(), ReportRefusal);
        _read.Open();
        using (var cmd = _read.CreateCommand())
        {
            // A reader that arrives during a commit waits for it and does not fail the query. In WAL
            // mode this is a rare, brief overlap (the writer's own commit).
            cmd.CommandText = "PRAGMA busy_timeout=5000; PRAGMA query_only=1";
            cmd.ExecuteNonQuery();
        }

        // Last, because it reads a note through the connection opened just above and writes it back.
        // Starting the app is one of the two things that give the generated lists different songs.
        NoteLaunchForGeneratedLists();
    }

    /// <summary>
    /// Brings the file up to <see cref="SchemaVersion"/>. Creating the table at the current
    /// shape covers a fresh mirror; the per-version steps patch an older one in place. Each
    /// step is idempotent, so a database from a build that shipped mid-version still ends up right.
    ///
    /// <para>Runs inside the one transaction that <see cref="Upgrade.Run"/> opens, after it has
    /// copied the file. The parameter makes that the only way in. A step must not begin a
    /// transaction of its own or run anything that SQLite refuses inside one (VACUUM, a journal_mode
    /// change).</para>
    /// </summary>
    private void Migrate(Upgrade inside)
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS tracks (
                id INTEGER PRIMARY KEY,
                relative_path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                folder TEXT NOT NULL COLLATE NOCASE,
                file_name TEXT NOT NULL COLLATE NOCASE,
                extension TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                modified_utc_ticks INTEGER NOT NULL,
                duration_seconds REAL NOT NULL DEFAULT 0,
                title TEXT COLLATE NOCASE,
                artist TEXT COLLATE NOCASE,
                album_artist TEXT COLLATE NOCASE,
                album TEXT COLLATE NOCASE,
                genre TEXT COLLATE NOCASE,
                subgenre TEXT COLLATE NOCASE,
                advisory INTEGER,
                track_number INTEGER NOT NULL DEFAULT 0,
                disc_number INTEGER NOT NULL DEFAULT 0,
                disc_count INTEGER NOT NULL DEFAULT 0,
                disc_subtitle TEXT,
                work TEXT COLLATE NOCASE,
                movement_name TEXT COLLATE NOCASE,
                movement_no INTEGER,
                composer TEXT COLLATE NOCASE,
                year INTEGER NOT NULL DEFAULT 0,
                channels INTEGER,
                sample_rate INTEGER,
                bits_per_sample INTEGER,
                bitrate_kbps INTEGER,
                codec_description TEXT,
                loudness_lufs REAL,
                true_peak REAL,
                tag_bpm REAL NOT NULL DEFAULT 0,
                tag_key TEXT,
                release_type TEXT,
                mood_brightness REAL,
                mood_onsets REAL,
                mood_dynamics REAL,
                rpm_id TEXT,
                content_hash TEXT,
                cloud_state INTEGER NOT NULL DEFAULT 0,
                cloud_selected INTEGER NOT NULL DEFAULT 0,
                cloud_pinned INTEGER NOT NULL DEFAULT 0,
                canonical_size_bytes INTEGER NOT NULL DEFAULT 0,
                cloud_synced_utc_ticks INTEGER NOT NULL DEFAULT 0,
                cloud_error TEXT,
                cloud_partial_error TEXT,
                cloud_attempts INTEGER NOT NULL DEFAULT 0,
                cloud_next_attempt_utc_ticks INTEGER NOT NULL DEFAULT 0,
                added_utc_ticks INTEGER NOT NULL DEFAULT 0,
                plays INTEGER NOT NULL DEFAULT 0,
                last_played_utc_ticks INTEGER NOT NULL DEFAULT 0,
                favorite INTEGER NOT NULL DEFAULT 0,
                favorited_utc_ticks INTEGER NOT NULL DEFAULT 0,
                liked_utc_ticks INTEGER NOT NULL DEFAULT 0,
                disliked_utc_ticks INTEGER NOT NULL DEFAULT 0,
                world_plays INTEGER,
                lyric_valence REAL,
                renditions_utc_ticks INTEGER NOT NULL DEFAULT 0,
                cloud_adopted INTEGER NOT NULL DEFAULT 0,
                metadata_edited_utc_ticks INTEGER NOT NULL DEFAULT 0,
                cloud_updated_at INTEGER NOT NULL DEFAULT 0,
                peer_device_id TEXT,
                peer_adopted INTEGER NOT NULL DEFAULT 0,
                bpm_octave REAL NOT NULL DEFAULT 1
            )
            """);

        long version = Convert.ToInt64(Scalar("PRAGMA user_version"));

        // Written by a newer build. This build does not know that build's steps, and finishing with
        // `user_version = SchemaVersion` below would mark the file older than it is, so the newer
        // build would run its steps again on the next open, and some of them rewrite data (v53
        // clears the stars). A mirror in the music folder goes wherever the folder goes, so two
        // machines on different versions can open the same one. Refused before anything is written.
        if (version > SchemaVersion)
            throw new LibraryTooNewException(DatabasePath, version, SchemaVersion);

        // v23: grooves_id becomes rpm_id, before anything below reads the column under its new name.
        // The index goes with it; the one at the end of this method creates the replacement.
        if (version < 23)
        {
            RenameColumnIfPresent("tracks", "grooves_id", "rpm_id");
            RenameColumnIfPresent("pl_item", "grooves_id", "rpm_id");
            Execute("DROP INDEX IF EXISTS idx_tracks_grooves_id");
        }

        // v2: the RPM id. A mirror written by a v1 build lacks the column entirely, so ReadTrack
        // would throw on it, and every row's id is NULL, which makes sync treat the rows as pending
        // and write the stamp from the file.
        if (version < 2)
            AddColumnIfMissing("tracks", "rpm_id", "TEXT");

        // v3: the cloud store. Every existing row defaults to Local (0). An upgrade puts
        // nothing in the cloud on its own; the user opts in.
        if (version < 3)
        {
            AddColumnIfMissing("tracks", "content_hash", "TEXT");
            AddColumnIfMissing("tracks", "cloud_state", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing("tracks", "cloud_selected", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing("tracks", "canonical_size_bytes", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing("tracks", "cloud_synced_utc_ticks", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing("tracks", "cloud_error", "TEXT");
            AddColumnIfMissing("tracks", "cloud_attempts", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing("tracks", "cloud_next_attempt_utc_ticks", "INTEGER NOT NULL DEFAULT 0");
        }

        // v4: the art chooser's saved order: which of an artist's backdrops shows first, and in
        // what order the carousel steps through the rest. One row per (artist, photo). A companion
        // pref row records whether the user arranged it by hand (so the automatic re-ranker leaves it
        // alone). These are new tables, so IF NOT EXISTS covers a fresh mirror and an upgrade alike,
        // with no ALTER. An older mirror gains them empty.
        Execute(
            """
            CREATE TABLE IF NOT EXISTS artist_art (
                artist_key TEXT NOT NULL COLLATE NOCASE,
                url TEXT NOT NULL,
                position INTEGER NOT NULL,
                score REAL NOT NULL DEFAULT 0,
                hidden INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (artist_key, url)
            )
            """);
        Execute(
            """
            CREATE TABLE IF NOT EXISTS artist_art_pref (
                artist_key TEXT PRIMARY KEY COLLATE NOCASE,
                curated INTEGER NOT NULL DEFAULT 0,
                updated_utc_ticks INTEGER NOT NULL DEFAULT 0
            )
            """);
        Execute("CREATE INDEX IF NOT EXISTS idx_artist_art_key ON artist_art(artist_key, position)");

        // v44: the framings a person made by hand. One row per (artist, photo), holding where the
        // middle of the window sits across and down the picture, and how tall it is against the
        // picture's own height (see RPM.Core.Art.ArtFraming for why it uses the photograph's units
        // and not the header's). A new table, so IF NOT EXISTS covers a fresh mirror and an upgrade
        // alike. An older mirror gains it empty: no framings, every picture framed by the chooser.
        // NO ROW means "let the chooser frame it". The automatic path is not a stored state and must
        // never look like one, or clearing a framing would leave something behind that the
        // reconciler and the ranker would have to know about.
        Execute(
            """
            CREATE TABLE IF NOT EXISTS artist_art_frame (
                artist_key TEXT NOT NULL COLLATE NOCASE,
                url TEXT NOT NULL,
                centre_x REAL NOT NULL,
                centre_y REAL NOT NULL,
                height_frac REAL NOT NULL,
                cover REAL NOT NULL DEFAULT 1,
                updated_utc_ticks INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (artist_key, url)
            )
            """);

        // v7: per-album metadata, the local seed of the album "social" schema. One row per album
        // folder (an album IS a folder here), holding the user's favourite flag, their star rating and
        // markdown review, and the Discogs release id stamped when the album was identified. A new
        // table, so IF NOT EXISTS covers a fresh mirror and an upgrade alike. An older mirror gains
        // it empty, and every album reads as "not a favourite, unrated, no review".
        Execute(
            """
            CREATE TABLE IF NOT EXISTS album_meta (
                folder TEXT PRIMARY KEY COLLATE NOCASE,
                is_favorite INTEGER NOT NULL DEFAULT 0,
                rating INTEGER NOT NULL DEFAULT 0,
                review_md TEXT,
                review_updated_utc_ticks INTEGER NOT NULL DEFAULT 0,
                -- Superseded by release_provider/release_id at v37 and kept only so an older mirror
                -- upgrades without a table rebuild. Nothing reads it.
                discogs_release_id TEXT,
                catno TEXT,
                released TEXT,
                classical INTEGER,
                normalize INTEGER,
                favorite_edited_utc_ticks INTEGER NOT NULL DEFAULT 0,
                favorite_synced_utc_ticks INTEGER NOT NULL DEFAULT 0,
                favorite_cloud_updated_at INTEGER NOT NULL DEFAULT 0,
                classical_edited_utc_ticks INTEGER NOT NULL DEFAULT 0,
                classical_synced_utc_ticks INTEGER NOT NULL DEFAULT 0,
                classical_cloud_updated_at INTEGER NOT NULL DEFAULT 0,
                -- When anything on this row was last said BY A PERSON, on whichever of their
                -- computers said it. Written by every setter and carried across the network with the
                -- row, so two machines that have both been edited apart agree on which edit is the
                -- later one without either of their clocks having to be right about anything else.
                -- See AlbumMetaEdit and LibraryDatabase.PeerAlbumMeta.
                edited_utc_ticks INTEGER NOT NULL DEFAULT 0
            )
            """);

        // v61: what a catalogue says about a release. IF NOT EXISTS covers fresh and upgrade alike,
        // and there is nothing to fill: a row appears when a provider is asked. See the note on
        // SchemaVersion for why this is keyed by the release and not by the folder.
        Execute(
            """
            CREATE TABLE IF NOT EXISTS release_meta (
                -- Which catalogue answered, and its own id for the thing it answered about. The two
                -- together, because a Discogs release number and an MBID share no namespace. Discogs
                -- answers about a PRESSING; MusicBrainz answers about the release group above them.
                provider TEXT NOT NULL COLLATE NOCASE,
                release_id TEXT NOT NULL,
                -- What the provider said, in its own words and its own order, semicolon-separated.
                -- Kept so a wider style list can be run over it without asking the catalogue again.
                raw_genres TEXT,
                raw_styles TEXT,
                -- How many people voted for each of raw_genres, aligned with it. MusicBrainz only:
                -- Discogs styles are an editor's, and have no count.
                votes TEXT,
                -- The same answer through RPM's own lists: one shelf, and every style RPM has a name
                -- for. Empty string is "asked, and nothing here is on our list", which is an answer.
                mapped_genre TEXT,
                mapped_styles TEXT,
                -- Which style list did the mapping. Subgenres.Version, so a list that has moved since
                -- can be found and re-run over raw_genres/raw_styles.
                vocab_version INTEGER NOT NULL DEFAULT 0,
                fetched_utc_ticks INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (provider, release_id)
            )
            """);

        // v8: the playlist tree. pl_node holds folders and playlists (a folder nests folders and
        // playlists; a playlist is a leaf whose contents are in pl_item). pl_item is a playlist's
        // ordered contents. Each row is either a song (by rpm_id, with rel_path as the display and
        // fallback value) or a reference to another playlist (item_kind='playlist' + ref_playlist_id,
        // which gives nesting). These are new tables, so IF NOT EXISTS covers fresh and upgrade alike.
        Execute(
            """
            CREATE TABLE IF NOT EXISTS pl_node (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                parent_id INTEGER,
                kind TEXT NOT NULL,
                name TEXT NOT NULL,
                sort INTEGER NOT NULL DEFAULT 0,
                is_now_playing INTEGER NOT NULL DEFAULT 0,
                created_utc_ticks INTEGER NOT NULL DEFAULT 0,
                modified_utc_ticks INTEGER NOT NULL DEFAULT 0,
                cloud_id TEXT,
                cloud_selected INTEGER NOT NULL DEFAULT 0,
                cloud_synced_utc_ticks INTEGER NOT NULL DEFAULT 0,
                cloud_adopted INTEGER NOT NULL DEFAULT 0
            )
            """);
        Execute(
            """
            CREATE TABLE IF NOT EXISTS pl_item (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                playlist_id INTEGER NOT NULL,
                position INTEGER NOT NULL DEFAULT 0,
                item_kind TEXT NOT NULL,
                rpm_id TEXT,
                rel_path TEXT,
                ref_playlist_id INTEGER
            )
            """);

        // v47: the cloud playlists this library has let go of. Keyed by the cloud id and nothing else,
        // because that is the only name that outlives the local row: the table says something about
        // a playlist whose node is deleted. Written by the two ways a playlist leaves the cloud
        // (DeletePlaylistNode, SetPlaylistOutOfCloud) and cleared by the one way it comes back
        // (SetPlaylistInCloud), so the answer is never worked out from timing.
        Execute(
            """
            CREATE TABLE IF NOT EXISTS pl_cloud_gone (
                cloud_id TEXT PRIMARY KEY,
                gone_utc_ticks INTEGER NOT NULL DEFAULT 0
            )
            """);
        Execute("CREATE INDEX IF NOT EXISTS idx_pl_node_parent ON pl_node(parent_id, sort)");
        Execute("CREATE INDEX IF NOT EXISTS idx_pl_item_playlist ON pl_item(playlist_id, position)");
        // The one pinned playlist that the player always plays. Seeded once. Its name is a stored
        // default that the UI shows through a localised label. is_now_playing is the flag the code finds it by.
        Execute(
            """
            INSERT INTO pl_node (parent_id, kind, name, sort, is_now_playing)
            SELECT NULL, 'playlist', 'Now playing', -1, 1
            WHERE NOT EXISTS (SELECT 1 FROM pl_node WHERE is_now_playing = 1)
            """);

        // v9: what the DJ analyser found in each track. One row per file, keyed by relative path
        // and stamped with the file's size and mtime: a file that changed no longer matches its row
        // and is analysed again. A new table, so IF NOT EXISTS covers fresh and upgrade alike.
        Execute(
            """
            CREATE TABLE IF NOT EXISTS track_analysis (
                relative_path TEXT PRIMARY KEY COLLATE NOCASE,
                size_bytes INTEGER NOT NULL DEFAULT 0,
                modified_utc_ticks INTEGER NOT NULL DEFAULT 0,
                analyzer_version INTEGER NOT NULL DEFAULT 0,
                duration_seconds REAL NOT NULL DEFAULT 0,
                bpm REAL NOT NULL DEFAULT 0,
                bpm_confidence REAL NOT NULL DEFAULT 0,
                beat_times TEXT,
                first_downbeat INTEGER NOT NULL DEFAULT 0,
                downbeat_confidence REAL NOT NULL DEFAULT 1,
                beats_per_bar INTEGER NOT NULL DEFAULT 4,
                camelot TEXT,
                key_confidence REAL NOT NULL DEFAULT 0,
                intensity REAL NOT NULL DEFAULT 0,
                energy_curve TEXT,
                intro_end_seconds REAL NOT NULL DEFAULT 0,
                outro_start_seconds REAL NOT NULL DEFAULT 0,
                analyzed_utc_ticks INTEGER NOT NULL DEFAULT 0,
                tags_written_utc_ticks INTEGER NOT NULL DEFAULT 0,
                sample_hash TEXT,
                audio_start_seconds REAL NOT NULL DEFAULT 0,
                audio_end_seconds REAL NOT NULL DEFAULT 0,
                beat_first_seconds REAL NOT NULL DEFAULT 0,
                beat_last_seconds REAL NOT NULL DEFAULT 0,
                bass_curve TEXT,
                mid_curve TEXT,
                top_curve TEXT,
                vocal_curve TEXT,
                phrase_bars INTEGER NOT NULL DEFAULT 0,
                phrase_first_bar INTEGER NOT NULL DEFAULT 0,
                phrase_confidence REAL NOT NULL DEFAULT 0,
                tempo_stability REAL NOT NULL DEFAULT 0,
                drop_seconds TEXT,
                intro_level REAL NOT NULL DEFAULT 0,
                outro_level REAL NOT NULL DEFAULT 0
            )
            """);

        // v30: what the app has done to this library, as opposed to what the user has asked for. A
        // pass that should run once per collection has nowhere else to record that: settings follow
        // the person to their next library, and a marker in one of the content tables would be a
        // column about the app on a row about a record. A new table, so IF NOT EXISTS covers fresh
        // and upgrade alike.
        Execute(
            """
            CREATE TABLE IF NOT EXISTS library_meta (
                key TEXT PRIMARY KEY,
                value TEXT,
                noted_utc_ticks INTEGER NOT NULL DEFAULT 0
            )
            """);

        // v18: each track's Chromaprint fingerprint, stamped like track_analysis with the file's
        // size and mtime, so a changed file no longer matches its row and is fingerprinted again.
        // submitted_utc_ticks is when this machine gave the fingerprint to the recognition
        // service (0 = not yet). It is kept per row so a library submits itself once and not on each
        // launch. A new table, so IF NOT EXISTS covers fresh and upgrade alike.
        Execute(
            """
            CREATE TABLE IF NOT EXISTS track_fingerprint (
                relative_path TEXT PRIMARY KEY COLLATE NOCASE,
                size_bytes INTEGER NOT NULL DEFAULT 0,
                modified_utc_ticks INTEGER NOT NULL DEFAULT 0,
                fp_version INTEGER NOT NULL DEFAULT 0,
                duration_seconds REAL NOT NULL DEFAULT 0,
                fingerprint BLOB,
                fingerprinted_utc_ticks INTEGER NOT NULL DEFAULT 0,
                submitted_utc_ticks INTEGER NOT NULL DEFAULT 0,
                song_id INTEGER NOT NULL DEFAULT 0
            )
            """);

        // v5: multi-disc columns. An older mirror gains them at the defaults (0 / NULL). A new sync
        // fills them from the files, which is where the disc tags live.
        if (version < 5)
        {
            AddColumnIfMissing("tracks", "disc_count", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing("tracks", "disc_subtitle", "TEXT");
        }

        // v6: the ranking algorithm's version, stamped on each saved automatic order. Existing rows
        // default to 0, which is older than any real algorithm, so every cached order is ranked once
        // more by the current chooser. This is how a change to the chooser reaches artists already seen.
        if (version < 6)
            AddColumnIfMissing("artist_art_pref", "rank_version", "INTEGER NOT NULL DEFAULT 0");

        // v45: how much of the header a hand-placed picture covers. Purely additive. The default is
        // edge to edge, which is what every framing written under v44 meant.
        if (version < 45)
            AddColumnIfMissing("artist_art_frame", "cover", "REAL NOT NULL DEFAULT 1");

        // v46: how a playlist's picture was chosen. Null everywhere on upgrade, which reads as the
        // automatic cover, so every list that exists before this step keeps its picture.
        if (version < 46)
            AddColumnIfMissing("pl_node", "cover", "TEXT");

        // v48: the stamp that the CLOUD put on this playlist the last time this machine wrote it:
        // unix seconds from the server's clock, not this machine's.
        //
        // It lets a sync run tell its own echo from somebody else's change. A phone can reorder a
        // playlist, so a cloud copy of a playlist that this tree already holds is not always this
        // machine's last push coming back. The only way to tell the two apart is whether the store
        // stamped the copy after this machine last did.
        //
        // Zero on every existing row, which reads as "never recorded" and is treated as no news: a
        // library that upgrades must not decide on its first run that every playlist it owns has
        // been changed elsewhere. The first push after the upgrade writes the real stamp.
        //
        // NOT merged into cloud_synced_utc_ticks, which holds LOCAL DateTime ticks. The two use
        // different clocks in different units, and a comparison across them misfires in whichever
        // direction the machines happen to disagree.
        if (version < 48)
            AddColumnIfMissing("pl_node", "cloud_updated_at", "INTEGER NOT NULL DEFAULT 0");

        // v66: which playlists the account lent this disk. See ForgetAdoptedPlaylists.
        if (version < 66)
            AddColumnIfMissing("pl_node", "cloud_adopted", "INTEGER NOT NULL DEFAULT 0");

        // v49: what this machine last SENT for this playlist: its track ids, in order, one per line.
        //
        // The stamp above says WHICH cloud copy this machine has seen. This column says WHAT it
        // published. They answer different questions and both are needed: the stamp decides whether
        // an inbound copy is news, and this decides what that news says. Given all three lists (what
        // was published, what is held, and what is in the cloud), a song missing from one of them is
        // a removal that somebody made, not an addition that somebody has not sent yet, and the two
        // can be applied together.
        //
        // Null on upgrade, and null is a stated answer: "this machine does not know what it last
        // published". See PlaylistBase.Unknown, where the merge refuses to infer any removal.
        if (version < 49)
        {
            AddColumnIfMissing("pl_node", "cloud_sent", "TEXT");

            // And the rest of what was published (the name and the folder), as one string to compare
            // against. Without it, a push that skips unchanged track lists would never send a rename
            // or a move to another folder, because neither changes a single id.
            AddColumnIfMissing("pl_node", "cloud_sent_meta", "TEXT");
        }

        // v10: how sure the analyser is about where the bar line falls. Purely additive. Existing
        // rows take the default, and they are analysed again anyway when the analyser version moves
        // past the one that wrote them. Nothing is dropped or rewritten here.
        // v11: playlists in the cloud. pl_node gains the identity a playlist is stored under in the
        // cloud (cloud_id, assigned the first time it is put in the cloud and kept for its whole
        // life), whether the user asked for it to be there, and when it was last pushed. Purely
        // additive: an older mirror gains them as "no cloud id, not synced".
        //
        // tracks.cloud_pinned separates a track that the user picked themselves from one that a synced
        // playlist pulled in. The first survives taking that playlist out of the cloud, the second
        // does not. Every selection that exists before this step was made by hand through an album or
        // a track, so they are all pinned. Anything else would silently un-pick the user's own choices.
        if (version < 11)
        {
            AddColumnIfMissing("pl_node", "cloud_id", "TEXT");
            AddColumnIfMissing("pl_node", "cloud_selected", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing("pl_node", "cloud_synced_utc_ticks", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing("tracks", "cloud_pinned", "INTEGER NOT NULL DEFAULT 0");
            Execute("UPDATE tracks SET cloud_pinned = 1 WHERE cloud_selected = 1");
        }

        Execute("CREATE INDEX IF NOT EXISTS idx_tracks_artist ON tracks(artist)");
        Execute("CREATE INDEX IF NOT EXISTS idx_tracks_album ON tracks(album)");
        Execute("CREATE INDEX IF NOT EXISTS idx_tracks_title ON tracks(title)");
        Execute("CREATE INDEX IF NOT EXISTS idx_tracks_folder ON tracks(folder)");
        // v12: the stream's own facts. No default, so every existing row reads as null. Sync treats
        // that as "never read" and fills it in on its next pass, with one tag read per track, once.
        // A file that answers nothing gets zero and not null, so it settles and is not read again.
        if (version < 12)
        {
            AddColumnIfMissing("tracks", "channels", "INTEGER");
            AddColumnIfMissing("tracks", "sample_rate", "INTEGER");
            AddColumnIfMissing("tracks", "bits_per_sample", "INTEGER");
            AddColumnIfMissing("tracks", "bitrate_kbps", "INTEGER");
            AddColumnIfMissing("tracks", "codec_description", "TEXT");
        }

        // v13: the cloud's log. One row per finished operation, so "did that album go up?" has an
        // answer after a restart. A new table, so IF NOT EXISTS covers fresh and upgrade alike. An
        // older mirror gains it empty, and the page shows nothing until the next sync writes.
        Execute(
            """
            CREATE TABLE IF NOT EXISTS cloud_activity (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                op INTEGER NOT NULL,
                title TEXT NOT NULL,
                subtitle TEXT,
                outcome INTEGER NOT NULL,
                detail TEXT,
                bytes INTEGER NOT NULL DEFAULT 0,
                at_utc_ticks INTEGER NOT NULL DEFAULT 0,
                full_error TEXT
            )
            """);
        Execute("CREATE INDEX IF NOT EXISTS idx_cloud_activity_at ON cloud_activity(at_utc_ticks DESC)");

        // full_error: the whole of a failure (CloudFailure.InFull), beside the one sentence in
        // `detail`, so a report sent from the Errors card carries what ffmpeg actually said.
        //
        // Unguarded and without a version of its own, as cloud_partial_error is. Every write to the
        // log names the column, so a mirror without it would stop logging. A nullable column that an
        // older build never names also leaves that build able to open this library, which a version
        // bump would not.
        AddColumnIfMissing("cloud_activity", "full_error", "TEXT");

        // v43: the listening log. One row per finished listen, skips included. The running totals on
        // the track row cannot answer a question with a WHEN in it ("what were you playing in March",
        // "what do you always skip"), and no number of counters can. A new table, so
        // IF NOT EXISTS covers fresh and upgrade alike. An existing mirror gains it empty, and its
        // windowed lists stay hidden until there is something in it to show.
        //
        // This is the one table that grows with USE and not with the collection, so three things
        // bound it: nothing under Listen.NoticedSeconds is written at all, rows age out
        // (LibraryDatabase.Listens), and there is a hard row cap above that. It is never pruned by
        // asking whether the track still exists. A row can be away for many reasons in this app
        // (offloaded, cloud-only, a peer's library off the network), and finding the history deleted
        // when the track comes back is a failure that cannot be undone.
        Execute(
            """
            CREATE TABLE IF NOT EXISTS track_verdict (
                relative_path TEXT NOT NULL COLLATE NOCASE,
                kind INTEGER NOT NULL,
                verdict INTEGER NOT NULL,
                said_utc_ticks INTEGER NOT NULL,
                PRIMARY KEY (relative_path, kind)
            ) WITHOUT ROWID
            """);

        // v63: the same statement about an act or a record. The subject is a folder for a record and a
        // credit for an act (see RecommendScope). It is one text column and not two: the scope says
        // which, and every reader wants both together anyway.
        //
        // NOCASE, and RecommendRefusals folds the case as well. This is deliberate: the collation
        // makes a row written from the menu match one read back by a query, and the fold makes the
        // in-memory set agree with both. A credit is a tag that somebody typed.
        //
        // A row is a refusal. Taking one back deletes it, as clearing a verdict does. A table of
        // "no, they are allowed again" rows would only grow.
        Execute(
            """
            CREATE TABLE IF NOT EXISTS recommend_refusal (
                scope INTEGER NOT NULL,
                subject TEXT NOT NULL COLLATE NOCASE,
                kind INTEGER NOT NULL,
                said_utc_ticks INTEGER NOT NULL,
                PRIMARY KEY (scope, subject, kind)
            ) WITHOUT ROWID
            """);

        Execute("""
            CREATE TABLE IF NOT EXISTS listen (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                relative_path TEXT NOT NULL COLLATE NOCASE,
                rpm_id TEXT,
                at_utc_ticks INTEGER NOT NULL,
                heard_seconds REAL NOT NULL DEFAULT 0,
                length_seconds REAL NOT NULL DEFAULT 0,
                counted INTEGER NOT NULL DEFAULT 0,
                local_hour INTEGER NOT NULL DEFAULT 0,
                local_dow INTEGER NOT NULL DEFAULT 0,
                session_id INTEGER NOT NULL DEFAULT 0
            )
            """);

        // v14: when a track joined the library. Filled from the file's mtime for rows that exist
        // before the column. That is the only evidence an existing mirror has, and it is better than
        // every album claiming to have arrived at the epoch. After this it is set once, on insert.
        // The step is guarded by the version only, like every step above it: a v1 mirror carries no
        // user_version at all and so reads as 0, which is what a fresh file says too. Both are safe,
        // because AddColumnIfMissing is idempotent and the fill touches no rows in a new mirror.
        if (version < 14)
        {
            AddColumnIfMissing("tracks", "added_utc_ticks", "INTEGER NOT NULL DEFAULT 0");
            Execute("UPDATE tracks SET added_utc_ticks = modified_utc_ticks WHERE added_utc_ticks = 0");
        }

        // v15: what has been listened to. Separate from v14 on purpose: one schema change per
        // version, so a mirror left half-migrated by a crash between the two is still at a version
        // that the next run knows how to finish.
        if (version < 15)
        {
            AddColumnIfMissing("tracks", "plays", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing("tracks", "last_played_utc_ticks", "INTEGER NOT NULL DEFAULT 0");
        }

        // v16: when this machine last made the cloud's renditions (the head clip and the free rate)
        // for a track. Without it, "which tracks still need renditions" has no answer that outlives the
        // process, and every launch would encode and upload the same oldest tracks again. Zero on
        // every existing row: a mirror cannot know what it was never told, so a library catches up
        // once, ten tracks a run, and then stops.
        if (version < 16)
            AddColumnIfMissing("tracks", "renditions_utc_ticks", "INTEGER NOT NULL DEFAULT 0");

        // v17: the pressing's own identity, as the linked Discogs release states it. Null means never
        // looked up, and not "has none". This lets an existing mirror fill itself the first time
        // each album is opened.
        if (version < 17)
        {
            AddColumnIfMissing("album_meta", "catno", "TEXT");
            AddColumnIfMissing("album_meta", "released", "TEXT");
        }

        // v19: ask again about every track that has no length. A row has no length when TagLib gave
        // up on the file: it parses containers from its own reading of the specifications and throws
        // on a layout that it does not expect. The track then sits in the library with no length
        // while it plays correctly (playback goes through ffmpeg, which reads the same file without
        // complaint). TagReader asks ffmpeg when TagLib comes back empty. Clearing channels back to
        // null puts these rows in front of it: null is "never read", and sync reads any row that says
        // so. This is one pass: a file that neither of them can measure gets a number and settles.
        if (version < 19)
            Execute("UPDATE tracks SET channels = NULL WHERE duration_seconds <= 0");

        // v20: rows this library adopted from the user's cloud catalogue; see the schema note above.
        // Purely additive, and every existing row reads as what it is: a row that came from this
        // machine's own disk.
        if (version < 20)
            AddColumnIfMissing("tracks", "cloud_adopted", "INTEGER NOT NULL DEFAULT 0");

        // v21: when this machine last edited the row's metadata itself; see the schema note above.
        if (version < 21)
            AddColumnIfMissing("tracks", "metadata_edited_utc_ticks", "INTEGER NOT NULL DEFAULT 0");

        // v22: the store's stamp as last accepted here; see the schema note above.
        if (version < 22)
            AddColumnIfMissing("tracks", "cloud_updated_at", "INTEGER NOT NULL DEFAULT 0");

        // v24: the advisory flag from the file. No default, so every existing row reads as null:
        // "never looked", which sync answers by reading the file once. See the schema note.
        if (version < 24)
            AddColumnIfMissing("tracks", "advisory", "INTEGER");

        // v25: which composition a track is part of. A classical track is one movement of one work by
        // one composer. Without these columns, nothing could group an album by its works, sort a
        // library by composer or offer either as a column without opening every file. Four columns,
        // one fact.
        //
        // movement_no is the sentinel for all four and has no default: every existing row reads as
        // null, which sync treats as "never read" and answers with one tag read per track, once.
        // After that a number (zero included) settles it for good. The text columns cannot do that
        // job themselves, because a track with no work and a track never asked about one both read
        // as nothing.
        if (version < 25)
        {
            AddColumnIfMissing("tracks", "work", "TEXT COLLATE NOCASE");
            AddColumnIfMissing("tracks", "movement_name", "TEXT COLLATE NOCASE");
            AddColumnIfMissing("tracks", "movement_no", "INTEGER");
            AddColumnIfMissing("tracks", "composer", "TEXT COLLATE NOCASE");
        }

        // v26: what kind of record an album is. NULL is an album nobody has answered for, and is
        // read as an ordinary record. 1 is the user saying that this one is classical, which survives
        // any rescan. See v34: every album is written as not classical, and nothing works the value
        // out from the tracks.
        if (version < 26)
            AddColumnIfMissing("album_meta", "classical", "INTEGER");

        // v27 also adds the per-album answer to whether this record is levelled at all. NULL (every
        // existing row, and every album that nobody has said anything about) means "let the setting
        // and the classical rule decide", so turning normalisation on levels a whole library without
        // anybody ticking anything. 0 and 1 are the listener's own answer for one record, which
        // overrides both and is never written back to null.
        if (version < 27)
            AddColumnIfMissing("album_meta", "normalize", "INTEGER");

        // v27: how loud each track is, and how close it comes to full scale. NULL in both means
        // "nobody has measured this". The background pass looks for that, and every existing row
        // reads that way, so a library already on this machine levels itself over the following few
        // minutes and not during the upgrade. A file with a ReplayGain tag from another program is
        // filled in by the tag reader at no cost. The other files are decoded once each. See
        // Analysis/LoudnessMeter.
        if (version < 27)
        {
            AddColumnIfMissing("tracks", "loudness_lufs", "REAL");
            AddColumnIfMissing("tracks", "true_peak", "REAL");
        }

        // v28: when the measured tempo and key were written into the file's own tags. Zero on every
        // existing row ("never written"), which is what the tagging pass looks for, and which keeps
        // it from opening a file that it has already done. It has its own version, like every step
        // above it: one schema change per bump, so a mirror left half-migrated by a crash is still
        // at a version that the next run knows how to finish.
        if (version < 28)
            AddColumnIfMissing("track_analysis", "tags_written_utc_ticks", "INTEGER NOT NULL DEFAULT 0");

        // v29: the analysis's own name for the audio, so a row can be found by what the record is
        // and not by where the file is. Null on every existing row and left that way: the hash
        // costs a decode, and the one place where a decode already happens is the next analysis of
        // that track. Nothing is read again to fill it. See Analysis/AudioKey.
        if (version < 29)
            AddColumnIfMissing("track_analysis", "sample_hash", "TEXT");

        // v31: the record's own edges. One step for the four columns, because they are one
        // measurement: a row with two of them and not the other two would describe nothing.
        if (version < 31)
        {
            AddColumnIfMissing("track_analysis", "audio_start_seconds", "REAL NOT NULL DEFAULT 0");
            AddColumnIfMissing("track_analysis", "audio_end_seconds", "REAL NOT NULL DEFAULT 0");
            AddColumnIfMissing("track_analysis", "beat_first_seconds", "REAL NOT NULL DEFAULT 0");
            AddColumnIfMissing("track_analysis", "beat_last_seconds", "REAL NOT NULL DEFAULT 0");
        }

        // v32: the songs the listener has loved, and when they said so. Zero everywhere on upgrade,
        // which is correct for every existing row: nothing could be marked before.
        if (version < 32)
        {
            AddColumnIfMissing("tracks", "favorite", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing("tracks", "favorited_utc_ticks", "INTEGER NOT NULL DEFAULT 0");
        }

        // v33: the stamps that a record's favourite travels on. They are on album_meta and not in a
        // table of their own: the mark IS album metadata, and the row exists as soon as somebody
        // says anything about the record.
        if (version < 33)
        {
            AddColumnIfMissing("album_meta", "favorite_edited_utc_ticks", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing("album_meta", "favorite_synced_utc_ticks", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing("album_meta", "favorite_cloud_updated_at", "INTEGER NOT NULL DEFAULT 0");
        }

        // v34: every album is an ordinary record. Nothing reads the tracks for this, so albums that
        // were classical only because their tags looked like it are set straight here, and so is
        // every album that was marked classical: all of them, because the rule that produced those
        // marks is withdrawn. Marking a record classical is something the listener does in the
        // album editor, one record at a time.
        if (version < 34)
            Execute("UPDATE album_meta SET classical = 0");

        // v35: the record measured in bands, and what its shape is. Nothing is filled in: every one
        // of these comes from a decode, and the analyser version is raised with them, so each row is
        // read again the next time anything asks for it.
        if (version < 35)
        {
            AddColumnIfMissing("track_analysis", "bass_curve", "TEXT");
            AddColumnIfMissing("track_analysis", "mid_curve", "TEXT");
            AddColumnIfMissing("track_analysis", "top_curve", "TEXT");
            AddColumnIfMissing("track_analysis", "vocal_curve", "TEXT");
            AddColumnIfMissing("track_analysis", "phrase_bars", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing("track_analysis", "phrase_first_bar", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing("track_analysis", "phrase_confidence", "REAL NOT NULL DEFAULT 0");
            AddColumnIfMissing("track_analysis", "tempo_stability", "REAL NOT NULL DEFAULT 0");
            AddColumnIfMissing("track_analysis", "drop_seconds", "TEXT");
            AddColumnIfMissing("track_analysis", "intro_level", "REAL NOT NULL DEFAULT 0");
            AddColumnIfMissing("track_analysis", "outro_level", "REAL NOT NULL DEFAULT 0");

            // The fingerprint's own name for this recording, once the store has matched it. It is
            // the one name that two independent rips of a CD can share. See RecordingKey.Song.
            AddColumnIfMissing("track_fingerprint", "song_id", "INTEGER NOT NULL DEFAULT 0");
        }

        // v36: music held by another of the user's own computers. Nothing on an existing row changes:
        // every one of them is held here, which is what the defaults say.
        if (version < 36)
        {
            AddColumnIfMissing("tracks", "peer_device_id", "TEXT");
            AddColumnIfMissing("tracks", "peer_adopted", "INTEGER NOT NULL DEFAULT 0");
        }

        // v37: the pressing an album is linked to, stored in a way that does not belong to one
        // catalogue. The Discogs ids already chosen are copied across and not asked for again: they
        // are the user's own work, and a link made once should not have to be made twice.
        if (version < 37)
        {
            AddColumnIfMissing("album_meta", "release_provider", "TEXT");
            AddColumnIfMissing("album_meta", "release_id", "TEXT");
            AddColumnIfMissing("album_meta", "barcode", "TEXT");
            AddColumnIfMissing("album_meta", "rpm_album_id", "TEXT");

            Execute(
                "UPDATE album_meta SET release_provider = 'discogs', release_id = discogs_release_id " +
                "WHERE discogs_release_id IS NOT NULL AND release_id IS NULL");
        }

        // v38: the disc a CD album was ripped from. Nothing to fill: the id of a disc that is already
        // ripped is not stored anywhere that can be read, and the tags of anything ripped from now on
        // carry it.
        if (version < 38)
            AddColumnIfMissing("album_meta", "disc_id", "TEXT");

        // v39: the album above its pressings. Nothing to fill it from: the tags that carry it are
        // read on the next import, and an album linked before this step has no group until then.
        if (version < 39)
            AddColumnIfMissing("album_meta", "release_group_id", "TEXT");

        // v40: which way the listener counts this record, when they have said. 1 everywhere on
        // upgrade, because nobody could say before. It is stored beside the favourite and not in
        // track_analysis, for the same reason as the favourite: it belongs to the person, and
        // track_analysis is a cache that is discarded whole every time the analyser version changes.
        // See Analysis/TempoOctave.
        if (version < 40)
            AddColumnIfMissing("tracks", "bpm_octave", "REAL NOT NULL DEFAULT 1");

        // v41: why a track that is in the cloud is not complete there. Null on every existing row,
        // including the ones this is about: a file whose head clip will not encode fails quietly
        // from the day it is uploaded, and the first run after the upgrade writes down why and
        // stops asking.
        //
        // Unguarded, unlike the steps above it. The version stamp is written at the end of this
        // method, so it records that the steps RAN, not that they all took effect. A mirror left by
        // a build that had the reader for this column but not the step that adds it is stamped at a
        // version whose guard will never fire again, and every query that names the column would
        // fail for the life of the install. The column is also named in SQL that the cloud runs on
        // every sync (CloudTracksWithLocalFiles), where a missing column stops the sync and does not
        // only hide a badge. AddColumnIfMissing is one PRAGMA and a comparison, which is cheaper to
        // pay once per open than the recovery.
        AddColumnIfMissing("tracks", "cloud_partial_error", "TEXT");

        // v42: when this album row was last edited, so a listing from another of the user's own
        // computers can be merged into it and not guessed at. Zero on every existing row, because the
        // stamp was not written when they were made. A row with no stamp loses to any row that has
        // one, which is the right way round: the machine that has been upgraded and edited since is
        // the machine with something to say.
        //
        // Unguarded, for the reason given above the previous step.
        AddColumnIfMissing("album_meta", "edited_utc_ticks", "INTEGER NOT NULL DEFAULT 0");

        // The virtualised tree grid pages a folder's tracks in name order.
        // This composite index turns every page query into an index range scan.
        Execute("CREATE INDEX IF NOT EXISTS idx_tracks_folder_name ON tracks(folder, file_name)");
        // Not UNIQUE: a file copied outside the app carries the id of its source until sync
        // stamps it again, and a constraint violation there would stop the whole sync.
        Execute("CREATE INDEX IF NOT EXISTS idx_tracks_rpm_id ON tracks(rpm_id)");
        // The upload worker asks "what is queued?" on every pass over a library that is
        // overwhelmingly not queued.
        Execute("CREATE INDEX IF NOT EXISTS idx_tracks_cloud_state ON tracks(cloud_state)");
        // The canonical hash says "this machine already has that cloud track" for a file that it
        // has uploaded itself. The same rip on two computers is one set of bytes, and the mirror
        // matches the catalogue on it before it adopts anything. See Cloud/CloudLibraryMirror.
        Execute("CREATE INDEX IF NOT EXISTS idx_tracks_content_hash ON tracks(content_hash)");
        // Not UNIQUE: two copies of one record are two files, two rows and one sound, and that is the
        // purpose of the column. The lookup wants any row that describes this audio, not only one.
        Execute("CREATE INDEX IF NOT EXISTS idx_track_analysis_sample_hash ON track_analysis(sample_hash)");
        // Partial on purpose: the levelling pass asks "what is unmeasured?" of a library that is
        // overwhelmingly measured, and it asks again every time anything changes (an import, a cloud
        // pull, a listing from another of the user's computers). Without this index each of those
        // walks every row to find nothing. The index holds only the rows still waiting, so a
        // finished library answers from an empty B-tree. It costs nothing to keep, because every
        // entry in it is deleted as soon as that track is measured. See LibraryDatabase.Loudness.
        Execute(
            "CREATE INDEX IF NOT EXISTS idx_tracks_needs_loudness ON tracks(added_utc_ticks) " +
            "WHERE loudness_lufs IS NULL");

        // v50: what the pressing is, as the catalogue describes it. Nothing to fill: the description
        // arrives with the release lookup, and an album linked before this step gets one the next
        // time its page asks Discogs anything.
        if (version < 50)
            AddColumnIfMissing("album_meta", "edition", "TEXT");

        // v51: the record's best-known song. Nothing to fill: the first time each album page is
        // opened it asks, and after that the star is drawn from this column.
        if (version < 51)
            AddColumnIfMissing("album_meta", "popular_track", "TEXT");

        // v52: when the star was last asked for. Zero everywhere on upgrade, so each album's first
        // page-open after the upgrade refreshes it.
        if (version < 52)
            AddColumnIfMissing("album_meta", "popular_asked_utc_ticks", "INTEGER NOT NULL DEFAULT 0");

        // v53: forget the stars. See the note on SchemaVersion: the stored answers were wrong, and
        // zero in the stamp sends each record's page back to the services once.
        if (version < 53)
            Execute("UPDATE album_meta SET popular_track = NULL, popular_asked_utc_ticks = 0");

        // v54: the tempo and the key that the file itself carries. There are two columns, named for
        // where they came FROM: this is what the tags say, not what this app measured. The app's own
        // readings live in track_analysis, which is a cache keyed on the file and discarded whole
        // whenever the analyser version changes. These values are neither cache nor measurement:
        // they are what arrived with the music.
        //
        // Zero and null on upgrade. Nothing is filled and no scan is added: every file is read on the
        // next sync that touches it anyway, and a library that has not been scanned again answers
        // "not known" until it is.
        if (version < 54)
        {
            AddColumnIfMissing("tracks", "tag_bpm", "REAL NOT NULL DEFAULT 0");
            AddColumnIfMissing("tracks", "tag_key", "TEXT");
        }

        // v55: what a track sounds like, in three numbers. Null everywhere on upgrade, which hands
        // every track to the pass once, in the same way as a library that has never been levelled.
        // Nothing is filled here: the pass IS the fill, and it is deliberately slow (see
        // LoudnessService: one decode at a time with a pause between them).
        if (version < 55)
        {
            AddColumnIfMissing("tracks", "mood_brightness", "REAL");
            AddColumnIfMissing("tracks", "mood_onsets", "REAL");
            AddColumnIfMissing("tracks", "mood_dynamics", "REAL");
        }

        // The mood half of the pass gets the same kind of index as the levelling half (see above).
        // It is created HERE and not with the other indexes because those run before this migration:
        // a mirror old enough to be upgrading has no mood_brightness column when they run, and an
        // index on a column that does not exist is an error that stops the whole upgrade.
        //
        // The two halves ask different questions: "what has no loudness?" and "what has no
        // brightness and is long enough to have one?". Without an index for the second, the query
        // searches the duration index and looks at every row that is long enough, which is most of
        // the library, to find a small part of it. It is indexed on duration because that is the
        // part of the question with a range in it. Measured on a copy of a 19,747-track library:
        // the count behind it goes from 65ms to 3ms warm, the index builds in 28ms, and the file
        // grows by less than a tenth of a megabyte. Like the loudness index, it holds only the rows
        // still waiting, so a finished library answers from an empty B-tree.
        Execute(
            "CREATE INDEX IF NOT EXISTS idx_tracks_needs_mood ON tracks(duration_seconds) " +
            "WHERE mood_brightness IS NULL");

        // v56: which sub-division of its genre a record belongs to. Nothing is filled: the answer is
        // in the file already, so a track picks its style up the next time its tags are read, and a
        // library that has never carried one has nulls until then.
        if (version < 56)
            AddColumnIfMissing("tracks", "subgenre", "TEXT COLLATE NOCASE");

        // v57: whether a song belonged where a generated list put it. NOT the heart, which keeps its
        // own two columns and its own meaning; see TrackVerdict, where the reason for keeping them
        // separate is written out. Zero on every existing row, which reads as nothing said and not
        // as approval.
        if (version < 57)
        {
            AddColumnIfMissing("tracks", "liked_utc_ticks", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing("tracks", "disliked_utc_ticks", "INTEGER NOT NULL DEFAULT 0");
        }

        // v58: how much of the world plays each song. No DEFAULT and no NOT NULL: every existing row
        // has to read as "nobody has asked", which null says and zero would deny.
        if (version < 58)
            AddColumnIfMissing("tracks", "world_plays", "INTEGER");

        // v64: how positive or negative a song's words are, where they could be read. See
        // LyricValence, which holds the whole rule. No DEFAULT and no NOT NULL, so every existing row
        // reads as "no believed reading", which null says and zero would deny: zero is the middle of
        // the scale and would place every unread song in the centre of a distribution that it was
        // never measured against.
        //
        // Null covers BOTH "never read" and "read, and the gates refused it". This is a decision.
        // The two cases are told apart elsewhere here (see release_meta, and SaveUnmeasurable)
        // because something loops over the nulls and would hand out the same track for ever. Nothing
        // loops over these: the reading is taken where a lyric sheet is already in hand, the sheet is
        // cached, and scoring one again costs microseconds. A pass that sweeps for them would need
        // the distinction and would have to add it.
        if (version < 64)
            AddColumnIfMissing("tracks", "lyric_valence", "REAL");

        // v59: the styles that Discogs files a release under. Nothing to fill here: the column is
        // filled by the sweep that walks the albums already linked to a Discogs release, and by every
        // release read from now on. Null on upgrade, which means nobody has asked.
        if (version < 59)
            AddColumnIfMissing("album_meta", "styles", "TEXT");

        // v60: the three columns that two lines of work both numbered 56 or 57. AddColumnIfMissing is
        // idempotent, so this costs a read of table_info on a database that has them, and repairs one
        // that has only its own half. See the note on SchemaVersion: the steps above cannot be
        // edited to do this, because on a database that needs it those steps have already run.
        if (version < 60)
        {
            AddColumnIfMissing("tracks", "subgenre", "TEXT COLLATE NOCASE");
            AddColumnIfMissing("tracks", "liked_utc_ticks", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing("tracks", "disliked_utc_ticks", "INTEGER NOT NULL DEFAULT 0");
        }

        if (version < 62)
        {
            // Every kind gets a row, because a stamp made before this step meant every list: the
            // listener had no way to say anything narrower, and reading it as "only Chill" would
            // put music back in front of them that they had turned down.
            foreach (var kind in Enum.GetValues<SmartPlaylistKind>())
            {
                Execute($"""
                    INSERT OR IGNORE INTO track_verdict (relative_path, kind, verdict, said_utc_ticks)
                    SELECT relative_path, {(int)kind},
                           CASE WHEN liked_utc_ticks >= disliked_utc_ticks THEN {(int)TrackVerdict.Liked}
                                ELSE {(int)TrackVerdict.Disliked} END,
                           MAX(liked_utc_ticks, disliked_utc_ticks)
                    FROM tracks
                    WHERE liked_utc_ticks <> 0 OR disliked_utc_ticks <> 0
                    """);
            }

            Execute("UPDATE tracks SET liked_utc_ticks = 0, disliked_utc_ticks = 0 " +
                    "WHERE liked_utc_ticks <> 0 OR disliked_utc_ticks <> 0");
        }

        // v65: how often each playlist has been played. A track carries plays, and a record is
        // counted through its tracks, but playing a LIST leaves no mark there. One table serves both
        // kinds and not a column on pl_node, because a generated list has no node to hold a column.
        // See LibraryDatabase.PlaylistPlays.
        Execute(
            """
            CREATE TABLE IF NOT EXISTS playlist_play (
                kind INTEGER NOT NULL,
                key TEXT NOT NULL,
                plays INTEGER NOT NULL DEFAULT 0,
                last_played_utc_ticks INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (kind, key)
            ) WITHOUT ROWID
            """);

        // v67: one row per (track, device). This machine's own row sits here beside the rows that its
        // other devices report, so tracks.plays can be the merged total while this machine's own
        // contribution stays readable, which is what it has to send. Keyed on rpm_id and not on the
        // path, because both heads already share that id. A track with none is a file that the
        // cloud has never seen, and its plays stay here.
        Execute(
            """
            CREATE TABLE IF NOT EXISTS track_plays (
                rpm_id TEXT NOT NULL,
                device TEXT NOT NULL,
                plays INTEGER NOT NULL DEFAULT 0,
                last_played_utc_ticks INTEGER NOT NULL DEFAULT 0,
                sent_plays INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (rpm_id, device)
            ) WITHOUT ROWID
            """);

        // Every play in a mirror written before that table existed is this machine's. They are seeded
        // with nothing sent, so the first sync publishes all of what this machine has listened to.
        // They are filed under the seed name because the migration runs before anything can give the
        // mirror this install's id. ClaimSeededPlays renames them on the first pass.
        if (version < 67)
            Execute(
                $"""
                INSERT OR IGNORE INTO track_plays (rpm_id, device, plays, last_played_utc_ticks, sent_plays)
                SELECT rpm_id, '{PlaysSeedDevice}', plays, last_played_utc_ticks, 0
                FROM tracks
                WHERE rpm_id IS NOT NULL AND plays > 0
                """);

        // v68: the stamps that the classical mark travels to the cloud on, the same three that the
        // favourite got at v33.
        if (version < 68)
        {
            AddColumnIfMissing("album_meta", "classical_edited_utc_ticks", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing("album_meta", "classical_synced_utc_ticks", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing("album_meta", "classical_cloud_updated_at", "INTEGER NOT NULL DEFAULT 0");
        }

        // The record's release type, as its files say. Added on every open and not behind a version
        // step, on purpose: a version step makes the file refuse every older build
        // (LibraryTooNewException above), and this library is opened by the installed app, by builds
        // from other branches of work and by this one. An older build reads columns by name and writes
        // an explicit list, so an extra column costs it nothing. Null on every existing row until the
        // file is read again. Nothing is filled, because the files are the source.
        AddColumnIfMissing("tracks", "release_type", "TEXT");

        // The one order it is read in: most played first. A small table (a row per list that a
        // listener has started), so this is cheap to keep and turns the quick selection's read into
        // a walk of the index.
        Execute("CREATE INDEX IF NOT EXISTS idx_playlist_play_plays ON playlist_play(plays DESC)");

        // The primary key is one row per (track, kind). This index is the other direction: every song
        // that a list has been told to stop offering, which is what NotTurnedDown asks on every
        // generated list that is drawn.
        Execute("CREATE INDEX IF NOT EXISTS idx_track_verdict_kind ON track_verdict(kind, verdict)");

        // The same direction, for the refusals: every act and record that ONE list has been told to
        // stop offering. That is the only way they are read (a kind at a time, never a subject at a
        // time), so the kind leads.
        Execute("CREATE INDEX IF NOT EXISTS idx_recommend_refusal_kind ON recommend_refusal(kind)");

        // The decade lists order by it and stop at fifty. This index turns "the world's fifty
        // favourite songs of the seventies" from a scan of the collection into a read from the
        // index, as the sortable columns above are served.
        Execute("CREATE INDEX IF NOT EXISTS idx_tracks_world_plays ON tracks(world_plays DESC)");

        // One index per column that the data table's header can be clicked to sort by. An index on a
        // column holds (that column, rowid), and the table's sort ends in the rowid (see
        // LibraryDatabase.TrackTable's OrderBy), so each of these IS the order that the query asks
        // for: SQLite reads it from the index, streaming, and does not scan the table into a temp
        // B-tree. Measured on a 9,639-track library, each of these sorts goes from 7-11ms to 1ms, and
        // the plan from SCAN + TEMP B-TREE to a covering index. The eight together build in 100ms and
        // add no measurable file size at that scale. The sorts they serve are the ones a collection
        // is actually read by.
        Execute("CREATE INDEX IF NOT EXISTS idx_tracks_album_artist ON tracks(album_artist)");
        Execute("CREATE INDEX IF NOT EXISTS idx_tracks_genre ON tracks(genre)");
        Execute("CREATE INDEX IF NOT EXISTS idx_tracks_year ON tracks(year)");
        Execute("CREATE INDEX IF NOT EXISTS idx_tracks_added_utc_ticks ON tracks(added_utc_ticks)");
        Execute("CREATE INDEX IF NOT EXISTS idx_tracks_duration_seconds ON tracks(duration_seconds)");
        Execute("CREATE INDEX IF NOT EXISTS idx_tracks_plays ON tracks(plays)");
        Execute("CREATE INDEX IF NOT EXISTS idx_tracks_last_played ON tracks(last_played_utc_ticks)");
        Execute("CREATE INDEX IF NOT EXISTS idx_tracks_size_bytes ON tracks(size_bytes)");

        // Every windowed list starts "since $when", and the log is read on the UI thread when the
        // playlist pane reloads. The second index makes one track's own history a lookup and not a
        // scan of everything ever played.
        Execute("CREATE INDEX IF NOT EXISTS idx_listen_at ON listen(at_utc_ticks DESC)");
        Execute("CREATE INDEX IF NOT EXISTS idx_listen_path ON listen(relative_path, at_utc_ticks DESC)");

        // The fingerprints not yet offered to the recognition service, oldest first, which is exactly what
        // QueryFingerprintsNeedingSubmission asks for. Without it the query scans every fingerprint row,
        // blob and all, and sorts in a temp B-tree, while it holds the read gate. Measured on a 28,068-row
        // table with 1,211 unsubmitted: 190 ms with a warm cache and 5.4 s in the app with a cold one,
        // against 7 ms with this index. The 5.4 s hold was longer than the wait at the app's close.
        Execute("CREATE INDEX IF NOT EXISTS idx_track_fingerprint_unsubmitted ON track_fingerprint(submitted_utc_ticks, fingerprinted_utc_ticks)");

        if (version != SchemaVersion)
            Execute($"PRAGMA user_version = {SchemaVersion}");
    }

    /// <summary>
    /// Renames a column when the table still carries the old name and not the new one. Both halves
    /// matter: the table may not exist at all (a mirror older than it), and a database that has
    /// already been through this step must not be asked again, because SQLite would throw on the
    /// missing column.
    /// </summary>
    private void RenameColumnIfPresent(string table, string from, string to)
    {
        var columns = ColumnNames(table);
        if (!columns.Contains(from) || columns.Contains(to))
            return;
        Execute($"ALTER TABLE {table} RENAME COLUMN {from} TO {to}");
    }

    private HashSet<string> ColumnNames(string table)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(1));
        return names;
    }

    private void AddColumnIfMissing(string table, string column, string declaration)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return;
            }
        }
        Execute($"ALTER TABLE {table} ADD COLUMN {column} {declaration}");
    }

    private object? Scalar(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    public int Count
    {
        get
        {
            using (_readGate.Take())
            {
                using var cmd = _read.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM tracks";
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }
    }

    /// <summary>Insert or replace by relative path.</summary>
    public void Upsert(LibraryTrack track)
    {
        using (_gate.Take())
            UpsertLocked(track);
    }

    public void UpsertMany(IEnumerable<LibraryTrack> tracks)
    {
        using (_gate.Take())
        {
            using var tx = _connection.BeginTransaction();
            foreach (var t in tracks)
                UpsertLocked(t);
            tx.Commit();
        }
    }

    /// <summary>The cloud states in which a row's RPM id belongs to the store, as SQL. It is read from
    /// <see cref="CloudName.Bound"/> so that the statement below and the sync pass cannot come to
    /// different answers about the same row.</summary>
    private static readonly string BoundToTheStore =
        string.Join(", ", CloudName.Bound.Select(state => (int)state));

    /// <summary>
    /// What "these bytes are not the bytes that were measured" means, as SQL.
    ///
    /// <para><b>It asks about the AUDIO, not about the file.</b> The measurements that use it (the
    /// level, the true peak and the three arousal numbers) cost a whole decode. They must survive a
    /// retag and be discarded after a re-encode. A test on the file's size and mtime does not do
    /// that, because a retag changes BOTH: writing one genre tag would make the row say that the
    /// music had changed, and the levelling pass would read again files that nobody had re-encoded.</para>
    ///
    /// <para><b>What is compared instead</b> is what the stream itself is: how long it runs, how many
    /// channels, at what rate and depth, in what codec. A tag write changes none of them. A re-encode
    /// or a fresh rip changes at least one, and that is the case in which the measurement no longer
    /// describes the file.</para>
    ///
    /// <para><b>Not the bitrate</b>, although it sits beside the others. For several formats it is
    /// derived from the file's length, so a tag write changes it, and that is what this test must
    /// not key on.</para>
    ///
    /// <para><b>Duration to the second</b>, not exactly: it is a REAL read back from a container, and
    /// a measurement must not be thrown away over a rounding difference. A second is far below any
    /// re-encode worth reading again and far above any noise.</para>
    ///
    /// <para><c>IS NOT</c> and not <c>&lt;&gt;</c> throughout, because it is null-safe in SQLite: a row
    /// whose channel count was never read must not compare equal to everything.</para>
    ///
    /// <para>One definition, used by every column that keeps a measurement, so the five cannot drift
    /// apart as five copies would.</para>
    /// </summary>
    private const string TheAudioChanged =
        """
        ABS(COALESCE(tracks.duration_seconds, 0) - COALESCE($dur, 0)) > 1
             OR tracks.channels IS NOT $chan
             OR tracks.sample_rate IS NOT $rate
             OR tracks.bits_per_sample IS NOT $bits
             OR tracks.codec_description IS NOT $codec
        """;

    /// <summary>
    /// SET clauses that clear the cloud's refusals of a track when its file's size or date has
    /// changed. Reads <c>$size</c> and <c>$mtime</c>.
    ///
    /// <para>What the cloud refused was those bytes, so the refusal stops being a fact about the track
    /// when the bytes change: a file ripped again without its thirty channels, encoded again, or no
    /// longer half-written is a different question and is asked again. Both refusals clear, and the
    /// attempt count and the backoff with them.</para>
    ///
    /// <para>One definition, used by every write that stamps a row with its file's new size and date
    /// (the scan's upsert, a retag's move, the analysis-tag writer). If a write stamped the new date
    /// without it, the next scan would see nothing to compare, and a refusal would outlive the bytes
    /// it was about.</para>
    /// </summary>
    private const string CloudRefusalsClearWhenTheFileChanges =
        """
        cloud_error = CASE WHEN tracks.size_bytes <> $size OR tracks.modified_utc_ticks <> $mtime
                           THEN NULL ELSE tracks.cloud_error END,
        cloud_partial_error = CASE WHEN tracks.size_bytes <> $size OR tracks.modified_utc_ticks <> $mtime
                                   THEN NULL ELSE tracks.cloud_partial_error END,
        cloud_attempts = CASE WHEN tracks.size_bytes <> $size OR tracks.modified_utc_ticks <> $mtime
                              THEN 0 ELSE tracks.cloud_attempts END,
        cloud_next_attempt_utc_ticks = CASE WHEN tracks.size_bytes <> $size OR tracks.modified_utc_ticks <> $mtime
                                            THEN 0 ELSE tracks.cloud_next_attempt_utc_ticks END
        """;

    /// <summary>
    /// The cloud columns are set on INSERT (a row restored from the cloud arrives already
    /// knowing where it lives) but are deliberately NOT in the DO UPDATE list. Upsert's callers
    /// (import and sync) build their rows with <see cref="FromTrackInfo"/>, which reads the
    /// file and knows nothing about the cloud. If they could write these columns, a synced track
    /// would be reset to Local every time the user retagged it. Cloud state is owned by
    /// <see cref="SetCloudState"/>, <see cref="MarkSynced"/> and <see cref="MarkOffloaded"/> alone.
    ///
    /// <para>The same rule applies, for the same reason, to what the row knows that the file cannot
    /// say: when it joined the library and how often it has been played. Both are stamped on insert
    /// and never written again by an upsert. A caller that holds a freshly read file has nothing to
    /// say about either, and if it could write them, an album's history would be reset every time a
    /// tag was corrected. See <see cref="NoteListen"/> and <see cref="MoveMany"/>.</para>
    /// </summary>
    private void UpsertLocked(LibraryTrack t)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            $"""
            INSERT INTO tracks (relative_path, folder, file_name, extension, size_bytes,
                                modified_utc_ticks, duration_seconds, title, artist,
                                album_artist, album, genre, subgenre, advisory, track_number, disc_number,
                                disc_count, disc_subtitle, work, movement_name, movement_no,
                                composer, year,
                                channels, sample_rate, bits_per_sample, bitrate_kbps,
                                codec_description, loudness_lufs, true_peak, tag_bpm, tag_key, release_type,
                                rpm_id, content_hash, cloud_state, cloud_selected, cloud_pinned,
                                canonical_size_bytes, cloud_synced_utc_ticks, cloud_error,
                                added_utc_ticks)
            VALUES ($path, $folder, $name, $ext, $size, $mtime, $dur, $title, $artist,
                    $albumartist, $album, $genre, $subgenre, $advisory, $track, $disc, $disccount, $discsub,
                    $work, $movement, $movementno, $composer, $year,
                    $chan, $rate, $bits, $kbps, $codec, $lufs, $peak, $tagbpm, $tagkey, $releasetype,
                    $gid, $hash, $cstate, $csel, $cpin, $csize, $cticks, $cerr,
                    $added)
            ON CONFLICT(relative_path) DO UPDATE SET
                folder = $folder, file_name = $name, extension = $ext, size_bytes = $size,
                modified_utc_ticks = $mtime, duration_seconds = $dur, title = $title,
                artist = $artist, album_artist = $albumartist, album = $album,
                genre = $genre, subgenre = $subgenre, advisory = $advisory, track_number = $track, disc_number = $disc,
                disc_count = $disccount, disc_subtitle = $discsub, work = $work,
                movement_name = $movement, movement_no = $movementno, composer = $composer,
                year = $year,
                channels = $chan, sample_rate = $rate, bits_per_sample = $bits,
                bitrate_kbps = $kbps, codec_description = $codec,

                -- Taken as read, the way the genre above it is, and NOT preserved the way the loudness
                -- below it is. The difference is what the column means: the loudness is a reading this
                -- app spent a whole decode on and the file usually says nothing about, so a tag read
                -- must not flatten it. These two ARE the file's own answer — a tempo cleared out of a
                -- file is a file that no longer claims one, and a row still claiming it would be this
                -- app remembering something the music has stopped saying.
                tag_bpm = $tagbpm, tag_key = $tagkey,
                -- The file's own answer as well: a type cleared out of a file is a file that no longer claims one.
                release_type = $releasetype,
                -- The measurement survives a retag and dies with a re-encode. A caller here is
                -- holding a freshly read file, and what it knows about loudness is whatever
                -- ReplayGain tag the file carries — usually nothing. Writing that straight in would
                -- throw away a reading this app spent a whole decode on every time somebody fixed a
                -- spelling. So it is only taken when the AUDIO changed, which is exactly when the old
                -- reading stopped describing it — see TheAudioChanged, and the note there about what
                -- this used to ask and what it cost.
                loudness_lufs = CASE WHEN {TheAudioChanged}
                                     THEN $lufs ELSE COALESCE($lufs, tracks.loudness_lufs) END,

                -- The three arousal numbers go the same way and for the same reason: they were paid
                -- for with the same decode, and a retag must not throw them away. Nothing that builds
                -- a row from a file knows them, so they are only ever cleared here — which is exactly
                -- what a re-encode should do to them.
                mood_brightness = CASE WHEN {TheAudioChanged}
                                       THEN NULL ELSE tracks.mood_brightness END,
                mood_onsets = CASE WHEN {TheAudioChanged}
                                   THEN NULL ELSE tracks.mood_onsets END,
                mood_dynamics = CASE WHEN {TheAudioChanged}
                                     THEN NULL ELSE tracks.mood_dynamics END,
                true_peak = CASE WHEN {TheAudioChanged}
                                 THEN $peak ELSE COALESCE($peak, tracks.true_peak) END,

                -- What the cloud refused was these bytes. Only on a change: a retag rewrites the
                -- row several times a session and must not put a file that cannot be encoded back
                -- on the queue each time.
                {CloudRefusalsClearWhenTheFileChanges},
                -- The name the store knows this track by is not this pass's to change. A file that
                -- comes back from a retag without its id is read as unstamped, sync mints a fresh
                -- one, and writing it here left the row saying "in the cloud" under a name the
                -- account has never had — a record shown as backed up that is not, and a rendition
                -- pass answered 404 about it on every run for ever. Nothing corrects such a row
                -- afterwards: the catalog pass skips ids the listing does not contain.
                --
                -- The one door to renaming a row that IS up there is SetRpmId, which the upload path
                -- opens when the store itself says this audio is already stored under another id.
                -- See CloudName, where the same rule is read by the sync pass.
                rpm_id = CASE
                    WHEN tracks.rpm_id IS NOT NULL AND tracks.rpm_id <> ''
                         AND tracks.cloud_state IN ({BoundToTheStore})
                         -- Unless the row is about to stop being the cloud's: a FILE arriving at a
                         -- cloud-only row's path that is not that object takes the row over and
                         -- demotes it to Local two lines below. Its name goes with it — the cloud
                         -- object is a different recording and keeps its own.
                         AND NOT ($cstate = $local AND tracks.cloud_state = $cloudOnly)
                    THEN tracks.rpm_id
                    ELSE $gid END,

                -- A caller holding a FILE takes the row over. An adopted row is the catalog's word
                -- about a track this machine does not hold (see MirrorCloudLibrary); the moment one
                -- is held, the row is this library's own. Without this, deleting a record that is
                -- also in the cloud and then importing it again left the track adopted and
                -- cloud-only for ever: the files were filed, the shelf still said they were not
                -- here, and the import read as having done nothing.
                --
                -- "Holding a file" is the incoming row saying Local, which is what FromTrackInfo
                -- produces and therefore what import and sync always say. The other caller — a
                -- metadata edit on a record this library does NOT hold — passes the row's own
                -- cloud state back, and must leave both columns exactly as they were.
                cloud_adopted = CASE WHEN $cstate = $local THEN 0 ELSE tracks.cloud_adopted END,
                cloud_state = CASE
                    WHEN $cstate <> $local OR tracks.cloud_state <> $cloudOnly THEN tracks.cloud_state
                    -- The same track come home — its id is the one the cloud copy was uploaded
                    -- under, so both copies exist. The library sync says exactly this for a file
                    -- that reappears; an import of the same file must not say something else.
                    WHEN tracks.rpm_id IS NOT NULL AND tracks.rpm_id = $gid THEN $synced
                    -- A different file at that path: on this disk, and nobody's cloud object.
                    ELSE $local END
            """;
        BindTrack(cmd, t);
        // The row's joining date, for the INSERT half only. Taken from the row when it carries one (a
        // track restored from the cloud knows when it first arrived) and the current time otherwise.
        cmd.Parameters.AddWithValue("$added", t.AddedUtcTicks > 0 ? t.AddedUtcTicks : DateTime.UtcNow.Ticks);
        cmd.Parameters.AddWithValue("$cloudOnly", (int)CloudState.CloudOnly);
        cmd.Parameters.AddWithValue("$synced", (int)CloudState.Synced);
        cmd.Parameters.AddWithValue("$local", (int)CloudState.Local);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Rewrites the row at <paramref name="fromRelativePath"/> to be <paramref name="track"/>. The
    /// file was moved or renamed outside the app and sync recognised it by its RPM id.
    /// An upsert would delete the old row and insert a new one. This keeps the row (and its id,
    /// which future play counts and history depend on) and changes only where it points.
    /// </summary>
    public bool Move(string fromRelativePath, LibraryTrack track)
    {
        using (_gate.Take())
            return MoveLocked(fromRelativePath, track);
    }

    private bool MoveLocked(string fromRelativePath, LibraryTrack track)
    {
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                $"""
                UPDATE tracks SET
                    -- Read against the row's old size and date: SQLite evaluates every SET
                    -- expression on the row as it was before this statement.
                    {CloudRefusalsClearWhenTheFileChanges},
                    relative_path = $path, folder = $folder, file_name = $name, extension = $ext,
                    size_bytes = $size, modified_utc_ticks = $mtime, duration_seconds = $dur,
                    title = $title, artist = $artist, album_artist = $albumartist, album = $album,
                    genre = $genre, subgenre = $subgenre, advisory = $advisory, track_number = $track, disc_number = $disc,
                    disc_count = $disccount, disc_subtitle = $discsub, work = $work,
                    movement_name = $movement, movement_no = $movementno, composer = $composer,
                    year = $year,
                    -- What kind of release the record is. The album editor sets it, and an edit is a
                    -- move, so a move carries it.
                    release_type = $releasetype,
                    channels = $chan, sample_rate = $rate, bits_per_sample = $bits,
                    bitrate_kbps = $kbps, codec_description = $codec,
                    -- A move is the same file somewhere else: its loudness did not change. Kept
                    -- unless the caller has something better to say, which it only does when the
                    -- file carries a ReplayGain tag of its own.
                    loudness_lufs = COALESCE($lufs, loudness_lufs),
                    true_peak = COALESCE($peak, true_peak),
                    rpm_id = $gid
                WHERE relative_path = $from
                """;
            BindTrack(cmd, track);
            cmd.Parameters.AddWithValue("$from", fromRelativePath);
            if (cmd.ExecuteNonQuery() == 0)
                return false;
        }

        {
            // The listening log is keyed by the path too. If a re-file left it behind, every play of
            // the track would be lost when somebody fixed a spelling. That is the same loss this whole
            // method exists to prevent, in a table that cannot be rebuilt from the files.
            using var listens = _connection.CreateCommand();
            listens.CommandText = "UPDATE listen SET relative_path = $path WHERE relative_path = $from";
            listens.Parameters.AddWithValue("$path", track.RelativePath);
            listens.Parameters.AddWithValue("$from", fromRelativePath);
            listens.ExecuteNonQuery();
        }

        return true;
    }

    /// <summary>
    /// Points a set of rows at where their files ended up, keeping every row, and so every id,
    /// every play count and every joined date, as it was.
    ///
    /// <para>This is what a retag is. It is not a delete and an insert. Everything that a row knows
    /// and the file does not say (when it joined the library, how often it has been played) lives
    /// only here, and remove-then-add would discard all of it every time somebody fixes a spelling.
    /// A path that has no row (a file the mirror had not seen) falls back to an insert, which is the
    /// one case where there is nothing to keep.</para>
    /// </summary>
    /// <param name="moves">Old relative path, and the row as it is now.</param>
    public void MoveMany(IEnumerable<(string From, LibraryTrack Track)> moves)
    {
        ArgumentNullException.ThrowIfNull(moves);

        using (_gate.Take())
        {
            using var tx = _connection.BeginTransaction();
            foreach (var (from, track) in moves)
                if (!MoveLocked(from, track))
                    UpsertLocked(track);
            tx.Commit();
        }
    }

    private static void BindTrack(SqliteCommand cmd, LibraryTrack t)
    {
        cmd.Parameters.AddWithValue("$path", t.RelativePath);
        cmd.Parameters.AddWithValue("$folder", t.Folder);
        cmd.Parameters.AddWithValue("$name", t.FileName);
        cmd.Parameters.AddWithValue("$ext", t.Extension);
        cmd.Parameters.AddWithValue("$size", t.SizeBytes);
        cmd.Parameters.AddWithValue("$mtime", t.ModifiedUtcTicks);
        cmd.Parameters.AddWithValue("$dur", t.DurationSeconds);
        cmd.Parameters.AddWithValue("$title", (object?)t.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$artist", (object?)t.Artist ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$albumartist", (object?)t.AlbumArtist ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$album", (object?)t.Album ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$genre", (object?)t.Genre ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$subgenre", (object?)t.Subgenre ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$advisory", t.Advisory is { } a ? (int)a : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$chan", (object?)t.Channels ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rate", (object?)t.SampleRate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$bits", (object?)t.BitsPerSample ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$kbps", (object?)t.BitrateKbps ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$codec", (object?)t.CodecDescription ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lufs", (object?)t.LoudnessLufs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$peak", (object?)t.TruePeak ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tagbpm", t.TagBpm);
        cmd.Parameters.AddWithValue("$tagkey", (object?)t.TagKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$releasetype",
            t.ReleaseType is { } releaseType ? ReleaseTypes.ToStored(releaseType) : DBNull.Value);
        cmd.Parameters.AddWithValue("$track", t.TrackNumber);
        cmd.Parameters.AddWithValue("$disc", t.DiscNumber);
        cmd.Parameters.AddWithValue("$disccount", t.DiscCount);
        cmd.Parameters.AddWithValue("$discsub", (object?)t.DiscSubtitle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$work", (object?)t.Work ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$movement", (object?)t.MovementName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$movementno", (object?)t.MovementNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$composer", (object?)t.Composer ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$year", t.Year);
        cmd.Parameters.AddWithValue("$gid", (object?)t.RpmId ?? DBNull.Value);
        // Bound for the INSERT half of the upsert. Move's statement names none of these, and
        // SQLite ignores parameters that a statement does not use.
        cmd.Parameters.AddWithValue("$hash", (object?)t.ContentHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cstate", (int)t.CloudState);
        cmd.Parameters.AddWithValue("$csel", t.CloudSelected ? 1 : 0);
        cmd.Parameters.AddWithValue("$cpin", t.CloudPinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$csize", t.CanonicalSizeBytes);
        cmd.Parameters.AddWithValue("$cticks", t.CloudSyncedUtcTicks);
        cmd.Parameters.AddWithValue("$cerr", (object?)t.CloudError ?? DBNull.Value);
    }

    public void Remove(string relativePath)
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM tracks WHERE relative_path = $path";
            cmd.Parameters.AddWithValue("$path", relativePath);
            cmd.ExecuteNonQuery();
        }
    }

    public void RemoveMany(IEnumerable<string> relativePaths)
    {
        using (_gate.Take())
        {
            using var tx = _connection.BeginTransaction();
            foreach (var path in relativePaths)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "DELETE FROM tracks WHERE relative_path = $path";
                cmd.Parameters.AddWithValue("$path", path);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>
    /// All tracks matching <paramref name="search"/> (substring, case-insensitive, over
    /// file name / artist / title / album / album artist), ordered by relative path so the
    /// tree builds in folder order. A null or empty search returns everything.
    /// </summary>
    public IReadOnlyList<LibraryTrack> Query(string? search = null)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText = $"SELECT * FROM tracks {SearchWhere(cmd, search)} ORDER BY relative_path";

            using var reader = cmd.ExecuteReader();
            var results = new List<LibraryTrack>();
            while (reader.Read())
                results.Add(ReadTrack(reader));
            return results;
        }
    }

    /// <summary>One row by its path, or null when the mirror does not have it. An edit needs this
    /// when the file it would normally read is not on this disk (a track that lives in the cloud).</summary>
    public LibraryTrack? FindByRelativePath(string relativePath)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText = "SELECT * FROM tracks WHERE relative_path = $path LIMIT 1";
            cmd.Parameters.AddWithValue("$path", relativePath);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadTrack(reader) : null;
        }
    }

    /// <summary>
    /// How long the whole collection plays, in seconds, under the same search as the rest of the
    /// library. A single SUM over the column, so it costs what a COUNT costs. Tracks that have never
    /// been read carry 0 and add nothing.
    /// </summary>
    public double QueryTotalDurationSeconds(string? search = null)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText = $"SELECT COALESCE(SUM(duration_seconds), 0) FROM tracks {SearchWhere(cmd, search)}";
            var value = cmd.ExecuteScalar();
            return value is null or DBNull ? 0 : Convert.ToDouble(value);
        }
    }

    /// <summary>
    /// Per-folder track counts (direct tracks only, not descendants), optionally
    /// restricted to tracks matching <paramref name="search"/>. This is the skeleton that the
    /// virtualised library tree is built from. One row per distinct folder, in folder
    /// order; the library root is the "" folder. Runs as a single indexed GROUP BY,
    /// so it stays fast when the grid is rebuilt on each search keystroke.
    /// </summary>
    public IReadOnlyList<(string Folder, int TrackCount)> QueryFolderTrackCounts(string? search = null)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                $"""
                SELECT folder, COUNT(*) FROM tracks
                {SearchWhere(cmd, search)}
                GROUP BY folder ORDER BY folder
                """;
            using var reader = cmd.ExecuteReader();
            var results = new List<(string, int)>();
            while (reader.Read())
                results.Add((reader.GetString(0), reader.GetInt32(1)));
            return results;
        }
    }

    /// <summary>
    /// How long each album folder plays for, at the given search. This is the running-time half of
    /// <see cref="QueryFolderTrackCounts"/>.
    ///
    /// <para>Read on the same pass as the counts, because a library that can be narrowed to some of
    /// its records (the favourites filter) has to state what THOSE records add up to. Summing
    /// the whole table would state what the collection adds up to, which is not what is on screen.</para>
    /// </summary>
    public IReadOnlyList<(string Folder, double Seconds)> QueryFolderDurations(string? search = null)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                $"""
                SELECT folder, COALESCE(SUM(duration_seconds), 0) FROM tracks
                {SearchWhere(cmd, search)}
                GROUP BY folder ORDER BY folder
                """;
            using var reader = cmd.ExecuteReader();
            var results = new List<(string, double)>();
            while (reader.Read())
                results.Add((reader.GetString(0), Convert.ToDouble(reader.GetValue(1))));
            return results;
        }
    }

    /// <summary>
    /// Stamps a row with what its file became, for a write that changed the file and nothing that
    /// the mirror holds.
    ///
    /// <para>There is exactly one such write: putting the measured tempo and key into a track's tags
    /// (see <see cref="Analysis.AnalysisTags"/>). Neither is a column here, so the row is still true,
    /// but the file's size and mtime have changed, and <see cref="LibrarySynchronizer"/> compares
    /// those to decide that a file needs reading again. Without this stamp, a tagging pass over a
    /// collection would make the next sync read the whole collection again, one file open per track,
    /// for nothing.</para>
    ///
    /// <para>Do not use it for a write that changes anything the mirror mirrors: this claims that the
    /// row is current, and sync will believe it.</para>
    ///
    /// <para>The cloud's refusals clear with the change, as they do on a scan, because the next scan
    /// will not see a difference to clear them for.</para>
    /// </summary>
    public void NoteFileStamp(string relativePath, long sizeBytes, long modifiedUtcTicks)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                $"""
                UPDATE tracks SET
                    {CloudRefusalsClearWhenTheFileChanges},
                    size_bytes = $size, modified_utc_ticks = $mtime
                WHERE relative_path = $path
                """;
            cmd.Parameters.AddWithValue("$path", relativePath);
            cmd.Parameters.AddWithValue("$size", sizeBytes);
            cmd.Parameters.AddWithValue("$mtime", modifiedUtcTicks);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Everything the album wall needs to lay itself out, per album folder: what the record is called,
    /// when it joined the library, how much it has been listened to and when it was last put on.
    ///
    /// <para>The last of those is for the home view and not the wall (see RPM.Core.Library.HomeShelf).
    /// It is read HERE and not by a query of its own because it is the same GROUP BY over the same
    /// rows: a second walk of a million tracks to fetch one more aggregate per folder would be
    /// wasted work.</para>
    ///
    /// <para>The year is NOT here. A record's year is the one that most of its tracks agree on, which
    /// is a count per year and not an aggregate (see <see cref="QueryAlbumYearCounts"/> and
    /// <see cref="Cloud.AlbumSort.YearFrom"/>).</para>
    /// </summary>
    /// <param name="AddedAt">The NEWEST of the album's tracks: a record is added over the minutes its
    /// files take to copy in, and one that is still arriving must not sort below one that finished
    /// yesterday. It also gives the right result for a record completed later.</param>
    /// <param name="Plays">The album's tracks added up. A record played through counts as many plays
    /// as it has tracks, so the order reads as most listened to and not as most owned.</param>
    /// <param name="LastPlayedAt">The most recent of the album's tracks, and zero for a record
    /// that nobody here has played. The NEWEST and not the oldest, for the same reason as
    /// <paramref name="AddedAt"/>: a record is listened to over its own length, and one that is still
    /// playing must not sort below one finished an hour ago.</param>
    public readonly record struct AlbumFactsRow(
        string Folder, string? Album, string? AlbumArtist, long AddedAt, int Plays, long LastPlayedAt);

    public IReadOnlyList<AlbumFactsRow> QueryAlbumFacts(string? search = null)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                $"""
                SELECT folder,
                       MIN(NULLIF(album, '')),
                       -- Album artist falls back to the track artist, the same rule the naming
                       -- template applies when it builds the folder this row is grouped by.
                       MIN(NULLIF(COALESCE(NULLIF(album_artist, ''), artist), '')),
                       MAX(added_utc_ticks),
                       SUM(plays),
                       MAX(last_played_utc_ticks)
                FROM tracks
                {SearchWhere(cmd, search)}
                GROUP BY folder ORDER BY folder
                """;
            using var reader = cmd.ExecuteReader();
            var results = new List<AlbumFactsRow>();
            while (reader.Read())
                results.Add(new AlbumFactsRow(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                    reader.IsDBNull(4) ? 0 : (int)reader.GetInt64(4),
                    reader.IsDBNull(5) ? 0 : reader.GetInt64(5)));
            return results;
        }
    }

    /// <summary>
    /// How many albums the collection holds, counted as distinct track folders.
    ///
    /// <para><b>A count and not <see cref="QueryAlbumFacts"/>.Count.</b> Two callers ask this at
    /// startup, before anything is on screen: <see cref="OpeningScreen.Choose"/>, which decides whether
    /// the wizard or the library opens, and the home screen's gate. Building a row per album to count
    /// the rows would walk a million tracks and allocate the whole library to answer with one
    /// integer, on the path where nothing is drawn yet.</para>
    ///
    /// <para>Grouped in the same way as the facts, so the two never disagree about what an album
    /// is.</para>
    /// </summary>
    public int CountAlbumFolders(TrackScope place)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(DISTINCT folder) FROM tracks{Where(place)}";
            return cmd.ExecuteScalar() is long n ? (int)n : 0;
        }
    }

    /// <summary>
    /// How many tracks the collection holds. The setup wizard prints this beside the album
    /// count when it finishes, and the measuring pass reads its progress against it.
    /// </summary>
    public int CountTracks(TrackScope place)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM tracks{Where(place)}";
            return cmd.ExecuteScalar() is long n ? (int)n : 0;
        }
    }

    /// <summary>
    /// How far the measuring pass has got through the collection.
    /// </summary>
    /// <remarks>
    /// <para><b>Counted live, and not from <see cref="SessionMoods"/>.</b> That cut holds the number
    /// it was built with for the life of the process, on purpose, so that a list does not change its
    /// contents under a listener during a session. Read as progress, it would not move: it would show
    /// "555 of 629" for a pass that finished hours earlier, until the app is restarted.
    /// </para>
    ///
    /// <para><b>Over the tracks the pass can actually reach.</b> A cloud-only row has no file on this
    /// disk, so nothing will ever measure it. Counting it in the denominator makes a pass that can
    /// never finish: 73 such rows in a collection of 629 hold the fraction at 88%, under the
    /// nine tenths that <see cref="MeasuringProgress.Enough"/> asks for, and the mood lists would
    /// keep blaming a pass that is done. Pass <see cref="TrackScope.Anywhere"/> and that is what
    /// comes back.</para>
    ///
    /// <para>Both halves come from one scan, so the two cannot come from different moments and report
    /// more measured than there are.</para>
    /// </remarks>
    public MeasuringProgress MeasuredSoFar(TrackScope place)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*), COALESCE(SUM(mood_brightness IS NOT NULL), 0) FROM tracks"
                + Where(place);

            using var reader = cmd.ExecuteReader();
            return reader.Read()
                ? new MeasuringProgress((int)reader.GetInt64(1), (int)reader.GetInt64(0))
                : default;
        }
    }

    /// <summary>The scope's own clause, without the WHERE, or null where the scope is every track.
    /// It is in one place, so a count and a narrowed count cannot disagree about what "on this
    /// computer" means.</summary>
    private static string? Scope(TrackScope place) => place switch
    {
        TrackScope.Anywhere => null,
        TrackScope.OnThisComputer =>
            $"cloud_state <> {(int)Cloud.CloudState.CloudOnly} AND peer_adopted = 0",
        _ => throw new ArgumentOutOfRangeException(nameof(place)),
    };

    private static string Where(TrackScope place) => Scope(place) is { } clause ? $" WHERE {clause}" : "";

    /// <summary>
    /// Every album folder that holds at least one explicit track. This puts the E beside a
    /// record's title. It is any track and not most of them: a record with one explicit song is one
    /// that a listener wants to know about, and the majority rule that the year and genre use would
    /// hide it.
    /// </summary>
    public IReadOnlyList<string> QueryExplicitAlbums(string? search = null)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            var where = SearchWhere(cmd, search);
            cmd.CommandText =
                $"""
                SELECT DISTINCT folder FROM tracks
                {(where.Length == 0 ? "WHERE" : where + " AND")} advisory = {(int)ContentAdvisory.Explicit}
                """;
            using var reader = cmd.ExecuteReader();
            var folders = new List<string>();
            while (reader.Read())
                folders.Add(reader.GetString(0));
            return folders;
        }
    }

    /// <summary>
    /// How many tracks in each album folder carry each year. The "most of its tracks agree on" rule
    /// is decided from this. There are at most a few rows per album (a record has one year, a
    /// compilation a few), so this stays a GROUP BY and is not a walk of a million track rows.
    /// </summary>
    public IReadOnlyList<(string Folder, int Year, int Count)> QueryAlbumYearCounts(string? search = null)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            var where = SearchWhere(cmd, search);
            cmd.CommandText =
                $"""
                SELECT folder, year, COUNT(*) FROM tracks
                {(where.Length == 0 ? "WHERE" : where + " AND")} year > 0
                GROUP BY folder, year
                """;
            using var reader = cmd.ExecuteReader();
            var results = new List<(string, int, int)>();
            while (reader.Read())
                results.Add((reader.GetString(0), (int)reader.GetInt64(1), (int)reader.GetInt64(2)));
            return results;
        }
    }

    /// <summary>
    /// How many tracks in each album folder carry each genre. It has the same shape, and exists for
    /// the same reason, as <see cref="QueryAlbumYearCounts"/>: a record's genre is what most of its
    /// tracks agree on, and a compilation with five genres has no single answer to give.
    /// </summary>
    public IReadOnlyList<(string Folder, string Genre, int Count)> QueryAlbumGenreCounts(string? search = null)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            var where = SearchWhere(cmd, search);
            cmd.CommandText =
                $"""
                SELECT folder, genre, COUNT(*) FROM tracks
                {(where.Length == 0 ? "WHERE" : where + " AND")} genre IS NOT NULL AND genre <> ''
                GROUP BY folder, genre
                """;
            using var reader = cmd.ExecuteReader();
            var results = new List<(string, string, int)>();
            while (reader.Read())
                results.Add((reader.GetString(0), reader.GetString(1), (int)reader.GetInt64(2)));
            return results;
        }
    }

    /// <summary>
    /// Who is named on each album folder: the record's own artist beside each distinct track credit on
    /// it, with the tracks that pairing covers. The artists wall is built from this
    /// (<see cref="ArtistWall.Build"/>).
    ///
    /// <para>Grouped and not walked, and grouped on the pair and not on either column alone. A guest
    /// credit is a property of the TRACK and the record it is on together: "1 album appears in 2
    /// artist listings" needs both halves in one row, and asking for the two columns separately
    /// would give a list of names with no record attached to any of them.</para>
    ///
    /// <para>There are a few rows per album (a record has one artist and a handful of guests), so this
    /// costs what the year and genre counts cost and not what a million track rows would.</para>
    /// </summary>
    public IReadOnlyList<ArtistCreditRow> QueryArtistCredits(string? search = null)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                $"""
                SELECT folder,
                       -- The same fallback the album facts apply: a record with nothing in its album
                       -- artist field is filed under whatever its tracks are credited to.
                       NULLIF(COALESCE(NULLIF(album_artist, ''), artist), ''),
                       NULLIF(artist, ''),
                       COUNT(*)
                FROM tracks
                {SearchWhere(cmd, search)}
                GROUP BY folder, album_artist, artist
                """;
            using var reader = cmd.ExecuteReader();
            var results = new List<ArtistCreditRow>();
            while (reader.Read())
                results.Add(new ArtistCreditRow(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    (int)reader.GetInt64(3)));
            return results;
        }
    }

    /// <summary>
    /// The lines in an album's tracklist that are NOT tracks: a divider for each disc of a multi-disc
    /// set and, on a record that somebody has marked classical, a header for each work on it.
    ///
    /// <para>Asked before a single track has been read, because the album list reserves room from it.
    /// Reserving room from the track count alone leaves these records short, and a block that is
    /// short GROWS when its tracks arrive, which moves every record below it while the list is being
    /// scrolled.</para>
    ///
    /// <para>Works are counted only where the record is marked classical: the tracklist draws work
    /// headers nowhere else, and a rock album whose tagger filled in the work field would otherwise
    /// reserve room for headers that never appear.</para>
    /// </summary>
    /// <param name="Discs">Distinct disc numbers, whatever their count. One disc draws no divider, and
    /// the caller is what knows that (see AlbumListEntry).</param>
    public readonly record struct AlbumHeaderLinesRow(string Folder, int Discs, int Works);

    public IReadOnlyList<AlbumHeaderLinesRow> QueryAlbumHeaderLines(string? search = null)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                $"""
                SELECT t.folder,
                       COUNT(DISTINCT CASE WHEN t.disc_number > 0 THEN t.disc_number END),
                       COUNT(DISTINCT CASE WHEN COALESCE(m.classical, 0) = 1
                                           THEN NULLIF(t.work, '') END)
                FROM tracks t
                LEFT JOIN album_meta m ON m.folder = t.folder
                {SearchWhere(cmd, search)}
                GROUP BY t.folder
                """;
            using var reader = cmd.ExecuteReader();
            var results = new List<AlbumHeaderLinesRow>();
            while (reader.Read())
                results.Add(new AlbumHeaderLinesRow(
                    reader.GetString(0), (int)reader.GetInt64(1), (int)reader.GetInt64(2)));
            return results;
        }
    }

    /// <summary>
    /// One window of a folder's tracks (direct children of <paramref name="folder"/>,
    /// "" = the library root), in file-name order, optionally restricted by
    /// <paramref name="search"/>. This is the page fetch behind the virtualised tree grid.
    /// </summary>
    public IReadOnlyList<LibraryTrack> QueryFolderPage(string folder, int offset, int limit, string? search = null)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            var where = SearchWhere(cmd, search);
            cmd.CommandText =
                $"""
                SELECT * FROM tracks
                {(where.Length == 0 ? "WHERE" : where + " AND")} folder = $folder
                ORDER BY file_name LIMIT $limit OFFSET $offset
                """;
            cmd.Parameters.AddWithValue("$folder", folder);
            cmd.Parameters.AddWithValue("$limit", limit);
            cmd.Parameters.AddWithValue("$offset", offset);
            using var reader = cmd.ExecuteReader();
            var results = new List<LibraryTrack>();
            while (reader.Read())
                results.Add(ReadTrack(reader));
            return results;
        }
    }

    /// <summary>
    /// Every track under <paramref name="folder"/> (itself and all descendants;
    /// "" = the whole library), in relative-path order. This is what a folder selection in
    /// the grid stands for when an action needs its tracks.
    /// </summary>
    public IReadOnlyList<LibraryTrack> QueryUnderFolder(string folder, string? search = null)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            var where = SearchWhere(cmd, search);
            if (folder.Length == 0)
            {
                cmd.CommandText = $"SELECT * FROM tracks {where} ORDER BY relative_path";
            }
            else
            {
                cmd.CommandText =
                    $"""
                    SELECT * FROM tracks
                    {(where.Length == 0 ? "WHERE" : where + " AND")}
                        (folder = $folder OR folder LIKE $prefix ESCAPE '\')
                    ORDER BY relative_path
                    """;
                cmd.Parameters.AddWithValue("$folder", folder);
                cmd.Parameters.AddWithValue("$prefix", EscapeLike(folder) + "/%");
            }
            using var reader = cmd.ExecuteReader();
            var results = new List<LibraryTrack>();
            while (reader.Read())
                results.Add(ReadTrack(reader));
            return results;
        }
    }

    /// <summary>
    /// The library folder an album already lives in, or null when it is not in the library yet. This
    /// lets a newly ripped disc of a multi-disc set be filed alongside the discs already imported.
    /// It matches on the album name with disc decoration removed
    /// (<see cref="Analysis.AlbumResolver.NormalizeAlbum"/>) plus album artist, and returns the
    /// folder that holds the most matching tracks.
    /// </summary>
    public string? FindAlbumFolder(string album, string? albumArtist)
    {
        if (string.IsNullOrWhiteSpace(album))
            return null;

        var norm = Analysis.AlbumResolver.NormalizeAlbum(album);
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            // Narrow with SQL (exact, or "Album%" to catch "Album (Disc 1)"), then confirm the
            // normalised match in code, because SQLite cannot run NormalizeAlbum.
            cmd.CommandText =
                """
                SELECT folder, album, album_artist FROM tracks
                WHERE album IS NOT NULL
                  AND (album = $album COLLATE NOCASE OR album LIKE $like ESCAPE '\')
                """;
            cmd.Parameters.AddWithValue("$album", norm);
            cmd.Parameters.AddWithValue("$like", EscapeLike(norm) + "%");

            using var reader = cmd.ExecuteReader();
            var byFolder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                var folder = reader.GetString(0);
                var alb = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var aa = reader.IsDBNull(2) ? null : reader.GetString(2);

                if (!string.Equals(Analysis.AlbumResolver.NormalizeAlbum(alb), norm, StringComparison.OrdinalIgnoreCase))
                    continue;
                // An album-artist mismatch rules it out. A missing one on either side does not.
                if (!string.IsNullOrWhiteSpace(albumArtist) && !string.IsNullOrWhiteSpace(aa)
                    && !string.Equals(aa, albumArtist, StringComparison.OrdinalIgnoreCase))
                    continue;

                byFolder[folder] = byFolder.GetValueOrDefault(folder) + 1;
            }

            return byFolder.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
        }
    }

    /// <summary>
    /// Every artist name the collection actually uses (track artists and album artists together),
    /// with how many tracks are filed under each, most used first. The spelling that the library has
    /// settled on is the one that the most files carry, so this is the input to
    /// <see cref="ArtistCanonicalizer"/>: a new import that says "guns n roses" is asking a question
    /// that the library has already answered.
    /// </summary>
    public IReadOnlyList<(string Name, int Tracks)> ArtistNames()
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            // Both columns, because both name folders: a compilation is filed under its album artist
            // and its tracks still carry their own performers, and a misspelling in either is one
            // that the user sees.
            cmd.CommandText =
                """
                SELECT name, SUM(n) AS total FROM (
                    SELECT artist AS name, COUNT(*) AS n FROM tracks
                    WHERE artist IS NOT NULL AND trim(artist) <> '' GROUP BY artist
                    UNION ALL
                    SELECT album_artist AS name, COUNT(*) AS n FROM tracks
                    WHERE album_artist IS NOT NULL AND trim(album_artist) <> '' GROUP BY album_artist
                )
                GROUP BY name
                ORDER BY total DESC, name
                """;

            using var reader = cmd.ExecuteReader();
            var names = new List<(string, int)>();
            while (reader.Read())
                names.Add((reader.GetString(0), reader.GetInt32(1)));
            return names;
        }
    }

    /// <summary>
    /// Show only what the cloud holds. Otherwise the whole of this mirror is unreadable.
    ///
    /// <para>
    /// This is for a borrowed library whose computer is not on this network. The rule for sharing is
    /// that a record on somebody's own disk plays where that disk can be reached and nowhere
    /// else, while a record they put in the cloud plays anywhere. A library that keeps showing the
    /// first kind after the owner has gone home is a wall of records that hang and then do not play.
    /// </para>
    ///
    /// <para>Nothing is deleted and nothing is rewritten: the rows stay as they were and come
    /// back when that computer does. It is set on the mirror and not at each call site, because
    /// every way of reading a collection (the wall, the list, a folder, a search, the running time)
    /// goes through here, and if one of them forgot it, a record would play from a library that
    /// says it cannot.</para>
    /// </summary>
    public bool CloudRowsOnly { get; set; }

    /// <summary>
    /// Show only what has a file on this disk, because there is no account to stream the rest with.
    ///
    /// <para>This is for the user's OWN mirror while nobody is signed in. It is the opposite of
    /// <see cref="CloudRowsOnly"/> and the same argument applies. A record whose file the user deleted
    /// is still in the store, and this row is the only thing that knows it, so the row STAYS:
    /// dropping it would lose the last record of where the music went. What it must not do is draw
    /// the record on the wall, where it is a sleeve that plays nothing.</para>
    ///
    /// <para>The rows that another install put here are a different case and are dropped outright on
    /// sign-out (see <see cref="ForgetAdopted"/>): they are the cloud's record and not this disk's,
    /// and the next person to sign in must not be shown the last one's collection. What this hides is
    /// what is left after that, which is this machine's own rows. They must survive the sign-out.</para>
    ///
    /// <para>It is set on the mirror and not at each call site, for the reason given at
    /// <see cref="CloudRowsOnly"/>: every way of reading a collection goes through the same few
    /// clauses, and if one of them forgot it, the wall would show a record that plays nothing.</para>
    /// </summary>
    public bool LocalRowsOnly { get; set; }

    /// <summary>
    /// Which rows this mirror may show at all, as a SQL term, or "" when it may show all of them.
    ///
    /// <para>One expression for both narrowings, so that a read cannot honour one and forget the other.
    /// When both are set, the answer is nothing, which is correct: a borrowed library whose owner has
    /// gone home has only its cloud rows, and a signed-out session cannot play those.</para>
    /// </summary>
    /// <param name="column">How <c>cloud_state</c> has to be written in this statement: bare, or
    /// qualified by its table where a join makes the bare name ambiguous. Every caller states it
    /// and there is no default, because SQLite does not object to the ambiguous name until the day
    /// the other table gains a column of the same name.</param>
    private string ReachTerm(string column)
    {
        var terms = new List<string>(2);
        if (CloudRowsOnly) terms.Add($"{column} = {(int)CloudState.CloudOnly}");
        if (LocalRowsOnly) terms.Add($"{column} <> {(int)CloudState.CloudOnly}");
        return terms.Count switch
        {
            0 => "",
            1 => terms[0],
            _ => "(" + string.Join(" AND ", terms) + ")",
        };
    }

    /// <summary>The same rule asked about one row and not about a statement, for the reads that
    /// walk rows and do not filter them. It must agree with <see cref="ReachTerm"/>; a test
    /// holds the two together.</summary>
    private bool Reaches(CloudState state) =>
        (!CloudRowsOnly || state == CloudState.CloudOnly)
        && (!LocalRowsOnly || state != CloudState.CloudOnly);

    /// <summary>Shared search predicate: substring match over the same columns
    /// <see cref="Query"/> uses. Empty string when there is no search; otherwise a
    /// WHERE clause with the $q parameter added to <paramref name="cmd"/>.</summary>
    private string SearchWhere(SqliteCommand cmd, string? search)
    {
        var reachable = ReachTerm("cloud_state");

        if (string.IsNullOrWhiteSpace(search))
            return reachable.Length == 0 ? "" : $"WHERE {reachable}";

        cmd.Parameters.AddWithValue("$q", "%" + EscapeLike(search.Trim()) + "%");

        var matches = """
                      (file_name LIKE $q ESCAPE '\' OR artist LIKE $q ESCAPE '\'
                        OR title LIKE $q ESCAPE '\' OR album LIKE $q ESCAPE '\'
                        OR album_artist LIKE $q ESCAPE '\')
                      """;

        return reachable.Length == 0
            ? $"WHERE {matches}"
            : $"WHERE {reachable} AND {matches}";
    }

    /// <summary>Relative path to (size, mtime, RPM id, cloud state) for the whole mirror. This is the
    /// sync diff input. The id lets sync tell a moved file from a deleted-plus-added one,
    /// and the cloud state lets it tell an offloaded file from a deleted one.</summary>
    public Dictionary<string, SnapshotRow> Snapshot()
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                """
                SELECT relative_path, id, size_bytes, modified_utc_ticks, rpm_id, cloud_state,
                       channels, advisory, movement_no, peer_adopted
                FROM tracks
                """;
            using var reader = cmd.ExecuteReader();
            var map = new Dictionary<string, SnapshotRow>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
                map[reader.GetString(0)] = new SnapshotRow(
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    (CloudState)reader.GetInt64(5),
                    reader.IsDBNull(6) ? null : (int)reader.GetInt64(6),
                    reader.IsDBNull(7) ? null : (int)reader.GetInt64(7),
                    reader.IsDBNull(8) ? null : (int)reader.GetInt64(8),
                    reader.GetInt64(9) != 0);
            return map;
        }
    }

    /// <summary>Sets a track's cloud state, and the failure reason with it (null clears).</summary>
    public void SetCloudState(long trackId, CloudState state, string? error = null)
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE tracks SET cloud_state = $s, cloud_error = $e WHERE id = $id";
            cmd.Parameters.AddWithValue("$s", (int)state);
            cmd.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", trackId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// The upload landed and the server confirmed it: the track now exists in both places.
    /// Records the hash it was stored under, so that a later pass can tell a retagged file (same
    /// canonical audio, new sidecar) from a file that was encoded again (new blob).
    /// </summary>
    /// <remarks>
    /// Only while the row is still Uploading, which the upload sets before it starts. Something that
    /// took the row out of the upload in the meantime decides what it is, and a late answer from the
    /// server does not overwrite that. After the account is deleted (<see cref="ForgetCloudAccount"/>)
    /// an upload that lands afterwards would otherwise mark a track Synced that no cloud holds, and
    /// the queue never promotes a Synced row again.
    /// </remarks>
    /// <returns>False when the row is no longer Uploading and nothing was written.</returns>
    public bool MarkSynced(long trackId, string contentHash, long canonicalSizeBytes, long syncedUtcTicks)
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                UPDATE tracks SET
                    cloud_state = $s, content_hash = $h, canonical_size_bytes = $csize,
                    cloud_synced_utc_ticks = $ticks, cloud_error = NULL
                WHERE id = $id AND cloud_state = $uploading
                """;
            cmd.Parameters.AddWithValue("$uploading", (int)CloudState.Uploading);
            cmd.Parameters.AddWithValue("$s", (int)CloudState.Synced);
            cmd.Parameters.AddWithValue("$h", contentHash);
            cmd.Parameters.AddWithValue("$csize", canonicalSizeBytes);
            cmd.Parameters.AddWithValue("$ticks", syncedUtcTicks);
            cmd.Parameters.AddWithValue("$id", trackId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// The track's metadata has been sent again: its sidecar is rewritten after the file was retagged.
    ///
    /// <para>
    /// Only the stamp changes. The audio is untouched (retagging does not change the canonical bytes,
    /// so there is no new blob and no new hash), and the state is already Synced. Writing either
    /// would claim something that did not happen. What this does record is that the file is no longer
    /// newer than what the cloud holds, which stops the next sync run from sending the same tags
    /// again.
    /// </para>
    /// </summary>
    public void NoteCloudMetadata(long trackId, long syncedUtcTicks)
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE tracks SET cloud_synced_utc_ticks = $ticks WHERE id = $id";
            cmd.Parameters.AddWithValue("$ticks", syncedUtcTicks);
            cmd.Parameters.AddWithValue("$id", trackId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// This machine has made the cloud's renditions for the track: the head clip and the free rate.
    ///
    /// <para>Separate from <see cref="MarkSynced"/> because the two are separate pieces of work that
    /// can happen years apart: a track uploaded before renditions existed is Synced with none, and a
    /// catch-up pass makes them later. It is recorded because encoding takes minutes of CPU time,
    /// and without a record the work could never be known to be finished. It is called only when the
    /// work succeeded. A failure leaves the row for the next run.</para>
    /// </summary>
    public void NoteRenditions(long trackId, long atUtcTicks)
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE tracks SET renditions_utc_ticks = $ticks WHERE id = $id";
            cmd.Parameters.AddWithValue("$ticks", atUtcTicks);
            cmd.Parameters.AddWithValue("$id", trackId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Writes down that this row's bytes are now in the cloud alone. The local file has been deleted
    /// and the cloud copy is the only one. The row keeps its <c>relative_path</c>, because that is
    /// where a restore puts the file back, and sync must leave it alone and not read the missing file
    /// as a deletion. Call this only after the upload was verified: it is the one operation here that
    /// destroys data if the cloud copy is not really there.
    ///
    /// <para>
    /// Returns whether it was written, and the caller must not delete anything if it was not. The two
    /// are one operation with a file on the other end of it: a row that says <c>Synced</c> with no
    /// file is a track that sync reads as deleted, and sync then removes the row, and with it the
    /// only pointer to the copy in the store, for good.
    /// </para>
    ///
    /// <para>Guarded on the state that means the store has it. A row that is already
    /// <c>CloudOnly</c> answers yes: two passes reaching the same track is ordinary, and the second
    /// one is right about where the bytes are.</para>
    /// </summary>
    /// <param name="rpmId">
    /// The track's own id, when the caller has it. It matters more than it looks.
    ///
    /// <para>A row id is this database's word for a row and it does not survive the row being
    /// replaced. A row IS replaced when sync reads its file as gone and then finds it again, which is
    /// what a file being moved into place during a running scan looks like. An upload that started
    /// minutes earlier then holds the old number, and every write it makes lands on nothing. That is
    /// how a delete gets made against a row that never learned it happened. The rpm id is the file's
    /// own name and it does not change.</para>
    /// </param>
    public bool MarkOffloaded(long trackId, string? rpmId = null)
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                UPDATE tracks SET cloud_state = $s
                WHERE cloud_state IN ($synced, $s)
                  AND (id = $id OR ($gid IS NOT NULL AND rpm_id = $gid))
                """;
            cmd.Parameters.AddWithValue("$s", (int)CloudState.CloudOnly);
            cmd.Parameters.AddWithValue("$synced", (int)CloudState.Synced);
            cmd.Parameters.AddWithValue("$id", trackId);
            cmd.Parameters.AddWithValue("$gid", (object?)rpmId ?? DBNull.Value);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// Opts tracks into (or out of) the cloud. This is what "selected music" mode acts on.
    /// </summary>
    /// <param name="pinned">
    /// Whether this is the user picking the tracks themselves. An album or a track says yes and the
    /// choice survives a playlist being taken out of the cloud. A playlist says no, because its tracks
    /// are up there only for as long as the playlist is. Un-selecting always clears the pin too: there
    /// is no such thing as a pinned track that is not in the cloud.
    /// </param>
    public void SetCloudSelected(IEnumerable<long> trackIds, bool selected, bool pinned = true)
    {
        using (_gate.Take())
        {
            using var tx = _connection.BeginTransaction();
            foreach (var id in trackIds)
            {
                using var cmd = _connection.CreateCommand();
                // A playlist must not unpin what an album pinned, so it only ever raises the flag.
                cmd.CommandText = selected
                    ? (pinned
                        ? "UPDATE tracks SET cloud_selected = 1, cloud_pinned = 1 WHERE id = $id"
                        : "UPDATE tracks SET cloud_selected = 1 WHERE id = $id")
                    : "UPDATE tracks SET cloud_selected = 0, cloud_pinned = 0 WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>
    /// Drops the user's own claim on these tracks and leaves them in the cloud. This is what "remove
    /// this album" does to a track that a synced playlist still wants. The album is no longer the
    /// reason the track is up there. The playlist is, and it is the one that takes it down.
    /// </summary>
    public void UnpinCloud(IEnumerable<long> trackIds)
    {
        using (_gate.Take())
        {
            using var tx = _connection.BeginTransaction();
            foreach (var id in trackIds)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "UPDATE tracks SET cloud_pinned = 0 WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>
    /// Forgets everything the cloud knows about one track in a single write: unselected, back to
    /// Local, and with the failure count and backoff of the upload that is no longer wanted cleared
    /// too. Otherwise a track removed after five failed attempts could never be added again.
    /// </summary>
    public void ForgetCloud(long trackId)
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                $"""
                UPDATE tracks SET
                    cloud_state = {(int)CloudState.Local}, cloud_selected = 0, cloud_pinned = 0,
                    cloud_error = NULL, cloud_attempts = 0, cloud_next_attempt_utc_ticks = 0
                WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$id", trackId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Tracks in a given cloud state, oldest row first. This is the upload worker's work list.</summary>
    public IReadOnlyList<LibraryTrack> QueryByCloudState(CloudState state, int limit = 500)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText = "SELECT * FROM tracks WHERE cloud_state = $s ORDER BY id LIMIT $limit";
            cmd.Parameters.AddWithValue("$s", (int)state);
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            var results = new List<LibraryTrack>();
            while (reader.Read())
                results.Add(ReadTrack(reader));
            return results;
        }
    }

    /// <summary>How many tracks are in a given cloud state. These are the progress counters, without the rows.</summary>
    public int CountByCloudState(CloudState state)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM tracks WHERE cloud_state = $s";
            cmd.Parameters.AddWithValue("$s", (int)state);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    /// <summary>
    /// The next tracks actually worth trying: queued, plus the ones that failed and have waited out
    /// their backoff. A track whose upload keeps failing must not be tried again on every pass,
    /// because it would hold back the rest of the queue behind a file that is never going to work.
    /// </summary>
    public IReadOnlyList<LibraryTrack> QueryDueForUpload(long nowUtcTicks, int limit = 50)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                $"""
                SELECT * FROM tracks
                WHERE cloud_state IN ({(int)CloudState.Queued}, {(int)CloudState.Error})
                  AND cloud_next_attempt_utc_ticks <= $now
                  AND cloud_attempts < {CloudRetrySchedule.MaxAttempts}
                ORDER BY cloud_state DESC, id
                LIMIT $limit
                """;
            // The attempt ceiling is in the WHERE and not applied afterwards. Error rows sort first,
            // so a page made entirely of tracks that had used up their attempts would read as an empty
            // queue, and the run would stop with every queued track still behind them.
            cmd.Parameters.AddWithValue("$now", nowUtcTicks);
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            var results = new List<LibraryTrack>();
            while (reader.Read())
                results.Add(ReadTrack(reader));
            return results;
        }
    }

    /// <summary>
    /// Records that the track is in the cloud but something about it could not be finished, and takes
    /// it off the pass that keeps trying; see <see cref="CloudTracksWithLocalFiles"/>.
    ///
    /// <para>The music is not touched. A track marked here is <see cref="CloudState.Synced"/> before
    /// and after, plays from the store, and counts as backed up everywhere that counts.</para>
    /// </summary>
    /// <param name="reason">Human-readable and already localised: it is shown to the user as it is.</param>
    public void MarkCloudPartial(long trackId, string reason)
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE tracks SET cloud_partial_error = $e WHERE id = $id";
            cmd.Parameters.AddWithValue("$e", reason);
            cmd.Parameters.AddWithValue("$id", trackId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Clears both of a track's cloud refusals and puts it back at the front of the queue. This is the
    /// Retry that the user presses, and the only way back for a failure that is marked never-again.
    ///
    /// <para>The attempt count is cleared with them. Keeping it would put the track back on the queue
    /// with its five failures already used, which is a retry that does not retry.</para>
    /// </summary>
    public void ClearCloudFailure(long trackId)
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                $"""
                UPDATE tracks SET
                    cloud_error = NULL, cloud_partial_error = NULL,
                    cloud_attempts = 0, cloud_next_attempt_utc_ticks = 0,
                    -- A track that never got up there goes back in the line; one that did keeps the
                    -- state it has, because its music is in the store and this was only its clip.
                    cloud_state = CASE WHEN cloud_state = {(int)CloudState.Error}
                                       THEN {(int)CloudState.Queued} ELSE cloud_state END
                WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$id", trackId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Records a failed upload and when it may be tried again.</summary>
    public void MarkCloudAttemptFailed(long trackId, string error, long nextAttemptUtcTicks)
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                UPDATE tracks SET
                    cloud_state = $s, cloud_error = $e, cloud_attempts = cloud_attempts + 1,
                    cloud_next_attempt_utc_ticks = $next
                WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$s", (int)CloudState.Error);
            cmd.Parameters.AddWithValue("$e", error);
            cmd.Parameters.AddWithValue("$next", nextAttemptUtcTicks);
            cmd.Parameters.AddWithValue("$id", trackId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Puts a track back in the upload queue without counting a failure, because its file was being
    /// written while the upload wanted to read it.
    ///
    /// <para>The row's error is cleared as well: whatever an earlier attempt said was about bytes that
    /// have since been rewritten, so the next refusal counts as a first one (see
    /// <see cref="CloudRetrySchedule.AfterFailure"/>). The attempt count stays, so a file that is
    /// written over and over keeps its backoff.</para>
    /// </summary>
    public void DeferCloudUpload(long trackId, long notBeforeUtcTicks)
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                $"""
                UPDATE tracks SET
                    cloud_state = {(int)CloudState.Queued}, cloud_error = NULL,
                    cloud_next_attempt_utc_ticks = $next
                WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$next", notBeforeUtcTicks);
            cmd.Parameters.AddWithValue("$id", trackId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// The earliest moment at which a track that is waiting out a backoff becomes due, or null when
    /// none is waiting. Tracks marked never-again and tracks out of attempts are not waiting for anything.
    /// </summary>
    public long? NextCloudRetryTicks(long nowUtcTicks)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                $"""
                SELECT MIN(cloud_next_attempt_utc_ticks) FROM tracks
                WHERE cloud_state IN ({(int)CloudState.Queued}, {(int)CloudState.Error})
                  AND cloud_attempts < {CloudRetrySchedule.MaxAttempts}
                  AND cloud_next_attempt_utc_ticks > $now
                  AND cloud_next_attempt_utc_ticks < $horizon
                """;
            cmd.Parameters.AddWithValue("$now", nowUtcTicks);
            // A date further out than a day is a never-again date, not a backoff.
            cmd.Parameters.AddWithValue("$horizon", nowUtcTicks + TimeSpan.FromDays(1).Ticks);
            return cmd.ExecuteScalar() is long next ? next : null;
        }
    }

    /// <summary>
    /// Gives a second attempt to every track that was refused for good on its first attempt.
    ///
    /// <para>A file that is still being written by the import looks to ffmpeg like a damaged one, and
    /// every such file converts later. So a refusal is final only when it repeats (see
    /// <see cref="CloudRetrySchedule.AfterFailure"/>), and a never-again date on a row with a single
    /// attempt can only come from an older rule that treated one refusal as final. Safe to run on
    /// every pass: once those rows are released, nothing writes that state again and this matches
    /// nothing.</para>
    /// </summary>
    /// <returns>How many tracks went back in the queue.</returns>
    public int ReleaseUnconfirmedRefusals(long nowUtcTicks)
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                $"""
                UPDATE tracks SET
                    cloud_state = {(int)CloudState.Queued}, cloud_next_attempt_utc_ticks = 0
                WHERE cloud_state = {(int)CloudState.Error}
                  AND cloud_attempts = 1
                  AND cloud_next_attempt_utc_ticks > $horizon
                """;
            // The row keeps its error and its one attempt: if the same refusal comes back, it is final.
            cmd.Parameters.AddWithValue("$horizon", nowUtcTicks + TimeSpan.FromDays(1).Ticks);
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Queues everything that the current sync mode says belongs in the cloud and that is not there
    /// yet. Returns how many were added. Tracks that are already up, already queued or already
    /// offloaded are left alone. A track that failed keeps its backoff and is not moved to the front
    /// of the queue.
    /// </summary>
    public int QueueForUpload(bool everything)
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                $"""
                UPDATE tracks SET cloud_state = {(int)CloudState.Queued}
                WHERE cloud_state = {(int)CloudState.Local}
                  {(everything ? "" : "AND cloud_selected = 1")}
                """;
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Puts tracks that were mid-upload when the app last stopped back in the queue.
    ///
    /// <para>
    /// <see cref="CloudState.Uploading"/> is written by the worker as it picks a track up and cleared
    /// by the same worker when it finishes, fails or is cancelled. All of those live in memory. If
    /// the app is killed between the two moments, the row keeps that state for ever: the queue only
    /// looks at Queued and Error, and <see cref="QueueForUpload"/> only promotes Local, so nothing
    /// would pick it up again. The result is one track that is silently never backed up, with its
    /// album showing the "going up" badge from then on.
    /// </para>
    ///
    /// <para>
    /// Safe only where no upload run is in flight, because a track that is being uploaded right now
    /// looks the same from here. The one caller is the start of a run, which is single and
    /// has touched nothing yet.
    /// </para>
    /// </summary>
    /// <returns>How many were reclaimed.</returns>
    public int ReclaimInterruptedUploads()
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                $"""
                UPDATE tracks SET cloud_state = {(int)CloudState.Queued}
                WHERE cloud_state = {(int)CloudState.Uploading}
                """;
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Takes tracks out of the upload queue and clears what the last attempt left behind, so that a
    /// queue built under one sync mode does not keep draining under the next one. Returns how many
    /// were taken out.
    ///
    /// <para>
    /// Only the queue is touched. A track whose bytes are already in the cloud keeps its state:
    /// changing a sync setting says what should go up from now on, never that something already up
    /// there should be deleted.
    /// </para>
    /// </summary>
    /// <param name="onlyUnselected">Leave hand-picked tracks in the queue. This is what the change from
    /// "all music" to "selected music" needs, where the picks are what should still go up.</param>
    public int Dequeue(bool onlyUnselected)
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                $"""
                UPDATE tracks SET
                    cloud_state = {(int)CloudState.Local},
                    cloud_error = NULL, cloud_attempts = 0, cloud_next_attempt_utc_ticks = 0
                WHERE cloud_state IN ({(int)CloudState.Queued}, {(int)CloudState.Uploading}, {(int)CloudState.Error})
                  {(onlyUnselected ? "AND cloud_selected = 0" : "")}
                """;
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Marks everything that the cloud already holds as chosen for it, and returns how many were adopted.
    ///
    /// <para>
    /// "All music" never asks the user to pick anything, so on the way back to "selected music" the
    /// files are up there while the picks are empty. Without this, the library would read as "nothing
    /// is chosen for the cloud" while a copy of every track sits in it, and un-picking an album would
    /// be un-picking something that was never picked.
    /// </para>
    /// </summary>
    public int SelectWhatIsInCloud()
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                $"""
                UPDATE tracks SET cloud_selected = 1
                WHERE cloud_selected = 0
                  AND cloud_state IN ({(int)CloudState.Synced}, {(int)CloudState.CloudOnly})
                """;
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Tracks that are in the cloud, still on this disk, and have no renditions on record, oldest
    /// first. They are used to make the head clips and free rate for a library that was uploaded
    /// before they existed.
    ///
    /// <para>The machine that holds the originals is by far the cheapest place to do this: it has
    /// ffmpeg, and the time is not paid for by the second.</para>
    ///
    /// <para>The <c>renditions_utc_ticks = 0</c> test is what makes this finish. Without it, the
    /// answer to "what still needs renditions" would be every synced track for ever, and each run of
    /// the app would encode and upload the same oldest ten again. <see cref="NoteRenditions"/> is
    /// what takes a row out of this list.</para>
    /// </summary>
    public IReadOnlyList<(long Id, string RpmId, string RelativePath, string? Title, string? Album,
        string? ContentHash)> CloudTracksWithLocalFiles(int limit)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();

            // Oldest synced first, so a library that is caught up stays caught up in the order
            // it was uploaded and does not walk the same rows every run.
            cmd.CommandText =
                $"""
                SELECT id, rpm_id, relative_path, title, album, content_hash
                FROM tracks
                WHERE cloud_state = {(int)CloudState.Synced}
                  AND rpm_id IS NOT NULL AND rpm_id <> ''
                  AND renditions_utc_ticks = 0
                  -- Asked once. A file the encoder has already refused is not going to encode on the
                  -- next run either, and leaving it here is what made one bad track re-read itself at
                  -- every launch for the life of the install. It comes back if its bytes change.
                  AND cloud_partial_error IS NULL
                ORDER BY cloud_synced_utc_ticks
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$limit", Math.Max(1, limit));

            using var reader = cmd.ExecuteReader();
            var rows = new List<(long, string, string, string?, string?, string?)>();
            while (reader.Read())
                rows.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5)));

            return rows;
        }
    }

    /// <summary>
    /// One track per album the cloud holds, for working out which sleeves are up there and which are
    /// missing. Only tracks whose file is still on disk: the picture has to be read from somewhere,
    /// and an offloaded row has no bytes left locally to read it from.
    /// </summary>
    /// <returns>
    /// Both artist fields as they are stored, the album title, and a relative path to read the cover
    /// from. Which of the two artists names the album is the decision of <c>AlbumArt.KeyForTrack</c>
    /// and not of this query. Deciding it here in SQL would be a second copy of the rule, and a repair
    /// that keyed an album differently from the upload would store the sleeve where nothing looks for it.
    /// </returns>
    public IReadOnlyList<(string? AlbumArtist, string? Artist, string? Album, string RelativePath)> CloudAlbumsWithLocalFiles()
        => CloudAlbumsIn(CloudState.Synced);

    /// <summary>
    /// The albums whose bytes are only in the cloud, one row each. They have no file to read a cover
    /// from, but they can still have one: a picture given to an offloaded record is written into its
    /// album folder as a sidecar, and the path here says which folder that is. The art repair reads it
    /// from there, so a sleeve put on a record that this machine does not hold still reaches the phone.
    /// </summary>
    public IReadOnlyList<(string? AlbumArtist, string? Artist, string? Album, string RelativePath)> OffloadedAlbums()
        => CloudAlbumsIn(CloudState.CloudOnly);

    /// <summary>
    /// The cloud albums in one state, <b>one row per folder</b>.
    ///
    /// <para>The folder is the album, not the tags. That is the rule the wall is drawn by
    /// (<see cref="Cloud.CloudAlbumGrouping"/>). Grouping on the tags would give one folder two
    /// album keys whenever it holds a track tagged differently from the rest: a stray album-artist
    /// spelling ("LP" and "LP*"), or a track deduplicated into another record's folder. Both keys
    /// would then take that folder's single cover.jpg as their own local copy and stamp it with their
    /// own store time, so each run would leave the other looking out of date and the two would swap
    /// pictures with the store for ever. <see cref="Cloud.AlbumArt.KeyForTrack"/> is what the rows are
    /// keyed by downstream, so one row per folder is one key per folder.</para>
    ///
    /// <para>The folder's tags are those of its majority: the (album artist, album) pair that most of
    /// its tracks carry, and within that the artist that most of them carry. Ties break on the text,
    /// and the file is the one with the lowest path, so the same folder resolves to the same album and
    /// the same file every run.</para>
    /// </summary>
    private IReadOnlyList<(string? AlbumArtist, string? Artist, string? Album, string RelativePath)> CloudAlbumsIn(
        CloudState state)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                $"""
                SELECT folder, album_artist, artist, album, MIN(relative_path), COUNT(*)
                FROM tracks
                WHERE cloud_state = {(int)state}
                GROUP BY folder, album_artist, artist, album
                """;
            using var reader = cmd.ExecuteReader();
            var tagged = new List<TaggedTracks>();
            while (reader.Read())
                tagged.Add(new TaggedTracks(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt32(5)));

            return
            [
                .. tagged
                    .GroupBy(t => t.Folder, StringComparer.OrdinalIgnoreCase)
                    .Select(MajorityAlbum)
            ];
        }
    }

    /// <summary>The tracks in one folder that carry one set of album tags, and the lowest path among
    /// them. <see cref="CloudAlbumsIn"/> reduces these to one album per folder.</summary>
    private sealed record TaggedTracks(
        string Folder, string? AlbumArtist, string? Artist, string? Album, string RelativePath, int Count);

    /// <summary>The album that a folder is, by count of tracks. See <see cref="CloudAlbumsIn"/>.</summary>
    private static (string? AlbumArtist, string? Artist, string? Album, string RelativePath) MajorityAlbum(
        IEnumerable<TaggedTracks> folder)
    {
        var album = folder
            .GroupBy(t => $"{Fold(t.AlbumArtist)} {Fold(t.Album)}", StringComparer.Ordinal)
            .OrderByDescending(g => g.Sum(t => t.Count))
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .First();

        // Which artist tag speaks for the album, for the case where the key falls back on it: a blank
        // album artist means the album is keyed under the track artist, and a compilation has one per track.
        var lead = album
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.Artist ?? "", StringComparer.Ordinal)
            .First();

        return (
            lead.AlbumArtist,
            lead.Artist,
            lead.Album,
            album.Select(t => t.RelativePath).Min(StringComparer.Ordinal)!);
    }

    private static string Fold(string? value) => (value ?? "").Trim().ToLowerInvariant();

    /// <summary>
    /// Bytes the cloud holds for this library. The canonical blob is what is actually stored and
    /// what the server charges for. The original file is bigger by the size of its tags.
    /// Approximate on purpose: <c>GET /cloud/quota</c> is the number that counts.
    /// </summary>
    public long CloudBytesUsed()
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                $"""
                SELECT COALESCE(SUM(canonical_size_bytes), 0) FROM tracks
                WHERE cloud_state IN ({(int)CloudState.Synced}, {(int)CloudState.CloudOnly})
                """;
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    /// <summary>
    /// Ids held by more than one row. These are files copied outside the app, which carry the id of
    /// their source. Sync stamps them again so that an id means exactly one file. One grouped index
    /// scan, so it is cheap to ask on every sync.
    /// </summary>
    public HashSet<string> DuplicateRpmIds()
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                """
                SELECT rpm_id FROM tracks
                WHERE rpm_id IS NOT NULL AND rpm_id <> ''
                GROUP BY rpm_id HAVING COUNT(*) > 1
                """;
            using var reader = cmd.ExecuteReader();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read())
                ids.Add(reader.GetString(0));
            return ids;
        }
    }

    // Artist backdrop order (the art chooser).
    //
    // Keyed by the artist NAME and not a track id: the strip shows one artist's photos whichever of
    // their tracks is selected, and the same order serves a library file and an import-queue file that
    // share an artist. The order is decoration, like the artwork itself. A missing or stale row sends
    // the chooser back to ranking from scratch and is never an error.

    /// <summary>The store key for an artist, trimmed and case-folded, so "Björk" and " björk " are one
    /// artist. It matches the NOCASE columns.</summary>
    public static string ArtistKey(string artist) => artist.Trim().ToLowerInvariant();

    /// <summary>The saved backdrop order for an artist, or null when none is stored yet.</summary>
    public ArtistArtOrder? GetArtistArt(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return null;

        var key = ArtistKey(artist);
        using (_readGate.Take())
        {
            var backdrops = new List<ArtistBackdrop>();
            using (var cmd = _read.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT url, position, score, hidden FROM artist_art WHERE artist_key = $k ORDER BY position";
                cmd.Parameters.AddWithValue("$k", key);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    backdrops.Add(new ArtistBackdrop(
                        reader.GetString(0), (int)reader.GetInt64(1), reader.GetDouble(2),
                        reader.GetInt64(3) != 0));
            }

            if (backdrops.Count == 0)
                return null;

            var curated = false;
            var rankVersion = 0;
            using (var cmd = _read.CreateCommand())
            {
                cmd.CommandText = "SELECT curated, rank_version FROM artist_art_pref WHERE artist_key = $k";
                cmd.Parameters.AddWithValue("$k", key);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    curated = reader.GetInt64(0) != 0;
                    rankVersion = (int)reader.GetInt64(1);
                }
            }

            return new ArtistArtOrder(key, curated, backdrops, rankVersion);
        }
    }

    /// <summary>
    /// Replaces an artist's saved order with <paramref name="backdrops"/>. <paramref name="curated"/>
    /// true records that the user arranged it by hand: the automatic re-ranker reads this and does not
    /// change it again. The whole set is rewritten in one transaction, so the stored order is always a
    /// clean 0..n-1 with no gaps.
    /// </summary>
    public void SaveArtistArt(string artist, IReadOnlyList<ArtistBackdrop> backdrops, bool curated, int rankVersion = 0)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return;

        var key = ArtistKey(artist);
        using (_gate.Take())
        {
            using var tx = _connection.BeginTransaction();

            using (var del = _connection.CreateCommand())
            {
                del.CommandText = "DELETE FROM artist_art WHERE artist_key = $k";
                del.Parameters.AddWithValue("$k", key);
                del.ExecuteNonQuery();
            }

            foreach (var b in backdrops)
            {
                using var ins = _connection.CreateCommand();
                ins.CommandText =
                    "INSERT INTO artist_art (artist_key, url, position, score, hidden) " +
                    "VALUES ($k, $u, $p, $s, $h)";
                ins.Parameters.AddWithValue("$k", key);
                ins.Parameters.AddWithValue("$u", b.Url);
                ins.Parameters.AddWithValue("$p", b.Position);
                ins.Parameters.AddWithValue("$s", b.Score);
                ins.Parameters.AddWithValue("$h", b.Hidden ? 1 : 0);
                ins.ExecuteNonQuery();
            }

            using (var pref = _connection.CreateCommand())
            {
                pref.CommandText =
                    """
                    INSERT INTO artist_art_pref (artist_key, curated, updated_utc_ticks, rank_version)
                    VALUES ($k, $c, $t, $v)
                    ON CONFLICT(artist_key) DO UPDATE SET curated = $c, updated_utc_ticks = $t, rank_version = $v
                    """;
                pref.Parameters.AddWithValue("$k", key);
                pref.Parameters.AddWithValue("$c", curated ? 1 : 0);
                pref.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.Ticks);
                pref.Parameters.AddWithValue("$v", rankVersion);
                pref.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    /// <summary>Hides (or shows) one of an artist's backdrops. The user does not want it on the strip,
    /// and the ranker must not keep bringing it back. Does nothing if the photo is not stored.</summary>
    public void SetArtistArtHidden(string artist, string url, bool hidden)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return;

        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE artist_art SET hidden = $h WHERE artist_key = $k AND url = $u";
            cmd.Parameters.AddWithValue("$h", hidden ? 1 : 0);
            cmd.Parameters.AddWithValue("$k", ArtistKey(artist));
            cmd.Parameters.AddWithValue("$u", url);
            cmd.ExecuteNonQuery();
        }
    }

    // The framings made by hand.

    /// <summary>
    /// How the person placed this artist's photograph in the header, or null for "the chooser frames
    /// it". That is the case for most pictures, and it is an absent row and not a stored state (see
    /// the table's own note in <c>Migrate</c>).
    /// </summary>
    public ArtFraming? GetArtistFraming(string? artist, string? url)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(url))
            return null;

        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                "SELECT centre_x, centre_y, height_frac, cover FROM artist_art_frame WHERE artist_key = $k AND url = $u";
            cmd.Parameters.AddWithValue("$k", ArtistKey(artist));
            cmd.Parameters.AddWithValue("$u", url);
            using var reader = cmd.ExecuteReader();
            return reader.Read()
                ? new ArtFraming(reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3))
                : null;
        }
    }

    /// <summary>Every framing this artist has, by photograph. It is one read for a carousel that the
    /// user is about to page through, and not a query per page.</summary>
    public IReadOnlyDictionary<string, ArtFraming> GetArtistFramings(string? artist)
    {
        var framings = new Dictionary<string, ArtFraming>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(artist))
            return framings;

        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                "SELECT url, centre_x, centre_y, height_frac, cover FROM artist_art_frame WHERE artist_key = $k";
            cmd.Parameters.AddWithValue("$k", ArtistKey(artist));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                framings[reader.GetString(0)] = new ArtFraming(
                    reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4));
        }

        return framings;
    }

    /// <summary>
    /// Files a framing against one of an artist's photographs. A null <paramref name="framing"/>
    /// DELETES it: "let the chooser frame it again" is the absence of a row, so the reset leaves
    /// nothing behind that anything else would have to know about.
    /// </summary>
    public void SaveArtistFraming(string artist, string url, ArtFraming? framing)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(url))
            return;

        var key = ArtistKey(artist);
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            if (framing is { } f)
            {
                cmd.CommandText =
                    """
                    INSERT INTO artist_art_frame (artist_key, url, centre_x, centre_y, height_frac, cover, updated_utc_ticks)
                    VALUES ($k, $u, $x, $y, $h, $c, $t)
                    ON CONFLICT(artist_key, url) DO UPDATE SET
                        centre_x = $x, centre_y = $y, height_frac = $h, cover = $c, updated_utc_ticks = $t
                    """;
                cmd.Parameters.AddWithValue("$x", f.CentreX);
                cmd.Parameters.AddWithValue("$y", f.CentreY);
                cmd.Parameters.AddWithValue("$h", f.Height);
                cmd.Parameters.AddWithValue("$c", f.Cover);
                cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.Ticks);
            }
            else
            {
                cmd.CommandText = "DELETE FROM artist_art_frame WHERE artist_key = $k AND url = $u";
            }

            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$u", url);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Clears an artist's saved order entirely, as a reset to "let the ranker choose". The
    /// next visit ranks from scratch.</summary>
    public void DeleteArtistArt(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return;

        var key = ArtistKey(artist);
        using (_gate.Take())
        {
            using var tx = _connection.BeginTransaction();
            // The framings go with it: a "let the ranker choose again" that left a hand-made window
            // filed against a photograph would come back framed by hand and look as if the reset failed.
            foreach (var table in new[] { "artist_art", "artist_art_pref", "artist_art_frame" })
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = $"DELETE FROM {table} WHERE artist_key = $k";
                cmd.Parameters.AddWithValue("$k", key);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Builds the mirror row for a file that lives in the library, from its analysed
    /// tags and current on-disk size and mtime.</summary>
    public static LibraryTrack FromTrackInfo(TrackInfo info, string libraryRoot)
    {
        var relative = Path.GetRelativePath(libraryRoot, info.FilePath)
            .Replace(Path.DirectorySeparatorChar, '/');
        var fileInfo = new FileInfo(info.FilePath);
        var slash = relative.LastIndexOf('/');
        return new LibraryTrack
        {
            RelativePath = relative,
            Folder = slash < 0 ? "" : relative[..slash],
            FileName = slash < 0 ? relative : relative[(slash + 1)..],
            Extension = info.Extension,
            SizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
            ModifiedUtcTicks = fileInfo.Exists ? fileInfo.LastWriteTimeUtc.Ticks : 0,
            DurationSeconds = info.DurationSeconds,
            Title = info.Title,
            Artist = info.Artist,
            AlbumArtist = info.AlbumArtist,
            Album = info.Album,
            Genre = info.Genre,
            Subgenre = info.Subgenre,
            Advisory = info.Advisory,
            TrackNumber = info.TrackNumber,
            DiscNumber = info.DiscNumber,
            DiscCount = info.DiscCount,
            DiscSubtitle = info.DiscSubtitle,
            Work = info.Work,
            MovementName = info.MovementName,
            // Coalesced for the same reason as the stream facts below: a number is the settled
            // answer, and zero says "asked, part of no work" and does not leave the row pending.
            MovementNumber = (int)info.MovementNumber,
            Composer = info.Composers,
            Year = info.Year,

            // Coalesced, and this is load-bearing: null in the mirror means "never read", and sync
            // reads again any row that says so. A reader that did not fill these in (anything that
            // builds a TrackInfo without opening the file) would otherwise mark its own row pending
            // for ever and cause a tag read of the whole library on every sync. Written as a number,
            // the row settles: tried, and the answer was nothing.
            Channels = info.Channels ?? 0,
            SampleRate = info.SampleRate ?? 0,
            BitsPerSample = info.BitsPerSample ?? 0,
            BitrateKbps = info.BitrateKbps ?? 0,
            CodecDescription = info.CodecDescription,
            LoudnessLufs = info.LoudnessLufs,
            TruePeak = info.TruePeak,

            // Passed through as they are, unlike the stream facts above: zero and null are the file's
            // own answer here and not a pending read. A file with no tempo in it is a file with no
            // tempo in it, and there is nothing for a later pass to come back for.
            TagBpm = info.Bpm,
            TagKey = info.MusicalKey,
            ReleaseType = info.ReleaseType,
            RpmId = info.RpmId,
        };
    }

    private static LibraryTrack ReadTrack(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        RelativePath = r.GetString(r.GetOrdinal("relative_path")),
        Folder = r.GetString(r.GetOrdinal("folder")),
        FileName = r.GetString(r.GetOrdinal("file_name")),
        Extension = r.GetString(r.GetOrdinal("extension")),
        SizeBytes = r.GetInt64(r.GetOrdinal("size_bytes")),
        ModifiedUtcTicks = r.GetInt64(r.GetOrdinal("modified_utc_ticks")),
        DurationSeconds = r.GetDouble(r.GetOrdinal("duration_seconds")),
        Title = NullableString(r, "title"),
        Artist = NullableString(r, "artist"),
        AlbumArtist = NullableString(r, "album_artist"),
        Album = NullableString(r, "album"),
        Genre = NullableString(r, "genre"),
        Subgenre = NullableString(r, "subgenre"),
        Advisory = NullableInt(r, "advisory") is { } a ? (ContentAdvisory)a : null,
        TrackNumber = (uint)r.GetInt64(r.GetOrdinal("track_number")),
        DiscNumber = (uint)r.GetInt64(r.GetOrdinal("disc_number")),
        DiscCount = (uint)r.GetInt64(r.GetOrdinal("disc_count")),
        DiscSubtitle = NullableString(r, "disc_subtitle"),
        Work = NullableString(r, "work"),
        MovementName = NullableString(r, "movement_name"),
        MovementNumber = NullableInt(r, "movement_no"),
        Composer = NullableString(r, "composer"),
        Year = (uint)r.GetInt64(r.GetOrdinal("year")),
        Channels = NullableInt(r, "channels"),
        SampleRate = NullableInt(r, "sample_rate"),
        BitsPerSample = NullableInt(r, "bits_per_sample"),
        BitrateKbps = NullableInt(r, "bitrate_kbps"),
        CodecDescription = NullableString(r, "codec_description"),
        LoudnessLufs = NullableDouble(r, "loudness_lufs"),
        TruePeak = NullableDouble(r, "true_peak"),
        TagBpm = r.GetDouble(r.GetOrdinal("tag_bpm")),
        TagKey = NullableString(r, "tag_key"),
        ReleaseType = ReleaseTypes.FromStored(NullableString(r, "release_type")),
        RpmId = NullableString(r, "rpm_id"),
        ContentHash = NullableString(r, "content_hash"),
        CloudState = (CloudState)r.GetInt64(r.GetOrdinal("cloud_state")),
        CloudSelected = r.GetInt64(r.GetOrdinal("cloud_selected")) != 0,
        CloudPinned = r.GetInt64(r.GetOrdinal("cloud_pinned")) != 0,
        CanonicalSizeBytes = r.GetInt64(r.GetOrdinal("canonical_size_bytes")),
        CloudSyncedUtcTicks = r.GetInt64(r.GetOrdinal("cloud_synced_utc_ticks")),
        CloudError = NullableString(r, "cloud_error"),
        CloudPartialError = Optional(r, "cloud_partial_error"),
        CloudAttempts = (int)r.GetInt64(r.GetOrdinal("cloud_attempts")),
        AddedUtcTicks = r.GetInt64(r.GetOrdinal("added_utc_ticks")),
        Plays = (int)r.GetInt64(r.GetOrdinal("plays")),
        LastPlayedUtcTicks = r.GetInt64(r.GetOrdinal("last_played_utc_ticks")),
        IsFavorite = r.GetInt64(r.GetOrdinal("favorite")) != 0,
        FavoritedUtcTicks = r.GetInt64(r.GetOrdinal("favorited_utc_ticks")),
        CloudAdopted = r.GetInt64(r.GetOrdinal("cloud_adopted")) != 0,
        MetadataEditedUtcTicks = r.GetInt64(r.GetOrdinal("metadata_edited_utc_ticks")),
        CloudUpdatedAt = r.GetInt64(r.GetOrdinal("cloud_updated_at")),
        PeerAdopted = r.GetInt64(r.GetOrdinal("peer_adopted")) != 0,
        PeerDeviceId = NullableString(r, "peer_device_id"),
    };

    /// <summary>Null means the column was never filled in; see the v12 note on the schema.</summary>
    private static int? NullableInt(SqliteDataReader r, string column)
    {
        int i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : (int)r.GetInt64(i);
    }

    private static string? NullableString(SqliteDataReader r, string column)
    {
        int i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    /// <summary>
    /// <see cref="NullableString"/> for a column that the reader may not have at all.
    ///
    /// <para>Every other column is required, and a reader that lacks one is a bug that deserves the
    /// throw. This is for a column that is not required: a note about a failure, where absence and
    /// null mean the same thing, that nothing has gone wrong with this track.</para>
    ///
    /// <para>It covers the window between a binary and the mirror it opens. A build newer than the
    /// database reads the column before <see cref="Migrate"/> has added it, and <c>ReadTrack</c> is
    /// reached from queries in four files, one of them the playlist panel, which is built while the
    /// main window is loading. Without this, that mismatch would not be a missing badge. The whole
    /// window would fail to open over a column that nobody has written to yet.</para>
    /// </summary>
    private static string? Optional(SqliteDataReader r, string column)
    {
        // Found by walking the reader's own names and not by catching what GetOrdinal throws.
        // ReadTrack runs once per ROW, so a throwing version would raise a first-chance exception
        // for every track in a mirror that has not been migrated yet, thousands of them, into the
        // debug output. A column that is legitimately absent is not an exceptional condition.
        for (int i = 0; i < r.FieldCount; i++)
            if (string.Equals(r.GetName(i), column, StringComparison.OrdinalIgnoreCase))
                return r.IsDBNull(i) ? null : r.GetString(i);

        return null;
    }

    private static double? NullableDouble(SqliteDataReader r, string column)
    {
        int i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetDouble(i);
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Closes the mirror: the gates are shut first, and a connection is taken apart only if its gate
    /// was idle when that happened.
    ///
    /// <para><b>Why the order matters.</b> A background pass holds a database for the length of a
    /// pass, not the length of a call. For example, the fingerprint sweep captures it at the start
    /// and comes back to it after a submission that can take a minute. So the window closing, or
    /// another library being opened, can happen in the middle of a pass. If the connections were
    /// taken apart first, the pass's next write would find a closed connection ("ExecuteNonQuery can
    /// only be called when the connection is open", from a worker), and a write already in flight
    /// would have its handle closed under a running statement. With the gates shut first, the pass is
    /// refused with <see cref="ObjectDisposedException"/> before it reaches the connection, which a
    /// caller can catch and stop on.</para>
    ///
    /// <para><b>A gate that is still held after the wait keeps its connection.</b> The thread inside
    /// is using it. Either the process is going away, where a connection that nobody closed costs
    /// nothing, or a different library is being opened, where this one is collected when the pass lets
    /// go and its last few writes land in the database they were always about. Waiting longer is not
    /// possible: this runs when the window closes.</para>
    /// </summary>
    public void Dispose()
    {
        var readIdle = _readGate.Close(CloseWait);
        var writeIdle = _gate.Close(CloseWait);

        if (readIdle)
            _read.Dispose();
        if (writeIdle)
            _connection.Dispose();
    }

    /// <summary>How long <see cref="Dispose"/> gives a hold in progress to finish. An ordinary hold is
    /// under the hundred milliseconds at which <see cref="Gate"/> starts to write notes. This value is for
    /// the import's batch, and it is short because the caller is a window that has already gone.</summary>
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(2);
}
