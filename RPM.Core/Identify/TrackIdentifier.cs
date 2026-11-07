using RPM.Core.Models;
using RPM.Fingerprint.Chroma;

namespace RPM.Core.Identify;

/// <summary>
/// Identifies a track by audio content: Chromaprint fingerprint (local fpcalc) →
/// <see cref="IIdentificationBackend"/> lookup → metadata merged into the TrackInfo.
/// The premium variant additionally cuts an audio clip for the paid AudD layer.
/// </summary>
public sealed class TrackIdentifier(IIdentificationBackend backend)
{
    /// <summary>Matches below this score are discarded.</summary>
    public double MinScore { get; init; } = 0.5;

    /// <summary>Fingerprint computation, replaceable for tests. Default shells out to fpcalc.</summary>
    public Func<string, FpcalcRunner.CompressedResult> Fingerprinter { get; init; } = FpcalcRunner.RunCompressed;

    /// <summary>
    /// Raw sub-fingerprint computation for the Lambda's own-DB layer and its store-back.
    /// A null result just leaves out the raw part.
    /// </summary>
    public Func<string, uint[]?> RawFingerprinter { get; init; } = path => FpcalcRunner.Run(path).SubFingerprints;

    /// <summary>Clip extraction for the paid layer, replaceable for tests. Default shells out to ffmpeg.</summary>
    public Func<string, double, byte[]> ClipCutter { get; init; } = Identify.ClipCutter.CutClip;

    /// <summary>True when the backend can run the paid clip layer (Lambda + AudD).</summary>
    public bool SupportsPremium => backend is IClipIdentificationBackend;

    /// <summary>
    /// What one identification actually did, not just what it produced. Which layer answered is a
    /// fact worth keeping. A track named from the own DB and a track named by the advanced layer are
    /// not the same event, even though neither costs anything at present.
    /// </summary>
    /// <param name="Source">"own-db", "acoustid", "audd": the layer that answered, or null on a miss.</param>
    /// <param name="AudD">What AudD did for this track. This is not read off <paramref name="Source"/>,
    /// which is null on every miss and so cannot tell an AudD miss from one that never reached AudD.</param>
    public sealed record IdentifyOutcome(
        TrackInfo Track,
        bool Matched,
        string? Source,
        double Score,
        AudDOutcome AudD);

    /// <summary>
    /// Tries to identify the file (free layers) and merge the result into <paramref name="info"/>.
    /// Returns the enriched TrackInfo, or the input unchanged when there is no confident match.
    /// Throws only on unexpected errors (fpcalc missing, network down). Callers decide policy.
    /// </summary>
    public async Task<TrackInfo> IdentifyAsync(TrackInfo info, CancellationToken ct = default)
        => (await IdentifyDetailedAsync(info, ct)).Track;

    /// <summary>The free ladder, reporting which layer answered.</summary>
    public async Task<IdentifyOutcome> IdentifyDetailedAsync(TrackInfo info, CancellationToken ct = default)
    {
        var query = await BuildQueryAsync(info, ct);
        var result = await backend.IdentifyAsync(query, ct);
        return Outcome(Merge(info, query, result), result, AudDOutcome.NotAsked);
    }

    /// <summary>What a run lost on the way up the ladder.</summary>
    public enum LostLayer
    {
        None,

        /// <summary>The server refused the advanced layer: the account does not support rpm.</summary>
        Refused,

        /// <summary>The advanced layer could not be reached or could not work. The basic layers are
        /// separate services, so this says nothing about them.</summary>
        Down,
    }

    /// <summary>
    /// One track up the ladder: the basic layers always, then the advanced layer for what they could not
    /// name. A track that the rpm database or AcoustID can name must never go out to AudD.
    ///
    /// <para>Losing the advanced layer is not a failure of the track. The answer the basic layers gave
    /// stands, and <see cref="LostLayer"/> tells the run to carry on without it. An AudD outage or a dead
    /// AudD key used to end the whole run at the first track the basic layers could not name, which left
    /// the rest of the list unasked of layers that were working.</para>
    /// </summary>
    public async Task<(IdentifyOutcome Outcome, LostLayer Lost)> ClimbAsync(
        TrackInfo info, bool advanced, CancellationToken ct = default)
    {
        var basic = await IdentifyDetailedAsync(info, ct);
        if (!advanced || basic.Matched)
            return (basic, LostLayer.None);

        try
        {
            return (await IdentifyPremiumDetailedAsync(info, ct), LostLayer.None);
        }
        catch (LambdaIdentificationBackend.SupportersOnlyException)
        {
            return (basic, LostLayer.Refused);
        }
        catch (LambdaIdentificationBackend.IdentificationUnavailableException)
        {
            return (basic, LostLayer.Down);
        }
    }

    /// <summary>
    /// The advanced layer, reporting what it found. Supporters only, and the server is what says so.
    /// A lookup by anyone else throws rather than coming back empty, so "not for you" is never
    /// mistaken for "not in the database".
    /// </summary>
    public async Task<IdentifyOutcome> IdentifyPremiumDetailedAsync(TrackInfo info, CancellationToken ct = default)
    {
        if (backend is not IClipIdentificationBackend clipBackend)
            return new IdentifyOutcome(info, false, null, 0, AudDOutcome.NotAsked);

        var query = await BuildQueryAsync(info, ct);
        var clip = await Task.Run(() => ClipCutter(info.FilePath, query.DurationSeconds));
        var answer = await clipBackend.IdentifyClipAsync(query, clip, ct);

        return Outcome(Merge(info, query, answer.Match), answer.Match, answer.AudD);
    }

    private static IdentifyOutcome Outcome(TrackInfo merged, IdentificationResult? result, AudDOutcome audd)
    {
        var matched = merged.Source == MetadataSource.Fingerprint;
        return new IdentifyOutcome(
            merged,
            matched,
            matched ? result?.Source : null,
            matched ? merged.IdentificationScore : 0,
            audd);
    }

    /// <summary>
    /// The paid layer: cuts a short clip and asks the backend's clip endpoint (AudD).
    /// Supporters only. Call this after <see cref="IdentifyAsync"/> came up empty and the user asked
    /// for it. Throws SupportersOnlyException when the account does not support rpm.
    /// Use <see cref="IdentifyPremiumDetailedAsync"/> when the caller must record the charge. This
    /// method discards that information.
    /// </summary>
    public async Task<TrackInfo> IdentifyPremiumAsync(TrackInfo info, CancellationToken ct = default)
        => (await IdentifyPremiumDetailedAsync(info, ct)).Track;

    private async Task<FingerprintQuery> BuildQueryAsync(TrackInfo info, CancellationToken ct)
    {
        var fp = await Task.Run(() => Fingerprinter(info.FilePath));
        uint[]? raw = null;
        try
        {
            raw = await Task.Run(() => RawFingerprinter(info.FilePath));
        }
        catch
        {
            // Raw form is an optimisation for the own-DB layer; the compressed lookup still works.
        }
        return new FingerprintQuery(fp.Base64Fingerprint, fp.DurationSeconds, raw);
    }

    private TrackInfo Merge(TrackInfo info, FingerprintQuery query, IdentificationResult? result)
    {
        if (result is null || result.Score < MinScore)
            return info;

        return info with
        {
            Title = result.Title,
            Artist = result.Artist,
            Album = result.Album ?? info.Album,
            Year = result.Year > 0 ? result.Year : info.Year,
            DurationSeconds = info.DurationSeconds > 0 ? info.DurationSeconds : query.DurationSeconds,
            Source = MetadataSource.Fingerprint,
            IdentificationScore = result.Score,
        };
    }
}
