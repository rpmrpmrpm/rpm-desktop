using RPM.Core.Identify;
using RPM.Core.Metadata;
using RPM.Core.Models;
using RPM.Core.Scanning;

namespace RPM.Core.Analysis;

/// <summary>A progress snapshot sent to the UI while a folder is analysed.</summary>
/// <param name="Processed">Files fully analysed so far.</param>
/// <param name="Total">Total audio files found.</param>
/// <param name="CurrentFile">The name of the file being worked on.</param>
/// <param name="Identifying">True while the current file is in the fingerprint identification step.</param>
public sealed record AnalysisProgress(int Processed, int Total, string CurrentFile, bool Identifying);

/// <summary>
/// Runs the full analysis pass over a music folder: finds the audio files, reads the embedded
/// tags, identifies the untagged files by fingerprint (when a backend is available), and falls
/// back to guessing from the file name. Metadata is trusted in this order: tags, fingerprint,
/// file name.
/// </summary>
public sealed class FolderAnalyzer(TrackIdentifier? identifier)
{
    public FolderAnalyzer(IIdentificationBackend? identificationBackend)
        : this(identificationBackend is null ? null : new TrackIdentifier(identificationBackend))
    {
    }

    /// <summary>
    /// Identification failures for a single file (network error, no fpcalc, and so on) are ignored,
    /// so one bad file cannot stop the run. The exception is
    /// <see cref="LambdaIdentificationBackend.SupportersOnlyException"/>. It is not a per-file
    /// problem: the account is not allowed to use that layer, and the next file would be refused in
    /// the same way. It stops identification for the rest of the run, leaves the remaining files to
    /// file name guessing, and is reported through <see cref="RecognitionRefused"/>.
    /// </summary>
    public bool RecognitionRefused { get; private set; }

    public async Task<IReadOnlyList<TrackInfo>> AnalyzeAsync(
        string root,
        IProgress<AnalysisProgress>? progress = null,
        CancellationToken ct = default)
    {
        var files = await Task.Run(() => AudioFileScanner.Scan(root).Files);
        var results = new TrackInfo[files.Count];
        bool identifyEnabled = identifier is not null;

        // Phase 1: read the embedded tags. This is most of the time of a scan (every file is opened
        // and parsed), and each file is independent, so it runs on all cores. Results are stored by
        // index, so the output order still matches the sorted file list. The progress count is an
        // atomic tally, so reports can arrive out of order. The final report below (Total/Total)
        // always sets the correct end state.
        var total = files.Count;
        var processed = 0;
        var parallelism = Math.Min(Environment.ProcessorCount, 8);
        await Parallel.ForEachAsync(
            Enumerable.Range(0, total),
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            (i, token) =>
            {
                token.ThrowIfCancellationRequested();
                results[i] = TagReader.Read(files[i]);
                var n = Interlocked.Increment(ref processed);
                progress?.Report(new AnalysisProgress(n, total, Path.GetFileName(files[i]), Identifying: false));
                return ValueTask.CompletedTask;
            });

        // Phase 2: identify the still-unrecognised files by fingerprint. This is serial and in order,
        // because it uses the network, spends credits, and must stop as soon as credits run out (the
        // remaining files then fall back to the file name). It is skipped for an offline scan
        // (identifier is null; "Add music" always scans offline).
        if (identifyEnabled)
        {
            for (int i = 0; i < total && identifyEnabled; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (results[i].HasUsableMetadata)
                    continue;

                progress?.Report(new AnalysisProgress(i, total, Path.GetFileName(files[i]), Identifying: true));
                try
                {
                    results[i] = await identifier!.IdentifyAsync(results[i], ct);
                }
                catch (LambdaIdentificationBackend.SupportersOnlyException)
                {
                    RecognitionRefused = true;
                    identifyEnabled = false;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Identification is best-effort for each file. The file name fallback still applies.
                }
            }
        }

        // Phase 3: guess from the file name for any file that tags and identification left without
        // usable metadata.
        for (int i = 0; i < total; i++)
        {
            if (!results[i].HasUsableMetadata)
                results[i] = FilenameGuesser.Enrich(results[i]);
        }

        progress?.Report(new AnalysisProgress(total, total, "", Identifying: false));
        return results;
    }
}
