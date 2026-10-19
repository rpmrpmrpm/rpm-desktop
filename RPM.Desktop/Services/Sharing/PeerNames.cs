using RPM.Core.Sharing;

namespace RPM.Desktop.Services.Sharing;

/// <summary>
/// What to call the computer whose shelf is on screen.
///
/// <para>
/// There is one answer, in one place, because the name is on screen more than once at a time: the
/// band across the top of the library, the subtitle over the borrowed playlists, and the share
/// button's label all name the same machine. Two of them disagreeing is worse than either being wrong.
/// </para>
///
/// <para>
/// The peer is paired, so its account is known. A profile has usually been fetched for it. The lookup
/// is asked for what it has now and is never waited on. A late answer reaches the screen because the
/// screen subscribes to <see cref="PeerFaces.Changed"/>. Without that subscription, whatever is drawn
/// keeps the machine name for good.
/// </para>
/// </summary>
public static class PeerNames
{
    /// <summary>The display name for one paired computer, falling back through its account profile,
    /// what it announced, and finally its device id.</summary>
    public static string Of(SharingService sharing, string deviceId)
    {
        ArgumentNullException.ThrowIfNull(sharing);

        var peer = sharing.Peers.FirstOrDefault(
            p => string.Equals(p.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));

        if (peer is null)
            return deviceId;

        if (App.Services.GetService<PeerFaces>() is not { } faces)
            return PeerLabel.For(PeerFace.Nobody, peer.Face, peer.Name);

        // Cheap and idempotent: an account that was already asked about adds no work. This is called
        // from places that name a peer without the share button's flyout ever being opened.
        faces.Learn(sharing.Peers);

        return faces.Label(peer.DeviceId, peer.Face, peer.Name);
    }

    /// <summary>The same, for the shelf currently on screen. It is empty when the collection being looked
    /// at is the user's own.</summary>
    public static string Lender()
    {
        var library = App.Services.GetService<Library.LibraryService>();
        var sharing = App.Services.GetService<SharingService>();

        return library is { IsPeerView: true, ViewingPeer: { } peer } && sharing is not null
            ? Of(sharing, peer)
            : "";
    }
}
