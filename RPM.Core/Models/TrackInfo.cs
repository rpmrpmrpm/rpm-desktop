namespace RPM.Core.Models;

/// <summary>Where the metadata for a track came from, in decreasing order of trust.</summary>
public enum MetadataSource
{
    /// <summary>Read from embedded tags (ID3/Vorbis/MP4 atoms) that looked complete.</summary>
    Tags,

    /// <summary>Identified with a Chromaprint fingerprint, then AcoustID, then MusicBrainz.</summary>
    Fingerprint,

    /// <summary>Guessed from the file name (e.g. "Artist - Title.mp3").</summary>
    FilenameGuess,

    /// <summary>Entered by hand in the review workbench.</summary>
    Manual,

    /// <summary>Nothing usable found; track stays unidentified.</summary>
    Unknown,
}

/// <summary>
/// Whether a file could be read at all, and if not, why not. The two failures look the same from
/// the outside (no tags, no length, nothing) but need opposite treatment. This is why the value is
/// not a bool.
/// </summary>
public enum FileAvailability
{
    /// <summary>The file is here and it opened. It may still carry no tags. That is a different
    /// problem, and it is the one the queue's question mark shows.</summary>
    Local,

    /// <summary>A cloud placeholder: a good file that is not on this disk. Nothing is read from it,
    /// because reading it would start the download.</summary>
    Dataless,

    /// <summary>The file is here but broken. Neither the tag parser nor ffmpeg could find audio in
    /// it. Examples: a truncated download, a damaged header, or a zero-byte file with a .mp3 name.</summary>
    Unreadable,
}

/// <summary>
/// What a file says about its own lyrics. The numbers are the iTunes values, which is how every
/// container that carries the flag stores it. See <see cref="Metadata.AdvisoryTag"/>.
/// </summary>
public enum ContentAdvisory
{
    /// <summary>The file carries no flag. This is not the same as clean: the file says nothing either way.</summary>
    Unknown = 0,

    /// <summary>Explicit lyrics. The track shows the E badge.</summary>
    Explicit = 1,

    /// <summary>Stated to be clean: either an edited version or a record that was never explicit.</summary>
    Clean = 2,
}

/// <summary>
/// Everything known about one audio file after the analysis pass.
/// A null or zero member means "not known".
/// </summary>
public sealed record TrackInfo
{
    public required string FilePath { get; init; }

    /// <summary>Lower-case extension including the dot, e.g. ".mp3".</summary>
    public required string Extension { get; init; }

    public double DurationSeconds { get; init; }

    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Album { get; init; }
    public uint TrackNumber { get; init; }
    public uint TrackCount { get; init; }
    public uint DiscNumber { get; init; }
    public uint DiscCount { get; init; }

    /// <summary>The disc's own title within a multi-disc set (for example "Disc 2: B-Sides"), when the
    /// release names it. Null for single-disc releases or discs with no subtitle.</summary>
    public string? DiscSubtitle { get; init; }

    public uint Year { get; init; }
    public string? Genre { get; init; }

    /// <summary>The narrower style inside the genre: "Deep House" under House, "Post-Punk" under
    /// Punk. It is often null, which is a normal value: many records have no narrower style.
    ///
    /// <para>Suggestions come from <see cref="Metadata.Subgenres.Under"/>. They are not enforced,
    /// as with the genre: a file may carry any style.</para></summary>
    public string? Subgenre { get; init; }

    // ── what the file is, as against what its tags say ──
    //
    // These are read from the audio stream, not from the tags, because a tag can claim anything.
    // They are nullable: null means "never read", so an existing mirror fills them in by itself.
    // A file that reports nothing gets zero written, so it is not read again. See
    // Metadata/AudioFormat for how they are worded.

    /// <summary>How many discrete channels the stream carries. 2 for most files, 6 for a 5.1 mix,
    /// 0 when the decoder does not say.</summary>
    public int? Channels { get; init; }

    public int? SampleRate { get; init; }

    /// <summary>Bit depth, where the format has one. Zero for the lossy codecs, which do not.</summary>
    public int? BitsPerSample { get; init; }

    public int? BitrateKbps { get; init; }

    /// <summary>What the decoder calls the stream. This is the only value that tells Apple Lossless
    /// from AAC, which both use the .m4a extension. Kept raw; <see cref="Metadata.AudioFormat.Codec"/>
    /// reads it.</summary>
    public string? CodecDescription { get; init; }

    // ── loudness ──
    //
    // These values let one track play at the same level as the one before it. Null on both means
    // "not measured yet", which is what the background pass looks for. The values come from a
    // ReplayGain tag written by another tagger, or from RPM decoding the track itself. See
    // Analysis/LoudnessMeter and Analysis/ReplayGain.

    /// <summary>Programme loudness in LUFS (ITU-R BS.1770-4). Null when the track has not been
    /// measured. The value is negative: a track at full scale would be near zero, and music sits well
    /// below that.</summary>
    public double? LoudnessLufs { get; init; }

    /// <summary>The track's true peak as a linear amplitude, where 1.0 is full scale. It can exceed
    /// 1.0 on a loud master. Null when not measured. A boost is limited by this value, so when it is
    /// missing the gain is applied without that limit.</summary>
    public double? TruePeak { get; init; }

    // Extended tags: the TagLib# fields that users can edit.
    public string? Composers { get; init; }
    public string? Conductor { get; init; }
    public string? Comment { get; init; }
    public string? Lyrics { get; init; }
    public string? Grouping { get; init; }
    public uint Bpm { get; init; }
    public string? Copyright { get; init; }
    public string? Publisher { get; init; }
    public string? RemixedBy { get; init; }
    public string? Isrc { get; init; }

    /// <summary>The musical key of the track ("A minor", "8A"). Read from the file, or set by the
    /// app's own key detection when the analysis has run.</summary>
    public string? MusicalKey { get; init; }

    // ── sort names ──
    //
    // These decide the order of lists, as against the name that is shown: "Beatles, The" sorts under
    // B, and "Beethoven, Ludwig van" sorts under B, not L. MusicBrainz gives a sort name for every
    // artist credit, so a lookup fills these at no extra cost. Without them a classical library
    // sorts its composers by first name.

    public string? TitleSort { get; init; }
    public string? ArtistSort { get; init; }
    public string? AlbumArtistSort { get; init; }
    public string? ComposerSort { get; init; }

    // ── classical music tags; see Metadata/ExtendedTags ──

    /// <summary>The composition this track is part of: "Symphony No. 5 in C minor, Op. 67".</summary>
    public string? Work { get; init; }

    /// <summary>This track's movement of that work: "I. Allegro con brio".</summary>
    public string? MovementName { get; init; }

    /// <summary>Which movement, 1-based. Zero when the track is not part of a work.</summary>
    public uint MovementNumber { get; init; }

    /// <summary>How many movements the work has, or zero.</summary>
    public uint MovementCount { get; init; }

    /// <summary>Who wrote the words, as against the music.</summary>
    public string? Lyricist { get; init; }

    /// <summary>Who arranged this performance.</summary>
    public string? Arranger { get; init; }

    /// <summary>The orchestra or choir. On a classical record this is rarely the album artist.</summary>
    public string? Ensemble { get; init; }

    /// <summary>The featured performers as the sleeve credits them: "Martha Argerich (piano)".</summary>
    public string? Soloists { get; init; }

    // ── which pressing the track comes from ──

    /// <summary>The EAN/UPC on the packaging. It names one pressing exactly.</summary>
    public string? Barcode { get; init; }

    /// <summary>The number printed on the sleeve and stamped in the run-out groove.</summary>
    public string? CatalogNumber { get; init; }

    /// <summary>The medium of the release: "CD", "12&quot; Vinyl".</summary>
    public string? Media { get; init; }

    /// <summary>The kind of release, or null when it is not known. See
    /// <see cref="Library.ReleaseType"/>.</summary>
    public Library.ReleaseType? ReleaseType { get; init; }

    /// <summary>The year the record first came out, as against the year of this pressing. Zero when
    /// unknown. It is not copied from <see cref="Year"/>, because that would make every reissue look
    /// like an original.</summary>
    public uint OriginalYear { get; init; }

    // ── MusicBrainz identifiers ──
    //
    // These make the next lookup exact instead of a fuzzy search on a title that may be misspelled.
    // Every release lookup already returns them, and files tagged with Picard already carry them, so
    // writing them keeps RPM's files compatible with other software.

    /// <summary>The recording (the performance), which stays the same across every release it
    /// appears on. TagLib stores it as <c>MUSICBRAINZ_TRACKID</c>, the name Picard uses.</summary>
    public string? MusicBrainzRecordingId { get; init; }

    public string? MusicBrainzReleaseId { get; init; }
    public string? MusicBrainzReleaseGroupId { get; init; }

    /// <summary>The disc ID of the CD this track was ripped from. It is computed from the table of
    /// contents, not read from a tag. It names one pressing and cannot be mistyped, so it is the most
    /// exact identifier a ripped record has.</summary>
    public string? MusicBrainzDiscId { get; init; }

    /// <summary>The track's own artist. On a compilation it differs from the release artist.</summary>
    public string? MusicBrainzArtistId { get; init; }

    public string? MusicBrainzReleaseArtistId { get; init; }

    /// <summary>Two-letter release country of the pressing (ISO 3166), as MusicBrainz or Discogs
    /// report it. It is written to the file so that a ripped disc keeps its pressing, which the audio
    /// cannot show.</summary>
    public string? ReleaseCountry { get; init; }

    /// <summary>
    /// The GUID written in the file's own tags (see <see cref="Metadata.RpmTag"/>). It links the
    /// file to its library row after the user moves or renames it. Null when the file has none yet;
    /// import and sync write one. Reading a file never gives
    /// <see cref="Metadata.RpmTag.NoCarrier"/>; only the mirror holds that marker.
    /// </summary>
    public string? RpmId { get; init; }

    /// <summary>True when the file has embedded cover art. The bytes are loaded on demand.</summary>
    public bool HasArt { get; init; }

    /// <summary>Whether the lyrics are explicit, as the file's own advisory flag states. Unknown
    /// when the file has no flag. See <see cref="Metadata.AdvisoryTag"/>.</summary>
    public ContentAdvisory Advisory { get; init; }

    /// <summary>True when the track shows the E badge. Every view asks this.</summary>
    public bool IsExplicit => Advisory == ContentAdvisory.Explicit;

    /// <summary>Whether the file could be read, and why not when it could not. Set by
    /// <see cref="Metadata.TagReader.Read"/>. A track built by hand, not from a disk scan, is
    /// Local.</summary>
    public FileAvailability Availability { get; init; }

    // ── where the file's bytes are ──
    //
    // These are stored on the track, not looked up by each view. Every tracklist is built from
    // TrackInfo, and a library row becomes one only in LibraryDatabase.ToTrackInfo. So a tracklist
    // line can show what the cloud holds without the view knowing about the library. A TrackInfo
    // that does not come from a library row has the state Local and is not selected.

    /// <summary>What the cloud holds of this file. <see cref="Cloud.CloudState.Local"/> for any track
    /// that does not come from the library mirror.</summary>
    public Cloud.CloudState CloudState { get; init; } = Cloud.CloudState.Local;

    /// <summary>Whether the user has picked this file for the cloud. It is true before any of the file
    /// is uploaded. Together with <see cref="CloudState"/> it decides whether the file is in the
    /// cloud's set.</summary>
    public bool CloudSelected { get; init; }

    /// <summary>
    /// Why this track is in the cloud without everything it should have, for example a head clip
    /// that could not be encoded. Null when nothing is wrong.
    ///
    /// <para>Stored on the track for the same reason as <see cref="CloudState"/>: the mark on a
    /// tracklist line is drawn from a <see cref="TrackInfo"/>. If a line had to query the library
    /// to choose its colour, it would query once per line.</para>
    /// </summary>
    public string? CloudPartialError { get; init; }

    /// <summary>How many times the upload has failed. The queue uses it to decide whether to try the
    /// track again. See <see cref="Cloud.CloudRetrySchedule.MayTryAgain"/>.</summary>
    public int CloudAttempts { get; init; }

    /// <summary>Whether the listener has made this song a favourite. False for any track that does not
    /// come from the library mirror, such as a file previewed from another place.</summary>
    public bool IsFavorite { get; init; }

    /// <summary>
    /// True when the cloud has this track or will have it: it is uploaded, or it is chosen.
    ///
    /// <para>The album rollup counts in the same way (see <c>LibraryDatabase.GetAlbumCloudRollup</c>).
    /// A track that is chosen but not yet uploaded is in the set, because the user asked for it and
    /// "remove" would undo it.</para>
    /// </summary>
    public bool CloudInSet =>
        CloudSelected || CloudState is Cloud.CloudState.Synced or Cloud.CloudState.CloudOnly;

    /// <summary>
    /// Replacement cover art chosen by the user. It is written to the file on Apply.
    /// Null keeps the art the file has. An empty array removes the art.
    /// </summary>
    public byte[]? PendingArt { get; init; }
    public string? PendingArtMime { get; init; }

    /// <summary>True when the user edited tag fields by hand. Apply then writes the tags to the file.</summary>
    public bool TagsEdited { get; init; }

    public MetadataSource Source { get; init; } = MetadataSource.Unknown;

    /// <summary>AcoustID match score (0..1) when <see cref="Source"/> is Fingerprint, otherwise 0.</summary>
    public double IdentificationScore { get; init; }

    /// <summary>True when there is enough to build a clean name (artist and title).</summary>
    public bool HasUsableMetadata =>
        !string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(Artist);
}
