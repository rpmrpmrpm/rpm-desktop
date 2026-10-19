using RPM.Core.Art;

namespace RPM.Core.Ripping;

/// <summary>One track of a CD's table of contents. <paramref name="Lba"/> is the logical block
/// address of the track's first sector. It is the raw value the drive reports, without the 150-frame
/// lead-in that MusicBrainz offsets include. One frame (sector) is 1/75 s.</summary>
/// <param name="Number">1-based track number as burned on the disc.</param>
/// <param name="Lba">Start sector, lead-in excluded.</param>
/// <param name="IsData">True when the TOC's control nibble has bit 0x04 set. That marks a data track
/// on an enhanced or CD-Extra disc. Data tracks cannot be ripped and are left out of the disc ID's
/// track range.</param>
public sealed record DiscTrack(int Number, int Lba, bool IsData);

/// <summary>A CD's table of contents: everything needed to compute a disc ID, look the disc up,
/// and size each track. Durations are derived, not stored. A track runs to the next track's start,
/// and the last one runs to the lead-out.</summary>
/// <param name="AudioLeadoutLba">On an enhanced or CD-Extra disc, the audio tracks are in session 1
/// and a data track is in session 2. This is the lead-out of the AUDIO session, which is where the
/// last audio track really ends. It is null on an ordinary disc (no data track), or when the
/// multisession TOC could not be read. See <see cref="EndLbaOf"/> for why the last audio track cannot
/// simply run to the data track.</param>
public sealed record DiscToc(
    int FirstTrack,
    int LastTrack,
    IReadOnlyList<DiscTrack> Tracks,
    int LeadoutLba,
    int? AudioLeadoutLba = null)
{
    /// <summary>Frames per second on a CD (Red Book).</summary>
    public const int FramesPerSecond = 75;

    public IEnumerable<DiscTrack> AudioTracks => Tracks.Where(t => !t.IsData);

    /// <summary>Where the given track ends: the next track's start, or the lead-out for the last one.</summary>
    public int EndLbaOf(DiscTrack track)
    {
        var next = Tracks.Where(t => t.Number > track.Number).MinBy(t => t.Number);

        // A CD-Extra puts a data track in a second session, after the audio. The last audio track must end
        // at the AUDIO session's lead-out, NOT at the data track's start. The sectors between the two are
        // the lead-out and lead-in gap of the session. They cannot be read, so a track that ran to the
        // data track would read straight into that gap, and the drive would refuse.
        if (!track.IsData && next is { IsData: true } && AudioLeadoutLba is { } audioEnd)
            return audioEnd;

        return next?.Lba ?? LeadoutLba;
    }

    public int SectorCountOf(DiscTrack track) => Math.Max(0, EndLbaOf(track) - track.Lba);

    public TimeSpan DurationOf(DiscTrack track) =>
        TimeSpan.FromSeconds(SectorCountOf(track) / (double)FramesPerSecond);
}

/// <summary>An optical drive, as offered to the user in the rip dialog.</summary>
/// <param name="Id">The stable handle the reader uses to talk to the drive: a BSD device node on
/// macOS (<c>/dev/disk4</c>), or a drive letter on Windows (<c>D:</c>).</param>
/// <param name="DisplayName">Vendor + product, for the drive picker.</param>
public sealed record DiscDrive(string Id, string DisplayName);

/// <summary>A disc sitting in a drive, ready to rip.</summary>
/// <param name="MountPath">Where the OS made the disc's tracks available as files, if it did.
/// macOS mounts audio CDs through cddafs and makes one AIFF per track. Windows' cdfs mounts audio CDs
/// as 44-byte <c>.cda</c> stubs that hold no audio. Null when nothing is mounted.</param>
public sealed record DiscInfo(
    DiscDrive Drive,
    DiscToc Toc,
    string DiscId,
    string? MountPath);

/// <summary>How far a rip has got. <paramref name="TrackFraction"/> is the progress within the current
/// track, 0..1. The progress for the whole rip is <see cref="Overall"/>.</summary>
public sealed record RipProgress(int TrackDone, int TrackTotal, string CurrentTrack, double TrackFraction)
{
    /// <summary>The whole disc, 0..1. Every view of a rip (the popup's bar and the deck chip's ring)
    /// uses this one number, so a bar and a ring can never disagree about the same disc.</summary>
    public double Overall => TrackTotal <= 0
        ? 0
        : Math.Clamp((TrackDone + TrackFraction) / TrackTotal, 0, 1);

    /// <summary>Which track is being worked on, counting from 1 and never above the total. The final
    /// report says every track is done, so "12 of 11" must never be shown.</summary>
    public int TrackNow => TrackTotal <= 0 ? 0 : Math.Min(TrackDone + 1, TrackTotal);
}

/// <summary>What became of one track the rip was asked for. A rip of a damaged disc is not all or
/// nothing, so the album is a list of these, not a count.</summary>
/// <param name="RepairedBlocks">Blocks the drive would not read, filled with silence so the track keeps
/// its timing. Zero on a clean read. Any value above zero is audible damage that the user should hear
/// about before the album goes into the library.</param>
public sealed record RipTrackOutcome(int Number, string Title, RipTrackResult Result, int RepairedBlocks = 0)
{
    public bool IsSkipped => Result != RipTrackResult.Ripped;
}

/// <summary>How a track ended. Everything except <see cref="Ripped"/> means there is no file for it.
/// The album is one track short by design, not by accident.</summary>
public enum RipTrackResult
{
    Ripped,

    /// <summary>The user pressed skip, usually on a track the drive was never going to finish.</summary>
    SkippedByHand,

    /// <summary>Too many blocks would not read, so the track was dropped by policy.</summary>
    SkippedUnreadable,

    /// <summary>The drive stopped responding and the read was abandoned.</summary>
    SkippedStalled,
}

/// <summary>Output encoding for a rip. FLAC and ALAC are native ffmpeg encoders. MP3 uses libmp3lame.
/// None of them requires a GPL build of ffmpeg.</summary>
public enum RipFormat
{
    Flac,
    Alac,
    Mp3,
}

/// <summary>One track the user chose to rip, with the metadata to stamp on it. <paramref name="Country"/>
/// is album-level (the release country of the pressing), but it is carried per track, like Album and
/// Year. The ripper then has everything it tags in one record.</summary>
/// <param name="Advisory">Whether the lyrics are explicit, as the catalogue says. Unknown is the
/// usual answer and stamps nothing.</param>
/// <param name="Work">The composition, for a classical disc. See
/// <see cref="Metadata.ExtendedTags"/>. A rip is the only time these can be filled, because nothing
/// looks a finished rip up again.</param>
public sealed record RipTrackPlan(
    int Number,
    string Title,
    string? Artist,
    string? Album,
    string? AlbumArtist,
    uint Year,
    string? Genre,
    int TrackCount,
    string? Subgenre = null,
    string? Country = null,
    int DiscNumber = 0,
    int DiscCount = 0,
    string? DiscSubtitle = null,
    Models.ContentAdvisory Advisory = Models.ContentAdvisory.Unknown,

    // ── who, as the catalogue credits them ──
    string? ArtistSort = null,
    string? AlbumArtistSort = null,
    string? Composers = null,
    string? ComposerSort = null,
    string? Conductor = null,
    string? Lyricist = null,
    string? Arranger = null,
    string? Ensemble = null,
    string? Soloists = null,

    // ── what it is a performance of ──
    string? Work = null,
    string? MovementName = null,
    uint MovementNumber = 0,
    uint MovementCount = 0,

    // ── which pressing it came off ──
    string? Isrc = null,
    string? Barcode = null,
    string? CatalogNumber = null,
    string? Label = null,
    string? Media = null,
    uint OriginalYear = 0,

    // ── what makes the next lookup exact ──
    string? RecordingId = null,
    string? ReleaseId = null,
    string? ReleaseGroupId = null,
    string? ArtistId = null,
    string? ReleaseArtistId = null)
{
    /// <summary>
    /// The plan as the tags to stamp on the file that was just ripped off the disc.
    ///
    /// <para>This is kept on the plan, not in each ripper. The CD and DVD rippers each wrote this same
    /// map, so a field added to one was silently missing from the other.</para>
    /// </summary>
    public Models.TrackInfo ToTrackInfo(
        string path, TimeSpan duration, byte[]? art = null, string? artMime = null) => new()
    {
        FilePath = path,
        Extension = Path.GetExtension(path),
        DurationSeconds = duration.TotalSeconds,
        Title = Title,
        Artist = Artist,
        AlbumArtist = AlbumArtist,
        Album = Album,
        TrackNumber = (uint)Number,
        TrackCount = (uint)TrackCount,
        DiscNumber = (uint)DiscNumber,
        DiscCount = (uint)DiscCount,
        DiscSubtitle = DiscSubtitle,
        Year = Year,
        Genre = Genre,
        ReleaseCountry = Country,
        Advisory = Advisory,

        ArtistSort = ArtistSort,
        AlbumArtistSort = AlbumArtistSort,
        Composers = Composers,
        ComposerSort = ComposerSort,
        Conductor = Conductor,
        Lyricist = Lyricist,
        Arranger = Arranger,
        Ensemble = Ensemble,
        Soloists = Soloists,

        Work = Work,
        MovementName = MovementName,
        MovementNumber = MovementNumber,
        MovementCount = MovementCount,

        Isrc = Isrc,
        Barcode = Barcode,
        CatalogNumber = CatalogNumber,
        // The label that released the record goes in the Publisher tag. Every tagger shows it under that
        // name, so the rip's label is where a user would look for it.
        Publisher = Label,
        Media = Media,
        OriginalYear = OriginalYear,

        MusicBrainzRecordingId = RecordingId,
        MusicBrainzReleaseId = ReleaseId,
        MusicBrainzReleaseGroupId = ReleaseGroupId,
        MusicBrainzArtistId = ArtistId,
        MusicBrainzReleaseArtistId = ReleaseArtistId,

        PendingArt = art,
        PendingArtMime = artMime,
        Source = Models.MetadataSource.Tags,
    };
}

/// <summary>Everything the ripper needs for one disc.</summary>
/// <param name="Cover">The sleeve the user chose, at both sizes. The full scan goes beside the tracks
/// as the album folder's cover image. The scaled copy goes inside each track file. A CD has no cover
/// of its own, so a sleeve that was not fetched before the rip is never available to the album.</param>
/// <param name="DiscArt">A transparent PNG of the disc itself, written to the album folder as
/// disc.png. It is at full size, as the source holds it. There is one file per album, not one per
/// track.</param>
public sealed record RipPlan(
    DiscInfo Disc,
    IReadOnlyList<RipTrackPlan> Tracks,
    RipFormat Format,
    Sleeve? Cover = null,
    byte[]? DiscArt = null);
