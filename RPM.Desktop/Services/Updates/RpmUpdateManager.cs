using Velopack;
using Velopack.Sources;

namespace RPM.Desktop.Services.Updates;

/// <summary>
/// Velopack's update manager with rpm's rules for delta packages: which deltas it takes, and where the
/// patch writes.
/// </summary>
/// <remarks>
/// <para><b>Which deltas.</b> <see cref="DeltaChain"/> decides, and it takes a chain only when the chain is
/// whole. Velopack's default takes every delta newer than the local package, whatever it was made
/// from.</para>
///
/// <para><b>Where the patch writes.</b> Velopack rebuilds the full package from the deltas in its updater
/// process (<c>UpdateMac patch</c>, <c>Update.exe patch</c>). That process is not ended when the app quits.
/// It always writes <c>&lt;package&gt;.partial</c>. So a patch that is still running after a quit and the
/// patch of the next launch would write the same file. The second run could then move a file into place
/// while the first is still writing it. Each run here gives its patch its own file, and moves that file to
/// the shared name only after its own patch has exited. A file left by an earlier run ends in
/// <c>.partial</c>, and Velopack deletes it after the next download.</para>
/// </remarks>
internal sealed class RpmUpdateManager(IUpdateSource source, UpdateOptions options) : UpdateManager(source, options)
{
    protected override UpdateInfo CreateDeltaUpdateStrategy(
        VelopackAsset[] feed, VelopackAsset? latestLocalFull, VelopackAsset latestRemoteFull)
        => latestLocalFull is not null && DeltaChain.From(feed, latestLocalFull, latestRemoteFull) is { } chain
            ? new UpdateInfo(latestRemoteFull, isDowngrade: false, latestLocalFull, chain)
            : new UpdateInfo(latestRemoteFull, isDowngrade: false);

    protected override async Task DownloadAndApplyDeltaUpdates(
        UpdateInfo updates, string targetFile, Action<int> progress, CancellationToken cancelToken)
    {
        var own = $"{targetFile}.{Guid.NewGuid():N}.partial";
        await base.DownloadAndApplyDeltaUpdates(updates, own, progress, cancelToken).ConfigureAwait(false);
        File.Move(own, targetFile, overwrite: true);
    }
}
