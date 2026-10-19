namespace RPM.Core.Identify;

/// <summary>
/// Answer from an identification backend for one fingerprint lookup.
/// </summary>
/// <param name="Title">Recording title.</param>
/// <param name="Artist">Display artist ("A, B" for multiples).</param>
/// <param name="Album">Representative release title, when known.</param>
/// <param name="Year">First release year, 0 when unknown.</param>
/// <param name="Score">Match confidence 0..1.</param>
/// <param name="Source">Which layer answered: "own-db", "acoustid" or "audd".</param>
public sealed record IdentificationResult(
    string Title,
    string Artist,
    string? Album,
    uint Year,
    double Score,
    string Source);

/// <summary>
/// What a backend gets to work with. The compressed form feeds AcoustID/AudD passthrough;
/// the raw sub-fingerprints feed the Lambda's own-DB matcher and its store-back.
/// </summary>
public sealed record FingerprintQuery(
    string CompressedFingerprint,
    double DurationSeconds,
    uint[]? RawFingerprint = null);

/// <summary>
/// One layer-agnostic identify call: fingerprint in, best match out.
///
/// The production backend is the RPM Lambda. It tries its own fingerprint database, then AcoustID,
/// then AudD. It stores every external hit in its own database, so over time it relies less on paid
/// lookups. A direct AcoustID backend exists for development.
/// </summary>
public interface IIdentificationBackend
{
    Task<IdentificationResult?> IdentifyAsync(FingerprintQuery query, CancellationToken ct = default);
}

/// <summary>
/// Backends that also support the advanced clip layer (AudD via the Lambda): a short audio clip is
/// recognised when the fingerprint layers came up empty. Supporters only. The server decides, and
/// refuses the call for everyone else.
/// </summary>
public interface IClipIdentificationBackend : IIdentificationBackend
{
    Task<ClipAnswer> IdentifyClipAsync(FingerprintQuery query, byte[] clipMp3, CancellationToken ct = default);
}

/// <summary>What AudD did for one track: not asked, asked and named it, or asked and did not know it.
/// Counted by whatever tells the user what went to AudD, so that a count is a count of what happened
/// and not of what the account was allowed to do.</summary>
public enum AudDOutcome
{
    NotAsked,
    Identified,
    NotIdentified,
}

/// <summary>
/// The clip endpoint's answer for one clip, saying who answered it.
///
/// <para>The endpoint looks in the rpm database once more before it asks AudD, so a clip sent there
/// is not necessarily a clip AudD heard. Only <see cref="AudDIdentified"/> and
/// <see cref="AudDNotIdentified"/> reached AudD. The server stores an AudD hit in the rpm database,
/// so the same recording is answered there next time. A miss is not stored, so the same clip goes to
/// AudD again next time.</para>
/// </summary>
public abstract record ClipAnswer
{
    private ClipAnswer() { }

    /// <summary>The rpm database named it on the server's second look. AudD was not asked.</summary>
    public sealed record FromOwnDb(IdentificationResult Result) : ClipAnswer;

    /// <summary>AudD was asked and named it.</summary>
    public sealed record AudDIdentified(IdentificationResult Result) : ClipAnswer;

    /// <summary>AudD was asked and did not know it.</summary>
    public sealed record AudDNotIdentified : ClipAnswer;

    /// <summary>The recording it was named as, or null when nobody knew it.</summary>
    public IdentificationResult? Match => this switch
    {
        FromOwnDb own => own.Result,
        AudDIdentified audd => audd.Result,
        _ => null,
    };

    public AudDOutcome AudD => this switch
    {
        AudDIdentified => AudDOutcome.Identified,
        AudDNotIdentified => AudDOutcome.NotIdentified,
        _ => AudDOutcome.NotAsked,
    };
}

/// <summary>A release a recording appears on and the track slot it occupies there.</summary>
public sealed record ReleaseRef(string ReleaseId, string? ReleaseTitle, uint TrackPosition, uint MediumTrackCount);

/// <summary>Match for a SEGMENT of a long file: the recording plus every release it appears
/// on, so segment votes can converge on the album the whole file is.</summary>
public sealed record SegmentIdentification(
    string? RecordingId,
    string Title,
    string Artist,
    double Score,
    IReadOnlyList<ReleaseRef> Releases);

/// <summary>
/// Backends that can identify a fingerprint of a segment (not a whole track) and return
/// release/track-position candidates, which are the input for album-in-one-file splitting.
/// </summary>
public interface ISegmentIdentificationBackend : IIdentificationBackend
{
    Task<SegmentIdentification?> IdentifySegmentAsync(FingerprintQuery query, CancellationToken ct = default);
}
