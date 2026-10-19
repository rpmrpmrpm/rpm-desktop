using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Core.Metadata;

namespace RPM.Desktop.Services.Cloud;

/// <summary>
/// Takes a correction made on another of the user's machines and applies it to this one.
///
/// <para>The rule belongs to <see cref="CloudPullApplier"/>: what happens to the file and the row, and in
/// what order. What is here is the code around it. That is how many corrections are taken in at a time, and
/// the tag writer the applier is given. The tag writer is the only part that touches a disk in a way a test
/// cannot.</para>
/// </summary>
public sealed partial class CloudSyncService
{
    /// <summary>
    /// How many corrections are taken in per run. Each one rewrites a file's tags, and may move it. That is
    /// real work on somebody's disk, behind their music. A handful per run catches an install up over a few
    /// minutes. The rest are found again on the next pass.
    /// </summary>
    private const int PullsPerRun = 50;

    /// <summary>
    /// Applies what other machines have changed. Returns how many corrections landed.
    ///
    /// <para>Each row is handled on its own. A file that will not open leaves its row alone, and its stamps
    /// untouched. So the same correction is offered again on the next run. It is neither lost nor
    /// half-applied.</para>
    /// </summary>
    private async Task<int> ApplyPullsAsync(
        LibraryDatabase db, string root, IReadOnlyList<CloudPull> pulls, CancellationToken ct)
    {
        if (pulls.Count == 0) return 0;

        var applier = new CloudPullApplier(db, root, TagReader.Write);
        var applied = 0;

        foreach (var pull in pulls.Take(PullsPerRun))
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var outcome = await Task.Run(() => applier.Apply(pull));

                if (outcome == PullOutcome.Applied)
                    applied++;
                else if (outcome == PullOutcome.TagsRefused)
                    _log.LogWarning(
                        "[Cloud] Could not write the corrected tags into {Path} — left as it was",
                        pull.Row.RelativePath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "[Cloud] Could not take the correction to {Path}", pull.Row.RelativePath);
            }
        }

        if (applied > 0)
        {
            _log.LogInformation(
                "[Cloud] Took {Count} corrections made on another machine into this library", applied);
            _library.NotifyChanged();
        }

        return applied;
    }
}
