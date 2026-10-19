using RPM.Core.Art;

namespace RPM.Core.Splitting;

/// <summary>Where a track boundary came from. The source sets the confidence that is shown and the
/// precision of the cut.</summary>
public enum BoundarySource
{
    /// <summary>Exact position from a cue sheet.</summary>
    CueSheet,
    /// <summary>Detected gap in the audio (RMS envelope silence).</summary>
    Silence,
    /// <summary>No audible gap; placed from the album's expected track durations (MusicBrainz).</summary>
    DurationAligned,
    /// <summary>Hand-edited by the user in the split dialog.</summary>
    Manual,
}

/// <summary>One proposed track inside an album-length file.</summary>
/// <param name="Catalogue">The listing's whole row for this track, when a pressing was picked. It has
/// the track's own artist, the work it performs, its ISRC and whether it is explicit. The whole row is
/// carried rather than copied field by field. The split is the only point where the cut files get
/// their tags, and nothing looks them up again later.</param>
public sealed record SplitTrack(
    double StartSeconds,
    double EndSeconds,
    string? Title,
    string? Artist,
    uint TrackNumber,
    double Confidence,
    BoundarySource Source,
    Metadata.Providers.ReleaseTrackInfo? Catalogue = null)
{
    public double DurationSeconds => EndSeconds - StartSeconds;
}

/// <summary>The full proposal for cutting one album file into tracks. Disc fields are set when
/// the file turned out to be one disc of a multi-disc release (0 = not applicable).</summary>
/// <param name="Art">A cover to fall back on when the source file has none embedded. It is set for a
/// recording of a record, which has no tags to carry art. Everything downstream (folder images, the
/// art display and the library) reads the picture from the track files. So a cover that is not
/// embedded here is never seen again. The <see cref="Sleeve.Folder"/> tier is also written beside the
/// cut tracks as the album folder's cover image.</param>
public sealed record SplitPlan(
    string SourcePath,
    string Extension,
    double TotalSeconds,
    string? Album,
    string? AlbumArtist,
    uint Year,
    string? ReleaseMbid,
    IReadOnlyList<SplitTrack> Tracks,
    uint DiscNumber = 0,
    uint DiscCount = 0,
    Sleeve? Art = null,
    string? Country = null);

/// <summary>Progress of the boundary/identification analysis (phases: detecting, fingerprinting,
/// looking up, aligning).</summary>
public sealed record SplitAnalysisProgress(string Phase, int Step, int Total, string Detail);

/// <summary>Progress of the actual ffmpeg cutting.</summary>
public sealed record SplitProgress(int TrackDone, int TrackTotal, string CurrentTrack);

/// <summary>How a file was flagged as a probable album-in-one-file.</summary>
[Flags]
public enum AlbumFileHint
{
    None = 0,
    /// <summary>Longer than the candidate duration threshold.</summary>
    LongDuration = 1,
    /// <summary>A sibling .cue sheet references it.</summary>
    CueSheet = 2,
    /// <summary>The filename looks like a full rip ("full album", "side a", and similar).</summary>
    FilenameHint = 4,
}

/// <summary>A file worth offering the split workflow for.</summary>
public sealed record AlbumFileCandidate(
    string FilePath,
    double DurationSeconds,
    string? CueSheetPath,
    AlbumFileHint Hint);
