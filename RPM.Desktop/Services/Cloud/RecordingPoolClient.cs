using Microsoft.Extensions.Logging;
using RPM.Core.Analysis;
using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Fingerprint;

namespace RPM.Desktop.Services.Cloud;

/// <summary>
/// The app's side of the shared pool: what other machines already know about a record, and what this
/// machine has to tell them.
///
/// <para>
/// Everything here is optional. When the user is signed out, offline, or the server does not answer, each
/// method returns an empty result or does nothing. The caller then measures the record itself, as it did
/// before this feature existed. The pool is a cache, not a dependency. No playback waits on it.
/// </para>
///
/// <para>
/// It is not a round trip per track. A pass asks about a page of tracks at once, before it opens any of
/// them, because one request per file would cost more than the reading it is meant to save.
/// </para>
/// </summary>
public sealed class RecordingPoolClient(
    RpmCloudClient cloud,
    AuthService auth,
    ILogger<RecordingPoolClient> log)
{
    /// <summary>How long a lookup may take before the pass gives up and reads the files itself. Asking is
    /// meant to save work, so waiting too long would cost more than it saves.</summary>
    private static readonly TimeSpan LookUpPatience = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The loudness already known for these tracks, keyed by the paths asked about. A track that is missing
    /// has not been measured by anyone. That is a normal answer, and it means the file must be read.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, LoudnessReading>> KnownLevelsAsync(
        LibraryDatabase db, IReadOnlyList<LoudnessWork> work, CancellationToken ct)
    {
        var known = new Dictionary<string, LoudnessReading>(StringComparer.OrdinalIgnoreCase);
        if (!auth.IsLoggedIn || work.Count == 0)
            return known;
        if (!await RPM.Core.Net.NetworkReach.Shared.AllowAsync(ct))
            return known;   // Nothing is known. This is the same as no one having measured the track.

        // A track can be asked about only if this machine has a name for it. The answers must be matched to
        // the right track, so the paths asked and the bundles sent are kept in the same order.
        var asked = new List<string>(work.Count);
        var bundles = new List<IReadOnlyList<string>>(work.Count);
        foreach (var item in work)
        {
            if (KeysFor(db, item.RelativePath) is not { Count: > 0 } keys)
                continue;
            asked.Add(item.RelativePath);
            bundles.Add(keys);
        }

        if (bundles.Count == 0)
            return known;

        try
        {
            using var patience = CancellationTokenSource.CreateLinkedTokenSource(ct);
            patience.CancelAfter(LookUpPatience);

            var answers = await cloud.LookUpRecordingsAsync(bundles, patience.Token).ConfigureAwait(false);

            for (int i = 0; i < answers.Count && i < asked.Count; i++)
            {
                if (LevelFact.Read(answers[i]?.Fact(RecordingFact.Level)) is not { } level)
                    continue;

                known[asked[i]] = new LoudnessReading(level.Lufs, level.TruePeak);
            }

            if (known.Count > 0)
                log.LogDebug("[pool] Already knew the level of {Known} of {Asked} tracks", known.Count, asked.Count);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The lookup took too long. The pass reads the files, as it would have done anyway.
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "[pool] Could not ask the pool what it knows");
        }

        return known;
    }

    /// <summary>Tells the pool what this machine measured. This is fire and forget. The reading is already
    /// saved in the local mirror, so the result of the report does not affect this method.
    /// </summary>
    public void PublishLevel(LibraryDatabase db, string relativePath, LoudnessReading reading)
    {
        if (!auth.IsLoggedIn || !reading.Known)
            return;

        if (LevelFact.From(reading.Lufs, reading.TruePeak) is not { } fact)
            return;

        var keys = Named(db, relativePath);
        if (keys.Count < 2)
            return;

        _ = Task.Run(async () =>
        {
            try { await cloud.ReportRecordingAsync(keys, [fact]).ConfigureAwait(false); }
            catch (Exception ex) { log.LogDebug(ex, "[pool] Could not tell the pool about {Path}", relativePath); }
        });
    }

    // ── analyses ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The analyses already known for these files, keyed by the absolute paths asked about.
    ///
    /// <para>A hit saves the model work, not the decode. Loading the models takes about 90 MB of weights,
    /// and one track can need a peak of nearly 1 GB of memory. This is why <c>AnalysisThrottle</c> exists.
    /// A sweep asks about its whole list before it opens any file.</para>
    /// </summary>
    /// <param name="fuzzyOnly">Ask only under the fingerprint's name. See <see cref="KeysFor"/>. The
    /// caller uses this second, after its exact names have found nothing.</param>
    public async Task<IReadOnlyDictionary<string, TrackAnalysis>> KnownAnalysesAsync(
        LibraryDatabase db,
        IReadOnlyList<(string AbsolutePath, string RelativePath)> tracks,
        string? sampleHash = null,
        CancellationToken ct = default,
        bool fuzzyOnly = false)
    {
        var known = new Dictionary<string, TrackAnalysis>(StringComparer.OrdinalIgnoreCase);
        if (!auth.IsLoggedIn || tracks.Count == 0)
            return known;
        if (!await RPM.Core.Net.NetworkReach.Shared.AllowAsync(ct))
            return known;

        var asked = new List<(string AbsolutePath, string RelativePath)>(tracks.Count);
        var bundles = new List<IReadOnlyList<string>>(tracks.Count);
        foreach (var track in tracks)
        {
            if (KeysFor(db, track.RelativePath, tracks.Count == 1 ? sampleHash : null, fuzzyOnly)
                is not { Count: > 0 } keys)
                continue;
            asked.Add(track);
            bundles.Add(keys);
        }

        if (bundles.Count == 0)
            return known;

        try
        {
            using var patience = CancellationTokenSource.CreateLinkedTokenSource(ct);
            patience.CancelAfter(LookUpPatience);

            var answers = await cloud.LookUpRecordingsAsync(bundles, patience.Token).ConfigureAwait(false);

            for (int i = 0; i < answers.Count && i < asked.Count; i++)
                if (AnalysisFacts.To(answers[i], asked[i].AbsolutePath) is { } analysis)
                    known[asked[i].AbsolutePath] = analysis;

            if (known.Count > 0)
                log.LogDebug("[pool] Already knew {Known} of {Asked} analyses{How}",
                    known.Count, asked.Count, fuzzyOnly ? " (by fingerprint)" : "");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "[pool] Could not ask the pool what it knows about these records");
        }

        return known;
    }

    /// <summary>The analysis that the pool holds for one file, or null. Used for the single track that a
    /// set is waiting on, where there is no batch.</summary>
    /// <param name="sampleHash">A name that the caller has just worked out and not yet saved: the hash
    /// of the decoded audio. It is the broadest exact name, and the one most likely to find this
    /// record.</param>
    public async Task<TrackAnalysis?> KnownAnalysisAsync(
        LibraryDatabase db, string absolutePath, string relativePath, string? sampleHash, CancellationToken ct,
        bool fuzzyOnly = false)
    {
        var known = await KnownAnalysesAsync(db, [(absolutePath, relativePath)], sampleHash, ct, fuzzyOnly)
            .ConfigureAwait(false);
        return known.TryGetValue(absolutePath, out var analysis) ? analysis : null;
    }

    /// <summary>The most records that one request may ask about. This is the endpoint's limit.</summary>
    private const int MostAsked = 50;

    /// <summary>
    /// Asks the store to read records that this machine cannot.
    ///
    /// <para>This is for a computer that fails the test in <see cref="RPM.Core.Analysis.ModelCapability"/>.
    /// It has no model weights, not enough memory, or it is not fast enough to stay a record ahead of the
    /// mix. The store reads the blob it already holds and publishes what it finds to the pool. The answer
    /// then arrives the same way as every other borrowed reading, through <see cref="KnownAnalysesAsync"/>,
    /// during the record that is playing now.</para>
    ///
    /// <para>Only tracks that the store has a copy of can be asked about. A record that exists only on this
    /// disk is not available to anyone else. In that case the set is told that it cannot mix the record,
    /// and the app shows this to the user.</para>
    /// </summary>
    /// <returns>How many were already known and how many were put on the store's queue.</returns>
    public async Task<(int Ready, int Queued)> EnsureAnalysedAsync(
        IReadOnlyList<string> rpmIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rpmIds);
        if (!auth.IsLoggedIn || rpmIds.Count == 0)
            return (0, 0);
        if (!await RPM.Core.Net.NetworkReach.Shared.AllowAsync(ct))
            return (0, 0);

        var asking = rpmIds.Where(id => !string.IsNullOrWhiteSpace(id))
                           .Distinct(StringComparer.Ordinal)
                           .Take(MostAsked)
                           .ToList();
        if (asking.Count == 0)
            return (0, 0);

        try
        {
            var (ready, queued) = await cloud.RequestAnalysisAsync(asking, ct).ConfigureAwait(false);
            log.LogDebug("[pool] The store was asked for {Queued} readings, {Ready} already known", queued, ready);
            return (ready, queued);
        }
        catch (Exception ex)
        {
            // The request can fail because the user is offline or signed out, or because the store refuses
            // it. The set then carries on. It mixes what it cannot match, as it does for any record that
            // has not been read.
            log.LogDebug(ex, "[pool] Could not ask the store to read these records");
            return (0, 0);
        }
    }

    /// <summary>Tells the pool what the analyser found. This is fire and forget, like <see
    /// cref="PublishLevel"/>.</summary>
    public void PublishAnalysis(LibraryDatabase db, string relativePath, TrackAnalysis analysis)
    {
        if (!auth.IsLoggedIn)
            return;

        // Send every name this machine uses for the record, including the fingerprint's name. A reading
        // sent without that name cannot be found by the next person who rips the same CD.
        var keys = Named(db, relativePath);
        if (keys.Count < 2)
            return;

        var facts = AnalysisFacts.From(analysis);
        if (facts.Count == 0)
            return;

        _ = Task.Run(async () =>
        {
            try { await cloud.ReportRecordingAsync(keys, facts).ConfigureAwait(false); }
            catch (Exception ex) { log.LogDebug(ex, "[pool] Could not tell the pool about {Path}", relativePath); }
        });
    }

    /// <summary>Every name at once: the three exact names and the fingerprint's name. They are sent
    /// together to state that they all refer to one record. A bundle is a claim about identity. The song id
    /// is the part of that claim that no other machine can make for itself.</summary>
    private static IReadOnlyList<string> Named(LibraryDatabase db, string relativePath)
    {
        var keys = new List<string>(KeysFor(db, relativePath));
        keys.AddRange(KeysFor(db, relativePath, fuzzyOnly: true));
        return keys;
    }

    /// <summary>Every name this machine has for a track, cheapest first. The store answers from the key
    /// that costs the least, and a later lookup asks in the same order.
    /// </summary>
    /// <param name="freshSampleHash">A hash that the caller has but has not stored yet. Used when the
    /// row has no sample hash, which is the case for every track that is analysed for the first time.</param>
    /// <param name="fuzzyOnly">Ask under the fingerprint's name and nothing else.
    ///
    /// <para>This name is different in kind from the other three. They identify this copy: its bytes, its
    /// samples, and the id in its tags. A hit under any of them is the same recording by definition. The
    /// song id identifies the <em>music</em>, and it is matched by ear, not by exact equality. So a hit
    /// under it may be another pressing, another edit, or another master that runs at a slightly different
    /// speed. This is why it is worth asking, since it is the only key that two independent rips of one CD
    /// share. It is also why the answer cannot be used as it is. See <see
    /// cref="RPM.Core.Analysis.GridRealign"/>, which the caller applies to the answer.</para>
    ///
    /// <para>It is kept as a separate question, not as a fourth key in the same request, so the caller
    /// knows what kind of answer it got. It is asked only after the exact keys have missed.</para></param>
    private static IReadOnlyList<string> KeysFor(
        LibraryDatabase db, string relativePath, string? freshSampleHash = null, bool fuzzyOnly = false)
    {
        (string? RpmId, string? CanonicalSha, string? SampleHash, long SongId) names;
        try { names = db.RecordingKeysFor(relativePath); }
        catch { return []; }

        if (fuzzyOnly)
            return RecordingKey.SongRow(names.SongId) is { } only ? [only.ToString()] : [];

        var keys = new List<string>(3);
        if (RecordingKey.Canonical(names.CanonicalSha) is { } canon) keys.Add(canon.ToString());
        if (RecordingKey.Samples(names.SampleHash ?? freshSampleHash) is { } samples) keys.Add(samples.ToString());
        if (RecordingKey.Rpm(names.RpmId) is { } id) keys.Add(id.ToString());
        return keys;
    }
}
