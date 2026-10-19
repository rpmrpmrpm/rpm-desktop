using System.Diagnostics;
using System.Globalization;
using RPM.Core.Identify;
using RPM.Core.Metadata.Providers;
using RPM.Core.Net;
using RPM.Fingerprint;
using RPM.Fingerprint.Catalog;
using RPM.Fingerprint.Chroma;
using RPM.Fingerprint.Decoding;

namespace RPM.Core.Splitting;

/// <summary>Offline analysis output: the plan, and the RMS envelope it was derived from. The envelope is reused
/// for the dialog's timeline and for boundary alignment after identification. It also holds the finer-grained
/// peaks that the editor draws the waveform from.</summary>
public sealed record SplitAnalysis(SplitPlan Plan, float[] EnvelopeDb, double WindowSeconds, WavePeaks? Peaks = null);

/// <summary>What one pass over the audio gives: the detector's RMS envelope, the length of the file, and the peaks
/// that the split editor draws. All of it comes from the same samples, at no extra cost.
/// <see cref="AlbumSplitAnalyzer.EnvelopeReader"/> returns one of these.</summary>
public sealed record EnvelopeScan(
    float[] EnvelopeDb, double WindowSeconds, double TotalSeconds, WavePeaks? Peaks = null);

/// <summary>One expected track of a release. This is the catalogue's shape, owned by Core so that it can be
/// tested.</summary>
public sealed record ExpectedTrack(string Title, string? Artist, double? LengthSeconds);

/// <summary>An album candidate looked up for alignment. Discs are kept separate. A rip is usually one disc of a
/// multi-disc set, so alignment must run against that disc only.</summary>
public sealed record ReleaseInfo(
    string Id, string Title, string? Artist, uint Year,
    IReadOnlyList<IReadOnlyList<ExpectedTrack>> Discs);

/// <summary>
/// Runs album-in-one-file analysis.
///
/// <para>Offline: a cue sheet gives an exact plan. Otherwise the RMS envelope is read, the silences are found as
/// boundaries, and a provisional "Track NN" plan is made.</para>
///
/// <para>Online (optional): segment fingerprints vote on the release they share. Its track list is fetched, and
/// the expected durations are aligned to the detected silences. Where the album is gapless, virtual boundaries
/// are used.</para>
///
/// <para>External work is behind injectable Funcs, the same pattern as <see cref="TrackIdentifier"/>.</para>
/// </summary>
/// <param name="backend">The basic layers (the rpm database, then AcoustID), or null when the split window offers
/// no recognition.</param>
/// <param name="advanced">The advanced layer (AudD) behind them, or null when this account may not reach it. The
/// app decides both from the account. See <c>RecognitionReach.ForSplit</c>.</param>
public sealed class AlbumSplitAnalyzer(ISegmentIdentificationBackend? backend, IClipIdentificationBackend? advanced)
{
    /// <summary>The sample rate the envelope is computed at. Low is fine for RMS level scanning.</summary>
    private const int EnvelopeSampleRate = 8000;

    /// <summary>Identify up to this many segments. Beyond that, segments are sampled, to cap the network cost.</summary>
    public const int MaxSegmentLookups = 12;

    public SilenceDetectorOptions SilenceOptions { get; init; } = new();

    /// <summary>
    /// Whether the file is a recorded record rather than a file that was always a file. Silence alone is a poor
    /// signal on vinyl. Songs hold beats, and every gap is bracketed by groove noise rather than by nothing. So
    /// the detected silences go through two tests that a record makes possible. They must reach the noise floor
    /// that the lead-in groove measured. And they must be as long as the gap that this record repeats. See
    /// <see cref="Recording.TrackGaps"/>.
    /// </summary>
    public bool VinylRecording { get; init; }

    /// <summary>
    /// Silences that look like the gaps between tracks. For a file, that is every silence found. For a record, it
    /// is only the ones that are both quiet enough and long enough to be the cutter's doing. Both filters fall back
    /// to the unfiltered set, not to nothing. A false split waits in the dialog to be edited. A missed split is a
    /// track the user never sees.
    /// </summary>
    private IReadOnlyList<SilenceCandidate> DetectGaps(float[] envelopeDb, double windowSeconds)
    {
        var silences = SilenceDetector.Detect(envelopeDb, windowSeconds, SilenceOptions);
        if (!VinylRecording || silences.Count == 0)
            return silences;

        // Depth first: drop the pauses that belong to the music. Then the gap length is profiled from real gaps,
        // not from whatever the songs happened to do. The floor is cross-checked between the lead-in and the
        // silences themselves. The lead-in is the most handled groove on the side, so it cannot be trusted on its
        // own.
        var leadIn = Recording.SideRegions.NoiseFloorFromLeadIn(envelopeDb, windowSeconds, SilenceOptions);
        if (Recording.TrackGaps.EstimateNoiseFloor(silences, leadIn) is { } floor)
            silences = Recording.TrackGaps.KeepAtNoiseFloor(silences, floor);

        return Recording.TrackGaps.KeepRepeatedGaps(silences);
    }

    /// <summary>Minimum AcoustID score for a segment match to count toward the album vote.</summary>
    public double MinSegmentScore { get; init; } = 0.5;

    /// <summary>Fingerprints a slice of the file: (path, startSec, lengthSec) gives a compressed fingerprint and the
    /// slice duration. The default runs ffmpeg to make a temp WAV, then runs fpcalc on it.</summary>
    public Func<string, double, double, (string Base64Fingerprint, double DurationSeconds)> SliceFingerprinter { get; init; }
        = DefaultSliceFingerprint;

    /// <summary>Reads the file's RMS envelope and the waveform peaks in one pass. The default streams the whole file
    /// through ffmpeg at a low sample rate.</summary>
    public Func<string, EnvelopeScan> EnvelopeReader { get; init; } = DefaultEnvelope;

    /// <summary>Fetches a release's expected track list. It returns null when the catalogue knows no such release.
    /// It returns unanswered when the catalogue did not answer. The default has no lookup, so it returns
    /// unanswered, not "no such release". To align against the catalogue, wire one through
    /// <see cref="ReleaseLookupVia"/>.</summary>
    public Func<string, CancellationToken, Task<Asked<ReleaseInfo?>>> ReleaseLookup { get; init; }
        = (_, _) => Task.FromResult(Asked.Failed<ReleaseInfo?>("no release lookup is wired"));

    /// <summary>True when segment identification can run. That needs a backend.</summary>
    public bool CanIdentify => backend is not null;

    /// <summary>True when a track that the basic layers cannot name may go on to the advanced layer.</summary>
    public bool CanReachAdvanced => backend is not null && advanced is not null;

    /// <summary>Matches below this score are not an answer. A track that gets only such a match goes on to the
    /// advanced layer. This is the same threshold as <see cref="TrackIdentifier.MinScore"/>.</summary>
    public double MinRecognitionScore { get; init; } = 0.5;

    /// <summary>Cuts the advanced layer's clip: (path, track start, track length) to MP3 bytes. The default shells
    /// out to ffmpeg.</summary>
    public Func<string, double, double, byte[]> ClipCutter { get; init; } = Identify.ClipCutter.CutClip;

    /// <summary>
    /// Recognises one track region of the recording on its own. The split dialog uses this for "Recognize
    /// selected", where the user picks which tracks to name, instead of voting on the whole album.
    ///
    /// <para>It uses the same slice rule as the album vote (see <see cref="IdentifyAsync"/>). AcoustID only matches
    /// a fingerprint that is aligned to the track start and gated on the claimed duration. So the first 120 s, or
    /// less, of the region is fingerprinted, and the full length of the region is claimed.</para>
    ///
    /// <para>The basic layers always run first, in the same order as the Recognize window. A track that the rpm
    /// database or AcoustID can name never goes out to AudD. Only when they cannot name it, and
    /// <paramref name="reachAdvanced"/> allows it, is a clip cut from inside this track and sent to the advanced
    /// layer. In a recording of a record, that is most tracks. AcoustID names an exact recording, and a transferred
    /// record is rarely the exact recording it has on file.</para>
    /// </summary>
    /// <param name="reachAdvanced">Whether this run may still use the advanced layer. The caller sets it once for
    /// the whole run. The server can refuse it part of the way, when support ends while the window is open.
    /// <see cref="LambdaIdentificationBackend.SupportersOnlyException"/> is how that refusal is reported.</param>
    public async Task<Identify.IdentificationResult?> RecognizeTrackAsync(
        string sourcePath, double startSeconds, double durationSeconds, bool reachAdvanced, CancellationToken ct)
    {
        if (backend is null || durationSeconds < 1)
            return null;

        double length = Math.Min(120, durationSeconds);
        var (fp, _) = await Task.Run(() => SliceFingerprinter(sourcePath, startSeconds, length));
        var query = new Identify.FingerprintQuery(fp, durationSeconds);

        var basic = await backend.IdentifyAsync(query, ct);
        if (Named(basic))
            return basic;
        if (!reachAdvanced || advanced is null)
            return null;

        var clip = await Task.Run(() => ClipCutter(sourcePath, startSeconds, durationSeconds), ct);
        var result = (await advanced.IdentifyClipAsync(query, clip, ct)).Match;
        return Named(result) ? result : null;
    }

    private bool Named(Identify.IdentificationResult? result) =>
        result is not null && result.Score >= MinRecognitionScore;

    /// <summary>Cue sheet or silence detection, with no network. The envelope is always computed, so the dialog has
    /// a timeline even on the cue path.</summary>
    public async Task<SplitAnalysis> AnalyzeOfflineAsync(
        AlbumFileCandidate candidate, IProgress<SplitAnalysisProgress>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new SplitAnalysisProgress("detecting", 0, 1, Path.GetFileName(candidate.FilePath)));
        var scan = await Task.Run(() => EnvelopeReader(candidate.FilePath));
        var (envelope, window, peaks) = (scan.EnvelopeDb, scan.WindowSeconds, scan.Peaks);
        double total = scan.TotalSeconds > 0 ? scan.TotalSeconds : candidate.DurationSeconds;
        ct.ThrowIfCancellationRequested();

        if (candidate.CueSheetPath is not null)
        {
            try
            {
                var cue = CueSheetParser.Parse(await RPM.Core.Files.ForeignText.ReadFileAsync(candidate.CueSheetPath, ct));
                if (cue is not null)
                {
                    var cuePlan = CueSheetParser.ToPlan(cue, candidate.FilePath, Path.GetExtension(candidate.FilePath), total);
                    if (cuePlan.Tracks.Count >= 2)
                        return new SplitAnalysis(cuePlan, envelope, window, peaks);
                }
            }
            catch { /* The cue sheet cannot be read. Fall through to silence detection. */ }
        }

        var silences = DetectGaps(envelope, window);
        var tracks = SilenceDetector.ToProvisionalTracks(silences, total, SilenceOptions);
        var plan = new SplitPlan(candidate.FilePath, Path.GetExtension(candidate.FilePath), total,
            Album: null, AlbumArtist: null, Year: 0, ReleaseMbid: null, tracks);
        progress?.Report(new SplitAnalysisProgress("detecting", 1, 1, ""));
        return new SplitAnalysis(plan, envelope, window, peaks);
    }

    /// <summary>
    /// Identifies the album and its tracks. Segment fingerprints vote on a release. Its track list is aligned to
    /// the detected silences. When no release consensus emerges, it falls back to the input plan. That plan keeps
    /// the detected boundaries, with per-segment titles where the matches were confident.
    /// </summary>
    public async Task<SplitAnalysis> IdentifyAsync(
        SplitAnalysis analysis, IProgress<SplitAnalysisProgress>? progress = null, CancellationToken ct = default)
    {
        if (backend is null || analysis.Plan.Tracks.Count == 0)
            return analysis;

        var plan = analysis.Plan;
        var sampled = PickSegments(plan.Tracks.Count);
        var matches = new Dictionary<int, SegmentIdentification>();

        for (int n = 0; n < sampled.Count; n++)
        {
            ct.ThrowIfCancellationRequested();
            int index = sampled[n];
            var seg = plan.Tracks[index];
            progress?.Report(new SplitAnalysisProgress("fingerprinting", n, sampled.Count, $"segment {index + 1}"));
            try
            {
                // AcoustID only matches fingerprints aligned to the track START. It also gates on the claimed
                // duration against the catalogue track. So fingerprint the first 120 s, or less, of the segment
                // (fpcalc's own default window). Claim the FULL segment length. That is exactly what a normal
                // whole-file client sends. An offset slice does not match, so this is the only form that works.
                double length = Math.Min(120, seg.DurationSeconds);
                var (fp, _) = await Task.Run(() => SliceFingerprinter(plan.SourcePath, seg.StartSeconds, length));

                var match = await backend.IdentifySegmentAsync(new FingerprintQuery(fp, seg.DurationSeconds), ct);
                if (match is not null && match.Score >= MinSegmentScore)
                    matches[index] = match;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                /* One bad segment must not stop the album vote. */
            }
        }

        // Majority vote across all matched segments, weighted by match score.
        var votes = new Dictionary<string, (double Score, int Segments)>();
        foreach (var m in matches.Values)
        {
            foreach (var relId in m.Releases.Select(r => r.ReleaseId).Distinct())
            {
                votes.TryGetValue(relId, out var v);
                votes[relId] = (v.Score + m.Score, v.Segments + 1);
            }
        }
        var winner = votes
            .OrderByDescending(kv => kv.Value.Segments)
            .ThenByDescending(kv => kv.Value.Score)
            .Select(kv => (string?)kv.Key)
            .FirstOrDefault(id => votes[id!].Segments >= Math.Min(2, matches.Count));

        if (winner is not null)
        {
            progress?.Report(new SplitAnalysisProgress("lookingup", 0, 1, ""));
            // A listing that did not arrive and a release with no listing take the same fallback. That is the
            // detected boundaries with the matched titles, which is the plan before the lookup. Neither is written
            // anywhere, and the plan makes no claim about the release either way.
            ReleaseInfo? release = null;
            try
            {
                if ((await ReleaseLookup(winner, ct)).Answers(out var found, out _))
                    release = found;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                /* A fault on our side. Use the same fallback. */
            }

            // The file is usually ONE disc of the release. Pick the disc by title overlap with the segments that were
            // actually matched. Duration alone chooses the wrong disc when the set's discs are similar in length. (A
            // real case: a two-disc compilation where both discs run about 45 minutes.)
            var disc = release is null
                ? null
                : PickDiscByMatches(release.Discs, matches.Values.Select(m => m.Title).ToList(), plan.TotalSeconds);
            if (release is not null && disc is not null)
            {
                progress?.Report(new SplitAnalysisProgress("aligning", 0, 1, release.Title));
                var discPlan = plan with
                {
                    Album = release.Title,
                    AlbumArtist = release.Artist,
                    Year = release.Year,
                    ReleaseMbid = release.Id,
                    DiscNumber = release.Discs.Count > 1 ? disc.Value.Number : 0,
                    DiscCount = release.Discs.Count > 1 ? (uint)release.Discs.Count : 0,
                };

                var silences = DetectGaps(analysis.EnvelopeDb, analysis.WindowSeconds);
                if (disc.Value.Tracks.All(t => t.LengthSeconds is > 0))
                {
                    // Durations are known, so run full alignment. This fixes false splits and handles gapless albums.
                    var aligned = BoundaryAligner.Align(
                        disc.Value.Tracks.Select(t => (t.Title, t.Artist, t.LengthSeconds!.Value)).ToList(),
                        silences, plan.TotalSeconds, analysis.EnvelopeDb, analysis.WindowSeconds);
                    return analysis with { Plan = discPlan with { Tracks = aligned.Tracks } };
                }
                // There are no durations in MusicBrainz (common for compilations). So map the detected segments onto
                // the disc's track list. The matched titles act as anchors. Surplus segments (false splits at quiet
                // passages) merge into their neighbours.
                var matchedTitlesByIndex = matches.ToDictionary(kv => kv.Key, kv => kv.Value.Title);
                var mapped = TryMapToDisc(plan.Tracks, disc.Value.Tracks, matchedTitlesByIndex);
                if (mapped is not null)
                    return analysis with { Plan = discPlan with { Tracks = mapped } };
                // This is the right album, but the boundaries and the track list disagree. Fall through to the
                // per-segment patch below, so the user still gets names where we matched.
            }
        }

        // No consensus. Keep the detected boundaries, and adopt titles for the segments that matched.
        var patched = plan.Tracks.ToList();
        foreach (var (index, match) in matches)
        {
            patched[index] = patched[index] with
            {
                Title = match.Title,
                Artist = match.Artist,
                Confidence = Math.Max(patched[index].Confidence, match.Score * 0.8),
            };
        }
        return analysis with { Plan = plan with { Tracks = patched } };
    }

    /// <summary>
    /// The disc that the matched segment titles actually live on. Title overlap is the main signal. Duration fit
    /// alone picks the wrong disc when a set's discs have similar lengths. At least 2 title hits are required, or
    /// every match when there are fewer. Ties are broken by duration fit. Null when no disc reaches the bar.
    /// </summary>
    public static (uint Number, IReadOnlyList<ExpectedTrack> Tracks)? PickDiscByMatches(
        IReadOnlyList<IReadOnlyList<ExpectedTrack>> discs,
        IReadOnlyList<string> matchedTitles,
        double totalSeconds)
    {
        (uint Number, IReadOnlyList<ExpectedTrack> Tracks)? best = null;
        int bestOverlap = -1;
        double bestError = double.MaxValue;
        int required = Math.Max(1, Math.Min(2, matchedTitles.Count));

        var normalizedMatches = matchedTitles.Select(NormalizeTitle).Where(t => t.Length > 0).ToList();
        for (int i = 0; i < discs.Count; i++)
        {
            var disc = discs[i];
            if (disc.Count < 2)
                continue;
            var discTitles = disc.Select(t => NormalizeTitle(t.Title)).ToHashSet();
            int overlap = normalizedMatches.Count(discTitles.Contains);
            if (overlap < required)
                continue;
            double sum = disc.Sum(t => t.LengthSeconds ?? 0);
            double error = sum > 0 ? Math.Abs(sum - totalSeconds) : totalSeconds;
            if (overlap > bestOverlap || (overlap == bestOverlap && error < bestError))
            {
                bestOverlap = overlap;
                bestError = error;
                best = ((uint)(i + 1), disc);
            }
        }
        return best;
    }

    /// <summary>Keeps lowercase letters and digits only. This survives punctuation and quote differences between
    /// AcoustID and MusicBrainz title spellings.</summary>
    public static string NormalizeTitle(string? title) =>
        new(title?.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray() ?? Array.Empty<char>());

    /// <summary>
    /// Maps detected segments onto a disc's track list when MusicBrainz has no durations. The segments whose match
    /// titles appear on the disc pin the mapping. These are the anchors, and they must be in order. Within each gap
    /// between anchors, surplus segments are merged into the adjacent segment that gives the shortest combined
    /// length. Surplus segments are false splits at quiet passages. Returns null when the mapping does not work out.
    /// That happens with fewer segments than tracks, or with anchors out of order.
    /// </summary>
    public static IReadOnlyList<SplitTrack>? TryMapToDisc(
        IReadOnlyList<SplitTrack> segments,
        IReadOnlyList<ExpectedTrack> disc,
        IReadOnlyDictionary<int, string> matchedTitlesBySegment)
    {
        if (segments.Count < disc.Count || disc.Count < 2)
            return null;

        // Anchors: a matched segment title equals a disc track title, and the anchors increase on both sides.
        var discNorm = disc.Select(t => NormalizeTitle(t.Title)).ToList();
        var anchors = new List<(int Seg, int Disc)>();
        int lastDisc = -1;
        for (int si = 0; si < segments.Count; si++)
        {
            if (!matchedTitlesBySegment.TryGetValue(si, out var title))
                continue;
            var norm = NormalizeTitle(title);
            int di = -1;
            for (int j = lastDisc + 1; j < discNorm.Count; j++)
                if (discNorm[j] == norm) { di = j; break; }
            if (di < 0)
                continue; // A match from another release's naming. It is not an anchor.
            anchors.Add((si, di));
            lastDisc = di;
        }
        if (anchors.Count < 2 && segments.Count != disc.Count)
            return null; // Merging without anchors would be guessing.

        // Sentinel anchors at both ends. Then fix each gap, right to left, so the indexes stay valid.
        var work = segments.ToList();
        var bounds = new List<(int Seg, int Disc)> { (-1, -1) };
        bounds.AddRange(anchors);
        bounds.Add((work.Count, disc.Count));
        for (int g = bounds.Count - 2; g >= 0; g--)
        {
            var (segLo, discLo) = bounds[g];
            var (segHi, discHi) = bounds[g + 1];
            int segSlots = segHi - segLo - 1;
            int discSlots = discHi - discLo - 1;
            if (segSlots < discSlots)
                return null; // A boundary was missed. It cannot be invented without durations.
            while (segSlots > discSlots)
            {
                // Merge the adjacent pair with the smallest combined duration in this gap.
                int bestAt = -1;
                double bestLen = double.MaxValue;
                for (int i = segLo + 1; i < segHi - 1; i++)
                {
                    double len = work[i].DurationSeconds + work[i + 1].DurationSeconds;
                    if (len < bestLen) { bestLen = len; bestAt = i; }
                }
                if (bestAt < 0)
                    return null;
                work[bestAt] = work[bestAt] with
                {
                    EndSeconds = work[bestAt + 1].EndSeconds,
                    Confidence = Math.Min(work[bestAt].Confidence, work[bestAt + 1].Confidence),
                };
                work.RemoveAt(bestAt + 1);
                segHi--;
                segSlots--;
            }
        }
        if (work.Count != disc.Count)
            return null;

        // Anchors are identified by reference. Merges shift the indexes, but untouched anchor records stay as the
        // same instances.
        var anchorSet = new HashSet<SplitTrack>(
            anchors.Select(a => segments[a.Seg]), ReferenceEqualityComparer.Instance);
        return work.Select((t, i) => t with
        {
            Title = disc[i].Title,
            Artist = disc[i].Artist,
            TrackNumber = (uint)(i + 1),
            Confidence = Math.Max(t.Confidence, anchorSet.Contains(t) ? 0.85 : 0.7),
        }).ToList();
    }

    /// <summary>All segments when there are few. Otherwise: the first, the last, and an even spread between
    /// them.</summary>
    public static IReadOnlyList<int> PickSegments(int count)
    {
        if (count <= MaxSegmentLookups)
            return Enumerable.Range(0, count).ToList();
        var picks = new SortedSet<int> { 0, count - 1 };
        for (int i = 1; picks.Count < MaxSegmentLookups; i++)
            picks.Add((int)Math.Round((double)i * (count - 1) / (MaxSegmentLookups - 1)));
        return picks.ToList();
    }

    private static (string, double) DefaultSliceFingerprint(string path, double startSec, double lengthSec)
    {
        // ffmpeg cuts a lossless temp WAV, because fpcalc has no seek options. fpcalc then fingerprints that WAV.
        string tempPath = Path.Combine(Path.GetTempPath(), $"md-split-fp-{Guid.NewGuid():N}.wav");
        try
        {
            var inv = CultureInfo.InvariantCulture;
            var psi = new ProcessStartInfo
            {
                FileName = FfmpegDecoder.FfmpegPath,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add(startSec.ToString("F3", inv));
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(path);
            psi.ArgumentList.Add("-t"); psi.ArgumentList.Add(lengthSec.ToString("F3", inv));
            psi.ArgumentList.Add("-vn");
            psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add("2");
            psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add("44100");
            psi.ArgumentList.Add(tempPath);
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("failed to start ffmpeg for fingerprint slice");
            string err = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"ffmpeg slice failed: {FfmpegSaid.Short(err)}");

            var result = FpcalcRunner.RunCompressed(tempPath);
            return (result.Base64Fingerprint, result.DurationSeconds);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    /// <summary>One decode, two products: the RMS envelope that the detector reads, and the peaks that the editor
    /// draws. Decoding an album-length file is the expensive part of analysis. The second accumulator costs one
    /// compare and store per sample. There is no reason to pay for the pass twice.</summary>
    private static EnvelopeScan DefaultEnvelope(string path)
    {
        var acc = new SilenceDetector.EnvelopeAccumulator(EnvelopeSampleRate, windowSeconds: 0.05);
        var peaks = new WavePeaks.Accumulator(EnvelopeSampleRate);
        FfmpegDecoder.DecodeStreaming(path, EnvelopeSampleRate, chunk =>
        {
            acc.Add(chunk.Span);
            peaks.Add(chunk.Span);
        });
        var envelope = acc.ToDbEnvelope();
        return new EnvelopeScan(envelope, acc.WindowSeconds, envelope.Length * acc.WindowSeconds, peaks.ToPeaks());
    }

    /// <summary>Builds a <see cref="ReleaseLookup"/> that fetches the expected track list through the RPM catalogue
    /// API. The upstream source and its rate limit are kept on the server. Nothing here talks to a provider
    /// directly.</summary>
    public static Func<string, CancellationToken, Task<Asked<ReleaseInfo?>>> ReleaseLookupVia(IRpmMetadataApi api) =>
        async (releaseId, ct) => (await api.GetReleaseAsync(releaseId, ct)).Then<ReleaseInfo?>(release =>
            release is null
                ? null
                : new ReleaseInfo(release.Id, release.Title, release.Artist, release.Year,
                    release.Media
                        .Select(m => (IReadOnlyList<ExpectedTrack>)m.Tracks
                            .Select(t => new ExpectedTrack(t.Title, t.Artist, t.LengthSeconds)).ToList())
                        .ToList()));
}
