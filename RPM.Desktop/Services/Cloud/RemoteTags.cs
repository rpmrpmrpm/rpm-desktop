using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Core.Models;
using RPM.Desktop.Services.Account;
using RPM.Desktop.Services.Sharing;

namespace RPM.Desktop.Services.Cloud;

/// <summary>
/// The tags of a track whose file is not on this disk, read from the copy this account keeps in the
/// cloud beside it.
///
/// <para><b>Why this exists.</b> The track pane reads its credits off the file: the composer, the
/// conductor, the work, the ISRC. A record that has been offloaded has no file, so the pane came up with
/// the encyclopedia's articles and none of the record's own credits, and an opera lost its conductor the
/// moment it went to the cloud. The tags were never lost. Every upload stores them in the track's sidecar
/// (see <see cref="TagSidecar"/>), the same record a download rebuilds the file from.</para>
///
/// <para><b>Kept in memory, without the picture.</b> A sidecar carries its cover inline, so one is
/// megabytes on the wire. Each is fetched once per run and kept as a <see cref="TrackInfo"/> with the art
/// taken out: the pane has its own way to the sleeve (see <see cref="Art.RemoteSleeve"/>), and a few
/// hundred tag sets are small. A track this account has no sidecar for is remembered as none, so a
/// record that was never uploaded is asked about once.</para>
///
/// <para>Only this account's own records are asked about. A borrowed record's tags are in its owner's
/// cloud, and a guest's track id names nothing in ours.</para>
/// </summary>
public static class RemoteTags
{
    /// <summary>How many tag sets are kept before the store starts again. An album is twenty tracks, so this
    /// is a listening session's worth, and a stale entry costs one fetch.</summary>
    private const int Keep = 500;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Task<TrackInfo?>> Held = new(StringComparer.Ordinal);
    private static string? _user;

    /// <summary>The track's tags from its sidecar, or null when it has none, when nobody is signed in, or when
    /// the cloud cannot be reached. Never throws.</summary>
    /// <param name="row">The library row. Its id is what the sidecar is stored under.</param>
    /// <param name="path">Where the file would be. It goes into the answer as the file's path, as a read of
    /// the file would give it.</param>
    public static async Task<TrackInfo?> ForAsync(LibraryTrack row, string path)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (App.Services.GetService<AuthService>()?.UserId is not { Length: > 0 } user
            || App.Services.GetService<RpmCloudClient>() is not { } cloud)
            return null;

        var sharing = App.Services.GetService<SharingService>();
        var id = sharing is null ? row.RpmId : sharing.OwnCloudId(row.RpmId, row.PeerDeviceId);
        if (string.IsNullOrWhiteSpace(id))
            return null;

        Task<TrackInfo?> asking;
        lock (Gate)
        {
            // Another account's tags are not this one's. Signing in as somebody else starts the store again.
            if (_user != user || Held.Count >= Keep)
            {
                _user = user;
                Held.Clear();
            }

            if (Held.TryGetValue(id, out var held))
                asking = held;
            else
                Held[id] = asking = FetchAsync(cloud, id, path);
        }

        var info = await asking;
        if (info is null)
            return null;

        // An edit whose sidecar an earlier run did not get to write is what the track says now.
        info = SidecarsOwed.Overlay(id, info);

        // Held under the id, so a second path to the same track gets its own.
        return info.FilePath == path ? info : info with { FilePath = path };
    }

    /// <summary>
    /// Keeps an edit as the tags this run has for a track whose file is not on this disk, and says which
    /// sidecar to write it into (see <see cref="WriteAsync"/>); null where there is none to write, because
    /// nobody is signed in or the track has no id in this account's cloud.
    ///
    /// <para><b>Why the editors write the sidecar themselves.</b> The sync pass carries an edit to a
    /// cloud-only track into its sidecar from the mirror row (see TagSidecar.With), and the row holds only
    /// the tags the library is browsed by. The publisher, the copyright, the conductor, the soloists and
    /// the rest were written nowhere, so an edit to them was gone the next time the editor opened.</para>
    ///
    /// <para>Kept at once, for every track an edit reaches, before any of them is written: the track pane
    /// and the next editor read the edit from here, and an editor opened while the writes are still going
    /// would otherwise see some tracks edited and the rest not.</para>
    /// </summary>
    public static string? Remember(LibraryTrack row, TrackInfo edited)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(edited);

        if (App.Services.GetService<AuthService>()?.UserId is not { Length: > 0 } user)
            return null;

        var sharing = App.Services.GetService<SharingService>();
        var id = sharing is null ? row.RpmId : sharing.OwnCloudId(row.RpmId, row.PeerDeviceId);
        if (string.IsNullOrWhiteSpace(id))
            return null;

        lock (Gate)
        {
            if (_user != user)
            {
                _user = user;
                Held.Clear();
            }
            Held[id] = Task.FromResult<TrackInfo?>(edited with { PendingArt = null, PendingArtMime = null });
        }
        return id;
    }

    /// <summary>Writes an edit into the sidecar <paramref name="id"/> names, filed at
    /// <paramref name="relativePath"/>. False where nothing was written: no sidecar under the id, or the
    /// cloud not reachable. The sync pass still carries what the row holds.</summary>
    public static async Task<bool> WriteAsync(string id, TrackInfo edited, string relativePath, CancellationToken ct = default)
    {
        if (App.Services.GetService<RpmCloudClient>() is not { } cloud)
            return false;
        try
        {
            // Written with the fields the sidecar did not state still left out, and said to be rewritten, so
            // a computer that holds the file takes the edit.
            return await cloud.RewriteSidecarAsync(
                id, sidecar => sidecar.WithTags(edited) with { RelativePath = relativePath }, ct) is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<TrackInfo?> FetchAsync(RpmCloudClient cloud, string id, string path)
    {
        try
        {
            return await cloud.GetSidecarAsync(id) is { } sidecar
                ? sidecar.ToTrackInfo(path) with { PendingArt = null, PendingArtMime = null }
                : null;
        }
        catch
        {
            // Offline, or signed out between the check and the call. Not remembered, so the next look asks
            // again: a dropped connection is not an answer about the track.
            lock (Gate)
                Held.Remove(id);
            return null;
        }
    }
}
